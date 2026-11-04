using MAIC.Domain.Widget;

namespace MAIC.Domain.Conversations;

public enum ConversationState { OPEN, RESOLVED, ARCHIVED }

public sealed class Conversation
{
    public const string ModeBotActive = "BOT_ACTIVE";
    public const string ModePendingHandoff = "PENDING_HANDOFF";
    public const string ModeHumanActive = "HUMAN_ACTIVE";

    private Conversation() { }

    public Conversation(Guid workspaceId, Guid channelId, Guid contactId, Guid createdByUserId, Guid clientRequestId)
    {
        if (workspaceId == Guid.Empty || channelId == Guid.Empty || contactId == Guid.Empty
            || createdByUserId == Guid.Empty || clientRequestId == Guid.Empty)
            throw new ArgumentException("Conversation bindings and request key are required.");
        WorkspaceId = workspaceId;
        ChannelId = channelId;
        ContactId = contactId;
        CreatedByUserId = createdByUserId;
        ClientRequestId = clientRequestId;
        LastActivityAt = CreatedAt;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid ContactId { get; private set; }
    public Guid? CreatedByUserId { get; private set; }
    public Guid? CreatedByGuestSessionId { get; private set; }
    public Guid ClientRequestId { get; private set; }
    public ConversationState Status { get; private set; } = ConversationState.OPEN;
    public string Mode { get; private set; } = ModeBotActive;
    public DateTimeOffset CreatedAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
    public DateTimeOffset LastActivityAt { get; private set; } = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
    public DateTimeOffset? ResolvedAt { get; private set; }
    public DateTimeOffset? ArchivedAt { get; private set; }
    public int UnreadCount { get; private set; } = 0;
    public long LastMessageSequence { get; private set; }
    public Guid? AssignedUserId { get; private set; }
    public long LastAssignmentSequence { get; private set; }
    public long LastHandoffSequence { get; private set; }
    public Guid? AiAgentId { get; private set; }
    public long AiBindingRevision { get; private set; }

    public static Conversation CreateGuest(GuestSession session)
    {
        ArgumentNullException.ThrowIfNull(session);
        return new Conversation
        {
            WorkspaceId = session.WorkspaceId,
            ChannelId = session.ChannelId,
            ContactId = session.ContactId,
            CreatedByGuestSessionId = session.Id,
            ClientRequestId = session.Id,
            CreatedAt = session.CreatedAt,
            LastActivityAt = session.CreatedAt,
            Mode = ModeBotActive
        };
    }

    public bool BindAiAgent(Guid? agentId)
    {
        if (agentId == Guid.Empty) throw new ArgumentException("Agent identifier must be valid.", nameof(agentId));
        if (AiAgentId == agentId) return false;
        AiAgentId = agentId;
        AiBindingRevision = checked(AiBindingRevision + 1);
        return true;
    }

    public long AdvanceMessageSequence() => LastMessageSequence = checked(LastMessageSequence + 1);

    public void TouchActivity(DateTimeOffset? now = null)
    {
        LastActivityAt = Truncate(now ?? DateTimeOffset.UtcNow);
    }

    public void IncrementUnread(int count = 1, DateTimeOffset? now = null)
    {
        if (count > 0) UnreadCount += count;
        TouchActivity(now);
    }

    public void ClearUnread()
    {
        UnreadCount = 0;
    }

    public void SetMode(string mode, DateTimeOffset? now = null)
    {
        if (!string.IsNullOrWhiteSpace(mode))
        {
            Mode = mode.Trim().ToUpperInvariant();
        }
        TouchActivity(now);
    }

    public ConversationAssignment? Assign(Guid? userId, Guid actorId, AssignmentReason reason)
    {
        if (userId == Guid.Empty || actorId == Guid.Empty || !Enum.IsDefined(reason))
            throw new ArgumentException("Assignment identifiers and reason must be valid.");
        TouchActivity();
        Mode = userId.HasValue ? ModeHumanActive : ModeBotActive;
        if (AssignedUserId == userId) return null;
        var history = new ConversationAssignment(WorkspaceId, Id, checked(LastAssignmentSequence + 1),
            AssignedUserId, userId, actorId, reason);
        AssignedUserId = userId;
        LastAssignmentSequence = history.Sequence;
        return history;
    }

    public ConversationAssignment? Takeover(Guid actorId)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("Actor identifier is required.", nameof(actorId));
        var assignment = Assign(actorId, actorId, AssignmentReason.MANUAL);
        Mode = ModeHumanActive;
        UnreadCount = 0;
        TouchActivity();
        return assignment;
    }

    public ConversationAssignment? HandbackToAi(Guid actorId)
    {
        if (actorId == Guid.Empty) throw new ArgumentException("Actor identifier is required.", nameof(actorId));
        var assignment = Assign(null, actorId, AssignmentReason.MANUAL);
        Mode = ModeBotActive;
        TouchActivity();
        return assignment;
    }

    public HandoffRequest RequestHandoff(Guid actorId, Guid clientRequestId, Guid? aiExecutionId = null)
    {
        if (Status != ConversationState.OPEN) throw new InvalidOperationException("Handoff requires an open conversation.");
        var request = new HandoffRequest(WorkspaceId, Id, actorId, clientRequestId, checked(LastHandoffSequence + 1), aiExecutionId);
        LastHandoffSequence = request.Sequence;
        Mode = ModePendingHandoff;
        TouchActivity();
        return request;
    }

    public bool Resolve()
    {
        if (Status == ConversationState.RESOLVED) return false;
        var now = Truncate(DateTimeOffset.UtcNow);
        Status = ConversationState.RESOLVED;
        ResolvedAt = now;
        ArchivedAt = null;
        UnreadCount = 0;
        LastActivityAt = now;
        AiBindingRevision = checked(AiBindingRevision + 1);
        return true;
    }

    public bool Reopen()
    {
        if (Status == ConversationState.OPEN) return false;
        var now = Truncate(DateTimeOffset.UtcNow);
        Status = ConversationState.OPEN;
        ResolvedAt = null;
        ArchivedAt = null;
        LastActivityAt = now;
        AiBindingRevision = checked(AiBindingRevision + 1);
        return true;
    }

    public bool Archive()
    {
        if (Status == ConversationState.ARCHIVED) return false;
        var now = Truncate(DateTimeOffset.UtcNow);
        Status = ConversationState.ARCHIVED;
        ResolvedAt ??= now;
        ArchivedAt = now;
        UnreadCount = 0;
        LastActivityAt = now;
        AiBindingRevision = checked(AiBindingRevision + 1);
        return true;
    }

    public bool Unarchive()
    {
        if (Status != ConversationState.ARCHIVED) return false;
        var now = Truncate(DateTimeOffset.UtcNow);
        Status = ConversationState.OPEN;
        ResolvedAt = null;
        ArchivedAt = null;
        LastActivityAt = now;
        AiBindingRevision = checked(AiBindingRevision + 1);
        return true;
    }

    private static DateTimeOffset Truncate(DateTimeOffset timestamp) =>
        new(timestamp.UtcTicks / 10 * 10, TimeSpan.Zero);
}
