namespace MAIC.Domain.Knowledge;

/// <summary>How a document's content reaches the ingestion pipeline. Each origin belongs to exactly one source kind.</summary>
public enum KnowledgeDocumentOrigin
{
    UPLOAD,
    TEXT,
    WEBSITE
}

/// <summary>
/// PENDING waits for (or is in) ingestion; READY has chunks eligible for retrieval while its source is enabled;
/// FAILED carries a <see cref="IngestionFailure"/>; SUPERSEDED is an older TEXT/WEBSITE snapshot whose chunks were removed.
/// </summary>
public enum KnowledgeDocumentStatus
{
    PENDING,
    READY,
    FAILED,
    SUPERSEDED
}

/// <summary>Stable failure codes; they never contain provider messages, URLs or content.</summary>
public enum IngestionFailure
{
    SOURCE_DISABLED,
    UNSUPPORTED_CONTENT,
    INVALID_CONTENT,
    EMPTY_CONTENT,
    CONTENT_TOO_LARGE,
    FETCH_BLOCKED,
    FETCH_FAILED,
    STORAGE_MISSING,
    STORAGE_UNAVAILABLE,
    LEASE_EXPIRED,
    INTERNAL_ERROR,
    /// <summary>
    /// Indexing only (never a document or job state): the embedding provider refused every chunk of the document as
    /// content. Provider outages and misconfiguration never produce it.
    /// </summary>
    EMBEDDING_REJECTED
}

/// <summary>
/// One ingestible unit of a knowledge source. An uploaded file is one document of its FILE source; every TEXT or
/// WEBSITE ingestion request creates a new snapshot document, and a newer READY snapshot supersedes older ones.
/// File metadata of an upload is fixed at creation; TEXT/WEBSITE content metadata is recorded when processing succeeds.
/// </summary>
public sealed class KnowledgeDocument
{
    public const int MaxFileNameLength = 255;
    public const long MaxUploadBytes = 10 * 1024 * 1024;
    /// <summary>Manual retries per document; bounds how often hostile content can be re-queued.</summary>
    public const int MaxManualRetries = 3;
    public const string TextPlain = "text/plain";
    public const string TextMarkdown = "text/markdown";
    public const string TextHtml = "text/html";
    public const string ApplicationPdf = "application/pdf";
    public const string ApplicationDocx = "application/vnd.openxmlformats-officedocument.wordprocessingml.document";
    public const long MaxScaleUploadBytes = 50L * 1024 * 1024;

    private KnowledgeDocument() { }

    private KnowledgeDocument(Guid workspaceId, Guid sourceId, KnowledgeDocumentOrigin origin, Guid createdByUserId,
        Guid clientRequestId)
    {
        if (workspaceId == Guid.Empty || sourceId == Guid.Empty || createdByUserId == Guid.Empty || clientRequestId == Guid.Empty)
            throw new ArgumentException("Document bindings and request key are required.");
        if (!Enum.IsDefined(origin)) throw new ArgumentOutOfRangeException(nameof(origin));
        WorkspaceId = workspaceId;
        KnowledgeSourceId = sourceId;
        Origin = origin;
        CreatedByUserId = createdByUserId;
        ClientRequestId = clientRequestId;
    }

    /// <summary>Registers an uploaded file whose bytes were already validated and hashed.</summary>
    public static KnowledgeDocument ForUpload(Guid workspaceId, Guid sourceId, Guid createdByUserId, Guid clientRequestId,
        string fileName, string contentType, long sizeBytes, string contentSha256, long maxUploadBytes = MaxUploadBytes)
    {
        if (!IsValidFileName(fileName)) throw new ArgumentException("File name is invalid.", nameof(fileName));
        if (!IsUploadContentType(contentType)) throw new ArgumentException("Content type is not supported.", nameof(contentType));
        if (sizeBytes < 1 || sizeBytes > maxUploadBytes || sizeBytes > MaxScaleUploadBytes) throw new ArgumentOutOfRangeException(nameof(sizeBytes));
        if (!IsValidSha256(contentSha256)) throw new ArgumentException("SHA-256 must be 64 lowercase hex digits.", nameof(contentSha256));
        return new(workspaceId, sourceId, KnowledgeDocumentOrigin.UPLOAD, createdByUserId, clientRequestId)
        {
            FileName = fileName,
            ContentType = contentType,
            SizeBytes = sizeBytes,
            ContentSha256 = contentSha256
        };
    }

    /// <summary>Creates a TEXT or WEBSITE snapshot; its content is read when the worker processes it.</summary>
    public static KnowledgeDocument ForSnapshot(Guid workspaceId, Guid sourceId, KnowledgeSourceKind kind, Guid createdByUserId,
        Guid clientRequestId) => kind switch
    {
        KnowledgeSourceKind.TEXT => new(workspaceId, sourceId, KnowledgeDocumentOrigin.TEXT, createdByUserId, clientRequestId),
        KnowledgeSourceKind.WEBSITE => new(workspaceId, sourceId, KnowledgeDocumentOrigin.WEBSITE, createdByUserId, clientRequestId),
        _ => throw new ArgumentException("Only TEXT and WEBSITE sources are ingested as snapshots.", nameof(kind))
    };

