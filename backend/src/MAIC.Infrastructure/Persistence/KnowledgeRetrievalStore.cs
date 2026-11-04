using MAIC.Application.AI;
using MAIC.Domain.AI;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Authoritative reads for retrieval. Every statement is bound to the agent's workspace; eligibility (READY document,
/// enabled source, agent selection) is decided in the same statement that returns the data.
/// </summary>
public sealed class KnowledgeRetrievalStore(MaicDbContext db) : IKnowledgeRetrievalStore
{
    public async Task<AgentKnowledgeScope?> LoadAgentScopeAsync(Guid workspaceId, Guid agentId, CancellationToken cancellationToken)
    {
        // One snapshot: the agent, its selection and the disabled sources are read consistently.
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, cancellationToken);
        var agent = await db.AiAgents.AsNoTracking().Where(a => a.WorkspaceId == workspaceId && a.Id == agentId)
            .Select(a => new { a.Enabled, a.KnowledgeScope }).SingleOrDefaultAsync(cancellationToken);
        if (agent is null) return null;
        var selected = await db.AiAgentKnowledgeSources.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && s.AiAgentId == agentId).Select(s => s.KnowledgeSourceId).ToArrayAsync(cancellationToken);
        // Bounded: beyond the exclusion limit, disabled sources are still dropped by the reload (they may cost candidates).
        var disabled = await db.KnowledgeSources.AsNoTracking()
            .Where(s => s.WorkspaceId == workspaceId && !s.Enabled).OrderBy(s => s.Id).Select(s => s.Id)
            .Take(KnowledgeScopeFilter.MaxExcludedSources).ToArrayAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new AgentKnowledgeScope(agent.Enabled, agent.KnowledgeScope, selected, disabled);
    }

    public Task<IReadOnlyList<RetrievedChunkRow>> LoadChunksAsync(Guid workspaceId, Guid agentId, AiKnowledgeScope scope,
        IReadOnlyCollection<Guid> chunkIds, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync<IReadOnlyList<RetrievedChunkRow>>(db, async (connection, transaction) =>
        {
            if (chunkIds.Count == 0) return [];
            await using var load = PgSql.Command(connection, transaction, $"""
                SELECT c."Id", c."DocumentId", s."Id", s."Name", d."FileName", d."SourceUrl", c."Sequence", c."Content"
                FROM "KnowledgeDocumentChunks" AS c
                JOIN "KnowledgeDocuments" AS d ON d."WorkspaceId" = c."WorkspaceId" AND d."Id" = c."DocumentId" AND d."Status" = 'READY'
                JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                {AgentJoin}
                {SelectionJoin(scope)}
                WHERE c."WorkspaceId" = @workspace AND c."Id" = ANY(@chunks)
                """, ("workspace", workspaceId), ("agent", agentId), ("scope", scope.ToString()), ("chunks", chunkIds.ToArray()));
            var rows = new List<RetrievedChunkRow>();
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                rows.Add(new RetrievedChunkRow(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetInt32(6),
                    reader.GetString(7)));
            return rows;
        }, cancellationToken);

    public Task<KnowledgeCoverage> CoverageAsync(EmbeddingSpace space, Guid workspaceId, Guid agentId, AiKnowledgeScope scope,
        CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            // READY documents by index state; documents still being ingested are pending, failed ingestion is partial
            // (their content cannot be searched, so "no match" cannot be claimed).
            await using var coverage = PgSql.Command(connection, transaction, $"""
                SELECT count(*) FILTER (WHERE d."Status" = 'READY')::int,
                    count(*) FILTER (WHERE d."Status" = 'PENDING' OR (d."Status" = 'READY' AND (st."DocumentId" IS NULL
                        OR st."DocumentProcessedAt" <> d."ProcessedAt" OR st."Status" IN ('PENDING', 'INDEXING', 'RETRY'))))::int,
                    count(*) FILTER (WHERE d."Status" = 'FAILED' OR (d."Status" = 'READY' AND st."DocumentProcessedAt" = d."ProcessedAt"
                        AND (st."Status" = 'FAILED' OR (st."Status" = 'INDEXED' AND st."RejectedChunks" > 0))))::int
                FROM "KnowledgeDocuments" AS d
                JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                {AgentJoin}
                {SelectionJoin(scope)}
                LEFT JOIN "KnowledgeIndexStates" AS st ON st."SpaceKey" = @space AND st."WorkspaceId" = d."WorkspaceId" AND st."DocumentId" = d."Id"
                WHERE d."WorkspaceId" = @workspace AND d."Status" IN ('READY', 'PENDING', 'FAILED')
                """, ("space", space.Key), ("workspace", workspaceId), ("agent", agentId), ("scope", scope.ToString()));
            await using var reader = await coverage.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new KnowledgeCoverage(reader.GetInt32(0), reader.GetInt32(1), reader.GetInt32(2));
        }, cancellationToken);

    public Task<bool> TryCountQueryAsync(Guid workspaceId, int dailyLimit, DateTimeOffset now, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var count = PgSql.Command(connection, transaction, """
                INSERT INTO "KnowledgeQueryUsage" AS u ("WorkspaceId", "Day", "Queries") VALUES (@workspace, @day, 1)
                ON CONFLICT ("Day", "WorkspaceId") DO UPDATE SET "Queries" = u."Queries" + 1 WHERE u."Queries" < @limit
                RETURNING 1
                """, ("workspace", workspaceId), ("day", DateOnly.FromDateTime(now.UtcDateTime)), ("limit", (long)dailyLimit));
            return await count.ExecuteScalarAsync(cancellationToken) is not null;
        }, cancellationToken);

    /// <summary>The agent is re-checked in the same statement: still in this workspace, enabled and with the scope read earlier.</summary>
    private const string AgentJoin = """
        JOIN "AiAgents" AS agent ON agent."WorkspaceId" = d."WorkspaceId" AND agent."Id" = @agent AND agent."Enabled"
            AND agent."KnowledgeScope" = @scope
        """;

    /// <summary>SELECTED_SOURCES requires a selection row of this agent for the document's source (same workspace).</summary>
    private static string SelectionJoin(AiKnowledgeScope scope) => scope == AiKnowledgeScope.SELECTED_SOURCES
        ? """
          JOIN "AiAgentKnowledgeSources" AS a ON a."WorkspaceId" = d."WorkspaceId" AND a."AiAgentId" = @agent
              AND a."KnowledgeSourceId" = d."KnowledgeSourceId"
          """
        : "";
}
