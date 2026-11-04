using System.Security.Cryptography;
using System.Text;
using MAIC.Domain.Knowledge;

namespace MAIC.Application.Knowledge;

/// <summary>A claimed job with the source/document facts needed to process it. Read after the claim committed.</summary>
public sealed record IngestionWork(Guid JobId, Guid LeaseToken, int Attempt, Guid WorkspaceId, Guid DocumentId, Guid SourceId,
    KnowledgeDocumentOrigin Origin, bool SourceEnabled, string? ContentType, string? TextContent, string? WebsiteUrl);

/// <summary>Processed content ready to persist. SourceUrl is set only for website snapshots.</summary>
public sealed record IngestionContent(string ContentType, long SizeBytes, string ContentSha256, string? SourceUrl, int CharacterCount,
    IReadOnlyList<KnowledgeChunkSpan> Chunks);

/// <summary>A fetched web response body, bounded by the fetcher; FinalUrl is the URL after validated redirects.</summary>
public sealed record FetchedWebPage(string FinalUrl, string ContentType, string? Charset, byte[] Body);

/// <summary>A classified ingestion failure. Transient failures are retried with backoff; others fail the document.</summary>
public sealed class IngestionException(IngestionFailure reason, bool transient, Exception? inner = null)
    : Exception($"Ingestion failed: {reason}.", inner)
{
    public IngestionFailure Reason { get; } = reason;
    public bool Transient { get; } = transient;
}

public interface IIngestionJobStore
{
    /// <summary>Claims the oldest due job (skipping rows other workers hold); exhausted expired jobs are failed instead.</summary>
    Task<IngestionWork?> ClaimNextAsync(DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    /// <summary>
    /// Persists chunks and READY state if the lease is still held. Returns Ready, Cancelled (the source was disabled
    /// during the attempt) or LeaseLost.
    /// </summary>
    Task<IngestionOutcomeKind> CompleteAsync(IngestionWork work, IngestionContent content, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>
    /// Records a failed attempt if the lease is still held: SOURCE_DISABLED cancels, a transient failure with attempts left
    /// requeues, anything else fails the document. Returns <see cref="IngestionOutcomeKind.LeaseLost"/> otherwise.
    /// </summary>
    Task<IngestionOutcomeKind> FailAsync(IngestionWork work, IngestionFailure reason, bool transient, DateTimeOffset now, CancellationToken cancellationToken);
    /// <summary>Requeues a held attempt without counting it. Returns false when the lease was lost.</summary>
    Task<bool> ReleaseAsync(IngestionWork work, DateTimeOffset now, CancellationToken cancellationToken);
}

/// <summary>A stored upload's key, as found by enumerating storage.</summary>
public sealed record StoredKnowledgeFile(Guid WorkspaceId, Guid DocumentId);

/// <summary>Stores uploaded file bytes under server-generated keys; never under client-supplied names.</summary>
public interface IKnowledgeFileStorage
{
    /// <summary>
    /// Enumerates stored files last written before <paramref name="olderThan"/> and removes abandoned temporary files
    /// older than it. Unrecognized names are never touched.
    /// </summary>
    IAsyncEnumerable<StoredKnowledgeFile> ListAsync(DateTimeOffset olderThan, CancellationToken cancellationToken);
    /// <summary>Moves a file out of the live tree into quarantine, where it stays recoverable until purged.</summary>
    Task QuarantineAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken);
    /// <summary>Permanently deletes quarantined files moved there before <paramref name="quarantinedBefore"/>; returns the count.</summary>
    Task<int> PurgeQuarantineAsync(DateTimeOffset quarantinedBefore, CancellationToken cancellationToken);
    Task SaveAsync(Guid workspaceId, Guid documentId, ReadOnlyMemory<byte> content, CancellationToken cancellationToken);
    /// <summary>Throws <see cref="IngestionException"/> with STORAGE_MISSING or STORAGE_UNAVAILABLE.</summary>
    Task<byte[]> ReadAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken);
    Task DeleteAsync(Guid workspaceId, Guid documentId, CancellationToken cancellationToken);
}

/// <summary>Fetches one page with network-destination (SSRF) checks on every hop. Throws <see cref="IngestionException"/>.</summary>
public interface IWebPageFetcher
{
    Task<FetchedWebPage> FetchAsync(string url, CancellationToken cancellationToken);
}

/// <summary>
/// Extracts plain text from supported media types. Throws <see cref="IngestionException"/>. Cancellation must stop the
/// work (an isolated implementation kills its parser process).
/// </summary>
public interface IDocumentTextExtractor
{
    Task<string> ExtractAsync(string contentType, string? charset, byte[] content, CancellationToken cancellationToken);
}

public enum IngestionOutcomeKind { Ready, Retrying, Failed, Cancelled, LeaseLost }

public sealed record IngestionOutcome(Guid JobId, Guid DocumentId, int Attempt, IngestionOutcomeKind Kind, IngestionFailure? Failure,
    Exception? Error = null);

