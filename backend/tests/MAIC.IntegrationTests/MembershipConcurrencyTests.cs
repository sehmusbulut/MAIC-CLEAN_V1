using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class MembershipConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("leave")]
    [InlineData("demote")]
    [InlineData("remove")]
    public async Task Concurrent_owner_mutations_preserve_exactly_one_owner(string operation)
    {
        var (workspace, first, second) = await SeedAsync(WorkspaceRole.OWNER);
        Task<MembershipResult> Mutate(MaicDbContext db, Guid actor, Guid other, CancellationToken ct) =>
            operation == "demote"
                ? new MembershipStore(db).ChangeRoleAsync(actor, workspace.Id, actor, WorkspaceRole.AGENT, ct)
                : new MembershipStore(db).RemoveAsync(actor, workspace.Id, operation == "leave" ? actor : other, ct);

        var results = await RaceAsync(workspace.Id,
            (db, ct) => Mutate(db, first.Id, second.Id, ct),
            (db, ct) => Mutate(db, second.Id, first.Id, ct));

        Assert.Single(results, result => result.Status == MembershipStatus.Success);
        Assert.All(results, result => Assert.Contains(result.Status,
            new[] { MembershipStatus.Success, MembershipStatus.Conflict, MembershipStatus.NotFound, MembershipStatus.Forbidden }));
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.WorkspaceMembers.CountAsync(
            member => member.WorkspaceId == workspace.Id && member.Role == WorkspaceRole.OWNER, Cancellation));
    }

    [Fact]
    public async Task Concurrent_acceptance_creates_one_membership_and_one_terminal_transition()
    {
        var (workspace, owner, recipient) = await SeedAsync(null);
        var invitationId = await InviteAsync(workspace.Id, owner.Id, recipient.Id);
        var results = await RaceAsync(workspace.Id,
            (db, ct) => new MembershipStore(db).AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, ct),
            (db, ct) => new MembershipStore(db).AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, ct));

        Assert.Single(results, result => result.Status == MembershipStatus.Success);
        Assert.Single(results, result => result.Status == MembershipStatus.Conflict);
        await using var verify = fixture.CreateContext();
        Assert.Equal(1, await verify.WorkspaceMembers.CountAsync(
            member => member.WorkspaceId == workspace.Id && member.UserId == recipient.Id, Cancellation));
        var invitation = await verify.Set<WorkspaceInvitation>().SingleAsync(value => value.Id == invitationId, Cancellation);
        Assert.NotNull(invitation.AcceptedAt);
        Assert.Null(invitation.RevokedAt);
        var acceptedAt = invitation.AcceptedAt;
        Assert.Equal(MembershipStatus.Conflict, (await new MembershipStore(verify)
            .AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, Cancellation)).Status);
        verify.ChangeTracker.Clear();
        Assert.Equal(acceptedAt, (await verify.Set<WorkspaceInvitation>()
            .SingleAsync(value => value.Id == invitationId, Cancellation)).AcceptedAt);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_and_revocation_have_only_one_terminal_winner(bool revokeFirst)
    {
        var (workspace, owner, recipient) = await SeedAsync(null);
        var invitationId = await InviteAsync(workspace.Id, owner.Id, recipient.Id);
        Task<MembershipResult> Accept(MaicDbContext db, CancellationToken ct) =>
            new MembershipStore(db).AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, ct);
        Task<MembershipResult> Revoke(MaicDbContext db, CancellationToken ct) =>
            new MembershipStore(db).RevokeInvitationAsync(owner.Id, workspace.Id, invitationId, ct);
        var results = await RaceAsync(workspace.Id, revokeFirst ? Revoke : Accept, revokeFirst ? Accept : Revoke);

        Assert.Single(results, result => result.Status == MembershipStatus.Success);
        Assert.Single(results, result => result.Status == MembershipStatus.Conflict);
        await using var verify = fixture.CreateContext();
        var invitation = await verify.Set<WorkspaceInvitation>().SingleAsync(value => value.Id == invitationId, Cancellation);
        Assert.True(invitation.AcceptedAt.HasValue ^ invitation.RevokedAt.HasValue);
        Assert.Equal(revokeFirst, invitation.RevokedAt.HasValue);
        Assert.Equal(revokeFirst ? 0 : 1, await verify.WorkspaceMembers.CountAsync(
            member => member.WorkspaceId == workspace.Id && member.UserId == recipient.Id, Cancellation));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_rechecks_inviter_authority_after_queued_removal_or_demotion(bool remove)
    {
        var (workspace, owner, inviter) = await SeedAsync(WorkspaceRole.ADMIN);
        var recipient = new User();
        await using (var seed = fixture.CreateContext())
        {
            seed.Users.Add(recipient);
            await seed.SaveChangesAsync(Cancellation);
        }
        var invitationId = await InviteAsync(workspace.Id, inviter.Id, recipient.Id);
        var results = await RaceAsync(workspace.Id,
            (db, ct) => remove
                ? new MembershipStore(db).RemoveAsync(owner.Id, workspace.Id, inviter.Id, ct)
                : new MembershipStore(db).ChangeRoleAsync(owner.Id, workspace.Id, inviter.Id, WorkspaceRole.AGENT, ct),
            (db, ct) => new MembershipStore(db).AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, ct));

        Assert.Equal(MembershipStatus.Success, results[0].Status);
        Assert.Equal(MembershipStatus.Forbidden, results[1].Status);
        await using var verify = fixture.CreateContext();
        Assert.False(await verify.WorkspaceMembers.AnyAsync(
            member => member.WorkspaceId == workspace.Id && member.UserId == recipient.Id, Cancellation));
        Assert.Null((await verify.Set<WorkspaceInvitation>()
            .SingleAsync(value => value.Id == invitationId, Cancellation)).AcceptedAt);
    }

    [Fact]
    public async Task Acceptance_checks_expiry_after_waiting_for_the_workspace_lock()
    {
        var (workspace, owner, recipient) = await SeedAsync(null);
        var invitationId = await InviteAsync(workspace.Id, owner.Id, recipient.Id);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id} FOR UPDATE", timeout.Token);
        var acceptName = "membership-expiry-" + Guid.NewGuid();
        await using var accepter = NamedContext(acceptName);
        var accept = new MembershipStore(accepter).AcceptInvitationAsync(recipient.Id, workspace.Id, invitationId, timeout.Token);
        try
        {
            await ObserveLockAsync(acceptName, accept, timeout.Token);
            // Make expiry fall after request start but before lock acquisition, without a sleep barrier.
            var expiresAt = DateTimeOffset.UtcNow;
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"WorkspaceInvitations\" SET \"ExpiresAt\" = {expiresAt} WHERE \"Id\" = {invitationId}", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Conflict, (await accept).Status);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.WorkspaceMembers.AnyAsync(
                member => member.WorkspaceId == workspace.Id && member.UserId == recipient.Id, Cancellation));
            Assert.Null((await verify.Set<WorkspaceInvitation>()
                .SingleAsync(value => value.Id == invitationId, Cancellation)).AcceptedAt);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(accept);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Rename_queues_behind_membership_mutation_and_rechecks_authorization(bool remove)
    {
        var (workspace, owner, target) = await SeedAsync(WorkspaceRole.ADMIN);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {target.Id} FOR UPDATE", timeout.Token);
        var changerName = "membership-change-" + Guid.NewGuid();
        var renameName = "membership-rename-" + Guid.NewGuid();
        await using var changer = NamedContext(changerName);
        await using var renamer = NamedContext(renameName);
        var change = remove
            ? new MembershipStore(changer).RemoveAsync(owner.Id, workspace.Id, target.Id, timeout.Token)
            : new MembershipStore(changer).ChangeRoleAsync(owner.Id, workspace.Id, target.Id, WorkspaceRole.AGENT, timeout.Token);
        Task<WorkspaceResult>? rename = null;
        try
        {
            await ObserveLockAsync(changerName, change, timeout.Token);
            rename = new WorkspaceStore(renamer).RenameAsync(target.Id, workspace.Id, "Unauthorized",
                [WorkspaceRole.OWNER, WorkspaceRole.ADMIN], timeout.Token);
            // The mutator owns the workspace while waiting on the held membership row.
            // A rename blocked directly by it proves both use workspace-first ordering.
            await ObserveLockAsync(renameName, rename, timeout.Token, changerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await change).Status);
            Assert.Equal(remove ? WorkspaceStatus.NotFound : WorkspaceStatus.Forbidden, (await rename).Status);
            await using var verify = fixture.CreateContext();
            Assert.Equal("Before", (await verify.Workspaces.SingleAsync(value => value.Id == workspace.Id, Cancellation)).Name);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(change);
            if (rename is not null) await DrainAsync(rename);
        }
    }

    private async Task<MembershipResult[]> RaceAsync(Guid workspaceId,
        Func<MaicDbContext, CancellationToken, Task<MembershipResult>> first,
        Func<MaicDbContext, CancellationToken, Task<MembershipResult>> second)
    {
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspaceId} FOR UPDATE", timeout.Token);
        var firstName = "membership-first-" + Guid.NewGuid();
        var secondName = "membership-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var firstTask = first(firstDb, timeout.Token);
        Task<MembershipResult>? secondTask = null;
        try
        {
            await ObserveLockAsync(firstName, firstTask, timeout.Token);
            secondTask = second(secondDb, timeout.Token);
            await ObserveLockAsync(secondName, secondTask, timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            return await Task.WhenAll(firstTask, secondTask);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(firstTask);
            if (secondTask is not null) await DrainAsync(secondTask);
        }
    }

    private async Task ObserveLockAsync(string applicationName, Task operation, CancellationToken ct, string? blockingApplication = null)
    {
        await using var observer = new NpgsqlConnection(fixture.ConnectionString);
        await observer.OpenAsync(ct);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (SELECT 1 FROM pg_stat_activity activity
                WHERE application_name = @app AND wait_event_type = 'Lock'
                AND (@blocker = '' OR EXISTS (SELECT 1 FROM pg_stat_activity blocking
                    WHERE blocking.application_name = @blocker AND blocking.pid = ANY(pg_blocking_pids(activity.pid)))))
            """, observer);
        command.Parameters.AddWithValue("app", applicationName);
        command.Parameters.AddWithValue("blocker", blockingApplication ?? "");
        while (!(bool)(await command.ExecuteScalarAsync(ct))!)
        {
            if (operation.IsCompleted) await operation; // Surface database failures before the assertion.
            Assert.False(operation.IsCompleted, "Operation must enter the expected PostgreSQL lock wait.");
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
        catch (Exception) { /* The primary test assertion/await reports the failure; cleanup observes both tasks. */ }
    }

    private async Task<(Workspace Workspace, User Owner, User Other)> SeedAsync(WorkspaceRole? otherRole)
    {
        await using var db = fixture.CreateContext();
        var owner = new User();
        var other = new User();
        var workspace = new Workspace("Before");
        db.Users.AddRange(owner, other);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER));
        if (otherRole.HasValue) db.WorkspaceMembers.Add(new WorkspaceMember(workspace.Id, other.Id, otherRole.Value));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, owner, other);
    }

    private async Task<Guid> InviteAsync(Guid workspaceId, Guid ownerId, Guid recipientId)
    {
        await using var db = fixture.CreateContext();
        var result = await new MembershipStore(db).CreateInvitationAsync(ownerId, workspaceId, recipientId, WorkspaceRole.AGENT, Cancellation);
        Assert.Equal(MembershipStatus.Success, result.Status);
        return Assert.IsType<InvitationView>(result.Invitation).Id;
    }
}
