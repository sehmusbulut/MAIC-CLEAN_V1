using MAIC.Application.Channels;
using MAIC.Application.Workspaces;
using MAIC.Domain.Channels;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ChannelConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_queued_behind_removal_rechecks_membership(bool update)
    {
        var (workspace, owner, agent) = await SeedAsync();
        var channel = new Channel(workspace.Id, "Before");
        if (update) await SeedChannelAsync(channel);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
        var removerName = "channel-remove-" + Guid.NewGuid();
        var writerName = "channel-write-" + Guid.NewGuid();
        await using var remover = NamedContext(removerName);
        await using var writer = NamedContext(writerName);
        var removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
        Task<ChannelResult>? mutation = null;
        try
        {
            await ObserveLockAsync(removerName, removal, "WorkspaceMembers", timeout.Token);
            mutation = update
                ? new ChannelStore(writer).UpdateAsync(agent.Id, workspace.Id, channel.Id, "Unauthorized", false, timeout.Token)
                : new ChannelStore(writer).CreateAsync(agent.Id, channel, timeout.Token);
            await ObserveLockAsync(writerName, mutation, "Workspaces", timeout.Token, removerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            Assert.Equal(ChannelStatus.NotFound, (await mutation).Status);
            await using var verify = fixture.CreateContext();
            var persisted = await verify.Channels.AsNoTracking().Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Cancellation);
            if (update)
            {
                var original = Assert.Single(persisted);
                Assert.Equal("Before", original.Name);
                Assert.True(original.Enabled);
            }
            else Assert.Empty(persisted);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(removal);
            if (mutation is not null) await DrainAsync(mutation);
        }
    }

    [Fact]
    public async Task Update_holds_workspace_and_actor_locks_until_its_changes_commit()
    {
        var (workspace, owner, agent) = await SeedAsync();
        var channel = new Channel(workspace.Id, "Before");
        await SeedChannelAsync(channel);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Channels\" WHERE \"Id\" = {channel.Id} FOR UPDATE", timeout.Token);
        var writerName = "channel-write-" + Guid.NewGuid();
        var probeName = "channel-member-probe-" + Guid.NewGuid();
        var removerName = "channel-remove-" + Guid.NewGuid();
        await using var writer = NamedContext(writerName);
        await using var memberProbe = NamedContext(probeName);
        await using var remover = NamedContext(removerName);
        await using var probeTransaction = await memberProbe.Database.BeginTransactionAsync(timeout.Token);
        var mutation = new ChannelStore(writer).UpdateAsync(agent.Id, workspace.Id, channel.Id, "Committed", false, timeout.Token);
        Task? probe = null;
        Task<MembershipResult>? removal = null;
        try
        {
            await ObserveLockAsync(writerName, mutation, "Channels", timeout.Token);
            probe = memberProbe.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
            await ObserveLockAsync(probeName, probe, "WorkspaceMembers", timeout.Token, writerName);
            removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
            await ObserveLockAsync(removerName, removal, "Workspaces", timeout.Token, writerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(ChannelStatus.Success, (await mutation).Status);
            await probe;
            await probeTransaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            await using var verify = fixture.CreateContext();
            var persisted = await verify.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id, Cancellation);
            Assert.Equal("Committed", persisted.Name);
            Assert.False(persisted.Enabled);
            Assert.Equal(channel.PublishableToken, persisted.PublishableToken);
            Assert.Equal(ChannelStatus.NotFound, (await new ChannelStore(verify).LookupPublicAsync(channel.PublishableToken, Cancellation)).Status);
            Assert.False(await verify.WorkspaceMembers.AnyAsync(m => m.WorkspaceId == workspace.Id && m.UserId == agent.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(mutation);
            if (probe is not null) await DrainAsync(probe);
            if (removal is not null) await DrainAsync(removal);
        }
    }

    [Fact]
    public async Task Concurrent_global_token_collision_commits_only_one_channel()
    {
        var (workspace, owner, _) = await SeedAsync();
        var (otherWorkspace, otherOwner, _) = await SeedAsync();
        var first = new Channel(workspace.Id, "First");
        var second = new Channel(otherWorkspace.Id, "Second");
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" IN ({workspace.Id}, {otherWorkspace.Id}) ORDER BY \"Id\" FOR UPDATE", timeout.Token);
        var firstName = "channel-first-" + Guid.NewGuid();
        var secondName = "channel-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        secondDb.Entry(second).Property(c => c.PublishableToken).CurrentValue = first.PublishableToken;
        var firstWrite = new ChannelStore(firstDb).CreateAsync(owner.Id, first, timeout.Token);
        var secondWrite = new ChannelStore(secondDb).CreateAsync(otherOwner.Id, second, timeout.Token);
        try
        {
            await ObserveLockAsync(firstName, firstWrite, "Workspaces", timeout.Token);
            await ObserveLockAsync(secondName, secondWrite, "Workspaces", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            var results = await Task.WhenAll(firstWrite, secondWrite);
            Assert.Single(results, r => r.Status == ChannelStatus.Success);
            Assert.Single(results, r => r.Status == ChannelStatus.Conflict);
            await using var verify = fixture.CreateContext();
            var winner = Assert.Single(await verify.Channels.AsNoTracking()
                .Where(c => c.PublishableToken == first.PublishableToken).ToListAsync(Cancellation));
            Assert.Equal(results.Single(r => r.Status == ChannelStatus.Success).Channel!.Id, winner.Id);
            Assert.Equal(1, await verify.Channels.CountAsync(c => c.WorkspaceId == workspace.Id || c.WorkspaceId == otherWorkspace.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(firstWrite);
            await DrainAsync(secondWrite);
        }
    }
    private async Task ObserveLockAsync(string applicationName, Task operation, string relation,
        CancellationToken ct, string? blockingApplication = null)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity activity
                WHERE application_name = @app AND wait_event_type = 'Lock'
                AND position(@relation in activity.query) > 0
                AND (@blocker = '' OR EXISTS (SELECT 1 FROM pg_stat_activity blocking
                    WHERE blocking.application_name = @blocker AND blocking.pid = ANY(pg_blocking_pids(activity.pid)))))
            """, observer);
        command.Parameters.AddWithValue("app", applicationName);
        command.Parameters.AddWithValue("relation", $"\"{relation}\"");
        command.Parameters.AddWithValue("blocker", blockingApplication ?? "");
        while (!(bool)(await command.ExecuteScalarAsync(ct))!)
        {
            if (operation.IsCompleted) await operation;
            Assert.False(operation.IsCompleted, $"Operation must enter the expected PostgreSQL lock wait on {relation}.");
            await Task.Delay(20, ct);
        }
    }

    private MaicDbContext NamedContext(string applicationName) => new(new DbContextOptionsBuilder<MaicDbContext>()
        .UseNpgsql(new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        { ApplicationName = applicationName, Pooling = false }.ConnectionString).Options);

    private static CancellationTokenSource Timeout()
    {
        var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        return timeout;
    }

    private static async Task DrainAsync(Task operation)
    {
        try { await operation; }
        catch (Exception) { /* The primary test assertion reports the failure; cleanup observes all tasks. */ }
    }

    private async Task<(Workspace Workspace, User Owner, User Agent)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var owner = new User();
        var agent = new User();
        var workspace = new Workspace("Channel races");
        db.Users.AddRange(owner, agent);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, owner, agent);
    }

    private async Task SeedChannelAsync(Channel channel)
    {
        await using var db = fixture.CreateContext();
        db.Channels.Add(channel);
        await db.SaveChangesAsync(Cancellation);
    }
}
