using MAIC.Domain.Workspaces;
using Xunit;

namespace MAIC.Domain.Tests;

public sealed class WorkspaceTests
{
    [Fact]
    public void Workspace_and_membership_use_UUIDv7_and_enforce_invariants()
    {
        var workspace = new Workspace("  Support  ");
        var member = new WorkspaceMember(workspace.Id, Guid.CreateVersion7(), WorkspaceRole.OWNER);
        Assert.Equal("Support", workspace.Name);
        Assert.Equal('7', workspace.Id.ToString()[14]);
        Assert.Equal('7', member.Id.ToString()[14]);
        Assert.Throws<ArgumentException>(() => workspace.Rename("  "));
        Assert.Throws<ArgumentException>(() => workspace.Rename(new string('x', 201)));
        Assert.Throws<ArgumentException>(() => workspace.Rename("line\nbreak"));
        Assert.Throws<ArgumentException>(() => new WorkspaceMember(Guid.Empty, member.UserId, WorkspaceRole.AGENT));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WorkspaceMember(workspace.Id, member.UserId, (WorkspaceRole)99));
        workspace.Rename(new string('x', 200));
        Assert.Equal(200, workspace.Name.Length);
    }
}
