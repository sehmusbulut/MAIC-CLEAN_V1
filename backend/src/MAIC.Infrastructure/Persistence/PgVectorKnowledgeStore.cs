using System.Collections.Concurrent;
using MAIC.Application.AI;
using Npgsql;
using NpgsqlTypes;

namespace MAIC.Infrastructure.Persistence;

public sealed class PgVectorSearchOptions
{
    /// <summary>Upper bound on HNSW tuples visited by an iterative (filtered) scan.</summary>
    public int MaxScanTuples { get; init; } = 20_000;
    public TimeSpan StatementTimeout { get; init; } = TimeSpan.FromSeconds(5);
    /// <summary>Tests only: disables sequential scans so an index path is exercised.</summary>
    public bool ForceIndexScan { get; init; }
    /// <summary>Tests only: also disables sorts, so even a workspace-filtered query takes the HNSW path.</summary>
    public bool ForceAnnScan { get; init; }
    /// <summary>
    /// A workspace with at most this many vectors in the space is searched exactly (primary key prefix): full recall, and
    /// immune to other workspaces. Larger ones use the HNSW index with the exact rerun when it comes back short.
    /// </summary>
    public int ExactSearchThreshold { get; init; } = 20_000;
    /// <summary>Tests only: turns off the exact per-workspace rerun, to prove the starvation it prevents.</summary>
    public bool DisableExactFallback { get; init; }
}

/// <summary>
/// pgvector implementation of <see cref="IKnowledgeVectorStore"/>. Vectors live in <c>KnowledgeChunkVectors</c> keyed by
/// (space, workspace, chunk) with composite foreign keys to the chunk (WorkspaceId, DocumentId, ChunkId) and document
/// (WorkspaceId, KnowledgeSourceId, DocumentId), cascading on delete: a vector can only exist for an existing chunk of the
/// same workspace, document and source, and disappears with it. Writes skip chunks that are gone or do not match. Search
/// filters space, workspace and source scope inside the query and uses pgvector's bounded iterative index scan; when that
/// returns fewer than topK (other workspaces' nearer vectors exhausted the scan budget) it reruns exactly over the
/// workspace, so neither filters nor co-tenants can starve topK. Every value is a parameter except the registry-validated space key and dimensions, which are inlined so
/// the planner can match the per-space partial HNSW index.
/// </summary>
public sealed class PgVectorKnowledgeStore(MaicDbContext db, PgVectorSearchOptions? options = null) : IKnowledgeVectorStore
{
    private readonly PgVectorSearchOptions options = options ?? new PgVectorSearchOptions();
    // Per store instance (one per scope): a deleted and re-registered space is seen by the next request.
    private readonly ConcurrentDictionary<string, int> registeredDimensions = new();