/// <summary>
/// Runs one ingestion attempt: claim, load content (stored upload, current source text, or a safely fetched page),
/// extract, normalize, chunk and persist. Every failure is classified; unexpected errors are retried as INTERNAL_ERROR.
/// </summary>
public sealed class IngestionProcessor(IIngestionJobStore store, IKnowledgeFileStorage storage, IWebPageFetcher fetcher,
    IDocumentTextExtractor extractor, TimeProvider clock)
{
    public static readonly TimeSpan DefaultLease = TimeSpan.FromMinutes(10);

    /// <summary>Extraction runs on a worker thread and must finish well within the lease.</summary>
    public static readonly TimeSpan DefaultExtractionTimeout = TimeSpan.FromMinutes(2);

    public TimeSpan Lease { get; init; } = DefaultLease;
    public TimeSpan ExtractionTimeout { get; init; } = DefaultExtractionTimeout;

    /// <summary>Processes at most one job. Returns null when no job is due.</summary>
    public async Task<IngestionOutcome?> ProcessNextAsync(CancellationToken cancellationToken)
    {
        var work = await store.ClaimNextAsync(clock.GetUtcNow(), Lease, cancellationToken);
        if (work is null) return null;
        if (!work.SourceEnabled) return await FailAsync(work, IngestionFailure.SOURCE_DISABLED, false, null, cancellationToken);

        IngestionContent content;
        try
        {
            content = await LoadAsync(work, cancellationToken);
        }
        catch (IngestionException error)
        {
            return await FailAsync(work, error.Reason, error.Transient, error, cancellationToken);
        }
        catch (OutOfMemoryException error)
        {
            // Content that exhausts memory will do so again; retrying would only repeat the damage.
            return await FailAsync(work, IngestionFailure.CONTENT_TOO_LARGE, false, error, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseOnShutdownAsync(work);
            throw;
        }
        catch (Exception error)
        {
            return await FailAsync(work, IngestionFailure.INTERNAL_ERROR, true, error, cancellationToken);
        }

        try
        {
            var kind = await store.CompleteAsync(work, content, clock.GetUtcNow(), cancellationToken);
            return new(work.JobId, work.DocumentId, work.Attempt, kind,
                kind == IngestionOutcomeKind.Cancelled ? IngestionFailure.SOURCE_DISABLED : null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await ReleaseOnShutdownAsync(work);
            throw;
        }
        catch (Exception error)
        {
            // A persistence error is retried with backoff instead of waiting for the lease to expire.
            return await FailAsync(work, IngestionFailure.INTERNAL_ERROR, true, error, cancellationToken);
        }
    }

    /// <summary>A shutdown must not consume an attempt or leave the job waiting for its lease to expire.</summary>
    private async Task ReleaseOnShutdownAsync(IngestionWork work)
    {
        using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            await store.ReleaseAsync(work, clock.GetUtcNow(), grace.Token);
        }
        catch (Exception)
        {
            // Best effort: the lease still expires and the job is reclaimed.
        }
    }

    private async Task<IngestionOutcome> FailAsync(IngestionWork work, IngestionFailure reason, bool transient, Exception? error,
        CancellationToken cancellationToken)
    {
        var kind = await store.FailAsync(work, reason, transient, clock.GetUtcNow(), cancellationToken);
        return new(work.JobId, work.DocumentId, work.Attempt, kind, reason, error);
    }

    /// <summary>
    /// Bounds parser time for hostile PDFs or markup. A timed-out parse is a permanent content failure. The deadline also
    /// cancels the extractor, which lets an isolated extractor kill its parser process and release the CPU.
    /// </summary>
    private async Task<string> ExtractAsync(string contentType, string? charset, byte[] content, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ExtractionTimeout);
        try
        {
            return await Task.Run(() => extractor.ExtractAsync(contentType, charset, content, deadline.Token), CancellationToken.None)
                .WaitAsync(ExtractionTimeout, clock, cancellationToken);
        }
        catch (Exception error) when (error is TimeoutException
            || (error is OperationCanceledException && !cancellationToken.IsCancellationRequested))
        {
            throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false, error);
        }
        finally
        {
            // Whichever timer fired first, the extractor must see cancellation so an isolated parser is killed.
            await deadline.CancelAsync();
        }
    }

    private async Task<IngestionContent> LoadAsync(IngestionWork work, CancellationToken cancellationToken)
    {
        string contentType, text;
        string? sourceUrl = null;
        byte[] bytes;
        switch (work.Origin)
        {
            case KnowledgeDocumentOrigin.UPLOAD:
                contentType = work.ContentType ?? throw new IngestionException(IngestionFailure.UNSUPPORTED_CONTENT, false);
                bytes = await storage.ReadAsync(work.WorkspaceId, work.DocumentId, cancellationToken);
                text = await ExtractAsync(contentType, null, bytes, cancellationToken);
                break;
            case KnowledgeDocumentOrigin.TEXT:
                text = work.TextContent ?? throw new IngestionException(IngestionFailure.EMPTY_CONTENT, false);
                contentType = KnowledgeDocument.TextPlain;
                bytes = Encoding.UTF8.GetBytes(text);
                break;
            case KnowledgeDocumentOrigin.WEBSITE:
                var url = work.WebsiteUrl ?? throw new IngestionException(IngestionFailure.FETCH_FAILED, false);
                var page = await fetcher.FetchAsync(url, cancellationToken);
                (contentType, bytes, sourceUrl) = (page.ContentType, page.Body, page.FinalUrl);
                text = await ExtractAsync(page.ContentType, page.Charset, page.Body, cancellationToken);
                break;
            default:
                throw new IngestionException(IngestionFailure.UNSUPPORTED_CONTENT, false);
        }

        var normalized = KnowledgeChunker.Normalize(text);
        if (normalized.Length > KnowledgeChunker.MaxTextLength) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
        var chunks = KnowledgeChunker.Split(normalized);
        if (chunks.Count == 0) throw new IngestionException(IngestionFailure.EMPTY_CONTENT, false);
        return new(contentType, bytes.LongLength, Convert.ToHexStringLower(SHA256.HashData(bytes)), sourceUrl, normalized.Length, chunks);
    }
}
