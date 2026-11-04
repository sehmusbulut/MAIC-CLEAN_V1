using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class KnowledgeSourceStore(MaicDbContext db) : IKnowledgeSourceStore
{
    private async Task<bool> AuthorizeWriteAsync(Guid actorId, Guid workspaceId, CancellationToken ct)
    {
        // Same protocol as every workspace writer: workspace -> actor membership, both locks held until
        // commit/rollback, so a concurrent removal or role change cannot race this mutation.
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return members.Count == 1;
    }

    public async Task<KnowledgeSourceResult> CreateAsync(Guid actorId, KnowledgeSource source, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, source.WorkspaceId, cancellationToken))
            return new(KnowledgeSourceStatus.NotFound);
        db.KnowledgeSources.Add(source);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(KnowledgeSourceStatus.Success, Source: View(source));
        }
        catch (DbUpdateException error) when (IsRejectedValue(error))
        {
            // Disposing the uncommitted transaction rolls the insert back; detach it so this scoped
            // context stays usable.
            db.Entry(source).State = EntityState.Detached;
            return new(KnowledgeSourceStatus.Invalid);
        }
    }

    public async Task<KnowledgeSourceResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take,
        CancellationToken cancellationToken)
    {
        if (take is < 1 or > KnowledgeSourceService.MaxPageSize || after == Guid.Empty) return new(KnowledgeSourceStatus.Invalid);
        var cursor = after.GetValueOrDefault();
        // One statement reads current membership and the bounded page; payload columns are never selected.
        var result = await db.WorkspaceMembers.AsNoTracking().AsSingleQuery()
            .Where(actor => actor.WorkspaceId == workspaceId && actor.UserId == actorId)
            .Select(actor => new
            {
                Items = db.KnowledgeSources.Where(s => s.WorkspaceId == workspaceId && s.WorkspaceId == actor.WorkspaceId
                        && (after == null || s.Id.CompareTo(cursor) > 0))
                    .OrderBy(s => s.Id).Take(take + 1)
                    .Select(s => new KnowledgeSourceSummary(s.Id, s.WorkspaceId, s.Kind.ToString(), s.Name, s.Enabled, s.CreatedAt))
                    .ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (result is null) return new(KnowledgeSourceStatus.NotFound);
        return new(KnowledgeSourceStatus.Success, Items: result.Items.Take(take).ToArray(),
            NextAfter: result.Items.Count > take ? result.Items[take - 1].Id : null);
    }

    public async Task<KnowledgeSourceResult> GetAsync(Guid actorId, Guid workspaceId, Guid sourceId, CancellationToken cancellationToken)
    {
        var source = await db.KnowledgeSources.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.Id == sourceId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == s.WorkspaceId && m.UserId == actorId))
            .Select(s => new KnowledgeSourceView(s.Id, s.WorkspaceId, s.Kind.ToString(), s.Name, s.Enabled, s.CreatedAt,
                s.TextContent, s.WebsiteUrl))
            .SingleOrDefaultAsync(cancellationToken);
        return source is null ? new(KnowledgeSourceStatus.NotFound) : new(KnowledgeSourceStatus.Success, Source: source);
    }

    public async Task<KnowledgeSourceResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid sourceId, string? name,
        bool? enabled, string? textContent, string? websiteUrl, CancellationToken cancellationToken)
    {
        if (!KnowledgeSource.IsValidUpdate(name, enabled, textContent, websiteUrl)) return new(KnowledgeSourceStatus.Invalid);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        // Authorization precedes every lookup, so nonmembers learn nothing about sources or their kinds.
        if (!await AuthorizeWriteAsync(actorId, workspaceId, cancellationToken))
            return new(KnowledgeSourceStatus.NotFound);
        var alreadyTracked = db.ChangeTracker.Entries<KnowledgeSource>().Any(e => e.Entity.Id == sourceId);
        var source = await db.KnowledgeSources.SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId && s.Id == sourceId,
            cancellationToken);
        if (source is null) return new(KnowledgeSourceStatus.NotFound);
        // A reused scoped context may hold an older copy; refresh it while the workspace lock serializes writers.
        if (alreadyTracked) await db.Entry(source).ReloadAsync(cancellationToken);
        // Kind mismatch is rejected before any field changes, so nothing is partially applied.
        if (!source.AcceptsPayload(textContent, websiteUrl)) return new(KnowledgeSourceStatus.Invalid);

        source.Update(name, enabled, textContent, websiteUrl);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(KnowledgeSourceStatus.Success, Source: View(source));
        }
        catch (DbUpdateException error) when (IsRejectedValue(error))
        {
            // The transaction rolls back; restore tracked values so the context does not retry the rejected change.
            var entry = db.Entry(source);
            entry.CurrentValues.SetValues(entry.OriginalValues);
            entry.State = EntityState.Unchanged;
            return new(KnowledgeSourceStatus.Invalid);
        }
    }

    // Domain validation is the primary gate; database checks are a backstop for any mismatch.
    private static bool IsRejectedValue(DbUpdateException error) => error.InnerException is PostgresException
    {
        SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.StringDataRightTruncation
    };

    private static KnowledgeSourceView View(KnowledgeSource s) => new(s.Id, s.WorkspaceId, s.Kind.ToString(), s.Name,
        s.Enabled, s.CreatedAt, s.TextContent, s.WebsiteUrl);
}
