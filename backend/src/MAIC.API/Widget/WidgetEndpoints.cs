using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MAIC.API.Knowledge;
using MAIC.API.Workspaces;
using MAIC.Application.AI;
using MAIC.Application.Widget;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Widget;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Widget;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WidgetStartRequest;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record WidgetMessageRequest(Guid ClientMessageId, string? Content);

public sealed record PublicWidgetSessionRequest(
    string? Key,
    string? VisitorName,
    bool? Reset,
    string? SessionToken,
    Guid? WorkspaceId);

public sealed record PublicWidgetMessageRequest(
    string? Content,
    Guid? ClientMessageId,
    string? SessionToken,
    string? Key,
    Guid? WorkspaceId);

public static class WidgetEndpoints
{
    public const string CorsPolicy = "PublicGuestWidget";
    public const string DaribaseWidgetCorsPolicy = "PublicDaribaseWidget";

    private static readonly string[] DefaultSuggestedQuestions =
    [
        "Neler yapabiliyorsunuz?",
        "Fiyatlandırma nasıl işliyor?",
        "Nasıl başlayabilirim?"
    ];

    public static void MapWidgetEndpoints(this WebApplication app)
    {
        // Legacy / publishable-token channel widget endpoints (preserved for existing contracts & tests)
        var group = app.MapGroup("/public/channels/{publishableToken}/guest-session")
            .RequireCors(CorsPolicy).AllowAnonymous();
        group.MapPost("", async (string publishableToken, WidgetStartRequest request, HttpContext context,
            WidgetService service, CancellationToken ct) =>
            Map(await service.StartAsync(publishableToken, Credential(context.Request), ct), context, created: true));
        group.MapGet("", async (string publishableToken, HttpContext context, WidgetService service, CancellationToken ct) =>
            Map(await service.GetAsync(publishableToken, Credential(context.Request), ct), context));
        group.MapDelete("", async (string publishableToken, HttpContext context, WidgetService service, CancellationToken ct) =>
            Map(await service.RevokeAsync(publishableToken, Credential(context.Request), ct), context, revoked: true));
        group.MapGet("/messages", async (string publishableToken, long? afterSequence, int? take, HttpContext context,
            WidgetService service, CancellationToken ct) =>
            Map(await service.ListMessagesAsync(publishableToken, Credential(context.Request), afterSequence ?? 0, take ?? 50, ct), context));
        group.MapPost("/messages", async (string publishableToken, WidgetMessageRequest request, HttpContext context,
            WidgetService service, CancellationToken ct) =>
            Map(await service.AppendAsync(publishableToken, Credential(context.Request), request.ClientMessageId, request.Content, ct),
                context, created: true));

        // Phase 4: WorkspaceId-based Public Widget Endpoints (/public/widget/{workspaceId:guid}/*)
        var publicWidgetGroup = app.MapGroup("/public/widget/{workspaceId:guid}")
            .RequireCors(DaribaseWidgetCorsPolicy)
            .AllowAnonymous();

        // 2. GET /public/widget/{workspaceId:guid}/config
        publicWidgetGroup.MapGet("/config", async (
            Guid workspaceId,
            string? key,
            string? lang,
            HttpContext httpContext,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";

            var workspace = await db.Workspaces
                .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
            if (workspace is null)
            {
                return Results.NotFound(new { error = "Workspace bulunamadı." });
            }

            var accessError = ValidatePublicWidgetAccess(httpContext, workspace, key);
            if (accessError is not null) return accessError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var defaultChannel = await WorkspaceSettingsEndpoints.EnsureDefaultChannelAsync(
                db, workspace.Id, agent.Id, cancellationToken);

            var canRemoveBadge = await WorkspaceSettingsEndpoints.CanRemoveBadgeAsync(db, workspace, cancellationToken);
            var effectiveShowBadge = canRemoveBadge ? workspace.ShowBadge : true;
            var suggestedQuestions = ExtractSuggestedQuestions(workspace.KeyFaq);

            return Results.Ok(new
            {
                workspaceId = workspace.Id,
                brandName = workspace.BrandName ?? workspace.Name,
                widgetTitle = workspace.WidgetTitle,
                widgetSubtitle = "Çevrimiçi",
                primaryColor = workspace.PrimaryColor,
                logoUrl = workspace.LogoUrl,
                showBadge = effectiveShowBadge,
                welcomeMessage = workspace.WelcomeMessage,
                aiNotice = "AI asistanımızla konuşuyorsunuz. Gerekirse bir temsilci devralabilir.",
                privacyNotice = "Burada sohbet ederek, paylaştığınız verilerin işlenmesini Gizlilik Politikası kapsamında kabul etmiş olursunuz.",
                suggestedQuestions,
                publishableToken = defaultChannel.PublishableToken
            });
        });

        // 3. POST /public/widget/{workspaceId:guid}/session
        publicWidgetGroup.MapPost("/session", async (
            Guid workspaceId,
            PublicWidgetSessionRequest? request,
            string? key,
            HttpContext httpContext,
            MaicDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            return await HandlePublicWidgetSessionAsync(
                workspaceId,
                request,
                key ?? request?.Key,
                httpContext,
                db,
                timeProvider,
                cancellationToken);
        });

        // 3. GET /public/widget/{workspaceId:guid}/messages
        publicWidgetGroup.MapGet("/messages", async (
            Guid workspaceId,
            string? key,
            string? sessionToken,
            HttpContext httpContext,
            MaicDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";

            var workspace = await db.Workspaces.AsNoTracking()
                .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
            if (workspace is null)
            {
                return Results.NotFound(new { error = "Workspace bulunamadı." });
            }

            var accessError = ValidatePublicWidgetAccess(httpContext, workspace, key);
            if (accessError is not null) return accessError;

            var rawToken = ExtractBearerOrToken(httpContext.Request, sessionToken);
            var credentialHash = HashAnySessionToken(rawToken);
            if (credentialHash is null)
            {
                return Results.Unauthorized();
            }

            var now = timeProvider.GetUtcNow();
            var session = await db.GuestSessions.AsNoTracking()
                .SingleOrDefaultAsync(s => s.WorkspaceId == workspaceId
                                           && s.CredentialHash == credentialHash
                                           && s.ExpiresAt > now
                                           && s.RevokedAt == null, cancellationToken);
            if (session is null)
            {
                return Results.Unauthorized();
            }

            var conversation = await db.Conversations.AsNoTracking()
                .SingleOrDefaultAsync(c => c.WorkspaceId == workspaceId
                                           && c.CreatedByGuestSessionId == session.Id, cancellationToken);
            if (conversation is null)
            {
                return Results.Unauthorized();
            }

            var messages = await db.Messages.AsNoTracking()
                .Where(m => m.WorkspaceId == workspaceId && m.ConversationId == conversation.Id)
                .OrderBy(m => m.Sequence)
                .Select(m => new
                {
                    id = m.Id,
                    clientMessageId = m.ClientMessageId,
                    sequence = m.Sequence,
                    senderKind = m.SenderKind.ToString(),
                    content = m.Content,
                    createdAt = m.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(new
            {
                conversationId = conversation.Id,
                conversationStatus = conversation.Status.ToString(),
                messages
            });
        });

        // 3 & 4. POST /public/widget/{workspaceId:guid}/messages
        publicWidgetGroup.MapPost("/messages", async (
            Guid workspaceId,
            PublicWidgetMessageRequest? request,
            string? key,
            HttpContext httpContext,
            MaicDbContext db,
            TimeProvider timeProvider,
            KnowledgeRetriever retriever,
            ITextGenerator textGenerator,
            CancellationToken cancellationToken) =>
        {
            return await HandlePublicWidgetMessageAsync(
                workspaceId,
                request,
                key ?? request?.Key,
                httpContext,
                db,
                timeProvider,
                retriever,
                textGenerator,
                cancellationToken);
        });

        // Alias endpoints: POST /widget/session and POST /widget/messages
        var widgetAliasGroup = app.MapGroup("/widget")
            .RequireCors(DaribaseWidgetCorsPolicy)
            .AllowAnonymous();

        widgetAliasGroup.MapPost("/session", async (
            PublicWidgetSessionRequest? request,
            Guid? workspaceId,
            string? key,
            HttpContext httpContext,
            MaicDbContext db,
            TimeProvider timeProvider,
            CancellationToken cancellationToken) =>
        {
            var targetWorkspaceId = workspaceId ?? request?.WorkspaceId ?? Guid.Empty;
            if (targetWorkspaceId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "workspaceId gereklidir." });
            }

            return await HandlePublicWidgetSessionAsync(
                targetWorkspaceId,
                request,
                key ?? request?.Key,
                httpContext,
                db,
                timeProvider,
                cancellationToken);
        });

        widgetAliasGroup.MapPost("/messages", async (
            PublicWidgetMessageRequest? request,
            Guid? workspaceId,
            string? key,
            HttpContext httpContext,
            MaicDbContext db,
            TimeProvider timeProvider,
            KnowledgeRetriever retriever,
            ITextGenerator textGenerator,
            CancellationToken cancellationToken) =>
        {
            var targetWorkspaceId = workspaceId ?? request?.WorkspaceId ?? Guid.Empty;
            if (targetWorkspaceId == Guid.Empty)
            {
                // Resolve workspaceId from the bearer/session token if not passed explicitly
                var rawToken = ExtractBearerOrToken(httpContext.Request, request?.SessionToken);
                var credentialHash = HashAnySessionToken(rawToken);
                if (credentialHash is not null)
                {
                    targetWorkspaceId = await db.GuestSessions.AsNoTracking()
                        .Where(s => s.CredentialHash == credentialHash)
                        .Select(s => s.WorkspaceId)
                        .FirstOrDefaultAsync(cancellationToken);
                }
            }

            if (targetWorkspaceId == Guid.Empty)
            {
                return Results.BadRequest(new { error = "workspaceId veya geçerli sessionToken gereklidir." });
            }

            return await HandlePublicWidgetMessageAsync(
                targetWorkspaceId,
                request,
                key ?? request?.Key,
                httpContext,
                db,
                timeProvider,
                retriever,
                textGenerator,
                cancellationToken);
        });
    }

