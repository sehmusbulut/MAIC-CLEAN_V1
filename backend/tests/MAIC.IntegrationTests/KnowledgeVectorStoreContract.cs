using MAIC.Application.AI;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>
/// Behavior every <see cref="IKnowledgeVectorStore"/> must have; RAG-002's pgvector store derives from this class too.
/// Tenant isolation, scope and exclusion filtering must happen inside the store query before topK: the scaled tests put
/// hundreds of nearer out-of-workspace or out-of-scope vectors in front of the expected ones, which an approximate
/// index with post-filtering fails. A pgvector subclass should make its store use the index path (for example
/// <c>enable_seqscan=off</c>) so these tests exercise it.
/// </summary>
public abstract class KnowledgeVectorStoreContract
{
    protected static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    protected static readonly EmbeddingSpace Space = new("contract-v1", 3);
    protected static readonly EmbeddingSpace OtherSpace = new("contract-v2", 3);
    /// <summary>Nearer distractors per scaled test; above typical HNSW candidate lists (ef_search 40).</summary>
    protected virtual int DistractorCount => 600;

    /// <summary>A store with no data visible to the workspaces this test creates.</summary>
    protected abstract Task<IKnowledgeVectorStore> CreateStoreAsync();

    /// <summary>
    /// Creates whatever rows the store's foreign keys need (workspace, source, document, chunk) before vectors with these
    /// keys are written. Stores without foreign keys need nothing.
    /// </summary>
    protected virtual Task PrepareKeysAsync(IReadOnlyCollection<KnowledgeVectorKey> keys) => Task.CompletedTask;

    protected static KnowledgeVector Vector(Guid workspace, Guid source, Guid document, float x, float y, float z, int sequence = 1) =>
        new(new KnowledgeVectorKey(workspace, source, document, Guid.CreateVersion7(), sequence), new[] { x, y, z });

    /// <summary>Stores each document's vectors through the atomic replace operation.</summary>
    protected async Task StoreAsync(IKnowledgeVectorStore store, EmbeddingSpace space, params KnowledgeVector[] vectors)
    {
        await PrepareKeysAsync(vectors.Select(v => v.Key).ToArray());
        foreach (var document in vectors.GroupBy(v => (v.Key.WorkspaceId, v.Key.DocumentId)))
            await store.ReplaceDocumentAsync(space, document.Key.WorkspaceId, document.Key.DocumentId, document.ToArray(), Cancellation);
    }

    private static VectorSearchRequest Request(Guid workspace, KnowledgeScopeFilter scope, int topK = 10, EmbeddingSpace? space = null) =>
        new(workspace, space ?? Space, scope, new float[] { 1, 0, 0 }, topK);

    private static KnowledgeVector[] Distractors(int count, Guid workspace, Guid source) =>
        Enumerable.Range(0, count).Select(i => Vector(workspace, source, Guid.CreateVersion7(), 1, 0.0001f * i, 0)).ToArray();

    [Fact]
    public async Task Search_never_returns_another_workspaces_vectors_even_when_they_are_nearer_and_numerous()
    {
        var store = await CreateStoreAsync();
        var (mine, theirs) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var source = Guid.CreateVersion7();
        var own = Enumerable.Range(0, 5).Select(i => Vector(mine, source, Guid.CreateVersion7(), 0.2f, 1, 0.1f * i)).ToArray();
        await StoreAsync(store, Space, own);
        await StoreAsync(store, Space, Distractors(DistractorCount, theirs, Guid.CreateVersion7()));

        var results = await store.SearchAsync(Request(mine, KnowledgeScopeFilter.All, topK: 5), Cancellation);
        Assert.Equal(own.Select(v => v.Key).OrderBy(k => k.ChunkId), results.Select(r => r.Key).OrderBy(k => k.ChunkId));
        Assert.Empty(await store.SearchAsync(Request(Guid.CreateVersion7(), KnowledgeScopeFilter.All), Cancellation));
    }

