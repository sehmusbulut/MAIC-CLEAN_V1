using System.Collections.Frozen;
using MAIC.Domain.AI;

namespace MAIC.Application.AI;

/// <summary>
/// The tenant-safe metadata every stored vector carries: which workspace, source, document and chunk it came from.
/// All IDs are required; a vector store must persist them with the vector and filter on them.
/// </summary>
public sealed record KnowledgeVectorKey(Guid WorkspaceId, Guid KnowledgeSourceId, Guid DocumentId, Guid ChunkId, int ChunkSequence)
{
    public bool IsValid => WorkspaceId != Guid.Empty && KnowledgeSourceId != Guid.Empty && DocumentId != Guid.Empty
        && ChunkId != Guid.Empty && ChunkSequence >= 1;
}

public sealed record KnowledgeVector(KnowledgeVectorKey Key, ReadOnlyMemory<float> Vector);

/// <summary>
/// Which sources a search may consider. There is deliberately no "unfiltered" value: ALL means every source of the
/// request's workspace, and a selection is a non-empty set. An agent whose selection became empty maps to
/// <see cref="None"/>, which returns no knowledge without touching the store. Excluded sources (for example disabled
/// ones) are removed inside the store query, before topK. Sets are immutable.
/// </summary>
public sealed class KnowledgeScopeFilter
{
    public const int MaxExcludedSources = 10_000;

    private KnowledgeScopeFilter(bool all, FrozenSet<Guid> sources, FrozenSet<Guid> excluded) =>
        (AllSources, SourceIds, ExcludedSourceIds) = (all, sources, excluded);

    public static KnowledgeScopeFilter All { get; } = new(true, FrozenSet<Guid>.Empty, FrozenSet<Guid>.Empty);
    public static KnowledgeScopeFilter None { get; } = new(false, FrozenSet<Guid>.Empty, FrozenSet<Guid>.Empty);

    public bool AllSources { get; }
    /// <summary>The selected sources; empty for ALL and NONE.</summary>
    public IReadOnlySet<Guid> SourceIds { get; }
    /// <summary>Sources that must never match, whatever the selection (for example disabled sources).</summary>
    public IReadOnlySet<Guid> ExcludedSourceIds { get; }
    public bool IsEmpty => !AllSources && SourceIds.Count == 0;

    public static KnowledgeScopeFilter Selected(IEnumerable<Guid> sourceIds)
    {
        var set = sourceIds.ToFrozenSet();
        if (set.Contains(Guid.Empty)) throw new ArgumentException("Source IDs must be nonempty.", nameof(sourceIds));
        if (set.Count > AiAgent.MaxSelectedSources) throw new ArgumentOutOfRangeException(nameof(sourceIds));
        return set.Count == 0 ? None : new(false, set, FrozenSet<Guid>.Empty);
    }

    /// <summary>Maps an agent's configured scope; an empty selection means no knowledge, never "no filter".</summary>
    public static KnowledgeScopeFilter For(AiKnowledgeScope scope, IEnumerable<Guid> selectedSourceIds) => scope switch
    {
        AiKnowledgeScope.ALL_SOURCES => All,
        AiKnowledgeScope.SELECTED_SOURCES => Selected(selectedSourceIds),
        _ => throw new ArgumentOutOfRangeException(nameof(scope))
    };

    /// <summary>
    /// Removes sources from the scope. A selection shrinks (and becomes <see cref="None"/> when nothing remains); ALL keeps
    /// the exclusions so the store filters them before topK.
    /// </summary>
    public KnowledgeScopeFilter Excluding(IEnumerable<Guid> sourceIds)
    {
        var excluded = ExcludedSourceIds.Concat(sourceIds).ToFrozenSet();
        if (excluded.Contains(Guid.Empty)) throw new ArgumentException("Source IDs must be nonempty.", nameof(sourceIds));
        if (excluded.Count > MaxExcludedSources) throw new ArgumentOutOfRangeException(nameof(sourceIds));
        if (IsEmpty) return None;
        if (!AllSources)
        {
            var remaining = SourceIds.Where(id => !excluded.Contains(id)).ToFrozenSet();
            return remaining.Count == 0 ? None : new(false, remaining, FrozenSet<Guid>.Empty);
        }
        return excluded.Count == 0 ? this : new(true, FrozenSet<Guid>.Empty, excluded);
    }