    private static async Task<IResult> HandlePublicWidgetSessionAsync(
        Guid workspaceId,
        PublicWidgetSessionRequest? request,
        string? effectiveKey,
        HttpContext httpContext,
        MaicDbContext db,
        TimeProvider timeProvider,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Results.NotFound(new { error = "Workspace bulunamadı." });
        }

        var accessError = ValidatePublicWidgetAccess(httpContext, workspace, effectiveKey);
        if (accessError is not null) return accessError;

        await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        var channel = await WorkspaceSettingsEndpoints.EnsureDefaultChannelAsync(
            db, workspace.Id, agent.Id, cancellationToken);

        var now = timeProvider.GetUtcNow();
        var existingToken = ExtractBearerOrToken(httpContext.Request, request?.SessionToken);

        if (request?.Reset != true && !string.IsNullOrWhiteSpace(existingToken))
        {
            var existingHash = HashAnySessionToken(existingToken);
            if (existingHash is not null)
            {
                var existingSession = await db.GuestSessions.AsNoTracking()
                    .SingleOrDefaultAsync(s => s.WorkspaceId == workspace.Id
                                               && s.ChannelId == channel.Id
                                               && s.CredentialHash == existingHash
                                               && s.ExpiresAt > now
                                               && s.RevokedAt == null, cancellationToken);

                if (existingSession is not null)
                {
                    var existingConversation = await db.Conversations.AsNoTracking()
                        .SingleOrDefaultAsync(c => c.WorkspaceId == workspace.Id
                                                   && c.CreatedByGuestSessionId == existingSession.Id, cancellationToken);

                    if (existingConversation is not null && existingConversation.Status == ConversationState.OPEN)
                    {
                        var existingMessages = await db.Messages.AsNoTracking()
                            .Where(m => m.WorkspaceId == workspace.Id && m.ConversationId == existingConversation.Id)
                            .OrderBy(m => m.Sequence)
                            .Select(m => new
                            {
                                id = m.Id,
                                clientMessageId = m.ClientMessageId,
                                sequence = m.Sequence,
                                senderKind = m.SenderKind.ToString(),
                                content = m.Content,
                                createdAt = m.CreatedAt
                            })
                            .ToListAsync(cancellationToken);

                        return Results.Ok(new
                        {
                            sessionToken = existingToken,
                            conversationId = existingConversation.Id,
                            status = existingConversation.Status.ToString(),
                            expiresAt = existingSession.ExpiresAt,
                            welcomeMessage = workspace.WelcomeMessage,
                            messages = existingMessages
                        });
                    }
                }
            }
        }

