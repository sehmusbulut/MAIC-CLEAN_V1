namespace MAIC.Application.AI;

/// <summary>
/// Indexing limits: a daily chunk quota per workspace (UTC day, soft: checked before each claim, so the last document of
/// the day may exceed it) and the indexing provider budget in chunks per minute, shared by all Workers of a space.
/// </summary>
public sealed record KnowledgeIndexLimits(int MaxIndexedChunksPerWorkspacePerDay = 200_000, int MaxIndexingChunksPerMinute = 3_000);

/// <summary>A claimed (space, document) indexing attempt. MisconfiguredStrikes counts consecutive MISCONFIGURED attempts.</summary>
public sealed record KnowledgeIndexWork(Guid WorkspaceId, Guid DocumentId, Guid LeaseToken, int Attempt, int MisconfiguredStrikes);

public enum KnowledgeIndexOutcomeKind { Indexed, Failed, Retrying, NotReady, LeaseLost }

/// <summary>Stable codes stored as the last failure of an indexing attempt; never provider text.</summary>
public enum KnowledgeIndexFailure { UNAVAILABLE, INVALID_RESPONSE, MISCONFIGURED, INTERNAL_ERROR }

public sealed record KnowledgeIndexOutcome(Guid WorkspaceId, Guid DocumentId, int Attempt, KnowledgeIndexOutcomeKind Kind,
    int IndexedChunks = 0, int RejectedChunks = 0, KnowledgeIndexFailure? Failure = null, Exception? Error = null);

