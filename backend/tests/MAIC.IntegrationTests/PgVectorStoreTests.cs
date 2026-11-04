using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Infrastructure.AI;
using MAIC.Infrastructure.Persistence;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>pgvector specifics beyond the shared contract: registry, index use, dimensions and foreign-key cascades.</summary>
public sealed class PgVectorStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    private static EmbeddingSpace NewSpace(int dimensions) => new($"t-{Guid.NewGuid():N}"[..20], dimensions);

    private static float[] Unit(int dimensions, int hot) => Enumerable.Range(0, dimensions).Select(i => i == hot ? 1f : 0.01f).ToArray();

    private async Task<KnowledgeVector[]> SeedAsync(EmbeddingSpace space, Guid workspace, Guid source, Guid document, int count)
    {
        var vectors = Enumerable.Range(1, count).Select(i => new KnowledgeVector(
            new KnowledgeVectorKey(workspace, source, document, Guid.CreateVersion7(), i), Unit(space.Dimensions, i % space.Dimensions))).ToArray();
        await PgVectorSeed.KeysAsync(fixture, await PgVectorSeed.UserAsync(fixture), vectors.Select(v => v.Key));
        await using var db = fixture.CreateContext();
        await new PgVectorKnowledgeStore(db).ReplaceDocumentAsync(space, workspace, document, vectors, Cancellation);
        return vectors;
    }

    [Fact]
    public async Task A_space_key_is_bound_to_its_dimensions_and_model_and_gets_a_partial_hnsw_index()
    {
        var space = NewSpace(8);
        await PgVectorSeed.RegisterAsync(fixture, space, "model-a");
        await PgVectorSeed.RegisterAsync(fixture, space, "model-a"); // idempotent
        await Assert.ThrowsAsync<InvalidOperationException>(() => PgVectorSeed.RegisterAsync(fixture, space, "model-b"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PgVectorSeed.RegisterAsync(fixture, new EmbeddingSpace(space.Key, 9), "model-a"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => PgVectorSeed.RegisterAsync(fixture, new EmbeddingSpace("disabled", 8)));

        await using var db = fixture.CreateContext();
        var definition = await db.Database.SqlQueryRaw<string>(
            """SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = {0}""", PgSql.IndexName(space)).SingleAsync(Cancellation);
        Assert.Contains("USING hnsw", definition);
        Assert.Contains("vector(8)", definition);
        Assert.Contains($"'{space.Key}'", definition);

        // Concurrent registration (API and Worker starting together) converges on one row.
        var again = NewSpace(8);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => PgVectorSeed.RegisterAsync(fixture, again)));
        Assert.Equal(1, await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0}""", again.Key).SingleAsync(Cancellation));
    }

    [Fact]
    public async Task Hosts_register_the_configured_space_at_startup_and_refuse_a_mismatch()
    {
        EmbeddingSettings Settings(string key, int dimensions, string model) => new()
        {
            Provider = EmbeddingProvider.OpenAICompatible, SpaceKey = key, Dimensions = dimensions, Model = model,
            Endpoint = "https://embeddings.example.com/v1"
        };
        ServiceProvider Host(EmbeddingSettings settings) => new ServiceCollection().AddLogging()
            .AddDbContext<MaicDbContext>(options => options.UseNpgsql(fixture.ConnectionString))
            .AddMaicEmbeddings(settings).BuildServiceProvider();

        var key = NewSpace(16).Key;
        await using (var host = Host(Settings(key, 16, "model-a")))
            foreach (var service in host.GetServices<IHostedService>()) await service.StartAsync(Cancellation);
        await using (var db = fixture.CreateContext())
            Assert.Equal(16, await db.Database.SqlQueryRaw<int>(
                """SELECT "Dimensions" AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0}""", key).SingleAsync(Cancellation));

        await using (var changed = Host(Settings(key, 32, "model-b")))
        {
            var check = Assert.IsType<EmbeddingSpaceStartupCheck>(Assert.Single(changed.GetServices<IHostedService>()));
            await Assert.ThrowsAsync<InvalidOperationException>(() => check.StartAsync(Cancellation));
        }
        // Without a provider nothing is registered at startup.
        await using (var disabled = new ServiceCollection().AddDbContext<MaicDbContext>(options => options.UseNpgsql(fixture.ConnectionString))
                         .AddMaicEmbeddings(new EmbeddingSettings()).BuildServiceProvider())
            Assert.Empty(disabled.GetServices<IHostedService>());
    }

    [Fact]
    public async Task The_forced_search_plan_uses_the_spaces_hnsw_index_with_the_filters_inside_the_scan()
    {
        var space = NewSpace(3);
        await PgVectorSeed.RegisterAsync(fixture, space);
        var workspace = Guid.CreateVersion7();
        await SeedAsync(space, workspace, Guid.CreateVersion7(), Guid.CreateVersion7(), 5);
        await using var db = fixture.CreateContext();
        var store = new PgVectorKnowledgeStore(db, new PgVectorSearchOptions { ForceIndexScan = true, ExactSearchThreshold = 0 });
        var request = new VectorSearchRequest(workspace, space, KnowledgeScopeFilter.All.Excluding([Guid.CreateVersion7()]), new float[] { 1, 0, 0 }, 3);

        await using var transaction = await db.Database.BeginTransactionAsync(Cancellation);
        var (connection, npgsql) = ((NpgsqlConnection)db.Database.GetDbConnection(), (NpgsqlTransaction)transaction.GetDbTransaction());
        await using (var configure = new NpgsqlCommand(store.SessionSettings(request), connection, npgsql))
            await configure.ExecuteNonQueryAsync(Cancellation);
        await using var explain = PgVectorKnowledgeStore.SearchCommand(connection, npgsql, request, "EXPLAIN (FORMAT JSON) ");
        var plan = JsonDocument.Parse((string)(await explain.ExecuteScalarAsync(Cancellation))!).RootElement.GetRawText();
        Assert.Contains(PgSql.IndexName(space), plan);
        Assert.Contains("\"Index Scan\"", plan);
        Assert.Contains("WorkspaceId", plan); // workspace and exclusion filters are evaluated during the index scan
    }

    [Fact]
    public async Task A_heavy_co_tenant_cannot_starve_another_workspaces_search()
    {
        var space = NewSpace(3);
        await PgVectorSeed.RegisterAsync(fixture, space);
        var (victim, heavy) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var user = await PgVectorSeed.UserAsync(fixture);
        var own = Enumerable.Range(1, 3).Select(i => new KnowledgeVector(
            new KnowledgeVectorKey(victim, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1), new float[] { 0.1f, 1, 0.1f * i })).ToArray();
        var crowd = Enumerable.Range(1, 300).Select(i => new KnowledgeVector(
            new KnowledgeVectorKey(heavy, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1), new float[] { 1, 0.001f * i, 0 })).ToArray();
        await PgVectorSeed.KeysAsync(fixture, user, own.Concat(crowd).Select(v => v.Key));
        await using var db = fixture.CreateContext();
        foreach (var vector in own.Concat(crowd))
            await new PgVectorKnowledgeStore(db).ReplaceDocumentAsync(space, vector.Key.WorkspaceId, vector.Key.DocumentId, [vector], Cancellation);
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "KnowledgeChunkVectors" """, Cancellation);

        var request = new VectorSearchRequest(victim, space, KnowledgeScopeFilter.All, new float[] { 1, 0, 0 }, 3);
        // Small workspaces are searched exactly by default, which cannot starve:
        Assert.Equal(3, (await new PgVectorKnowledgeStore(db).SearchAsync(request, Cancellation)).Count);
        // A small scan budget stands in for millions of nearer foreign vectors: the HNSW scan alone comes back short...
        var starved = await new PgVectorKnowledgeStore(db, new PgVectorSearchOptions { ForceAnnScan = true, ForceIndexScan = true, ExactSearchThreshold = 0, MaxScanTuples = 50, DisableExactFallback = true })
            .SearchAsync(request, Cancellation);
        Assert.True(starved.Count < 3, $"expected the bounded HNSW scan to starve, got {starved.Count}");
        // ...and the exact per-workspace rerun restores the full answer.
        var results = await new PgVectorKnowledgeStore(db, new PgVectorSearchOptions { ForceAnnScan = true, ForceIndexScan = true, ExactSearchThreshold = 0, MaxScanTuples = 50 })
            .SearchAsync(request, Cancellation);
        Assert.Equal(own.Select(v => v.Key.ChunkId).Order(), results.Select(r => r.Key.ChunkId).Order());
    }

    [Fact]
    public async Task Vectors_are_stored_at_unit_length_so_halfvec_spaces_accept_any_finite_magnitude()
    {
        var space = NewSpace(2500);
        await PgVectorSeed.RegisterAsync(fixture, space);
        var workspace = Guid.CreateVersion7();
        var key = new KnowledgeVectorKey(workspace, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        await PgVectorSeed.KeysAsync(fixture, await PgVectorSeed.UserAsync(fixture), [key]);
        var huge = Enumerable.Range(0, 2500).Select(i => i == 0 ? 70000f : 1e-7f).ToArray(); // beyond half precision range
        var tiny = Enumerable.Range(0, 2500).Select(i => 1e-8f * (i + 1)).ToArray(); // underflows to zero in half precision
        await using var db = fixture.CreateContext();
        var store = new PgVectorKnowledgeStore(db, new PgVectorSearchOptions { ForceIndexScan = true });
        await store.ReplaceDocumentAsync(space, workspace, key.DocumentId, [new KnowledgeVector(key, huge)], Cancellation);
        var match = Assert.Single(await store.SearchAsync(new VectorSearchRequest(workspace, space, KnowledgeScopeFilter.All, huge, 1), Cancellation));
        Assert.Equal(1, match.Score, 3);
        await store.ReplaceDocumentAsync(space, workspace, key.DocumentId, [new KnowledgeVector(key, tiny)], Cancellation);
        Assert.True(double.IsFinite(Assert.Single(await store.SearchAsync(
            new VectorSearchRequest(workspace, space, KnowledgeScopeFilter.All, tiny, 1), Cancellation)).Score));
    }

    [Theory]
    [InlineData(2048)] // native NVIDIA Nemotron embedding size, existing halfvec index
    [InlineData(2500)] // halfvec index
    [InlineData(4096)] // above pgvector's HNSW limits: exact scan, still correct
    public async Task Large_dimensions_are_indexed_or_scanned_exactly(int dimensions)
    {
        var space = NewSpace(dimensions);
        await PgVectorSeed.RegisterAsync(fixture, space);
        var (workspace, source) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var vectors = await SeedAsync(space, workspace, source, Guid.CreateVersion7(), 3);
        await SeedAsync(space, Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 3);
        await SeedAsync(space, workspace, Guid.CreateVersion7(), Guid.CreateVersion7(), 3);
        await using var db = fixture.CreateContext();
        var results = await new PgVectorKnowledgeStore(db, new PgVectorSearchOptions { ForceIndexScan = true }).SearchAsync(
            new VectorSearchRequest(workspace, space, KnowledgeScopeFilter.Selected([source]), vectors[1].Vector, 3), Cancellation);
        Assert.Equal(3, results.Count);
        Assert.All(results, result =>
        {
            Assert.Equal(workspace, result.Key.WorkspaceId);
            Assert.Equal(source, result.Key.KnowledgeSourceId);
        });
        Assert.Equal(vectors[1].Key, results[0].Key);
        Assert.Equal(1, results[0].Score, 3);
        var indexCount = await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM pg_indexes WHERE indexname = {0}""", PgSql.IndexName(space)).SingleAsync(Cancellation);
        Assert.Equal(dimensions <= 4000 ? 1 : 0, indexCount);
    }

    [Fact]
    public async Task Vectors_follow_their_chunks_documents_and_workspaces_and_are_never_stored_for_missing_or_foreign_rows()
    {
        var space = NewSpace(3);
        await PgVectorSeed.RegisterAsync(fixture, space);
        var (workspace, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var (source, document, doomed) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var kept = await SeedAsync(space, workspace, source, document, 3);
        await SeedAsync(space, workspace, source, doomed, 2);
        var foreign = await SeedAsync(space, other, Guid.CreateVersion7(), Guid.CreateVersion7(), 1);
        Assert.Equal(5, await PgVectorSeed.CountVectorsAsync(fixture, "\"WorkspaceId\" = {0}", workspace));

        await using (var db = fixture.CreateContext())
        {
            // Deleting a document (cascade from the API's delete) removes its vectors in every space.
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeDocuments" WHERE "Id" = {0}""", [doomed], Cancellation);
            // Chunks replaced or superseded: their vectors go too.
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeDocumentChunks" WHERE "Id" = {0}""", [kept[0].Key.ChunkId], Cancellation);
        }
        Assert.Equal(2, await PgVectorSeed.CountVectorsAsync(fixture, "\"WorkspaceId\" = {0}", workspace));

        await using (var db = fixture.CreateContext())
        {
            var store = new PgVectorKnowledgeStore(db);
            // A gone chunk, a key claiming another source, and another workspace's chunk store nothing.
            await store.ReplaceDocumentAsync(space, workspace, document, [kept[0], kept[1] with { Key = kept[1].Key with { KnowledgeSourceId = Guid.CreateVersion7() } }], Cancellation);
            Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));
            var stolen = new KnowledgeVector(foreign[0].Key with { WorkspaceId = workspace, DocumentId = document }, foreign[0].Vector);
            await store.ReplaceDocumentAsync(space, workspace, document, [stolen], Cancellation);
            Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));
            Assert.Equal(1, await PgVectorSeed.CountVectorsAsync(fixture, "\"WorkspaceId\" = {0}", other));

            // A worker that loaded chunks before the source was disabled (or the document left READY) stores nothing.
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [source], Cancellation);
            await store.ReplaceDocumentAsync(space, workspace, document, [kept[2]], Cancellation);
            Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = true WHERE "Id" = {0}""", [source], Cancellation);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocuments" SET "Status" = 'SUPERSEDED' WHERE "Id" = {0}""", [document], Cancellation);
            await store.ReplaceDocumentAsync(space, workspace, document, [kept[2]], Cancellation);
            Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));

            // Unregistered spaces cannot be written; unknown or resized spaces return nothing.
            await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReplaceDocumentAsync(NewSpace(3), workspace, document, [kept[2]], Cancellation));
            Assert.Empty(await store.SearchAsync(new VectorSearchRequest(workspace, NewSpace(3), KnowledgeScopeFilter.All, new float[] { 1, 0, 0 }, 3), Cancellation));
        }

        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""DELETE FROM "Workspaces" WHERE "Id" = {0}""", [other], Cancellation);
        Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"WorkspaceId\" = {0}", other));
    }
}