    public Task ReplaceDocumentAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> vectors,
        CancellationToken cancellationToken) =>
        ReplaceAsync(space, workspaceId, documentId, vectors, null, cancellationToken);

    public Task<bool> ReplaceDocumentIfLeasedAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId,
        IReadOnlyList<KnowledgeVector> vectors, Guid leaseToken, CancellationToken cancellationToken) =>
        ReplaceAsync(space, workspaceId, documentId, vectors, leaseToken, cancellationToken);

    private async Task<bool> ReplaceAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> vectors,
        Guid? leaseToken, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Workspace and document are required.");
        if (vectors.Any(v => !v.Key.IsValid || v.Key.WorkspaceId != workspaceId || v.Key.DocumentId != documentId
                || !KnowledgeVectorIndex.IsUsableVector(v.Vector.Span, space))
            || vectors.Select(v => v.Key.ChunkId).Distinct().Count() != vectors.Count)
            throw new ArgumentException("Vectors must be distinct, valid and belong to this workspace, document and space.", nameof(vectors));

        return await PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            if (await DimensionsAsync(connection, transaction, space, cancellationToken) != space.Dimensions)
                throw new InvalidOperationException("The embedding space is not registered with these dimensions.");
            if (leaseToken is { } token)
            {
                // Lock order matches document deletion (document, then its dependent rows): the document row first
                // (KEY SHARE, as the vector foreign keys would), then the claim row, held until commit so the lease cannot
                // be lost between this check and the write.
                await using (var document = PgSql.Command(connection, transaction, """
                                 SELECT 1 FROM "KnowledgeDocuments" WHERE "WorkspaceId" = @workspace AND "Id" = @document FOR KEY SHARE
                                 """, ("workspace", workspaceId), ("document", documentId)))
                    if (await document.ExecuteScalarAsync(cancellationToken) is null) return false;
                await using var fence = PgSql.Command(connection, transaction, """
                    SELECT 1 FROM "KnowledgeIndexStates"
                    WHERE "SpaceKey" = @space AND "WorkspaceId" = @workspace AND "DocumentId" = @document
                        AND "Status" = 'INDEXING' AND "LeaseToken" = @token
                    FOR UPDATE
                    """, ("space", space.Key), ("workspace", workspaceId), ("document", documentId), ("token", token));
                if (await fence.ExecuteScalarAsync(cancellationToken) is null) return false;
            }
            // Serializes replaces of one document so concurrent attempts cannot interleave delete and insert.
            await using (var gate = PgSql.Command(connection, transaction,
                             "SELECT pg_advisory_xact_lock(hashtextextended(@space || ':' || @document::text, 0))",
                             ("space", space.Key), ("document", documentId)))
                await gate.ExecuteNonQueryAsync(cancellationToken);
            await using (var delete = PgSql.Command(connection, transaction, """
                             DELETE FROM "KnowledgeChunkVectors"
                             WHERE "SpaceKey" = @space AND "WorkspaceId" = @workspace AND "DocumentId" = @document
                             """, ("space", space.Key), ("workspace", workspaceId), ("document", documentId)))
                await delete.ExecuteNonQueryAsync(cancellationToken);

            foreach (var slice in vectors.Chunk(200))
            {
                await using var batch = new NpgsqlBatch(connection, transaction);
                foreach (var vector in slice)
                {
                    // Source, document and chunk come from the stored rows; a gone or mismatching chunk, a document that is
                    // no longer READY or a disabled source stores nothing. Vectors are stored at unit length (cosine is
                    // unchanged), so halfvec index expressions can neither overflow nor underflow to zero.
                    var insert = new NpgsqlBatchCommand("""
                        INSERT INTO "KnowledgeChunkVectors"
                            ("SpaceKey", "WorkspaceId", "KnowledgeSourceId", "DocumentId", "ChunkId", "Sequence", "Embedding")
                        SELECT $1, c."WorkspaceId", d."KnowledgeSourceId", c."DocumentId", c."Id", $5, $6::real[]::vector
                        FROM "KnowledgeDocumentChunks" AS c
                        JOIN "KnowledgeDocuments" AS d ON d."WorkspaceId" = c."WorkspaceId" AND d."Id" = c."DocumentId"
                            AND d."Status" = 'READY'
                        JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId" AND s."Enabled"
                        WHERE c."WorkspaceId" = $2 AND c."DocumentId" = $3 AND c."Id" = $4 AND d."KnowledgeSourceId" = $7
                        """);
                    insert.Parameters.Add(new NpgsqlParameter { Value = space.Key });
                    insert.Parameters.Add(new NpgsqlParameter { Value = workspaceId });
                    insert.Parameters.Add(new NpgsqlParameter { Value = documentId });
                    insert.Parameters.Add(new NpgsqlParameter { Value = vector.Key.ChunkId });
                    insert.Parameters.Add(new NpgsqlParameter { Value = vector.Key.ChunkSequence });
                    insert.Parameters.Add(new NpgsqlParameter { Value = Normalize(vector.Vector.Span), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Real });
                    insert.Parameters.Add(new NpgsqlParameter { Value = vector.Key.KnowledgeSourceId });
                    batch.BatchCommands.Add(insert);
                }
                await batch.ExecuteNonQueryAsync(cancellationToken);
            }
            return true;
        }, cancellationToken);
    }

    public Task DeleteDocumentsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        DeleteAsync(workspaceId, "DocumentId", documentIds, cancellationToken);

    public Task DeleteSourcesAsync(Guid workspaceId, IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken) =>
        DeleteAsync(workspaceId, "KnowledgeSourceId", sourceIds, cancellationToken);

    private async Task DeleteAsync(Guid workspaceId, string column, IReadOnlyCollection<Guid> ids, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (ids.Count == 0) return;
        await PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            // Every space; the column name is one of two constants.
            await using var delete = PgSql.Command(connection, transaction,
                $"""DELETE FROM "KnowledgeChunkVectors" WHERE "WorkspaceId" = @workspace AND "{column}" = ANY(@ids)""",
                ("workspace", workspaceId), ("ids", ids.ToArray()));
            return await delete.ExecuteNonQueryAsync(cancellationToken);
        }, cancellationToken);
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var space = request.Space;
        return await PgSql.InTransactionAsync<IReadOnlyList<VectorMatch>>(db, async (connection, transaction) =>
        {
            // An unregistered space, or a known key with other dimensions, has no vectors to return.
            if (await DimensionsAsync(connection, transaction, space, cancellationToken) != space.Dimensions) return [];

            await using (var configure = PgSql.Command(connection, transaction, SessionSettings(request)))
                await configure.ExecuteNonQueryAsync(cancellationToken);
            var small = await WorkspaceVectorsAtMostAsync(connection, transaction, request, options.ExactSearchThreshold, cancellationToken);
            await using var search = SearchCommand(connection, transaction, request, exact: small);

            var matches = await ReadAsync(search, cancellationToken);
            if (!small && matches.Count < request.TopK && PgSql.IndexedExpression(space) is not null && !options.DisableExactFallback)
            {
                // The shared HNSW scan stops after max_scan_tuples; many nearer vectors of other workspaces can exhaust it
                // before this workspace's matches are reached. Rerun exactly over this workspace only (primary key prefix).
                await using var exact = SearchCommand(connection, transaction, request, exact: true);
                matches = await ReadAsync(exact, cancellationToken);
            }
            return matches;
        }, cancellationToken);
    }

    /// <summary>Transaction-local planner settings: bounded iterative HNSW scan, timeout, optional forced index path.</summary>
    internal string SessionSettings(VectorSearchRequest request) => $"""
        SET LOCAL statement_timeout = {(int)options.StatementTimeout.TotalMilliseconds};
        SET LOCAL hnsw.iterative_scan = strict_order;
        SET LOCAL hnsw.max_scan_tuples = {options.MaxScanTuples};
        SET LOCAL hnsw.ef_search = {Math.Max(40, request.TopK)};
        {(options.ForceIndexScan ? "SET LOCAL enable_seqscan = off; SET LOCAL enable_bitmapscan = off;" : "")}
        {(options.ForceAnnScan ? "SET LOCAL enable_sort = off;" : "")}
        """;

    /// <summary>The filtered nearest-neighbour query; workspace, scope, query and topK are parameters.</summary>
    internal static NpgsqlCommand SearchCommand(NpgsqlConnection connection, NpgsqlTransaction transaction, VectorSearchRequest request,
        string prefix = "", bool exact = false)
    {
        var space = request.Space;
        // The exact form orders by the plain column, which no HNSW index matches, so it scans this workspace's rows only.
        var ordered = exact ? "\"Embedding\"" : PgSql.IndexedExpression(space)?.Expression ?? "\"Embedding\"";
        var cast = exact ? "vector" : PgSql.QueryCast(space);
        var scope = (request.Scope.AllSources ? "" : """ AND "KnowledgeSourceId" = ANY(@sources)""")
            + (request.Scope.ExcludedSourceIds.Count > 0 ? """ AND NOT ("KnowledgeSourceId" = ANY(@excluded))""" : "");
        var search = PgSql.Command(connection, transaction, $"""
            {prefix}SELECT "WorkspaceId", "KnowledgeSourceId", "DocumentId", "ChunkId", "Sequence",
                1 - ("Embedding" <=> @query::real[]::vector) AS "Score"
            FROM "KnowledgeChunkVectors"
            WHERE "SpaceKey" = {PgSql.SpaceLiteral(space)} AND "WorkspaceId" = @workspace{scope}
            ORDER BY {ordered} <=> @query::real[]::{cast}
            LIMIT @topK
            """, ("workspace", request.WorkspaceId), ("topK", request.TopK));
        search.Parameters.Add(new NpgsqlParameter("query", NpgsqlDbType.Array | NpgsqlDbType.Real) { Value = Normalize(request.Query.Span) });
        if (!request.Scope.AllSources) search.Parameters.AddWithValue("sources", request.Scope.SourceIds.ToArray());
        if (request.Scope.ExcludedSourceIds.Count > 0) search.Parameters.AddWithValue("excluded", request.Scope.ExcludedSourceIds.ToArray());
        return search;
    }

    /// <summary>Counts at most threshold + 1 of the workspace's vectors in the space (bounded primary key prefix scan).</summary>
    private static async Task<bool> WorkspaceVectorsAtMostAsync(NpgsqlConnection connection, NpgsqlTransaction transaction,
        VectorSearchRequest request, int threshold, CancellationToken cancellationToken)
    {
        if (threshold <= 0) return false;
        await using var count = PgSql.Command(connection, transaction, """
            SELECT count(*)::int FROM (
                SELECT 1 FROM "KnowledgeChunkVectors" WHERE "SpaceKey" = @space AND "WorkspaceId" = @workspace LIMIT @limit) AS v
            """, ("space", request.Space.Key), ("workspace", request.WorkspaceId), ("limit", threshold + 1));
        return (int)(await count.ExecuteScalarAsync(cancellationToken))! <= threshold;
    }

    private static async Task<List<VectorMatch>> ReadAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        var matches = new List<VectorMatch>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
            matches.Add(new VectorMatch(new KnowledgeVectorKey(reader.GetGuid(0), reader.GetGuid(1), reader.GetGuid(2), reader.GetGuid(3),
                reader.GetInt32(4)), reader.GetDouble(5)));
        return matches;
    }

    /// <summary>Unit length in double precision; callers already rejected zero, non-finite and underflowing vectors.</summary>
    internal static float[] Normalize(ReadOnlySpan<float> vector)
    {
        double squared = 0;
        foreach (var value in vector) squared += (double)value * value;
        var norm = Math.Sqrt(squared);
        var result = new float[vector.Length];
        for (var i = 0; i < vector.Length; i++) result[i] = (float)(vector[i] / norm);
        return result;
    }

    private async Task<int?> DimensionsAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, EmbeddingSpace space,
        CancellationToken cancellationToken)
    {
        if (registeredDimensions.TryGetValue(space.Key, out var cached)) return cached;
        await using var lookup = PgSql.Command(connection, transaction,
            """SELECT "Dimensions" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = @space""", ("space", space.Key));
        if (await lookup.ExecuteScalarAsync(cancellationToken) is not int dimensions) return null;
        registeredDimensions[space.Key] = dimensions;
        return dimensions;
    }
}
