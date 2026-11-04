using System.Security.Cryptography;
using System.Text;
using MAIC.Application.Identity;
using MAIC.Domain.Knowledge;

namespace MAIC.Application.Knowledge;

public enum KnowledgeDocumentStatusCode { Success, Unauthenticated, Invalid, NotFound, Conflict, TooLarge, QuotaExceeded }

/// <summary>
/// Per-workspace limits that keep one tenant from exhausting shared storage or worker capacity. Stored bytes count
/// uploads; documents count every non-superseded document. Bound from the <c>KnowledgeLimits</c> configuration section.
/// </summary>
public sealed class KnowledgeLimits
{
    public long MaxStorageBytesPerWorkspace { get; init; } = 1024L * 1024 * 1024;
    public int MaxDocumentsPerWorkspace { get; init; } = 5000;
    /// <summary>Jobs of one workspace running at once across all workers; further due jobs wait for other workspaces.</summary>
    public int MaxRunningJobsPerWorkspace { get; init; } = 2;
    /// <summary>Knowledge writes (uploads, ingestion requests, retries, deletions, source edits) per user per minute.</summary>
    public int WritesPerMinutePerUser { get; init; } = 60;

    /// <summary>Fails fast on a misconfiguration that would otherwise stop ingestion or reject every write.</summary>
    public KnowledgeLimits Validate()
    {
        if (MaxStorageBytesPerWorkspace < 1 || MaxDocumentsPerWorkspace < 1 || MaxRunningJobsPerWorkspace < 1 || WritesPerMinutePerUser < 1)
            throw new InvalidOperationException("KnowledgeLimits values must be positive.");
        return this;
    }
}

public sealed record IngestionJobView(Guid Id, string Status, int Attempts, int MaxAttempts, DateTimeOffset NextAttemptAt,
    string? LastError, DateTimeOffset CreatedAt, DateTimeOffset? CompletedAt);

/// <summary>Document state plus its most recent ingestion job. Never includes file bytes or source payloads.</summary>
public sealed record KnowledgeDocumentView(Guid Id, Guid WorkspaceId, Guid KnowledgeSourceId, string Origin, string Status,
    Guid CreatedByUserId, Guid ClientRequestId, string? FileName, string? ContentType, long? SizeBytes, string? ContentSha256,
    string? SourceUrl, int ChunkCount, int CharacterCount, string? FailureReason, DateTimeOffset CreatedAt,
    DateTimeOffset? ProcessedAt, IngestionJobView? LatestJob)
{
    /// <summary>Indexing in the configured embedding space; null for documents that are not READY.</summary>
    public KnowledgeIndexStatusView? Index { get; init; }
}

/// <summary>
/// DISABLED (no embedding provider), SOURCE_DISABLED, PENDING (waiting or retrying after a provider fault), INDEXING,
/// INDEXED (possibly with rejected chunks; also while such content is being rechecked) or FAILED (every chunk was rejected as content: EMBEDDING_REJECTED). Provider fault codes are
/// operator information and are not shown here.
/// </summary>
public sealed record KnowledgeIndexStatusView(string Status, int IndexedChunks, int RejectedChunks, string? Failure);

/// <summary>Reads index states of already authorized documents for the configured embedding space.</summary>
public interface IKnowledgeIndexStatusReader
{
    Task<IReadOnlyDictionary<Guid, KnowledgeIndexStatusView>> GetAsync(Guid workspaceId, IReadOnlyCollection<(Guid DocumentId,
        DateTimeOffset ProcessedAt)> readyDocuments, CancellationToken cancellationToken);
}

public sealed record KnowledgeChunkView(Guid Id, Guid WorkspaceId, Guid DocumentId, int Sequence, int StartOffset, string Content,
    DateTimeOffset CreatedAt);

public sealed record KnowledgeDocumentResult(KnowledgeDocumentStatusCode Status, KnowledgeDocumentView? Document = null,
    bool Replayed = false, IReadOnlyList<KnowledgeDocumentView>? Items = null, Guid? NextAfter = null,
    IReadOnlyList<KnowledgeChunkView>? Chunks = null, int? NextAfterSequence = null);

/// <summary>An upload whose bytes, media type and name were already validated.</summary>
public sealed record KnowledgeUpload(string FileName, string ContentType, ReadOnlyMemory<byte> Content, string ContentSha256);

