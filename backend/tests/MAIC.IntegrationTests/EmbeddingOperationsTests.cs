using MAIC.Application.AI;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using MAIC.Infrastructure.AI;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>Operational guarantees around indexing: lease fencing, quotas, status, retirement and schema roles.</summary>
public sealed class EmbeddingOperationsTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    private static EmbeddingSpace NewSpace(int dimensions = 3) => new($"op-{Guid.NewGuid():N}"[..20], dimensions);

    private sealed class Embeddings(EmbeddingSpace space) : IEmbeddingGenerator
    {
        public EmbeddingSpace Space { get; } = space;
        public int MaxBatchSize => 8;
        public int MaxInputLength => KnowledgeChunker.MaxChunkLength;
        public Func<Task>? BeforeReturn { get; set; }
        public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
        {
            if (BeforeReturn is not null) await BeforeReturn();
            return inputs.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 0.5f, 0 }).ToArray();
        }
    }

    private async Task<(Guid Workspace, Guid Source, Guid Document, KnowledgeVectorKey[] Keys)> DocumentAsync(int chunks = 2)
    {
        var (workspace, source, document) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var keys = Enumerable.Range(1, chunks).Select(i => new KnowledgeVectorKey(workspace, source, document, Guid.CreateVersion7(), i)).ToArray();
        await PgVectorSeed.KeysAsync(fixture, await PgVectorSeed.UserAsync(fixture), keys);
        return (workspace, source, document, keys);
    }

    private async Task<int> ScalarAsync(string sql, params object[] args)
    {
        await using var db = fixture.CreateContext();
        return await db.Database.SqlQueryRaw<int>(sql, args).SingleAsync(Cancellation);
    }

    private async Task DiscoverOnlyAsync(EmbeddingSpace space, params Guid[] workspaces)
    {
        await using var db = fixture.CreateContext();
        await new KnowledgeIndexStore(db).DiscoverAsync(space, Now, 100_000, Cancellation);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND NOT ("WorkspaceId" = ANY({1}))""",
            [space.Key, workspaces], Cancellation);
    }

    [Fact]
    public async Task A_worker_whose_lease_expired_cannot_write_vectors()
    {
        var space = NewSpace();
        await PgVectorSeed.RegisterAsync(fixture, space);
        var (workspace, _, document, keys) = await DocumentAsync();
        await DiscoverOnlyAsync(space, workspace);

        await using var db = fixture.CreateContext();
        var store = new KnowledgeIndexStore(db);
        var stale = await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(1), Cancellation);
        // The stale worker is slow; meanwhile its lease expires and another worker takes the document over.
        var embeddings = new Embeddings(space);
        embeddings.BeforeReturn = async () =>
        {
            embeddings.BeforeReturn = null;
            await using var other = fixture.CreateContext();
            Assert.NotNull(await new KnowledgeIndexStore(other).ClaimNextAsync(space, Now + TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), Cancellation));
        };
        var outcome = await new KnowledgeIndexProcessor(new StaleClaim(store, stale!), new KnowledgeVectorIndex(embeddings, new PgVectorKnowledgeStore(db)),
            TimeProvider.System).ProcessNextAsync(Cancellation);
        Assert.Equal(KnowledgeIndexOutcomeKind.LeaseLost, outcome!.Kind);
        Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));

        // The holder of the lease writes normally.
        var index = new KnowledgeVectorIndex(new Embeddings(space), new PgVectorKnowledgeStore(db));
        var chunks = keys.Select(k => new KnowledgeChunkText(k, "text")).ToArray();
        await Assert.ThrowsAsync<KnowledgeIndexLeaseLostException>(() =>
            index.IndexDocumentAsync(workspace, document, chunks, KnowledgeIndexMode.SkipRejected, stale!.LeaseToken, Cancellation));
        var current = await db.Database.SqlQueryRaw<Guid>("""SELECT "LeaseToken" AS "Value" FROM "KnowledgeIndexStates" WHERE "SpaceKey" = {0} AND "DocumentId" = {1}""",
            space.Key, document).SingleAsync(Cancellation);
        await index.IndexDocumentAsync(workspace, document, chunks, KnowledgeIndexMode.SkipRejected, current, Cancellation);
        Assert.Equal(2, await PgVectorSeed.CountVectorsAsync(fixture, "\"DocumentId\" = {0}", document));
    }

    /// <summary>Hands the processor an already made claim, as if this worker had claimed it before stalling.</summary>
    private sealed class StaleClaim(KnowledgeIndexStore inner, KnowledgeIndexWork work) : IKnowledgeIndexStore
    {
        public Task<int> DiscoverAsync(EmbeddingSpace s, DateTimeOffset n, int l, CancellationToken ct) => inner.DiscoverAsync(s, n, l, ct);
        public Task<KnowledgeIndexWork?> ClaimNextAsync(EmbeddingSpace s, DateTimeOffset n, TimeSpan l, CancellationToken ct) => Task.FromResult<KnowledgeIndexWork?>(work);
        public Task<IReadOnlyList<KnowledgeChunkText>> LoadChunksAsync(KnowledgeIndexWork w, CancellationToken ct) => inner.LoadChunksAsync(w, ct);
        public Task ChargeAsync(EmbeddingSpace s, KnowledgeIndexWork w, int c, DateTimeOffset n, CancellationToken ct) => inner.ChargeAsync(s, w, c, n, ct);
        public Task<bool> HasIndexingBudgetAsync(EmbeddingSpace s, DateTimeOffset n, CancellationToken ct) => inner.HasIndexingBudgetAsync(s, n, ct);
        public Task<bool> CompleteAsync(EmbeddingSpace s, KnowledgeIndexWork w, int i, int r, DateTimeOffset n, CancellationToken ct) => inner.CompleteAsync(s, w, i, r, n, ct);
        public Task<bool> RejectAsync(EmbeddingSpace s, KnowledgeIndexWork w, int r, DateTimeOffset n, CancellationToken ct) => inner.RejectAsync(s, w, r, n, ct);
        public Task<bool> RetryAsync(EmbeddingSpace s, KnowledgeIndexWork w, KnowledgeIndexFailure? f, DateTimeOffset x, DateTimeOffset n, CancellationToken ct) =>
            inner.RetryAsync(s, w, f, x, n, ct);
        public Task<bool> ReleaseAsync(EmbeddingSpace s, KnowledgeIndexWork w, DateTimeOffset n, CancellationToken ct) => inner.ReleaseAsync(s, w, n, ct);
    }

    [Fact]
    public async Task The_daily_quota_counts_every_attempt_and_survives_deleting_or_reingesting_content()
    {
        var space = NewSpace();
        await PgVectorSeed.RegisterAsync(fixture, space);
        var heavy = await DocumentAsync(6);
        var second = await DocumentAsync();
        await DiscoverOnlyAsync(space, heavy.Workspace, second.Workspace);
        await using var db = fixture.CreateContext();
        var limits = new KnowledgeIndexLimits(MaxIndexedChunksPerWorkspacePerDay: 10);
        var store = new KnowledgeIndexStore(db, limits);
        var processor = new KnowledgeIndexProcessor(store, new KnowledgeVectorIndex(new Embeddings(space), new PgVectorKnowledgeStore(db)),
            new FixedClock(Now));

        // Heavy indexes 6 chunks, then deletes the document (churn): its usage stays.
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeIndexStates" SET "NextAttemptAt" = {1} WHERE "SpaceKey" = {0} AND "WorkspaceId" = {2}""",
            [space.Key, Now - TimeSpan.FromMinutes(1), heavy.Workspace], Cancellation);
        Assert.Equal(heavy.Document, (await processor.ProcessNextAsync(Cancellation))!.DocumentId);
        await db.Database.ExecuteSqlRawAsync("""DELETE FROM "KnowledgeDocuments" WHERE "Id" = {0}""", [heavy.Document], Cancellation);
        Assert.Equal(6, await ScalarAsync("""SELECT "Chunks"::int AS "Value" FROM "KnowledgeIndexUsage" WHERE "SpaceKey" = {0} AND "WorkspaceId" = {1}""",
            space.Key, heavy.Workspace));

        // A retry is charged too: 6 + 6 reaches the quota of 10.
        var again = await DocumentAsync(6);
        await store.ChargeAsync(space, new KnowledgeIndexWork(heavy.Workspace, again.Document, Guid.NewGuid(), 1, 0), 6, Now, Cancellation);
        var user = await PgVectorSeed.UserAsync(fixture);
        await PgVectorSeed.KeysAsync(fixture, user, [new KnowledgeVectorKey(heavy.Workspace, heavy.Source, Guid.CreateVersion7(), Guid.CreateVersion7(), 1)]);
        await DiscoverOnlyAsync(space, heavy.Workspace, second.Workspace);

        var claim = await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(5), Cancellation);
        Assert.Equal(second.Workspace, claim!.WorkspaceId);
        Assert.Null(await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(5), Cancellation)); // heavy waits for the next UTC day
        var tomorrow = new DateTimeOffset(Now.UtcDateTime.Date.AddDays(1).AddMinutes(1), TimeSpan.Zero);
        Assert.Equal(heavy.Workspace, (await store.ClaimNextAsync(space, tomorrow, TimeSpan.FromMinutes(5), Cancellation))!.WorkspaceId);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    [Fact]
    public async Task The_indexing_budget_is_shared_by_all_workers_and_refills_per_minute()
    {
        var space = NewSpace();
        await PgVectorSeed.RegisterAsync(fixture, space);
        var work = new KnowledgeIndexWork(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.NewGuid(), 1, 0);
        var (workspace, _, _, _) = await DocumentAsync(1);
        work = work with { WorkspaceId = workspace };
        var limits = new KnowledgeIndexLimits(MaxIndexingChunksPerMinute: 60);
        await using var first = fixture.CreateContext();
        await using var second = fixture.CreateContext();
        var (workerA, workerB) = (new KnowledgeIndexStore(first, limits), new KnowledgeIndexStore(second, limits));

        Assert.True(await workerA.HasIndexingBudgetAsync(space, Now, Cancellation)); // no row yet: full budget, nothing written
        Assert.Equal(0, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM "KnowledgeIndexBudgets" WHERE "SpaceKey" = {0}""", space.Key));
        await workerA.ChargeAsync(space, work, 90, Now, Cancellation); // one large document overdraws
        Assert.False(await workerB.HasIndexingBudgetAsync(space, Now, Cancellation)); // the other Worker sees the same bucket

        // Refill follows the database clock (a Worker's own clock is ignored): 30 s later the debt of 30 is repaid.
        async Task AgeAsync(int seconds)
        {
            await using var aging = fixture.CreateContext();
            await aging.Database.ExecuteSqlRawAsync("""
                UPDATE "KnowledgeIndexBudgets" SET "RefilledAt" = "RefilledAt" - make_interval(secs => {1}) WHERE "SpaceKey" = {0}
                """, [space.Key, seconds], Cancellation);
        }
        await AgeAsync(29);
        Assert.False(await workerB.HasIndexingBudgetAsync(space, Now + TimeSpan.FromHours(5), Cancellation));
        await AgeAsync(2);
        Assert.True(await workerA.HasIndexingBudgetAsync(space, Now, Cancellation));

        // Refill is capped at one minute's worth.
        await AgeAsync(3600);
        await workerA.ChargeAsync(space, work, 61, Now, Cancellation); // a full minute's worth plus one: in debt again
        Assert.False(await workerA.HasIndexingBudgetAsync(space, Now, Cancellation));

        // Concurrent debits from many Workers are never lost.
        await AgeAsync(3600);
        var contexts = Enumerable.Range(0, 8).Select(_ => fixture.CreateContext()).ToArray();
        await Task.WhenAll(contexts.Select(c => new KnowledgeIndexStore(c, limits).ChargeAsync(space, work, 10, Now, Cancellation)));
        foreach (var c in contexts) await c.DisposeAsync();
        Assert.InRange(await ScalarAsync("""SELECT round("Tokens")::int AS "Value" FROM "KnowledgeIndexBudgets" WHERE "SpaceKey" = {0}""", space.Key), -20, -18);
    }

    [Fact]
    public async Task Claims_are_weighted_by_the_chunks_each_workspace_sent_today()
    {
        var space = NewSpace();
        await PgVectorSeed.RegisterAsync(fixture, space);
        var big = await DocumentAsync();
        var small = await DocumentAsync();
        await DiscoverOnlyAsync(space, big.Workspace, small.Workspace);
        await using var db = fixture.CreateContext();
        var store = new KnowledgeIndexStore(db, new KnowledgeIndexLimits(MaxIndexedChunksPerWorkspacePerDay: 10_000));
        // The big tenant already sent 2,000 chunks today (one large document); the small one sent 5.
        await store.ChargeAsync(space, new KnowledgeIndexWork(big.Workspace, big.Document, Guid.NewGuid(), 1, 0), 2000, Now, Cancellation);
        await store.ChargeAsync(space, new KnowledgeIndexWork(small.Workspace, small.Document, Guid.NewGuid(), 1, 0), 5, Now, Cancellation);
        Assert.Equal(small.Workspace, (await store.ClaimNextAsync(space, Now, TimeSpan.FromMinutes(5), Cancellation))!.WorkspaceId);
    }

    [Fact]
    public async Task Operator_commands_register_and_retire_spaces_with_migration_credentials()
    {
        var (current, old) = (NewSpace(), NewSpace());
        await PgVectorSeed.RegisterAsync(fixture, old);
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeEmbeddingSpaces" SET "LastSeenAt" = now() - interval '30 days' WHERE "Key" = {0}""",
                [old.Key], Cancellation);
        IConfiguration Config(string? migrations, int retention = 7) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Maic"] = "Host=127.0.0.1;Port=1;Username=runtime;Password=runtime-secret;Timeout=2",
            ["ConnectionStrings:MaicMigrations"] = migrations,
            ["Embeddings:Provider"] = "OpenAICompatible", ["Embeddings:SpaceKey"] = current.Key, ["Embeddings:Dimensions"] = "3",
            ["Embeddings:Model"] = "test-model", ["Embeddings:Endpoint"] = "https://e.example.com/v1",
            ["Embeddings:RetiredSpaceRetentionDays"] = retention.ToString()
        }).Build();

        var output = new StringWriter();
        Assert.Equal(0, await EmbeddingSpaceCommands.RunAsync([EmbeddingSpaceCommands.Register], Config(fixture.ConnectionString), output, Cancellation));
        Assert.Equal(0, await EmbeddingSpaceCommands.RunAsync([EmbeddingSpaceCommands.Retire], Config(fixture.ConnectionString), output, Cancellation));
        var text = output.ToString();
        Assert.Contains("Using ConnectionStrings:MaicMigrations.", text);
        Assert.Contains($"Deleted retired embedding space '{old.Key}'", text);
        Assert.DoesNotContain("Password", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fixture.ConnectionString, text);
        Assert.Equal(1, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0}""", current.Key));
        Assert.Equal(0, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0}""", old.Key));

        // Failures print only a message and return 1: no retention, no migration credentials (unless explicitly opted in).
        async Task<(int Code, string Text)> FailAsync(string[] args, IConfiguration config)
        {
            var writer = new StringWriter();
            return (await EmbeddingSpaceCommands.RunAsync(args, config, writer, Cancellation), writer.ToString());
        }
        var noRetention = await FailAsync([EmbeddingSpaceCommands.Retire], Config(fixture.ConnectionString, 0));
        Assert.Equal(1, noRetention.Code);
        Assert.StartsWith("Using ConnectionStrings:MaicMigrations.", noRetention.Text);
        Assert.Contains("Error: Embeddings:RetiredSpaceRetentionDays", noRetention.Text);
        var noMigrations = await FailAsync([EmbeddingSpaceCommands.Register], Config(null));
        Assert.Equal(1, noMigrations.Code);
        Assert.Contains(EmbeddingSpaceCommands.UseRuntimeConnection, noMigrations.Text);
        var unreachable = await FailAsync([EmbeddingSpaceCommands.Register, EmbeddingSpaceCommands.UseRuntimeConnection], Config(null));
        Assert.Equal(1, unreachable.Code); // falls back to the (unreachable) runtime connection only when asked
        Assert.Contains("Using ConnectionStrings:Maic.", unreachable.Text);
        Assert.DoesNotContain("   at ", unreachable.Text);
        Assert.DoesNotContain("runtime-secret", unreachable.Text);
        var disabled = await FailAsync([EmbeddingSpaceCommands.Register], new ConfigurationBuilder().Build());
        Assert.Equal((1, true), (disabled.Code, disabled.Text.Contains("Provider is None")));
        Assert.True(EmbeddingSpaceCommands.IsCommand(["retire-embedding-spaces"]));
        Assert.False(EmbeddingSpaceCommands.IsCommand(["extract-text"]));
    }

    [Fact]
    public async Task Documents_show_their_index_status_in_the_configured_space()
    {
        var space = NewSpace();
        await PgVectorSeed.RegisterAsync(fixture, space);
        var (workspace, _, indexed, _) = await DocumentAsync();
        var failed = Guid.CreateVersion7();
        var waiting = Guid.CreateVersion7();
        var user = await PgVectorSeed.UserAsync(fixture);
        foreach (var document in new[] { failed, waiting })
            await PgVectorSeed.KeysAsync(fixture, user, [new KnowledgeVectorKey(workspace, Guid.CreateVersion7(), document, Guid.CreateVersion7(), 1)]);
        await DiscoverOnlyAsync(space, workspace);
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "KnowledgeIndexStates" SET "Status" = 'INDEXED', "IndexedChunks" = 2, "RejectedChunks" = 1 WHERE "SpaceKey" = {0} AND "DocumentId" = {1};
            UPDATE "KnowledgeIndexStates" SET "Status" = 'FAILED', "Failure" = 'EMBEDDING_REJECTED', "RejectedChunks" = 1 WHERE "SpaceKey" = {0} AND "DocumentId" = {2};
            UPDATE "KnowledgeIndexStates" SET "Status" = 'RETRY', "LastFailure" = 'MISCONFIGURED' WHERE "SpaceKey" = {0} AND "DocumentId" = {3};
            """, [space.Key, indexed, failed, waiting], Cancellation);
        var processed = await db.KnowledgeDocuments.Where(d => d.WorkspaceId == workspace).ToDictionaryAsync(d => d.Id, d => d.ProcessedAt!.Value, Cancellation);
        var settings = new EmbeddingSettings
        {
            Provider = EmbeddingProvider.OpenAICompatible, SpaceKey = space.Key, Dimensions = 3, Model = "test-model", Endpoint = "https://e.example.com/v1"
        };
        var status = await new KnowledgeIndexStatusReader(db, settings).GetAsync(workspace,
            processed.Select(p => (p.Key, p.Value)).ToArray(), Cancellation);
        Assert.Equal(new KnowledgeIndexStatusView("INDEXED", 2, 1, null), status[indexed]);
        Assert.Equal(new KnowledgeIndexStatusView("FAILED", 0, 1, "EMBEDDING_REJECTED"), status[failed]);
        Assert.Equal(new KnowledgeIndexStatusView("PENDING", 0, 0, null), status[waiting]); // no provider fault code for tenants

        // Another workspace's document IDs never return that workspace's state.
        var (otherWorkspace, _, _, _) = await DocumentAsync();
        var foreign = await new KnowledgeIndexStatusReader(db, settings).GetAsync(otherWorkspace, [(indexed, processed[indexed])], Cancellation);
        Assert.Equal("PENDING", foreign[indexed].Status);

        // Disabled source.
        var sourceOfIndexed = await db.KnowledgeDocuments.Where(d => d.Id == indexed).Select(d => d.KnowledgeSourceId).SingleAsync(Cancellation);
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = false WHERE "Id" = {0}""", [sourceOfIndexed], Cancellation);
        Assert.Equal("SOURCE_DISABLED", (await new KnowledgeIndexStatusReader(db, settings).GetAsync(workspace, [(indexed, processed[indexed])], Cancellation))[indexed].Status);
        await db.Database.ExecuteSqlRawAsync("""UPDATE "KnowledgeSources" SET "Enabled" = true WHERE "Id" = {0}""", [sourceOfIndexed], Cancellation);

        // Content processed again after indexing: pending until indexed again. No provider: disabled.
        var stale = await new KnowledgeIndexStatusReader(db, settings).GetAsync(workspace, [(indexed, processed[indexed].AddSeconds(1))], Cancellation);
        Assert.Equal("PENDING", stale[indexed].Status);
        Assert.Equal("DISABLED", (await new KnowledgeIndexStatusReader(db, new EmbeddingSettings())
            .GetAsync(workspace, [(indexed, processed[indexed])], Cancellation))[indexed].Status);
    }

    [Fact]
    public async Task Spaces_no_host_uses_anymore_are_deleted_with_their_vectors_and_index()
    {
        var (current, retired, recent) = (NewSpace(), NewSpace(), NewSpace());
        foreach (var space in new[] { current, retired, recent }) await PgVectorSeed.RegisterAsync(fixture, space);
        var (workspace, _, document, keys) = await DocumentAsync(1);
        await using var db = fixture.CreateContext();
        await new PgVectorKnowledgeStore(db).ReplaceDocumentAsync(retired, workspace, document, [new KnowledgeVector(keys[0], new float[] { 1, 0, 0 })], Cancellation);
        await db.Database.ExecuteSqlRawAsync("""
            UPDATE "KnowledgeEmbeddingSpaces" SET "LastSeenAt" = now() - interval '30 days' WHERE "Key" IN ({0}, {1})
            """, [retired.Key, current.Key], Cancellation);

        var registry = new EmbeddingSpaceRegistry(db);
        var deleted = (await registry.DeleteRetiredAsync(current, 7, manageSchema: true, Cancellation)).ToDictionary(r => r.Key, r => r.VectorsDeleted);
        Assert.Equal(1, deleted[retired.Key]);
        Assert.DoesNotContain(current.Key, deleted.Keys); // the configured space is never retired, however old
        Assert.DoesNotContain(recent.Key, deleted.Keys);
        Assert.Equal(0, await PgVectorSeed.CountVectorsAsync(fixture, "\"SpaceKey\" = {0}", retired.Key));
        Assert.Equal(0, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM pg_indexes WHERE indexname = {0}""", PgSql.IndexName(retired)));
        Assert.Equal(1, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM pg_indexes WHERE indexname = {0}""", PgSql.IndexName(current)));

        // Using a space refreshes it.
        await registry.TouchAsync(current, Cancellation);
        Assert.Equal(1, await ScalarAsync("""
            SELECT count(*)::int AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0} AND "LastSeenAt" > now() - interval '1 minute'
            """, current.Key));
    }

    [Fact]
    public async Task Without_schema_management_hosts_only_verify_what_the_register_command_created()
    {
        var space = NewSpace();
        await using var db = fixture.CreateContext();
        var registry = new EmbeddingSpaceRegistry(db);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() => registry.EnsureAsync(space, "m", Cancellation, manageSchema: false));
        Assert.Contains(EmbeddingSpaceRegistry.RegisterCommand, missing.Message);
        Assert.Equal(0, await ScalarAsync("""SELECT count(*)::int AS "Value" FROM "KnowledgeEmbeddingSpaces" WHERE "Key" = {0}""", space.Key));

        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: true); // what the operator command runs
        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: false);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.EnsureAsync(space, "other", Cancellation, manageSchema: false));

        // A leftover index of a retired space that reused the key with other dimensions is detected and rebuilt.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"" + PgSql.IndexName(space) + "\"", Cancellation);
        await db.Database.ExecuteSqlRawAsync("CREATE INDEX \"" + PgSql.IndexName(space) + "\" ON \"KnowledgeChunkVectors\" USING hnsw ((\"Embedding\"::vector(5)) vector_cosine_ops) WHERE \"SpaceKey\" = '" + space.Key + "'", Cancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.EnsureAsync(space, "m", Cancellation, manageSchema: false));
        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: true);
        Assert.Contains("vector(3)", await db.Database.SqlQueryRaw<string>("""SELECT indexdef AS "Value" FROM pg_indexes WHERE indexname = {0}""",
            PgSql.IndexName(space)).SingleAsync(Cancellation));
        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: false);

        // A dropped (or invalid) index is detected instead of silently searching without it.
        await db.Database.ExecuteSqlRawAsync("DROP INDEX \"" + PgSql.IndexName(space) + "\"", Cancellation);
        await Assert.ThrowsAsync<InvalidOperationException>(() => registry.EnsureAsync(space, "m", Cancellation, manageSchema: false));
        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: true);
        await registry.EnsureAsync(space, "m", Cancellation, manageSchema: false);
    }
}
