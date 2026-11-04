using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class AiAgentStore(MaicDbContext db, TimeProvider clock) : IAiAgentStore
{
    private static AiAgentResult Result(AiAgentStatus status) => new(status);

    /// <summary>
    /// Workspace -> actor membership, both locked until commit/rollback like every workspace writer, so a concurrent
    /// role change or removal is either observed or waits. Returns NotFound for nonmembers and Forbidden for readers.
    /// </summary>
    private async Task<AiAgentStatus> AuthorizeWriteAsync(Guid actorId, Guid workspaceId, IReadOnlyCollection<WorkspaceRole> writers,
        CancellationToken ct)
    {
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return members.SingleOrDefault() is not { } member ? AiAgentStatus.NotFound
            : writers.Contains(member.Role) ? AiAgentStatus.Success : AiAgentStatus.Forbidden;
    }

    /// <summary>Selected sources must all exist in this workspace; checked while the workspace lock is held.</summary>
    private async Task<bool> SourcesExistAsync(Guid workspaceId, IReadOnlyList<Guid> ids, CancellationToken ct) =>
        ids.Count == 0 || await db.KnowledgeSources.CountAsync(s => s.WorkspaceId == workspaceId && ids.Contains(s.Id), ct) == ids.Count;

    public async Task<AiAgentResult> CreateAsync(Guid actorId, Guid workspaceId, AiAgentSettings settings,
        IReadOnlyCollection<WorkspaceRole> writers, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var authorized = await AuthorizeWriteAsync(actorId, workspaceId, writers, cancellationToken);
        if (authorized != AiAgentStatus.Success) return Result(authorized);
        if (!await SourcesExistAsync(workspaceId, settings.KnowledgeSourceIds, cancellationToken)) return Result(AiAgentStatus.Invalid);

        if (await db.AiAgents.AnyAsync(a => a.WorkspaceId == workspaceId, cancellationToken)) return Result(AiAgentStatus.Conflict);
        var agent = new AiAgent(workspaceId, settings, clock.GetUtcNow());
        db.AiAgents.Add(agent);
        return await SaveAsync(agent, transaction, cancellationToken);
    }

    public async Task<AiAgentResult> UpdateAsync(Guid actorId, Guid workspaceId, Guid agentId, AiAgentSettings settings,
        IReadOnlyCollection<WorkspaceRole> writers, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var authorized = await AuthorizeWriteAsync(actorId, workspaceId, writers, cancellationToken);
        if (authorized != AiAgentStatus.Success) return Result(authorized);
        // A reused scoped context may hold an older copy; drop it and read the current row under the workspace lock.
        foreach (var stale in db.ChangeTracker.Entries().Where(e => e.Entity is AiAgent a && a.Id == agentId
                     || e.Entity is AiAgentKnowledgeSource s && s.AiAgentId == agentId).ToArray())
            stale.State = EntityState.Detached;
        var agent = await db.AiAgents.Include(a => a.KnowledgeSources)
            .SingleOrDefaultAsync(a => a.WorkspaceId == workspaceId && a.Id == agentId, cancellationToken);
        if (agent is null) return Result(AiAgentStatus.NotFound);
        if (!await SourcesExistAsync(workspaceId, settings.KnowledgeSourceIds, cancellationToken)) return Result(AiAgentStatus.Invalid);

        agent.Apply(settings, clock.GetUtcNow());
        return await SaveAsync(agent, transaction, cancellationToken);
    }

    public async Task<AiAgentResult> DeleteAsync(Guid actorId, Guid workspaceId, Guid agentId, IReadOnlyCollection<WorkspaceRole> writers,
        CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var authorized = await AuthorizeWriteAsync(actorId, workspaceId, writers, cancellationToken);
        if (authorized != AiAgentStatus.Success) return Result(authorized);
        // Source selections cascade in the database.
        await db.Conversations.Where(c => c.WorkspaceId == workspaceId && c.AiAgentId == agentId)
            .ExecuteUpdateAsync(setters => setters.SetProperty(c => c.AiAgentId, (Guid?)null)
                .SetProperty(c => c.AiBindingRevision, c => c.AiBindingRevision + 1), cancellationToken);
        var deleted = await db.AiAgents.Where(a => a.WorkspaceId == workspaceId && a.Id == agentId).ExecuteDeleteAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is AiAgent a && a.Id == agentId
                     || e.Entity is AiAgentKnowledgeSource s && s.AiAgentId == agentId).ToArray())
            entry.State = EntityState.Detached;
        return Result(deleted == 1 ? AiAgentStatus.Success : AiAgentStatus.NotFound);
    }

    public async Task<AiAgentResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > AiAgentService.MaxPageSize || after == Guid.Empty) return Result(AiAgentStatus.Invalid);
        var cursor = after.GetValueOrDefault();
        // One statement reads current membership and the bounded page; instructions are never selected.
        var page = await db.WorkspaceMembers.AsNoTracking().AsSingleQuery()
            .Where(m => m.WorkspaceId == workspaceId && m.UserId == actorId)
            .Select(m => new
            {
                Items = db.AiAgents.Where(a => a.WorkspaceId == m.WorkspaceId && (after == null || a.Id.CompareTo(cursor) > 0))
                    .OrderBy(a => a.Id).Take(take + 1)
                    .Select(a => new AiAgentSummary(a.Id, a.WorkspaceId, a.Name, a.Enabled, a.ModelTier.ToString(),
                        a.KnowledgeScope.ToString(), a.CreatedAt, a.UpdatedAt))
                    .ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (page is null) return Result(AiAgentStatus.NotFound);
        return new(AiAgentStatus.Success, Items: page.Items.Take(take).ToArray(),
            NextAfter: page.Items.Count > take ? page.Items[take - 1].Id : null);
    }

    public async Task<AiAgentResult> GetAsync(Guid actorId, Guid workspaceId, Guid agentId, CancellationToken cancellationToken)
    {
        var agent = await db.AiAgents.AsNoTracking().AsSingleQuery()
            .Where(a => a.WorkspaceId == workspaceId && a.Id == agentId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == a.WorkspaceId && m.UserId == actorId))
            .Select(a => new
            {
                Agent = a,
                Sources = db.AiAgentKnowledgeSources.Where(s => s.WorkspaceId == a.WorkspaceId && s.AiAgentId == a.Id)
                    .OrderBy(s => s.KnowledgeSourceId).Select(s => s.KnowledgeSourceId).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return agent is null ? Result(AiAgentStatus.NotFound) : new(AiAgentStatus.Success, View(agent.Agent, agent.Sources));
    }

    private async Task<AiAgentResult> SaveAsync(AiAgent agent, Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction transaction,
        CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(AiAgentStatus.Success, View(agent, agent.KnowledgeSources.Select(s => s.KnowledgeSourceId).Order().ToArray()));
        }
        catch (DbUpdateException error)
        {
            // The transaction rolls back; never leave rejected changes tracked in the scoped context.
            foreach (var entry in db.ChangeTracker.Entries().Where(e => e.Entity is AiAgent || e.Entity is AiAgentKnowledgeSource).ToArray())
                entry.State = EntityState.Detached;
            // Domain validation is the primary gate; a constraint rejection (for example a source deleted mid-request) is bad input.
            if (error.InnerException is PostgresException
                {
                    SqlState: PostgresErrorCodes.CheckViolation or PostgresErrorCodes.StringDataRightTruncation or PostgresErrorCodes.ForeignKeyViolation
                }) return Result(AiAgentStatus.Invalid);
            throw;
        }
    }

    private static AiAgentView View(AiAgent a, IReadOnlyList<Guid> sources) => new(a.Id, a.WorkspaceId, a.Name, a.Enabled,
        a.Instructions, a.Language, a.ModelTier.ToString(), a.KnowledgeScope.ToString(), sources, a.HandoffOnRequest,
        a.HandoffWhenUnanswered, a.MaxAiReplies, a.CreatedAt, a.UpdatedAt);
}
