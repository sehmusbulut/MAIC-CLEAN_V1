namespace MAIC.Domain.Conversations;

public enum MessageSenderKind { MEMBER, AI, GUEST }

public sealed class Message
{
    private Message() { }

    public Message(Guid workspaceId, Guid conversationId, Guid senderUserId, Guid clientMessageId, long sequence, string content)
    {
        if (workspaceId == Guid.Empty || conversationId == Guid.Empty || senderUserId == Guid.Empty || clientMessageId == Guid.Empty)
            throw new ArgumentException("Message bindings and request key are required.");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!IsValidContent(content)) throw new ArgumentException("Message content must be valid nonblank text of at most 4000 UTF-16 characters.", nameof(content));
        WorkspaceId = workspaceId;
        ConversationId = conversationId;
        SenderUserId = senderUserId;
        ClientMessageId = clientMessageId;
        Sequence = sequence;
        Content = content;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid? SenderUserId { get; private set; }
    public MessageSenderKind SenderKind { get; private set; } = MessageSenderKind.MEMBER;
    public Guid? AiExecutionId { get; private set; }
    public Guid? SenderGuestSessionId { get; private set; }
    public Guid ClientMessageId { get; private set; }
    public long Sequence { get; private set; }
    public string Content { get; private set; } = null!;
    public DateTimeOffset CreatedAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);

    public static Message CreateAi(Guid workspaceId, Guid conversationId, Guid executionId, long sequence, string content)
    {
        if (workspaceId == Guid.Empty || conversationId == Guid.Empty || executionId == Guid.Empty)
            throw new ArgumentException("AI message provenance is required.");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!IsValidContent(content)) throw new ArgumentException("Invalid AI message content.", nameof(content));
        return new Message { WorkspaceId = workspaceId, ConversationId = conversationId, SenderKind = MessageSenderKind.AI,
            AiExecutionId = executionId, ClientMessageId = executionId, Sequence = sequence, Content = content };
    }

    public static Message CreateGuest(Conversation conversation, Guid guestSessionId, Guid clientMessageId, long sequence, string content)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        if (guestSessionId == Guid.Empty || conversation.CreatedByGuestSessionId != guestSessionId || clientMessageId == Guid.Empty)
            throw new ArgumentException("Guest message must belong to the conversation creator.");
        if (sequence < 1) throw new ArgumentOutOfRangeException(nameof(sequence));
        if (!IsValidContent(content)) throw new ArgumentException("Invalid guest message content.", nameof(content));
        return new Message { WorkspaceId = conversation.WorkspaceId, ConversationId = conversation.Id,
            SenderKind = MessageSenderKind.GUEST, SenderGuestSessionId = guestSessionId,
            ClientMessageId = clientMessageId, Sequence = sequence, Content = content };
    }

    public static bool IsValidContent(string? content)
    {
        if (string.IsNullOrWhiteSpace(content) || content.Length > 4000) return false;
        for (var i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (char.IsControl(c) && c is not ('\r' or '\n' or '\t')) return false;
            if (char.IsHighSurrogate(c))
            {
                if (++i >= content.Length || !char.IsLowSurrogate(content[i])) return false;
            }
            else if (char.IsLowSurrogate(c)) return false;
        }
        return true;
    }
}
