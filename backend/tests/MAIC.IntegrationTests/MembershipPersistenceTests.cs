using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class MembershipPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [Fact]
    public async Task Member_page_is_bounded_in_SQL_with_one_authorized_snapshot()
    {
        var ct = TestContext.Current.CancellationToken;
        var owner = new User();
        var workspace = new Workspace("Bounded read");
        await using (var setup = fixture.CreateContext())
        {
            setup.Users.Add(owner);
            setup.Workspaces.Add(workspace);
            setup.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER));
            await setup.SaveChangesAsync(ct);
        }
        var commands = new List<string>();
        var options = new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options;
        await using var db = new MaicDbContext(options);
        var result = await new MembershipStore(db).ListMembersAsync(owner.Id, workspace.Id, 1, 1, ct);
        Assert.Equal(MembershipStatus.Success, result.Status);
        Assert.Empty(result.Members!);
        var sql = Assert.Single(commands);
        // EF translates correlated collection pagination to a bounded row-number window.
        Assert.Contains("ROW_NUMBER() OVER", sql);
        Assert.Contains("@__skip_", sql);
        Assert.Contains(".row <=", sql);
        Assert.Contains("+ @__take_", sql);
        commands.Clear();
        var denied = await new MembershipStore(db).ListMembersAsync(Guid.NewGuid(), workspace.Id, 1, 1, ct);
        Assert.Equal(MembershipStatus.NotFound, denied.Status);
        Assert.Single(commands);
    }
}

