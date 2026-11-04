using MAIC.Application.Knowledge;
using MAIC.Infrastructure.AI;

namespace MAIC.Infrastructure.Persistence;

/// <summary>
/// Index status of READY documents in the configured space. A missing state, or one written for older content, is
/// PENDING (discovery has not caught up yet); RETRY is shown as PENDING without the provider fault code.
/// </summary>
public sealed class KnowledgeIndexStatusReader(MaicDbContext db, EmbeddingSettings settings) : IKnowledgeIndexStatusReader
{
    private static readonly KnowledgeIndexStatusView Disabled = new("DISABLED", 0, 0, null);
    private static readonly KnowledgeIndexStatusView Pending = new("PENDING", 0, 0, null);
    private static readonly KnowledgeIndexStatusView SourceDisabled = new("SOURCE_DISABLED", 0, 0, null);

    public async Task<IReadOnlyDictionary<Guid, KnowledgeIndexStatusView>> GetAsync(Guid workspaceId,
        IReadOnlyCollection<(Guid DocumentId, DateTimeOffset ProcessedAt)> readyDocuments, CancellationToken cancellationToken)
    {
        if (!settings.Enabled) return readyDocuments.ToDictionary(d => d.DocumentId, _ => Disabled);
        var result = readyDocuments.ToDictionary(d => d.DocumentId, _ => Pending);
        var processedAt = readyDocuments.ToDictionary(d => d.DocumentId, d => d.ProcessedAt);
        await PgSql.InTransactionAsync(db, async (connection, transaction) =>
        {
            await using var read = PgSql.Command(connection, transaction, """
                SELECT d."Id", st."DocumentProcessedAt", st."Status", st."IndexedChunks", st."RejectedChunks", st."Failure",
                    s."Enabled", st."NextAttemptAt" < now() AS "Expired"
                FROM "KnowledgeDocuments" AS d
                JOIN "KnowledgeSources" AS s ON s."WorkspaceId" = d."WorkspaceId" AND s."Id" = d."KnowledgeSourceId"
                LEFT JOIN "KnowledgeIndexStates" AS st ON st."SpaceKey" = @space AND st."WorkspaceId" = d."WorkspaceId" AND st."DocumentId" = d."Id"
                WHERE d."WorkspaceId" = @workspace AND d."Id" = ANY(@documents)
                """, ("space", settings.Space().Key), ("workspace", workspaceId), ("documents", processedAt.Keys.ToArray()));
            await using var reader = await read.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var document = reader.GetGuid(0);
                if (!reader.GetBoolean(6))
                {
                    result[document] = SourceDisabled; // not indexed and not retrievable while the source is disabled
                    continue;
                }
                if (reader.IsDBNull(1) || reader.GetFieldValue<DateTimeOffset>(1) != processedAt[document]) continue;
                var (indexed, rejected) = (reader.GetInt32(3), reader.GetInt32(4));
                result[document] = reader.GetString(2) switch
                {
                    "INDEXING" when !reader.GetBoolean(7) => new("INDEXING", indexed, rejected, null),
                    "INDEXED" => new("INDEXED", indexed, rejected, null),
                    "FAILED" => new("FAILED", 0, rejected, reader.IsDBNull(5) ? null : reader.GetString(5)),
                    // A recheck of partly rejected content keeps the searchable vectors of the last attempt.
                    _ when indexed > 0 => new("INDEXED", indexed, rejected, null),
                    _ => Pending
                };
            }
            return 0;
        }, cancellationToken);
        return result;
    }
}