    public static KnowledgeDocumentOrigin OriginFor(KnowledgeSourceKind kind) => kind switch
    {
        KnowledgeSourceKind.FILE => KnowledgeDocumentOrigin.UPLOAD,
        KnowledgeSourceKind.TEXT => KnowledgeDocumentOrigin.TEXT,
        KnowledgeSourceKind.WEBSITE => KnowledgeDocumentOrigin.WEBSITE,
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid KnowledgeSourceId { get; private set; }
    public KnowledgeDocumentOrigin Origin { get; private set; }
    public KnowledgeDocumentStatus Status { get; private set; } = KnowledgeDocumentStatus.PENDING;
    public Guid CreatedByUserId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public string? FileName { get; private set; }
    public string? ContentType { get; private set; }
    public long? SizeBytes { get; private set; }
    public string? ContentSha256 { get; private set; }
    public string? SourceUrl { get; private set; }
    public int ChunkCount { get; private set; }
    public int CharacterCount { get; private set; }
    public IngestionFailure? FailureReason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = KnowledgeClock.Truncate(DateTimeOffset.UtcNow);
    public DateTimeOffset? ProcessedAt { get; private set; }

    public static bool IsUploadContentType(string? contentType) =>
        contentType is TextPlain or TextMarkdown or ApplicationPdf or ApplicationDocx;

    public static bool IsValidSha256(string? value) =>
        value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// A display-only name: 1–255 UTF-16 units without controls, invisible format characters (such as bidi overrides),
    /// path separators, surrounding whitespace or dot names.
    /// </summary>
    public static bool IsValidFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName) || fileName.Length > MaxFileNameLength || fileName != fileName.Trim()
            || fileName is "." or "..") return false;
        for (var i = 0; i < fileName.Length; i++)
        {
            var c = fileName[i];
            if (char.IsControl(c) || c is '/' or '\\'
                || char.GetUnicodeCategory(fileName, i) == System.Globalization.UnicodeCategory.Format) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= fileName.Length || !char.IsLowSurrogate(fileName[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }

    /// <summary>Records a successful ingestion. TEXT/WEBSITE snapshots also record the content that was processed.</summary>
    public void MarkReady(int chunkCount, int characterCount, DateTimeOffset now, string? contentType = null, long? sizeBytes = null,
        string? contentSha256 = null, string? sourceUrl = null)
    {
        if (Status != KnowledgeDocumentStatus.PENDING) throw new InvalidOperationException("Only a pending document can become ready.");
        if (chunkCount < 1 || characterCount < 1) throw new ArgumentOutOfRangeException(nameof(chunkCount));
        if (Origin != KnowledgeDocumentOrigin.UPLOAD)
        {
            if (contentType is null || sizeBytes is null or < 0 || !IsValidSha256(contentSha256))
                throw new ArgumentException("Snapshot content metadata is required.");
            if (Origin == KnowledgeDocumentOrigin.WEBSITE != (sourceUrl is not null))
                throw new ArgumentException("Only website snapshots record a source URL.", nameof(sourceUrl));
            (ContentType, SizeBytes, ContentSha256, SourceUrl) = (contentType, sizeBytes, contentSha256, sourceUrl);
        }
        else if (contentType is not null || sizeBytes is not null || contentSha256 is not null || sourceUrl is not null)
            throw new ArgumentException("Upload metadata is fixed at registration.");
        Status = KnowledgeDocumentStatus.READY;
        ChunkCount = chunkCount;
        CharacterCount = characterCount;
        FailureReason = null;
        ProcessedAt = KnowledgeClock.Truncate(now);
    }

    public void MarkFailed(IngestionFailure reason, DateTimeOffset now)
    {
        if (!Enum.IsDefined(reason)) throw new ArgumentOutOfRangeException(nameof(reason));
        if (Status != KnowledgeDocumentStatus.PENDING) throw new InvalidOperationException("Only a pending document can fail.");
        Status = KnowledgeDocumentStatus.FAILED;
        FailureReason = reason;
        ProcessedAt = KnowledgeClock.Truncate(now);
    }

    /// <summary>Retires a TEXT/WEBSITE snapshot because a newer snapshot of the same source is READY.</summary>
    public void MarkSuperseded(DateTimeOffset now)
    {
        if (Origin == KnowledgeDocumentOrigin.UPLOAD) throw new InvalidOperationException("Uploads are never superseded.");
        if (Status is not (KnowledgeDocumentStatus.READY or KnowledgeDocumentStatus.PENDING))
            throw new InvalidOperationException("Only a ready or pending snapshot can be superseded.");
        Status = KnowledgeDocumentStatus.SUPERSEDED;
        FailureReason = null;
        ProcessedAt = KnowledgeClock.Truncate(now);
    }

    /// <summary>Returns a failed document to PENDING for a new ingestion job.</summary>
    public void Requeue()
    {
        if (Status != KnowledgeDocumentStatus.FAILED) throw new InvalidOperationException("Only a failed document can be retried.");
        Status = KnowledgeDocumentStatus.PENDING;
        FailureReason = null;
        ProcessedAt = null;
    }
}
