using MAIC.Application.Identity;

namespace MAIC.Application.AI;

public enum AiBindingStatus { Success, Unauthenticated, Forbidden, NotFound, Invalid }
public sealed record AiBindingResult(AiBindingStatus Status, Guid? AgentId = null);
public interface IConversationAiBindingStore
{
    Task<AiBindingResult> BindAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid? agentId, CancellationToken ct);
}

public sealed class ConversationAiBindingService(ICurrentUser currentUser, IConversationAiBindingStore store)
{
    public Task<AiBindingResult> BindAsync(Guid workspaceId, Guid conversationId, Guid? agentId, CancellationToken ct)
    {
        if (currentUser.UserId is not { } actor) return Task.FromResult(new AiBindingResult(AiBindingStatus.Unauthenticated));
        if (workspaceId == Guid.Empty || conversationId == Guid.Empty || agentId == Guid.Empty)
            return Task.FromResult(new AiBindingResult(AiBindingStatus.Invalid));
        return store.BindAsync(actor, workspaceId, conversationId, agentId, ct);
    }
}
