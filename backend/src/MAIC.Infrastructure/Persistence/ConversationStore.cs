using MAIC.Application.Conversations;
using MAIC.Domain.Channels;
using MAIC.Domain.Conversations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed partial class ConversationStore(MaicDbContext db) : IConversationStore
{
    private async Task<bool> AuthorizeWriteAsync(Guid actorId, Guid workspaceId, CancellationToken ct, Guid? targetId = null)
    {
        // Match membership and channel writers: workspace -> actor membership -> conversation.
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        var members = await db.WorkspaceMembers.FromSqlInterpolated($"""
            SELECT * FROM "WorkspaceMembers" WHERE "WorkspaceId" = {workspaceId}
                AND ("UserId" = {actorId} OR "UserId" = {targetId}) ORDER BY "Id" FOR UPDATE
            """).AsNoTracking().ToListAsync(ct);
        return members.Any(m => m.UserId == actorId) && (targetId is null || members.Any(m => m.UserId == targetId));
    }

    public async Task<ConversationResult> CreateAsync(Guid actorId, Conversation conversation, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, conversation.WorkspaceId, cancellationToken))
            return new(ConversationResultStatus.NotFound);
        if (conversation.CreatedByUserId != actorId) return new(ConversationResultStatus.Invalid);

        var existing = await db.Conversations.AsNoTracking().SingleOrDefaultAsync(c => c.WorkspaceId == conversation.WorkspaceId
            && c.CreatedByUserId == actorId && c.ClientRequestId == conversation.ClientRequestId, cancellationToken);
        if (existing is not null)
            return existing.ChannelId == conversation.ChannelId && existing.ContactId == conversation.ContactId
                ? new(ConversationResultStatus.Success, Conversation: View(existing), Replayed: true)
                : new(ConversationResultStatus.Conflict);

        var channel = await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.WorkspaceId == conversation.WorkspaceId
            && c.Id == conversation.ChannelId && c.Type == ChannelType.WEB, cancellationToken);
        if (channel is null || !await db.Contacts.AnyAsync(c => c.WorkspaceId == conversation.WorkspaceId
            && c.Id == conversation.ContactId, cancellationToken)) return new(ConversationResultStatus.NotFound);
        if (!channel.Enabled) return new(ConversationResultStatus.Conflict);

        db.Conversations.Add(conversation);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ConversationResultStatus.Success, Conversation: View(conversation));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.Entry(conversation).State = EntityState.Detached;
            return new(ConversationResultStatus.Conflict);
        }
    }

    public async Task<ConversationResult> AppendAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        Guid clientMessageId, string content, CancellationToken cancellationToken)
    {
        if (clientMessageId == Guid.Empty || !Message.IsValidContent(content)) return new(ConversationResultStatus.Invalid);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        if (!await AuthorizeWriteAsync(actorId, workspaceId, cancellationToken)) return new(ConversationResultStatus.NotFound);
        var conversations = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM "Conversations" WHERE "WorkspaceId" = {workspaceId} AND "Id" = {conversationId} FOR UPDATE
            """).ToListAsync(cancellationToken);
        var conversation = conversations.SingleOrDefault();
        if (conversation is null) return new(ConversationResultStatus.NotFound);
        // A scoped store may be reused. Refresh any previously tracked counter while holding its lock.
        await db.Entry(conversation).ReloadAsync(cancellationToken);
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId
            && m.ConversationId == conversationId && m.SenderUserId == actorId && m.ClientMessageId == clientMessageId, cancellationToken);
        if (existing is not null)
            return existing.Content == content
                ? new(ConversationResultStatus.Success, Message: View(existing), Replayed: true)
                : new(ConversationResultStatus.Conflict);
        if (conversation.Status != ConversationState.OPEN) return new(ConversationResultStatus.Conflict);
        if (!await db.Channels.AnyAsync(c => c.WorkspaceId == workspaceId && c.Id == conversation.ChannelId
            && c.Type == ChannelType.WEB && c.Enabled, cancellationToken)) return new(ConversationResultStatus.Conflict);

        var message = new Message(workspaceId, conversationId, actorId, clientMessageId, conversation.AdvanceMessageSequence(), content);
        db.Messages.Add(message);

        // Step 3: Team member manual reply switches conversation to HUMAN_ACTIVE, clears unread, and accepts pending handoffs
        conversation.SetMode(Conversation.ModeHumanActive, message.CreatedAt);
        conversation.ClearUnread();
        if (conversation.CreatedByGuestSessionId is not null || conversation.Mode == Conversation.ModePendingHandoff)
        {
            AddAssignment(conversation.Takeover(actorId));
        }
        await CompletePendingAsAcceptedAsync(conversation, actorId, cancellationToken);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new(ConversationResultStatus.Success, Message: View(message));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // The transaction rolls back both insert and counter. Also discard pending tracked changes.
            db.Entry(message).State = EntityState.Detached;
            db.Entry(conversation).CurrentValues.SetValues(db.Entry(conversation).OriginalValues);
            db.Entry(conversation).State = EntityState.Unchanged;
            return new(ConversationResultStatus.Conflict);
        }
    }

    public async Task<ConversationResult> ListAsync(Guid actorId, Guid workspaceId, Guid? after, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100 || after == Guid.Empty) return new(ConversationResultStatus.Invalid);
        var cursor = after.GetValueOrDefault();
        var result = await db.WorkspaceMembers.AsNoTracking().AsSingleQuery()
            .Where(actor => actor.WorkspaceId == workspaceId && actor.UserId == actorId)
            .Select(actor => new
            {
                Items = db.Conversations.Where(c => c.WorkspaceId == workspaceId && c.WorkspaceId == actor.WorkspaceId
                        && (after == null || c.Id.CompareTo(cursor) > 0))
                    .OrderBy(c => c.Id).Take(take + 1).Select(c => new ConversationView(
                        c.Id,
                        c.WorkspaceId,
                        c.ChannelId,
                        c.ContactId,
                        c.CreatedByUserId,
                        c.ClientRequestId,
                        c.Status.ToString(),
                        c.CreatedAt,
                        c.AssignedUserId,
                        c.AiAgentId,
                        c.Mode,
                        c.LastActivityAt,
                        c.ResolvedAt,
                        c.ArchivedAt,
                        c.UnreadCount)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (result is null) return new(ConversationResultStatus.NotFound);
        return new(ConversationResultStatus.Success, Items: result.Items.Take(take).ToArray(),
            NextAfter: result.Items.Count > take ? result.Items[take - 1].Id : null);
    }

    public async Task<ConversationResult> GetAsync(Guid actorId, Guid workspaceId, Guid conversationId, CancellationToken cancellationToken)
    {
        var result = await db.Conversations.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new ConversationView(
                c.Id,
                c.WorkspaceId,
                c.ChannelId,
                c.ContactId,
                c.CreatedByUserId,
                c.ClientRequestId,
                c.Status.ToString(),
                c.CreatedAt,
                c.AssignedUserId,
                c.AiAgentId,
                c.Mode,
                c.LastActivityAt,
                c.ResolvedAt,
                c.ArchivedAt,
                c.UnreadCount)).SingleOrDefaultAsync(cancellationToken);
        return result is null ? new(ConversationResultStatus.NotFound) : new(ConversationResultStatus.Success, Conversation: result);
    }

    public async Task<ConversationResult> ListMessagesAsync(Guid actorId, Guid workspaceId, Guid conversationId,
        long afterSequence, int take, CancellationToken cancellationToken)
    {
        if (take is < 1 or > 100 || afterSequence < 0) return new(ConversationResultStatus.Invalid);
        var result = await db.Conversations.AsNoTracking().AsSingleQuery()
            .Where(c => c.WorkspaceId == workspaceId && c.Id == conversationId
                && db.WorkspaceMembers.Any(m => m.WorkspaceId == c.WorkspaceId && m.UserId == actorId))
            .Select(c => new
            {
                Items = db.Messages.Where(m => m.WorkspaceId == workspaceId && m.WorkspaceId == c.WorkspaceId
                        && m.ConversationId == c.Id && m.Sequence > afterSequence)
                    .OrderBy(m => m.Sequence).Take(take + 1).Select(m => new MessageView(m.Id, m.WorkspaceId, m.ConversationId,
                        m.SenderUserId, m.ClientMessageId, m.Sequence, m.Content, m.CreatedAt, m.SenderKind.ToString(), m.AiExecutionId)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        if (result is null) return new(ConversationResultStatus.NotFound);
        return new(ConversationResultStatus.Success, Messages: result.Items.Take(take).ToArray(),
            NextAfterSequence: result.Items.Count > take ? result.Items[take - 1].Sequence : null);
    }

    private static ConversationView View(Conversation c) => new(
        c.Id,
        c.WorkspaceId,
        c.ChannelId,
        c.ContactId,
        c.CreatedByUserId,
        c.ClientRequestId,
        c.Status.ToString(),
        c.CreatedAt,
        c.AssignedUserId,
        c.AiAgentId,
        c.Mode,
        c.LastActivityAt,
        c.ResolvedAt,
        c.ArchivedAt,
        c.UnreadCount);

    private static MessageView View(Message m) => new(m.Id, m.WorkspaceId, m.ConversationId, m.SenderUserId,
        m.ClientMessageId, m.Sequence, m.Content, m.CreatedAt, m.SenderKind.ToString(), m.AiExecutionId);
}
