using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MAIC.Application.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Workspaces;

public sealed record UpdatePlanDefinitionRequest(
    string? Name,
    int? MonthlyMessageLimit,
    long? StorageLimitBytes,
    long? MaxStorageBytes,
    long? MaxFileSizeBytes,
    int? MaxDocumentCount,
    int? MaxUrlSourceCount,
    int? MaxSeats,
    int? PlaygroundMonthlyLimit,
    decimal? MonthlyPriceTry,
    string? AllowedFeaturesJson,
    string[]? AllowedFeatures);

public sealed record UpsertWizardCatalogItemRequest(
    string? Category,
    string? Key,
    string? Title,
    string? Description,
    string? IconName,
    string? DefaultWelcomeMessage,
    string? DefaultBusinessSummary,
    string? DefaultKeyFaq,
    string? SystemPromptHint,
    int? SortOrder,
    bool? IsActive);

public sealed record UpdateSuperAdminWorkspacePlanRequest(
    string? Plan,
    int? MonthlyMessageCap,
    long? StorageLimitBytes,
    int? PlaygroundMonthlyCap);

public sealed record SuperAdminAuditLogEntry(
    Guid Id,
    DateTimeOffset Timestamp,
    Guid UserId,
    string Email,
    string IpAddress,
    string HttpMethod,
    string Path,
    string ActionSummary,
    int StatusCode);

/// <summary>
/// 4-Layer Zero-Trust Security Filter for all /superadmin/* endpoints:
/// Layer 1: Real-time Database verification of Users.IsSuperAdmin on every request.
/// Layer 2: Optional MAIC_SUPERADMIN_EMAILS whitelist verification.
/// Layer 3: Step-Up Master Key (X-SuperAdmin-Key) constant-time verification via CryptographicOperations.FixedTimeEquals on write operations.
/// Layer 4: Structured Audit Log recording timestamp, userId, email, IP address, and mutation details.
/// </summary>
public sealed class SuperAdminSecurityFilter : IEndpointFilter
{
    public const string DefaultDevMasterKey = "daribase-master-2026";
    public const string MasterKeyHeaderName = "X-SuperAdmin-Key";

    private static readonly ConcurrentQueue<SuperAdminAuditLogEntry> RecentAuditLogs = new();

    public static IReadOnlyList<SuperAdminAuditLogEntry> GetRecentAuditLogs() =>
        RecentAuditLogs.Reverse().Take(100).ToArray();

    public async ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        var httpContext = context.HttpContext;
        httpContext.Response.Headers.CacheControl = "no-store";

        var currentUser = httpContext.RequestServices.GetRequiredService<ICurrentUser>();
        var db = httpContext.RequestServices.GetRequiredService<MaicDbContext>();
        var config = httpContext.RequestServices.GetRequiredService<IConfiguration>();
        var loggerFactory = httpContext.RequestServices.GetRequiredService<ILoggerFactory>();
        var logger = loggerFactory.CreateLogger("MAIC.SuperAdmin.Audit");

        if (currentUser.UserId is not { } userId)
        {
            return Results.Unauthorized();
        }

