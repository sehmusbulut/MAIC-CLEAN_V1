using MAIC.Domain.Knowledge;

namespace MAIC.Application.AI;

/// <summary>A chunk to embed. Its text is tenant content and is never printed (ToString shows only the key and length).</summary>
public sealed record KnowledgeChunkText(KnowledgeVectorKey Key, string Text)
{
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"Key = {Key}, TextLength = {Text?.Length ?? 0}");
        return true;
    }
}

/// <summary>The indexing claim expired and another attempt took over; this attempt wrote nothing.</summary>
public sealed class KnowledgeIndexLeaseLostException() : Exception("The indexing lease was lost.");

public enum KnowledgeIndexMode
{
    /// <summary>Any failure fails the whole document.</summary>
    Strict,
    /// <summary>Chunks the provider rejects as content are isolated by splitting batches and left out.</summary>
    SkipRejected,
    /// <summary>Also isolates chunks answered with MISCONFIGURED, when the provider is proven to work afterwards.</summary>
    IsolateSuspects
}

/// <summary>
/// The only entry point to embeddings and the vector store. It validates everything that crosses the port boundary
/// (single workspace and document, complete metadata, dimensions, finite non-zero vectors, provider limits and response
/// shape), keeps each call within the provider's batch and input limits, short-circuits empty scopes, and drops any store
/// match outside the request's workspace or scope as defense in depth against a faulty implementation.
/// </summary>
public sealed class KnowledgeVectorIndex(IEmbeddingGenerator embeddings, IKnowledgeVectorStore store)
{
    /// <summary>A chunker-produced document stays far below this; larger batches indicate a bug or abuse.</summary>
    public const int MaxChunksPerDocument = 2000;
    public const int MaxIdsPerDeletion = 1000;

    /// <summary>Scores may exceed [-1, 1] by float rounding only.</summary>
    private const double ScoreTolerance = 1e-4;

    public EmbeddingSpace Space => embeddings.Space;

    /// <summary>
    /// Embeds a document's chunks and atomically replaces its vectors in the current space (an empty list removes them),
    /// so a re-ingested document never shows stale and fresh chunks together. Any provider failure fails the whole call.
    /// </summary>
    public Task IndexDocumentAsync(Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeChunkText> chunks,
        CancellationToken cancellationToken) =>
        IndexDocumentAsync(workspaceId, documentId, chunks, KnowledgeIndexMode.Strict, cancellationToken);

    /// <summary>
    /// Like <see cref="IndexDocumentAsync(Guid, Guid, IReadOnlyList{KnowledgeChunkText}, CancellationToken)"/>, but in
    /// <see cref="KnowledgeIndexMode.SkipRejected"/> a batch the provider rejects as content is split until the offending
    /// chunks are isolated; they are left out and returned, and the rest is stored. In
    /// <see cref="KnowledgeIndexMode.IsolateSuspects"/> a single chunk answered with MISCONFIGURED is treated the same way
    /// (a gateway refusing some text). In both modes a chunk is blamed only after a benign call succeeds (the provider
    /// works) and the chunk fails again on its own; otherwise MISCONFIGURED is thrown and nothing is blamed on content.
    /// </summary>
    public Task<IReadOnlyList<KnowledgeVectorKey>> IndexDocumentAsync(Guid workspaceId, Guid documentId,
        IReadOnlyList<KnowledgeChunkText> chunks, KnowledgeIndexMode mode, CancellationToken cancellationToken) =>
        IndexDocumentAsync(workspaceId, documentId, chunks, mode, null, cancellationToken);