    [Fact]
    public async Task A_selected_scope_is_still_confined_to_the_requesting_workspace()
    {
        var store = await CreateStoreAsync();
        var (mine, theirs) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        // The other workspace uses the same source ID. Stores whose IDs are global keys (pgvector) cannot hold those rows,
        // so for them this proves only that nothing leaks; the scaled workspace test above covers numerous neighbours.
        var shared = Guid.CreateVersion7();
        var own = Vector(mine, shared, Guid.CreateVersion7(), 0, 1, 0);
        await StoreAsync(store, Space, own);
        await StoreAsync(store, Space, Distractors(20, theirs, shared));

        var results = await store.SearchAsync(Request(mine, KnowledgeScopeFilter.Selected([shared])), Cancellation);
        Assert.Equal([own.Key], results.Select(r => r.Key));
        Assert.Equal([own.Key], (await store.SearchAsync(Request(mine, KnowledgeScopeFilter.All.Excluding([Guid.CreateVersion7()])), Cancellation))
            .Select(r => r.Key));
    }

    [Fact]
    public async Task Selection_and_exclusions_are_applied_inside_the_query_before_topK()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var (near, far) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        await StoreAsync(store, Space, Distractors(DistractorCount, workspace, near));
        var allowed = Enumerable.Range(0, 3).Select(i => Vector(workspace, far, Guid.CreateVersion7(), 0.2f, 1, 0.1f * i)).ToArray();
        await StoreAsync(store, Space, allowed);

