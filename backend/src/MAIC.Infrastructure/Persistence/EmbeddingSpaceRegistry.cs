using MAIC.Application.AI;
using Microsoft.EntityFrameworkCore;
using MAIC.Infrastructure.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Persists embedding spaces (key, dimensions, model, last use). A key is bound to its dimensions and model forever:
/// reusing it with other values would mix incompatible vectors, so registration refuses and the host does not start.
/// Registering a space also creates its partial HNSW index (cosine) on <c>KnowledgeChunkVectors</c>. With
/// <c>manageSchema: false</c> the registry only verifies (runtime roles without DDL rights) and records the use.
/// </summary>
public sealed class EmbeddingSpaceRegistry(MaicDbContext db)
{
    public const string RegisterCommand = "register-embedding-space";

    public async Task EnsureAsync(EmbeddingSpace space, string model, CancellationToken cancellationToken, bool manageSchema = true)
    {
        if (space.Key == EmbeddingSettings.ReservedSpaceKey) throw new InvalidOperationException("The disabled space is never registered.");
        if (string.IsNullOrWhiteSpace(model) || model.Length > 200) throw new ArgumentException("A model name is required.", nameof(model));
        await PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            // API and Worker may start together; one registers, the other verifies.
            await using (var gate = PgSql.Command(connection, transaction, "SELECT pg_advisory_xact_lock(hashtextextended('maic:embedding-spaces', 0))"))
                await gate.ExecuteNonQueryAsync(cancellationToken);
            if (manageSchema)
            {
                await using var insert = PgSql.Command(connection, transaction, """
                    INSERT INTO "KnowledgeEmbeddingSpaces" ("Key", "Dimensions", "Model", "CreatedAt", "LastSeenAt")
                    VALUES (@key, @dimensions, @model, now(), now()) ON CONFLICT ("Key") DO NOTHING
                    """, ("key", space.Key), ("dimensions", space.Dimensions), ("model", model));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            await using (var check = PgSql.Command(connection, transaction,
                             """SELECT "Dimensions", "Model" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = @key""", ("key", space.Key)))
            await using (var reader = await check.ExecuteReaderAsync(cancellationToken))
            {
                if (!await reader.ReadAsync(cancellationToken))
                    throw new InvalidOperationException(
                        $"Embeddings:SpaceKey '{space.Key}' is not registered. Run 'MAIC.Worker {RegisterCommand}' with migration credentials.");
                if (reader.GetInt32(0) != space.Dimensions || reader.GetString(1) != model)
                    throw new InvalidOperationException(
                        $"Embeddings:SpaceKey '{space.Key}' is already registered with other dimensions or another model. " +
                        "Choose a new SpaceKey for the new model; documents are then indexed again in the new space.");
            }
            if (PgSql.IndexedExpression(space) is var (expression, operators))
            {
                // The index name is derived from the key only, so an index left by a retired space with the same key but
                // other dimensions (or an interrupted build) must be recognized by its definition, not its name.
                var definition = await IndexDefinitionAsync(connection, transaction, space, cancellationToken);
                var matches = definition is { Valid: true } found && found.Definition.Contains($"::{PgSql.QueryCast(space)})", StringComparison.Ordinal)
                    && found.Definition.Contains(operators, StringComparison.Ordinal)
                    && found.Definition.Contains(PgSql.SpaceLiteral(space), StringComparison.Ordinal);
                if (!matches && manageSchema)
                {
                    if (definition is not null)
                    {
                        await using var drop = PgSql.Command(connection, transaction, $"""DROP INDEX "{PgSql.IndexName(space)}" """);
                        await drop.ExecuteNonQueryAsync(cancellationToken);
                    }
                    await using var index = PgSql.Command(connection, transaction, $"""
                        CREATE INDEX "{PgSql.IndexName(space)}" ON "KnowledgeChunkVectors"
                        USING hnsw ({expression} {operators}) WHERE "SpaceKey" = {PgSql.SpaceLiteral(space)}
                        """);
                    await index.ExecuteNonQueryAsync(cancellationToken);
                }
                else if (!matches)
                {
                    throw new InvalidOperationException(
                        $"The vector index of Embeddings:SpaceKey '{space.Key}' is missing, invalid or built for other dimensions. " +
                        $"Run 'MAIC.Worker {RegisterCommand}' with migration credentials.");
                }
            }
            await TouchAsync(connection, transaction, space, cancellationToken);
            return 0;
        }, cancellationToken);
    }

    private static async Task<(string Definition, bool Valid)?> IndexDefinitionAsync(Npgsql.NpgsqlConnection connection,
        Npgsql.NpgsqlTransaction transaction, EmbeddingSpace space, CancellationToken cancellationToken)
    {
        await using var read = PgSql.Command(connection, transaction, """
            SELECT pg_get_indexdef(c.oid), i.indisvalid FROM pg_class AS c JOIN pg_index AS i ON i.indexrelid = c.oid
            WHERE c.relname = @name
            """, ("name", PgSql.IndexName(space)));
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? (reader.GetString(0), reader.GetBoolean(1)) : null;
    }

    /// <summary>Records that a host still uses the space, so retirement never removes it.</summary>
    public Task TouchAsync(EmbeddingSpace space, CancellationToken cancellationToken) =>
        PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await TouchAsync(connection, transaction, space, cancellationToken);
            return 0;
        }, cancellationToken);

    private static async Task TouchAsync(Npgsql.NpgsqlConnection connection, Npgsql.NpgsqlTransaction transaction, EmbeddingSpace space,
        CancellationToken cancellationToken)
    {
        await using var touch = PgSql.Command(connection, transaction,
            """UPDATE "KnowledgeEmbeddingSpaces" SET "LastSeenAt" = greatest("LastSeenAt", now()) WHERE "Key" = @key""", ("key", space.Key));
        if (await touch.ExecuteNonQueryAsync(cancellationToken) != 1)
            throw new InvalidOperationException($"Embeddings:SpaceKey '{space.Key}' is not registered; its use cannot be recorded.");
    }

    public sealed record RetiredSpace(string Key, long VectorsDeleted);

    /// <summary>
    /// Deletes spaces other than <paramref name="current"/> that no host used for <paramref name="retentionDays"/> days
    /// (database clock). Vectors and index states are deleted in small batches first, so no long transaction blocks
    /// searches; the space row (cascading the rest) is then deleted in a short transaction with a lock timeout, only if it
    /// is still unused, and its index is dropped CONCURRENTLY when <paramref name="manageSchema"/>. A space used again
    /// meanwhile keeps its row; its dropped states make discovery index its documents again.
    /// </summary>
    public async Task<IReadOnlyList<RetiredSpace>> DeleteRetiredAsync(EmbeddingSpace current, int retentionDays, bool manageSchema,
        CancellationToken cancellationToken)
    {
        if (retentionDays < 1) throw new ArgumentOutOfRangeException(nameof(retentionDays));
        var candidates = await PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            var found = new List<(string Key, int Dimensions)>();
            await using var select = PgSql.Command(connection, transaction, """
                SELECT "Key", "Dimensions" FROM "KnowledgeEmbeddingSpaces"
                WHERE "Key" <> @current AND "LastSeenAt" < now() - make_interval(days => @days)
                """, ("current", current.Key), ("days", retentionDays));
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) found.Add((reader.GetString(0), reader.GetInt32(1)));
            return found;
        }, cancellationToken);

        var retired = new List<RetiredSpace>();
        foreach (var (key, dimensions) in candidates)
        {
            long vectors = 0;
            foreach (var table in new[] { "KnowledgeChunkVectors", "KnowledgeIndexStates" })
                while (true)
                {
                    var deleted = await PgSql.InTransactionAsync(db, async (connection, transaction) =>
                    {
                        // Table names are constants; each batch re-checks that the space is still unused.
                        await using var batch = PgSql.Command(connection, transaction, $"""
                            DELETE FROM "{table}" WHERE ctid = ANY(ARRAY(
                                SELECT t.ctid FROM "{table}" AS t
                                JOIN "KnowledgeEmbeddingSpaces" AS s ON s."Key" = t."SpaceKey"
                                WHERE t."SpaceKey" = @key AND s."LastSeenAt" < now() - make_interval(days => @days)
                                LIMIT 5000))
                            """, ("key", key), ("days", retentionDays));
                        return await batch.ExecuteNonQueryAsync(cancellationToken);
                    }, cancellationToken);
                    if (table == "KnowledgeChunkVectors") vectors += deleted;
                    if (deleted == 0) break;
                }

            var removed = await PgSql.InTransactionAsync(db, async (connection, transaction) =>
            {
                await using (var settings = PgSql.Command(connection, transaction,
                                 "SET LOCAL lock_timeout = '5s'; SELECT pg_advisory_xact_lock(hashtextextended('maic:embedding-spaces', 0))"))
                    await settings.ExecuteNonQueryAsync(cancellationToken);
                await using var delete = PgSql.Command(connection, transaction, """
                    DELETE FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = @key AND "LastSeenAt" < now() - make_interval(days => @days)
                    """, ("key", key), ("days", retentionDays));
                return await delete.ExecuteNonQueryAsync(cancellationToken) == 1;
            }, cancellationToken);
            if (!removed) continue;
            if (manageSchema)
            {
                // CONCURRENTLY cannot run in a transaction and does not block searches or writes.
                var connection = (Npgsql.NpgsqlConnection)db.Database.GetDbConnection();
                await db.Database.OpenConnectionAsync(cancellationToken);
                try
                {
                    await using var drop = new Npgsql.NpgsqlCommand(
                        $"""DROP INDEX CONCURRENTLY IF EXISTS "{PgSql.IndexName(new EmbeddingSpace(key, dimensions))}" """, connection);
                    await drop.ExecuteNonQueryAsync(cancellationToken);
                }
                finally
                {
                    await db.Database.CloseConnectionAsync();
                }
            }
            retired.Add(new RetiredSpace(key, vectors));
        }
        return retired;
    }
}

