using System.Net.Mail;
using MAIC.API.Identity;
using MAIC.Application.Identity;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MAIC.API.Workspaces;

public sealed record InviteTeamMemberRequest(
    string? FullName,
    string? Email,
    string? Role);

public sealed record UpdateTeamMemberRoleRequest(
    string? Role);

public static class TeamEndpoints
{
    public static void MapTeamEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/workspaces/{workspaceId:guid}/team").RequireAuthorization();

        // 1. GET /workspaces/{workspaceId}/team
        group.MapGet("", async (
            Guid workspaceId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: false, cancellationToken);
            if (authError is not null) return authError;

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);
            var response = await BuildTeamResponseAsync(db, workspace!, cancellationToken);
            return Results.Ok(response);
        });

        // 2. POST /workspaces/{workspaceId}/team/invite
        group.MapPost("/invite", async (
            Guid workspaceId,
            InviteTeamMemberRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            if (request is null
                || string.IsNullOrWhiteSpace(request.FullName)
                || request.FullName.Trim().Length > 200
                || !IsValidEmail(request.Email))
            {
                return Results.BadRequest(new
                {
                    error = "Geçerli bir Ad Soyad ve E-posta adresi girilmelidir."
                });
            }

            var roleText = string.IsNullOrWhiteSpace(request.Role) ? "AGENT" : request.Role.Trim().ToUpperInvariant();
            if (!Enum.TryParse<WorkspaceRole>(roleText, ignoreCase: true, out var targetRole)
                || targetRole is not (WorkspaceRole.ADMIN or WorkspaceRole.AGENT))
            {
                return Results.BadRequest(new
                {
                    error = "Geçersiz rol seçimi. Yalnızca ADMIN veya AGENT rolü atanabilir."
                });
            }

            var normalizedEmail = request.Email!.Trim().ToLowerInvariant();
            var fullName = request.FullName.Trim();

            await DynamicRbacSeeder.EnsureSeededAsync(db, cancellationToken);

            var planDefinition = await db.PlanDefinitions.AsNoTracking()
                .SingleOrDefaultAsync(p => p.Plan == workspace!.Plan, cancellationToken)
                ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace!.Plan);

            var existingUser = await db.Users
                .SingleOrDefaultAsync(u => u.Email == normalizedEmail, cancellationToken);

            if (existingUser is not null)
            {
                var alreadyInThisWorkspace = await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.WorkspaceId == workspaceId && m.UserId == existingUser.Id, cancellationToken);
                if (alreadyInThisWorkspace)
                {
                    return Results.Conflict(new
                    {
                        code = "ALREADY_MEMBER",
                        error = "Bu e-posta adresi zaten çalışma alanınızda ekip üyesi olarak bulunuyor."
                    });
                }

                var inAnotherWorkspace = await db.WorkspaceMembers.AsNoTracking()
                    .AnyAsync(m => m.UserId == existingUser.Id && m.WorkspaceId != workspaceId, cancellationToken);
                if (inAnotherWorkspace)
                {
                    return Results.Conflict(new
                    {
                        code = "USER_ALREADY_IN_ANOTHER_WORKSPACE",
                        error = "Bu kullanıcı zaten başka bir çalışma alanına kayıtlı olduğu için eklenemez."
                    });
                }
            }

            var now = DateTimeOffset.UtcNow;
            var currentMemberCount = await db.WorkspaceMembers.AsNoTracking()
                .CountAsync(m => m.WorkspaceId == workspaceId, cancellationToken);
            var pendingInvitationsCount = await db.WorkspaceInvitations.AsNoTracking()
                .CountAsync(i => i.WorkspaceId == workspaceId
                                 && i.AcceptedAt == null
                                 && i.RevokedAt == null
                                 && i.ExpiresAt > now,
                    cancellationToken);

            if (currentMemberCount + pendingInvitationsCount >= planDefinition.MaxSeats)
            {
                return Results.Conflict(new
                {
                    code = "MAX_SEATS_REACHED",
                    error = $"Mevcut paketinizde en fazla {planDefinition.MaxSeats} ekip üyesi ekleyebilirsiniz. Limitinizi artırmak için paketinizi yükseltin.",
                    maxSeats = planDefinition.MaxSeats,
                    currentMembers = currentMemberCount
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            if (existingUser is null)
            {
                var tempPasswordHash = PasswordHasher.Hash($"Daribase!{Guid.NewGuid():N}");
                existingUser = User.CreateWithPassword(normalizedEmail, fullName, tempPasswordHash, isSuperAdmin: false);
                db.Users.Add(existingUser);
            }
            else if (string.IsNullOrWhiteSpace(existingUser.FullName))
            {
                existingUser.UpdateProfile(fullName);
            }

            var newMember = new WorkspaceMember(workspaceId, existingUser.Id, targetRole);
            db.WorkspaceMembers.Add(newMember);

            var pendingUserInvites = await db.WorkspaceInvitations
                .Where(i => i.WorkspaceId == workspaceId
                            && i.RecipientUserId == existingUser.Id
                            && i.AcceptedAt == null
                            && i.RevokedAt == null
                            && i.ExpiresAt > now)
                .ToListAsync(cancellationToken);

            foreach (var inv in pendingUserInvites)
            {
                inv.Accept(now);
            }

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var response = await BuildTeamResponseAsync(db, workspace!, cancellationToken);
            return Results.Ok(response);
        });

        // 3. PUT /workspaces/{workspaceId}/team/members/{memberUserId}/role
        group.MapPut("/members/{memberUserId:guid}/role", async (
            Guid workspaceId,
            Guid memberUserId,
            UpdateTeamMemberRoleRequest? request,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            if (request is null || string.IsNullOrWhiteSpace(request.Role))
            {
                return Results.BadRequest(new { error = "Rol bilgisi zorunludur." });
            }

            if (!Enum.TryParse<WorkspaceRole>(request.Role.Trim(), ignoreCase: true, out var targetRole)
                || targetRole is not (WorkspaceRole.ADMIN or WorkspaceRole.AGENT))
            {
                return Results.BadRequest(new { error = "Yalnızca ADMIN veya AGENT rolü atanabilir." });
            }

            var member = await db.WorkspaceMembers
                .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, cancellationToken);
            if (member is null)
            {
                return Results.NotFound(new { error = "Ekip üyesi bulunamadı." });
            }

            if (member.Role == WorkspaceRole.OWNER)
            {
                return Results.Conflict(new
                {
                    code = "CANNOT_MODIFY_OWNER",
                    error = "Çalışma alanı sahibinin (OWNER) rolü değiştirilemez."
                });
            }

            member.ChangeRole(targetRole);
            await db.SaveChangesAsync(cancellationToken);

            var response = await BuildTeamResponseAsync(db, workspace!, cancellationToken);
            return Results.Ok(response);
        });

        // 4. DELETE /workspaces/{workspaceId}/team/members/{memberUserId}
        group.MapDelete("/members/{memberUserId:guid}", async (
            Guid workspaceId,
            Guid memberUserId,
            HttpContext httpContext,
            ICurrentUser currentUser,
            MaicDbContext db,
            CancellationToken cancellationToken) =>
        {
            httpContext.Response.Headers.CacheControl = "no-store";
            var (workspace, _, authError) = await AuthorizeTeamAccessAsync(
                db, currentUser, workspaceId, requireManage: true, cancellationToken);
            if (authError is not null) return authError;

            var member = await db.WorkspaceMembers
                .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == memberUserId, cancellationToken);
            if (member is null)
            {
                return Results.NotFound(new { error = "Ekip üyesi bulunamadı." });
            }

            if (member.Role == WorkspaceRole.OWNER)
            {
                return Results.Conflict(new
                {
                    code = "CANNOT_REMOVE_OWNER",
                    error = "Çalışma alanı sahibi (OWNER) ekipten çıkarılamaz."
                });
            }

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

            // Unassign any conversations assigned to this member before removing the membership row.
            await db.Conversations
                .Where(c => c.WorkspaceId == workspaceId && c.AssignedUserId == memberUserId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(c => c.AssignedUserId, (Guid?)null)
                    .SetProperty(c => c.Mode, Conversation.ModeBotActive),
                    cancellationToken);

            db.WorkspaceMembers.Remove(member);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            var response = await BuildTeamResponseAsync(db, workspace!, cancellationToken);
            return Results.Ok(response);
        });
    }

    internal static async Task<object> BuildTeamResponseAsync(
        MaicDbContext db,
        Workspace workspace,
        CancellationToken cancellationToken)
    {
        var planDef = await db.PlanDefinitions.AsNoTracking()
            .SingleOrDefaultAsync(p => p.Plan == workspace.Plan, cancellationToken)
            ?? DynamicRbacSeeder.GetDefaultPlanDefinition(workspace.Plan);

        var maxSeats = Math.Max(1, planDef.MaxSeats);

        var memberRows = await db.WorkspaceMembers.AsNoTracking()
            .Where(m => m.WorkspaceId == workspace.Id)
            .Join(
                db.Users.AsNoTracking(),
                m => m.UserId,
                u => u.Id,
                (m, u) => new
                {
                    m.UserId,
                    u.FullName,
                    u.Email,
                    m.Role,
                    u.CreatedAt
                })
            .ToListAsync(cancellationToken);

        var orderedMembers = memberRows
            .OrderBy(m => m.Role switch
            {
                WorkspaceRole.OWNER => 0,
                WorkspaceRole.ADMIN => 1,
                _ => 2
            })
            .ThenBy(m => m.CreatedAt)
            .Select(m =>
            {
                var displayName = string.IsNullOrWhiteSpace(m.FullName) ? m.Email : m.FullName;
                return new
                {
                    userId = m.UserId,
                    fullName = displayName,
                    email = m.Email,
                    initials = BuildInitials(m.FullName, m.Email),
                    role = m.Role.ToString(),
                    isOwner = m.Role == WorkspaceRole.OWNER,
                    joinedAt = m.CreatedAt
                };
            })
            .ToArray();

        var now = DateTimeOffset.UtcNow;
        var pendingInvites = await db.WorkspaceInvitations.AsNoTracking()
            .Where(i => i.WorkspaceId == workspace.Id
                        && i.AcceptedAt == null
                        && i.RevokedAt == null
                        && i.ExpiresAt > now)
            .Join(
                db.Users.AsNoTracking(),
                i => i.RecipientUserId,
                u => u.Id,
                (i, u) => new
                {
                    id = i.Id,
                    recipientUserId = i.RecipientUserId,
                    fullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Email : u.FullName,
                    email = u.Email,
                    role = i.Role.ToString(),
                    createdAt = i.CreatedAt,
                    expiresAt = i.ExpiresAt
                })
            .OrderByDescending(i => i.createdAt)
            .ToArrayAsync(cancellationToken);

        var adminCount = orderedMembers.Count(m => m.role is "OWNER" or "ADMIN");

        return new
        {
            counts = new
            {
                totalMembers = orderedMembers.Length,
                maxSeats,
                adminCount,
                pendingInvitationsCount = pendingInvites.Length
            },
            members = orderedMembers,
            invitations = pendingInvites
        };
    }

    internal static async Task<(Workspace? Workspace, WorkspaceMember? Member, IResult? Error)> AuthorizeTeamAccessAsync(
        MaicDbContext db,
        ICurrentUser currentUser,
        Guid workspaceId,
        bool requireManage,
        CancellationToken cancellationToken)
    {
        if (currentUser.UserId is not { } userId)
        {
            return (null, null, Results.Unauthorized());
        }

        var workspace = await db.Workspaces
            .SingleOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (workspace is null)
        {
            return (null, null, Results.NotFound());
        }

        var isSuperAdmin = currentUser.IsSuperAdmin
            || await db.Users.AsNoTracking().AnyAsync(u => u.Id == userId && u.IsSuperAdmin, cancellationToken);

        var member = await db.WorkspaceMembers.AsNoTracking()
            .SingleOrDefaultAsync(m => m.WorkspaceId == workspaceId && m.UserId == userId, cancellationToken);

        if (isSuperAdmin)
        {
            return (workspace, member, null);
        }

        if (member is null)
        {
            return (null, null, Results.NotFound());
        }

        if (!requireManage)
        {
            return (workspace, member, null);
        }

        var hasManagePermission = await db.WorkspaceRolePermissions.AsNoTracking()
            .AnyAsync(p => p.Role == member.Role
                           && (p.PermissionKey == "team.manage" || p.PermissionKey == "members.manage")
                           && p.IsGranted,
                cancellationToken);

        if (!hasManagePermission && member.Role is not (WorkspaceRole.OWNER or WorkspaceRole.ADMIN))
        {
            return (null, null, Results.StatusCode(StatusCodes.Status403Forbidden));
        }

        return (workspace, member, null);
    }

    internal static string BuildInitials(string? fullName, string? email)
    {
        var source = !string.IsNullOrWhiteSpace(fullName) ? fullName.Trim() : (email ?? "U").Trim();
        var parts = source.Split([' ', '\t', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
        {
            return string.Concat(parts[0][0], parts[^1][0]).ToUpperInvariant();
        }
        return source.Length >= 2 ? source[..2].ToUpperInvariant() : source.ToUpperInvariant();
    }

    private static bool IsValidEmail(string? email)
    {
        if (string.IsNullOrWhiteSpace(email)) return false;
        var trimmed = email.Trim();
        if (trimmed.Length > 320 || trimmed.Any(char.IsWhiteSpace)) return false;
        return MailAddress.TryCreate(trimmed, out var parsed) && parsed.Address == trimmed;
    }
}