        var selected = await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.Selected([far]), topK: 3), Cancellation);
        Assert.Equal(3, selected.Count);
        Assert.All(selected, r => Assert.Equal(far, r.Key.KnowledgeSourceId));
        var excluding = await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All.Excluding([near]), topK: 3), Cancellation);
        Assert.Equal(allowed.Select(v => v.Key.ChunkId).Order(), excluding.Select(r => r.Key.ChunkId).Order());
        var all = await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All, topK: 3), Cancellation);
        Assert.Equal(3, all.Count);
        Assert.All(all, r => Assert.Equal(near, r.Key.KnowledgeSourceId));
    }

    [Fact]
    public async Task Spaces_are_isolated_by_key_and_dimensions()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var vector = Vector(workspace, Guid.CreateVersion7(), Guid.CreateVersion7(), 0, 1, 0);
        await StoreAsync(store, Space, vector);
        await StoreAsync(store, OtherSpace, vector with { Vector = new float[] { 1, 0, 0 } });
        Assert.Equal(0, (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Single().Score, 3);
        Assert.Equal(1, (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All, space: OtherSpace), Cancellation)).Single().Score, 3);
        // Same key, different dimensions: a different space (a misconfigured model change must not mix vectors).
        var resized = new EmbeddingSpace(Space.Key, 4);
        Assert.Empty(await store.SearchAsync(new VectorSearchRequest(workspace, resized, KnowledgeScopeFilter.All, new float[] { 1, 0, 0, 0 }, 5), Cancellation));
    }

    [Fact]
    public async Task Replacing_a_document_swaps_all_its_vectors_and_touches_nothing_else()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var (source, document, other) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var old = new[] { Vector(workspace, source, document, 1, 0, 0), Vector(workspace, source, document, 1, 0.1f, 0, 2) };
        var neighbour = Vector(workspace, source, other, 0, 1, 0);
        await StoreAsync(store, Space, [.. old, neighbour]);
        await StoreAsync(store, OtherSpace, old);

        var fresh = Vector(workspace, source, document, 0.5f, 0.5f, 0);
        await StoreAsync(store, Space, fresh);
        Assert.Equal(new[] { fresh.Key.ChunkId, neighbour.Key.ChunkId }.Order(),
            (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Select(r => r.Key.ChunkId).Order());
        Assert.Equal(2, (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All, space: OtherSpace), Cancellation)).Count);

        await store.ReplaceDocumentAsync(Space, workspace, document, [], Cancellation);
        Assert.Equal([neighbour.Key], (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Select(r => r.Key));
    }

    [Fact]
    public async Task Another_workspace_can_never_overwrite_or_delete_a_vector_by_reusing_its_ids()
    {
        var store = await CreateStoreAsync();
        var (victim, attacker) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var original = Vector(victim, Guid.CreateVersion7(), Guid.CreateVersion7(), 1, 0, 0);
        await StoreAsync(store, Space, original);

        var forged = new KnowledgeVector(original.Key with { WorkspaceId = attacker }, new float[] { 0, 1, 0 });
        // Seeding may fail for a forged key (chunk IDs are globally unique); the store itself must still be exercised.
        try { await PrepareKeysAsync([forged.Key]); } catch (Exception error) when (error is not OperationCanceledException) { }
        // The store may reject the forged key (for example through a composite foreign key) or keep it separate.
        try { await store.ReplaceDocumentAsync(Space, attacker, forged.Key.DocumentId, [forged], Cancellation); }
        catch (Exception error) when (error is not OperationCanceledException) { }
        await store.DeleteDocumentsAsync(attacker, [original.Key.DocumentId], Cancellation);
        await store.DeleteSourcesAsync(attacker, [original.Key.KnowledgeSourceId], Cancellation);
        await store.ReplaceDocumentAsync(Space, attacker, original.Key.DocumentId, [], Cancellation);

        var kept = Assert.Single(await store.SearchAsync(Request(victim, KnowledgeScopeFilter.All), Cancellation));
        Assert.Equal((original.Key, 1.0), (kept.Key, Math.Round(kept.Score, 3)));
    }

    [Fact]
    public async Task Results_are_cosine_ordered_bounded_and_carry_the_stored_metadata()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var (source, document) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var exact = Vector(workspace, source, document, 2, 0, 0, 1); // same direction, different length: cosine 1
        var diagonal = Vector(workspace, source, document, 1, 1, 0, 2);
        var opposite = Vector(workspace, source, document, -1, 0, 0, 3);
        var orthogonal = Vector(workspace, source, document, 0, 0, 5, 4);
        await StoreAsync(store, Space, orthogonal, opposite, diagonal, exact);

        var results = await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All, topK: 3), Cancellation);
        Assert.Equal([exact.Key, diagonal.Key, orthogonal.Key], results.Select(r => r.Key));
        Assert.Equal([1.0, Math.Round(Math.Sqrt(0.5), 3), 0.0], results.Select(r => Math.Round(r.Score, 3)));
        Assert.Equal(-1.0, (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Last().Score, 3);
    }

    [Fact]
    public async Task Deletions_cover_every_space_stay_in_the_workspace_and_empty_lists_delete_nothing()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var (source, otherSource, doomed) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var kept = Vector(workspace, source, Guid.CreateVersion7(), 0, 1, 0);
        var sourceVector = Vector(workspace, otherSource, Guid.CreateVersion7(), 0, 0, 1);
        await StoreAsync(store, Space, Vector(workspace, source, doomed, 1, 0, 0), Vector(workspace, source, doomed, 1, 0.1f, 0, 2), kept, sourceVector);
        await StoreAsync(store, OtherSpace, Vector(workspace, source, doomed, 1, 0, 0), sourceVector);

        await store.DeleteDocumentsAsync(workspace, [], Cancellation);
        await store.DeleteSourcesAsync(workspace, [], Cancellation);
        Assert.Equal(4, (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Count);

        await store.DeleteDocumentsAsync(workspace, [doomed], Cancellation);
        await store.DeleteSourcesAsync(workspace, [otherSource], Cancellation);
        Assert.Equal([kept.Key], (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Select(r => r.Key));
        Assert.Empty(await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All, space: OtherSpace), Cancellation));
    }

    [Fact]
    public async Task Invalid_batches_are_rejected_whole_and_leave_the_previous_vectors_in_place()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var (source, document) = (Guid.CreateVersion7(), Guid.CreateVersion7());
        var existing = Vector(workspace, source, document, 1, 0, 0);
        await StoreAsync(store, Space, existing);
        var valid = Vector(workspace, source, document, 0, 1, 0, 2);
        await PrepareKeysAsync([valid.Key]);
        var duplicate = valid with { Vector = new float[] { 0, 0, 1 } };
        foreach (var batch in new KnowledgeVector[][]
                 {
                     [valid, Vector(Guid.CreateVersion7(), source, document, 1, 0, 0)], // another workspace
                     [valid, Vector(workspace, source, Guid.CreateVersion7(), 1, 0, 0)], // another document
                     [valid, valid with { Vector = new float[] { 1, 0 } }], // wrong dimensions
                     [valid, valid with { Key = valid.Key with { ChunkId = Guid.CreateVersion7() }, Vector = new[] { float.NaN, 0, 0 } }],
                     [valid, valid with { Key = valid.Key with { ChunkId = Guid.CreateVersion7() }, Vector = new float[] { 0, 0, 0 } }],
                     [valid, valid with { Key = valid.Key with { KnowledgeSourceId = Guid.Empty } }],
                     [valid, duplicate] // same chunk twice
                 })
            await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReplaceDocumentAsync(Space, workspace, document, batch, Cancellation));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.ReplaceDocumentAsync(Space, Guid.Empty, document, [], Cancellation));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => store.DeleteDocumentsAsync(Guid.Empty, [document], Cancellation));

        Assert.Equal([existing.Key], (await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation)).Select(r => r.Key));
    }

    [Fact]
    public async Task Cancelled_operations_throw_and_change_nothing()
    {
        var store = await CreateStoreAsync();
        var workspace = Guid.CreateVersion7();
        var vector = Vector(workspace, Guid.CreateVersion7(), Guid.CreateVersion7(), 1, 0, 0);
        await PrepareKeysAsync([vector.Key]);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            store.ReplaceDocumentAsync(Space, workspace, vector.Key.DocumentId, [vector], cancelled.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), cancelled.Token));
        Assert.Empty(await store.SearchAsync(Request(workspace, KnowledgeScopeFilter.All), Cancellation));
    }
}

