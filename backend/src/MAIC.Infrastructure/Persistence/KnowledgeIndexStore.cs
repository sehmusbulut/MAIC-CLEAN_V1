using MAIC.Application.AI;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Indexing state per (space, document) in <c>KnowledgeIndexStates</c>. A state remembers the document's ProcessedAt, so
/// content processed again is indexed again. Claims lock only the state row (SKIP LOCKED) and prefer workspaces with the
/// fewest live attempts; every transition requires the claim's lease token. States cascade with their document and space.
/// </summary>
public sealed class KnowledgeIndexStore(MaicDbContext db, KnowledgeIndexLimits? limits = null) : IKnowledgeIndexStore
{
    private readonly KnowledgeIndexLimits limits = limits ?? new KnowledgeIndexLimits();

    /// <summary>Activity window for round-robin claims.</summary>
    public static readonly TimeSpan FairnessWindow = TimeSpan.FromMinutes(10);

    /// <summary>Content that was (partly) rejected is re-checked this often, at most <see cref="MaxRechecks"/> times.</summary>
    public static readonly TimeSpan RecheckAfter = TimeSpan.FromHours(24);
    public const int MaxRechecks = 3;
    /// <summary>Discovery takes at most this many documents per workspace per pass, so one backlog cannot delay others.</summary>
    public const int DiscoveryPerWorkspace = 50;