    public bool Allows(Guid sourceId) => !ExcludedSourceIds.Contains(sourceId) && (AllSources || SourceIds.Contains(sourceId));
}

/// <summary>
/// A validated nearest-neighbour search. Workspace, embedding space and scope are mandatory constructor arguments, so
/// no implementation can be asked for an unfiltered or cross-workspace search. The query vector is copied.
/// </summary>
public sealed class VectorSearchRequest
{
    public const int MaxTopK = 100;

    public VectorSearchRequest(Guid workspaceId, EmbeddingSpace space, KnowledgeScopeFilter scope, ReadOnlyMemory<float> query, int topK)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        ArgumentNullException.ThrowIfNull(space);
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsEmpty) throw new ArgumentException("An empty scope has no knowledge; do not search.", nameof(scope));
        if (topK is < 1 or > MaxTopK) throw new ArgumentOutOfRangeException(nameof(topK));
        if (!KnowledgeVectorIndex.IsUsableVector(query.Span, space)) throw new ArgumentException("Query vector is invalid for the space.", nameof(query));
        (WorkspaceId, Space, Scope, Query, TopK) = (workspaceId, space, scope, query.ToArray(), topK);
    }

    public Guid WorkspaceId { get; }
    public EmbeddingSpace Space { get; }
    public KnowledgeScopeFilter Scope { get; }
    public ReadOnlyMemory<float> Query { get; }
    public int TopK { get; }
}

/// <summary>
/// A candidate chunk with cosine similarity in [-1, 1] (higher is closer). Candidates are not authoritative: retrieval
/// must still load the chunk from PostgreSQL filtered by workspace, enabled source, READY document and agent scope.
/// </summary>
public sealed record VectorMatch(KnowledgeVectorKey Key, double Score);

/// <summary>
/// Stores and searches knowledge vectors. Implementations (pgvector in RAG-002) must:
/// <list type="bullet">
/// <item>filter every operation by the given workspace inside the store query itself, never after the fact;</item>
/// <item>apply scope (selection and exclusions) in the same query and return exactly min(topK, in-scope vectors) results
/// even when many nearer vectors are out of workspace or scope (approximate indexes need iterative/exact scans);</item>
/// <item>keep spaces separate, keyed by space key and dimensions;</item>
/// <item>key vectors by (space, workspace, chunk), so one workspace can never replace or delete another's vector;</item>
/// <item>reject a whole batch (store nothing) with invalid metadata, another workspace, wrong dimensions, non-finite or
/// zero vectors, or duplicate chunks;</item>
/// <item>make <see cref="ReplaceDocumentAsync"/> atomic, and make deletions cover every space.</item>
/// </list>
/// The reusable contract test suite (KnowledgeVectorStoreContract in the integration tests) encodes these rules. Use
/// <see cref="KnowledgeVectorIndex"/> instead of calling a store directly.
/// </summary>
public interface IKnowledgeVectorStore
{
    /// <summary>Atomically replaces every vector of one document in one space with <paramref name="vectors"/>.</summary>
    Task ReplaceDocumentAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> vectors,
        CancellationToken cancellationToken);
    /// <summary>
    /// Like <see cref="ReplaceDocumentAsync"/>, but only while the indexing claim identified by <paramref name="leaseToken"/>
    /// is still held for this space and document, checked atomically with the write. Returns false (writing nothing) when
    /// the lease was lost, so a stale worker can never overwrite a newer attempt's vectors.
    /// </summary>
    Task<bool> ReplaceDocumentIfLeasedAsync(EmbeddingSpace space, Guid workspaceId, Guid documentId, IReadOnlyList<KnowledgeVector> vectors,
        Guid leaseToken, CancellationToken cancellationToken);
    /// <summary>Deletes the vectors of these documents of the workspace in every space.</summary>
    Task DeleteDocumentsAsync(Guid workspaceId, IReadOnlyCollection<Guid> documentIds, CancellationToken cancellationToken);
    /// <summary>Deletes the vectors of these sources of the workspace in every space.</summary>
    Task DeleteSourcesAsync(Guid workspaceId, IReadOnlyCollection<Guid> sourceIds, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorMatch>> SearchAsync(VectorSearchRequest request, CancellationToken cancellationToken);
}