/// <summary>Reference implementation used to prove the contract suite; production uses pgvector (RAG-002).</summary>
public sealed class InMemoryKnowledgeVectorStore : IKnowledgeVectorStore
{
    // Keyed by space (key and dimensions), workspace and chunk: one workspace can never touch another's vector.
    private readonly Dictionary<(string Space, int Dimensions, Guid Workspace, Guid Chunk), KnowledgeVector> vectors = [];
    private readonly Lock gate = new();

    public Task ReplaceDocumentAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> batch,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Workspace and document are required.");
        if (batch.Any(v => !v.Key.IsValid || v.Key.WorkspaceId != workspaceId || v.Key.DocumentId != documentId
                || !KnowledgeVectorIndex.IsUsableVector(v.Vector.Span, space))
            || batch.Select(v => v.Key.ChunkId).Distinct().Count() != batch.Count)
            throw new ArgumentException("Vectors must be distinct, valid and belong to this workspace, document and space.", nameof(batch));
        lock (gate)
        {
            foreach (var key in vectors.Where(p => p.Key.Space == space.Key && p.Key.Dimensions == space.Dimensions
                         && p.Key.Workspace == workspaceId && p.Value.Key.DocumentId == documentId).Select(p => p.Key).ToArray())
                vectors.Remove(key);
            foreach (var vector in batch)
                vectors[(space.Key, space.Dimensions, workspaceId, vector.Key.ChunkId)] = vector with { Vector = vector.Vector.ToArray() };
        }
        return Task.CompletedTask;
    }

    /// <summary>The in-memory store keeps no indexing claims; every lease counts as held.</summary>
    public async Task<bool> ReplaceDocumentIfLeasedAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId,
        IReadOnlyList<KnowledgeVector> batch, Guid leaseToken, CancellationToken cancellationToken)
    {
        await ReplaceDocumentAsync(space, workspaceId, documentId, batch, cancellationToken);
        return true;
    }

    public Task DeleteDocumentsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken) =>
        Delete(workspaceId, v => documentIds.Contains(v.Key.DocumentId), cancellationToken);

    public Task DeleteSourcesAsync(Guid workspaceId, IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken) =>
        Delete(workspaceId, v => sourceIds.Contains(v.Key.KnowledgeSourceId), cancellationToken);

    private Task Delete(Guid workspaceId, Func<KnowledgeVector, bool> match, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        lock (gate)
            foreach (var key in vectors.Where(p => p.Key.Workspace == workspaceId && match(p.Value)).Select(p => p.Key).ToArray())
                vectors.Remove(key);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (gate)
            return Task.FromResult<IReadOnlyList<VectorMatch>>(vectors
                .Where(p => p.Key.Space == request.Space.Key && p.Key.Dimensions == request.Space.Dimensions
                    && p.Key.Workspace == request.WorkspaceId && request.Scope.Allows(p.Value.Key.KnowledgeSourceId))
                .Select(p => new VectorMatch(p.Value.Key, Cosine(request.Query.Span, p.Value.Vector.Span)))
                .OrderByDescending(m => m.Score).Take(request.TopK).ToArray());
    }

    private static double Cosine(ReadOnlySpan<float> a, ReadOnlySpan<float> b)
    {
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++) (dot, na, nb) = (dot + a[i] * b[i], na + a[i] * a[i], nb + b[i] * b[i]);
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}

