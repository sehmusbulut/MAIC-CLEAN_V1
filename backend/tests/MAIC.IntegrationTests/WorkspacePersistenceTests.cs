using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class WorkspacePersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Creation_is_atomic_and_database_enforces_membership_constraints()
    {
        await using var db = fixture.CreateContext();
        var store = new WorkspaceStore(db);
        var failed = new Workspace("Failed owner");
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => store.CreateAsync(failed,
            new WorkspaceMember(failed.Id, Guid.CreateVersion7(), WorkspaceRole.OWNER), Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        db.ChangeTracker.Clear();
        Assert.False(await db.Workspaces.AnyAsync(w => w.Id == failed.Id, Cancellation));

        var user = new User();
        db.Users.Add(user);
        await db.SaveChangesAsync(Cancellation);
        var workspace = new Workspace("Support");
        await store.CreateAsync(workspace, new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.OWNER), Cancellation);
        var member = await db.WorkspaceMembers.SingleAsync(m => m.WorkspaceId == workspace.Id, Cancellation);
        Assert.Equal(user.Id, member.UserId);
        Assert.Equal(WorkspaceRole.OWNER, member.Role);
        Assert.Equal('7', member.Id.ToString()[14]);

        // Let PostgreSQL enforce uniqueness instead of EF's tracked alternate-key identity map.
        db.ChangeTracker.Clear();
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.ADMIN));
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(duplicate.InnerException).SqlState);
        db.ChangeTracker.Clear();
        db.WorkspaceMembers.Add(new WorkspaceMember(Guid.CreateVersion7(), user.Id, WorkspaceRole.AGENT));
        var orphan = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync(Cancellation));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(orphan.InnerException).SqlState);
        db.ChangeTracker.Clear();
        var role = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"WorkspaceMembers\" SET \"Role\" = 'ROOT' WHERE \"Id\" = {member.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, role.SqlState);
        var name = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Workspaces\" SET \"Name\" = '   ' WHERE \"Id\" = {workspace.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.CheckViolation, name.SqlState);
        var nullName = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Workspaces\" SET \"Name\" = NULL WHERE \"Id\" = {workspace.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.NotNullViolation, nullName.SqlState);
        var longName = new string('x', 201);
        var length = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE \"Workspaces\" SET \"Name\" = {longName} WHERE \"Id\" = {workspace.Id}", Cancellation));
        Assert.Equal(PostgresErrorCodes.StringDataRightTruncation, length.SqlState);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_WorkspaceFoundation"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_waits_for_concurrent_membership_change_and_observes_committed_authorization(bool remove)
    {
        await using var changer = fixture.CreateContext();
        var user = new User();
        changer.Users.Add(user);
        await changer.SaveChangesAsync(Cancellation);
        var workspace = new Workspace("Before");
        await new WorkspaceStore(changer).CreateAsync(workspace,
            new WorkspaceMember(workspace.Id, user.Id, WorkspaceRole.ADMIN), Cancellation);
        await using var transaction = await changer.Database.BeginTransactionAsync(Cancellation);
        if (remove)
            await changer.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id).ExecuteDeleteAsync(Cancellation);
        else
            await changer.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id)
                .ExecuteUpdateAsync(setters => setters.SetProperty(m => m.Role, WorkspaceRole.AGENT), Cancellation);

        var applicationName = "workspace-lock-" + Guid.NewGuid();
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName };
        await using var writer = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection.ConnectionString).Options);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var rename = new WorkspaceStore(writer).RenameAsync(user.Id, workspace.Id, "Unauthorized", [WorkspaceRole.OWNER, WorkspaceRole.ADMIN], timeout.Token);
        try
        {
            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(timeout.Token);
            // Observe an actual PostgreSQL lock wait, rather than infer contention from a delay.
            await using var command = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @app AND wait_event_type = 'Lock')", observer);
            command.Parameters.AddWithValue("app", applicationName);
            while (!(bool)(await command.ExecuteScalarAsync(timeout.Token))!)
            {
                Assert.False(rename.IsCompleted, "Rename must wait for the authorizing membership row.");
                await Task.Delay(20, timeout.Token);
            }
            await transaction.CommitAsync(timeout.Token);
            var result = await rename;
            Assert.Equal(remove ? WorkspaceStatus.NotFound : WorkspaceStatus.Forbidden, result.Status);
            await using var verify = fixture.CreateContext();
            Assert.Equal("Before", (await verify.Workspaces.SingleAsync(w => w.Id == workspace.Id, Cancellation)).Name);
        }
        finally
        {
            await timeout.CancelAsync();
            try { await rename; } catch (OperationCanceledException) { }
        }
    }
}