        // Generate a fresh 32-byte base64url session token (compatible with WidgetService.HashCredential)
        var rawBytes = RandomNumberGenerator.GetBytes(32);
        var sessionToken = Convert.ToBase64String(rawBytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var credentialHash = Convert.ToHexString(SHA256.HashData(rawBytes));

        var visitorName = string.IsNullOrWhiteSpace(request?.VisitorName)
            ? "Ziyaretçi"
            : request!.VisitorName.Trim();
        if (visitorName.Length > 200 || visitorName.Any(char.IsControl))
        {
            visitorName = "Ziyaretçi";
        }

        var contact = new Contact(workspace.Id, visitorName, []);
        var guestSession = new GuestSession(
            workspace.Id,
            channel.Id,
            contact.Id,
            credentialHash,
            channel.GuestAccessGeneration,
            now);
        var conversation = Conversation.CreateGuest(guestSession);
        conversation.BindAiAgent(agent.Id);

        db.AddRange(contact, guestSession, conversation);
        await db.SaveChangesAsync(cancellationToken);

        return Results.Ok(new
        {
            sessionToken,
            conversationId = conversation.Id,
            status = conversation.Status.ToString(),
            expiresAt = guestSession.ExpiresAt,
            welcomeMessage = workspace.WelcomeMessage,
            messages = Array.Empty<object>()
        });
    }

    private static async Task<IResult> HandlePublicWidgetMessageAsync(
        Guid workspaceId,
        PublicWidgetMessageRequest? request,
        string? effectiveKey,
        HttpContext httpContext,
        MaicDbContext db,
        TimeProvider timeProvider,
        KnowledgeRetriever retriever,
        ITextGenerator textGenerator,
        CancellationToken cancellationToken)
    {
        httpContext.Response.Headers.CacheControl = "no-store";

        if (request is null || string.IsNullOrWhiteSpace(request.Content))
        {
            return Results.BadRequest(new { error = "Mesaj içeriği boş olamaz." });
        }

        var content = request.Content.Trim();
        if (!Message.IsValidContent(content))
        {
            return Results.BadRequest(new { error = "Geçersiz mesaj içeriği (en fazla 4000 karakter)." });
        }

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return Results.NotFound(new { error = "Workspace bulunamadı." });
        }