/// <summary>
/// Registers (or, without schema management, verifies) the configured space when the API or Worker starts; a mismatch
/// stops the host. Afterwards it records the space's use every hour, so a running host's space is never retired. Only
/// added when an embedding provider is configured.
/// </summary>
public sealed class EmbeddingSpaceStartupCheck(IServiceScopeFactory scopes, EmbeddingSettings settings, ILogger<EmbeddingSpaceStartupCheck> logger)
    : IHostedService, IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromHours(1);
    private readonly CancellationTokenSource stopping = new();
    private Task heartbeat = Task.CompletedTask;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try { await using (var scope = scopes.CreateAsyncScope())
            await scope.ServiceProvider.GetRequiredService<EmbeddingSpaceRegistry>()
                .EnsureAsync(settings.Space(), settings.Model!, cancellationToken, settings.ManageSchema); } catch (InvalidOperationException) { throw; } catch (Exception error) { logger.LogWarning(error, "Could not verify the embedding space at startup; will retry on heartbeat."); }
        heartbeat = HeartbeatAsync(stopping.Token);
    }

    private async Task HeartbeatAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatInterval, cancellationToken);
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<EmbeddingSpaceRegistry>().TouchAsync(settings.Space(), cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception error)
            {
                logger.LogWarning(error, "Recording the embedding space's use failed; retrying in an hour.");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        await stopping.CancelAsync();
        await heartbeat.WaitAsync(cancellationToken).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    public void Dispose() => stopping.Dispose();
}
