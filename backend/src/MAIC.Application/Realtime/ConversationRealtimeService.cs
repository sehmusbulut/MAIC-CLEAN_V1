namespace MAIC.Application.Realtime;

public sealed record RealtimeHandoffSnapshot(Guid Id, long Sequence, string Status,
    Guid? TerminalActorUserId, DateTimeOffset? TerminalAt, string? TerminalReason);

public sealed record ConversationRealtimeSnapshot(Guid WorkspaceId, Guid ConversationId,
    long LastMessageSequence, long LastAssignmentSequence, long LastHandoffSequence, long Revision,
    string Status, Guid? AssignedUserId, Guid? AiAgentId, RealtimeHandoffSnapshot? LatestHandoff);

public sealed record RealtimeReadResult(ConversationRealtimeSnapshot Snapshot, IReadOnlyList<Guid> AuthorizedUserIds);

public interface IConversationRealtimeStore
{
    Task<RealtimeReadResult?> ReadAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        IReadOnlyCollection<Guid> candidateUserIds, CancellationToken ct);
}

public sealed class ConversationRealtimeService(IConversationRealtimeStore store)
{
    public const int MaxCandidateUsers = 256;

    // The transport supplies the authenticated actor; persisted membership remains the store's authority.
    public Task<RealtimeReadResult?> ReadAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        IReadOnlyCollection<Guid> candidateUserIds, CancellationToken ct) =>
        actorId == Guid.Empty || workspaceId == Guid.Empty || conversationId == Guid.Empty
            || candidateUserIds.Count > MaxCandidateUsers || candidateUserIds.Contains(Guid.Empty)
            ? Task.FromResult<RealtimeReadResult?>(null)
            : store.ReadAsync(actorId, workspaceId, conversationId, candidateUserIds, ct);
}
