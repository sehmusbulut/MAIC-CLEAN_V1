using System.Text.Json.Serialization;
using MAIC.API.Channels;
using MAIC.Application.Conversations;
using MAIC.Application.Identity;
using MAIC.Domain.Conversations;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Conversations;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConversationCreateRequest(Guid ChannelId, Guid ContactId, Guid ClientRequestId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record MessageCreateRequest(Guid? ClientMessageId, string? Content);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AssignmentRequest
{
    public required Guid? AssignedUserId { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HandoffCreateRequest(Guid ClientRequestId);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConversationActionRequest;

public sealed record UpdateConversationContactRequest(
    string? DisplayName,
    string? Email,
    string? Phone,
    string? Notes,
    string[]? Tags);

public sealed class AutoArchiveSweepService(
    IServiceScopeFactory scopeFactory,
    ILogger<AutoArchiveSweepService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<MaicDbContext>();
                await ConversationEndpoints.SweepAutoArchiveAsync(db, null, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "AutoArchiveSweepService iteration skipped.");
            }

            if (!await timer.WaitForNextTickAsync(stoppingToken))
            {
                break;
            }
        }
    }
}

public static class ConversationEndpoints
{
    public static async Task SweepAutoArchiveAsync(
        MaicDbContext db,
        Guid? workspaceId,
        CancellationToken cancellationToken)
    {
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        var cutoff = now.AddHours(-6);

        var query = db.Conversations.Where(c =>
            c.Status == ConversationState.RESOLVED
            && c.ResolvedAt != null
            && c.ResolvedAt <= cutoff);

        if (workspaceId.HasValue)
        {
            query = query.Where(c => c.WorkspaceId == workspaceId.Value);
        }

        await query.ExecuteUpdateAsync(s => s
            .SetProperty(c => c.Status, ConversationState.ARCHIVED)
            .SetProperty(c => c.ArchivedAt, now)
            .SetProperty(c => c.UnreadCount, 0), cancellationToken);
    }

    public static void MapConversationEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/conversations").RequireAuthorization();
        group.AddEndpointFilter(async (context, next) =>
        {
            context.HttpContext.Response.Headers.CacheControl = "no-store";
            if (!HttpMethods.IsGet(context.HttpContext.Request.Method) && !HttpMethods.IsHead(context.HttpContext.Request.Method))
            {
                // Enforce strict antiforgery when X-Test-User or X-CSRF-TOKEN is supplied
                if (context.HttpContext.Request.Headers.ContainsKey("X-Test-User")
                    || context.HttpContext.Request.Headers.ContainsKey("X-CSRF-TOKEN"))
                {
                    try
                    {
                        await context.HttpContext.RequestServices.GetRequiredService<IAntiforgery>()
                            .ValidateRequestAsync(context.HttpContext);
                    }
                    catch (AntiforgeryValidationException)
                    {
                        return Results.BadRequest();
                    }
                }
            }
            return await next(context);
        });

        group.MapPost("", async (Guid workspaceId, ConversationCreateRequest request, ConversationService service, CancellationToken ct) =>
        {
            var result = await service.CreateAsync(workspaceId, request.ChannelId, request.ContactId, request.ClientRequestId, ct);
            return result.Status == ConversationResultStatus.Success && !result.Replayed
                ? Results.Created($"/workspaces/{workspaceId}/conversations/{result.Conversation!.Id}", result.Conversation) : Map(result);
        });

        // 4. GET /workspaces/{workspaceId}/conversations (Enriched Inbox with Tabs, Search, SortBy, GroupBy + 6h Lazy Auto-Archive)
        group.MapGet("", async (
            Guid workspaceId,
            Guid? after,
            int? take,
            string? tab,
            string? search,
            string? sortBy,
            string? groupBy,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } actorId)
            {
                return Results.Unauthorized();
            }

            var effectiveTake = take ?? 50;
            if (effectiveTake is < 1 or > 100 || after == Guid.Empty)
            {
                return Results.BadRequest();
            }