        var accessError = ValidatePublicWidgetAccess(httpContext, workspace, effectiveKey);
        if (accessError is not null) return accessError;

        var rawToken = ExtractBearerOrToken(httpContext.Request, request.SessionToken);
        var credentialHash = HashAnySessionToken(rawToken);
        if (credentialHash is null)
        {
            return Results.Unauthorized();
        }

        var now = timeProvider.GetUtcNow();
        var guestSession = await db.GuestSessions
            .SingleOrDefaultAsync(s => s.WorkspaceId == workspace.Id
                                       && s.CredentialHash == credentialHash
                                       && s.ExpiresAt > now
                                       && s.RevokedAt == null, cancellationToken);
        if (guestSession is null)
        {
            return Results.Unauthorized();
        }

        var conversation = await db.Conversations
            .SingleOrDefaultAsync(c => c.WorkspaceId == workspace.Id
                                       && c.CreatedByGuestSessionId == guestSession.Id, cancellationToken);
        if (conversation is null)
        {
            return Results.Unauthorized();
        }

        if (conversation.Status != ConversationState.OPEN)
        {
            return Results.Conflict(new
            {
                code = "CONVERSATION_CLOSED",
                error = "Bu konuşma sonlandırılmış veya arşivlenmiştir. Yeni bir konuşma başlatabilirsiniz."
            });
        }

        var clientMessageId = request.ClientMessageId is { } cid && cid != Guid.Empty
            ? cid
            : Guid.NewGuid();

