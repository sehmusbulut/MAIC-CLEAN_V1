using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class KnowledgeDocumentStore(MaicDbContext db, IKnowledgeFileStorage storage, TimeProvider clock, KnowledgeLimits limits)
    : IKnowledgeDocumentStore
{
    private static KnowledgeDocumentResult Result(KnowledgeDocumentStatusCode status) => new(status);

    /// <summary>
    /// Workspace -> actor membership -> source, all held until commit/rollback, matching every workspace writer. Worker
    /// transactions start at the source, so both paths lock source -> document -> job in the same order.
    /// </summary>
    private async Task<KnowledgeSource?> AuthorizeAndLockSourceAsync(Guid actorId, Guid workspaceId, Guid sourceId, CancellationToken ct)
    {
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        if (members.Count != 1) return null;
        var sources = await db.KnowledgeSources.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeSources" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {sourceId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return sources.SingleOrDefault();
    }

    private Task<KnowledgeDocument?> FindByKeyAsync(Guid workspaceId, Guid actorId, Guid clientRequestId, CancellationToken ct) =>
        db.KnowledgeDocuments.AsNoTracking().SingleOrDefaultAsync(d => d.WorkspaceId == workspaceId
            && d.CreatedByUserId == actorId && d.ClientRequestId == clientRequestId, ct);

    public async Task<KnowledgeDocumentResult> UploadAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid clientRequestId,
        KnowledgeUpload upload, CancellationToken cancellationToken)
    {
        var document = KnowledgeDocument.ForUpload(workspaceId, sourceId, actorId, clientRequestId, upload.FileName,
            upload.ContentType, upload.Content.Length, upload.ContentSha256);
        // Bytes are written under a server-generated key before any lock is taken, so a slow disk never holds the
        // workspace lock. Paths that certainly did not commit remove the file at once; if the commit outcome is unknown
        // the file stays, and the storage sweeper removes it later if no row references it.
        await storage.SaveAsync(workspaceId, document.Id, upload.Content, cancellationToken);
        var keepFile = false;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var source = await AuthorizeAndLockSourceAsync(actorId, workspaceId, sourceId, cancellationToken);
            if (source is null) return Result(KnowledgeDocumentStatusCode.NotFound);

            // Membership is re-checked above before any replay is answered.
            var existing = await FindByKeyAsync(workspaceId, actorId, clientRequestId, cancellationToken);
            if (existing is not null)
                return existing.KnowledgeSourceId == sourceId && existing.Origin == KnowledgeDocumentOrigin.UPLOAD
                    && existing.FileName == upload.FileName && existing.ContentType == upload.ContentType
                    && existing.ContentSha256 == upload.ContentSha256
                    ? await ReplayAsync(existing, cancellationToken)
                    : Result(KnowledgeDocumentStatusCode.Conflict);
            if (source.Kind != KnowledgeSourceKind.FILE) return Result(KnowledgeDocumentStatusCode.Invalid);
            if (!source.Enabled) return Result(KnowledgeDocumentStatusCode.Conflict);
            if (!await WithinQuotaAsync(workspaceId, upload.Content.Length, cancellationToken))
                return Result(KnowledgeDocumentStatusCode.QuotaExceeded);

            var job = new IngestionJob(workspaceId, document.Id, clock.GetUtcNow());
            db.KnowledgeDocuments.Add(document);
            db.IngestionJobs.Add(job);
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                Detach(document, job);
                return Result(KnowledgeDocumentStatusCode.Conflict);
            }
            keepFile = true;
            await transaction.CommitAsync(cancellationToken);
            return new(KnowledgeDocumentStatusCode.Success, View(document, job));
        }
        finally
        {
            if (!keepFile) await storage.DeleteAsync(workspaceId, document.Id, CancellationToken.None);
        }
    }

    /// <summary>Checks workspace limits while the workspace row is locked, so concurrent writers cannot overshoot them.</summary>
    private async Task<bool> WithinQuotaAsync(Guid workspaceId, long newBytes, CancellationToken ct)
    {
        var usage = await db.KnowledgeDocuments.Where(d => d.WorkspaceId == workspaceId && d.Status != KnowledgeDocumentStatus.SUPERSEDED)
            .GroupBy(d => d.WorkspaceId)
            .Select(g => new
            {
                Documents = g.Count(),
                Bytes = g.Sum(d => d.Origin == KnowledgeDocumentOrigin.UPLOAD ? d.SizeBytes ?? 0 : 0)
            }).SingleOrDefaultAsync(ct);
        return (usage?.Documents ?? 0) + 1 <= limits.MaxDocumentsPerWorkspace
            && (usage?.Bytes ?? 0) + newBytes <= limits.MaxStorageBytesPerWorkspace;
    }

    public async Task<KnowledgeDocumentResult> DeleteAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken)
    {
        KnowledgeDocumentOrigin origin;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            if (await AuthorizeAndLockSourceAsync(actorId, workspaceId, sourceId, cancellationToken) is null)
                return Result(KnowledgeDocumentStatusCode.NotFound);
            var documents = await db.KnowledgeDocuments.FromSqlInterpolated($"""
                SELECT * FROM "KnowledgeDocuments"
                WHERE "WorkspaceId" = {workspaceId} AND "KnowledgeSourceId" = {sourceId} AND "Id" = {documentId} FOR UPDATE
                """).AsNoTracking().ToListAsync(cancellationToken);
            if (documents.SingleOrDefault() is not { } document) return Result(KnowledgeDocumentStatusCode.NotFound);
            origin = document.Origin;
            // Chunks and jobs cascade in the database. A worker holding this document's lease loses it and changes nothing.
            await db.KnowledgeDocuments.Where(d => d.WorkspaceId == workspaceId && d.Id == documentId).ExecuteDeleteAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is KnowledgeDocument d && d.Id == documentId
                     || e.Entity is IngestionJob j && j.DocumentId == documentId).ToArray())
            entry.State = EntityState.Detached;
        // Best effort after commit; the storage sweeper removes the file later if this fails.
        if (origin == KnowledgeDocumentOrigin.UPLOAD) await storage.DeleteAsync(workspaceId, documentId, CancellationToken.None);
        return Result(KnowledgeDocumentStatusCode.Success);
    }

    private void Detach(params object[] entities)
    {
        foreach (var entity in entities) db.Entry(entity).State = EntityState.Detached;
    }

    public async Task<KnowledgeDocumentStatusCode> CheckUploadTargetAsync(Guid actorId, Guid workspaceId, Guid sourceId,
        long? declaredBytes, CancellationToken cancellationToken)
    {
        // One unlocked read so nonmembers and wrong targets are rejected before any body byte is read; UploadAsync
        // re-checks everything under lock.
        var source = await db.KnowledgeSources.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Id == sourceId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == s.WorkspaceId && m.UserId == actorId))
            .Select(s => new { s.Kind, s.Enabled }).SingleOrDefaultAsync(cancellationToken);
        if (source is null) return KnowledgeDocumentStatusCode.NotFound;
        if (source.Kind != KnowledgeSourceKind.FILE) return KnowledgeDocumentStatusCode.Invalid;
        // Early quota answer before the body is read; UploadAsync re-checks under the workspace lock with the real size.
        return await WithinQuotaAsync(workspaceId, Math.Max(declaredBytes ?? 1, 1), cancellationToken)
            ? KnowledgeDocumentStatusCode.Success : KnowledgeDocumentStatusCode.QuotaExceeded;
    }

    public async Task<KnowledgeDocumentResult> RequestIngestionAsync(Guid actorId, Guid workspaceId, Guid sourceId,
        Guid clientRequestId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var source = await AuthorizeAndLockSourceAsync(actorId, workspaceId, sourceId, cancellationToken);
        if (source is null) return Result(KnowledgeDocumentStatusCode.NotFound);

        var existing = await FindByKeyAsync(workspaceId, actorId, clientRequestId, cancellationToken);
        if (existing is not null)
            return existing.KnowledgeSourceId == sourceId && existing.Origin != KnowledgeDocumentOrigin.UPLOAD
                ? await ReplayAsync(existing, cancellationToken)
                : Result(KnowledgeDocumentStatusCode.Conflict);
        if (source.Kind == KnowledgeSourceKind.FILE) return Result(KnowledgeDocumentStatusCode.Invalid);
        if (!source.Enabled || await HasPendingSnapshotAsync(workspaceId, sourceId, cancellationToken))
            return Result(KnowledgeDocumentStatusCode.Conflict);
        // Refreshing a source that already has a READY snapshot replaces it, so it adds no document net.
        var replacesSnapshot = await db.KnowledgeDocuments.AnyAsync(d => d.WorkspaceId == workspaceId && d.KnowledgeSourceId == sourceId
            && d.Origin != KnowledgeDocumentOrigin.UPLOAD && d.Status == KnowledgeDocumentStatus.READY, cancellationToken);
        if (!replacesSnapshot && !await WithinQuotaAsync(workspaceId, 0, cancellationToken))
            return Result(KnowledgeDocumentStatusCode.QuotaExceeded);

        var document = KnowledgeDocument.ForSnapshot(workspaceId, sourceId, source.Kind, actorId, clientRequestId);
        var job = new IngestionJob(workspaceId, document.Id, clock.GetUtcNow());
        db.KnowledgeDocuments.Add(document);
        db.IngestionJobs.Add(job);
        return await SaveAsync([document, job], transaction, cancellationToken)
            ? new(KnowledgeDocumentStatusCode.Success, View(document, job))
            : Result(KnowledgeDocumentStatusCode.Conflict);
    }

    public async Task<KnowledgeDocumentResult> RetryAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var source = await AuthorizeAndLockSourceAsync(actorId, workspaceId, sourceId, cancellationToken);
        if (source is null) return Result(KnowledgeDocumentStatusCode.NotFound);
        var documents = await db.KnowledgeDocuments.FromSqlInterpolated($"""
            SELECT * FROM "KnowledgeDocuments"
            WHERE "WorkspaceId" = {workspaceId} AND "KnowledgeSourceId" = {sourceId} AND "Id" = {documentId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        var document = documents.SingleOrDefault();
        if (document is null) return Result(KnowledgeDocumentStatusCode.NotFound);
        // A reused scoped context may hold an older copy; refresh it while the source lock serializes writers.
        await db.Entry(document).ReloadAsync(cancellationToken);
        // The first job plus at most MaxManualRetries retries, so hostile content cannot be re-queued forever.
        if (document.Status != KnowledgeDocumentStatus.FAILED || !source.Enabled
            || await db.IngestionJobs.CountAsync(j => j.WorkspaceId == workspaceId && j.DocumentId == documentId, cancellationToken)
                > KnowledgeDocument.MaxManualRetries
            || (document.Origin != KnowledgeDocumentOrigin.UPLOAD && await HasPendingSnapshotAsync(workspaceId, sourceId, cancellationToken)))
            return Result(KnowledgeDocumentStatusCode.Conflict);

        document.Requeue();
        var job = new IngestionJob(workspaceId, document.Id, clock.GetUtcNow());
        db.IngestionJobs.Add(job);
        return await SaveAsync([document, job], transaction, cancellationToken)
            ? new(KnowledgeDocumentStatusCode.Success, View(document, job))
            : Result(KnowledgeDocumentStatusCode.Conflict);
    }

    public async Task<KnowledgeDocumentResult> ListAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid? after, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > KnowledgeDocumentService.MaxPageSize || after == Guid.Empty) return Result(KnowledgeDocumentStatusCode.Invalid);
        var cursor = after.GetValueOrDefault();
        var projection = Projection(db);
        // One statement authorizes membership, resolves the source and reads the bounded page.
        var page = await db.KnowledgeSources.AsNoTracking().AsSingleQuery()
            .Where(s => s.WorkspaceId == workspaceId && s.Id == sourceId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == s.WorkspaceId && m.UserId == actorId))
            .Select(s => new
            {
                Items = db.KnowledgeDocuments.Where(d => d.WorkspaceId == s.WorkspaceId && d.KnowledgeSourceId == s.Id
                        && (after == null || d.Id.CompareTo(cursor) > 0))
                    .OrderBy(d => d.Id).Take(take + 1).Select(projection).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (page is null) return Result(KnowledgeDocumentStatusCode.NotFound);
        return new(KnowledgeDocumentStatusCode.Success, Items: page.Items.Take(take).ToArray(),
            NextAfter: page.Items.Count > take ? page.Items[take - 1].Id : null);
    }

    public async Task<KnowledgeDocumentResult> GetAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken)
    {
        var document = await Documents(workspaceId, sourceId)
            .Where(d => d.Id == documentId && db.WorkspaceMembers.Any(m => m.WorkspaceId == d.WorkspaceId && m.UserId == actorId))
            .Select(Projection(db)).SingleOrDefaultAsync(cancellationToken);
        return document is null ? Result(KnowledgeDocumentStatusCode.NotFound) : new(KnowledgeDocumentStatusCode.Success, document);
    }

    public async Task<KnowledgeDocumentResult> ListChunksAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        int afterSequence, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > KnowledgeDocumentService.MaxPageSize || afterSequence < 0) return Result(KnowledgeDocumentStatusCode.Invalid);
        var page = await Documents(workspaceId, sourceId).AsSingleQuery()
            .Where(d => d.Id == documentId && db.WorkspaceMembers.Any(m => m.WorkspaceId == d.WorkspaceId && m.UserId == actorId))
            .Select(d => new
            {
                Items = db.KnowledgeDocumentChunks.Where(c => c.WorkspaceId == d.WorkspaceId && c.DocumentId == d.Id && c.Sequence > afterSequence)
                    .OrderBy(c => c.Sequence).Take(take + 1)
                    .Select(c => new KnowledgeChunkView(c.Id, c.WorkspaceId, c.DocumentId, c.Sequence, c.StartOffset, c.Content, c.CreatedAt))
                    .ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (page is null) return Result(KnowledgeDocumentStatusCode.NotFound);
        return new(KnowledgeDocumentStatusCode.Success, Chunks: page.Items.Take(take).ToArray(),
            NextAfterSequence: page.Items.Count > take ? page.Items[take - 1].Sequence : null);
    }

    private IQueryable<KnowledgeDocument> Documents(Guid workspaceId, Guid sourceId) => db.KnowledgeDocuments.AsNoTracking()
        .Where(d => d.WorkspaceId == workspaceId && d.KnowledgeSourceId == sourceId);

    private Task<bool> HasPendingSnapshotAsync(Guid workspaceId, Guid sourceId, CancellationToken ct) =>
        db.KnowledgeDocuments.AnyAsync(d => d.WorkspaceId == workspaceId && d.KnowledgeSourceId == sourceId
            && d.Origin != KnowledgeDocumentOrigin.UPLOAD && d.Status == KnowledgeDocumentStatus.PENDING, ct);

    private async Task<KnowledgeDocumentResult> ReplayAsync(KnowledgeDocument existing, CancellationToken ct)
    {
        var view = await db.KnowledgeDocuments.AsNoTracking().Where(d => d.WorkspaceId == existing.WorkspaceId && d.Id == existing.Id)
            .Select(Projection(db)).SingleAsync(ct);
        return new(KnowledgeDocumentStatusCode.Success, view, Replayed: true);
    }

    /// <summary>Commits new rows; a unique-key race (request key, pending snapshot, active job) is a conflict.</summary>
    private async Task<bool> SaveAsync(object[] entities, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return true;
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The transaction rolls back when disposed; detach or restore so this scoped context stays usable.
            foreach (var entity in entities)
            {
                var entry = db.Entry(entity);
                if (entry.State == EntityState.Added) entry.State = EntityState.Detached;
                else
                {
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                }
            }
            return false;
        }
    }

    private static System.Linq.Expressions.Expression<Func<KnowledgeDocument, KnowledgeDocumentView>> Projection(MaicDbContext db) =>
        d => new KnowledgeDocumentView(d.Id, d.WorkspaceId, d.KnowledgeSourceId, d.Origin.ToString(), d.Status.ToString(),
            d.CreatedByUserId, d.ClientRequestId, d.FileName, d.ContentType, d.SizeBytes, d.ContentSha256, d.SourceUrl,
            d.ChunkCount, d.CharacterCount, d.FailureReason == null ? null : d.FailureReason.ToString(), d.CreatedAt, d.ProcessedAt,
            db.IngestionJobs.Where(j => j.WorkspaceId == d.WorkspaceId && j.DocumentId == d.Id).OrderByDescending(j => j.Id)
                .Select(j => new IngestionJobView(j.Id, j.Status.ToString(), j.Attempts, j.MaxAttempts, j.NextAttemptAt,
                    j.LastError == null ? null : j.LastError.ToString(), j.CreatedAt, j.CompletedAt))
                .FirstOrDefault());

    private static KnowledgeDocumentView View(KnowledgeDocument d, IngestionJob j) => new(d.Id, d.WorkspaceId, d.KnowledgeSourceId,
        d.Origin.ToString(), d.Status.ToString(), d.CreatedByUserId, d.ClientRequestId, d.FileName, d.ContentType, d.SizeBytes,
        d.ContentSha256, d.SourceUrl, d.ChunkCount, d.CharacterCount, d.FailureReason?.ToString(), d.CreatedAt, d.ProcessedAt,
        new(j.Id, j.Status.ToString(), j.Attempts, j.MaxAttempts, j.NextAttemptAt, j.LastError?.ToString(), j.CreatedAt, j.CompletedAt));
}
