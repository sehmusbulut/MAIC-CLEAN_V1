namespace MAIC.Domain.Conversations;

public enum HandoffState { PENDING, ACCEPTED, CANCELLED }
public enum HandoffTerminalReason { ACCEPTED, MANUAL_ASSIGNMENT, RESOLVED }

public sealed class HandoffRequest
{
    private HandoffRequest() { }

    public HandoffRequest(Guid workspaceId, Guid conversationId, Guid requesterUserId, Guid clientRequestId, long sequence, Guid? aiExecutionId = null)
    {
        if (workspaceId == Guid.Empty || conversationId == Guid.Empty || requesterUserId == Guid.Empty || clientRequestId == Guid.Empty)
            throw new ArgumentException("Handoff bindings and request key are required.");
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        WorkspaceId = workspaceId;
        ConversationId = conversationId;
        RequesterUserId = requesterUserId;
        ClientRequestId = clientRequestId;
        Sequence = sequence;
        if (aiExecutionId == Guid.Empty) throw new ArgumentException("Execution identifier must be valid.");
        AiExecutionId = aiExecutionId;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ConversationId { get; private set; }
    public Guid RequesterUserId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public long Sequence { get; private set; }
    public Guid? AiExecutionId { get; private set; }
    public HandoffState Status { get; private set; } = HandoffState.PENDING;
    public DateTimeOffset CreatedAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
    public Guid? TerminalActorUserId { get; private set; }
    public DateTimeOffset? TerminalAt { get; private set; }
    public HandoffTerminalReason? TerminalReason { get; private set; }

    public void Complete(Guid actorId, HandoffTerminalReason reason)
    {
        if (actorId == Guid.Empty || !Enum.IsDefined(reason)) throw new ArgumentException("Terminal actor and reason must be valid.");
        if (Status != HandoffState.PENDING) throw new InvalidOperationException("Handoff is already terminal.");
        Status = reason == HandoffTerminalReason.ACCEPTED ? HandoffState.ACCEPTED : HandoffState.CANCELLED;
        TerminalActorUserId = actorId;
        TerminalAt = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        TerminalReason = reason;
    }
}