        // Check idempotent replay if ClientMessageId was already sent in this session
        var existingGuestMsg = await db.Messages.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspace.Id
                                       && m.ConversationId == conversation.Id
                                       && m.SenderGuestSessionId == guestSession.Id
                                       && m.ClientMessageId == clientMessageId, cancellationToken);

        Message guestMessage;
        if (existingGuestMsg is not null)
        {
            guestMessage = existingGuestMsg;
        }
        else
        {
            guestMessage = Message.CreateGuest(
                conversation,
                guestSession.Id,
                clientMessageId,
                conversation.AdvanceMessageSequence(),
                content);
            conversation.IncrementUnread(1, guestMessage.CreatedAt);
            db.Messages.Add(guestMessage);
            await db.SaveChangesAsync(cancellationToken);
        }

        var guestMessageDto = new
        {
            id = guestMessage.Id,
            clientMessageId = guestMessage.ClientMessageId,
            sequence = guestMessage.Sequence,
            senderKind = guestMessage.SenderKind.ToString(),
            content = guestMessage.Content,
            createdAt = guestMessage.CreatedAt
        };

        // Step 3 & 4: Check mode, human takeover, pending handoff, automatic escalation, and monthly message quota
        var isHumanActive = conversation.AssignedUserId is not null
            || string.Equals(conversation.Mode, Conversation.ModeHumanActive, StringComparison.OrdinalIgnoreCase);

        if (conversation.Status != ConversationState.OPEN || isHumanActive)
        {
            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = (object?)null,
                aiReply = (string?)null,
                conversationStatus = conversation.Status.ToString(),
                mode = conversation.Mode
            });
        }

        var hasPendingHandoff = await db.HandoffRequests.AsNoTracking()
            .AnyAsync(h => h.WorkspaceId == workspace.Id
                           && h.ConversationId == conversation.Id
                           && h.Status == HandoffState.PENDING, cancellationToken);

        var isPendingHandoffMode = hasPendingHandoff
            || string.Equals(conversation.Mode, Conversation.ModePendingHandoff, StringComparison.OrdinalIgnoreCase);

        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);

        // If already in PENDING_HANDOFF and no agent has taken over yet, send waiting notice without calling AI
        if (isPendingHandoffMode)
        {
            const string waitingNotice = "Talebiniz temsilcimize iletildi, kısa süre içinde bağlanacaktır.";
            var waitingAiMsg = await PersistSystemAiNoticeAsync(
                db, timeProvider, workspace, conversation, agent, guestMessage,
                waitingNotice, "HANDOFF", createPendingHandoff: !hasPendingHandoff, cancellationToken);

            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = waitingAiMsg,
                aiReply = waitingNotice,
                conversationStatus = conversation.Status.ToString(),
                mode = conversation.Mode
            });
        }

        // Check if the visitor message requests a human representative / live support
        if (RequestsHumanEscalation(content))
        {
            const string escalatedNotice = "Talebiniz müşteri temsilcimize iletildi, ekip arkadaşımız kısa süre içinde sohbete katılacaktır.";
            var escalatedAiMsg = await PersistSystemAiNoticeAsync(
                db, timeProvider, workspace, conversation, agent, guestMessage,
                escalatedNotice, "HANDOFF", createPendingHandoff: true, cancellationToken);

            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = escalatedAiMsg,
                aiReply = escalatedNotice,
                conversationStatus = conversation.Status.ToString(),
                mode = conversation.Mode
            });
        }

        var isManualOnly = string.Equals(workspace.AiScheduleMode, "MANUAL_ONLY", StringComparison.OrdinalIgnoreCase);
        if (isManualOnly || !agent.Enabled)
        {
            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = (object?)null,
                aiReply = (string?)null,
                conversationStatus = conversation.Status.ToString(),
                mode = conversation.Mode
            });
        }

        await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
        var planDef = await db.PlanDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Plan == workspace.Plan, cancellationToken)
            ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace.Plan);

        var effectiveMonthlyCap = workspace.MonthlyMessageCap > 0
            ? workspace.MonthlyMessageCap
            : (planDef.MonthlyMessageLimit > 0 ? planDef.MonthlyMessageLimit : 5000);

        if (workspace.UsedMessagesThisMonth >= effectiveMonthlyCap)
        {
            // Quota exhausted: do NOT run AI; leave conversation OPEN for a human agent
            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = (object?)null,
                aiReply = (string?)null,
                conversationStatus = conversation.Status.ToString(),
                mode = conversation.Mode,
                quotaExceeded = true
            });
        }

        // Retrieve RAG passages + fallback chunks
        var knowledgeSnippets = new List<string>();
        try
        {
            var retrieval = await retriever.RetrieveAsync(
                new RetrievalRequest(
                    WorkspaceId: workspace.Id,
                    AgentId: agent.Id,
                    Query: content,
                    VisitorKey: $"guest:{guestSession.Id:N}",
                    Preview: false),
                cancellationToken);

            if (retrieval.Passages.Count > 0)
            {
                foreach (var passage in retrieval.Passages)
                {
                    knowledgeSnippets.Add(passage.Text);
                }
            }
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Fall back to direct DB chunk lookup below.
        }

        if (knowledgeSnippets.Count == 0)
        {
            var fallbackChunks = await WorkspaceSettingsEndpoints.LoadFallbackChunksAsync(
                db, workspace.Id, content, cancellationToken);
            foreach (var chunk in fallbackChunks)
            {
                knowledgeSnippets.Add(chunk.Content);
            }
        }

        var (useCaseHint, assistantRoleHint) = await WorkspaceSettingsEndpoints.GetCatalogSystemPromptHintsAsync(
            db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

        var systemPrompt = WorkspaceSettingsEndpoints.BuildPlaygroundSystemPrompt(
            workspace, agent, knowledgeSnippets, useCaseHint, assistantRoleHint);

        var historyRows = await db.Messages.AsNoTracking()
            .Where(m => m.WorkspaceId == workspace.Id
                        && m.ConversationId == conversation.Id
                        && m.Id != guestMessage.Id)
            .OrderByDescending(m => m.Sequence)
            .Take(10)
            .ToListAsync(cancellationToken);
        historyRows.Reverse();

        var genMessages = new List<TextGenerationMessage>
        {
            new(TextGenerationRole.System, systemPrompt)
        };

        foreach (var row in historyRows)
        {
            var role = row.SenderKind == MessageSenderKind.AI
                ? TextGenerationRole.Assistant
                : TextGenerationRole.User;
            var msgText = row.Content.Length > 2000 ? row.Content[..2000] : row.Content;
            genMessages.Add(new TextGenerationMessage(role, msgText));
        }

        genMessages.Add(new TextGenerationMessage(TextGenerationRole.User, content));

        string aiReplyText;
        try
        {
            var generated = await textGenerator.GenerateAsync(
                new TextGenerationRequest(agent.ModelTier, genMessages, 1024),
                cancellationToken);
            aiReplyText = generated.Text.Trim();
        }
        catch (TextGenerationException)
        {
            aiReplyText = WorkspaceSettingsEndpoints.BuildFallbackReply(workspace, content, knowledgeSnippets);
        }

        if (aiReplyText.Length > 4000)
        {
            aiReplyText = aiReplyText[..4000];
        }
        if (!Message.IsValidContent(aiReplyText))
        {
            aiReplyText = WorkspaceSettingsEndpoints.BuildFallbackReply(workspace, content, []);
        }

        // Persist AI execution, AI message, and atomically increment workspace.UsedMessagesThisMonth
        await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

        var quotaUpdated = await db.Workspaces
            .Where(w => w.Id == workspace.Id && w.UsedMessagesThisMonth < effectiveMonthlyCap)
            .ExecuteUpdateAsync(
                s => s.SetProperty(w => w.UsedMessagesThisMonth, w => w.UsedMessagesThisMonth + 1),
                cancellationToken);

        if (quotaUpdated == 0)
        {
            await tx.RollbackAsync(cancellationToken);
            return Results.Ok(new
            {
                guestMessage = guestMessageDto,
                aiMessage = (object?)null,
                aiReply = (string?)null,
                conversationStatus = conversation.Status.ToString(),
                quotaExceeded = true
            });
        }

        var actorUserId = await ResolveWorkspaceActorUserIdAsync(db, workspace.Id, cancellationToken);
        var execNow = timeProvider.GetUtcNow();
        var agentSnapshotJson = JsonSerializer.Serialize(new AiAgentSettings(
            agent.Name,
            agent.Enabled,
            agent.Instructions,
            agent.Language,
            agent.ModelTier,
            agent.KnowledgeScope,
            [],
            agent.HandoffOnRequest,
            agent.HandoffWhenUnanswered,
            agent.MaxAiReplies));

        var aiExecution = new AiExecution(
            workspace.Id,
            conversation.Id,
            guestMessage.Id,
            actorUserId,
            agent.Id,
            agentSnapshotJson,
            conversation.AiBindingRevision,
            guestMessage.Sequence,
            conversation.LastAssignmentSequence,
            conversation.LastHandoffSequence,
            execNow,
            execNow.AddSeconds(45));
        aiExecution.Complete("ANSWERED", null, execNow);

        var aiMessage = Message.CreateAi(
            workspace.Id,
            conversation.Id,
            aiExecution.Id,
            conversation.AdvanceMessageSequence(),
            aiReplyText);
        conversation.TouchActivity(aiMessage.CreatedAt);

        db.AiExecutions.Add(aiExecution);
        db.Messages.Add(aiMessage);
        await db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        var aiMessageDto = new
        {
            id = aiMessage.Id,
            clientMessageId = aiMessage.ClientMessageId,
            sequence = aiMessage.Sequence,
            senderKind = aiMessage.SenderKind.ToString(),
            content = aiMessage.Content,
            createdAt = aiMessage.CreatedAt
        };

        return Results.Ok(new
        {
            guestMessage = guestMessageDto,
            aiMessage = aiMessageDto,
            aiReply = aiMessage.Content,
            conversationStatus = conversation.Status.ToString(),
            mode = conversation.Mode
        });
    }

    private static async Task<object> PersistSystemAiNoticeAsync(
        MaicDbContext db,
        TimeProvider timeProvider,
        Workspace workspace,
        Conversation conversation,
        AiAgent agent,
        Message triggerGuestMessage,
        string noticeText,
        string outcome,
        bool createPendingHandoff,
        CancellationToken cancellationToken)
    {
        var actorUserId = await ResolveWorkspaceActorUserIdAsync(db, workspace.Id, cancellationToken);
        var execNow = timeProvider.GetUtcNow();
        var agentSnapshotJson = JsonSerializer.Serialize(new AiAgentSettings(
            agent.Name,
            agent.Enabled,
            agent.Instructions,
            agent.Language,
            agent.ModelTier,
            agent.KnowledgeScope,
            [],
            agent.HandoffOnRequest,
            agent.HandoffWhenUnanswered,
            agent.MaxAiReplies));

        var aiExecution = new AiExecution(
            workspace.Id,
            conversation.Id,
            triggerGuestMessage.Id,
            actorUserId,
            agent.Id,
            agentSnapshotJson,
            conversation.AiBindingRevision,
            triggerGuestMessage.Sequence,
            conversation.LastAssignmentSequence,
            conversation.LastHandoffSequence,
            execNow,
            execNow.AddSeconds(45));
        aiExecution.Complete(outcome, "HUMAN_REQUEST", execNow);

        conversation.SetMode(Conversation.ModePendingHandoff, execNow);

        if (createPendingHandoff)
        {
            var hasExistingPending = await db.HandoffRequests.AsNoTracking()
                .AnyAsync(h => h.WorkspaceId == workspace.Id
                               && h.ConversationId == conversation.Id
                               && h.Status == HandoffState.PENDING, cancellationToken);

            if (!hasExistingPending)
            {
                var handoff = conversation.RequestHandoff(actorUserId, Guid.NewGuid(), aiExecution.Id);
                db.HandoffRequests.Add(handoff);
            }
        }

        var aiMessage = Message.CreateAi(
            workspace.Id,
            conversation.Id,
            aiExecution.Id,
            conversation.AdvanceMessageSequence(),
            noticeText);
        conversation.TouchActivity(aiMessage.CreatedAt);

        db.AiExecutions.Add(aiExecution);
        db.Messages.Add(aiMessage);
        await db.SaveChangesAsync(cancellationToken);

        return new
        {
            id = aiMessage.Id,
            clientMessageId = aiMessage.ClientMessageId,
            sequence = aiMessage.Sequence,
            senderKind = aiMessage.SenderKind.ToString(),
            content = aiMessage.Content,
            createdAt = aiMessage.CreatedAt
        };
    }

    public static bool RequestsHumanEscalation(string? content)
    {
        if (string.IsNullOrWhiteSpace(content)) return false;

        var lower = content.Trim().ToLowerInvariant()
            .Replace('ı', 'i')
            .Replace('İ', 'i')
            .Replace('ş', 's')
            .Replace('ğ', 'g')
            .Replace('ü', 'u')
            .Replace('ö', 'o')
            .Replace('ç', 'c');

        if (lower.Contains("istemiyorum", StringComparison.Ordinal)
            || lower.Contains("istemem", StringComparison.Ordinal))
        {
            return false;
        }

        string[] keywords =
        [
            "canli destek",
            "temsilci",
            "insanla gorus",
            "insanla konus",
            "insana bagla",
            "operator",
            "yetkili",
            "musteri hizmetleri",
            "gercek kisi",
            "human agent",
            "live agent",
            "live support",
            "talk to a human",
            "speak to a person",
            "real person"
        ];

        return keywords.Any(k => lower.Contains(k, StringComparison.Ordinal));
    }

    private static async Task<Guid> ResolveWorkspaceActorUserIdAsync(
        MaicDbContext db,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var memberUserId = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspaceId)
            .OrderBy(m => m.Role)
            .Select(m => (Guid?)m.UserId)
            .FirstOrDefaultAsync(cancellationToken);
        if (memberUserId.HasValue) return memberUserId.Value;

        var anyUserId = await db.Users.AsNoTracking()
            .Select(u => (Guid?)u.Id)
            .FirstOrDefaultAsync(cancellationToken);
        if (anyUserId.HasValue) return anyUserId.Value;

        var systemUser = new User();
        db.Users.Add(systemUser);
        await db.SaveChangesAsync(cancellationToken);
        return systemUser.Id;
    }

    public static IResult? ValidatePublicWidgetAccess(
        HttpContext httpContext,
        Workspace workspace,
        string? key)
    {
        // 1. Direct Share request (?key=...)
        if (!string.IsNullOrWhiteSpace(key))
        {
            if (!workspace.DirectShareActive
                || !string.Equals(workspace.DirectShareKey, key.Trim(), StringComparison.Ordinal))
            {
                return Results.Json(new
                {
                    code = "DIRECT_SHARE_DISABLED_OR_INVALID_KEY",
                    error = "Doğrudan paylaşım bağlantısı devre dışı bırakılmış veya geçersiz anahtar."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            return null;
        }

        // 2. Embedded Website request: validate Origin / Referer against AllowedDomains if configured
        if (string.IsNullOrWhiteSpace(workspace.AllowedDomains))
        {
            return null;
        }

        var allowedList = workspace.AllowedDomains
            .Split([',', ';', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(NormalizeDomainEntry)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .ToArray();

        if (allowedList.Length == 0)
        {
            return null;
        }

        var callerHost = ExtractCallerHost(httpContext.Request);
        if (string.IsNullOrWhiteSpace(callerHost))
        {
            // Allow same-host local/server calls when no cross-origin header is sent on localhost
            var selfHost = httpContext.Request.Host.Host;
            if (IsBuiltInTrustedHost(selfHost, httpContext.Request.Host.Host))
            {
                return null;
            }

            return Results.Json(new
            {
                code = "DOMAIN_NOT_ALLOWED",
                error = "Bu alan adı (domain) widget erişimi için yetkilendirilmemiş."
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        if (IsBuiltInTrustedHost(callerHost, httpContext.Request.Host.Host)
            || allowedList.Any(allowed => IsHostMatchingAllowedDomain(callerHost, allowed!)))
        {
            return null;
        }

        return Results.Json(new
        {
            code = "DOMAIN_NOT_ALLOWED",
            error = "Bu alan adı (domain) widget erişimi için yetkilendirilmemiş."
        }, statusCode: StatusCodes.Status403Forbidden);
    }

    private static string? ExtractCallerHost(HttpRequest request)
    {
        var origin = request.Headers.Origin.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(origin) && !string.Equals(origin, "null", StringComparison.OrdinalIgnoreCase))
        {
            if (Uri.TryCreate(origin, UriKind.Absolute, out var originUri) && !string.IsNullOrWhiteSpace(originUri.Host))
            {
                return originUri.Host.ToLowerInvariant();
            }
        }

        var referer = request.Headers.Referer.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(referer))
        {
            if (Uri.TryCreate(referer, UriKind.Absolute, out var refererUri) && !string.IsNullOrWhiteSpace(refererUri.Host))
            {
                return refererUri.Host.ToLowerInvariant();
            }
        }

        return null;
    }

    private static string? NormalizeDomainEntry(string raw)
    {
        var trimmed = raw.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(trimmed)) return null;

        if (trimmed.StartsWith("*."))
        {
            trimmed = trimmed[2..];
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && !string.IsNullOrWhiteSpace(uri.Host))
        {
            return uri.Host.ToLowerInvariant();
        }

        if (Uri.TryCreate("https://" + trimmed, UriKind.Absolute, out var httpsUri) && !string.IsNullOrWhiteSpace(httpsUri.Host))
        {
            return httpsUri.Host.ToLowerInvariant();
        }

        var slashIdx = trimmed.IndexOf('/');
        if (slashIdx > 0) trimmed = trimmed[..slashIdx];
        var colonIdx = trimmed.IndexOf(':');
        if (colonIdx > 0) trimmed = trimmed[..colonIdx];

        return trimmed.Trim('.');
    }

    private static bool IsBuiltInTrustedHost(string host, string requestHost)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        var lower = host.Trim().ToLowerInvariant();

        if (lower is "localhost" or "127.0.0.1" or "::1" or "daribase.com"
            || lower.EndsWith(".daribase.com", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(requestHost)
            && string.Equals(lower, requestHost.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static bool IsHostMatchingAllowedDomain(string callerHost, string allowedDomain)
    {
        var cleanCaller = callerHost.Trim('.').ToLowerInvariant();
        var cleanAllowed = allowedDomain.Trim('.').ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(cleanCaller) || string.IsNullOrWhiteSpace(cleanAllowed))
        {
            return false;
        }

        return string.Equals(cleanCaller, cleanAllowed, StringComparison.OrdinalIgnoreCase)
               || cleanCaller.EndsWith("." + cleanAllowed, StringComparison.OrdinalIgnoreCase);
    }

    public static string[] ExtractSuggestedQuestions(string? keyFaq)
    {
        if (string.IsNullOrWhiteSpace(keyFaq))
        {
            return DefaultSuggestedQuestions;
        }

        var lines = keyFaq
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var questions = new List<string>();

        foreach (var rawLine in lines)
        {
            var cleaned = CleanFaqLine(rawLine);
            if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 4) continue;

            var qIndex = cleaned.IndexOf('?');
            if (qIndex >= 3)
            {
                var qCandidate = cleaned[..(qIndex + 1)].Trim();
                if (qCandidate.Length is >= 4 and <= 120
                    && !questions.Contains(qCandidate, StringComparer.OrdinalIgnoreCase))
                {
                    questions.Add(qCandidate);
                    if (questions.Count == 4) break;
                }
            }
        }

        if (questions.Count < 4)
        {
            foreach (var rawLine in lines)
            {
                var cleaned = CleanFaqLine(rawLine);
                if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length is < 4 or > 100) continue;

                var colonIdx = cleaned.IndexOf(':');
                if (colonIdx is >= 4 and <= 80)
                {
                    cleaned = cleaned[..colonIdx].Trim();
                }

                if (cleaned.Length is >= 4 and <= 100
                    && !questions.Contains(cleaned, StringComparer.OrdinalIgnoreCase))
                {
                    questions.Add(cleaned);
                    if (questions.Count == 4) break;
                }
            }
        }

        return questions.Count > 0 ? questions.ToArray() : DefaultSuggestedQuestions;
    }

    private static string CleanFaqLine(string line)
    {
        var s = line.Trim().TrimStart('-', '*', '•', '#', '>', ' ');
        if (s.Length > 2 && char.IsDigit(s[0]) && (s[1] is '.' or ')' || (char.IsDigit(s[1]) && s.Length > 3 && s[2] is '.' or ')')))
        {
            var dotIdx = s.IndexOfAny(['.', ')']);
            if (dotIdx >= 0 && dotIdx + 1 < s.Length)
            {
                s = s[(dotIdx + 1)..].Trim();
            }
        }

        foreach (var prefix in new[] { "Soru:", "S:", "Q:", "Question:" })
        {
            if (s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                s = s[prefix.Length..].Trim();
                break;
            }
        }

        return s;
    }

    private static string? ExtractBearerOrToken(HttpRequest request, string? fallbackToken)
    {
        var authHeader = request.Headers.Authorization.FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(authHeader)
            && authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            var bearer = authHeader["Bearer ".Length..].Trim();
            if (!string.IsNullOrWhiteSpace(bearer)) return bearer;
        }

        return string.IsNullOrWhiteSpace(fallbackToken) ? null : fallbackToken.Trim();
    }

    private static string? HashAnySessionToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var canonical = WidgetService.HashCredential(token);
        if (canonical is not null) return canonical;

        if (token.Length is < 16 or > 256) return null;
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
    }

    internal static string? Credential(HttpRequest request)
    {
        var values = request.Headers.Authorization;
        return values.Count == 1 && values[0] is { Length: 50 } value && value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? value[7..] : null;
    }

    private static IResult Map(WidgetResult result, HttpContext context, bool created = false, bool revoked = false)
    {
        switch (result.Status)
        {
            case WidgetStatus.Success:
                if (revoked) return Results.NoContent();
                object payload = result.Messages is not null
                    ? new { Status = result.ConversationStatus, result.Messages, result.NextAfterSequence }
                    : (object?)result.Message ?? result.Session ?? throw new InvalidOperationException("Missing widget response.");
                return Results.Json(payload, statusCode: created && !result.Replayed ? 201 : 200);
            case WidgetStatus.Invalid: return Results.BadRequest();
            case WidgetStatus.Unauthenticated: return Results.Unauthorized();
            case WidgetStatus.NotFound: return Results.NotFound();
            case WidgetStatus.Conflict: return Results.Conflict();
            case WidgetStatus.Limited:
                context.Response.Headers.RetryAfter = "60";
                return Results.StatusCode(StatusCodes.Status429TooManyRequests);
            default: throw new InvalidOperationException("Unknown widget result.");
        }
    }
}
