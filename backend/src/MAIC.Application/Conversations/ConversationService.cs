using MAIC.Application.Identity;
using MAIC.Domain.Conversations;

namespace MAIC.Application.Conversations;

public enum ConversationResultStatus { Success, Unauthenticated, Invalid, NotFound, Conflict }

public sealed record ConversationView(
    Guid Id,
    Guid WorkspaceId,
    Guid ChannelId,
    Guid ContactId,
    Guid? CreatedByUserId,
    Guid ClientRequestId,
    string Status,
    DateTimeOffset CreatedAt,
    Guid? AssignedUserId = null,
    Guid? AiAgentId = null,
    string Mode = Conversation.ModeBotActive,
    DateTimeOffset? LastActivityAt = null,
    DateTimeOffset? ResolvedAt = null,
    DateTimeOffset? ArchivedAt = null,
    int UnreadCount = 0);

public sealed record MessageView(Guid Id, Guid WorkspaceId, Guid ConversationId, Guid? SenderUserId,
    Guid ClientMessageId, long Sequence, string Content, DateTimeOffset CreatedAt, string SenderKind = "MEMBER", Guid? AiExecutionId = null);
public sealed record AssignmentView(Guid Id, Guid WorkspaceId, Guid ConversationId, long Sequence,
    Guid? PreviousAssignedUserId, Guid? AssignedUserId, Guid ActorUserId, string Reason, DateTimeOffset CreatedAt);
public sealed record HandoffView(Guid Id, Guid WorkspaceId, Guid ConversationId, long Sequence, Guid RequesterUserId,
    Guid ClientRequestId, string Status, DateTimeOffset CreatedAt, Guid? TerminalActorUserId,
    DateTimeOffset? TerminalAt, string? TerminalReason, Guid? AiExecutionId = null);
public sealed record ConversationResult(ConversationResultStatus Status, ConversationView? Conversation = null,
    MessageView? Message = null, IReadOnlyList<ConversationView>? Items = null, IReadOnlyList<MessageView>? Messages = null,
    Guid? NextAfter = null, long? NextAfterSequence = null, bool Replayed = false,
    IReadOnlyList<AssignmentView>? Assignments = null, HandoffView? Handoff = null, IReadOnlyList<HandoffView>? Handoffs = null);

public interface IConversationStore
{
    Task<ConversationResult> CreateAsync(Guid actorId, Conversation conversation, CancellationToken cancellationToken);
    Task<ConversationResult> AppendAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid clientMessageId,
        string content, CancellationToken cancellationToken);
    Task<ConversationResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take, CancellationToken cancellationToken);
    Task<ConversationResult> GetAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> ListMessagesAsync(Guid actorId, Guid workspaceId, Guid conversationId, long afterSequence,
        int take, CancellationToken cancellationToken);
    Task<ConversationResult> AssignAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid? assignedUserId, CancellationToken cancellationToken);
    Task<ConversationResult> RequestHandoffAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid clientRequestId, CancellationToken cancellationToken);
    Task<ConversationResult> AcceptHandoffAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid handoffId, CancellationToken cancellationToken);
    Task<ConversationResult> ResolveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> ReopenAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> ArchiveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> UnarchiveAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> TakeoverAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> HandbackToAiAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken);
    Task<ConversationResult> ListAssignmentsAsync(Guid actorId, Guid workspaceId, Guid conversationId, long afterSequence, int take, CancellationToken cancellationToken);
    Task<ConversationResult> ListHandoffsAsync(Guid actorId, Guid workspaceId, Guid conversationId, long afterSequence, int take, CancellationToken cancellationToken);
}

public sealed class ConversationService(ICurrentUser currentUser, IConversationStore store)
{
    private Task<ConversationResult> Run(Func<Guid, Task<ConversationResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Task.FromResult(new ConversationResult(ConversationResultStatus.Unauthenticated));

    public Task<ConversationResult> CreateAsync(Guid workspaceId, Guid channelId, Guid contactId, Guid clientRequestId, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || channelId == Guid.Empty || contactId == Guid.Empty || clientRequestId == Guid.Empty
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.CreateAsync(actor, new Conversation(workspaceId, channelId, contactId, actor, clientRequestId), ct));

    public Task<ConversationResult> AppendAsync(Guid workspaceId, Guid conversationId, Guid clientMessageId, string? content, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || conversationId == Guid.Empty || clientMessageId == Guid.Empty || !Message.IsValidContent(content)
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.AppendAsync(actor, workspaceId, conversationId, clientMessageId, content!, ct));

    public Task<ConversationResult> ListAsync(Guid workspaceId, Guid? after, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > 100 || after == Guid.Empty
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.ListAsync(actor, workspaceId, after, take, ct));

    public Task<ConversationResult> GetAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.GetAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> ListMessagesAsync(Guid workspaceId, Guid conversationId, long afterSequence, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > 100 || afterSequence < 0
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.ListMessagesAsync(actor, workspaceId, conversationId, afterSequence, take, ct));

    public Task<ConversationResult> AssignAsync(Guid workspaceId, Guid conversationId, Guid? assignedUserId, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || conversationId == Guid.Empty || assignedUserId == Guid.Empty
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.AssignAsync(actor, workspaceId, conversationId, assignedUserId, ct));

    public Task<ConversationResult> RequestHandoffAsync(Guid workspaceId, Guid conversationId, Guid clientRequestId, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || conversationId == Guid.Empty || clientRequestId == Guid.Empty
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.RequestHandoffAsync(actor, workspaceId, conversationId, clientRequestId, ct));

    public Task<ConversationResult> AcceptHandoffAsync(Guid workspaceId, Guid conversationId, Guid handoffId, CancellationToken ct) => Run(actor =>
        workspaceId == Guid.Empty || conversationId == Guid.Empty || handoffId == Guid.Empty
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.AcceptHandoffAsync(actor, workspaceId, conversationId, handoffId, ct));

    public Task<ConversationResult> ResolveAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.ResolveAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> ReopenAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.ReopenAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> ArchiveAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.ArchiveAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> UnarchiveAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.UnarchiveAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> TakeoverAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.TakeoverAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> HandbackToAiAsync(Guid workspaceId, Guid conversationId, CancellationToken ct) => Run(actor =>
        store.HandbackToAiAsync(actor, workspaceId, conversationId, ct));

    public Task<ConversationResult> ListAssignmentsAsync(Guid workspaceId, Guid conversationId, long afterSequence, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > 100 || afterSequence < 0
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.ListAssignmentsAsync(actor, workspaceId, conversationId, afterSequence, take, ct));

    public Task<ConversationResult> ListHandoffsAsync(Guid workspaceId, Guid conversationId, long afterSequence, int take, CancellationToken ct) => Run(actor =>
        take is < 1 or > 100 || afterSequence < 0
            ? Task.FromResult(new ConversationResult(ConversationResultStatus.Invalid))
            : store.ListHandoffsAsync(actor, workspaceId, conversationId, afterSequence, take, ct));
}