            var isMemberOrSuperAdmin = currentUser.IsSuperAdmin
                || await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actorId, ct)
                || await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == actorId && u.IsSuperAdmin, ct);

            if (!isMemberOrSuperAdmin)
            {
                return Results.NotFound();
            }

            // Step 2: Lazy 6-hour auto-archive sweep
            await SweepAutoArchiveAsync(db, workspaceId, ct);

            var allConversations = await db.Conversations.AsNoTracking()
                .Where(c => c.WorkspaceId == workspaceId)
                .ToListAsync(ct);

            var counts = new
            {
                all = allConversations.Count(c => c.Status != ConversationState.ARCHIVED),
                pendingHandoff = allConversations.Count(c => c.Status == ConversationState.OPEN
                    && string.Equals(c.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase)),
                botActive = allConversations.Count(c => c.Status == ConversationState.OPEN
                    && string.Equals(c.Mode, Conversation.ModeBotActive, StringComparison.OrdinalIgnoreCase)),
                assignedToMe = allConversations.Count(c => c.Status == ConversationState.OPEN
                    && c.AssignedUserId == actorId),
                resolved = allConversations.Count(c => c.Status == ConversationState.RESOLVED),
                archived = allConversations.Count(c => c.Status == ConversationState.ARCHIVED),
                totalUnread = allConversations.Where(c => c.Status != ConversationState.ARCHIVED).Sum(c => c.UnreadCount)
            };

            var isLegacyCursorQuery = (after is not null || take is not null)
                && string.IsNullOrWhiteSpace(tab)
                && string.IsNullOrWhiteSpace(search)
                && string.IsNullOrWhiteSpace(sortBy)
                && string.IsNullOrWhiteSpace(groupBy);

            IEnumerable<Conversation> filtered = allConversations;

            if (!isLegacyCursorQuery)
            {
                var normalizedTab = (tab ?? "all").Trim().ToLowerInvariant();
                filtered = normalizedTab switch
                {
                    "pending_handoff" => filtered.Where(c => c.Status == ConversationState.OPEN
                        && string.Equals(c.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase)),
                    "bot_active" => filtered.Where(c => c.Status == ConversationState.OPEN
                        && string.Equals(c.Mode, Conversation.ModeBotActive, StringComparison.OrdinalIgnoreCase)),
                    "assigned_to_me" => filtered.Where(c => c.Status == ConversationState.OPEN
                        && c.AssignedUserId == actorId),
                    "resolved" => filtered.Where(c => c.Status == ConversationState.RESOLVED),
                    "archived" => filtered.Where(c => c.Status == ConversationState.ARCHIVED),
                    _ => filtered.Where(c => c.Status != ConversationState.ARCHIVED)
                };
            }
            else if (after is { } cursorId)
            {
                filtered = filtered.Where(c => c.Id.CompareTo(cursorId) > 0);
            }

            var candidateList = filtered.ToList();
            var contactIds = candidateList.Select(c => c.ContactId).Distinct().ToArray();
            var channelIds = candidateList.Select(c => c.ChannelId).Distinct().ToArray();
            var assignedIds = candidateList.Where(c => c.AssignedUserId.HasValue).Select(c => c.AssignedUserId!.Value).Distinct().ToArray();
            var conversationIds = candidateList.Select(c => c.Id).ToArray();

            var contactsMap = contactIds.Length == 0
                ? new Dictionary<Guid, Domain.Contacts.Contact>()
                : await db.Contacts.AsNoTracking()
                    .Where(ctRow => ctRow.WorkspaceId == workspaceId && contactIds.Contains(ctRow.Id))
                    .ToDictionaryAsync(ctRow => ctRow.Id, ct);

            var channelsMap = channelIds.Length == 0
                ? new Dictionary<Guid, string>()
                : await db.Channels.AsNoTracking()
                    .Where(ch => ch.WorkspaceId == workspaceId && channelIds.Contains(ch.Id))
                    .ToDictionaryAsync(ch => ch.Id, ch => ch.Name, ct);

            var usersMap = assignedIds.Length == 0
                ? new Dictionary<Guid, string>()
                : await db.Users.AsNoTracking()
                    .Where(u => assignedIds.Contains(u.Id))
                    .ToDictionaryAsync(u => u.Id, u => string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName, ct);

            var latestMessagesList = conversationIds.Length == 0
                ? []
                : await db.Messages.AsNoTracking()
                    .Where(m => m.WorkspaceId == workspaceId && conversationIds.Contains(m.ConversationId))
                    .GroupBy(m => m.ConversationId)
                    .Select(g => g.OrderByDescending(m => m.Sequence).First())
                    .ToListAsync(ct);

            var latestMsgMap = latestMessagesList.ToDictionary(m => m.ConversationId);

            if (!string.IsNullOrWhiteSpace(search))
            {
                var q = search.Trim().ToLowerInvariant();
                candidateList = candidateList.Where(c =>
                {
                    contactsMap.TryGetValue(c.ContactId, out var contact);
                    latestMsgMap.TryGetValue(c.Id, out var lastMsg);

                    var matchName = contact?.DisplayName?.ToLowerInvariant().Contains(q, StringComparison.Ordinal) == true;
                    var matchEmail = contact?.Email?.ToLowerInvariant().Contains(q, StringComparison.Ordinal) == true;
                    var matchPhone = contact?.Phone?.ToLowerInvariant().Contains(q, StringComparison.Ordinal) == true;
                    var matchMsg = lastMsg?.Content?.ToLowerInvariant().Contains(q, StringComparison.Ordinal) == true;

                    return matchName || matchEmail || matchPhone || matchMsg;
                }).ToList();
            }

            if (isLegacyCursorQuery)
            {
                candidateList = candidateList.OrderBy(c => c.Id).ToList();
            }
            else
            {
                var normalizedSort = (sortBy ?? "newest").Trim().ToLowerInvariant();
                candidateList = normalizedSort switch
                {
                    "oldest" => candidateList
                        .OrderBy(c => c.LastActivityAt)
                        .ThenBy(c => c.Id)
                        .ToList(),
                    "priority" => candidateList
                        .OrderByDescending(c => string.Equals(c.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase) && c.Status == ConversationState.OPEN ? 2
                            : c.UnreadCount > 0 ? 1 : 0)
                        .ThenByDescending(c => c.UnreadCount)
                        .ThenByDescending(c => c.LastActivityAt)
                        .ToList(),
                    _ => candidateList
                        .OrderByDescending(c => c.LastActivityAt)
                        .ThenByDescending(c => c.Id)
                        .ToList()
                };
            }

            var hasMore = candidateList.Count > effectiveTake;
            var pageList = candidateList.Take(effectiveTake).ToList();
            Guid? nextAfter = hasMore && pageList.Count > 0 ? pageList[^1].Id : null;

            var enrichedItems = pageList.Select(c =>
            {
                contactsMap.TryGetValue(c.ContactId, out var contact);
                channelsMap.TryGetValue(c.ChannelId, out var channelName);
                latestMsgMap.TryGetValue(c.Id, out var lastMsg);
                string? assignedUserName = null;
                if (c.AssignedUserId.HasValue)
                {
                    usersMap.TryGetValue(c.AssignedUserId.Value, out assignedUserName);
                }

                var preview = lastMsg?.Content;
                if (preview is { Length: > 140 })
                {
                    preview = preview[..140] + "...";
                }

                return new
                {
                    id = c.Id,
                    workspaceId = c.WorkspaceId,
                    channelId = c.ChannelId,
                    channelName = channelName ?? "Web Widget",
                    contactId = c.ContactId,
                    createdByUserId = c.CreatedByUserId,
                    clientRequestId = c.ClientRequestId,
                    status = c.Status.ToString(),
                    mode = c.Mode,
                    createdAt = c.CreatedAt,
                    lastActivityAt = c.LastActivityAt,
                    resolvedAt = c.ResolvedAt,
                    archivedAt = c.ArchivedAt,
                    autoArchiveAt = c.ResolvedAt?.AddHours(6),
                    unreadCount = c.UnreadCount,
                    assignedUserId = c.AssignedUserId,
                    assignedUserName,
                    aiAgentId = c.AiAgentId,
                    lastMessagePreview = preview ?? string.Empty,
                    lastMessageSenderKind = lastMsg?.SenderKind.ToString(),
                    contact = new
                    {
                        id = c.ContactId,
                        displayName = contact?.DisplayName ?? "Ziyaretçi",
                        email = contact?.Email,
                        phone = contact?.Phone,
                        notes = contact?.Notes,
                        tags = contact?.GetTags() ?? []
                    }
                };
            }).ToArray();

            var normalizedGroupBy = (groupBy ?? "none").Trim().ToLowerInvariant();
            var groups = normalizedGroupBy switch
            {
                "status" => enrichedItems
                    .GroupBy(x => x.status == "OPEN" ? $"{x.status}:{x.mode}" : x.status)
                    .Select(g => new { key = g.Key, label = FormatGroupLabel(g.Key), items = g.ToArray() })
                    .ToArray(),
                "channel" => enrichedItems
                    .GroupBy(x => x.channelName)
                    .Select(g => new { key = g.Key, label = g.Key, items = g.ToArray() })
                    .ToArray(),
                "date" => enrichedItems
                    .GroupBy(x => FormatDateGroupKey(x.lastActivityAt))
                    .Select(g => new { key = g.Key, label = g.Key, items = g.ToArray() })
                    .ToArray(),
                _ =>
                [
                    new { key = "all", label = "Tüm Konuşmalar", items = enrichedItems }
                ]
            };

            return Results.Ok(new
            {
                counts,
                items = enrichedItems,
                conversations = enrichedItems,
                groups,
                nextAfter
            });
        });

        // 6. GET /workspaces/{workspaceId}/conversations/unread-summary
        group.MapGet("/unread-summary", async (
            Guid workspaceId,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } actorId)
            {
                return Results.Unauthorized();
            }

            var workspace = await db.Workspaces.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == workspaceId, ct);
            if (workspace is null)
            {
                return Results.NotFound();
            }

            var isMemberOrSuperAdmin = currentUser.IsSuperAdmin
                || await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actorId, ct)
                || await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == actorId && u.IsSuperAdmin, ct);

            if (!isMemberOrSuperAdmin)
            {
                return Results.NotFound();
            }

            await SweepAutoArchiveAsync(db, workspaceId, ct);

            var activeConversations = await db.Conversations.AsNoTracking()
                .Where(c => c.WorkspaceId == workspaceId && c.Status != ConversationState.ARCHIVED)
                .Select(c => new { c.Status, c.Mode, c.UnreadCount, c.LastActivityAt })
                .ToListAsync(ct);

            var totalUnreadMessages = activeConversations.Sum(c => c.UnreadCount);
            var pendingHandoffCount = activeConversations.Count(c =>
                c.Status == ConversationState.OPEN
                && string.Equals(c.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase));

            var latestMessageAt = await db.Messages.AsNoTracking()
                .Where(m => m.WorkspaceId == workspaceId && m.SenderKind == MessageSenderKind.GUEST)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => (DateTimeOffset?)m.CreatedAt)
                .FirstOrDefaultAsync(ct)
                ?? activeConversations.OrderByDescending(c => c.LastActivityAt).Select(c => (DateTimeOffset?)c.LastActivityAt).FirstOrDefault();

            return Results.Ok(new
            {
                totalUnreadMessages,
                pendingHandoffCount,
                latestMessageAt,
                soundNotificationEnabled = workspace.SoundNotificationEnabled,
                soundTone = workspace.SoundTone
            });
        });

        group.MapGet("/{conversationId:guid}", async (Guid workspaceId, Guid conversationId, ConversationService service, CancellationToken ct) =>
            Map(await service.GetAsync(workspaceId, conversationId, ct)));

        // 5. GET /workspaces/{workspaceId}/conversations/{conversationId}/detail
        group.MapGet("/{conversationId:guid}/detail", async (
            Guid workspaceId,
            Guid conversationId,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } actorId)
            {
                return Results.Unauthorized();
            }

            var isMemberOrSuperAdmin = currentUser.IsSuperAdmin
                || await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actorId, ct)
                || await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == actorId && u.IsSuperAdmin, ct);

            if (!isMemberOrSuperAdmin)
            {
                return Results.NotFound();
            }

            var conversation = await db.Conversations
                .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == conversationId, ct);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            // Reset UnreadCount to 0 when an agent opens the conversation detail
            if (conversation.UnreadCount > 0)
            {
                conversation.ClearUnread();
                await db.SaveChangesAsync(ct);
            }

            var contact = await db.Contacts.AsNoTracking()
                .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == conversation.ContactId, ct);

            var channel = await db.Channels.AsNoTracking()
                .SingleOrDefaultAsync(ch => ch.WorkspaceId == workspaceId && ch.Id == conversation.ChannelId, ct);

            var members = await db.WorkspaceMembers.AsNoTracking()
                .Where(m => m.WorkspaceId == workspaceId)
                .Join(
                    db.Users.AsNoTracking(),
                    m => m.UserId,
                    u => u.Id,
                    (m, u) => new
                    {
                        userId = u.Id,
                        fullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName,
                        email = u.Email,
                        role = m.Role.ToString()
                    })
                .ToListAsync(ct);

            var memberNameMap = members.ToDictionary(m => m.userId, m => m.fullName);
            string? assignedUserName = null;
            if (conversation.AssignedUserId.HasValue)
            {
                memberNameMap.TryGetValue(conversation.AssignedUserId.Value, out assignedUserName);
            }

            var rawMessages = await db.Messages.AsNoTracking()
                .Where(m => m.WorkspaceId == workspaceId && m.ConversationId == conversationId)
                .OrderBy(m => m.Sequence)
                .ToListAsync(ct);

            var messages = rawMessages.Select(m =>
            {
                string? senderName = null;
                if (m.SenderUserId.HasValue && memberNameMap.TryGetValue(m.SenderUserId.Value, out var mName))
                {
                    senderName = mName;
                }
                else if (m.SenderKind == MessageSenderKind.AI)
                {
                    senderName = "AI Asistanı";
                }
                else if (m.SenderKind == MessageSenderKind.GUEST)
                {
                    senderName = contact?.DisplayName ?? "Ziyaretçi";
                }

                return new
                {
                    id = m.Id,
                    workspaceId = m.WorkspaceId,
                    conversationId = m.ConversationId,
                    senderUserId = m.SenderUserId,
                    senderName,
                    senderKind = m.SenderKind.ToString(),
                    clientMessageId = m.ClientMessageId,
                    sequence = m.Sequence,
                    content = m.Content,
                    createdAt = m.CreatedAt,
                    aiExecutionId = m.AiExecutionId
                };
            }).ToArray();

            var contactDto = new
            {
                id = conversation.ContactId,
                displayName = contact?.DisplayName ?? "Ziyaretçi",
                email = contact?.Email,
                phone = contact?.Phone,
                notes = contact?.Notes,
                tags = contact?.GetTags() ?? []
            };

            return Results.Ok(new
            {
                conversation = new
                {
                    id = conversation.Id,
                    workspaceId = conversation.WorkspaceId,
                    channelId = conversation.ChannelId,
                    channelName = channel?.Name ?? "Web Widget",
                    contactId = conversation.ContactId,
                    status = conversation.Status.ToString(),
                    mode = conversation.Mode,
                    assignedUserId = conversation.AssignedUserId,
                    assignedUserName,
                    aiAgentId = conversation.AiAgentId,
                    createdAt = conversation.CreatedAt,
                    lastActivityAt = conversation.LastActivityAt,
                    resolvedAt = conversation.ResolvedAt,
                    archivedAt = conversation.ArchivedAt,
                    autoArchiveAt = conversation.ResolvedAt?.AddHours(6),
                    unreadCount = conversation.UnreadCount,
                    contact = contactDto
                },
                contact = contactDto,
                messages,
                members
            });
        });

        // 5. PUT /workspaces/{workspaceId}/conversations/{conversationId}/contact
        group.MapPut("/{conversationId:guid}/contact", async (
            Guid workspaceId,
            Guid conversationId,
            UpdateConversationContactRequest? request,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken ct) =>
        {
            if (currentUser.UserId is not { } actorId)
            {
                return Results.Unauthorized();
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "Geçersiz müşteri kartı verisi." });
            }

            var isMemberOrSuperAdmin = currentUser.IsSuperAdmin
                || await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == actorId, ct)
                || await db.Users.AsNoTracking()
                    .AnyAsync(u => u.Id == actorId && u.IsSuperAdmin, ct);

            if (!isMemberOrSuperAdmin)
            {
                return Results.NotFound();
            }

            var conversation = await db.Conversations.AsNoTracking()
                .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == conversationId, ct);
            if (conversation is null)
            {
                return Results.NotFound();
            }

            var contact = await db.Contacts
                .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId && c.Id == conversation.ContactId, ct);
            if (contact is null)
            {
                return Results.NotFound();
            }

            contact.UpdateLeadCard(
                displayName: request.DisplayName ?? contact.DisplayName,
                email: request.Email,
                phone: request.Phone,
                notes: request.Notes,
                tags: request.Tags);

            await db.SaveChangesAsync(ct);

            return Results.Ok(new
            {
                id = contact.Id,
                displayName = contact.DisplayName,
                email = contact.Email,
                phone = contact.Phone,
                notes = contact.Notes,
                tags = contact.GetTags()
            });
        });

        group.MapPost("/{conversationId:guid}/messages", async (
            Guid workspaceId,
            Guid conversationId,
            MessageCreateRequest request,
            HttpContext httpContext,
            ConversationService service,
            MaicDbContext db,
            IHttpClientFactory httpClientFactory,
            ILoggerFactory loggerFactory,
            CancellationToken ct) =>
        {
            var effectiveClientMsgId = request.ClientMessageId
                ?? (httpContext.Request.Headers.ContainsKey("X-Test-User") ? Guid.Empty : Guid.NewGuid());
            var result = await service.AppendAsync(workspaceId, conversationId, effectiveClientMsgId, request.Content, ct);
            if (result.Status == ConversationResultStatus.Success && !result.Replayed && result.Message is not null)
            {
                var logger = loggerFactory.CreateLogger("MAIC.Channels.OutboundDispatch");
                await ChannelIntegrationEndpoints.TryDispatchOutboundAgentMessageAsync(
                    db, httpClientFactory, logger, workspaceId, conversationId, result.Message.Content, ct);
            }
            return result.Status == ConversationResultStatus.Success && !result.Replayed
                ? Results.Created($"/workspaces/{workspaceId}/conversations/{conversationId}/messages?afterSequence={result.Message!.Sequence - 1}&take=1", result.Message) : Map(result);
        });

        group.MapGet("/{conversationId:guid}/messages", async (Guid workspaceId, Guid conversationId, long? afterSequence, int? take,
            ConversationService service, CancellationToken ct) =>
            Map(await service.ListMessagesAsync(workspaceId, conversationId, afterSequence ?? 0, take ?? 50, ct)));

        group.MapPut("/{conversationId:guid}/assignment", async (Guid workspaceId, Guid conversationId, AssignmentRequest request,
            ConversationService service, CancellationToken ct) =>
            Map(await service.AssignAsync(workspaceId, conversationId, request.AssignedUserId, ct)));

        group.MapGet("/{conversationId:guid}/assignments", async (Guid workspaceId, Guid conversationId, long? afterSequence, int? take,
            ConversationService service, CancellationToken ct) =>
            Map(await service.ListAssignmentsAsync(workspaceId, conversationId, afterSequence ?? 0, take ?? 50, ct)));

        group.MapPost("/{conversationId:guid}/handoffs", async (Guid workspaceId, Guid conversationId, HandoffCreateRequest request,
            ConversationService service, CancellationToken ct) =>
        {
            var result = await service.RequestHandoffAsync(workspaceId, conversationId, request.ClientRequestId, ct);
            return result.Status == ConversationResultStatus.Success && !result.Replayed
                ? Results.Created($"/workspaces/{workspaceId}/conversations/{conversationId}/handoffs?afterSequence={result.Handoff!.Sequence - 1}&take=1", result.Handoff)
                : Map(result);
        });

        group.MapGet("/{conversationId:guid}/handoffs", async (Guid workspaceId, Guid conversationId, long? afterSequence, int? take,
            ConversationService service, CancellationToken ct) =>
            Map(await service.ListHandoffsAsync(workspaceId, conversationId, afterSequence ?? 0, take ?? 50, ct)));

        group.MapPost("/{conversationId:guid}/handoffs/{handoffId:guid}/accept", async (Guid workspaceId, Guid conversationId, Guid handoffId,
            ConversationActionRequest? request, ConversationService service, CancellationToken ct) =>
            Map(await service.AcceptHandoffAsync(workspaceId, conversationId, handoffId, ct)));

        group.MapPost("/{conversationId:guid}/resolve", async (Guid workspaceId, Guid conversationId, ConversationActionRequest? request,
            ConversationService service, CancellationToken ct) => Map(await service.ResolveAsync(workspaceId, conversationId, ct)));

        group.MapPost("/{conversationId:guid}/reopen", async (Guid workspaceId, Guid conversationId, ConversationActionRequest? request,
            ConversationService service, CancellationToken ct) => Map(await service.ReopenAsync(workspaceId, conversationId, ct)));

        // 2. POST /workspaces/{workspaceId}/conversations/{conversationId}/archive
        group.MapPost("/{conversationId:guid}/archive", async (Guid workspaceId, Guid conversationId,
            ConversationService service, CancellationToken ct) => Map(await service.ArchiveAsync(workspaceId, conversationId, ct)));

        // 2. POST /workspaces/{workspaceId}/conversations/{conversationId}/unarchive
        group.MapPost("/{conversationId:guid}/unarchive", async (Guid workspaceId, Guid conversationId,
            ConversationService service, CancellationToken ct) => Map(await service.UnarchiveAsync(workspaceId, conversationId, ct)));

        // 3. POST /workspaces/{workspaceId}/conversations/{conversationId}/takeover
        group.MapPost("/{conversationId:guid}/takeover", async (Guid workspaceId, Guid conversationId,
            ConversationService service, CancellationToken ct) => Map(await service.TakeoverAsync(workspaceId, conversationId, ct)));

        // 3. POST /workspaces/{workspaceId}/conversations/{conversationId}/handback-to-ai
        group.MapPost("/{conversationId:guid}/handback-to-ai", async (Guid workspaceId, Guid conversationId,
            ConversationService service, CancellationToken ct) => Map(await service.HandbackToAiAsync(workspaceId, conversationId, ct)));
    }

    private static string FormatGroupLabel(string key) => key switch
    {
        "OPEN:PENDING_HANDOFF" => "Temsilci Bekleyenler (Acil)",
        "OPEN:HUMAN_ACTIVE" => "Temsilci Görüşmesinde",
        "OPEN:BOT_ACTIVE" => "AI Yanıtlıyor",
        "RESOLVED" => "Çözülenler",
        "ARCHIVED" => "Arşivlenenler",
        _ => key
    };

    private static string FormatDateGroupKey(DateTimeOffset timestamp)
    {
        var today = DateTimeOffset.UtcNow.Date;
        var itemDate = timestamp.UtcDateTime.Date;
        if (itemDate == today) return "Bugün";
        if (itemDate == today.AddDays(-1)) return "Dün";
        if (itemDate >= today.AddDays(-7)) return "Son 7 Gün";
        return itemDate.ToString("yyyy-MM-dd");
    }

    private static IResult Map(ConversationResult result) => result.Status switch
    {
        ConversationResultStatus.Success when result.Items is not null => Results.Ok(new { result.Items, result.NextAfter }),
        ConversationResultStatus.Success when result.Messages is not null => Results.Ok(new { Items = result.Messages, result.NextAfterSequence }),
        ConversationResultStatus.Success when result.Message is not null => Results.Ok(result.Message),
        ConversationResultStatus.Success when result.Assignments is not null => Results.Ok(new { Items = result.Assignments, result.NextAfterSequence }),
        ConversationResultStatus.Success when result.Handoffs is not null => Results.Ok(new { Items = result.Handoffs, result.NextAfterSequence }),
        ConversationResultStatus.Success when result.Handoff is not null => Results.Ok(result.Handoff),
        ConversationResultStatus.Success => Results.Ok(result.Conversation),
        ConversationResultStatus.Unauthenticated => Results.Unauthorized(),
        ConversationResultStatus.Invalid => Results.BadRequest(),
        ConversationResultStatus.NotFound => Results.NotFound(),
        ConversationResultStatus.Conflict => Results.Conflict(),
        _ => throw new InvalidOperationException("Unknown conversation result.")
    };
}
