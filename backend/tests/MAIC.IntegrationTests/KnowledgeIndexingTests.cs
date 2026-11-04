using MAIC.Application.AI;
using MAIC.Domain.Knowledge;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>The indexing stage against real PostgreSQL/pgvector: discovery, fair claims, leases, policy and search.</summary>
public sealed class KnowledgeIndexingTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private sealed class Embeddings(EmbeddingSpace space) : IEmbeddingGenerator
    {
        public Func<IReadOnlyList<string>, EmbeddingException?> Fail { get; set; } = _ => null;
        public int Calls;
        public EmbeddingSpace Space { get; } = space;
        public int MaxBatchSize => 8;
        public int MaxInputLength => KnowledgeChunker.MaxChunkLength;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
        {
            Interlocked.Increment(ref Calls);
            if (Fail(inputs) is { } error) throw error;
            // Texts mentioning "refund" point one way, everything else another.
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(inputs.Select(text =>
                (ReadOnlyMemory<float>)(text.Contains("refund") ? new float[] { 1, 0.1f, 0 } : new float[] { 0, 1, 0.1f })).ToArray());
        }
    }

    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = KnowledgeIndexingTests.Now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private async Task<EmbeddingSpace> SpaceAsync()
    {
        var space = new EmbeddingSpace($"ix-{Guid.NewGuid():N}"[..20], 3);
        await PgVectorSeed.RegisterAsync(fixture, space);
        return space;
    }

    /// <summary>A workspace with one enabled TEXT source per text list, each with a READY document holding those chunks.</summary>
    private async Task<(Guid Workspace, Guid[] Sources, Guid[] Documents)> WorkspaceAsync(params string[][] documents)
    {
        var user = await PgVectorSeed.UserAsync(fixture);
        var workspace = Guid.CreateVersion7();
        var (sources, ids) = (new List<Guid>(), new List<Guid>());
        foreach (var texts in documents)
        {
            var (source, document) = (Guid.CreateVersion7(), Guid.CreateVersion7());
            await PgVectorSeed.KeysAsync(fixture, user, texts.Select(_ => new KnowledgeVectorKey(workspace, source, document, Guid.CreateVersion7(), 1)));
            await using var db = fixture.CreateContext();
            var chunks = await db.KnowledgeDocumentChunks.Where(c => c.DocumentId == document).OrderBy(c => c.Sequence).ToListAsync(Cancellation);
            for (var i = 0; i < chunks.Count; i++)
                await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocumentChunks" SET "Content" = {0} WHERE "Id" = {1}""",
                    [texts[i], chunks[i].Id], Cancellation);
            (sources, ids) = ([.. sources, source], [.. ids, document]);
        }
        return (workspace, sources.ToArray(), ids.ToArray());
    }

    private (KnowledgeIndexProcessor Processor, MaicDbContext Db) Processor(Embeddings embeddings, Clock clock)
    {
        var db = fixture.CreateContext();
        return (new KnowledgeIndexProcessor(new KnowledgeIndexStore(db), new KnowledgeVectorIndex(embeddings, new PgVectorKnowledgeStore(db)), clock), db);
    }

    /// <summary>Discovers, then drops the states other tests' documents got in this test's space.</summary>
    private async Task<int> DiscoverAsync(EmbeddingSpace space, params Guid[] workspaces)
    {
        await using var db = fixture.CreateContext();
        await new KnowledgeIndexStore(db).DiscoverAsync(space, Now, 100_000, Cancellation);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND NOT ("WorkspaceId" = ANY({1}))""",
            [space.Key, workspaces], Cancellation);
        return await StatesAsync(space, "true");
    }

    private async Task<int> StatesAsync(EmbeddingSpace space, string where, params object[] args)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQueryRaw<int>(
            """SELECT count(*)::int AS "Value" FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND """ + where, [space.Key, .. args])
            .SingleAsync(Cancellation);
    }

    private async Task<(string Status, int Attempts, int Strikes, string? LastFailure, string? Failure, int Indexed, int Rejected, DateTimeOffset Next)>
        StateAsync(EmbeddingSpace space, Guid document)
    {
        await using var db = fixture.CreateContext();
        var row = await db.Database.SqlQueryRaw<StateRow>("""
            SELECT "Status", "Attempts", "MisconfiguredStrikes", "LastFailure", "Failure", "IndexedChunks", "RejectedChunks", "NextAttemptAt"
            FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND "DocumentId" = {1}
            """, space.Key, document).SingleAsync(Cancellation);
        return (row.Status, row.Attempts, row.MisconfiguredStrikes, row.LastFailure, row.Failure, row.IndexedChunks, row.RejectedChunks, row.NextAttemptAt);
    }

    private sealed record StateRow(string Status, int Attempts, int MisconfiguredStrikes, string? LastFailure, string? Failure, int IndexedChunks,
        int RejectedChunks, DateTimeOffset NextAttemptAt);

    [Fact]
    public async Task Ready_documents_are_indexed_once_and_become_searchable_within_their_workspace_and_scope()
    {
        var space = await SpaceAsync();
        var (workspace, sources, documents) = await WorkspaceAsync(["How do refunds work? refund policy", "Shipping takes 3 days"], ["Opening hours"]);
        var (other, _, _) = await WorkspaceAsync(["refund refund refund"]);
        Assert.Equal(3, await DiscoverAsync(space, workspace, other));
        Assert.Equal(0, await DiscoverAsync(space, workspace, other) - 3); // nothing new

        var embeddings = new Embeddings(space);
        var clock = new Clock();
        var (processor, db) = Processor(embeddings, clock);
        await using var _ = db;
        var outcomes = new List<KnowledgeIndexOutcome>();
        while (await processor.ProcessNextAsync(Cancellation) is { } outcome) outcomes.Add(outcome);
        Assert.Equal(3, outcomes.Count);
        Assert.All(outcomes, o => Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, o.Kind));
        Assert.Equal(("INDEXED", 2, 0), (await StateAsync(space, documents[0])) is var s ? (s.Status, s.Indexed, s.Rejected) : default);

        var index = new KnowledgeVectorIndex(embeddings, new PgVectorKnowledgeStore(db));
        var results = await index.SearchAsync(workspace, KnowledgeScopeFilter.All, "refund please", 10, Cancellation);
        Assert.Equal(3, results.Count);
        Assert.All(results, r => Assert.Equal(workspace, r.Key.WorkspaceId));
        Assert.Equal(documents[0], results[0].Key.DocumentId);
        Assert.All(await index.SearchAsync(workspace, KnowledgeScopeFilter.Selected([sources[1]]), "refund", 10, Cancellation),
            r => Assert.Equal(sources[1], r.Key.KnowledgeSourceId));

        // Indexed documents are not claimed again.
        Assert.Null(await processor.ProcessNextAsync(Cancellation));
    }

    [Fact]
    public async Task Disabled_sources_and_non_ready_documents_are_not_indexed_and_reprocessed_content_is_indexed_again()
    {
        var space = await SpaceAsync();
        var (workspace, sources, documents) = await WorkspaceAsync(["alpha"], ["beta"]);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [sources[1]], Cancellation);
        Assert.Equal(1, await DiscoverAsync(space, workspace));

        var embeddings = new Embeddings(space);
        var (processor, context) = Processor(embeddings, new Clock());
        await using var _ = context;
        Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, (await processor.ProcessNextAsync(Cancellation))!.Kind);
        Assert.Null(await processor.ProcessNextAsync(Cancellation));

        // Re-enabled: discovered and indexed. Content processed again (new ProcessedAt): indexed again.
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = true WHERE "Id" = {0}""", [sources[1]], Cancellation);
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeDocuments" SET "ProcessedAt" = "ProcessedAt" + interval '1 second' WHERE "Id" = {0}""",
                [documents[0]], Cancellation);
        }
        Assert.Equal(2, await DiscoverAsync(space, workspace));
        Assert.Equal(("PENDING", 0), (await StateAsync(space, documents[0])) is var s ? (s.Status, s.Attempts) : default);
        Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, (await processor.ProcessNextAsync(Cancellation))!.Kind);
        Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, (await processor.ProcessNextAsync(Cancellation))!.Kind);

        // A document no longer READY is not claimed even with a due state.
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeIndexStates" SET "Status" = 'PENDING' WHERE "SpaceKey" = {0}""", [space.Key], Cancellation);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "KnowledgeDocuments" SET "Status" = 'SUPERSEDED' WHERE "Id" = ANY({0});
                """, [documents], Cancellation);
        }
        Assert.Null(await processor.ProcessNextAsync(Cancellation));
    }

    [Fact]
    public async Task Provider_faults_retry_with_backoff_rejected_content_fails_and_leases_protect_transitions()
    {
        var space = await SpaceAsync();
        var (workspace, _, documents) = await WorkspaceAsync(["good text", "bad text"], ["bad only"], ["fine"]);
        await DiscoverAsync(space, workspace);
        var embeddings = new Embeddings(space)
        {
            Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? new EmbeddingException(EmbeddingFailure.REJECTED_INPUT) : null
        };
        var clock = new Clock();
        var (processor, db) = Processor(embeddings, clock);
        await using var _ = db;
        while (await processor.ProcessNextAsync(Cancellation) is not null) { }
        Assert.Equal(("INDEXED", 1, 1), (await StateAsync(space, documents[0])) is var a ? (a.Status, a.Indexed, a.Rejected) : default);
        Assert.Equal(("FAILED", "EMBEDDING_REJECTED", 1), (await StateAsync(space, documents[1])) is var b ? (b.Status, b.Failure, b.Rejected) : default);

        // An outage: retried later with backoff, never failed.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeIndexStates" SET "Status" = 'PENDING', "Attempts" = 0 WHERE "SpaceKey" = {0} AND "DocumentId" = {1}""",
            [space.Key, documents[2]], Cancellation);
        embeddings.Fail = _ => new EmbeddingException(EmbeddingFailure.UNAVAILABLE, retryAfter: TimeSpan.FromMinutes(3));
        Assert.Equal(KnowledgeIndexOutcomeKind.Retrying, (await processor.ProcessNextAsync(Cancellation))!.Kind);
        var retry = await StateAsync(space, documents[2]);
        Assert.Equal(("RETRY", 1, "UNAVAILABLE"), (retry.Status, retry.Attempts, retry.LastFailure));
        Assert.Equal(clock.Now + TimeSpan.FromMinutes(3), retry.Next, TimeSpan.FromMilliseconds(1));
        Assert.Null(await processor.ProcessNextAsync(Cancellation)); // not due yet

        // Misconfiguration counts strikes; success resets them.
        embeddings.Fail = _ => new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
        clock.Now += TimeSpan.FromMinutes(4);
        await processor.ProcessNextAsync(Cancellation);
        clock.Now += TimeSpan.FromHours(2);
        await processor.ProcessNextAsync(Cancellation);
        Assert.Equal(("RETRY", 2, "MISCONFIGURED"), (await StateAsync(space, documents[2])) is var m ? (m.Status, m.Strikes, m.LastFailure) : default);
        embeddings.Fail = _ => null;
        clock.Now += TimeSpan.FromHours(2);
        Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, (await processor.ProcessNextAsync(Cancellation))!.Kind);
        Assert.Equal(("INDEXED", 0, (string?)null), (await StateAsync(space, documents[2])) is var r ? (r.Status, r.Strikes, r.LastFailure) : default);

        // A stale lease token cannot change a state that another worker re-claimed.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeIndexStates" SET "Status" = 'PENDING' WHERE "SpaceKey" = {0} AND "DocumentId" = {1}""",
            [space.Key, documents[2]], Cancellation);
        var store = new KnowledgeIndexStore(db);
        var first = await store.ClaimNextAsync(space, clock.Now, TimeSpan.FromMinutes(1), Cancellation);
        Assert.NotNull(first);
        Assert.Null(await store.ClaimNextAsync(space, clock.Now, TimeSpan.FromMinutes(1), Cancellation)); // held
        var second = await store.ClaimNextAsync(space, clock.Now + TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(1), Cancellation); // lease expired
        Assert.Equal(first!.DocumentId, second!.DocumentId);
        Assert.False(await store.CompleteAsync(space, first, 1, 0, clock.Now, Cancellation));
        Assert.True(await store.CompleteAsync(space, second, 1, 0, clock.Now, Cancellation));
    }

    [Fact]
    public async Task Discovery_takes_a_bounded_share_per_workspace_and_rechecks_rejected_content_a_few_times()
    {
        var space = await SpaceAsync();
        var backlog = Enumerable.Range(0, KnowledgeIndexStore.DiscoveryPerWorkspace + 5).Select(i => new[] { $"doc {i}" }).ToArray();
        var (busy, _, _) = await WorkspaceAsync(backlog);
        var (quiet, _, quietDocuments) = await WorkspaceAsync(["late arrival"]);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeIndexStore(db);
        await store.DiscoverAsync(space, Now, 100_000, Cancellation);
        Assert.Equal(KnowledgeIndexStore.DiscoveryPerWorkspace, await StatesAsync(space, "\"WorkspaceId\" = {1}", busy));
        Assert.Equal(1, await StatesAsync(space, "\"WorkspaceId\" = {1}", quiet));

        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "KnowledgeIndexStates" SET "Status" = 'FAILED', "Failure" = 'EMBEDDING_REJECTED', "UpdatedAt" = {1}
            WHERE "SpaceKey" = {0} AND "DocumentId" = {2}
            """, [space.Key, Now - KnowledgeIndexStore.RecheckAfter, quietDocuments[0]], Cancellation);
        for (var round = 1; round <= KnowledgeIndexStore.MaxRechecks + 1; round++)
        {
            await store.DiscoverAsync(space, Now, 0, Cancellation);
            var state = await StateAsync(space, quietDocuments[0]);
            Assert.Equal(round <= KnowledgeIndexStore.MaxRechecks ? "PENDING" : "FAILED", state.Status);
            await db.Database.ExecuteSqlRawAsync("""
                UPDATE "KnowledgeIndexStates" SET "Status" = 'FAILED', "Failure" = 'EMBEDDING_REJECTED', "UpdatedAt" = {1}
                WHERE "SpaceKey" = {0} AND "DocumentId" = {2}
                """, [space.Key, Now - KnowledgeIndexStore.RecheckAfter, quietDocuments[0]], Cancellation);
        }
    }

    [Fact]
    public async Task Claims_are_fair_across_workspaces_and_states_cascade_with_documents()
    {
        var space = await SpaceAsync();
        var (busy, _, _) = await WorkspaceAsync(["a"], ["b"], ["c"], ["d"]);
        var (quiet, _, quietDocuments) = await WorkspaceAsync(["e"]);
        await DiscoverAsync(space, busy, quiet);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeIndexStore(db);
        // A single worker completing one document at a time: after serving a workspace, the other one is next.
        var first = await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(5), Cancellation);
        Assert.True(await store.CompleteAsync(space, first!, 1, 0, Now, Cancellation));
        var second = await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(5), Cancellation);
        Assert.NotEqual(first!.WorkspaceId, second!.WorkspaceId);

        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeDocuments" WHERE "Id" = {0}""", [quietDocuments[0]], Cancellation);
        Assert.Equal(0, await StatesAsync(space, "\"WorkspaceId\" = {1}", quiet));
    }
}
