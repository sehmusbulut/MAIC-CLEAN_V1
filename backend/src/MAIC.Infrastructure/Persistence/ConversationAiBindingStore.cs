using MAIC.Application.AI;
using MAIC.Domain.Workspaces;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed class ConversationAiBindingStore(MaicDbContext db) : IConversationAiBindingStore
{
    public async Task<AiBindingResult> BindAsync(Guid actorId, Guid workspaceId, Guid conversationId, Guid? agentId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var member = (await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId} AND "UserId" = {actorId} FOR UPDATE
            """).AsNoTracking().ToListAsync(ct)).SingleOrDefault();
        if (member is null) return new(AiBindingStatus.NotFound);
        if (member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN)) return new(AiBindingStatus.Forbidden);
        var conversation = (await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM "Conversations" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {conversationId} FOR UPDATE
            """).ToListAsync(ct)).SingleOrDefault();
        if (conversation is null) return new(AiBindingStatus.NotFound);
        await db.Entry(conversation).ReloadAsync(ct);
        if (agentId is not null && !await db.AiAgents.AnyAsync(a => a.WorkspaceId == workspaceId && a.Id == agentId, ct))
            return new(AiBindingStatus.NotFound);
        conversation.BindAiAgent(agentId);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(AiBindingStatus.Success, agentId);
    }
}
