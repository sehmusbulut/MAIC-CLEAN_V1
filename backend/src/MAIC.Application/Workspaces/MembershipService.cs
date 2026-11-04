using MAIC.Application.Identity;
using MAIC.Domain.Workspaces;

namespace MAIC.Application.Workspaces;

public enum MembershipStatus { Success, Unauthenticated, Invalid, NotFound, Forbidden, Conflict }
public sealed record MemberView(Guid UserId, WorkspaceRole Role);
public sealed record InvitationView(Guid Id, Guid WorkspaceId, Guid RecipientUserId, Guid InviterUserId,
    WorkspaceRole Role, DateTimeOffset CreatedAt, DateTimeOffset ExpiresAt, DateTimeOffset? AcceptedAt, DateTimeOffset? RevokedAt);
public sealed record MembershipResult(MembershipStatus Status, IReadOnlyList<MemberView>? Members = null,
    IReadOnlyList<InvitationView>? Invitations = null, InvitationView? Invitation = null);

// Mutations must serialize on the workspace before reading/locking persisted memberships and invitations.
// Authorization and final-owner checks remain valid until the atomic write commits.
public interface IMembershipStore
{
    Task<MembershipResult> ListMembersAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken);
    Task<MembershipResult> ChangeRoleAsync(Guid actorId, Guid workspaceId, Guid targetUserId, WorkspaceRole role, CancellationToken cancellationToken);
    Task<MembershipResult> RemoveAsync(Guid actorId, Guid workspaceId, Guid targetUserId, CancellationToken cancellationToken);
    Task<MembershipResult> ListInvitationsAsync(Guid actorId, Guid workspaceId, int skip, int take, CancellationToken cancellationToken);
    Task<MembershipResult> CreateInvitationAsync(Guid actorId, Guid workspaceId, Guid recipientUserId, WorkspaceRole role, CancellationToken cancellationToken);
    Task<MembershipResult> RevokeInvitationAsync(Guid actorId, Guid workspaceId, Guid invitationId, CancellationToken cancellationToken);
    Task<MembershipResult> AcceptInvitationAsync(Guid actorId, Guid workspaceId, Guid invitationId, CancellationToken cancellationToken);
}

public sealed class MembershipService(ICurrentUser currentUser, IMembershipStore store)
{
    private Task<MembershipResult> Run(Func<Guid, Task<MembershipResult>> action) => currentUser.UserId is { } actor
        ? action(actor) : Task.FromResult(new MembershipResult(MembershipStatus.Unauthenticated));
    private static Task<MembershipResult> Invalid() => Task.FromResult(new MembershipResult(MembershipStatus.Invalid));
    private static bool TryRole(string? value, out WorkspaceRole role) => Enum.TryParse(value, out role) && Enum.IsDefined(role) && value == role.ToString();
    public Task<MembershipResult> ListMembersAsync(Guid workspaceId, int skip, int take, CancellationToken ct) =>
        Run(actor => skip < 0 || take is < 1 or > 100 ? Invalid() : store.ListMembersAsync(actor, workspaceId, skip, take, ct));
    public Task<MembershipResult> ChangeRoleAsync(Guid workspaceId, Guid targetUserId, string? role, CancellationToken ct) =>
        Run(actor => TryRole(role, out var parsed) ? store.ChangeRoleAsync(actor, workspaceId, targetUserId, parsed, ct) : Invalid());
    public Task<MembershipResult> RemoveAsync(Guid workspaceId, Guid targetUserId, CancellationToken ct) =>
        Run(actor => store.RemoveAsync(actor, workspaceId, targetUserId, ct));
    public Task<MembershipResult> LeaveAsync(Guid workspaceId, CancellationToken ct) =>
        Run(actor => store.RemoveAsync(actor, workspaceId, actor, ct));
    public Task<MembershipResult> ListInvitationsAsync(Guid workspaceId, int skip, int take, CancellationToken ct) =>
        Run(actor => skip < 0 || take is < 1 or > 100 ? Invalid() : store.ListInvitationsAsync(actor, workspaceId, skip, take, ct));
    public Task<MembershipResult> CreateInvitationAsync(Guid workspaceId, Guid recipientUserId, string? role, CancellationToken ct) =>
        Run(actor => recipientUserId != Guid.Empty && TryRole(role, out var parsed)
            ? store.CreateInvitationAsync(actor, workspaceId, recipientUserId, parsed, ct) : Invalid());
    public Task<MembershipResult> RevokeInvitationAsync(Guid workspaceId, Guid invitationId, CancellationToken ct) =>
        Run(actor => store.RevokeInvitationAsync(actor, workspaceId, invitationId, ct));
    public Task<MembershipResult> AcceptInvitationAsync(Guid workspaceId, Guid invitationId, CancellationToken ct) =>
        Run(actor => store.AcceptInvitationAsync(actor, workspaceId, invitationId, ct));
}