        // Layer 1: Always verify against the database Users record (never trust session claim alone)
        var dbUser = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, httpContext.RequestAborted);

        if (dbUser is null || !dbUser.IsSuperAdmin)
        {
            logger.LogWarning(
                "[SUPERADMIN_SECURITY_DENIED] Layer 1 DB check failed for UserId={UserId}, IP={Ip}",
                userId,
                httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

            return Results.Json(new
            {
                code = "SUPERADMIN_REQUIRED",
                error = "Bu işlem için doğrulanmış SuperAdmin yetkisi gereklidir."
            }, statusCode: StatusCodes.Status403Forbidden);
        }

        // Layer 2: Email Whitelist check (if MAIC_SUPERADMIN_EMAILS is configured)
        var allowedEmailsRaw = config["MAIC_SUPERADMIN_EMAILS"]
            ?? Environment.GetEnvironmentVariable("MAIC_SUPERADMIN_EMAILS")
            ?? config["SuperAdmin:AllowedEmails"];

        if (!string.IsNullOrWhiteSpace(allowedEmailsRaw))
        {
            var whitelist = allowedEmailsRaw
                .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(e => e.ToLowerInvariant())
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            if (whitelist.Count > 0 && !whitelist.Contains(dbUser.Email))
            {
                logger.LogWarning(
                    "[SUPERADMIN_SECURITY_DENIED] Layer 2 Email Whitelist check failed for Email={Email}, UserId={UserId}, IP={Ip}",
                    dbUser.Email,
                    userId,
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

                return Results.Json(new
                {
                    code = "SUPERADMIN_EMAIL_NOT_WHITELISTED",
                    error = "E-posta adresiniz SuperAdmin güvenli beyaz listesinde (MAIC_SUPERADMIN_EMAILS) yer almıyor."
                }, statusCode: StatusCodes.Status403Forbidden);
            }
        }

        // Layer 3: Step-Up Master Key Verification (X-SuperAdmin-Key) on write methods (POST, PUT, DELETE, PATCH)
        var method = httpContext.Request.Method;
        var isWriteOperation = HttpMethods.IsPost(method)
            || HttpMethods.IsPut(method)
            || HttpMethods.IsDelete(method)
            || HttpMethods.IsPatch(method);

        if (isWriteOperation)
        {
            var configuredMasterKey = config["MAIC_SUPERADMIN_MASTER_KEY"]
                ?? Environment.GetEnvironmentVariable("MAIC_SUPERADMIN_MASTER_KEY")
                ?? config["SuperAdmin:MasterKey"]
                ?? DefaultDevMasterKey;

            var providedKey = httpContext.Request.Headers[MasterKeyHeaderName].FirstOrDefault() ?? string.Empty;

            if (!FixedTimeEqualsUtf8(providedKey.Trim(), configuredMasterKey.Trim()))
            {
                logger.LogWarning(
                    "[SUPERADMIN_SECURITY_DENIED] Layer 3 Master Key mismatch on {Method} {Path} by Email={Email} (UserId={UserId}), IP={Ip}",
                    method,
                    httpContext.Request.Path.Value,
                    dbUser.Email,
                    userId,
                    httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown");

                return Results.Json(new
                {
                    code = "INVALID_SUPERADMIN_MASTER_KEY",
                    error = "Geçersiz veya eksik X-SuperAdmin-Key güvenlik anahtarı. Yazma işlemleri için geçerli Master Key zorunludur."
                }, statusCode: StatusCodes.Status403Forbidden);
            }
        }

        var result = await next(context);

        // Layer 4: Audit Log for every write operation
        if (isWriteOperation)
        {
            var ip = httpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            var path = httpContext.Request.Path.Value ?? "/superadmin";
            var detail = httpContext.Items.TryGetValue("SuperAdminAuditDetail", out var detailObj) && detailObj is string s
                ? s
                : $"{method} {path}";

            var entry = new SuperAdminAuditLogEntry(
                Id: Guid.CreateVersion7(),
                Timestamp: DateTimeOffset.UtcNow,
                UserId: userId,
                Email: dbUser.Email,
                IpAddress: ip,
                HttpMethod: method,
                Path: path,
                ActionSummary: detail,
                StatusCode: httpContext.Response.StatusCode);

            RecentAuditLogs.Enqueue(entry);
            while (RecentAuditLogs.Count > 200 && RecentAuditLogs.TryDequeue(out _)) { }

            logger.LogInformation(
                "[SUPERADMIN_AUDIT] Timestamp={Timestamp:O} UserId={UserId} Email={Email} IP={Ip} Method={Method} Path={Path} Detail={Detail}",
                entry.Timestamp,
                entry.UserId,
                entry.Email,
                entry.IpAddress,
                entry.HttpMethod,
                entry.Path,
                entry.ActionSummary);
        }

        return result;
    }

    private static bool FixedTimeEqualsUtf8(string left, string right)
    {
        if (string.IsNullOrEmpty(left) || string.IsNullOrEmpty(right))
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}

public static class SuperAdminPlanEndpoints
{
    public static void MapSuperAdminPlanEndpoints(this WebApplication app)
    {
        var superAdminRoot = app.MapGroup("/superadmin")
            .RequireAuthorization()
            .AddEndpointFilter<SuperAdminSecurityFilter>();

        var plansGroup = superAdminRoot.MapGroup("/plans");

        plansGroup.MapGet("", async (
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var plans = await db.PlanDefinitions.AsNoTracking()
                .OrderBy(p => p.Plan)
                .ToListAsync(cancellationToken);

            return Results.Ok(plans.Select(ToPlanDto));
        });

        plansGroup.MapPut("/{plan}", async (
            string plan,
            UpdatePlanDefinitionRequest? request,
            HttpContext context,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (!Enum.TryParse<PlanTier>(plan, ignoreCase: true, out var planTier) || !Enum.IsDefined(planTier))
            {
                return Results.NotFound(new { error = "Geçersiz paket adı." });
            }

            if (request is null)
            {
                return Results.BadRequest(new { error = "Paket güncelleme verisi boş olamaz." });
            }

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var definition = await db.PlanDefinitions
                .SingleOrDefaultAsync(p => p.Plan == planTier, cancellationToken);
            var fallback = definition ?? DynamicRbacSeeder.GetDefaultPlanDefinition(planTier);

            var monthlyMessageLimit = request.MonthlyMessageLimit ?? fallback.MonthlyMessageLimit;
            var storageLimitBytes = request.StorageLimitBytes ?? request.MaxStorageBytes ?? fallback.StorageLimitBytes;
            var maxFileSizeBytes = request.MaxFileSizeBytes ?? fallback.MaxFileSizeBytes;
            var maxDocumentCount = request.MaxDocumentCount ?? fallback.MaxDocumentCount;
            var maxUrlSourceCount = request.MaxUrlSourceCount ?? fallback.MaxUrlSourceCount;
            var maxSeats = request.MaxSeats ?? fallback.MaxSeats;
            var playgroundLimit = request.PlaygroundMonthlyLimit ?? fallback.PlaygroundMonthlyLimit;
            var monthlyPriceTry = request.MonthlyPriceTry ?? fallback.MonthlyPriceTry;

            if (monthlyMessageLimit < 0
                || storageLimitBytes < 0
                || maxFileSizeBytes < 0
                || maxDocumentCount < 0
                || maxUrlSourceCount < 0
                || playgroundLimit < 0
                || maxSeats < 1
                || monthlyPriceTry < 0)
            {
                return Results.BadRequest(new { error = "Paket limit veya fiyat değerleri geçersiz." });
            }

            var featuresJson = !string.IsNullOrWhiteSpace(request.AllowedFeaturesJson)
                ? request.AllowedFeaturesJson.Trim()
                : request.AllowedFeatures is not null
                    ? JsonSerializer.Serialize(request.AllowedFeatures)
                    : fallback.AllowedFeaturesJson;

            var name = string.IsNullOrWhiteSpace(request.Name)
                ? fallback.Name
                : request.Name.Trim();

            if (definition is null)
            {
                definition = new PlanDefinition(
                    planTier,
                    name,
                    monthlyMessageLimit,
                    storageLimitBytes,
                    maxSeats,
                    featuresJson,
                    maxFileSizeBytes,
                    maxDocumentCount,
                    maxUrlSourceCount,
                    playgroundLimit,
                    monthlyPriceTry);
                db.PlanDefinitions.Add(definition);
            }
            else
            {
                definition.Update(
                    name,
                    monthlyMessageLimit,
                    storageLimitBytes,
                    maxSeats,
                    featuresJson,
                    maxFileSizeBytes,
                    maxDocumentCount,
                    maxUrlSourceCount,
                    playgroundLimit,
                    monthlyPriceTry);
            }

            await db.Workspaces
                .Where(w => w.Plan == planTier)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(w => w.MonthlyMessageCap, monthlyMessageLimit)
                    .SetProperty(w => w.StorageLimitBytes, storageLimitBytes)
                    .SetProperty(w => w.PlaygroundMonthlyCap, playgroundLimit),
                    cancellationToken);

            await db.SaveChangesAsync(cancellationToken);

            context.Items["SuperAdminAuditDetail"] =
                $"PlanDefinition '{planTier}' güncellendi: MaxSeats={maxSeats}, MsgLimit={monthlyMessageLimit}, Storage={storageLimitBytes}, Docs={maxDocumentCount}, Urls={maxUrlSourceCount}, PriceTRY={monthlyPriceTry}";

            return Results.Ok(ToPlanDto(definition));
        });

        // SuperAdmin Dynamic Wizard Catalog Endpoints (/superadmin/wizard-catalog)
        var catalogGroup = superAdminRoot.MapGroup("/wizard-catalog");

        catalogGroup.MapGet("", async (
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var items = await db.WizardCatalogItems.AsNoTracking()
                .OrderBy(x => x.Category)
                .ThenBy(x => x.SortOrder)
                .ThenBy(x => x.Title)
                .ToListAsync(cancellationToken);

            return Results.Ok(items.Select(ToCatalogDto));
        });

        catalogGroup.MapPost("", async (
            UpsertWizardCatalogItemRequest? request,
            HttpContext context,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (request is null
                || string.IsNullOrWhiteSpace(request.Category)
                || string.IsNullOrWhiteSpace(request.Key)
                || string.IsNullOrWhiteSpace(request.Title)
                || string.IsNullOrWhiteSpace(request.Description))
            {
                return Results.BadRequest(new { error = "Kategori, anahtar (key), başlık ve açıklama zorunludur." });
            }

            var category = request.Category.Trim().ToUpperInvariant();
            if (!WizardCatalogItem.ValidCategories.Contains(category, StringComparer.Ordinal))
            {
                return Results.BadRequest(new
                {
                    error = $"Geçersiz kategori. Desteklenen kategoriler: {string.Join(", ", WizardCatalogItem.ValidCategories)}"
                });
            }

            var key = request.Key.Trim().ToLowerInvariant();
            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            if (await db.WizardCatalogItems.AnyAsync(x => x.Category == category && x.Key == key, cancellationToken))
            {
                return Results.Conflict(new
                {
                    code = "CATALOG_KEY_ALREADY_EXISTS",
                    error = $"'{category}' kategorisinde '{key}' anahtarı zaten mevcut."
                });
            }

            var nextSortOrder = request.SortOrder ?? ((await db.WizardCatalogItems
                .Where(x => x.Category == category)
                .Select(x => (int?)x.SortOrder)
                .MaxAsync(cancellationToken)) ?? 0) + 1;

            var item = new WizardCatalogItem(
                category: category,
                key: key,
                title: request.Title,
                description: request.Description,
                iconName: string.IsNullOrWhiteSpace(request.IconName) ? "Sparkles" : request.IconName,
                sortOrder: nextSortOrder,
                isActive: request.IsActive ?? true,
                defaultWelcomeMessage: request.DefaultWelcomeMessage,
                defaultBusinessSummary: request.DefaultBusinessSummary,
                defaultKeyFaq: request.DefaultKeyFaq,
                systemPromptHint: request.SystemPromptHint);

            db.WizardCatalogItems.Add(item);
            await db.SaveChangesAsync(cancellationToken);

            context.Items["SuperAdminAuditDetail"] =
                $"WizardCatalogItem eklendi: Category={category}, Key={key}, Title={item.Title}";

            return Results.Created($"/superadmin/wizard-catalog/{item.Id}", ToCatalogDto(item));
        });

        catalogGroup.MapPut("/{id:guid}", async (
            Guid id,
            UpsertWizardCatalogItemRequest? request,
            HttpContext context,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            if (request is null)
            {
                return Results.BadRequest(new { error = "Geçersiz istek gövdesi." });
            }

            var item = await db.WizardCatalogItems
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound(new { error = "Katalog öğesi bulunamadı." });
            }

            var targetCategory = string.IsNullOrWhiteSpace(request.Category)
                ? item.Category
                : request.Category.Trim().ToUpperInvariant();
            if (!WizardCatalogItem.ValidCategories.Contains(targetCategory, StringComparer.Ordinal))
            {
                return Results.BadRequest(new { error = "Geçersiz kategori." });
            }

            var targetKey = string.IsNullOrWhiteSpace(request.Key)
                ? item.Key
                : request.Key.Trim().ToLowerInvariant();

            if ((targetCategory != item.Category || targetKey != item.Key)
                && await db.WizardCatalogItems.AnyAsync(
                    x => x.Id != id && x.Category == targetCategory && x.Key == targetKey,
                    cancellationToken))
            {
                return Results.Conflict(new
                {
                    code = "CATALOG_KEY_ALREADY_EXISTS",
                    error = $"'{targetCategory}' kategorisinde '{targetKey}' anahtarı zaten mevcut."
                });
            }

            item.Update(
                title: string.IsNullOrWhiteSpace(request.Title) ? item.Title : request.Title,
                description: string.IsNullOrWhiteSpace(request.Description) ? item.Description : request.Description,
                iconName: string.IsNullOrWhiteSpace(request.IconName) ? item.IconName : request.IconName,
                sortOrder: request.SortOrder ?? item.SortOrder,
                isActive: request.IsActive ?? item.IsActive,
                defaultWelcomeMessage: request.DefaultWelcomeMessage ?? item.DefaultWelcomeMessage,
                defaultBusinessSummary: request.DefaultBusinessSummary ?? item.DefaultBusinessSummary,
                defaultKeyFaq: request.DefaultKeyFaq ?? item.DefaultKeyFaq,
                systemPromptHint: request.SystemPromptHint ?? item.SystemPromptHint,
                category: targetCategory,
                key: targetKey);

            await db.SaveChangesAsync(cancellationToken);

            context.Items["SuperAdminAuditDetail"] =
                $"WizardCatalogItem güncellendi: Id={id}, Category={item.Category}, Key={item.Key}, IsActive={item.IsActive}";

            return Results.Ok(ToCatalogDto(item));
        });

        catalogGroup.MapDelete("/{id:guid}", async (
            Guid id,
            bool? deactivateOnly,
            HttpContext context,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            var item = await db.WizardCatalogItems
                .SingleOrDefaultAsync(x => x.Id == id, cancellationToken);
            if (item is null)
            {
                return Results.NotFound(new { error = "Katalog öğesi bulunamadı." });
            }

            if (deactivateOnly == true)
            {
                item.Update(
                    title: item.Title,
                    description: item.Description,
                    iconName: item.IconName,
                    sortOrder: item.SortOrder,
                    isActive: false,
                    defaultWelcomeMessage: item.DefaultWelcomeMessage,
                    defaultBusinessSummary: item.DefaultBusinessSummary,
                    defaultKeyFaq: item.DefaultKeyFaq,
                    systemPromptHint: item.SystemPromptHint);
                await db.SaveChangesAsync(cancellationToken);

                context.Items["SuperAdminAuditDetail"] =
                    $"WizardCatalogItem pasife alındı: Id={id}, Key={item.Key}";

                return Results.Ok(ToCatalogDto(item));
            }

            db.WizardCatalogItems.Remove(item);
            await db.SaveChangesAsync(cancellationToken);

            context.Items["SuperAdminAuditDetail"] =
                $"WizardCatalogItem silindi: Id={id}, Category={item.Category}, Key={item.Key}";

            return Results.Ok(new { deletedId = id });
        });

        // SuperAdmin Workspaces & Audit Logs
        superAdminRoot.MapGet("/workspaces", async (
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            var workspaces = await db.Workspaces.AsNoTracking()
                .OrderByDescending(w => w.CreatedAt)
                .Select(w => new
                {
                    id = w.Id,
                    name = w.Name,
                    plan = w.Plan.ToString(),
                    usedMessagesThisMonth = w.UsedMessagesThisMonth,
                    monthlyMessageCap = w.MonthlyMessageCap,
                    usedStorageBytes = w.UsedStorageBytes,
                    storageLimitBytes = w.StorageLimitBytes,
                    usedPlaygroundMessagesThisMonth = w.UsedPlaygroundMessagesThisMonth,
                    playgroundMonthlyCap = w.PlaygroundMonthlyCap,
                    memberCount = db.WorkspaceMembers.Count(m => m.WorkspaceId == w.Id),
                    createdAt = w.CreatedAt
                })
                .ToListAsync(cancellationToken);

            return Results.Ok(workspaces);
        });

        superAdminRoot.MapPut("/workspaces/{workspaceId:guid}/plan", async (
            Guid workspaceId,
            UpdateSuperAdminWorkspacePlanRequest? request,
            HttpContext context,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            var workspace = await db.Workspaces
                .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
            if (workspace is null)
            {
                return Results.NotFound(new { error = "Workspace bulunamadı." });
            }

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var targetTier = workspace.Plan;
            if (!string.IsNullOrWhiteSpace(request?.Plan))
            {
                if (!Enum.TryParse<PlanTier>(request.Plan.Trim(), ignoreCase: true, out targetTier) || !Enum.IsDefined(targetTier))
                {
                    return Results.BadRequest(new { error = "Geçersiz paket adı." });
                }
            }

            var planDef = await db.PlanDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Plan == targetTier, cancellationToken)
                ?? DynamicRbacSeeder.GetDefaultPlanDefinition(targetTier);

            var monthlyCap = request?.MonthlyMessageCap ?? planDef.MonthlyMessageLimit;
            var storageCap = request?.StorageLimitBytes ?? planDef.StorageLimitBytes;
            var playgroundCap = request?.PlaygroundMonthlyCap ?? planDef.PlaygroundMonthlyLimit;

            workspace.UpdatePlan(targetTier, monthlyCap, storageCap, playgroundCap);
            await db.SaveChangesAsync(cancellationToken);

            context.Items["SuperAdminAuditDetail"] =
                $"Workspace '{workspace.Name}' ({workspace.Id}) paketi '{targetTier}' olarak güncellendi.";

            return Results.Ok(new
            {
                id = workspace.Id,
                name = workspace.Name,
                plan = workspace.Plan.ToString(),
                monthlyMessageCap = workspace.MonthlyMessageCap,
                storageLimitBytes = workspace.StorageLimitBytes,
                playgroundMonthlyCap = workspace.PlaygroundMonthlyCap
            });
        });

        superAdminRoot.MapGet("/audit-logs", () =>
            Results.Ok(SuperAdminAuditLogEntryList()));
    }

    private static IReadOnlyList<SuperAdminAuditLogEntry> SuperAdminAuditLogEntryList() =>
        SuperAdminSecurityFilter.GetRecentAuditLogs();

    private static object ToPlanDto(PlanDefinition p)
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
            allowedFeaturesJson = p.AllowedFeaturesJson,
            allowedFeatures,
            updatedAt = p.UpdatedAt
        };
    }

    public static object ToCatalogDto(WizardCatalogItem item) => new
    {
        id = item.Id,
        category = item.Category,
        key = item.Key,
        title = item.Title,
        description = item.Description,
        iconName = item.IconName,
        defaultWelcomeMessage = item.DefaultWelcomeMessage,
        defaultBusinessSummary = item.DefaultBusinessSummary,
        defaultKeyFaq = item.DefaultKeyFaq,
        systemPromptHint = item.SystemPromptHint,
        sortOrder = item.SortOrder,
        isActive = item.IsActive,
        updatedAt = item.UpdatedAt
    };
}
