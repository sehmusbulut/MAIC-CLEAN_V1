using MAIC.Application.AI;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>Seeds the rows vector foreign keys reference, with the exact IDs a test's keys carry.</summary>
internal static class PgVectorSeed
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    public static async Task<Guid> UserAsync(PostgresFixture fixture)
    {
        await using var db = fixture.CreateContext();
        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        return user.Id;
    }

    /// <summary>
    /// Creates the workspace, TEXT source, READY document and chunk of each key when missing. Chunk sequences are assigned
    /// per document (contract keys may reuse a sequence); vectors keep the key's sequence as informational metadata.
    /// </summary>
    public static async Task KeysAsync(PostgresFixture fixture, Guid userId, IEnumerable<KnowledgeVectorKey> keys)
    {
        foreach (var key in keys)
        {
            // IDs are global primary keys: a key reusing another workspace's source cannot be represented, so it is left
            // unseeded (the store then stores nothing for it).
            await using (var check = fixture.CreateContext())
                if (await check.KnowledgeSources.AnyAsync(s => s.Id == key.KnowledgeSourceId && s.WorkspaceId != key.WorkspaceId, Cancellation))
                    continue;
            await using var db = fixture.CreateContext();
            if (!await db.Workspaces.AnyAsync(w => w.Id == key.WorkspaceId, Cancellation))
            {
                var workspace = new Workspace("Vector tests");
                db.Workspaces.Add(workspace);
                db.Entry(workspace).Property(w => w.Id).CurrentValue = key.WorkspaceId;
            }
            if (!await db.KnowledgeSources.AnyAsync(s => s.Id == key.KnowledgeSourceId, Cancellation))
            {
                var source = new KnowledgeSource(key.WorkspaceId, KnowledgeSourceKind.TEXT, "Vector source", "text", null);
                db.KnowledgeSources.Add(source);
                db.Entry(source).Property(s => s.Id).CurrentValue = key.KnowledgeSourceId;
            }
            if (!await db.KnowledgeDocuments.AnyAsync(d => d.Id == key.DocumentId, Cancellation))
            {
                var document = KnowledgeDocument.ForSnapshot(key.WorkspaceId, key.KnowledgeSourceId, KnowledgeSourceKind.TEXT, userId, Guid.NewGuid());
                document.MarkReady(1, 4, DateTimeOffset.UtcNow, KnowledgeDocument.TextPlain, 4, new string('a', 64));
                db.KnowledgeDocuments.Add(document);
                db.Entry(document).Property(d => d.Id).CurrentValue = key.DocumentId;
            }
            await db.SaveChangesAsync(Cancellation);
            if (!await db.KnowledgeDocumentChunks.AnyAsync(c => c.Id == key.ChunkId, Cancellation))
            {
                var sequence = 1 + (await db.KnowledgeDocumentChunks.Where(c => c.DocumentId == key.DocumentId)
                    .MaxAsync(c => (int?)c.Sequence, Cancellation) ?? 0);
                var chunk = new KnowledgeDocumentChunk(key.WorkspaceId, key.DocumentId, sequence, 0, "chunk");
                db.KnowledgeDocumentChunks.Add(chunk);
                db.Entry(chunk).Property(c => c.Id).CurrentValue = key.ChunkId;
                await db.SaveChangesAsync(Cancellation);
            }
        }
    }

    public static async Task RegisterAsync(PostgresFixture fixture, EmbeddingSpace space, string model = "test-model")
    {
        await using var db = fixture.CreateContext();
        await new EmbeddingSpaceRegistry(db).EnsureAsync(space, model, Cancellation);
    }

    public static async Task<int> CountVectorsAsync(PostgresFixture fixture, string where, params object[] args)
    {
        await using var db = fixture.CreateContext();
        // The filter is a test constant; values stay parameters.
        return await db.Database.SqlQueryRaw<int>("""SELECT count(*)::int AS "Value" FROM "KnowledgeChunkVectors" WHERE """ + where, args)
            .SingleAsync(Cancellation);
    }
}

/// <summary>The shared store contract against pgvector, with sequential scans disabled so the HNSW path is exercised.</summary>
public sealed class PgVectorIndexedStoreContractTests(PostgresFixture fixture)
    : PgVectorStoreContract(fixture, new PgVectorSearchOptions { ForceIndexScan = true, ExactSearchThreshold = 0 }), IClassFixture<PostgresFixture>;

/// <summary>The same contract with the planner's default choices (small tables usually scan exactly).</summary>
public sealed class PgVectorDefaultStoreContractTests(PostgresFixture fixture)
    : PgVectorStoreContract(fixture, new PgVectorSearchOptions()), IClassFixture<PostgresFixture>;

public abstract class PgVectorStoreContract(PostgresFixture fixture, PgVectorSearchOptions options) : KnowledgeVectorStoreContract, IAsyncDisposable
{
    private readonly List<MaicDbContext> contexts = [];
    private Guid? userId;

    protected override async Task<IKnowledgeVectorStore> CreateStoreAsync()
    {
        await PgVectorSeed.RegisterAsync(fixture, Space);
        await PgVectorSeed.RegisterAsync(fixture, OtherSpace);
        var db = fixture.CreateContext();
        contexts.Add(db);
        return new PgVectorKnowledgeStore(db, options);
    }

    protected override async Task PrepareKeysAsync(IReadOnlyCollection<KnowledgeVectorKey> keys) =>
        await PgVectorSeed.KeysAsync(fixture, userId ??= await PgVectorSeed.UserAsync(fixture), keys);

    public async ValueTask DisposeAsync()
    {
        foreach (var db in contexts) await db.DisposeAsync();
        GC.SuppressFinalize(this);
    }
}
