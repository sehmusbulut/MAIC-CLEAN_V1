using MAIC.Application.Contacts;
using MAIC.Application.Workspaces;
using MAIC.Domain.Contacts;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ContactConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Concurrent_duplicate_identity_creates_commit_exactly_one_contact()
    {
        var (workspace, owner, agent) = await SeedAsync();
        var firstContact = new Contact(workspace.Id, "First", [new("email", "duplicate@example.com")]);
        var secondContact = new Contact(workspace.Id, "Second", [new("email", "duplicate@example.com")]);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id} FOR UPDATE", timeout.Token);
        var firstName = "contacts-first-" + Guid.NewGuid();
        var secondName = "contacts-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var first = new ContactStore(firstDb).CreateAsync(owner.Id, firstContact, timeout.Token);
        Task<ContactResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Workspaces", timeout.Token);
            second = new ContactStore(secondDb).CreateAsync(agent.Id, secondContact, timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(ContactStatus.Success, (await first).Status);
            Assert.Equal(ContactStatus.Conflict, (await second).Status);
            await using var verify = fixture.CreateContext();
            var persisted = Assert.Single(await verify.Contacts.AsNoTracking()
                .Where(contact => contact.WorkspaceId == workspace.Id).ToListAsync(Cancellation));
            Assert.Equal(firstContact.Id, persisted.Id);
            Assert.Equal("First", persisted.DisplayName);
            var identity = Assert.Single(await verify.ContactIdentities.AsNoTracking()
                .Where(value => value.WorkspaceId == workspace.Id).ToListAsync(Cancellation));
            Assert.Equal(firstContact.Id, identity.ContactId);
            Assert.False(await verify.Contacts.AnyAsync(contact => contact.Id == secondContact.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Contact_mutation_queued_behind_removal_rechecks_membership(bool update)
    {
        var (workspace, owner, agent) = await SeedAsync();
        var contact = new Contact(workspace.Id, "Before", [new("email", "before@example.com")]);
        if (update) await SeedContactAsync(contact);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
        var removerName = "contacts-remover-" + Guid.NewGuid();
        var writerName = "contacts-writer-" + Guid.NewGuid();
        await using var remover = NamedContext(removerName);
        await using var writer = NamedContext(writerName);
        var removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
        Task<ContactResult>? mutation = null;
        try
        {
            await ObserveLockAsync(removerName, removal, "WorkspaceMembers", timeout.Token);
            mutation = update
                ? new ContactStore(writer).UpdateAsync(agent.Id, workspace.Id, contact.Id, "Unauthorized",
                    [new("email", "unauthorized@example.com")], timeout.Token)
                : new ContactStore(writer).CreateAsync(agent.Id, contact, timeout.Token);
            // Removal already owns the workspace while it waits for the held member row.
            await ObserveLockAsync(writerName, mutation, "Workspaces", timeout.Token, removerName);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            Assert.Equal(ContactStatus.NotFound, (await mutation).Status);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.WorkspaceMembers.AnyAsync(member =>
                member.WorkspaceId == workspace.Id && member.UserId == agent.Id, Cancellation));
            var persisted = await verify.Contacts.AsNoTracking()
                .Where(value => value.WorkspaceId == workspace.Id).ToListAsync(Cancellation);
            var identities = await verify.ContactIdentities.AsNoTracking()
                .Where(value => value.WorkspaceId == workspace.Id).ToListAsync(Cancellation);
            if (update)
            {
                Assert.Equal(contact.Id, Assert.Single(persisted).Id);
                Assert.Equal("Before", persisted[0].DisplayName);
                Assert.Equal(contact.Id, Assert.Single(identities).ContactId);
                Assert.Equal("before@example.com", identities[0].Value);
            }
            else
            {
                Assert.Empty(persisted);
                Assert.Empty(identities);
            }
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(removal);
            if (mutation is not null) await DrainAsync(mutation);
        }
    }

    [Fact]
    public async Task Contact_update_retains_workspace_and_actor_locks_through_commit()
    {
        var (workspace, owner, agent) = await SeedAsync();
        var contact = new Contact(workspace.Id, "Before", [new("email", "before@example.com")]);
        await SeedContactAsync(contact);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Contacts\" WHERE \"Id\" = {contact.Id} FOR UPDATE", timeout.Token);
        var writerName = "contacts-writer-" + Guid.NewGuid();
        var probeName = "contacts-member-probe-" + Guid.NewGuid();
        var removerName = "contacts-remover-" + Guid.NewGuid();
        await using var writer = NamedContext(writerName);
        await using var memberProbe = NamedContext(probeName);
        await using var remover = NamedContext(removerName);
        await using var probeTransaction = await memberProbe.Database.BeginTransactionAsync(timeout.Token);
        var mutation = new ContactStore(writer).UpdateAsync(agent.Id, workspace.Id, contact.Id, "Committed",
            [new("email", "committed@example.com")], timeout.Token);
        Task? probe = null;
        Task<MembershipResult>? removal = null;
        try
        {
            await ObserveLockAsync(writerName, mutation, "Contacts", timeout.Token);
            // A direct row writer must also wait: workspace serialization alone is insufficient
            // to retain authorization against legacy writers that only lock the membership.
            probe = memberProbe.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
            await ObserveLockAsync(probeName, probe, "WorkspaceMembers", timeout.Token, writerName);
            removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
            await ObserveLockAsync(removerName, removal, "Workspaces", timeout.Token, writerName);
            await transaction.CommitAsync(timeout.Token);

            Assert.Equal(ContactStatus.Success, (await mutation).Status);
            await probe;
            await probeTransaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.WorkspaceMembers.AnyAsync(member =>
                member.WorkspaceId == workspace.Id && member.UserId == agent.Id, Cancellation));
            var persisted = Assert.Single(await verify.Contacts.AsNoTracking()
                .Where(value => value.WorkspaceId == workspace.Id).ToListAsync(Cancellation));
            Assert.Equal(contact.Id, persisted.Id);
            Assert.Equal("Committed", persisted.DisplayName);
            var identity = Assert.Single(await verify.ContactIdentities.AsNoTracking()
                .Where(value => value.WorkspaceId == workspace.Id).ToListAsync(Cancellation));
            Assert.Equal(contact.Id, identity.ContactId);
            Assert.Equal("committed@example.com", identity.Value);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(mutation);
            if (probe is not null) await DrainAsync(probe);
            if (removal is not null) await DrainAsync(removal);
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
        var workspace = new Workspace("Contact races");
        db.Users.AddRange(owner, agent);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(
            new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, owner, agent);
    }

    private async Task SeedContactAsync(Contact contact)
    {
        await using var db = fixture.CreateContext();
        db.Contacts.Add(contact);
        await db.SaveChangesAsync(Cancellation);
    }
}