/// <summary>
/// Per-space indexing state of READY documents. Implementations claim with SKIP LOCKED and act only while the claim's
/// lease token is held; a document whose content was re-processed after its state was written is indexed again.
/// </summary>
public interface IKnowledgeIndexStore
{
    /// <summary>Adds pending states for READY documents of enabled sources that have none for their current content.</summary>
    Task<int> DiscoverAsync(EmbeddingSpace space, DateTimeOffset now, int limit, CancellationToken cancellationToken);
    /// <summary>Claims the next due document fairly across workspaces, or null.</summary>
    Task<KnowledgeIndexWork?> ClaimNextAsync(EmbeddingSpace space, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    /// <summary>
    /// The indexing share of the provider quota, shared by every Worker: a token bucket of chunks per space refilled per
    /// minute. True while the balance is positive; a document is debited in <see cref="ChargeAsync"/> (the balance may go
    /// negative), so one large document never blocks but the average rate across all Workers stays within the budget.
    /// Query embedding never draws from it.
    /// </summary>
    Task<bool> HasIndexingBudgetAsync(EmbeddingSpace space, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>
    /// Adds chunks sent for indexing to the workspace's daily usage (UTC day) and debits the shared budget. The ledger is append-only: deleting or
    /// re-ingesting documents never reduces it, and every attempt (retries included) is charged.
    /// </summary>
    Task ChargeAsync(EmbeddingSpace space, KnowledgeIndexWork work, int chunks, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>The claimed document's chunks in order; empty when it is no longer READY or its source is disabled.</summary>
    Task<IReadOnlyList<KnowledgeChunkText>> LoadChunksAsync(KnowledgeIndexWork work, CancellationToken cancellationToken);
    /// <summary>INDEXED (some chunks may have been rejected as content). False when the lease was lost.</summary>
    Task<bool> CompleteAsync(EmbeddingSpace space, KnowledgeIndexWork work, int indexedChunks, int rejectedChunks, DateTimeOffset now,
        CancellationToken cancellationToken);
    /// <summary>FAILED with EMBEDDING_REJECTED: the provider refused every chunk as content. False when the lease was lost.</summary>
    Task<bool> RejectAsync(EmbeddingSpace space, KnowledgeIndexWork work, int rejectedChunks, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Back to RETRY at <paramref name="nextAttemptAt"/>; never permanent. False when the lease was lost.</summary>
    Task<bool> RetryAsync(EmbeddingSpace space, KnowledgeIndexWork work, KnowledgeIndexFailure? failure, DateTimeOffset nextAttemptAt,
        DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Returns a held claim without counting the attempt (shutdown).</summary>
    Task<bool> ReleaseAsync(EmbeddingSpace space, KnowledgeIndexWork work, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>
/// Indexes one READY document per call in the configured embedding space. Only content the provider rejects is ever held
/// against a document: rejected chunks are isolated and skipped, and a document is FAILED only when every chunk is
/// rejected. Outages, invalid responses, misconfiguration and internal errors are provider faults: the document is
/// retried with capped exponential backoff (honouring Retry-After up to the cap) and never fails permanently. After
/// repeated MISCONFIGURED attempts the document is indexed chunk by chunk, so a gateway refusing one chunk's text cannot
/// block the rest, while a broken provider still blames no content. Each document has its own deadline, and documents
/// are started only within the indexing budget shared by all Workers.
/// </summary>
public sealed class KnowledgeIndexProcessor(IKnowledgeIndexStore store, KnowledgeVectorIndex index, TimeProvider clock)
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan DefaultDocumentTimeout = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan FirstBackoff = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromHours(1);
    public static readonly TimeSpan NotReadyDelay = TimeSpan.FromMinutes(1);
    public const int SuspectAfterStrikes = 3;
    public const int DiscoveryBatch = 500;

    public TimeSpan Lease { get; init; } = DefaultLease;
    public TimeSpan DocumentTimeout { get; init; } = DefaultDocumentTimeout;

    public EmbeddingSpace Space => index.Space;

    public Task<int> DiscoverAsync(CancellationToken cancellationToken) =>
        store.DiscoverAsync(Space, clock.GetUtcNow(), DiscoveryBatch, cancellationToken);

    /// <summary>Indexes at most one document. Returns null when none is due or the indexing budget is used up.</summary>
    public async Task<KnowledgeIndexOutcome?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var space = Space;
        if (!await store.HasIndexingBudgetAsync(space, clock.GetUtcNow(), cancellationToken)) return null;
        var work = await store.ClaimNextAsync(space, clock.GetUtcNow(), Lease, cancellationToken);
        if (work is null) return null;
        try
        {
            var chunks = await store.LoadChunksAsync(work, cancellationToken);
            if (chunks.Count == 0)
            {
                var held = await store.RetryAsync(space, work, null, clock.GetUtcNow() + NotReadyDelay, clock.GetUtcNow(), cancellationToken);
                return Outcome(work, held ? KnowledgeIndexOutcomeKind.NotReady : KnowledgeIndexOutcomeKind.LeaseLost);
            }

            await store.ChargeAsync(space, work, chunks.Count, clock.GetUtcNow(), cancellationToken);
            var mode = work.MisconfiguredStrikes >= SuspectAfterStrikes ? KnowledgeIndexMode.IsolateSuspects : KnowledgeIndexMode.SkipRejected;
            IReadOnlyList<KnowledgeVectorKey> rejected;
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(DocumentTimeout);
                try
                {
                    rejected = await index.IndexDocumentAsync(work.WorkspaceId, work.DocumentId, chunks, mode, work.LeaseToken, deadline.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new EmbeddingException(EmbeddingFailure.UNAVAILABLE); // the document's deadline passed
                }
            }

            var now = clock.GetUtcNow();
            if (rejected.Count == chunks.Count)
                return Outcome(work, await store.RejectAsync(space, work, rejected.Count, now, cancellationToken)
                    ? KnowledgeIndexOutcomeKind.Failed : KnowledgeIndexOutcomeKind.LeaseLost, 0, rejected.Count);
            return Outcome(work, await store.CompleteAsync(space, work, chunks.Count - rejected.Count, rejected.Count, now, cancellationToken)
                ? KnowledgeIndexOutcomeKind.Indexed : KnowledgeIndexOutcomeKind.LeaseLost, chunks.Count - rejected.Count, rejected.Count);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseOnShutdownAsync(space, work);
            throw;
        }
        catch (KnowledgeIndexLeaseLostException)
        {
            return Outcome(work, KnowledgeIndexOutcomeKind.LeaseLost);
        }
        catch (EmbeddingException error)
        {
            // REJECTED_INPUT cannot reach here (chunks are isolated), so every embedding failure is a provider fault.
            var failure = error.Failure switch
            {
                EmbeddingFailure.UNAVAILABLE => KnowledgeIndexFailure.UNAVAILABLE,
                EmbeddingFailure.MISCONFIGURED => KnowledgeIndexFailure.MISCONFIGURED,
                EmbeddingFailure.INVALID_RESPONSE => KnowledgeIndexFailure.INVALID_RESPONSE,
                _ => KnowledgeIndexFailure.INTERNAL_ERROR
            };
            return await RetryAsync(space, work, failure, error.RetryAfter, null, cancellationToken);
        }
        catch (Exception error)
        {
            return await RetryAsync(space, work, KnowledgeIndexFailure.INTERNAL_ERROR, null, error, cancellationToken);
        }
    }

    /// <summary>30 s doubling per attempt, at most one hour; a provider's Retry-After is honoured up to the same cap.</summary>
    public static TimeSpan Backoff(int attempt, TimeSpan? retryAfter)
    {
        var exponential = FirstBackoff * Math.Pow(2, Math.Clamp(attempt - 1, 0, 16));
        var wait = exponential > MaxBackoff ? MaxBackoff : exponential;
        if (retryAfter is { } hint && hint > wait) wait = hint > MaxBackoff ? MaxBackoff : hint;
        return wait;
    }

    private async Task<KnowledgeIndexOutcome> RetryAsync(EmbeddingSpace space, KnowledgeIndexWork work, KnowledgeIndexFailure failure,
        TimeSpan? retryAfter, Exception? error, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var held = await store.RetryAsync(space, work, failure, now + Backoff(work.Attempt, retryAfter), now, cancellationToken);
        return Outcome(work, held ? KnowledgeIndexOutcomeKind.Retrying : KnowledgeIndexOutcomeKind.LeaseLost, failure: failure, error: error);
    }

    private async Task ReleaseOnShutdownAsync(EmbeddingSpace space, KnowledgeIndexWork work)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await store.ReleaseAsync(space, work, clock.GetUtcNow(), grace.Token);
        }
        catch (Exception)
        {
            // Best effort: the lease still expires and the document is reclaimed.
        }
    }

    private static KnowledgeIndexOutcome Outcome(KnowledgeIndexWork work, KnowledgeIndexOutcomeKind kind, int indexed = 0, int rejected = 0,
        KnowledgeIndexFailure? failure = null, Exception? error = null) =>
        new(work.WorkspaceId, work.DocumentId, work.Attempt, kind, indexed, rejected, failure, error);
}
