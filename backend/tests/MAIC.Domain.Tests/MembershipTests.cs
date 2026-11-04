using MAIC.Domain.Workspaces;
using Xunit;
namespace MAIC.Domain.Tests;

public sealed class MembershipTests
{
    [Fact]
    public void Invitation_expiry_and_terminal_states_are_enforced()
    {
        var now = DateTimeOffset.UtcNow;
        var invitation = new WorkspaceInvitation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), WorkspaceRole.AGENT, now);
        Assert.Equal('7', invitation.Id.ToString()[14]);
        Assert.True(invitation.IsPending(now.AddDays(7).AddTicks(-1)));
        Assert.False(invitation.IsPending(now.AddDays(7)));
        Assert.Throws<InvalidOperationException>(() => invitation.Accept(now.AddDays(7)));
        invitation.Revoke(now);
        Assert.Throws<InvalidOperationException>(() => invitation.Accept(now));
        Assert.Throws<InvalidOperationException>(() => invitation.Revoke(now));
        Assert.Throws<ArgumentException>(() => new WorkspaceInvitation(Guid.Empty, Guid.NewGuid(), Guid.NewGuid(), WorkspaceRole.AGENT, now));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceInvitation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), (WorkspaceRole)99, now));
    }
    [Fact]
    public void Policy_protects_privileged_roles_and_final_owner()
    {
        foreach (var role in Enum.GetValues<WorkspaceRole>()) Assert.True(MembershipPolicy.CanInvite(WorkspaceRole.OWNER, role));
        Assert.False(MembershipPolicy.CanInvite(WorkspaceRole.ADMIN, WorkspaceRole.ADMIN));
        Assert.False(MembershipPolicy.CanInvite(WorkspaceRole.ADMIN, WorkspaceRole.OWNER));
        Assert.True(MembershipPolicy.CanInvite(WorkspaceRole.ADMIN, WorkspaceRole.AGENT));
        Assert.False(MembershipPolicy.CanChangeRole(WorkspaceRole.ADMIN));
        Assert.True(MembershipPolicy.CanRemove(WorkspaceRole.AGENT, WorkspaceRole.AGENT, true));
        Assert.True(MembershipPolicy.WouldLoseFinalOwner(WorkspaceRole.OWNER, null, 1));
        Assert.True(MembershipPolicy.WouldLoseFinalOwner(WorkspaceRole.OWNER, WorkspaceRole.ADMIN, 1));
        Assert.False(MembershipPolicy.WouldLoseFinalOwner(WorkspaceRole.OWNER, WorkspaceRole.OWNER, 1));
        Assert.False(MembershipPolicy.WouldLoseFinalOwner(WorkspaceRole.OWNER, null, 2));
    }
}
