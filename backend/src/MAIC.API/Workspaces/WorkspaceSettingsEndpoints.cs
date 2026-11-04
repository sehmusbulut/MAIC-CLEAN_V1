using System.Text;
using System.Text.Json;
using MAIC.API.Knowledge;
using MAIC.Application.AI;
using MAIC.Application.Identity;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Workspaces;

public sealed record UpdateWorkspaceSettingsRequest(
    string? BrandName,
    string? WidgetTitle,
    string? WidgetSubtitle,
    string? PrimaryColor,
    bool? ShowBadge,
    bool? DirectShareActive,
    string? UseCase,
    string? AssistantRole,
    string? PrimaryChannel,
    string? InitialKnowledgeMethod,
    string? WelcomeMessage,
    string? BusinessSummary,
    string? KeyFaq,
    string? AnswerBoundaries,
    string? EscalationRules,
    string? AiScheduleMode,
    bool? SoundNotificationEnabled,
    string? SoundTone,
    string? AllowedDomains,
    string? AgentName,
    string? Language,
    string? ModelTier,
    bool? Enabled);

public sealed record PlaygroundHistoryMessageDto(string? Role, string? Content);

public sealed record PlaygroundChatRequest(
    string? Message,
    PlaygroundHistoryMessageDto[]? History);

public sealed record UpdateWidgetConfigRequest(
    bool? DirectShareActive,
    string? AllowedDomains);

public static class WorkspaceSettingsEndpoints
{
    public const long MaxLogoSizeBytes = 2L * 1024 * 1024; // 2 MB

    public static void MapWorkspaceSettingsEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}").RequireAuthorization();

        // 2. GET /workspaces/{workspaceId}/settings
        group.MapGet("/settings", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace!, cancellationToken);
            var defaultChannel = await EnsureDefaultChannelAsync(db, workspace!.Id, agent.Id, cancellationToken);

            var response = await BuildSettingsResponseAsync(db, workspace, agent, defaultChannel, cancellationToken);
            return Results.Ok(response);
        });

        // 2. PUT /workspaces/{workspaceId}/settings
        group.MapPut("/settings", async (
            Guid workspaceId,
            UpdateWorkspaceSettingsRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (request is null)
            {
                return Results.BadRequest(new { error = "Geçersiz ayar gövdesi." });
            }

            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var canRemoveBadge = await CanRemoveBadgeAsync(db, workspace!, cancellationToken);

            if (request.ShowBadge == false && !canRemoveBadge)
            {
                return Results.Json(new
                {
                    code = "BADGE_REMOVAL_REQUIRES_PRO",
                    error = "Daribase rozetini gizlemek için Pro veya Scale pakete yükseltmelisiniz."
                }, statusCode: StatusCodes.Status403Forbidden);
            }

            var effectiveShowBadge = canRemoveBadge
                ? (request.ShowBadge ?? workspace!.ShowBadge)
                : true;

            workspace!.UpdateWizardSettings(
                brandName: request.BrandName ?? workspace.BrandName,
                widgetTitle: request.WidgetTitle ?? workspace.WidgetTitle,
                widgetSubtitle: request.WidgetSubtitle ?? workspace.WidgetSubtitle,
                primaryColor: request.PrimaryColor ?? workspace.PrimaryColor,
                showBadge: effectiveShowBadge,
                directShareActive: request.DirectShareActive ?? workspace.DirectShareActive,
                useCase: request.UseCase ?? workspace.UseCase,
                assistantRole: request.AssistantRole ?? workspace.AssistantRole,
                primaryChannel: request.PrimaryChannel ?? workspace.PrimaryChannel,
                initialKnowledgeMethod: request.InitialKnowledgeMethod ?? workspace.InitialKnowledgeMethod,
                welcomeMessage: request.WelcomeMessage ?? workspace.WelcomeMessage,
                businessSummary: request.BusinessSummary ?? workspace.BusinessSummary,
                keyFaq: request.KeyFaq ?? workspace.KeyFaq,
                answerBoundaries: request.AnswerBoundaries ?? workspace.AnswerBoundaries,
                escalationRules: request.EscalationRules ?? workspace.EscalationRules,
                aiScheduleMode: request.AiScheduleMode ?? workspace.AiScheduleMode,
                soundNotificationEnabled: request.SoundNotificationEnabled ?? workspace.SoundNotificationEnabled,
                soundTone: request.SoundTone ?? workspace.SoundTone,
                allowedDomains: request.AllowedDomains ?? workspace.AllowedDomains);

            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var defaultChannel = await EnsureDefaultChannelAsync(db, workspace.Id, agent.Id, cancellationToken);
            var (useCaseHint, assistantRoleHint) = await GetCatalogSystemPromptHintsAsync(
                db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

            var compiledInstructions = KnowledgeBaseEndpoints.BuildSystemPrompt(
                workspace.BusinessSummary,
                workspace.KeyFaq,
                workspace.AnswerBoundaries,
                workspace.EscalationRules,
                useCaseHint,
                assistantRoleHint);

            var targetName = string.IsNullOrWhiteSpace(request.AgentName)
                ? (string.IsNullOrWhiteSpace(workspace.BrandName) ? agent.Name : $"{workspace.BrandName} Asistanı")
                : request.AgentName.Trim();
            if (!KnowledgeSource.IsValidName(targetName)) targetName = agent.Name;

            var targetLanguage = string.IsNullOrWhiteSpace(request.Language)
                ? agent.Language
                : request.Language.Trim();
            if (targetLanguage is not null && !AiAgent.IsValidLanguage(targetLanguage)) targetLanguage = agent.Language;

            var targetModelTier = agent.ModelTier;
            if (!string.IsNullOrWhiteSpace(request.ModelTier)
                && Enum.TryParse<AiModelTier>(request.ModelTier.Trim(), ignoreCase: true, out var parsedTier)
                && Enum.IsDefined(parsedTier))
            {
                targetModelTier = parsedTier;
            }

            var targetEnabled = request.Enabled ?? agent.Enabled;

            agent.Apply(new AiAgentSettings(
                Name: targetName,
                Enabled: targetEnabled,
                Instructions: compiledInstructions,
                Language: targetLanguage,
                ModelTier: targetModelTier,
                KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
                KnowledgeSourceIds: [],
                HandoffOnRequest: agent.HandoffOnRequest,
                HandoffWhenUnanswered: agent.HandoffWhenUnanswered,
                MaxAiReplies: agent.MaxAiReplies), DateTimeOffset.UtcNow);

            await db.SaveChangesAsync(cancellationToken);

            var response = await BuildSettingsResponseAsync(db, workspace, agent, defaultChannel, cancellationToken);
            return Results.Ok(response);
        });

        // 3. POST /workspaces/{workspaceId}/settings/logo (Computer upload only)
        group.MapPost("/settings/logo", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            if (!httpContext.Request.HasFormContentType)
            {
                return Results.BadRequest(new
                {
                    code = "MULTIPART_FILE_REQUIRED",
                    error = "Harici logo URL'si kabul edilmemektedir. Lütfen bilgisayarınızdan bir dosya yükleyin."
                });
            }

            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var file = form.Files.FirstOrDefault();
            if (file is null || file.Length == 0)
            {
                return Results.BadRequest(new { error = "Yüklenecek logo dosyası bulunamadı veya boş." });
            }

            if (file.Length > MaxLogoSizeBytes)
            {
                return Results.BadRequest(new
                {
                    code = "LOGO_TOO_LARGE",
                    error = "Logo dosyası en fazla 2 MB olabilir."
                });
            }

            using var ms = new MemoryStream();
            await file.CopyToAsync(ms, cancellationToken);
            var bytes = ms.ToArray();

            var detectedMime = DetectAndValidateImageMimeType(bytes, file.FileName, file.ContentType);
            if (detectedMime is null)
            {
                return Results.BadRequest(new
                {
                    code = "INVALID_LOGO_FORMAT",
                    error = "Sadece geçerli PNG, JPEG, WEBP veya güvenli SVG dosyaları yüklenebilir."
                });
            }

            var base64 = Convert.ToBase64String(bytes);
            var dataUrl = $"data:{detectedMime};base64,{base64}";

            workspace!.SetLogoUrl(dataUrl);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { logoUrl = workspace.LogoUrl });
        }).DisableAntiforgery();

        // 3. DELETE /workspaces/{workspaceId}/settings/logo
        group.MapDelete("/settings/logo", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            workspace!.SetLogoUrl(null);
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new { logoUrl = (string?)null });
        });

        // 4. POST /workspaces/{workspaceId}/playground/chat (200-credit isolated Playground)
        group.MapPost("/playground/chat", async (
            Guid workspaceId,
            PlaygroundChatRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            KnowledgeRetriever retriever,
            ITextGenerator textGenerator,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (request is null || string.IsNullOrWhiteSpace(request.Message))
            {
                return Results.BadRequest(new { error = "Mesaj içeriği boş olamaz." });
            }

            var userMessage = request.Message.Trim();
            if (userMessage.Length > 4000)
            {
                return Results.BadRequest(new { error = "Mesaj en fazla 4000 karakter olabilir." });
            }

            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var planDef = await db.PlanDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Plan == workspace!.Plan, cancellationToken)
                ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace!.Plan);

            var effectiveCap = workspace!.PlaygroundMonthlyCap > 0
                ? workspace.PlaygroundMonthlyCap
                : (planDef.PlaygroundMonthlyLimit > 0 ? planDef.PlaygroundMonthlyLimit : 200);

            if (workspace.UsedPlaygroundMessagesThisMonth >= effectiveCap)
            {
                return Results.Json(new
                {
                    code = "PLAYGROUND_LIMIT_EXCEEDED",
                    error = "Aylık 200 Playground test mesajı krediniz doldu.",
                    usedPlaygroundMessages = workspace.UsedPlaygroundMessagesThisMonth,
                    playgroundMonthlyCap = effectiveCap
                }, statusCode: StatusCodes.Status429TooManyRequests);
            }

            // Increment ONLY Playground usage counter — never touch Conversations, Messages, or UsedMessagesThisMonth!
            workspace.IncrementPlaygroundUsage(1);
            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            // Retrieve RAG passages (vector search first, with DB chunk keyword fallback if embeddings are not active)
            var citations = new List<object>();
            var knowledgeSnippets = new List<string>();

            try
            {
                var retrieval = await retriever.RetrieveAsync(
                    new RetrievalRequest(
                        WorkspaceId: workspace.Id,
                        AgentId: agent.Id,
                        Query: userMessage,
                        VisitorKey: $"playground:{currentUser.UserId}",
                        Preview: true),
                    cancellationToken);

                if (retrieval.Passages.Count > 0)
                {
                    foreach (var passage in retrieval.Passages)
                    {
                        knowledgeSnippets.Add(passage.Text);
                        citations.Add(new
                        {
                            documentId = passage.Citation.DocumentId,
                            title = passage.Citation.FileName ?? passage.Citation.SourceName,
                            fileName = passage.Citation.FileName,
                            sourceUrl = passage.Citation.SourceUrl,
                            chunkSequence = passage.Citation.ChunkSequence,
                            score = passage.Score
                        });
                    }
                }
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
                // Fall back to direct DB chunk lookup below.
            }

            if (knowledgeSnippets.Count == 0)
            {
                var fallbackChunks = await LoadFallbackChunksAsync(db, workspace.Id, userMessage, cancellationToken);
                foreach (var chunk in fallbackChunks)
                {
                    knowledgeSnippets.Add(chunk.Content);
                    citations.Add(new
                    {
                        documentId = chunk.DocumentId,
                        title = chunk.Title,
                        fileName = chunk.FileName,
                        sourceUrl = chunk.SourceUrl,
                        chunkSequence = chunk.Sequence,
                        score = 1.0
                    });
                }
            }

            var (useCaseHint, assistantRoleHint) = await GetCatalogSystemPromptHintsAsync(
                db, workspace.UseCase, workspace.AssistantRole, cancellationToken);

            var systemPrompt = BuildPlaygroundSystemPrompt(
                workspace, agent, knowledgeSnippets, useCaseHint, assistantRoleHint);
            var messages = new List<TextGenerationMessage>
            {
                new(TextGenerationRole.System, systemPrompt)
            };

            if (request.History is { Length: > 0 })
            {
                foreach (var item in request.History.TakeLast(10))
                {
                    if (string.IsNullOrWhiteSpace(item?.Content)) continue;
                    var role = string.Equals(item.Role, "assistant", StringComparison.OrdinalIgnoreCase)
                        ? TextGenerationRole.Assistant
                        : TextGenerationRole.User;
                    var cleanContent = item.Content.Trim();
                    if (cleanContent.Length > 2000) cleanContent = cleanContent[..2000];
                    messages.Add(new TextGenerationMessage(role, cleanContent));
                }
            }

            messages.Add(new TextGenerationMessage(TextGenerationRole.User, userMessage));

            string reply;
            try
            {
                var result = await textGenerator.GenerateAsync(
                    new TextGenerationRequest(agent.ModelTier, messages, 1024),
                    cancellationToken);
                reply = result.Text.Trim();
            }
            catch (TextGenerationException)
            {
                reply = BuildFallbackReply(workspace, userMessage, knowledgeSnippets);
            }

            return Results.Ok(new
            {
                reply,
                citations,
                usedPlaygroundMessages = workspace.UsedPlaygroundMessagesThisMonth,
                playgroundMonthlyCap = effectiveCap
            });
        });

        // 5. GET /workspaces/{workspaceId}/widget-config
        group.MapGet("/widget-config", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace!, cancellationToken);
            var defaultChannel = await EnsureDefaultChannelAsync(db, workspace!.Id, agent.Id, cancellationToken);

            var response = await BuildWidgetConfigResponseAsync(db, workspace, defaultChannel, cancellationToken);
            return Results.Ok(response);
        });

        // 6. PUT /workspaces/{workspaceId}/widget-config
        group.MapPut("/widget-config", async (
            Guid workspaceId,
            UpdateWidgetConfigRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            if (request is null)
            {
                return Results.BadRequest(new { error = "Geçersiz widget yapılandırma gövdesi." });
            }

            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            workspace!.UpdateWidgetConfig(
                directShareActive: request.DirectShareActive ?? workspace.DirectShareActive,
                allowedDomains: request.AllowedDomains ?? workspace.AllowedDomains);

            var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
            var defaultChannel = await EnsureDefaultChannelAsync(db, workspace.Id, agent.Id, cancellationToken);
            await db.SaveChangesAsync(cancellationToken);

            var response = await BuildWidgetConfigResponseAsync(db, workspace, defaultChannel, cancellationToken);
            return Results.Ok(response);
        });

        // 7. POST /workspaces/{workspaceId}/widget-config/regenerate-key
        group.MapPost("/widget-config/regenerate-key", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, authError) = await AuthorizeWorkspaceAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            workspace!.RegenerateDirectShareKey();
            await db.SaveChangesAsync(cancellationToken);

            return Results.Ok(new
            {
                directShareKey = workspace.DirectShareKey,
                directShareUrl = $"/c/{workspace.Id}?key={workspace.DirectShareKey}"
            });
        });
    }

    private static async Task<object> BuildWidgetConfigResponseAsync(
        MaicDbContext db,
        Workspace workspace,
        Channel defaultChannel,
        CancellationToken cancellationToken)
    {
        var canRemoveBadge = await CanRemoveBadgeAsync(db, workspace, cancellationToken);
        var effectiveShowBadge = canRemoveBadge ? workspace.ShowBadge : true;

        return new
        {
            id = workspace.Id,
            workspaceId = workspace.Id,
            name = workspace.Name,
            brandName = workspace.BrandName,
            widgetTitle = workspace.WidgetTitle,
            widgetSubtitle = workspace.WidgetSubtitle,
            primaryColor = workspace.PrimaryColor,
            logoUrl = workspace.LogoUrl,
            showBadge = effectiveShowBadge,
            welcomeMessage = workspace.WelcomeMessage,
            allowedDomains = workspace.AllowedDomains ?? string.Empty,
            directShareActive = workspace.DirectShareActive,
            directShareKey = workspace.DirectShareKey,
            publishableToken = defaultChannel.PublishableToken,
            embedScriptCode = $"<script src=\"https://daribase.com/embed.js\" data-workspace-id=\"{workspace.Id}\"></script>",
            directShareUrl = $"/c/{workspace.Id}?key={workspace.DirectShareKey}"
        };
    }

    private static async Task<(Workspace? Workspace, IResult? Error)> AuthorizeWorkspaceAsync(
        MaicDbContext db,
        ICurrentUser currentUser,
        Guid workspaceId,
        bool requireManage,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return (null, Results.Unauthorized());
        }

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return (null, Results.NotFound());
        }

        var isSuperAdmin = currentUser.IsSuperAdmin
            || await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsSuperAdmin, cancellationToken);

        if (isSuperAdmin) return (workspace, null);

        var member = await db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);
        if (member is null) return (null, Results.NotFound());

        if (!requireManage) return (workspace, null);

        var hasManagePermission = await db.WorkspaceRolePermissions.AsNoTracking()
            .AnyAsync(p => p.Role == member.Role
                           && (p.PermissionKey == "settings.manage" || p.PermissionKey == "widget.manage")
                           && p.IsGranted,
                cancellationToken);

        if (!hasManagePermission && member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN))
        {
            return (null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (workspace, null);
    }

    public static async Task<Channel> EnsureDefaultChannelAsync(
        MaicDbContext db,
        Guid workspaceId,
        Guid defaultAgentId,
        CancellationToken cancellationToken)
    {
        var channel = await db.Channels
            .Where(c => c.WorkspaceId == workspaceId)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (channel is null)
        {
            channel = new Channel(workspaceId, "Web Widget", defaultAgentId);
            db.Channels.Add(channel);
            await db.SaveChangesAsync(cancellationToken);
        }
        else if (channel.DefaultAiAgentId is null)
        {
            channel.SetDefaultAiAgent(defaultAgentId);
            await db.SaveChangesAsync(cancellationToken);
        }

        return channel;
    }

    public static async Task<bool> CanRemoveBadgeAsync(
        MaicDbContext db,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        if (workspace.Plan is PlanTier.PRO or PlanTier.SCALE)
        {
            return true;
        }

        var planDef = await db.PlanDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Plan == workspace.Plan, cancellationToken);

        if (planDef is null || string.IsNullOrWhiteSpace(planDef.AllowedFeaturesJson))
        {
            return false;
        }

        try
        {
            var features = JsonSerializer.Deserialize<string[]>(planDef.AllowedFeaturesJson) ?? [];
            return features.Any(f => string.Equals(f, "remove_badge", StringComparison.OrdinalIgnoreCase));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static async Task<(string? UseCaseHint, string? AssistantRoleHint)> GetCatalogSystemPromptHintsAsync(
        MaicDbContext db,
        string? useCase,
        string? assistantRole,
        CancellationToken cancellationToken)
    {
        var ucKey = (useCase ?? string.Empty).Trim().ToLowerInvariant();
        var arKey = (assistantRole ?? string.Empty).Trim().ToLowerInvariant();

        var items = await db.WizardCatalogItems.AsNoTracking()
            .Where(i => i.IsActive
                        && ((i.Category == "USE_CASE" && i.Key == ucKey)
                            || (i.Category == "ASSISTANT_ROLE" && i.Key == arKey)))
            .ToListAsync(cancellationToken);

        var useCaseHint = items
            .FirstOrDefault(i => i.Category == "USE_CASE" && i.Key == ucKey)
            ?.SystemPromptHint;

        var assistantRoleHint = items
            .FirstOrDefault(i => i.Category == "ASSISTANT_ROLE" && i.Key == arKey)
            ?.SystemPromptHint;

        return (useCaseHint, assistantRoleHint);
    }

    private static async Task<object> BuildSettingsResponseAsync(
        MaicDbContext db,
        Workspace workspace,
        AiAgent agent,
        Channel defaultChannel,
        CancellationToken cancellationToken)
    {
        var canRemoveBadge = await CanRemoveBadgeAsync(db, workspace, cancellationToken);
        var sourceCount = await db.KnowledgeDocuments.AsNoTracking()
            .CountAsync(d => d.WorkspaceId == workspace.Id && d.Status == KnowledgeDocumentStatus.READY, cancellationToken);
        var hasReadyDocuments = sourceCount > 0;

        var hasRules = !string.IsNullOrWhiteSpace(workspace.BusinessSummary)
            || !string.IsNullOrWhiteSpace(workspace.KeyFaq)
            || !string.IsNullOrWhiteSpace(workspace.AnswerBoundaries)
            || !string.IsNullOrWhiteSpace(workspace.EscalationRules);

        var isTrained = hasReadyDocuments || hasRules;
        var effectiveShowBadge = canRemoveBadge ? workspace.ShowBadge : true;

        var activeCatalogItems = await db.WizardCatalogItems.AsNoTracking()
            .Where(i => i.IsActive)
            .OrderBy(i => i.SortOrder)
            .ThenBy(i => i.Title)
            .ToListAsync(cancellationToken);

        static object MapCatalogDto(WizardCatalogItem x) => new
        {
            id = x.Id,
            category = x.Category,
            key = x.Key,
            title = x.Title,
            description = x.Description,
            iconName = x.IconName,
            defaultWelcomeMessage = x.DefaultWelcomeMessage,
            defaultBusinessSummary = x.DefaultBusinessSummary,
            defaultKeyFaq = x.DefaultKeyFaq,
            systemPromptHint = x.SystemPromptHint,
            sortOrder = x.SortOrder,
            isActive = x.IsActive,
            updatedAt = x.UpdatedAt
        };

        var catalog = new
        {
            useCases = activeCatalogItems.Where(i => i.Category == "USE_CASE").Select(MapCatalogDto).ToArray(),
            assistantRoles = activeCatalogItems.Where(i => i.Category == "ASSISTANT_ROLE").Select(MapCatalogDto).ToArray(),
            primaryChannels = activeCatalogItems.Where(i => i.Category == "PRIMARY_CHANNEL").Select(MapCatalogDto).ToArray(),
            knowledgeMethods = activeCatalogItems.Where(i => i.Category == "KNOWLEDGE_METHOD").Select(MapCatalogDto).ToArray()
        };

        var hasUseCase = !string.IsNullOrWhiteSpace(workspace.UseCase);
        var hasAssistantRole = !string.IsNullOrWhiteSpace(workspace.AssistantRole);
        var hasPrimaryChannel = !string.IsNullOrWhiteSpace(workspace.PrimaryChannel);
        var hasBusinessSummary = (workspace.BusinessSummary?.Trim().Length ?? 0) >= 20;

        var profileScore = (hasUseCase ? 25 : 0)
            + (hasAssistantRole ? 25 : 0)
            + (hasPrimaryChannel ? 25 : 0)
            + (hasBusinessSummary ? 25 : 0);

        var faqSeedCount = string.IsNullOrWhiteSpace(workspace.KeyFaq)
            ? 0
            : workspace.KeyFaq
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Count(line => !string.IsNullOrWhiteSpace(line));

        var strengths = new List<string>();
        var missingPoints = new List<string>();

        if (hasUseCase && hasAssistantRole && hasPrimaryChannel)
        {
            strengths.Add("Kullanım alanı, asistan rolü ve ana kanal profili yapılandırıldı.");
        }
        else
        {
            missingPoints.Add("Sektör, asistan rolü veya ana kanal seçimlerinden biri eksik.");
        }

        if (hasBusinessSummary)
        {
            strengths.Add("İşletme özeti tanımlandı.");
        }
        else
        {
            missingPoints.Add("İşletme özeti henüz en az 20 karakter olacak şekilde girilmedi.");
        }

        if (faqSeedCount > 0)
        {
            strengths.Add($"Sık sorulan sorular alanında {faqSeedCount} satır/kural tanımlandı.");
        }
        else
        {
            missingPoints.Add("Henüz sık sorulan sorular (SSS) eklenmedi.");
        }

        if (sourceCount > 0)
        {
            strengths.Add($"Bilgi bankasında {sourceCount} aktif döküman var.");
        }
        else
        {
            missingPoints.Add("Bilgi bankasına henüz döküman veya URL kaynağı yüklenmedi.");
        }

        if (!string.IsNullOrWhiteSpace(defaultChannel.PublishableToken) && !string.IsNullOrWhiteSpace(workspace.DirectShareKey))
        {
            strengths.Add("Yayın anahtarı ve doğrudan paylaşım linki hazır.");
        }

        var readiness = new
        {
            profileScore,
            faqSeedCount,
            sourceCount,
            strengths = strengths.ToArray(),
            missingPoints = missingPoints.ToArray(),
            embedScriptUrl = "https://daribase.com/embed.js",
            directShareUrl = $"/c/{workspace.Id}?key={workspace.DirectShareKey}"
        };

        return new
        {
            workspaceId = workspace.Id,
            name = workspace.Name,
            plan = workspace.Plan.ToString(),
            brandName = workspace.BrandName,
            widgetTitle = workspace.WidgetTitle,
            widgetSubtitle = workspace.WidgetSubtitle,
            primaryColor = workspace.PrimaryColor,
            logoUrl = workspace.LogoUrl,
            showBadge = effectiveShowBadge,
            canRemoveBadge,
            useCase = workspace.UseCase,
            assistantRole = workspace.AssistantRole,
            primaryChannel = workspace.PrimaryChannel,
            initialKnowledgeMethod = workspace.InitialKnowledgeMethod,
            welcomeMessage = workspace.WelcomeMessage,
            businessSummary = workspace.BusinessSummary ?? string.Empty,
            keyFaq = workspace.KeyFaq ?? string.Empty,
            answerBoundaries = workspace.AnswerBoundaries ?? string.Empty,
            escalationRules = workspace.EscalationRules ?? string.Empty,
            aiScheduleMode = workspace.AiScheduleMode,
            soundNotificationEnabled = workspace.SoundNotificationEnabled,
            soundTone = workspace.SoundTone,
            allowedDomains = workspace.AllowedDomains ?? string.Empty,
            directShareActive = workspace.DirectShareActive,
            directShareKey = workspace.DirectShareKey,
            publishableToken = defaultChannel.PublishableToken,
            isTrained,
            usedPlaygroundMessagesThisMonth = workspace.UsedPlaygroundMessagesThisMonth,
            playgroundMonthlyCap = workspace.PlaygroundMonthlyCap,
            agent = new
            {
                id = agent.Id,
                name = agent.Name,
                language = agent.Language ?? "tr",
                modelTier = agent.ModelTier.ToString(),
                enabled = agent.Enabled
            },
            catalog,
            readiness
        };
    }

    private static string? DetectAndValidateImageMimeType(byte[] bytes, string? fileName, string? contentType)
    {
        if (bytes.Length < 4) return null;

        // PNG: 89 50 4E 47 0D 0A 1A 0A
        if (bytes.Length >= 8
            && bytes[0] == 0x89 && bytes[1] == 0x50 && bytes[2] == 0x4E && bytes[3] == 0x47
            && bytes[4] == 0x0D && bytes[5] == 0x0A && bytes[6] == 0x1A && bytes[7] == 0x0A)
        {
            return "image/png";
        }

        // JPEG: FF D8 FF
        if (bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
        {
            return "image/jpeg";
        }

        // WEBP: RIFF....WEBP
        if (bytes.Length >= 12
            && bytes[0] == (byte)'R' && bytes[1] == (byte)'I' && bytes[2] == (byte)'F' && bytes[3] == (byte)'F'
            && bytes[8] == (byte)'W' && bytes[9] == (byte)'E' && bytes[10] == (byte)'B' && bytes[11] == (byte)'P')
        {
            return "image/webp";
        }

        // SVG: Check UTF-8 text for <svg and ensure no script/event handler vectors
        var ext = Path.GetExtension(fileName ?? string.Empty).ToLowerInvariant();
        if (ext == ".svg" || string.Equals(contentType, "image/svg+xml", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var text = Encoding.UTF8.GetString(bytes);
                if (text.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("<script", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("javascript:", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("onload=", StringComparison.OrdinalIgnoreCase)
                    && !text.Contains("onerror=", StringComparison.OrdinalIgnoreCase))
                {
                    return "image/svg+xml";
                }
            }
            catch
            {
                return null;
            }
        }

        return null;
    }

    public sealed record FallbackChunkRow(
        Guid DocumentId,
        string Title,
        string? FileName,
        string? SourceUrl,
        int Sequence,
        string Content);

    public static async Task<IReadOnlyList<FallbackChunkRow>> LoadFallbackChunksAsync(
        MaicDbContext db,
        Guid workspaceId,
        string query,
        CancellationToken cancellationToken)
    {
        var readyDocs = await db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.WorkspaceId == workspaceId && d.Status == KnowledgeDocumentStatus.READY)
            .Join(
                db.KnowledgeSources.AsNoTracking(),
                d => new { d.WorkspaceId, Id = d.KnowledgeSourceId },
                s => new { s.WorkspaceId, s.Id },
                (d, s) => new { Doc = d, SourceName = s.Name, SourceUrl = d.SourceUrl ?? s.WebsiteUrl })
            .Take(20)
            .ToListAsync(cancellationToken);

        if (readyDocs.Count == 0) return [];

        var docMap = readyDocs.ToDictionary(x => x.Doc.Id);
        var docIds = docMap.Keys.ToArray();

        var chunks = await db.KnowledgeDocumentChunks.AsNoTracking()
            .Where(c => c.WorkspaceId == workspaceId && docIds.Contains(c.DocumentId))
            .OrderBy(c => c.Sequence)
            .Take(40)
            .ToListAsync(cancellationToken);

        if (chunks.Count == 0) return [];

        var keywords = query
            .Split([' ', '\t', '\r', '\n', ',', '.', '?', '!', ';', ':'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length >= 3)
            .Select(w => w.ToLowerInvariant())
            .Distinct()
            .ToArray();

        var ranked = chunks
            .Select(c =>
            {
                var lower = c.Content.ToLowerInvariant();
                var hits = keywords.Count(k => lower.Contains(k, StringComparison.Ordinal));
                return new { Chunk = c, Hits = hits };
            })
            .OrderByDescending(x => x.Hits)
            .ThenBy(x => x.Chunk.Sequence)
            .Take(4)
            .Select(x =>
            {
                var meta = docMap[x.Chunk.DocumentId];
                return new FallbackChunkRow(
                    DocumentId: x.Chunk.DocumentId,
                    Title: meta.Doc.FileName ?? meta.SourceName,
                    FileName: meta.Doc.FileName,
                    SourceUrl: meta.SourceUrl,
                    Sequence: x.Chunk.Sequence,
                    Content: x.Chunk.Content);
            })
            .ToList();

        return ranked;
    }

    public static string BuildPlaygroundSystemPrompt(
        Workspace workspace,
        AiAgent agent,
        IReadOnlyList<string> knowledgeSnippets,
        string? useCaseHint,
        string? assistantRoleHint)
    {
        var brand = string.IsNullOrWhiteSpace(workspace.BrandName) ? workspace.Name : workspace.BrandName;
        var compiledRules = KnowledgeBaseEndpoints.BuildSystemPrompt(
            workspace.BusinessSummary,
            workspace.KeyFaq,
            workspace.AnswerBoundaries,
            workspace.EscalationRules,
            useCaseHint,
            assistantRoleHint) ?? agent.Instructions ?? "Müşterilere nazik, net ve doğru yanıtlar ver.";

        var sb = new StringBuilder();
        sb.AppendLine($"Sen '{brand}' markasının yapay zeka müşteri destek asistanısın.");
        sb.AppendLine($"Kullanım Senaryosu: {workspace.UseCase} | Rolün: {workspace.AssistantRole} | Dil: {agent.Language ?? "tr"}");
        sb.AppendLine();
        sb.AppendLine(compiledRules);

        if (knowledgeSnippets.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine("[BİLGİ BANKASI KAYNAKLARI]");
            for (var i = 0; i < knowledgeSnippets.Count; i++)
            {
                sb.AppendLine($"--- Kaynak Parça #{i + 1} ---");
                sb.AppendLine(knowledgeSnippets[i]);
            }
        }

        return sb.ToString().Trim();
    }

    public static string BuildFallbackReply(
        Workspace workspace,
        string userMessage,
        IReadOnlyList<string> knowledgeSnippets)
    {
        var brand = string.IsNullOrWhiteSpace(workspace.BrandName) ? workspace.Name : workspace.BrandName;

        if (knowledgeSnippets.Count > 0)
        {
            var topSnippet = knowledgeSnippets[0];
            if (topSnippet.Length > 450) topSnippet = topSnippet[..450] + "...";
            return $"[{brand} Bilgi Bankası] Sorunuzla ilgili kayıtlarımızda şu bilgi yer almaktadır:\n\n{topSnippet}";
        }

        if (!string.IsNullOrWhiteSpace(workspace.KeyFaq))
        {
            return $"[{brand} Asistanı] Sıkça sorulan sorular ve kural özetimize göre:\n\n{workspace.KeyFaq}";
        }

        if (!string.IsNullOrWhiteSpace(workspace.BusinessSummary))
        {
            return $"[{brand} Asistanı] {workspace.BusinessSummary}\n\nSize \"{userMessage}\" konusunda yardımcı olmak için canlı destek ekibimize yönlendirebilirim.";
        }

        return $"Merhaba! Ben {brand} dijital destek asistanıyım. Bilgi bankası kurallarınızı ve dökümanlarınızı kaydettiğinizde sorularınıza markanıza özel yanıtlar üreteceğim.";
    }
}
