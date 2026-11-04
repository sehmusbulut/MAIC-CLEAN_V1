namespace MAIC.Domain.Knowledge;

/// <summary>
/// A contiguous span of a document's normalized text. Sequences start at 1 per document; StartOffset is the UTF-16
/// offset in the normalized text. Chunks are derived data: replaced on success and removed when superseded.
/// </summary>
public sealed class KnowledgeDocumentChunk
{
    private KnowledgeDocumentChunk() { }

    public KnowledgeDocumentChunk(Guid workspaceId, Guid documentId, int sequence, int startOffset, string content)
    {
        if (workspaceId == Guid.Empty || documentId == Guid.Empty) throw new ArgumentException("Chunk bindings are required.");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (startOffset < 0) throw new ArgumentOutOfRangeException(nameof(startOffset));
        if (string.IsNullOrWhiteSpace(content) || content.Length > KnowledgeChunker.MaxChunkLength)
            throw new ArgumentException("Chunk content must be nonblank and bounded.", nameof(content));
        WorkspaceId = workspaceId;
        DocumentId = documentId;
        Sequence = sequence;
        StartOffset = startOffset;
        Content = content;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid DocumentId { get; private set; }
    public int Sequence { get; private set; }
    public int StartOffset { get; private set; }
    public string Content { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; } = KnowledgeClock.Truncate(DateTimeOffset.UtcNow);
}
