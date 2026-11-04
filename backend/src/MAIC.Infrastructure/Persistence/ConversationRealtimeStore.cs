using MAIC.Application.Realtime;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed class ConversationRealtimeStore(MaicDbContext db) : IConversationRealtimeStore
{
    public async Task<RealtimeReadResult?> ReadAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        IReadOnlyCollection<Guid> candidateUserIds, CancellationToken ct)
    {
        if (actorId == Guid.Empty || workspaceId == Guid.Empty || conversationId == Guid.Empty
            || candidateUserIds.Count > ConversationRealtimeService.MaxCandidateUsers || candidateUserIds.Contains(Guid.Empty))
            return null;

        // One committed database snapshot covers authorization, metadata and the candidate audience.
        // No tracked entities or message bodies are materialized, and polling never acquires write locks.
        var result = await db.Conversations.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new
            {
                c.WorkspaceId,
                ConversationId = c.Id,
                c.LastMessageSequence,
                c.LastAssignmentSequence,
                c.LastHandoffSequence,
                Revision = c.AiBindingRevision,
                c.Status,
                c.AssignedUserId,
                c.AiAgentId,
                LatestHandoffs = db.HandoffRequests
                    .Where(h => h.WorkspaceId == workspaceId && h.WorkspaceId == c.WorkspaceId && h.ConversationId == c.Id)
                    .OrderByDescending(h => h.Sequence).Take(1)
                    .Select(h => new RealtimeHandoffSnapshot(h.Id, h.Sequence, h.Status.ToString(),
                        h.TerminalActorUserId, h.TerminalAt, h.TerminalReason == null ? null : h.TerminalReason.ToString()))
                    .ToList(),
                AuthorizedUserIds = db.WorkspaceMembers
                    .Where(m => m.WorkspaceId == workspaceId && m.WorkspaceId == c.WorkspaceId && candidateUserIds.Contains(m.UserId))
                    .OrderBy(m => m.UserId).Select(m => m.UserId).ToList()
            }).SingleOrDefaultAsync(ct);

        return result is null ? null : new RealtimeReadResult(new ConversationRealtimeSnapshot(
            result.WorkspaceId, result.ConversationId, result.LastMessageSequence, result.LastAssignmentSequence,
            result.LastHandoffSequence, result.Revision, result.Status.ToString(), result.AssignedUserId, result.AiAgentId,
            result.LatestHandoffs.SingleOrDefault()), result.AuthorizedUserIds);
    }
}