    /// <summary>
    /// With <paramref name="leaseToken"/>, vectors are written only while that indexing claim is held; otherwise
    /// <see cref="KnowledgeIndexLeaseLostException"/> is thrown and nothing is written.
    /// </summary>
    public async Task<IReadOnlyList<KnowledgeVectorKey>> IndexDocumentAsync(Guid workspaceId, Guid documentId,
        IReadOnlyList<KnowledgeChunkText> chunks, KnowledgeIndexMode mode, Guid? leaseToken, CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Workspace and document are required.");
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        RequireSaneLimits(requireChunkCapacity: true);
        if (chunks.Count > MaxChunksPerDocument) throw new ArgumentOutOfRangeException(nameof(chunks));
        if (chunks.Any(c => !c.Key.IsValid || c.Key.WorkspaceId != workspaceId || c.Key.DocumentId != documentId))
            throw new ArgumentException("Every chunk must carry complete metadata of this workspace and document.", nameof(chunks));
        if (chunks.Select(c => c.Key.ChunkId).Distinct().Count() != chunks.Count)
            throw new ArgumentException("Chunks must be distinct.", nameof(chunks));

        var rejected = new List<KnowledgeVectorKey>();
        var unusable = chunks.Where(c => string.IsNullOrWhiteSpace(c.Text) || c.Text.Length > embeddings.MaxInputLength).ToArray();
        if (unusable.Length > 0)
        {
            if (mode == KnowledgeIndexMode.Strict) throw new EmbeddingException(EmbeddingFailure.REJECTED_INPUT);
            rejected.AddRange(unusable.Select(c => c.Key));
            chunks = chunks.Except(unusable).ToArray();
        }

        var run = new IndexRun(mode, rejected);
        foreach (var batch in chunks.Chunk(embeddings.MaxBatchSize)) await EmbedBatchAsync(batch, run, cancellationToken);
        if (run.Suspects.Count > 0)
        {
            // Blame content only when the provider demonstrably works: a benign call (an accepted chunk, or fixed text)
            // must succeed now, and each suspect must fail again on its own. A provider that regressed or broke during
            // the run therefore blames nothing; a suspect that succeeds on the second try is stored.
            try
            {
                await EmbedAsync([run.Accepted.FirstOrDefault()?.Text ?? HealthCheckText], EmbeddingPurpose.Document, cancellationToken);
            }
            catch (EmbeddingException error) when (error.Failure is EmbeddingFailure.REJECTED_INPUT or EmbeddingFailure.MISCONFIGURED)
            {
                throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
            }
            foreach (var suspect in run.Suspects)
            {
                try
                {
                    var vector = await EmbedAsync([suspect.Text], EmbeddingPurpose.Document, cancellationToken);
                    run.Vectors.Add(new KnowledgeVector(suspect.Key, vector[0]));
                }
                catch (EmbeddingException error) when (Splittable(error, mode))
                {
                    rejected.Add(suspect.Key);
                }
            }
        }
        if (leaseToken is { } token)
        {
            if (!await store.ReplaceDocumentIfLeasedAsync(Space, workspaceId, documentId, run.Vectors, token, cancellationToken))
                throw new KnowledgeIndexLeaseLostException();
        }
        else
        {
            await store.ReplaceDocumentAsync(Space, workspaceId, documentId, run.Vectors, cancellationToken);
        }
        return rejected;
    }

    /// <summary>Benign text used to confirm the provider works when no chunk of the document was accepted.</summary>
    public const string HealthCheckText = "Customer support knowledge base: returns, exchanges and delivery times.";

    private sealed class IndexRun(KnowledgeIndexMode mode, List<KnowledgeVectorKey> rejected)
    {
        public KnowledgeIndexMode Mode { get; } = mode;
        public List<KnowledgeVectorKey> Rejected { get; } = rejected;
        public List<KnowledgeVector> Vectors { get; } = [];
        public List<KnowledgeChunkText> Accepted { get; } = [];
        public List<KnowledgeChunkText> Suspects { get; } = [];
    }

    /// <summary>Splits a refused batch until single chunks remain (at most 2n - 1 calls for n chunks).</summary>
    private async Task EmbedBatchAsync(KnowledgeChunkText[] batch, IndexRun run, CancellationToken cancellationToken)
    {
        IReadOnlyList<ReadOnlyMemory<float>> embedded;
        try
        {
            embedded = await EmbedAsync(batch.Select(c => c.Text).ToArray(), EmbeddingPurpose.Document, cancellationToken);
        }
        catch (EmbeddingException error) when (Splittable(error, run.Mode))
        {
            if (batch.Length > 1)
            {
                var half = batch.Length / 2;
                await EmbedBatchAsync(batch[..half], run, cancellationToken);
                await EmbedBatchAsync(batch[half..], run, cancellationToken);
                return;
            }
            run.Suspects.Add(batch[0]);
            return;
        }
        run.Vectors.AddRange(batch.Select((chunk, i) => new KnowledgeVector(chunk.Key, embedded[i])));
        run.Accepted.AddRange(batch);
    }

    private static bool Splittable(EmbeddingException error, KnowledgeIndexMode mode) => mode switch
    {
        KnowledgeIndexMode.SkipRejected => error.Failure == EmbeddingFailure.REJECTED_INPUT,
        KnowledgeIndexMode.IsolateSuspects => error.Failure is EmbeddingFailure.REJECTED_INPUT or EmbeddingFailure.MISCONFIGURED,
        _ => false
    };

