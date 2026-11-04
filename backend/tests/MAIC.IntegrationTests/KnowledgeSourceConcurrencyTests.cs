using MAIC.Application.Knowledge;
using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Knowledge;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class KnowledgeSourceConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private const string Url = "https://example.com/before";
    private const string NewUrl = "https://example.com/after";
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_queued_behind_membership_removal_rechecks_membership_and_writes_nothing(bool update)
    {
        var (workspace, owner, agent) = await SeedAsync();
        var source = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Before", "Original body", null);
        if (update) await SeedSourceAsync(source);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
        var removerName = "knowledge-remove-" + Guid.NewGuid();
        var writerName = "knowledge-write-" + Guid.NewGuid();
        await using var remover = NamedContext(removerName);
        await using var writer = NamedContext(writerName);
        var removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
        Task<KnowledgeSourceResult>? mutation = null;
        try
        {
            // The removal holds the workspace lock and waits for the member row; the mutation then queues on the workspace.
            await ObserveLockAsync(removerName, removal, "WorkspaceMembers", timeout.Token);
            mutation = update
                ? new KnowledgeSourceStore(writer).UpdateAsync(agent.Id, workspace.Id, source.Id, "Unauthorized", false,
                    "Unauthorized body", null, timeout.Token)
                : new KnowledgeSourceStore(writer).CreateAsync(agent.Id, source, timeout.Token);
            await ObserveLockAsync(writerName, mutation, "Workspaces", timeout.Token, removerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            var result = await mutation;
            Assert.Equal(KnowledgeSourceStatus.NotFound, result.Status);
            Assert.Null(result.Source);
            await using var verify = fixture.CreateContext();
            var persisted = await verify.KnowledgeSources.AsNoTracking().Where(s => s.WorkspaceId == workspace.Id).ToListAsync(Cancellation);
            if (update)
            {
                var original = Assert.Single(persisted);
                Assert.Equal(("Before", true, (string?)"Original body"), (original.Name, original.Enabled, original.TextContent));
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Mutation_holds_workspace_and_actor_locks_until_its_changes_commit(bool update)
    {
        var (workspace, owner, agent) = await SeedAsync();
        var (elsewhere, _, _) = await SeedAsync();
        var source = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Before", null, Url);
        if (update) await SeedSourceAsync(source);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        if (update)
            // The writer's UPDATE waits on this row lock after it has taken the workspace and membership locks.
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"KnowledgeSources\" WHERE \"Id\" = {source.Id} FOR UPDATE", timeout.Token);
        else
            // An uncommitted row with the same primary key in another workspace makes the writer's INSERT wait on this
            // transaction after it has taken the workspace and membership locks.
            await blocker.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO "KnowledgeSources" ("Id", "WorkspaceId", "Kind", "Name", "TextContent", "WebsiteUrl", "Enabled", "CreatedAt")
                VALUES ({source.Id}, {elsewhere.Id}, 'FILE', 'Placeholder', NULL, NULL, TRUE, now())
                """, timeout.Token);
        var writerName = "knowledge-write-" + Guid.NewGuid();
        var probeName = "knowledge-member-probe-" + Guid.NewGuid();
        var removerName = "knowledge-remove-" + Guid.NewGuid();
        await using var writer = NamedContext(writerName);
        await using var memberProbe = NamedContext(probeName);
        await using var remover = NamedContext(removerName);
        await using var probeTransaction = await memberProbe.Database.BeginTransactionAsync(timeout.Token);
        var mutation = update
            ? new KnowledgeSourceStore(writer).UpdateAsync(agent.Id, workspace.Id, source.Id, "Committed", false, null, NewUrl, timeout.Token)
            : new KnowledgeSourceStore(writer).CreateAsync(agent.Id, source, timeout.Token);
        Task? probe = null;
        Task<MembershipResult>? removal = null;
        try
        {
            await ObserveLockAsync(writerName, mutation, "KnowledgeSources", timeout.Token);
            // While the write is pending, the actor's membership row and the workspace row are both held by the writer.
            probe = memberProbe.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE", timeout.Token);
            await ObserveLockAsync(probeName, probe, "WorkspaceMembers", timeout.Token, writerName);
            removal = new MembershipStore(remover).RemoveAsync(owner.Id, workspace.Id, agent.Id, timeout.Token);
            await ObserveLockAsync(removerName, removal, "Workspaces", timeout.Token, writerName);
            Assert.False(mutation.IsCompleted);
            if (update) await transaction.CommitAsync(timeout.Token);
            else await transaction.RollbackAsync(timeout.Token);
            var result = await mutation;
            Assert.Equal(KnowledgeSourceStatus.Success, result.Status);
            await probe;
            await probeTransaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);

            await using var verify = fixture.CreateContext();
            var persisted = await verify.KnowledgeSources.AsNoTracking().SingleAsync(s => s.Id == source.Id, Cancellation);
            (string Name, bool Enabled, string? Url) expected = update ? ("Committed", false, NewUrl) : ("Before", true, Url);
            Assert.Equal(expected, (persisted.Name, persisted.Enabled, persisted.WebsiteUrl));
            Assert.Equal((workspace.Id, KnowledgeSourceKind.WEBSITE, (string?)null), (persisted.WorkspaceId, persisted.Kind, persisted.TextContent));
            Assert.Equal(expected, (result.Source!.Name, result.Source.Enabled, result.Source.WebsiteUrl));
            Assert.False(await verify.KnowledgeSources.AnyAsync(s => s.WorkspaceId == elsewhere.Id, Cancellation));
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
    public async Task Rejected_kind_mismatch_update_releases_its_locks_and_applies_nothing()
    {
        var (workspace, owner, agent) = await SeedAsync();
        var site = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.WEBSITE, "Site", null, Url);
        await SeedSourceAsync(site);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id} FOR UPDATE", timeout.Token);
        var rejectedName = "knowledge-reject-" + Guid.NewGuid();
        var followerName = "knowledge-follow-" + Guid.NewGuid();
        await using var rejectedDb = NamedContext(rejectedName);
        await using var followerDb = NamedContext(followerName);
        // Keep the rejected writer's session open afterwards: a leaked transaction would keep holding its locks.
        await rejectedDb.Database.OpenConnectionAsync(timeout.Token);
        var rejected = new KnowledgeSourceStore(rejectedDb).UpdateAsync(agent.Id, workspace.Id, site.Id, "Renamed", false,
            "Text for a website", null, timeout.Token);
        Task<KnowledgeSourceResult>? follower = null;
        try
        {
            await ObserveLockAsync(rejectedName, rejected, "Workspaces", timeout.Token);
            follower = new KnowledgeSourceStore(followerDb).UpdateAsync(owner.Id, workspace.Id, site.Id, null, null, null, NewUrl, timeout.Token);
            await ObserveLockAsync(followerName, follower, "Workspaces", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            var rejection = await rejected;
            Assert.Equal(KnowledgeSourceStatus.Invalid, rejection.Status);
            Assert.Null(rejection.Source);
            // The queued writer proceeds, so the rejected update released the workspace and membership locks.
            var followed = await follower;
            Assert.Equal(KnowledgeSourceStatus.Success, followed.Status);
            Assert.Equal(("Site", true, (string?)null, (string?)NewUrl),
                (followed.Source!.Name, followed.Source.Enabled, followed.Source.TextContent, followed.Source.WebsiteUrl));

            // The rejected writer's still-open session is idle outside any transaction and holds no locks at all.
            Assert.Null(rejectedDb.Database.CurrentTransaction);
            await using (var observer = new NpgsqlConnection(fixture.ConnectionString))
            {
                await observer.OpenAsync(timeout.Token);
                await using var command = new NpgsqlCommand("""
                    SELECT activity.state, (SELECT count(*) FROM pg_locks held WHERE held.pid = activity.pid)
                    FROM pg_stat_activity activity WHERE activity.application_name = @app
                    """, observer);
                command.Parameters.AddWithValue("app", rejectedName);
                await using var reader = await command.ExecuteReaderAsync(timeout.Token);
                Assert.True(await reader.ReadAsync(timeout.Token), "The rejected writer's session must still be connected.");
                Assert.Equal("idle", reader.GetString(0));
                Assert.Equal(0L, reader.GetInt64(1));
                Assert.False(await reader.ReadAsync(timeout.Token));
            }
            await using (var probe = fixture.CreateContext())
            {
                await using var probeTransaction = await probe.Database.BeginTransactionAsync(timeout.Token);
                await probe.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id} FOR UPDATE NOWAIT", timeout.Token);
                await probe.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {workspace.Id} AND \"UserId\" = {agent.Id} FOR UPDATE NOWAIT", timeout.Token);
                await probe.Database.ExecuteSqlInterpolatedAsync(
                    $"SELECT 1 FROM \"KnowledgeSources\" WHERE \"Id\" = {site.Id} FOR UPDATE NOWAIT", timeout.Token);
                await probeTransaction.RollbackAsync(timeout.Token);
            }
            // No partial rename or enable change from the rejected request; only the follower's URL change landed.
            await AssertStoredAsync(site.Id, "Site", true, null, NewUrl);

            // The rejected context stays usable and works from the latest committed row.
            var retried = await new KnowledgeSourceStore(rejectedDb).UpdateAsync(agent.Id, workspace.Id, site.Id, "Renamed", false,
                null, null, timeout.Token);
            Assert.Equal(KnowledgeSourceStatus.Success, retried.Status);
            Assert.Equal(("Renamed", false, (string?)NewUrl), (retried.Source!.Name, retried.Source.Enabled, retried.Source.WebsiteUrl));
            await AssertStoredAsync(site.Id, "Renamed", false, null, NewUrl);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(rejected);
            if (follower is not null) await DrainAsync(follower);
        }
    }

    [Fact]
    public async Task Concurrent_partial_updates_serialize_so_the_later_one_builds_on_the_committed_row()
    {
        var (workspace, owner, agent) = await SeedAsync();
        var text = new KnowledgeSource(workspace.Id, KnowledgeSourceKind.TEXT, "Before", "Original body", null);
        await SeedSourceAsync(text);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"KnowledgeSources\" WHERE \"Id\" = {text.Id} FOR UPDATE", timeout.Token);
        var firstName = "knowledge-first-" + Guid.NewGuid();
        var secondName = "knowledge-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var first = new KnowledgeSourceStore(firstDb).UpdateAsync(owner.Id, workspace.Id, text.Id, "Renamed first", null, null, null, timeout.Token);
        Task<KnowledgeSourceResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "KnowledgeSources", timeout.Token);
            second = new KnowledgeSourceStore(secondDb).UpdateAsync(agent.Id, workspace.Id, text.Id, null, false, "Second body", null, timeout.Token);
            // The second writer cannot even load the source until the first commits: it queues on the workspace lock.
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);
            var firstResult = await first;
            Assert.Equal(KnowledgeSourceStatus.Success, firstResult.Status);
            Assert.Equal(("Renamed first", true, (string?)"Original body"),
                (firstResult.Source!.Name, firstResult.Source.Enabled, firstResult.Source.TextContent));
            var secondResult = await second;
            Assert.Equal(KnowledgeSourceStatus.Success, secondResult.Status);
            Assert.Equal(("Renamed first", false, (string?)"Second body"),
                (secondResult.Source!.Name, secondResult.Source.Enabled, secondResult.Source.TextContent));
            await AssertStoredAsync(text.Id, "Renamed first", false, "Second body", null);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
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
        var workspace = new Workspace("Knowledge races");
        db.Users.AddRange(owner, agent);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, owner, agent);
    }

    private async Task SeedSourceAsync(KnowledgeSource source)
    {
        await using var db = fixture.CreateContext();
        db.KnowledgeSources.Add(source);
        await db.SaveChangesAsync(Cancellation);
    }

    private async Task AssertStoredAsync(Guid id, string name, bool enabled, string? text, string? url)
    {
        await using var db = fixture.CreateContext();
        var stored = await db.KnowledgeSources.AsNoTracking().SingleAsync(s => s.Id == id, Cancellation);
        Assert.Equal((name, enabled, text, url), (stored.Name, stored.Enabled, stored.TextContent, stored.WebsiteUrl));
    }
}
