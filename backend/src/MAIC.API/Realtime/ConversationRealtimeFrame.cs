namespace MAIC.API.Realtime;

public sealed record RealtimePresence(Guid UserId, bool IsTyping);

// Invalidation hints only. Durable values and actor identities are fetched through authorized REST.
public sealed record ConversationRealtimeFrame(
    Guid WorkspaceId,
    Guid ConversationId,
    bool ResyncRequired,
    long LastMessageSequence,
    long LastAssignmentSequence,
    long LastHandoffSequence,
    long Revision,
    IReadOnlyList<string> Changes,
    IReadOnlyList<RealtimePresence> Presence);