public sealed class InMemoryKnowledgeVectorStoreTests : KnowledgeVectorStoreContract
{
    protected override Task<IKnowledgeVectorStore> CreateStoreAsync() => Task.FromResult<IKnowledgeVectorStore>(new InMemoryKnowledgeVectorStore());
}

/// <summary>
/// Proves the scaled tests catch the classic approximate-index mistake: take the nearest candidates first, then filter.
/// </summary>
public sealed class PostFilteringStoreIsRejected
{
    private sealed class PostFilteringStore(InMemoryKnowledgeVectorStore inner) : IKnowledgeVectorStore
    {
        public Task ReplaceDocumentAsync(EmbeddingSpace s, Guid w, Guid d, IReadOnlyList<KnowledgeVector> v, CancellationToken ct) => inner.ReplaceDocumentAsync(s, w, d, v, ct);
        public Task<bool> ReplaceDocumentIfLeasedAsync(EmbeddingSpace s, Guid w, Guid d, IReadOnlyList<KnowledgeVector> v, Guid l, CancellationToken ct) =>
            inner.ReplaceDocumentIfLeasedAsync(s, w, d, v, l, ct);
        public Task DeleteDocumentsAsync(Guid w, IReadOnlyCollection<Guid> d, CancellationToken ct) => inner.DeleteDocumentsAsync(w, d, ct);
        public Task DeleteSourcesAsync(Guid w, IReadOnlyCollection<Guid> s, CancellationToken ct) => inner.DeleteSourcesAsync(w, s, ct);

        // Simulates an index scan with a fixed candidate list (like HNSW ef_search=40) followed by the scope filter.
        public async Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken ct)
        {
            var candidates = await inner.SearchAsync(new VectorSearchRequest(request.WorkspaceId, request.Space, KnowledgeScopeFilter.All,
                request.Query, 40), ct);
            return candidates.Where(m => request.Scope.Allows(m.Key.KnowledgeSourceId)).Take(request.TopK).ToArray();
        }
    }

    private sealed class Contract : KnowledgeVectorStoreContract
    {
        protected override Task<IKnowledgeVectorStore> CreateStoreAsync() =>
            Task.FromResult<IKnowledgeVectorStore>(new PostFilteringStore(new InMemoryKnowledgeVectorStore()));
        public Task RunAsync() => Selection_and_exclusions_are_applied_inside_the_query_before_topK();
    }

    [Fact]
    public async Task The_contract_fails_a_store_that_filters_after_taking_candidates() =>
        await Assert.ThrowsAnyAsync<Exception>(() => new Contract().RunAsync());
}
