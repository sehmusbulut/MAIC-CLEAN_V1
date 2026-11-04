using System.Net.Mail;
using System.Security.Claims;
using System.Text.Json;
using MAIC.Application.Identity;
using MAIC.Domain.AI;
using MAIC.Domain.Channels;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Identity;

public sealed record RegisterRequest(string? FullName, string? WorkspaceName, string? Email, string? Password);
public sealed record LoginRequest(string? Email, string? Password);

public static class IdentityEndpoints
{
    public static void MapIdentityEndpoints(this WebApplication app)
    {
        app.MapGet("/auth/csrf", (HttpContext context, IAntiforgery antiforgery) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { token = antiforgery.GetAndStoreTokens(context).RequestToken });
        });

        app.MapPost("/auth/register", async (
            HttpContext context,
            RegisterRequest? request,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request is null
                || string.IsNullOrWhiteSpace(request.FullName)
                || request.FullName.Trim().Length > 200
                || !IsValidEmail(request.Email)
                || string.IsNullOrEmpty(request.Password)
                || request.Password.Length < 8)
            {
                return Results.BadRequest(new { error = "Geçerli ad soyad, e-posta ve en az 8 karakterli şifre gereklidir." });
            }

            var normalizedEmail = request.Email!.Trim().ToLowerInvariant();
            var fullName = request.FullName.Trim();
            var workspaceName = string.IsNullOrWhiteSpace(request.WorkspaceName)
                ? $"{fullName} Çalışma Alanı"
                : request.WorkspaceName.Trim();

            if (!Workspace.IsValidName(workspaceName))
            {
                return Results.BadRequest(new { error = "Çalışma alanı adı geçersiz." });
            }

            if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
            {
                return Results.Conflict(new { error = "Bu e-posta adresi zaten kayıtlı." });
            }

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (await db.Users.AnyAsync(u => u.Email == normalizedEmail, cancellationToken))
            {
                return Results.Conflict(new { error = "Bu e-posta adresi zaten kayıtlı." });
            }

            var isFirstUser = !await db.Users.AnyAsync(cancellationToken);
            var passwordHash = PasswordHasher.Hash(request.Password);
            var user = User.CreateWithPassword(normalizedEmail, fullName, passwordHash, isSuperAdmin: isFirstUser);
            db.Users.Add(user);

            CreateWorkspacePoolEntities(db, user.Id, workspaceName);

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            await SignInUserAsync(context, user.Id, user.IsSuperAdmin);
            var me = await BuildMeResponseAsync(db, user.Id, cancellationToken);
            return Results.Created("/auth/me", me);
        });

        app.MapPost("/auth/login", async (
            HttpContext context,
            LoginRequest? request,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (request is null || string.IsNullOrWhiteSpace(request.Email) || string.IsNullOrEmpty(request.Password))
            {
                return Results.Unauthorized();
            }

            var normalizedEmail = request.Email.Trim().ToLowerInvariant();
            var user = await db.Users.AsNoTracking()
                .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

            if (user is null || string.IsNullOrWhiteSpace(user.PasswordHash) || !PasswordHasher.Verify(request.Password, user.PasswordHash))
            {
                return Results.Unauthorized();
            }

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            await SignInUserAsync(context, user.Id, user.IsSuperAdmin);
            var me = await BuildMeResponseAsync(db, user.Id, cancellationToken);
            return Results.Ok(me);
        });

        app.MapPost("/auth/logout", async (HttpContext context, IAntiforgery antiforgery) =>
        {
            if (!await ValidateAsync(context, antiforgery)) return Results.BadRequest();
            await context.SignOutAsync(IdentitySetup.SessionScheme);
            return Results.NoContent();
        }).RequireAuthorization();

        app.MapGet("/auth/me", async (
            HttpContext context,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (currentUser.UserId is not { } userId) return Results.Unauthorized();

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var me = await BuildMeResponseAsync(db, userId, cancellationToken);
            return me is not null ? Results.Ok(me) : Results.Unauthorized();
        }).RequireAuthorization();
    }

    private static async Task SignInUserAsync(HttpContext context, Guid userId, bool isSuperAdmin = false)
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [
                new Claim(HttpCurrentUser.UserIdClaim, userId.ToString()),
                new Claim(ClaimTypes.NameIdentifier, userId.ToString()),
                new Claim(HttpCurrentUser.SuperAdminClaim, isSuperAdmin ? "true" : "false")
            ],
            IdentitySetup.SessionScheme));
        await context.SignInAsync(
            IdentitySetup.SessionScheme,
            principal,
            new AuthenticationProperties { IsPersistent = false });
    }

    private static Workspace CreateWorkspacePoolEntities(MaicDbContext db, Guid userId, string workspaceName)
    {
        var workspace = new Workspace(workspaceName);
        db.Workspaces.Add(workspace);

        var ownerMember = new WorkspaceMember(workspace.Id, userId, WorkspaceRole.OWNER);
        db.WorkspaceMembers.Add(ownerMember);

        var defaultKnowledgeSource = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.FILE, "Ana Bilgi Bankası", null, null);
        db.KnowledgeSources.Add(defaultKnowledgeSource);

        var now = DateTimeOffset.UtcNow;
        var defaultAgentSettings = new AiAgentSettings(
            Name: "Destek Asistanı",
            Enabled: true,
            Instructions: null,
            Language: "tr",
            ModelTier: AiModelTier.BALANCED,
            KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
            KnowledgeSourceIds: [],
            HandoffOnRequest: true,
            HandoffWhenUnanswered: true,
            MaxAiReplies: null);
        var defaultAgent = new AiAgent(workspace.Id, defaultAgentSettings, now);
        db.AiAgents.Add(defaultAgent);

        var defaultChannel = new Channel(workspace.Id, "Web Widget", defaultAgent.Id);
        db.Channels.Add(defaultChannel);

        return workspace;
    }

    private static async Task EnsureDefaultPoolAsync(MaicDbContext db, User user, CancellationToken cancellationToken)
    {
        var existingMember = await db.WorkspaceMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);

        if (existingMember is null)
        {
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            existingMember = await db.WorkspaceMembers.AsNoTracking()
                .FirstOrDefaultAsync(m => m.UserId == user.Id, cancellationToken);

            if (existingMember is null)
            {
                var baseName = string.IsNullOrWhiteSpace(user.FullName)
                    ? "Varsayılan Çalışma Alanı"
                    : $"{user.FullName.Trim()} Çalışma Alanı";
                if (baseName.Length > 200) baseName = baseName[..200].Trim();
                if (!Workspace.IsValidName(baseName)) baseName = "Varsayılan Çalışma Alanı";

                CreateWorkspacePoolEntities(db, user.Id, baseName);
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        var workspaceId = existingMember.WorkspaceId;
        var hasKnowledgeSource = await db.KnowledgeSources.AsNoTracking()
            .AnyAsync(s => s.WorkspaceId == workspaceId, cancellationToken);
        var existingAgent = await db.AiAgents.AsNoTracking()
            .FirstOrDefaultAsync(a => a.WorkspaceId == workspaceId, cancellationToken);
        var hasChannel = await db.Channels.AsNoTracking()
            .AnyAsync(c => c.WorkspaceId == workspaceId, cancellationToken);

        if (!hasKnowledgeSource || existingAgent is null || !hasChannel)
        {
            if (!hasKnowledgeSource)
            {
                db.KnowledgeSources.Add(new KnowledgeSource(workspaceId, KnowledgeSourceKind.FILE, "Ana Bilgi Bankası", null, null));
            }

            var agentId = existingAgent?.Id;
            if (existingAgent is null)
            {
                var defaultAgentSettings = new AiAgentSettings(
                    Name: "Destek Asistanı",
                    Enabled: true,
                    Instructions: null,
                    Language: "tr",
                    ModelTier: AiModelTier.BALANCED,
                    KnowledgeScope: AiKnowledgeScope.ALL_SOURCES,
                    KnowledgeSourceIds: [],
                    HandoffOnRequest: true,
                    HandoffWhenUnanswered: true,
                    MaxAiReplies: null);
                var createdAgent = new AiAgent(workspaceId, defaultAgentSettings, DateTimeOffset.UtcNow);
                db.AiAgents.Add(createdAgent);
                agentId = createdAgent.Id;
            }

            if (!hasChannel)
            {
                db.Channels.Add(new Channel(workspaceId, "Web Widget", agentId));
            }

            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static async Task<object?> BuildMeResponseAsync(MaicDbContext db, Guid userId, CancellationToken cancellationToken)
    {
        var user = await db.Users.AsNoTracking()
            .SingleOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null) return null;

        await EnsureDefaultPoolAsync(db, user, cancellationToken);

        var membership = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.UserId == userId)
            .Join(
                db.Workspaces.AsNoTracking(),
                m => m.WorkspaceId,
                w => w.Id,
                (m, w) => new { Member = m, Workspace = w })
            .FirstOrDefaultAsync(cancellationToken);

        string[] permissions = [];
        string[] planFeatures = [];
        object? workspaceDto = null;

        if (membership is not null)
        {
            permissions = await db.WorkspaceRolePermissions.AsNoTracking()
                .Where(p => p.Role == membership.Member.Role && p.IsGranted)
                .OrderBy(p => p.PermissionKey)
                .Select(p => p.PermissionKey)
                .ToArrayAsync(cancellationToken);

            if (permissions.Length == 0)
            {
                permissions = DynamicRbacSeeder.GetDefaultPermissions(membership.Member.Role).ToArray();
            }

            var planDef = await db.PlanDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Plan == membership.Workspace.Plan, cancellationToken);

            if (planDef is not null)
            {
                try
                {
                    planFeatures = JsonSerializer.Deserialize<string[]>(planDef.AllowedFeaturesJson) ?? [];
                }
                catch (JsonException)
                {
                    planFeatures = DynamicRbacSeeder.GetDefaultPlanFeatures(membership.Workspace.Plan).ToArray();
                }
            }
            else
            {
                planFeatures = DynamicRbacSeeder.GetDefaultPlanFeatures(membership.Workspace.Plan).ToArray();
            }

            workspaceDto = new
            {
                id = membership.Workspace.Id,
                name = membership.Workspace.Name,
                role = membership.Member.Role.ToString(),
                plan = membership.Workspace.Plan.ToString(),
                monthlyMessageCap = membership.Workspace.MonthlyMessageCap,
                usedMessagesThisMonth = membership.Workspace.UsedMessagesThisMonth,
                storageLimitBytes = membership.Workspace.StorageLimitBytes,
                usedStorageBytes = membership.Workspace.UsedStorageBytes,
                primaryColor = membership.Workspace.PrimaryColor,
                widgetTitle = membership.Workspace.WidgetTitle,
                logoUrl = membership.Workspace.LogoUrl,
                usedPlaygroundMessagesThisMonth = membership.Workspace.UsedPlaygroundMessagesThisMonth,
                playgroundMonthlyCap = membership.Workspace.PlaygroundMonthlyCap
            };
        }

        return new
        {
            userId = user.Id,
            email = user.Email,
            fullName = user.FullName,
            isSuperAdmin = user.IsSuperAdmin,
            workspace = workspaceDto,
            permissions,
            planFeatures
        };
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var trimmed = email.Trim();
        if (trimmed.Length > 320 || !trimmed.Contains('@')) return false;
        return MailAddress.TryCreate(trimmed, out var parsed) && string.Equals(parsed.Address, trimmed, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<bool> ValidateAsync(HttpContext context, IAntiforgery antiforgery)
    {
        try { await antiforgery.ValidateRequestAsync(context); return true; }
        catch (AntiforgeryValidationException) { return false; }
    }
}
