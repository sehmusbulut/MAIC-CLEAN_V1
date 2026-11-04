using System.Globalization;
using System.Text.Json;
using MAIC.API.Knowledge;
using MAIC.Application.Identity;
using MAIC.Domain.Conversations;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Workspaces;

public sealed record UpgradeWorkspacePlanRequest(
    string? TargetPlan,
    string? Plan);

public static class OverviewAndBillingEndpoints
{
    private static readonly CultureInfo TrCulture = new("tr-TR");

    public static void MapOverviewAndBillingEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}").RequireAuthorization();

        // 1. GET /workspaces/{workspaceId}/overview?period=7d
        group.MapGet("/overview", async (
            Guid workspaceId,
            string? period,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await TeamEndpoints.AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var response = await BuildOverviewResponseAsync(db, workspace!, period, cancellationToken);
            return Results.Ok(response);
        });

        // 2. GET /workspaces/{workspaceId}/billing
        group.MapGet("/billing", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await TeamEndpoints.AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var response = await BuildBillingResponseAsync(db, workspace!, cancellationToken);
            return Results.Ok(response);
        });

        // 3. POST /workspaces/{workspaceId}/billing/upgrade
        group.MapPost("/billing/upgrade", async (
            Guid workspaceId,
            UpgradeWorkspacePlanRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, member, authError) = await AuthorizeBillingManageAsync(
                db, currentUser, workspaceId, cancellationToken);
            if (authError is not null) return authError;

            var targetPlanRaw = !string.IsNullOrWhiteSpace(request?.TargetPlan)
                ? request!.TargetPlan
                : request?.Plan;

            if (string.IsNullOrWhiteSpace(targetPlanRaw)
                || !Enum.TryParse<PlanTier>(targetPlanRaw.Trim(), ignoreCase: true, out var targetTier)
                || !Enum.IsDefined(targetTier))
            {
                return Results.BadRequest(new
                {
                    error = "Geçersiz paket seçimi. Desteklenen paketler: FREE, STARTER, PRO, SCALE."
                });
            }

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var planDef = await db.PlanDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Plan == targetTier, cancellationToken)
                ?? DynamicRbacSeeder.GetDefaultPlanDefinition(targetTier);

            workspace!.UpdatePlan(
                targetTier,
                planDef.MonthlyMessageLimit,
                planDef.StorageLimitBytes,
                planDef.PlaygroundMonthlyLimit > 0 ? planDef.PlaygroundMonthlyLimit : 200);

            await db.SaveChangesAsync(cancellationToken);

            var response = await BuildBillingResponseAsync(db, workspace, cancellationToken);
            return Results.Ok(response);
        });
    }

    private static async Task<object> BuildOverviewResponseAsync(
        MaicDbContext db,
        Workspace workspace,
        string? period,
        CancellationToken cancellationToken)
    {
        var normalizedPeriod = NormalizePeriod(period);
        var now = DateTimeOffset.UtcNow;
        var (periodDays, periodLabel) = normalizedPeriod switch
        {
            "24h" => (1, "24 saat"),
            "30d" => (30, "30 gün"),
            "90d" => (90, "90 gün"),
            _ => (7, "7 gün")
        };

        var periodStart = normalizedPeriod == "24h"
            ? now.AddHours(-24)
            : now.Date.AddDays(-(periodDays - 1));

        var dateRangeLabel = $"{periodStart.ToString("dd MMM", TrCulture)} - {now.ToString("dd MMM", TrCulture)}";
        var lastUpdatedLabel = $"Bugün {now.ToLocalTime():HH:mm}";

        var (_, agent) = await KnowledgeBaseEndpoints.EnsureDefaultPoolAsync(db, workspace, cancellationToken);
        var defaultChannel = await WorkspaceSettingsEndpoints.EnsureDefaultChannelAsync(db, workspace.Id, agent.Id, cancellationToken);

        var planDef = await db.PlanDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Plan == workspace.Plan, cancellationToken)
            ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace.Plan);

        // Knowledge & Readiness checks (out of 5)
        var documentCount = await db.KnowledgeDocuments.AsNoTracking()
            .CountAsync(d => d.WorkspaceId == workspace.Id && d.Status == KnowledgeDocumentStatus.READY, cancellationToken);

        var checkProfile = !string.IsNullOrWhiteSpace(workspace.UseCase)
            && !string.IsNullOrWhiteSpace(workspace.AssistantRole)
            && !string.IsNullOrWhiteSpace(workspace.PrimaryChannel);
        var checkSummary = (workspace.BusinessSummary?.Trim().Length ?? 0) >= 20;
        var checkKnowledge = documentCount > 0 || !string.IsNullOrWhiteSpace(workspace.KeyFaq);
        var checkWelcome = !string.IsNullOrWhiteSpace(workspace.WelcomeMessage);
        var checkPublishReady = !string.IsNullOrWhiteSpace(defaultChannel.PublishableToken)
            && !string.IsNullOrWhiteSpace(workspace.DirectShareKey);

        var readinessPassedCount = (checkProfile ? 1 : 0)
            + (checkSummary ? 1 : 0)
            + (checkKnowledge ? 1 : 0)
            + (checkWelcome ? 1 : 0)
            + (checkPublishReady ? 1 : 0);
        const int readinessTotalCount = 5;
        var readinessScore = readinessPassedCount * 20;

        // Conversations for queue & period KPIs
        var allConversations = await db.Conversations.AsNoTracking()
            .Where(c => c.WorkspaceId == workspace.Id)
            .Select(c => new
            {
                c.Id,
                c.ContactId,
                c.Status,
                c.Mode,
                c.AssignedUserId,
                c.CreatedAt,
                c.LastActivityAt,
                c.LastMessageSequence
            })
            .ToListAsync(cancellationToken);

        var openConversations = allConversations
            .Where(c => c.Status == ConversationState.OPEN)
            .ToList();

        var unresolvedQueueCount = openConversations.Count;
        var unassignedCount = openConversations.Count(c => c.AssignedUserId == null);

        var openConversationIds = openConversations.Select(c => c.Id).ToArray();
        var lastMessagesOfOpen = openConversationIds.Length == 0
            ? []
            : await db.Messages.AsNoTracking()
                .Where(m => m.WorkspaceId == workspace.Id && openConversationIds.Contains(m.ConversationId))
                .GroupBy(m => m.ConversationId)
                .Select(g => g.OrderByDescending(x => x.Sequence).Select(x => new { x.ConversationId, x.SenderKind }).First())
                .ToListAsync(cancellationToken);

        var unansweredCount = lastMessagesOfOpen.Count(m => m.SenderKind == MessageSenderKind.GUEST);

        var aiActiveCount = openConversations.Count(c =>
            string.Equals(c.Mode, Conversation.ModeBotActive, StringComparison.OrdinalIgnoreCase));

        var aiCoveragePercent = unresolvedQueueCount > 0
            ? Math.Round(aiActiveCount * 100.0 / unresolvedQueueCount, 1)
            : 100.0;
        var aiActiveConversationsText = $"{aiActiveCount}/{unresolvedQueueCount} AI aktif sohbet";

        // Period KPIs
        var periodConversations = allConversations
            .Where(c => c.CreatedAt >= periodStart)
            .ToList();

        var totalConversations = periodConversations.Count;
        var activeUsers = periodConversations.Select(c => c.ContactId).Distinct().Count();
        var aiSuccessCount = periodConversations.Count(c =>
            c.AssignedUserId == null
            && string.Equals(c.Mode, Conversation.ModeBotActive, StringComparison.OrdinalIgnoreCase));
        var aiSuccessRate = totalConversations > 0
            ? Math.Round(aiSuccessCount * 100.0 / totalConversations, 1)
            : 100.0;
        const double avgResponseSeconds = 3.5;

        // Daily Interactions chart (7 days for 24h/7d, 30 days for 30d/90d)
        var chartDays = periodDays <= 7 ? 7 : 30;
        var chartStartDate = now.UtcDateTime.Date.AddDays(-(chartDays - 1));
        var chartStartOffset = new DateTimeOffset(chartStartDate, TimeSpan.Zero);

        var periodMessages = await db.Messages.AsNoTracking()
            .Where(m => m.WorkspaceId == workspace.Id && m.CreatedAt >= chartStartOffset)
            .Select(m => new { m.CreatedAt, m.SenderKind })
            .ToListAsync(cancellationToken);

        var dailyInteractions = new List<object>(chartDays);
        for (var i = 0; i < chartDays; i++)
        {
            var dayDate = chartStartDate.AddDays(i);
            var dayMessages = periodMessages.Where(m => m.CreatedAt.UtcDateTime.Date == dayDate).ToList();
            var botCount = dayMessages.Count(m => m.SenderKind == MessageSenderKind.AI);
            var humanCount = dayMessages.Count(m => m.SenderKind == MessageSenderKind.MEMBER);
            var guestCount = dayMessages.Count(m => m.SenderKind == MessageSenderKind.GUEST);
            var total = botCount + humanCount + guestCount;

            dailyInteractions.Add(new
            {
                dateLabel = dayDate.ToString("dd MMM", TrCulture),
                botCount,
                humanCount,
                total
            });
        }

        // Live Feed (Last 8 messages/events)
        var recentMessages = await db.Messages.AsNoTracking()
            .Where(m => m.WorkspaceId == workspace.Id)
            .OrderByDescending(m => m.CreatedAt)
            .ThenByDescending(m => m.Sequence)
            .Take(8)
            .Join(
                db.Conversations.AsNoTracking(),
                m => new { m.WorkspaceId, Id = m.ConversationId },
                c => new { c.WorkspaceId, c.Id },
                (m, c) => new { Message = m, c.ContactId })
            .Join(
                db.Contacts.AsNoTracking(),
                mc => new { mc.Message.WorkspaceId, Id = mc.ContactId },
                ct => new { ct.WorkspaceId, ct.Id },
                (mc, ct) => new
                {
                    mc.Message.Id,
                    mc.Message.ConversationId,
                    VisitorName = ct.DisplayName,
                    mc.Message.Content,
                    mc.Message.SenderKind,
                    mc.Message.CreatedAt
                })
            .ToListAsync(cancellationToken);

        var liveFeed = recentMessages.Select(item =>
        {
            var prefix = item.SenderKind switch
            {
                MessageSenderKind.AI => "AI: ",
                MessageSenderKind.MEMBER => "Temsilci: ",
                _ => string.Empty
            };
            var rawText = item.Content.ReplaceLineEndings(" ").Trim();
            if (rawText.Length > 100) rawText = rawText[..100] + "...";

            return new
            {
                id = item.Id,
                conversationId = item.ConversationId,
                visitorName = string.IsNullOrWhiteSpace(item.VisitorName) ? "Ziyaretçi" : item.VisitorName,
                preview = prefix + rawText,
                senderKind = item.SenderKind.ToString(),
                createdAt = item.CreatedAt,
                relativeTime = FormatRelativeTime(item.CreatedAt, now)
            };
        }).ToArray();

        // System Status
        var connectedIntegrationsCount = await db.ChannelIntegrations.AsNoTracking()
            .CountAsync(i => i.WorkspaceId == workspace.Id && i.Status == ChannelIntegration.StatusConnected, cancellationToken);
        var connectedChannelsCount = 1 + connectedIntegrationsCount; // 1 (Web Widget) + connected external channels
        var memberCount = await db.WorkspaceMembers.AsNoTracking()
            .CountAsync(m => m.WorkspaceId == workspace.Id, cancellationToken);
        var maxSeats = Math.Max(1, planDef.MaxSeats);
        var effectiveMonthlyCap = workspace.MonthlyMessageCap > 0
            ? workspace.MonthlyMessageCap
            : planDef.MonthlyMessageLimit;

        var systemStatus = new
        {
            documentCount,
            widgetLive = workspace.DirectShareActive && !string.IsNullOrWhiteSpace(defaultChannel.PublishableToken),
            connectedChannelsCount = Math.Max(1, connectedChannelsCount),
            memberCount,
            maxSeats,
            aiMessagesUsed = workspace.UsedMessagesThisMonth,
            monthlyMessageCap = effectiveMonthlyCap
        };

        // Recommendations
        var recommendations = new List<object>();
        if (documentCount < 3)
        {
            recommendations.Add(new
            {
                title = "Bilgi bankası zayıf",
                description = "Daha fazla doküman ekleyerek AI yanıt kalitesini artırın.",
                targetSection = "knowledge"
            });
        }
        if (connectedChannelsCount <= 1)
        {
            recommendations.Add(new
            {
                title = "Omnichannel bağlı değil",
                description = "WhatsApp/Instagram/E-posta entegrasyonlarını aktive edin.",
                targetSection = "channels"
            });
        }
        if (!checkSummary)
        {
            recommendations.Add(new
            {
                title = "İşletme özeti eksik",
                description = "Ayarlar sihirbazından işletme özetinizi ve SSS kurallarınızı güçlendirin.",
                targetSection = "settings"
            });
        }
        if (unansweredCount > 0)
        {
            recommendations.Add(new
            {
                title = "Yanıt bekleyen sohbetler var",
                description = $"Gelen kutusunda son mesajı ziyaretçiye ait {unansweredCount} açık görüşme bulunuyor.",
                targetSection = "conversations"
            });
        }

        return new
        {
            period = normalizedPeriod,
            periodLabel,
            dateRangeLabel,
            lastUpdatedLabel,
            readinessScore,
            readinessPassedCount,
            readinessTotalCount,
            unresolvedQueueCount,
            unassignedCount,
            unansweredCount,
            aiCoveragePercent,
            aiActiveConversationsText,
            totalConversations,
            activeUsers,
            aiSuccessRate,
            avgResponseSeconds,
            dailyInteractions,
            liveFeed,
            systemStatus,
            recommendations
        };
    }

    internal static async Task<object> BuildBillingResponseAsync(
        MaicDbContext db,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var allPlans = await db.PlanDefinitions.AsNoTracking()
            .OrderBy(p => p.Plan)
            .ToListAsync(cancellationToken);

        var currentPlanDef = allPlans.FirstOrDefault(p => p.Plan == workspace.Plan)
            ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace.Plan);

        var activeDocs = await db.KnowledgeDocuments.AsNoTracking()
            .Where(d => d.WorkspaceId == workspace.Id && d.Status != KnowledgeDocumentStatus.SUPERSEDED)
            .Select(d => new { d.Origin })
            .ToListAsync(cancellationToken);

        var documentCount = activeDocs.Count(d => d.Origin is KnowledgeDocumentOrigin.UPLOAD or KnowledgeDocumentOrigin.TEXT);
        var urlCount = activeDocs.Count(d => d.Origin == KnowledgeDocumentOrigin.WEBSITE);
        var memberCount = await db.WorkspaceMembers.AsNoTracking()
            .CountAsync(m => m.WorkspaceId == workspace.Id, cancellationToken);

        var monthlyMessageCap = workspace.MonthlyMessageCap > 0
            ? workspace.MonthlyMessageCap
            : currentPlanDef.MonthlyMessageLimit;
        var storageLimitBytes = workspace.StorageLimitBytes > 0
            ? workspace.StorageLimitBytes
            : currentPlanDef.StorageLimitBytes;
        var playgroundMonthlyCap = workspace.PlaygroundMonthlyCap > 0
            ? workspace.PlaygroundMonthlyCap
            : (currentPlanDef.PlaygroundMonthlyLimit > 0 ? currentPlanDef.PlaygroundMonthlyLimit : 200);

        var plansDto = allPlans.Select(p =>
        {
            string[] allowedFeatures;
            try
            {
                allowedFeatures = JsonSerializer.Deserialize<string[]>(p.AllowedFeaturesJson) ?? [];
            }
            catch (JsonException)
            {
                allowedFeatures = [];
            }

            return new
            {
                plan = p.Plan.ToString(),
                planCode = p.Plan.ToString(),
                name = p.Name,
                displayName = p.Name,
                monthlyPriceTry = p.MonthlyPriceTry,
                monthlyMessageLimit = p.MonthlyMessageLimit,
                storageLimitBytes = p.StorageLimitBytes,
                maxStorageBytes = p.StorageLimitBytes,
                maxFileSizeBytes = p.MaxFileSizeBytes,
                maxDocumentCount = p.MaxDocumentCount,
                maxUrlSourceCount = p.MaxUrlSourceCount,
                playgroundMonthlyLimit = p.PlaygroundMonthlyLimit,
                maxSeats = p.MaxSeats,
                allowedFeatures,
                isCurrent = p.Plan == workspace.Plan
            };
        }).ToArray();

        return new
        {
            workspaceId = workspace.Id,
            currentPlan = workspace.Plan.ToString(),
            currentPlanName = currentPlanDef.Name,
            monthlyPriceTry = currentPlanDef.MonthlyPriceTry,
            usedMessagesThisMonth = workspace.UsedMessagesThisMonth,
            monthlyMessageCap,
            usedStorageBytes = workspace.UsedStorageBytes,
            storageLimitBytes,
            documentCount,
            maxDocumentCount = currentPlanDef.MaxDocumentCount,
            urlCount,
            maxUrlSourceCount = currentPlanDef.MaxUrlSourceCount,
            memberCount,
            maxSeats = currentPlanDef.MaxSeats,
            usedPlaygroundMessagesThisMonth = workspace.UsedPlaygroundMessagesThisMonth,
            playgroundMonthlyCap,
            usage = new
            {
                usedMessagesThisMonth = workspace.UsedMessagesThisMonth,
                monthlyMessageCap,
                usedStorageBytes = workspace.UsedStorageBytes,
                storageLimitBytes,
                documentCount,
                maxDocumentCount = currentPlanDef.MaxDocumentCount,
                urlCount,
                maxUrlSourceCount = currentPlanDef.MaxUrlSourceCount,
                memberCount,
                maxSeats = currentPlanDef.MaxSeats,
                usedPlaygroundMessagesThisMonth = workspace.UsedPlaygroundMessagesThisMonth,
                playgroundMonthlyCap
            },
            plans = plansDto
        };
    }

    private static async Task<(Workspace? Workspace, WorkspaceMember? Member, IResult? Error)> AuthorizeBillingManageAsync(
        MaicDbContext db,
        ICurrentUser currentUser,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        var (workspace, member, error) = await TeamEndpoints.AuthorizeTeamAccessAsync(
            db, currentUser, workspaceId, requireManage: false, cancellationToken);
        if (error is not null) return (null, null, error);

        if (currentUser.UserId is not { } userId) return (null, null, Results.Unauthorized());

        var isSuperAdmin = currentUser.IsSuperAdmin
            || await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsSuperAdmin, cancellationToken);
        if (isSuperAdmin) return (workspace, member, null);

        if (member is null) return (null, null, Results.NotFound());

        var hasBillingManage = await db.WorkspaceRolePermissions.AsNoTracking()
            .AnyAsync(p => p.Role == member.Role && p.PermissionKey == "billing.manage" && p.IsGranted, cancellationToken);

        if (!hasBillingManage && member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN))
        {
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (workspace, member, null);
    }

    private static string NormalizePeriod(string? period)
    {
        var trimmed = (period ?? "7d").Trim().ToLowerInvariant();
        return trimmed is "24h" or "7d" or "30d" or "90d" ? trimmed : "7d";
    }

    private static string FormatRelativeTime(DateTimeOffset createdAt, DateTimeOffset now)
    {
        var diff = now - createdAt;
        if (diff.TotalSeconds < 60) return "Az önce";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes} dk önce";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours} sa önce";
        return $"{(int)diff.TotalDays} gün önce";
    }
}
