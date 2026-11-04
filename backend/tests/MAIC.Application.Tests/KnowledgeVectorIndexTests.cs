using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Domain.Knowledge;
using Xunit;

namespace MAIC.Application.Tests;

public sealed class KnowledgeVectorIndexTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private static readonly EmbeddingSpace Space = new("test-v1", 3);
    private static readonly Guid Workspace = Guid.CreateVersion7();
    private static readonly Guid Document = Guid.CreateVersion7();

    private sealed class FakeEmbeddings(Func<IReadOnlyList<string>, IReadOnlyList<ReadOnlyMemory<float>>>? respond = null) : IEmbeddingGenerator
    {
        public List<(IReadOnlyList<string> Inputs, EmbeddingPurpose Purpose)> Calls { get; } = [];
        public EmbeddingSpace Space => KnowledgeVectorIndexTests.Space;
        public int MaxBatchSize { get; init; } = 2;
        public int MaxInputLength { get; init; } = KnowledgeChunker.MaxChunkLength;

        public Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Calls.Add((inputs, purpose));
            return Task.FromResult(respond?.Invoke(inputs)
                ?? inputs.Select(text => (ReadOnlyMemory<float>)new float[] { text.Length, 1, 0 }).ToArray());
        }
    }

    private sealed class FakeStore(IReadOnlyList<VectorMatch>? matches = null) : IKnowledgeVectorStore
    {
        public List<(Guid Workspace, Guid Document, IReadOnlyList<KnowledgeVector> Vectors)> Replacements { get; } = [];
        public List<VectorSearchRequest> Searches { get; } = [];
        public List<(string Kind, IReadOnlyCollection<Guid> Ids)> Deletes { get; } = [];

        public Task ReplaceDocumentAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> vectors, CancellationToken ct)
        {
            Replacements.Add((workspaceId, documentId, vectors));
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

        public Task DeleteDocumentsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken ct)
        {
            Deletes.Add(("documents", documentIds));
            return Task.CompletedTask;
        }

        public Task DeleteSourcesAsync(Guid workspaceId, IReadOnlyCollection<Guid> sourceIds, CancellationToken ct)
        {
            Deletes.Add(("sources", sourceIds));
            return Task.CompletedTask;
        }

        public Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken ct)
        {
            Searches.Add(request);
            return Task.FromResult(matches ?? []);
        }
    }

    private static KnowledgeVectorKey Key(Guid? workspace = null, Guid? source = null, int sequence = 1, Guid? document = null) =>
        new(workspace ?? Workspace, source ?? Guid.CreateVersion7(), document ?? Document, Guid.CreateVersion7(), sequence);

    [Fact]
    public async Task Indexing_batches_by_provider_limits_and_replaces_the_documents_vectors_atomically()
    {
        var embeddings = new FakeEmbeddings();
        var store = new FakeStore();
        var chunks = Enumerable.Range(1, 5).Select(i => new KnowledgeChunkText(Key(sequence: i), new string('x', i))).ToArray();
        var index = new KnowledgeVectorIndex(embeddings, store);
        await index.IndexDocumentAsync(Workspace, Document, chunks, Cancellation);

        Assert.Equal([2, 2, 1], embeddings.Calls.Select(c => c.Inputs.Count));
        Assert.All(embeddings.Calls, c => Assert.Equal(EmbeddingPurpose.Document, c.Purpose));
        var (workspace, document, vectors) = Assert.Single(store.Replacements);
        Assert.Equal((Workspace, Document), (workspace, document));
        Assert.Equal(chunks.Select(c => c.Key), vectors.Select(v => v.Key));
        Assert.Equal(Enumerable.Range(1, 5).Select(i => (float)i), vectors.Select(v => v.Vector.Span[0]));

        // A document without chunks replaces its vectors with nothing.
        await index.IndexDocumentAsync(Workspace, Document, [], Cancellation);
        Assert.Empty(store.Replacements[1].Vectors);
    }

    [Fact]
    public async Task Indexing_rejects_foreign_or_incomplete_metadata_and_oversized_input_before_calling_anything()
    {
        var embeddings = new FakeEmbeddings();
        var store = new FakeStore();
        var index = new KnowledgeVectorIndex(embeddings, store);
        var duplicate = Key();
        foreach (var chunks in new[]
                 {
                     new[] { new KnowledgeChunkText(Key(Guid.CreateVersion7()), "foreign workspace") },
                     [new KnowledgeChunkText(Key(document: Guid.CreateVersion7()), "other document")],
                     [new KnowledgeChunkText(Key() with { KnowledgeSourceId = Guid.Empty }, "incomplete")],
                     [new KnowledgeChunkText(Key() with { ChunkSequence = 0 }, "sequence")],
                     [new KnowledgeChunkText(duplicate, "a"), new KnowledgeChunkText(duplicate, "b")]
                 })
            await Assert.ThrowsAsync<ArgumentException>(() => index.IndexDocumentAsync(Workspace, Document, chunks, Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => index.IndexDocumentAsync(Guid.Empty, Document, [], Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => index.IndexDocumentAsync(Workspace, Guid.Empty, [], Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.IndexDocumentAsync(Workspace, Document,
            Enumerable.Range(1, KnowledgeVectorIndex.MaxChunksPerDocument + 1).Select(i => new KnowledgeChunkText(Key(sequence: i), "x")).ToArray(), Cancellation));
        var tooLong = await Assert.ThrowsAsync<EmbeddingException>(() => index.IndexDocumentAsync(Workspace, Document,
            [new KnowledgeChunkText(Key(), new string('x', KnowledgeChunker.MaxChunkLength + 1))], Cancellation));
        Assert.Equal((EmbeddingFailure.REJECTED_INPUT, false), (tooLong.Failure, tooLong.Transient));
        Assert.Empty(embeddings.Calls);
        Assert.Empty(store.Replacements);
    }

    [Theory]
    [InlineData("count")]
    [InlineData("dimensions")]
    [InlineData("nan")]
    [InlineData("infinity")]
    [InlineData("zero")]
    [InlineData("underflow")]
    public async Task A_malformed_provider_response_is_rejected_and_nothing_is_stored(string defect)
    {
        var embeddings = new FakeEmbeddings(inputs => defect switch
        {
            "count" => [new float[] { 1, 1, 1 }],
            "dimensions" => inputs.Select(_ => (ReadOnlyMemory<float>)new float[] { 1, 1 }).ToArray(),
            "nan" => inputs.Select(_ => (ReadOnlyMemory<float>)new[] { float.NaN, 1, 1 }).ToArray(),
            "infinity" => inputs.Select(_ => (ReadOnlyMemory<float>)new[] { float.PositiveInfinity, 1, 1 }).ToArray(),
            "underflow" => inputs.Select(_ => (ReadOnlyMemory<float>)new[] { float.Epsilon, 0, 0 }).ToArray(),
            _ => inputs.Select(_ => (ReadOnlyMemory<float>)new float[] { 0, 0, 0 }).ToArray()
        });
        var store = new FakeStore();
        var error = await Assert.ThrowsAsync<EmbeddingException>(() => new KnowledgeVectorIndex(embeddings, store)
            .IndexDocumentAsync(Workspace, Document, [new KnowledgeChunkText(Key(), "a"), new KnowledgeChunkText(Key(), "b")], Cancellation));
        Assert.Equal(EmbeddingFailure.INVALID_RESPONSE, error.Failure);
        Assert.Empty(store.Replacements);
    }

    [Theory]
    [InlineData(0, 2000, true)]
    [InlineData(2, 0, true)]
    [InlineData(2, 1999, true)] // cannot hold a whole chunk: indexing would fail every document
    [InlineData(2, 1999, false)] // search does not need chunk capacity
    public async Task Broken_adapter_limits_are_configuration_errors(int batch, int input, bool indexing)
    {
        var index = new KnowledgeVectorIndex(new FakeEmbeddings { MaxBatchSize = batch, MaxInputLength = input }, new FakeStore());
        if (indexing)
            Assert.Equal(EmbeddingFailure.MISCONFIGURED, (await Assert.ThrowsAsync<EmbeddingException>(() =>
                index.IndexDocumentAsync(Workspace, Document, [new KnowledgeChunkText(Key(), "x")], Cancellation))).Failure);
        else
            Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "question", 5, Cancellation));
        if (batch == 0 || input == 0)
            Assert.Equal(EmbeddingFailure.MISCONFIGURED, (await Assert.ThrowsAsync<EmbeddingException>(() =>
                index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "question", 5, Cancellation))).Failure);
    }

    [Fact]
    public async Task Search_keeps_only_candidates_of_the_requested_workspace_and_scope_best_first()
    {
        var allowed = Guid.CreateVersion7();
        var excluded = Guid.CreateVersion7();
        var good = Key(source: allowed);
        var matches = new VectorMatch[]
        {
            new(Key(Guid.CreateVersion7(), allowed), 0.99), // another workspace: dropped
            new(Key(source: excluded), 0.98), // outside scope: dropped
            new(good, 0.5), new(good, 0.5), // duplicate chunk: once
            new(Key(source: allowed), 0.9), new(Key(source: allowed), double.NaN), // non-finite score: dropped
            new(Key(source: allowed), 1e9), new(Key(source: allowed), -3), // outside [-1, 1]: dropped
            new(Key(source: allowed) with { ChunkId = Guid.Empty }, 0.97) // incomplete metadata: dropped
        };
        var embeddings = new FakeEmbeddings();
        var store = new FakeStore(matches);
        var results = await new KnowledgeVectorIndex(embeddings, store)
            .SearchAsync(Workspace, KnowledgeScopeFilter.Selected([allowed]), "return policy", 5, Cancellation);

        Assert.Equal([0.9, 0.5], results.Select(r => r.Score));
        Assert.All(results, r => Assert.Equal((Workspace, allowed), (r.Key.WorkspaceId, r.Key.KnowledgeSourceId)));
        var request = Assert.Single(store.Searches);
        Assert.Equal((Workspace, Space, 5, false), (request.WorkspaceId, request.Space, request.TopK, request.Scope.AllSources));
        Assert.Equal([allowed], request.Scope.SourceIds);
        Assert.Equal(EmbeddingPurpose.Query, Assert.Single(embeddings.Calls).Purpose);
    }

    [Fact]
    public async Task Excluded_sources_reach_the_store_and_are_dropped_from_results()
    {
        var (enabled, disabled) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var store = new FakeStore([new(Key(source: disabled), 0.9), new(Key(source: enabled), 0.8)]);
        var results = await new KnowledgeVectorIndex(new FakeEmbeddings(), store)
            .SearchAsync(Workspace, KnowledgeScopeFilter.All.Excluding([disabled]), "q", 5, Cancellation);
        Assert.Equal([enabled], results.Select(r => r.Key.KnowledgeSourceId));
        var scope = Assert.Single(store.Searches).Scope;
        Assert.True(scope.AllSources);
        Assert.Equal([disabled], scope.ExcludedSourceIds);
    }

    [Fact]
    public async Task Empty_scopes_and_blank_queries_return_no_knowledge_without_calling_the_provider_or_store()
    {
        var embeddings = new FakeEmbeddings { MaxInputLength = 4 };
        var store = new FakeStore([new(Key(), 1)]);
        var index = new KnowledgeVectorIndex(embeddings, store);
        var only = Guid.CreateVersion7();
        Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.None, "question", 5, Cancellation));
        Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.For(AiKnowledgeScope.SELECTED_SOURCES, []), "question", 5, Cancellation));
        Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.Selected([only]).Excluding([only]), "question", 5, Cancellation));
        Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.All, " \n", 5, Cancellation));
        Assert.Empty(await index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "    question", 5, Cancellation)); // blank once truncated
        Assert.Empty(embeddings.Calls);
        Assert.Empty(store.Searches);
        await Assert.ThrowsAsync<ArgumentException>(() => index.SearchAsync(Guid.Empty, KnowledgeScopeFilter.All, "q", 5, Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "q", 0, Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "q", VectorSearchRequest.MaxTopK + 1, Cancellation));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => index.SearchAsync(Workspace, KnowledgeScopeFilter.All, "q", 5, cancelled.Token));
    }

    [Fact]
    public async Task Long_queries_are_truncated_to_the_provider_limit_without_splitting_surrogate_pairs()
    {
        var embeddings = new FakeEmbeddings { MaxInputLength = 20 };
        var index = new KnowledgeVectorIndex(embeddings, new FakeStore());
        await index.SearchAsync(Workspace, KnowledgeScopeFilter.All, new string('a', 19) + "\ud83d\ude42tail", 5, Cancellation);
        Assert.Equal(new string('a', 19), Assert.Single(Assert.Single(embeddings.Calls).Inputs));
    }

    [Fact]
    public async Task Removing_documents_and_sources_is_scoped_bounded_and_skips_empty_lists()
    {
        var store = new FakeStore();
        var index = new KnowledgeVectorIndex(new FakeEmbeddings(), store);
        var (document, source) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await index.RemoveDocumentsAsync(Workspace, [document], Cancellation);
        await index.RemoveSourcesAsync(Workspace, [source], Cancellation);
        await index.RemoveDocumentsAsync(Workspace, [], Cancellation);
        await index.RemoveSourcesAsync(Workspace, [], Cancellation);
        Assert.Equal([("documents", document), ("sources", source)], store.Deletes.Select(d => (d.Kind, d.Ids.Single())));
        await Assert.ThrowsAsync<ArgumentException>(() => index.RemoveDocumentsAsync(Guid.Empty, [document], Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => index.RemoveDocumentsAsync(Workspace, [Guid.Empty], Cancellation));
        await Assert.ThrowsAsync<ArgumentException>(() => index.RemoveSourcesAsync(Workspace, [Guid.Empty], Cancellation));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => index.RemoveSourcesAsync(Workspace,
            Enumerable.Range(0, KnowledgeVectorIndex.MaxIdsPerDeletion + 1).Select(_ => Guid.NewGuid()).ToArray(), Cancellation));
    }

    [Fact]
    public void Requests_scopes_and_spaces_validate_their_invariants_and_cannot_change_after_validation()
    {
        float[] ok = [0.1f, 0.2f, 0.3f];
        var request = new VectorSearchRequest(Workspace, Space, KnowledgeScopeFilter.All, ok, 7);
        ok[0] = float.NaN; // the request copied the validated vector
        Assert.Equal(0.1f, request.Query.Span[0]);
        Assert.Throws<ArgumentException>(() => new VectorSearchRequest(Guid.Empty, Space, KnowledgeScopeFilter.All, request.Query, 5));
        Assert.Throws<ArgumentException>(() => new VectorSearchRequest(Workspace, Space, KnowledgeScopeFilter.None, request.Query, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VectorSearchRequest(Workspace, Space, KnowledgeScopeFilter.All, request.Query, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new VectorSearchRequest(Workspace, Space, KnowledgeScopeFilter.All, request.Query, VectorSearchRequest.MaxTopK + 1));
        foreach (var bad in new[] { new float[] { 1, 1 }, [0, 0, 0], [float.NaN, 1, 1], [1, float.NegativeInfinity, 1] })
            Assert.Throws<ArgumentException>(() => new VectorSearchRequest(Workspace, Space, KnowledgeScopeFilter.All, bad, 5));

        var (source, other) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        Assert.True(KnowledgeScopeFilter.All.Allows(Guid.CreateVersion7()));
        Assert.False(KnowledgeScopeFilter.None.Allows(source));
        Assert.True(KnowledgeScopeFilter.For(AiKnowledgeScope.SELECTED_SOURCES, [source, source]).Allows(source));
        Assert.Same(KnowledgeScopeFilter.All, KnowledgeScopeFilter.For(AiKnowledgeScope.ALL_SOURCES, []));
        Assert.Same(KnowledgeScopeFilter.None, KnowledgeScopeFilter.Selected([]));
        var narrowed = KnowledgeScopeFilter.Selected([source, other]).Excluding([other]);
        Assert.Equal([source], narrowed.SourceIds);
        Assert.False(narrowed.Allows(other));
        var allButOne = KnowledgeScopeFilter.All.Excluding([other]);
        Assert.Equal((true, false, true), (allButOne.AllSources, allButOne.Allows(other), allButOne.Allows(source)));
        Assert.Empty(KnowledgeScopeFilter.All.ExcludedSourceIds); // Excluding returned a new instance
        // Shared instances cannot be mutated through a cast.
        Assert.IsNotType<HashSet<Guid>>(KnowledgeScopeFilter.None.SourceIds);
        Assert.Throws<ArgumentException>(() => KnowledgeScopeFilter.Selected([Guid.Empty]));
        Assert.Throws<ArgumentException>(() => KnowledgeScopeFilter.All.Excluding([Guid.Empty]));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnowledgeScopeFilter.Selected(Enumerable.Range(0, 101).Select(_ => Guid.NewGuid())));
        Assert.Throws<ArgumentOutOfRangeException>(() => KnowledgeScopeFilter.For((AiKnowledgeScope)9, []));

        Assert.Equal(("default-v1", 1536), (new EmbeddingSpace("default-v1", 1536).Key, new EmbeddingSpace("default-v1", 1536).Dimensions));
        foreach (var key in new[] { "", "Default", "-x", "a b", "a/b", new string('a', 65), "x\n" })
            Assert.Throws<ArgumentException>(() => new EmbeddingSpace(key, 3));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingSpace("x", 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmbeddingSpace("x", EmbeddingSpace.MaxDimensions + 1));
        var unavailable = new EmbeddingException(EmbeddingFailure.UNAVAILABLE, retryAfter: TimeSpan.FromSeconds(3));
        Assert.Equal((true, TimeSpan.FromSeconds(3)), (unavailable.Transient, unavailable.RetryAfter));
        Assert.False(new EmbeddingException(EmbeddingFailure.MISCONFIGURED).Transient);
    }

    [Fact]
    public void Chunk_text_never_appears_in_ToString()
    {
        var chunk = new KnowledgeChunkText(Key(), "customer secret: 4111 1111 1111 1111");
        Assert.DoesNotContain("secret", chunk.ToString());
        Assert.Contains("TextLength = 36", chunk.ToString());
    }
}
