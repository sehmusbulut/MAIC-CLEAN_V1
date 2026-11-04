using MAIC.Application.Workspaces;
using MAIC.Domain.Workspaces;
using MAIC.Domain.Conversations;
using Microsoft.EntityFrameworkCore;

namespace MAIC.Infrastructure.Persistence;

public sealed class MembershipStore(MaicDbContext db) : IMembershipStore
{
    private static InvitationView View(WorkspaceInvitation i) => new(i.Id, i.WorkspaceId, i.RecipientUserId,
        i.InviterUserId, i.Role, i.CreatedAt, i.ExpiresAt, i.AcceptedAt, i.RevokedAt);

    // Every writer takes workspace -> ordered members -> ordered conversations/invitation locks.
    // Member locks also coordinate with existing direct membership writers.
    private async Task<List<WorkspaceMember>> LockAsync(Guid workspaceId, CancellationToken ct)
    {
        await db.Workspaces.FromSqlInterpolated($"SELECT * FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
        return await db.WorkspaceMembers.FromSqlInterpolated($"SELECT * FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspaceId} ORDER BY \"Id\" FOR UPDATE")
            .AsNoTracking().ToListAsync(ct);
    }

    public async Task<MembershipResult> ListMembersAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        // A single statement observes authorization and data from the same snapshot.
        var result = await db.WorkspaceMembers.AsNoTracking()
            .Where(actor => actor.WorkspaceId == workspaceId && actor.UserId == actorId)
            .Select(actor => new
            {
                Members = db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId && m.WorkspaceId == actor.WorkspaceId)
                    .OrderBy(m => m.UserId).Skip(skip).Take(take)
                    .Select(m => new MemberView(m.UserId, m.Role)).ToList()
            }).SingleOrDefaultAsync(cancellationToken);
        return result is null ? new(MembershipStatus.NotFound) : new(MembershipStatus.Success, Members: result.Members);
    }

    public Task<MembershipResult> ChangeRoleAsync(Guid actorId, Guid workspaceId, Guid targetUserId, WorkspaceRole role, CancellationToken cancellationToken) =>
        ChangeMemberAsync(actorId, workspaceId, targetUserId, role, cancellationToken);
    public Task<MembershipResult> RemoveAsync(Guid actorId, Guid workspaceId, Guid targetUserId, CancellationToken cancellationToken) =>
        ChangeMemberAsync(actorId, workspaceId, targetUserId, null, cancellationToken);

    private async Task<MembershipResult> ChangeMemberAsync(Guid actorId, Guid workspaceId, Guid targetUserId, WorkspaceRole? role, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var members = await LockAsync(workspaceId, ct);
        var actor = members.SingleOrDefault(m => m.UserId == actorId);
        if (actor is null) return new(MembershipStatus.NotFound);
        if (role is { } requested && !Enum.IsDefined(requested)) return new(MembershipStatus.Invalid);
        var target = members.SingleOrDefault(m => m.UserId == targetUserId);
        if (target is null) return new(MembershipStatus.NotFound);
        if (role is not null ? !MembershipPolicy.CanChangeRole(actor.Role) : !MembershipPolicy.CanRemove(actor.Role, target.Role, actorId == targetUserId))
            return new(MembershipStatus.Forbidden);
        if (MembershipPolicy.WouldLoseFinalOwner(target.Role, role, members.Count(m => m.Role == WorkspaceRole.OWNER)))
            return new(MembershipStatus.Conflict);
        var query = db.WorkspaceMembers.Where(m => m.WorkspaceId == workspaceId && m.UserId == targetUserId);
        if (role is { } replacement) await query.ExecuteUpdateAsync(s => s.SetProperty(m => m.Role, replacement), ct);
        else
        {
            var assigned = await db.Conversations.FromSqlInterpolated($"""
                SELECT * FROM "Conversations" WHERE "WorkspaceId" = {workspaceId}
                    AND "AssignedUserId" = {targetUserId} ORDER BY "Id" FOR UPDATE
                """).ToListAsync(ct);
            var histories = new List<ConversationAssignment>();
            try
            {
                foreach (var conversation in assigned)
                {
                    await db.Entry(conversation).ReloadAsync(ct);
                    var history = conversation.Assign(null, actorId, AssignmentReason.MEMBER_REMOVED)!;
                    histories.Add(history);
                    db.ConversationAssignments.Add(history);
                }
                // Persist the current-assignee FK clear and durable history before deleting the membership.
                await db.SaveChangesAsync(ct);
                await query.ExecuteDeleteAsync(ct);
                await transaction.CommitAsync(ct);
            }
            catch
            {
                foreach (var conversation in assigned) db.Entry(conversation).State = EntityState.Detached;
                foreach (var history in histories) db.Entry(history).State = EntityState.Detached;
                throw;
            }
            return new(MembershipStatus.Success);
        }
        await transaction.CommitAsync(ct);
        return new(MembershipStatus.Success);
    }

    public async Task<MembershipResult> ListInvitationsAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken)
    {
        // Serialize this administrative read with writers so permission cannot change between queries.
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var members = await LockAsync(workspaceId, cancellationToken);
        var actor = members.SingleOrDefault(m => m.UserId == actorId);
        if (actor is null) return new(MembershipStatus.NotFound);
        if (!MembershipPolicy.CanInvite(actor.Role, WorkspaceRole.AGENT)) return new(MembershipStatus.Forbidden);
        var invitations = await db.WorkspaceInvitations.AsNoTracking().Where(i => i.WorkspaceId == workspaceId &&
            (actor.Role == WorkspaceRole.OWNER || i.Role == WorkspaceRole.AGENT))
            .OrderBy(i => i.Id).Skip(skip).Take(take).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MembershipStatus.Success, Invitations: invitations.Select(View).ToArray());
    }

    public async Task<MembershipResult> CreateInvitationAsync(Guid actorId, Guid workspaceId, Guid recipientUserId, WorkspaceRole role, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var members = await LockAsync(workspaceId, cancellationToken);
        var actor = members.SingleOrDefault(m => m.UserId == actorId);
        if (actor is null) return new(MembershipStatus.NotFound);
        if (!Enum.IsDefined(role) || recipientUserId == Guid.Empty) return new(MembershipStatus.Invalid);
        if (!MembershipPolicy.CanInvite(actor.Role, role)) return new(MembershipStatus.Forbidden);
        if (!await db.Users.AnyAsync(u => u.Id == recipientUserId, cancellationToken)) return new(MembershipStatus.NotFound);
        var now = DateTimeOffset.UtcNow;
        if (members.Any(m => m.UserId == recipientUserId) || await db.WorkspaceInvitations.AnyAsync(i =>
            i.WorkspaceId == workspaceId && i.RecipientUserId == recipientUserId && i.AcceptedAt == null && i.RevokedAt == null && i.ExpiresAt > now, cancellationToken))
            return new(MembershipStatus.Conflict);
        var invitation = new WorkspaceInvitation(workspaceId, recipientUserId, actorId, role, now);
        db.WorkspaceInvitations.Add(invitation);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MembershipStatus.Success, Invitation: View(invitation));
    }

    public async Task<MembershipResult> RevokeInvitationAsync(Guid actorId, Guid workspaceId, Guid invitationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var members = await LockAsync(workspaceId, cancellationToken);
        var actor = members.SingleOrDefault(m => m.UserId == actorId);
        if (actor is null) return new(MembershipStatus.NotFound);
        var invitation = await InvitationAsync(workspaceId, invitationId, cancellationToken);
        if (invitation is null) return new(MembershipStatus.NotFound);
        if (!MembershipPolicy.CanInvite(actor.Role, invitation.Role)) return new(MembershipStatus.Forbidden);
        var now = DateTimeOffset.UtcNow;
        if (!invitation.IsPending(now)) return new(MembershipStatus.Conflict);
        invitation.Revoke(now);
        await db.WorkspaceInvitations.Where(i => i.WorkspaceId == workspaceId && i.Id == invitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.RevokedAt, now), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MembershipStatus.Success);
    }

    public async Task<MembershipResult> AcceptInvitationAsync(Guid actorId, Guid workspaceId, Guid invitationId, CancellationToken cancellationToken)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        var members = await LockAsync(workspaceId, cancellationToken);
        var invitation = await InvitationAsync(workspaceId, invitationId, cancellationToken);
        if (invitation is null || invitation.RecipientUserId != actorId) return new(MembershipStatus.NotFound);
        var now = DateTimeOffset.UtcNow;
        if (!invitation.IsPending(now)) return new(MembershipStatus.Conflict);
        var inviter = members.SingleOrDefault(m => m.UserId == invitation.InviterUserId);
        if (inviter is null || !MembershipPolicy.CanInvite(inviter.Role, invitation.Role)) return new(MembershipStatus.Forbidden);
        if (members.Any(m => m.UserId == actorId)) return new(MembershipStatus.Conflict);
        invitation.Accept(now);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspaceId, actorId, invitation.Role));
        await db.WorkspaceInvitations.Where(i => i.WorkspaceId == workspaceId && i.Id == invitationId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.AcceptedAt, now), cancellationToken);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(MembershipStatus.Success);
    }

    private async Task<WorkspaceInvitation?> InvitationAsync(Guid workspaceId, Guid invitationId, CancellationToken ct) =>
        (await db.WorkspaceInvitations.FromSqlInterpolated($"SELECT * FROM \"WorkspaceInvitations\" WHERE \"WorkspaceId\" = {workspaceId} AND \"Id\" = {invitationId} FOR UPDATE")
            .AsNoTracking().ToListAsync(ct)).SingleOrDefault();
}
