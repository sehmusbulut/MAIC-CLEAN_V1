using MAIC.Application.Widget;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Widget;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace MAIC.Infrastructure.Persistence;

public sealed class WidgetSessionStore(MaicDbContext db, TimeProvider time, WidgetLimits limits) : IWidgetSessionStore
{
    private readonly WidgetLimits bounds = limits.Validate();

    public async Task<WidgetResult> StartAsync(string channelToken, string credentialHash, CancellationToken ct)
    {
        if (!GuestSession.IsValidCredentialHash(credentialHash)) return new(WidgetStatus.Unauthenticated);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var channel = await LockChannelAsync(channelToken, ct);
        if (channel is null) return new(WidgetStatus.NotFound);
        var guest = await LockGuestAsync(channel, credentialHash, ct);
        if (guest is not null)
        {
            var conversation = await LockConversationAsync(guest, ct);
            if (conversation is null || !guest.IsCurrent(time.GetUtcNow(), channel.GuestAccessGeneration))
                return new(WidgetStatus.Unauthenticated);
            return new(WidgetStatus.Success, Session: View(guest, conversation), Replayed: true);
        }
        // A digest is globally single-use, including retained expired/revoked/other-channel rows.
        if (await db.GuestSessions.AnyAsync(s => s.CredentialHash == credentialHash, ct))
            return new(WidgetStatus.Unauthenticated);
        var now = time.GetUtcNow();
        var since = now.AddHours(-24);
        var sessions = db.GuestSessions.Where(s => s.WorkspaceId == channel.WorkspaceId && s.ChannelId == channel.Id);
        if (await sessions.CountAsync(ct) >= bounds.SessionsPerChannel
            || await sessions.CountAsync(s => s.CreatedAt >= since, ct) >= bounds.SessionsPerChannelDay)
            return new(WidgetStatus.Limited);
        var contact = new Contact(channel.WorkspaceId, "Visitor", []);
        guest = new GuestSession(channel.WorkspaceId, channel.Id, contact.Id, credentialHash, channel.GuestAccessGeneration, now);
        var created = Conversation.CreateGuest(guest);
        if (channel.DefaultAiAgentId.HasValue)
        {
            created.BindAiAgent(channel.DefaultAiAgentId.Value);
        }
        db.AddRange(contact, guest, created);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(WidgetStatus.Success, Session: View(guest, created));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg)
        {
            await transaction.RollbackAsync(ct);
            db.Entry(created).State = EntityState.Detached;
            db.Entry(guest).State = EntityState.Detached;
            db.Entry(contact).State = EntityState.Detached;
            // Different workspaces can race the globally unique digest. Never expose or adopt the winner.
            return new(pg.ConstraintName == "IX_GuestSessions_CredentialHash" ? WidgetStatus.Unauthenticated : WidgetStatus.Conflict);
        }
    }

    public async Task<WidgetResult> GetAsync(string channelToken, string credentialHash, CancellationToken ct)
    {
        var access = await ReadAccess(channelToken, credentialHash, time.GetUtcNow()).SingleOrDefaultAsync(ct);
        return access is null ? new(WidgetStatus.NotFound)
            : access.Session is null || access.Conversation is null ? new(WidgetStatus.Unauthenticated)
            : new(WidgetStatus.Success, Session: View(access.Session, access.Conversation));
    }

    public async Task<WidgetResult> ListMessagesAsync(string channelToken, string credentialHash, long afterSequence, int take, CancellationToken ct)
    {
        if (afterSequence < 0 || take is < 1 or > 100) return new(WidgetStatus.Invalid);
        var access = await ReadAccess(channelToken, credentialHash, time.GetUtcNow()).AsSingleQuery()
            .Select(a => new
            {
                a.Session, a.Conversation,
                Messages = db.Messages.Where(m => a.Conversation != null && a.Session != null
                        && m.WorkspaceId == a.Conversation.WorkspaceId && m.ConversationId == a.Conversation.Id
                        && m.Sequence > afterSequence)
                    .OrderBy(m => m.Sequence).Take(take + 1)
                    .Select(m => new WidgetMessageView(m.Id,
                        m.SenderKind == MessageSenderKind.GUEST && m.SenderGuestSessionId == a.Session!.Id ? m.ClientMessageId : null,
                        m.Sequence, m.SenderKind.ToString(), m.Content, m.CreatedAt)).ToList()
            }).SingleOrDefaultAsync(ct);
        if (access is null) return new(WidgetStatus.NotFound);
        if (access.Session is null || access.Conversation is null) return new(WidgetStatus.Unauthenticated);
        return new(WidgetStatus.Success, Messages: access.Messages.Take(take).ToArray(),
            ConversationStatus: access.Conversation.Status.ToString(),
            NextAfterSequence: access.Messages.Count > take ? access.Messages[take - 1].Sequence : null);
    }

    public async Task<WidgetResult> RevokeAsync(string channelToken, string credentialHash, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var channel = await LockChannelAsync(channelToken, ct);
        if (channel is null) return new(WidgetStatus.NotFound);
        var guest = await LockGuestAsync(channel, credentialHash, ct);
        if (guest is null) return new(WidgetStatus.Unauthenticated);
        var conversation = await LockConversationAsync(guest, ct);
        var now = time.GetUtcNow();
        if (conversation is null || !guest.IsCurrent(now, channel.GuestAccessGeneration, allowRevoked: true))
            return new(WidgetStatus.Unauthenticated);
        var replayed = guest.RevokedAt is not null;
        guest.Revoke(now);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new(WidgetStatus.Success, Replayed: replayed);
    }

    public async Task<WidgetResult> AppendAsync(string channelToken, string credentialHash, Guid clientMessageId, string content, CancellationToken ct)
    {
        if (clientMessageId == Guid.Empty || !Message.IsValidContent(content)) return new(WidgetStatus.Invalid);
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var channel = await LockChannelAsync(channelToken, ct);
        if (channel is null) return new(WidgetStatus.NotFound);
        var guest = await LockGuestAsync(channel, credentialHash, ct);
        if (guest is null) return new(WidgetStatus.Unauthenticated);
        var conversation = await LockConversationAsync(guest, ct);
        // Refresh clock after any lock wait, including idempotent replays.
        if (conversation is null || !guest.IsCurrent(time.GetUtcNow(), channel.GuestAccessGeneration))
            return new(WidgetStatus.Unauthenticated);
        var existing = await db.Messages.AsNoTracking().SingleOrDefaultAsync(m => m.WorkspaceId == guest.WorkspaceId
            && m.ConversationId == conversation.Id && m.SenderGuestSessionId == guest.Id && m.ClientMessageId == clientMessageId, ct);
        if (existing is not null)
            return existing.Content == content ? new(WidgetStatus.Success, Message: View(existing), Replayed: true)
                : new(WidgetStatus.Conflict);
        if (conversation.Status != ConversationState.OPEN) return new(WidgetStatus.Conflict);
        if (await db.Messages.CountAsync(m => m.WorkspaceId == guest.WorkspaceId && m.ConversationId == conversation.Id
                && m.SenderKind == MessageSenderKind.GUEST, ct) >= bounds.GuestMessagesPerConversation)
            return new(WidgetStatus.Limited);
        var message = Message.CreateGuest(conversation, guest.Id, clientMessageId, conversation.AdvanceMessageSequence(), content);
        conversation.IncrementUnread(1, message.CreatedAt);
        db.Messages.Add(message);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new(WidgetStatus.Success, Message: View(message));
        }
        catch (DbUpdateException error) when (error.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await transaction.RollbackAsync(ct);
            db.Entry(message).State = EntityState.Detached;
            db.Entry(conversation).CurrentValues.SetValues(db.Entry(conversation).OriginalValues);
            db.Entry(conversation).State = EntityState.Unchanged;
            return new(WidgetStatus.Conflict);
        }
    }

    private sealed class Access
    {
        public GuestSession? Session { get; init; }
        public Conversation? Conversation { get; init; }
    }

    // One committed SQL statement authorizes and projects the complete read. Disabled/missing
    // channels yield no outer row; invalid credentials yield an outer row with no guest.
    private IQueryable<Access> ReadAccess(string token, string hash, DateTimeOffset now) =>
        from channel in db.Channels.AsNoTracking()
        where channel.PublishableToken == token && channel.Enabled && channel.Type == ChannelType.WEB
        from guest in db.GuestSessions.Where(s => s.WorkspaceId == channel.WorkspaceId && s.ChannelId == channel.Id
            && s.CredentialHash == hash && s.ChannelGeneration == channel.GuestAccessGeneration
            && s.ExpiresAt > now && s.RevokedAt == null).DefaultIfEmpty()
        from conversation in db.Conversations.Where(c => guest != null && c.WorkspaceId == guest.WorkspaceId
            && c.ChannelId == guest.ChannelId && c.ContactId == guest.ContactId && c.CreatedByGuestSessionId == guest.Id).DefaultIfEmpty()
        select new Access { Session = guest, Conversation = conversation };

    private async Task<Channel?> LockChannelAsync(string token, CancellationToken ct)
    {
        var workspaceId = await db.Channels.AsNoTracking().Where(c => c.PublishableToken == token)
            .Select(c => (Guid?)c.WorkspaceId).SingleOrDefaultAsync(ct);
        if (workspaceId is null) return null;
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId.Value} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        // Workspace lock serializes channel updates; use fresh persisted state, not a tracked channel.
        return await db.Channels.AsNoTracking().SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId.Value
            && c.PublishableToken == token && c.Enabled && c.Type == ChannelType.WEB, ct);
    }

    private async Task<GuestSession?> LockGuestAsync(Channel channel, string hash, CancellationToken ct)
    {
        var rows = await db.GuestSessions.FromSqlInterpolated($"""
            SELECT * FROM "GuestSessions" WHERE "WorkspaceId" = {channel.WorkspaceId} AND "ChannelId" = {channel.Id}
                AND "CredentialHash" = {hash} FOR UPDATE
            """).ToListAsync(ct);
        var guest = rows.SingleOrDefault();
        if (guest is not null) await db.Entry(guest).ReloadAsync(ct);
        return guest;
    }

    private async Task<Conversation?> LockConversationAsync(GuestSession guest, CancellationToken ct)
    {
        var rows = await db.Conversations.FromSqlInterpolated($"""
            SELECT * FROM "Conversations" WHERE "WorkspaceId" = {guest.WorkspaceId} AND "ChannelId" = {guest.ChannelId}
                AND "ContactId" = {guest.ContactId} AND "CreatedByGuestSessionId" = {guest.Id} FOR UPDATE
            """).ToListAsync(ct);
        var conversation = rows.SingleOrDefault();
        if (conversation is not null) await db.Entry(conversation).ReloadAsync(ct);
        return conversation;
    }

    private static WidgetSessionView View(GuestSession guest, Conversation conversation) =>
        new(conversation.Id, conversation.Status.ToString(), guest.ExpiresAt);
    private static WidgetMessageView View(Message message) => new(message.Id, message.ClientMessageId,
        message.Sequence, message.SenderKind.ToString(), message.Content, message.CreatedAt);
}
