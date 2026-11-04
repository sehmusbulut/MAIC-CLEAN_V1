using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Worker-side persistence. Claims lock only the job row (SKIP LOCKED); completion and failure lock
/// source -> document -> job, the same order API writers use, and act only while the claim's lease token is held.
/// </summary>
public sealed class IngestionJobStore(MaicDbContext db, KnowledgeLimits limits) : IIngestionJobStore
{
    public async Task<IngestionWork?> ClaimNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken)
    {
        while (true)
        {
            db.ChangeTracker.Clear();
            IngestionJob? job;
            Guid token;
            await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
            {
                // Fair scheduling: prefer workspaces with the fewest live attempts and skip workspaces at their cap, so one
                // tenant's backlog cannot starve others. The cap is soft under concurrent claims (counts are not locked).
                var cap = limits.MaxRunningJobsPerWorkspace;
                // Live counts are computed once per workspace from the small RUNNING partial index, not per candidate row.
                var due = await db.IngestionJobs.FromSqlInterpolated($"""
                    WITH live AS (
                        SELECT "WorkspaceId", count(*) AS "Live" FROM "IngestionJobs"
                        WHERE "Status" = 'RUNNING' AND "NextAttemptAt" > {now}
                        GROUP BY "WorkspaceId")
                    SELECT j.* FROM "IngestionJobs" AS j
                    LEFT JOIN live AS w ON w."WorkspaceId" = j."WorkspaceId"
                    WHERE j."Status" IN ('QUEUED', 'RUNNING') AND j."NextAttemptAt" <= {now} AND coalesce(w."Live", 0) < {cap}
                    ORDER BY coalesce(w."Live", 0), j."NextAttemptAt", j."Id" LIMIT 1 FOR UPDATE OF j SKIP LOCKED
                    """).ToListAsync(cancellationToken);
                job = due.SingleOrDefault();
                if (job is null) return null;
                if (!job.CanAttempt)
                {
                    // A worker stopped during the final attempt. Fail it in canonical lock order after releasing this row.
                    await transaction.RollbackAsync(cancellationToken);
                    await AbandonAsync(job.Id, now, cancellationToken);
                    continue;
                }
                token = job.Claim(now, lease);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            var facts = await db.KnowledgeDocuments.AsNoTracking()
                .Where(d => d.WorkspaceId == job.WorkspaceId && d.Id == job.DocumentId)
                .Join(db.KnowledgeSources, d => new { d.WorkspaceId, Id = d.KnowledgeSourceId }, s => new { s.WorkspaceId, s.Id },
                    (d, s) => new { d.KnowledgeSourceId, d.Origin, d.ContentType, s.Enabled, s.TextContent, s.WebsiteUrl })
                .SingleOrDefaultAsync(cancellationToken);
            // Deleted right after the claim committed: its job cascaded away, so there is nothing to process.
            if (facts is null) continue;
            return new(job.Id, token, job.Attempts, job.WorkspaceId, job.DocumentId, facts.KnowledgeSourceId, facts.Origin,
                facts.Enabled, facts.ContentType, facts.TextContent, facts.WebsiteUrl);
        }
    }

    public async Task<IngestionOutcomeKind> CompleteAsync(IngestionWork work, IngestionContent content, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(work, cancellationToken) is not var (source, document, job)) return IngestionOutcomeKind.LeaseLost;
        var outcome = IngestionOutcomeKind.Ready;

        if (!source.Enabled)
        {
            // Disabled while this attempt ran: nothing becomes retrievable.
            job.Cancel(IngestionFailure.SOURCE_DISABLED, now);
            document.MarkFailed(IngestionFailure.SOURCE_DISABLED, now);
            outcome = IngestionOutcomeKind.Cancelled;
        }
        else
        {
            await DeleteChunksAsync(work.WorkspaceId, [document.Id], cancellationToken);
            var snapshot = document.Origin != KnowledgeDocumentOrigin.UPLOAD;
            var newerReady = snapshot && await db.KnowledgeDocuments.AnyAsync(d => d.WorkspaceId == work.WorkspaceId
                && d.KnowledgeSourceId == work.SourceId && d.Origin != KnowledgeDocumentOrigin.UPLOAD
                && d.Status == KnowledgeDocumentStatus.READY && d.Id.CompareTo(document.Id) > 0, cancellationToken);
            if (newerReady)
            {
                // A newer snapshot already won; this older content never becomes retrievable.
                document.MarkSuperseded(now);
            }
            else
            {
                document.MarkReady(content.Chunks.Count, content.CharacterCount, now,
                    snapshot ? content.ContentType : null, snapshot ? content.SizeBytes : null,
                    snapshot ? content.ContentSha256 : null, snapshot ? content.SourceUrl : null);
                db.KnowledgeDocumentChunks.AddRange(content.Chunks.Select(c =>
                    new KnowledgeDocumentChunk(work.WorkspaceId, document.Id, c.Sequence, c.StartOffset, c.Content)));
                if (snapshot)
                {
                    var older = await db.KnowledgeDocuments.Where(d => d.WorkspaceId == work.WorkspaceId
                        && d.KnowledgeSourceId == work.SourceId && d.Origin != KnowledgeDocumentOrigin.UPLOAD
                        && d.Status == KnowledgeDocumentStatus.READY && d.Id.CompareTo(document.Id) < 0).ToListAsync(cancellationToken);
                    foreach (var previous in older) previous.MarkSuperseded(now);
                    await DeleteChunksAsync(work.WorkspaceId, older.Select(d => d.Id).ToArray(), cancellationToken);
                }
            }
            job.Succeed(work.LeaseToken, now);
        }
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    public async Task<bool> ReleaseAsync(IngestionWork work, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(work, cancellationToken) is not var (_, _, job)) return false;
        job.Release(work.LeaseToken, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<IngestionOutcomeKind> FailAsync(IngestionWork work, IngestionFailure reason, bool transient, DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (await LockAsync(work, cancellationToken) is not var (_, document, job)) return IngestionOutcomeKind.LeaseLost;

        IngestionOutcomeKind outcome;
        if (reason == IngestionFailure.SOURCE_DISABLED)
        {
            job.Cancel(reason, now);
            outcome = IngestionOutcomeKind.Cancelled;
        }
        else outcome = job.Fail(work.LeaseToken, reason, transient, now) ? IngestionOutcomeKind.Retrying : IngestionOutcomeKind.Failed;
        if (outcome != IngestionOutcomeKind.Retrying) document.MarkFailed(reason, now);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return outcome;
    }

    private async Task AbandonAsync(Guid jobId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        db.ChangeTracker.Clear();
        var target = await db.IngestionJobs.AsNoTracking().Where(j => j.Id == jobId)
            .Join(db.KnowledgeDocuments, j => new { j.WorkspaceId, Id = j.DocumentId }, d => new { d.WorkspaceId, d.Id },
                (j, d) => new { j.WorkspaceId, j.DocumentId, d.KnowledgeSourceId }).SingleOrDefaultAsync(cancellationToken);
        if (target is null) return;
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var locked = await LockRowsAsync(target.WorkspaceId, target.KnowledgeSourceId, target.DocumentId, jobId, cancellationToken);
        // Recheck under lock: another worker may already have abandoned it.
        if (locked is var (_, document, job) && job.Status == IngestionJobStatus.RUNNING && job.IsDue(now) && !job.CanAttempt)
        {
            job.Abandon(now);
            if (document.Status == KnowledgeDocumentStatus.PENDING) document.MarkFailed(IngestionFailure.LEASE_EXPIRED, now);
            await db.SaveChangesAsync(cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
    }

    private async Task<(KnowledgeSource, KnowledgeDocument, IngestionJob)?> LockAsync(IngestionWork work, CancellationToken ct)
    {
        var locked = await LockRowsAsync(work.WorkspaceId, work.SourceId, work.DocumentId, work.JobId, ct);
        // A lost lease (expired and reclaimed, or already finished) must not change anything.
        return locked is var (_, document, job) && job.Holds(work.LeaseToken) && document.Status == KnowledgeDocumentStatus.PENDING
            ? locked : null;
    }

    private async Task<(KnowledgeSource, KnowledgeDocument, IngestionJob)?> LockRowsAsync(Guid workspaceId, Guid sourceId,
        Guid documentId, Guid jobId, CancellationToken ct)
    {
        var source = (await db.KnowledgeSources.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeSources" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {sourceId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        var document = (await db.KnowledgeDocuments.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeDocuments" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {documentId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        var job = (await db.IngestionJobs.FromSqlInterpolated($"""
            SELECT * FROM "IngestionJobs" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {jobId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        return source is null || document is null || job is null || document.KnowledgeSourceId != sourceId
            || job.DocumentId != documentId ? null : (source, document, job);
    }

    private Task DeleteChunksAsync(Guid workspaceId, Guid[] documentIds, CancellationToken ct) => documentIds.Length == 0
        ? Task.CompletedTask
        : db.KnowledgeDocumentChunks.Where(c => c.WorkspaceId == workspaceId && documentIds.Contains(c.DocumentId)).ExecuteDeleteAsync(ct);
}
