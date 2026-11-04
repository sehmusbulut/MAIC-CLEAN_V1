using MAIC.Application.Channels;
using MAIC.Domain.Channels;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ChannelPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schema_enforces_global_exact_token_uniqueness_type_name_and_workspace()
    {
        var (workspace, actor) = await SeedAsync();
        var (other, otherActor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ChannelStore(db);
        var first = new Channel(workspace.Id, "First");
        db.Entry(first).Property(c => c.PublishableToken).CurrentValue = new string('A', 43);
        Assert.Equal(ChannelStatus.Success, (await store.CreateAsync(actor.Id, first, Cancellation)).Status);
        var second = new Channel(other.Id, "Second");
        db.Entry(second).Property(c => c.PublishableToken).CurrentValue = new string('a', 43);
        Assert.Equal(ChannelStatus.Success, (await store.CreateAsync(otherActor.Id, second, Cancellation)).Status);
        Assert.Equal("First", (await store.LookupPublicAsync(first.PublishableToken, Cancellation)).Public!.Name);
        Assert.Equal("Second", (await store.LookupPublicAsync(second.PublishableToken, Cancellation)).Public!.Name);

        var duplicate = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Channels\" SET \"PublishableToken\" = {first.PublishableToken} WHERE \"Id\" = {second.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, duplicate.SqlState);
        Assert.Equal("IX_Channels_PublishableToken", duplicate.ConstraintName);
        var orphan = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Channels\" SET \"WorkspaceId\" = {Guid.NewGuid()} WHERE \"Id\" = {first.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, orphan.SqlState);
        var type = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Channels\" SET \"Type\" = 'SMS' WHERE \"Id\" = {first.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, type.SqlState);
        foreach (var name in new[] { " ", "bad\nname" })
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Channels\" SET \"Name\" = {name} WHERE \"Id\" = {first.Id}", Cancellation));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        }
        foreach (var token in new[] { new string('a', 42), new string('a', 42) + "!" })
        {
            var invalid = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Channels\" SET \"PublishableToken\" = {token} WHERE \"Id\" = {first.Id}", Cancellation));
            Assert.Equal(PostgresErrorCodes.CheckViolation, invalid.SqlState);
        }
        var longName = new string('n', 201);
        var length = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Channels\" SET \"Name\" = {longName} WHERE \"Id\" = {first.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, length.SqlState);
        var required = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Channels\" SET \"PublishableToken\" = NULL WHERE \"Id\" = {first.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, required.SqlState);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_WebChannels"));
    }

    [Fact]
    public async Task Token_conflict_rolls_back_and_does_not_poison_reused_context()
    {
        var (workspace, actor) = await SeedAsync();
        var (other, otherActor) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ChannelStore(db);
        var first = new Channel(workspace.Id, "First");
        Assert.Equal(ChannelStatus.Success, (await store.CreateAsync(actor.Id, first, Cancellation)).Status);
        var collision = new Channel(other.Id, "Collision");
        db.Entry(collision).Property(c => c.PublishableToken).CurrentValue = first.PublishableToken;
        var result = await store.CreateAsync(otherActor.Id, collision, Cancellation);
        Assert.Equal(ChannelStatus.Conflict, result.Status);
        Assert.Null(result.Channel);
        Assert.Equal(EntityState.Detached, db.Entry(collision).State);
        Assert.False(await db.Channels.AnyAsync(c => c.Id == collision.Id, Cancellation));
        var retry = new Channel(other.Id, "Retry");
        Assert.Equal(ChannelStatus.Success, (await store.CreateAsync(otherActor.Id, retry, Cancellation)).Status);
        Assert.Single(await db.Channels.Where(c => c.WorkspaceId == other.Id).ToListAsync(Cancellation));
        Assert.Equal("First", (await store.LookupPublicAsync(first.PublishableToken, Cancellation)).Public!.Name);
    }

    [Fact]
    public async Task Listing_bounds_rows_in_one_authorized_SQL_snapshot()
    {
        var (workspace, actor) = await SeedAsync();
        var (other, otherActor) = await SeedAsync();
        var channels = Enumerable.Range(0, 3).Select(i => new Channel(workspace.Id, "Channel " + i)).OrderBy(c => c.Id).ToArray();
        await using (var setup = fixture.CreateContext())
        {
            setup.Channels.AddRange(channels);
            setup.Channels.Add(new Channel(other.Id, "Hidden"));
            await setup.SaveChangesAsync(Cancellation);
        }
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);
        var store = new ChannelStore(db);
        var page = await store.ListAsync(actor.Id, workspace.Id, 1, 1, Cancellation);
        Assert.Equal(ChannelStatus.Success, page.Status);
        Assert.Equal(channels[1].Id, Assert.Single(page.Items!).Id);
        var sql = Assert.Single(commands);
        Assert.True(sql.Contains("LIMIT") && sql.Contains("OFFSET")
            || sql.Contains("ROW_NUMBER() OVER") && sql.Contains(".row <="), "Pagination must be bounded within SQL.");
        commands.Clear();
        var empty = await store.ListAsync(actor.Id, workspace.Id, 100, 1, Cancellation);
        Assert.Equal(ChannelStatus.Success, empty.Status);
        Assert.Empty(empty.Items!);
        Assert.Single(commands);
        commands.Clear();
        Assert.Equal(ChannelStatus.NotFound, (await store.ListAsync(otherActor.Id, workspace.Id, 100, 1, Cancellation)).Status);
        Assert.Single(commands);
        commands.Clear();
        Assert.Equal(ChannelStatus.NotFound, (await store.GetAsync(otherActor.Id, workspace.Id, channels[0].Id, Cancellation)).Status);
        Assert.Single(commands);
    }

    private async Task<(Workspace Workspace, User Actor)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var actor = new User();
        var workspace = new Workspace("Channel persistence");
        db.Users.Add(actor);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, actor);
    }
}