    /// <summary>
    /// Embeds a fixed query so the provider adapter verifies its configuration at startup instead of on a visitor's
    /// first question. Failures are the caller's to log; nothing is stored.
    /// </summary>
    public async Task WarmUpAsync(CancellationToken cancellationToken)
    {
        if (Space.IsDisabled) return;
        RequireSaneLimits(requireChunkCapacity: false);
        await EmbedAsync(["knowledge search warm-up"], EmbeddingPurpose.Query, cancellationToken);
    }

    /// <summary>Deletes the vectors of these documents in every space (document deleted or superseded).</summary>
    public Task RemoveDocumentsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (documentIds.Contains(Guid.Empty)) throw new ArgumentException("Document IDs must be nonempty.", nameof(documentIds));
        if (documentIds.Count > MaxIdsPerDeletion) throw new ArgumentOutOfRangeException(nameof(documentIds));
        return documentIds.Count == 0 ? Task.CompletedTask : store.DeleteDocumentsAsync(workspaceId, documentIds, cancellationToken);
    }

    /// <summary>Deletes the vectors of these sources in every space (source deleted).</summary>
    public Task RemoveSourcesAsync(Guid workspaceId, IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (sourceIds.Contains(Guid.Empty)) throw new ArgumentException("Source IDs must be nonempty.", nameof(sourceIds));
        if (sourceIds.Count > MaxIdsPerDeletion) throw new ArgumentOutOfRangeException(nameof(sourceIds));
        return sourceIds.Count == 0 ? Task.CompletedTask : store.DeleteSourcesAsync(workspaceId, sourceIds, cancellationToken);
    }

    /// <summary>
    /// Embeds the query and returns at most <paramref name="topK"/> candidates of this workspace and scope, best first.
    /// An empty scope or blank query returns no candidates without calling the provider or the store.
    /// </summary>
    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(Guid workspaceId, KnowledgeScopeFilter scope, string query, int topK,
        CancellationToken cancellationToken)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(scope);
        if (topK is < 1 or > VectorSearchRequest.MaxTopK) throw new ArgumentOutOfRangeException(nameof(topK));
        RequireSaneLimits(requireChunkCapacity: false);
        if (query.Length > embeddings.MaxInputLength)
        {
            // Truncate without splitting a surrogate pair.
            var length = embeddings.MaxInputLength;
            if (char.IsHighSurrogate(query[length - 1])) length--;
            query = query[..length];
        }
        if (scope.IsEmpty || string.IsNullOrWhiteSpace(query)) return [];

        var vector = (await EmbedAsync([query], EmbeddingPurpose.Query, cancellationToken))[0];
        var request = new VectorSearchRequest(workspaceId, Space, scope, vector, topK);
        var matches = await store.SearchAsync(request, cancellationToken);
        return matches
            .Where(m => m.Key.IsValid && m.Key.WorkspaceId == workspaceId && scope.Allows(m.Key.KnowledgeSourceId)
                && double.IsFinite(m.Score) && Math.Abs(m.Score) <= 1 + ScoreTolerance)
            .DistinctBy(m => m.Key.ChunkId)
            .OrderByDescending(m => m.Score)
            .Take(topK)
            .ToArray();
    }

    /// <summary>
    /// Adapter limits must be positive, and indexing needs room for a whole chunk; otherwise every document would fail
    /// permanently, so this is reported as a configuration problem instead of bad content.
    /// </summary>
    private void RequireSaneLimits(bool requireChunkCapacity)
    {
        if (embeddings.MaxBatchSize < 1 || embeddings.MaxInputLength < 1
            || (requireChunkCapacity && embeddings.MaxInputLength < KnowledgeChunker.MaxChunkLength))
            throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
    }

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        var vectors = await embeddings.EmbedAsync(inputs, purpose, cancellationToken);
        if (vectors is null || vectors.Count != inputs.Count || vectors.Any(v => !IsUsableVector(v.Span, Space)))
            throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE);
        return vectors;
    }

    /// <summary>
    /// Right dimensions, only finite values and a norm that does not underflow in single precision (cosine similarity is
    /// undefined for a zero vector).
    /// </summary>
    public static bool IsUsableVector(ReadOnlySpan<float> vector, EmbeddingSpace space)
    {
        if (vector.Length != space.Dimensions) return false;
        float squared = 0;
        foreach (var value in vector)
        {
            if (!float.IsFinite(value)) return false;
            squared += value * value;
        }
        return squared > 0 && float.IsFinite(squared);
    }
}
