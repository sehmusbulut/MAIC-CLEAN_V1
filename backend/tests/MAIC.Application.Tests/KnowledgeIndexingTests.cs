using MAIC.Application.AI;
using MAIC.Domain.Knowledge;
using Xunit;

namespace MAIC.Application.Tests;

/// <summary>Index modes (isolating rejected chunks) and the indexing processor's failure policy.</summary>
public sealed class KnowledgeIndexingTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly EmbeddingSpace Space = new("test-v1", 3);
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Source = Guid.CreateVersion7();
    private static readonly Guid Document = Guid.CreateVersion7();

    /// <summary>Embeds each input as [length, 1, 0] unless <see cref="Fail"/> returns a failure for the batch.</summary>
    private sealed class ScriptedEmbeddings : IEmbeddingGenerator
    {
        public Func<IReadOnlyList<string>, EmbeddingException?> Fail { get; set; } = _ => null;
        public List<IReadOnlyList<string>> Calls { get; } = [];
        public EmbeddingSpace Space => KnowledgeIndexingTests.Space;
        public int MaxBatchSize { get; init; } = 4;
        public int MaxInputLength => KnowledgeChunker.MaxChunkLength;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add(inputs);
            if (Fail(inputs) is { } error) throw error;
            return Task.FromResult<IReadOnlyList<ReadOnlyMemory<float>>>(
                inputs.Select(text => (ReadOnlyMemory<float>)new float[] { text.Length, 1, 0 }).ToArray());
        }
    }

    private sealed class RecordingStore : IKnowledgeVectorStore
    {
        public List<IReadOnlyList<KnowledgeVector>> Replacements { get; } = [];
        public Task ReplaceDocumentAsync(EmbeddingSpace s, Guid w, Guid d, IReadOnlyList<KnowledgeVector> v, CancellationToken ct)
        {
            Replacements.Add(v);
            return Task.CompletedTask;
        }
        public async Task<bool> ReplaceDocumentIfLeasedAsync(EmbeddingSpace s, Guid w, Guid d, IReadOnlyList<KnowledgeVector> v, Guid lease,
            CancellationToken ct)
        {
            if (!LeaseHeld) return false;
            await ReplaceDocumentAsync(s, w, d, v, ct);
            return true;
        }
        public bool LeaseHeld { get; set; } = true;

        public Task DeleteDocumentsAsync(Guid w, IReadOnlyCollection<Guid> d, CancellationToken ct) => Task.CompletedTask;
        public Task DeleteSourcesAsync(Guid w, IReadOnlyCollection<Guid> s, CancellationToken ct) => Task.CompletedTask;
        public Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest r, CancellationToken ct) => Task.FromResult<IReadOnlyList<VectorMatch>>([]);
    }

    private static KnowledgeChunkText[] Chunks(params string[] texts) => texts.Select((text, i) =>
        new KnowledgeChunkText(new KnowledgeVectorKey(Workspace, Source, Document, Guid.CreateVersion7(), i + 1), text)).ToArray();

    private static EmbeddingException Rejected() => new(EmbeddingFailure.REJECTED_INPUT);

    [Fact]
    public async Task Skip_rejected_isolates_the_offending_chunks_by_splitting_and_stores_the_rest()
    {
        var embeddings = new ScriptedEmbeddings { Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? Rejected() : null };
        var store = new RecordingStore();
        var chunks = Chunks("ok one", "bad two", "ok three", "ok four", "bad five", "ok six");
        var rejected = await new KnowledgeVectorIndex(embeddings, store)
            .IndexDocumentAsync(Workspace, Document, chunks, KnowledgeIndexMode.SkipRejected, Cancellation);

        Assert.Equal([chunks[1].Key, chunks[4].Key], rejected);
        Assert.Equal(chunks.Where(c => c.Text.StartsWith("ok")).Select(c => c.Key), store.Replacements.Single().Select(v => v.Key).OrderBy(k => k.ChunkSequence));
        Assert.All(embeddings.Calls, call => Assert.True(call.Count <= 4));

        // Strict mode fails the whole document on the same content.
        await Assert.ThrowsAsync<EmbeddingException>(() => new KnowledgeVectorIndex(embeddings, store)
            .IndexDocumentAsync(Workspace, Document, chunks, Cancellation));
    }

    [Theory]
    [InlineData(EmbeddingFailure.UNAVAILABLE)]
    [InlineData(EmbeddingFailure.MISCONFIGURED)]
    [InlineData(EmbeddingFailure.INVALID_RESPONSE)]
    public async Task Skip_rejected_never_blames_content_for_provider_faults(EmbeddingFailure failure)
    {
        var embeddings = new ScriptedEmbeddings { Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? new EmbeddingException(failure) : null };
        var store = new RecordingStore();
        var error = await Assert.ThrowsAsync<EmbeddingException>(() => new KnowledgeVectorIndex(embeddings, store)
            .IndexDocumentAsync(Workspace, Document, Chunks("ok", "bad"), KnowledgeIndexMode.SkipRejected, Cancellation));
        Assert.Equal(failure, error.Failure);
        Assert.Empty(store.Replacements);
    }

    [Fact]
    public async Task Isolating_suspects_blames_a_chunk_only_when_the_provider_works_and_the_chunk_fails_again()
    {
        // A gateway refusing one chunk's text with 403 (MISCONFIGURED) while the provider works.
        var embeddings = new ScriptedEmbeddings { Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? new EmbeddingException(EmbeddingFailure.MISCONFIGURED) : null };
        var store = new RecordingStore();
        var chunks = Chunks("ok one", "ok two", "bad three");
        var rejected = await new KnowledgeVectorIndex(embeddings, store)
            .IndexDocumentAsync(Workspace, Document, chunks, KnowledgeIndexMode.IsolateSuspects, Cancellation);
        Assert.Equal([chunks[2].Key], rejected);
        Assert.Equal([["ok one"], ["bad three"]], embeddings.Calls.TakeLast(2)); // provider check, then the suspect alone again
        Assert.Equal(2, store.Replacements.Single().Count);

        // A broken provider refuses everything, including the benign check: nothing is blamed and nothing is stored.
        var broken = new ScriptedEmbeddings { Fail = _ => new EmbeddingException(EmbeddingFailure.MISCONFIGURED) };
        var untouched = new RecordingStore();
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, (await Assert.ThrowsAsync<EmbeddingException>(() => new KnowledgeVectorIndex(broken, untouched)
            .IndexDocumentAsync(Workspace, Document, Chunks("ok", "ok too"), KnowledgeIndexMode.IsolateSuspects, Cancellation))).Failure);
        Assert.Empty(untouched.Replacements);
        Assert.Equal([KnowledgeVectorIndex.HealthCheckText], broken.Calls.Last());

        // The provider was briefly broken mid-run (key revoked and restored): the suspects succeed on their second try.
        var calls = 0;
        var flapping = new ScriptedEmbeddings { MaxBatchSize = 1, Fail = _ => ++calls is 2 or 3 ? new EmbeddingException(EmbeddingFailure.MISCONFIGURED) : null };
        var all = new RecordingStore();
        Assert.Empty(await new KnowledgeVectorIndex(flapping, all)
            .IndexDocumentAsync(Workspace, Document, Chunks("a", "b", "c"), KnowledgeIndexMode.IsolateSuspects, Cancellation));
        Assert.Equal(3, all.Replacements.Single().Count);
    }

    [Fact]
    public async Task A_provider_that_starts_refusing_everything_blames_no_content()
    {
        // After the adapter's probe passed, the provider starts answering 400 to every request (a regression).
        var regressed = new ScriptedEmbeddings { Fail = _ => Rejected() };
        var store = new RecordingStore();
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, (await Assert.ThrowsAsync<EmbeddingException>(() => new KnowledgeVectorIndex(regressed, store)
            .IndexDocumentAsync(Workspace, Document, Chunks("fine", "also fine"), KnowledgeIndexMode.SkipRejected, Cancellation))).Failure);
        Assert.Empty(store.Replacements);
    }

    private sealed class FakeIndexStore(params KnowledgeChunkText[] chunks) : IKnowledgeIndexStore
    {
        public KnowledgeIndexWork Work { get; set; } = new(Workspace, Document, Guid.NewGuid(), 1, 0);
        public List<string> Transitions { get; } = [];
        public DateTimeOffset? NextAttemptAt { get; private set; }
        public KnowledgeIndexFailure? LastFailure { get; private set; }
        public bool Claimable { get; set; } = true;

        public Task<int> DiscoverAsync(EmbeddingSpace s, DateTimeOffset now, int limit, CancellationToken ct) => Task.FromResult(0);
        public Task<KnowledgeIndexWork?> ClaimNextAsync(EmbeddingSpace s, DateTimeOffset now, TimeSpan lease, CancellationToken ct) =>
            Task.FromResult(Claimable ? Work : null);
        public Task<IReadOnlyList<KnowledgeChunkText>> LoadChunksAsync(KnowledgeIndexWork w, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<KnowledgeChunkText>>(chunks);
        public bool Budget { get; set; } = true;
        public Task<bool> HasIndexingBudgetAsync(EmbeddingSpace s, DateTimeOffset now, CancellationToken ct) => Task.FromResult(Budget);
        public int Charged { get; private set; }
        public Task ChargeAsync(EmbeddingSpace s, KnowledgeIndexWork w, int count, DateTimeOffset now, CancellationToken ct)
        {
            Charged += count;
            return Task.CompletedTask;
        }
        public Task<bool> CompleteAsync(EmbeddingSpace s, KnowledgeIndexWork w, int indexed, int rejected, DateTimeOffset now, CancellationToken ct) =>
            Record($"indexed {indexed}/{rejected}");
        public Task<bool> RejectAsync(EmbeddingSpace s, KnowledgeIndexWork w, int rejected, DateTimeOffset now, CancellationToken ct) =>
            Record($"rejected {rejected}");
        public Task<bool> RetryAsync(EmbeddingSpace s, KnowledgeIndexWork w, KnowledgeIndexFailure? failure, DateTimeOffset next, DateTimeOffset now,
            CancellationToken ct)
        {
            (NextAttemptAt, LastFailure) = (next, failure);
            return Record($"retry {failure}");
        }
        public Task<bool> ReleaseAsync(EmbeddingSpace s, KnowledgeIndexWork w, DateTimeOffset now, CancellationToken ct) => Record("released");

        private Task<bool> Record(string transition)
        {
            Transitions.Add(transition);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now = new(2026, 9, 27, 12, 0, 0, TimeSpan.Zero);

    private static KnowledgeIndexProcessor Processor(FakeIndexStore store, IEmbeddingGenerator embeddings) =>
        new(store, new KnowledgeVectorIndex(embeddings, new RecordingStore()), new FixedClock(Now));

    [Fact]
    public async Task The_processor_indexes_skips_rejected_chunks_and_fails_only_fully_rejected_documents()
    {
        var store = new FakeIndexStore(Chunks("ok", "bad"));
        var embeddings = new ScriptedEmbeddings { Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? Rejected() : null };
        var outcome = await Processor(store, embeddings).ProcessNextAsync(Cancellation);
        Assert.Equal((KnowledgeIndexOutcomeKind.Indexed, 1, 1), (outcome!.Kind, outcome.IndexedChunks, outcome.RejectedChunks));
        Assert.Equal(["indexed 1/1"], store.Transitions);

        var all = new FakeIndexStore(Chunks("bad a", "bad b"));
        Assert.Equal(KnowledgeIndexOutcomeKind.Failed, (await Processor(all, embeddings).ProcessNextAsync(Cancellation))!.Kind);
        Assert.Equal(["rejected 2"], all.Transitions);

        Assert.Null(await Processor(new FakeIndexStore { Claimable = false }, embeddings).ProcessNextAsync(Cancellation));
        var empty = new FakeIndexStore();
        Assert.Equal(KnowledgeIndexOutcomeKind.NotReady, (await Processor(empty, embeddings).ProcessNextAsync(Cancellation))!.Kind);
        Assert.Equal((Now + KnowledgeIndexProcessor.NotReadyDelay, (KnowledgeIndexFailure?)null), (empty.NextAttemptAt, empty.LastFailure));
    }

    [Theory]
    [InlineData(EmbeddingFailure.UNAVAILABLE, KnowledgeIndexFailure.UNAVAILABLE)]
    [InlineData(EmbeddingFailure.MISCONFIGURED, KnowledgeIndexFailure.MISCONFIGURED)]
    [InlineData(EmbeddingFailure.INVALID_RESPONSE, KnowledgeIndexFailure.INVALID_RESPONSE)]
    public async Task Provider_faults_are_retried_with_backoff_and_never_fail_the_document(EmbeddingFailure failure, KnowledgeIndexFailure expected)
    {
        var store = new FakeIndexStore(Chunks("text")) { Work = new(Workspace, Document, Guid.NewGuid(), 3, 0) };
        var outcome = await Processor(store, new ScriptedEmbeddings { Fail = _ => new EmbeddingException(failure) }).ProcessNextAsync(Cancellation);
        Assert.Equal((KnowledgeIndexOutcomeKind.Retrying, (KnowledgeIndexFailure?)expected), (outcome!.Kind, outcome.Failure));
        Assert.Equal(Now + TimeSpan.FromMinutes(2), store.NextAttemptAt); // attempt 3: 30 s x 4
        Assert.Equal(expected, store.LastFailure);
    }

    [Fact]
    public async Task The_processor_charges_usage_honours_the_budget_and_reports_a_lost_lease()
    {
        var store = new FakeIndexStore(Chunks("a", "b", "c"));
        var clock = new FixedClock(Now);
        var vectors = new RecordingStore();
        var processor = new KnowledgeIndexProcessor(store, new KnowledgeVectorIndex(new ScriptedEmbeddings(), vectors), clock);
        Assert.Equal(KnowledgeIndexOutcomeKind.Indexed, (await processor.ProcessNextAsync(Cancellation))!.Kind);
        Assert.Equal(3, store.Charged);
        store.Budget = false;
        store.Transitions.Clear();
        Assert.Null(await processor.ProcessNextAsync(Cancellation)); // shared budget used up: nothing is claimed
        Assert.Empty(store.Transitions);

        var lost = new FakeIndexStore(Chunks("a"));
        var fenced = new RecordingStore { LeaseHeld = false };
        var outcome = await new KnowledgeIndexProcessor(lost, new KnowledgeVectorIndex(new ScriptedEmbeddings(), fenced), clock).ProcessNextAsync(Cancellation);
        Assert.Equal(KnowledgeIndexOutcomeKind.LeaseLost, outcome!.Kind);
        Assert.Empty(fenced.Replacements);
        Assert.Empty(lost.Transitions); // a stale worker records nothing either
    }

    [Fact]
    public async Task Backoff_doubles_is_capped_at_an_hour_and_honours_retry_after_up_to_the_cap()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), KnowledgeIndexProcessor.Backoff(1, null));
        Assert.Equal(TimeSpan.FromSeconds(60), KnowledgeIndexProcessor.Backoff(2, null));
        Assert.Equal(TimeSpan.FromHours(1), KnowledgeIndexProcessor.Backoff(50, null));
        Assert.Equal(TimeSpan.FromMinutes(10), KnowledgeIndexProcessor.Backoff(1, TimeSpan.FromMinutes(10)));
        Assert.Equal(TimeSpan.FromHours(1), KnowledgeIndexProcessor.Backoff(1, TimeSpan.FromHours(5)));
        Assert.Equal(TimeSpan.FromMinutes(2), KnowledgeIndexProcessor.Backoff(3, TimeSpan.FromSeconds(5)));

        // The provider's Retry-After reaches the stored next attempt.
        var store = new FakeIndexStore(Chunks("text"));
        await Processor(store, new ScriptedEmbeddings { Fail = _ => new EmbeddingException(EmbeddingFailure.UNAVAILABLE, retryAfter: TimeSpan.FromMinutes(7)) })
            .ProcessNextAsync(Cancellation);
        Assert.Equal(Now + TimeSpan.FromMinutes(7), store.NextAttemptAt);
    }

    [Fact]
    public async Task Repeated_misconfiguration_switches_to_isolating_suspect_chunks()
    {
        var embeddings = new ScriptedEmbeddings { Fail = inputs => inputs.Any(i => i.StartsWith("bad")) ? new EmbeddingException(EmbeddingFailure.MISCONFIGURED) : null };
        var fresh = new FakeIndexStore(Chunks("ok", "bad")) { Work = new(Workspace, Document, Guid.NewGuid(), 1, KnowledgeIndexProcessor.SuspectAfterStrikes - 1) };
        Assert.Equal(KnowledgeIndexOutcomeKind.Retrying, (await Processor(fresh, embeddings).ProcessNextAsync(Cancellation))!.Kind);

        var struck = new FakeIndexStore(Chunks("ok", "bad")) { Work = new(Workspace, Document, Guid.NewGuid(), 4, KnowledgeIndexProcessor.SuspectAfterStrikes) };
        var outcome = await Processor(struck, embeddings).ProcessNextAsync(Cancellation);
        Assert.Equal((KnowledgeIndexOutcomeKind.Indexed, 1, 1), (outcome!.Kind, outcome.IndexedChunks, outcome.RejectedChunks));
    }

    [Fact]
    public async Task A_slow_provider_hits_the_document_deadline_and_shutdown_releases_the_claim()
    {
        var slow = new SlowEmbeddings();
        var store = new FakeIndexStore(Chunks("text"));
        var processor = new KnowledgeIndexProcessor(store, new KnowledgeVectorIndex(slow, new RecordingStore()), new FixedClock(Now))
        {
            DocumentTimeout = TimeSpan.FromMilliseconds(50)
        };
        Assert.Equal((KnowledgeIndexOutcomeKind.Retrying, (KnowledgeIndexFailure?)KnowledgeIndexFailure.UNAVAILABLE),
            ((await processor.ProcessNextAsync(Cancellation))!.Kind, store.LastFailure));

        var stopping = new FakeIndexStore(Chunks("text"));
        using var shutdown = new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new KnowledgeIndexProcessor(stopping, new KnowledgeVectorIndex(slow, new RecordingStore()), new FixedClock(Now)).ProcessNextAsync(shutdown.Token));
        Assert.Equal(["released"], stopping.Transitions);
    }

    private sealed class SlowEmbeddings : IEmbeddingGenerator
    {
        public EmbeddingSpace Space => KnowledgeIndexingTests.Space;
        public int MaxBatchSize => 4;
        public int MaxInputLength => KnowledgeChunker.MaxChunkLength;
        public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), ct);
            return [];
        }
    }
}

