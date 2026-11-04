namespace MAIC.Domain.Conversations;

public enum AssignmentReason { MANUAL, HANDOFF_ACCEPTED, MEMBER_REMOVED }

public sealed class ConversationAssignment
{
    private ConversationAssignment() { }

    public ConversationAssignment(Guid workspaceId, Guid conversationId, long sequence, Guid? previousAssignedUserId,
        Guid? assignedUserId, Guid actorUserId, AssignmentReason reason)
    {
        if (workspaceId == Guid.Empty || conversationId == Guid.Empty || actorUserId == Guid.Empty
            || previousAssignedUserId == Guid.Empty || assignedUserId == Guid.Empty || !Enum.IsDefined(reason)
            || previousAssignedUserId == assignedUserId
            || reason == AssignmentReason.HANDOFF_ACCEPTED && assignedUserId != actorUserId
            || reason == AssignmentReason.MEMBER_REMOVED && assignedUserId is not null)
            throw new ArgumentException("Assignment history requires valid bindings and a changed assignee.");
        if (sequence <= 0) throw new ArgumentOutOfRangeException(nameof(sequence));
        WorkspaceId = workspaceId;
        ConversationId = conversationId;
        Sequence = sequence;
        PreviousAssignedUserId = previousAssignedUserId;
        AssignedUserId = assignedUserId;
        ActorUserId = actorUserId;
        Reason = reason;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ConversationId { get; private set; }
    public long Sequence { get; private set; }
    public Guid? PreviousAssignedUserId { get; private set; }
    public Guid? AssignedUserId { get; private set; }
    public Guid ActorUserId { get; private set; }
    public AssignmentReason Reason { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
}