public interface IKnowledgeDocumentStore
{
    /// <summary>Unlocked pre-check that the actor is a member and the source is a FILE source of that workspace.</summary>
    Task<KnowledgeDocumentStatusCode> CheckUploadTargetAsync(Guid actorId, Guid workspaceId, Guid sourceId, long? declaredBytes,
        CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> UploadAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid clientRequestId,
        KnowledgeUpload upload, CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> RequestIngestionAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid clientRequestId,
        CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> ListAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid? after, int take,
        CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> GetAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> RetryAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken);
    /// <summary>Deletes a document with its chunks and jobs; returns NotFound (404) for anything not visible to the actor.</summary>
    Task<KnowledgeDocumentResult> DeleteAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        CancellationToken cancellationToken);
    Task<KnowledgeDocumentResult> ListChunksAsync(Guid actorId, Guid workspaceId, Guid sourceId, Guid documentId,
        int afterSequence, int take, CancellationToken cancellationToken);
}

public sealed class KnowledgeDocumentService(ICurrentUser currentUser, IKnowledgeDocumentStore store, IKnowledgeIndexStatusReader indexStatus)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static Task<KnowledgeDocumentResult> Status(KnowledgeDocumentStatusCode status) => Task.FromResult(new KnowledgeDocumentResult(status));

    private Task<KnowledgeDocumentResult> Run(Func<Guid, Task<KnowledgeDocumentResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Status(KnowledgeDocumentStatusCode.Unauthenticated);

    /// <summary>
    /// Checks everything that does not need the body: authentication, membership and source (404 first, as for every
    /// route), then the request key, file name and declared media type. Callers read the body only after success.
    /// </summary>
    public Task<KnowledgeDocumentResult> PrepareUploadAsync(Guid workspaceId, Guid sourceId, Guid clientRequestId, string? fileName,
        string? contentTypeHeader, CancellationToken ct, long? declaredBytes = null) => Run(async actor =>
    {
        var target = workspaceId == Guid.Empty ? KnowledgeDocumentStatusCode.NotFound
            : await store.CheckUploadTargetAsync(actor, workspaceId, sourceId, declaredBytes, ct);
        if (target != KnowledgeDocumentStatusCode.Success) return new KnowledgeDocumentResult(target);
        return clientRequestId == Guid.Empty || !KnowledgeDocument.IsValidFileName(fileName)
            || ParseUploadContentType(contentTypeHeader) is null
            ? new KnowledgeDocumentResult(KnowledgeDocumentStatusCode.Invalid)
            : new KnowledgeDocumentResult(KnowledgeDocumentStatusCode.Success);
    });

    /// <summary>
    /// Validates and registers an upload. The declared media type must be text/plain, text/markdown (UTF-8 only) or
    /// application/pdf, and the bytes must match it: text must be strict UTF-8 without NUL, PDF must carry its header.
    /// </summary>
    public Task<KnowledgeDocumentResult> UploadAsync(Guid workspaceId, Guid sourceId, Guid clientRequestId, string? fileName,
        string? contentTypeHeader, ReadOnlyMemory<byte> content, CancellationToken ct) => Run(actor =>
    {
        if (content.Length > KnowledgeDocument.MaxUploadBytes) return Status(KnowledgeDocumentStatusCode.TooLarge);
        if (workspaceId == Guid.Empty || clientRequestId == Guid.Empty || !KnowledgeDocument.IsValidFileName(fileName)
            || ParseUploadContentType(contentTypeHeader) is not { } contentType || !MatchesContentType(contentType, content.Span))
            return Status(KnowledgeDocumentStatusCode.Invalid);
        var upload = new KnowledgeUpload(fileName!, contentType, content, Convert.ToHexStringLower(SHA256.HashData(content.Span)));
        return store.UploadAsync(actor, workspaceId, sourceId, clientRequestId, upload, ct);
    });

    public Task<KnowledgeDocumentResult> RequestIngestionAsync(Guid workspaceId, Guid sourceId, Guid clientRequestId,
        CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || clientRequestId == Guid.Empty
            ? Status(KnowledgeDocumentStatusCode.Invalid)
            : store.RequestIngestionAsync(actor, workspaceId, sourceId, clientRequestId, ct));

    public async Task<KnowledgeDocumentResult> ListAsync(Guid workspaceId, Guid sourceId, Guid? after, int take, CancellationToken ct) =>
        await WithIndexStatusAsync(await Run(actor => take is < 1 or > MaxPageSize || after == Guid.Empty
            ? Status(KnowledgeDocumentStatusCode.Invalid)
            : store.ListAsync(actor, workspaceId, sourceId, after, take, ct)), ct);

    public async Task<KnowledgeDocumentResult> GetAsync(Guid workspaceId, Guid sourceId, Guid documentId, CancellationToken ct) =>
        await WithIndexStatusAsync(await Run(actor => store.GetAsync(actor, workspaceId, sourceId, documentId, ct)), ct);

    /// <summary>Adds index status to documents the store already authorized and returned.</summary>
    private async Task<KnowledgeDocumentResult> WithIndexStatusAsync(KnowledgeDocumentResult result, CancellationToken ct)
    {
        var views = result.Items ?? (result.Document is { } single ? [single] : []);
        var ready = views.Where(v => v.Status == nameof(KnowledgeDocumentStatus.READY) && v.ProcessedAt is not null).ToArray();
        if (ready.Length == 0) return result;
        var states = await indexStatus.GetAsync(ready[0].WorkspaceId, ready.Select(v => (v.Id, v.ProcessedAt!.Value)).ToArray(), ct);
        KnowledgeDocumentView Enrich(KnowledgeDocumentView v) => states.TryGetValue(v.Id, out var state) ? v with { Index = state } : v;
        return result with { Document = result.Document is null ? null : Enrich(result.Document), Items = result.Items?.Select(Enrich).ToArray() };
    }

    public Task<KnowledgeDocumentResult> RetryAsync(Guid workspaceId, Guid sourceId, Guid documentId, CancellationToken ct) =>
        Run(actor => store.RetryAsync(actor, workspaceId, sourceId, documentId, ct));

    public Task<KnowledgeDocumentResult> DeleteAsync(Guid workspaceId, Guid sourceId, Guid documentId, CancellationToken ct) =>
        Run(actor => store.DeleteAsync(actor, workspaceId, sourceId, documentId, ct));

    public Task<KnowledgeDocumentResult> ListChunksAsync(Guid workspaceId, Guid sourceId, Guid documentId, int afterSequence,
        int take, CancellationToken ct) => Run(actor => take is < 1 or > MaxPageSize || afterSequence < 0
            ? Status(KnowledgeDocumentStatusCode.Invalid)
            : store.ListChunksAsync(actor, workspaceId, sourceId, documentId, afterSequence, take, ct));

    /// <summary>
    /// Returns the canonical media type for an allowed upload header, or null. Text types accept only a UTF-8 (or
    /// US-ASCII) charset parameter; other parameters are ignored. Matching is case-insensitive.
    /// </summary>
    public static string? ParseUploadContentType(string? header)
    {
        if (string.IsNullOrWhiteSpace(header)) return null;
        var parts = header.Split(';');
        var mediaType = parts[0].Trim().ToLowerInvariant();
        if (!KnowledgeDocument.IsUploadContentType(mediaType)) return null;
        foreach (var parameter in parts.Skip(1))
        {
            var separator = parameter.IndexOf('=');
            if (separator <= 0) return null;
            var name = parameter[..separator].Trim();
            var value = parameter[(separator + 1)..].Trim().Trim('"');
            if (name.Equals("charset", StringComparison.OrdinalIgnoreCase)
                && mediaType is not (KnowledgeDocument.ApplicationPdf or KnowledgeDocument.ApplicationDocx)
                && !value.Equals("utf-8", StringComparison.OrdinalIgnoreCase)
                && !value.Equals("us-ascii", StringComparison.OrdinalIgnoreCase)) return null;
        }
        return mediaType;
    }

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool MatchesContentType(string contentType, ReadOnlySpan<byte> content)
    {
        if (content.IsEmpty) return false;
        if (contentType == KnowledgeDocument.ApplicationPdf) return content.StartsWith("%PDF-"u8);
        if (contentType == KnowledgeDocument.ApplicationDocx) return content.StartsWith("PK\x03\x04"u8);
        if (content.Contains((byte)0)) return false;
        try
        {
            StrictUtf8.GetCharCount(content);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }
}