    public Task<int> DiscoverAsync(EmbeddingSpace space, DateTimeOffset now, int limit, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var discover = PgSql.Command(connection, transaction, """
                WITH candidates AS (
                    SELECT d."WorkspaceId", d."Id", d."ProcessedAt",
                        row_number() OVER (PARTITION BY d."WorkspaceId" ORDER BY d."ProcessedAt", d."Id") AS "Rank"
                    FROM "KnowledgeDocuments" AS d
                    JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                    WHERE d."Status" = 'READY' AND NOT EXISTS (
                        SELECT 1 FROM "KnowledgeIndexStates" AS e
                        WHERE e."SpaceKey" = @space AND e."WorkspaceId" = d."WorkspaceId" AND e."DocumentId" = d."Id"
                            AND e."DocumentProcessedAt" = d."ProcessedAt"))
                INSERT INTO "KnowledgeIndexStates" AS st ("SpaceKey", "WorkspaceId", "DocumentId", "DocumentProcessedAt", "Status",
                    "Attempts", "MisconfiguredStrikes", "NextAttemptAt", "UpdatedAt")
                SELECT @space, c."WorkspaceId", c."Id", c."ProcessedAt", 'PENDING', 0, 0, @now, @now
                FROM candidates AS c
                WHERE c."Rank" <= @perWorkspace
                ORDER BY c."Rank", c."ProcessedAt", c."Id"
                LIMIT @limit
                ON CONFLICT ("SpaceKey", "WorkspaceId", "DocumentId") DO UPDATE SET
                    "DocumentProcessedAt" = EXCLUDED."DocumentProcessedAt", "Status" = 'PENDING', "Attempts" = 0,
                    "MisconfiguredStrikes" = 0, "LeaseToken" = NULL, "NextAttemptAt" = EXCLUDED."NextAttemptAt",
                    "LastFailure" = NULL, "Failure" = NULL, "IndexedChunks" = 0, "RejectedChunks" = 0, "Rechecks" = 0,
                    "UpdatedAt" = EXCLUDED."UpdatedAt"
                WHERE st."DocumentProcessedAt" <> EXCLUDED."DocumentProcessedAt"
                """, ("space", space.Key), ("now", now), ("limit", limit), ("perWorkspace", DiscoveryPerWorkspace));
            var discovered = await discover.ExecuteNonQueryAsync(cancellationToken);

            // Rejections can be transient in ways the provider check cannot see: look again later, a bounded number of times.
            await using var recheck = PgSql.Command(connection, transaction, """
                UPDATE "KnowledgeIndexStates"
                SET "Status" = 'PENDING', "Failure" = NULL, "Attempts" = 0, "NextAttemptAt" = @now, "Rechecks" = "Rechecks" + 1,
                    "UpdatedAt" = @now
                WHERE "SpaceKey" = @space AND "Rechecks" < @maxRechecks AND "UpdatedAt" <= @before
                    AND ("Status" = 'FAILED' OR ("Status" = 'INDEXED' AND "RejectedChunks" > 0))
                """, ("space", space.Key), ("now", now), ("before", now - RecheckAfter), ("maxRechecks", MaxRechecks));
            return discovered + await recheck.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<KnowledgeIndexWork?> ClaimNextAsync(EmbeddingSpace space, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            var token = Guid.CreateVersion7();
            // Workspaces that used their daily chunk quota (append-only ledger, UTC day) wait. Fairness is weighted by cost:
            // workspaces that sent fewer chunks today go first (in steps of 1 % of the quota), so a tenant of large
            // documents cannot take most of the shared budget; within a step, round-robin: prefer workspaces with the least indexing activity (claims, retries, completions) in the recent
            // window, so one workspace's backlog cannot delay others even with a single worker. An INDEXING row whose lease
            // (NextAttemptAt) passed is due again: its worker stopped.
            await using var claim = PgSql.Command(connection, transaction, """
                WITH live AS (
                    SELECT "WorkspaceId", count(*) AS "Live" FROM "KnowledgeIndexStates"
                    WHERE "SpaceKey" = @space AND "Status" <> 'PENDING' AND "UpdatedAt" > @recent
                    GROUP BY "WorkspaceId"),
                used AS (
                    SELECT "WorkspaceId", "Chunks" FROM "KnowledgeIndexUsage" WHERE "SpaceKey" = @space AND "Day" = @day),
                next AS (
                    SELECT st."WorkspaceId", st."DocumentId" FROM "KnowledgeIndexStates" AS st
                    JOIN "KnowledgeDocuments" AS d ON d."WorkspaceId" = st."WorkspaceId" AND d."Id" = st."DocumentId"
                        AND d."Status" = 'READY' AND d."ProcessedAt" = st."DocumentProcessedAt"
                    JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                    LEFT JOIN live AS w ON w."WorkspaceId" = st."WorkspaceId"
                    LEFT JOIN used AS u ON u."WorkspaceId" = st."WorkspaceId"
                    WHERE coalesce(u."Chunks", 0) < @quota AND st."SpaceKey" = @space AND st."Status" IN ('PENDING', 'RETRY', 'INDEXING') AND st."NextAttemptAt" <= @now
                    ORDER BY coalesce(u."Chunks", 0) / greatest(1, @quota / 100), coalesce(w."Live", 0), st."NextAttemptAt", st."DocumentId"
                    LIMIT 1 FOR UPDATE OF st SKIP LOCKED)
                UPDATE "KnowledgeIndexStates" AS st
                SET "Status" = 'INDEXING', "LeaseToken" = @token, "Attempts" = st."Attempts" + 1, "NextAttemptAt" = @leaseUntil,
                    "UpdatedAt" = @now
                FROM next
                WHERE st."SpaceKey" = @space AND st."WorkspaceId" = next."WorkspaceId" AND st."DocumentId" = next."DocumentId"
                RETURNING st."WorkspaceId", st."DocumentId", st."Attempts", st."MisconfiguredStrikes"
                """, ("space", space.Key), ("now", now), ("token", token), ("leaseUntil", now + lease), ("recent", now - FairnessWindow),
                ("day", DateOnly.FromDateTime(now.UtcDateTime)), ("quota", (long)limits.MaxIndexedChunksPerWorkspacePerDay));
            await using var reader = await claim.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken)) return null;
            return new KnowledgeIndexWork(reader.GetGuid(0), reader.GetGuid(1), token, reader.GetInt32(2), reader.GetInt32(3));
        }, cancellationToken);

    // Refills the space's bucket up to one minute's worth, on the database clock so no Worker's clock can stall refills.
    private const string RefilledTokens = """
        least(@capacity, b."Tokens" + greatest(0, extract(epoch FROM (clock_timestamp() - b."RefilledAt"))) * @capacity / 60.0)
        """;

    /// <summary>Read-only: idle Workers polling for work never write the shared budget row.</summary>
    public Task<bool> HasIndexingBudgetAsync(EmbeddingSpace space, DateTimeOffset now, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var check = PgSql.Command(connection, transaction, $"""
                SELECT {RefilledTokens} FROM "KnowledgeIndexBudgets" AS b WHERE b."SpaceKey" = @space
                """, ("space", space.Key), ("capacity", (double)limits.MaxIndexingChunksPerMinute));
            return await check.ExecuteScalarAsync(cancellationToken) is not double tokens || tokens > 0;
        }, cancellationToken);

    public Task ChargeAsync(EmbeddingSpace space, KnowledgeIndexWork work, int chunks, DateTimeOffset now, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using (var debit = PgSql.Command(connection, transaction, $"""
                             INSERT INTO "KnowledgeIndexBudgets" AS b ("SpaceKey", "Tokens", "RefilledAt")
                             VALUES (@space, @capacity - @chunks, clock_timestamp())
                             ON CONFLICT ("SpaceKey") DO UPDATE SET "Tokens" = {RefilledTokens} - @chunks, "RefilledAt" = clock_timestamp()
                             """, ("space", space.Key), ("capacity", (double)limits.MaxIndexingChunksPerMinute), ("chunks", (double)chunks)))
                await debit.ExecuteNonQueryAsync(cancellationToken);
            await using var charge = PgSql.Command(connection, transaction, """
                INSERT INTO "KnowledgeIndexUsage" ("SpaceKey", "WorkspaceId", "Day", "Chunks") VALUES (@space, @workspace, @day, @chunks)
                ON CONFLICT ("SpaceKey", "Day", "WorkspaceId") DO UPDATE SET "Chunks" = "KnowledgeIndexUsage"."Chunks" + EXCLUDED."Chunks"
                """, ("space", space.Key), ("workspace", work.WorkspaceId), ("day", DateOnly.FromDateTime(now.UtcDateTime)), ("chunks", (long)chunks));
            return await charge.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);

    public Task<IReadOnlyList<KnowledgeChunkText>> LoadChunksAsync(KnowledgeIndexWork work, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync<IReadOnlyList<KnowledgeChunkText>>(db, async (connection, transaction) =>
        {
            await using var load = PgSql.Command(connection, transaction, """
                SELECT d."KnowledgeSourceId", c."Id", c."Sequence", c."Content"
                FROM "KnowledgeDocumentChunks" AS c
                JOIN "KnowledgeDocuments" AS d ON d."WorkspaceId" = c."WorkspaceId" AND d."Id" = c."DocumentId" AND d."Status" = 'READY'
                JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                WHERE c."WorkspaceId" = @workspace AND c."DocumentId" = @document
                ORDER BY c."Sequence"
                """, ("workspace", work.WorkspaceId), ("document", work.DocumentId));
            var chunks = new List<KnowledgeChunkText>();
            await using var reader = await load.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
                chunks.Add(new KnowledgeChunkText(new KnowledgeVectorKey(work.WorkspaceId, reader.GetGuid(0), work.DocumentId,
                    reader.GetGuid(1), reader.GetInt32(2)), reader.GetString(3)));
            return chunks;
        }, cancellationToken);

    public Task<bool> CompleteAsync(EmbeddingSpace space, KnowledgeIndexWork work, int indexedChunks, int rejectedChunks, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        TransitionAsync(space, work, """
            "Status" = 'INDEXED', "LeaseToken" = NULL, "NextAttemptAt" = @now, "MisconfiguredStrikes" = 0, "LastFailure" = NULL,
            "IndexedChunks" = @indexed, "RejectedChunks" = @rejected, "UpdatedAt" = @now
            """, cancellationToken, ("now", now), ("indexed", indexedChunks), ("rejected", rejectedChunks));

    public Task<bool> RejectAsync(EmbeddingSpace space, KnowledgeIndexWork work, int rejectedChunks, DateTimeOffset now,
        CancellationToken cancellationToken) =>
        TransitionAsync(space, work, """
            "Status" = 'FAILED', "LeaseToken" = NULL, "NextAttemptAt" = @now, "MisconfiguredStrikes" = 0, "LastFailure" = NULL,
            "Failure" = 'EMBEDDING_REJECTED', "IndexedChunks" = 0, "RejectedChunks" = @rejected, "UpdatedAt" = @now
            """, cancellationToken, ("now", now), ("rejected", rejectedChunks));

    public Task<bool> RetryAsync(EmbeddingSpace space, KnowledgeIndexWork work, KnowledgeIndexFailure? failure, DateTimeOffset nextAttemptAt,
        DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(space, work, """
            "Status" = 'RETRY', "LeaseToken" = NULL, "NextAttemptAt" = @next, "LastFailure" = CAST(@failure AS text), "UpdatedAt" = @now,
            "MisconfiguredStrikes" = CASE WHEN CAST(@failure AS text) = 'MISCONFIGURED' THEN "MisconfiguredStrikes" + 1
                WHEN CAST(@failure AS text) IS NULL THEN "MisconfiguredStrikes" ELSE 0 END
            """, cancellationToken, ("now", now), ("next", nextAttemptAt), ("failure", (object?)failure?.ToString() ?? DBNull.Value));

    public Task<bool> ReleaseAsync(EmbeddingSpace space, KnowledgeIndexWork work, DateTimeOffset now, CancellationToken cancellationToken) =>
        TransitionAsync(space, work, """
            "Status" = 'RETRY', "LeaseToken" = NULL, "NextAttemptAt" = @now, "Attempts" = greatest("Attempts" - 1, 0), "UpdatedAt" = @now
            """, cancellationToken, ("now", now));

    private Task<bool> TransitionAsync(EmbeddingSpace space, KnowledgeIndexWork work, string set, CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var update = PgSql.Command(connection, transaction, $"""
                UPDATE "KnowledgeIndexStates" SET {set}
                WHERE "SpaceKey" = @space AND "WorkspaceId" = @workspace AND "DocumentId" = @document
                    AND "Status" = 'INDEXING' AND "LeaseToken" = @token
                """, [.. parameters, ("space", space.Key), ("workspace", work.WorkspaceId), ("document", work.DocumentId),
                ("token", work.LeaseToken)]);
            return await update.ExecuteNonQueryAsync(cancellationToken) == 1;
        }, cancellationToken);
}
