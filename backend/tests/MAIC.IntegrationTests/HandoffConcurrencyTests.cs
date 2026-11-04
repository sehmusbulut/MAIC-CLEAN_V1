using MAIC.Application.Conversations;
using MAIC.Application.Workspaces;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class HandoffConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_and_target_removal_serialize_without_stale_assignment(bool removalFirst)
    {
        var seed = await SeedAsync();
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        if (removalFirst)
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {seed.Workspace.Id} AND \"UserId\" = {seed.Agent.Id} FOR UPDATE", timeout.Token);
        else
            await LockConversationAsync(blocker, seed, timeout.Token);
        var writerName = Name("assign");
        var removerName = Name("remove");
        await using var writer = NamedContext(writerName);
        await using var remover = NamedContext(removerName);
        Task<ConversationResult>? assignment = null;
        Task<MembershipResult>? removal = null;
        try
        {
            if (removalFirst)
            {
                removal = new MembershipStore(remover).RemoveAsync(
                    seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, timeout.Token);
                await ObserveLockAsync(removerName, removal, "WorkspaceMembers", timeout.Token);
                assignment = AssignAsync(writer, seed, seed.Agent.Id, timeout.Token);
                await ObserveLockAsync(writerName, assignment, "Workspaces", timeout.Token, removerName);
            }
            else
            {
                assignment = AssignAsync(writer, seed, seed.Agent.Id, timeout.Token);
                await ObserveLockAsync(writerName, assignment, "Conversations", timeout.Token);
                removal = new MembershipStore(remover).RemoveAsync(
                    seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, timeout.Token);
                await ObserveLockAsync(removerName, removal, "Workspaces", timeout.Token, writerName);
            }
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(removalFirst ? ConversationResultStatus.NotFound : ConversationResultStatus.Success,
                (await assignment).Status);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            await using var verify = fixture.CreateContext();
            var conversation = await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
            Assert.Null(conversation.AssignedUserId);
            Assert.False(await verify.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Agent.Id, Cancellation));
            var history = await verify.ConversationAssignments.AsNoTracking()
                .Where(a => a.ConversationId == seed.Conversation.Id).OrderBy(a => a.Sequence).ToArrayAsync(Cancellation);
            if (removalFirst)
            {
                Assert.Empty(history);
                Assert.Equal(0, conversation.LastAssignmentSequence);
            }
            else
            {
                Assert.Equal(new long[] { 1, 2 }, history.Select(a => a.Sequence));
                Assert.Null(history[0].PreviousAssignedUserId);
                Assert.Equal(seed.Agent.Id, history[0].AssignedUserId);
                Assert.Equal(AssignmentReason.MANUAL, history[0].Reason);
                Assert.Equal(seed.Agent.Id, history[1].PreviousAssignedUserId);
                Assert.Null(history[1].AssignedUserId);
                Assert.Equal(AssignmentReason.MEMBER_REMOVED, history[1].Reason);
                Assert.Equal(seed.Owner.Id, history[1].ActorUserId);
                Assert.Equal(2, conversation.LastAssignmentSequence);
            }
        }
        finally
        {
            await timeout.CancelAsync();
            if (assignment is not null) await DrainAsync(assignment);
            if (removal is not null) await DrainAsync(removal);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlapping_requests_preserve_one_pending_handoff_and_authorize_terminal_replays(bool exactRetry)
    {
        var seed = await SeedAsync();
        var requestKey = Guid.NewGuid();
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await LockConversationAsync(blocker, seed, timeout.Token);
        var firstName = Name("request-first");
        var secondName = Name("request-second");
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var first = RequestAsync(firstDb, seed, seed.Agent.Id, requestKey, timeout.Token);
        Task<ConversationResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Conversations", timeout.Token);
            second = RequestAsync(secondDb, seed, exactRetry ? seed.Agent.Id : seed.OtherAgent.Id,
                exactRetry ? requestKey : Guid.NewGuid(), timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);
            var created = await first;
            var competing = await second;
            Assert.Equal(ConversationResultStatus.Success, created.Status);
            Assert.False(created.Replayed);
            Assert.Equal(exactRetry ? ConversationResultStatus.Success : ConversationResultStatus.Conflict, competing.Status);
            if (exactRetry)
            {
                Assert.True(competing.Replayed);
                Assert.Equal(created.Handoff!.Id, competing.Handoff!.Id);
            }
            await using var verify = fixture.CreateContext();
            var pending = Assert.Single(await verify.HandoffRequests.AsNoTracking()
                .Where(h => h.ConversationId == seed.Conversation.Id).ToArrayAsync(Cancellation));
            Assert.Equal(created.Handoff!.Id, pending.Id);
            Assert.Equal(HandoffState.PENDING, pending.Status);
            Assert.Equal(1, pending.Sequence);
            Assert.Equal(1, (await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation)).LastHandoffSequence);

            Assert.Equal(ConversationResultStatus.Success, (await ResolveAsync(verify, seed, Cancellation)).Status);
            var terminalReplay = await RequestAsync(verify, seed, seed.Agent.Id, requestKey, Cancellation);
            Assert.Equal(ConversationResultStatus.Success, terminalReplay.Status);
            Assert.True(terminalReplay.Replayed);
            Assert.Equal(pending.Id, terminalReplay.Handoff!.Id);
            Assert.Equal("CANCELLED", terminalReplay.Handoff.Status);
            Assert.Equal(MembershipStatus.Success, (await new MembershipStore(verify).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation)).Status);
            Assert.Equal(ConversationResultStatus.NotFound,
                (await RequestAsync(verify, seed, seed.Agent.Id, requestKey, Cancellation)).Status);
            Assert.Equal(1, await verify.HandoffRequests.CountAsync(h => h.ConversationId == seed.Conversation.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [Fact]
    public async Task Competing_accepts_have_one_winner_and_retries_never_restore_removed_assignment()
    {
        var seed = await SeedAsync();
        Guid handoffId;
        await using (var initial = fixture.CreateContext())
        {
            var requested = await RequestAsync(initial, seed, seed.Owner.Id, Guid.NewGuid(), Cancellation);
            Assert.Equal(ConversationResultStatus.Success, requested.Status);
            handoffId = requested.Handoff!.Id;
        }
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await LockConversationAsync(blocker, seed, timeout.Token);
        var firstName = Name("accept-first");
        var secondName = Name("accept-second");
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var first = AcceptAsync(firstDb, seed, seed.Agent.Id, handoffId, timeout.Token);
        Task<ConversationResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Conversations", timeout.Token);
            second = AcceptAsync(secondDb, seed, seed.OtherAgent.Id, handoffId, timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, (await first).Status);
            Assert.Equal(ConversationResultStatus.Conflict, (await second).Status);
            await using var verify = fixture.CreateContext();
            var accepted = await verify.HandoffRequests.AsNoTracking().SingleAsync(h => h.Id == handoffId, Cancellation);
            Assert.Equal(HandoffState.ACCEPTED, accepted.Status);
            Assert.Equal(seed.Agent.Id, accepted.TerminalActorUserId);
            Assert.Equal(seed.Agent.Id, (await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation)).AssignedUserId);
            var history = Assert.Single(await verify.ConversationAssignments.AsNoTracking()
                .Where(a => a.ConversationId == seed.Conversation.Id).ToArrayAsync(Cancellation));
            Assert.Equal(AssignmentReason.HANDOFF_ACCEPTED, history.Reason);
            Assert.Equal(seed.Agent.Id, history.AssignedUserId);
            var replay = await AcceptAsync(verify, seed, seed.Agent.Id, handoffId, Cancellation);
            Assert.Equal(ConversationResultStatus.Success, replay.Status);
            Assert.True(replay.Replayed);

            Assert.Equal(MembershipStatus.Success, (await new MembershipStore(verify).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation)).Status);
            Assert.Equal(ConversationResultStatus.NotFound,
                (await AcceptAsync(verify, seed, seed.Agent.Id, handoffId, Cancellation)).Status);
            verify.WorkspaceMembers.Add(new WorkspaceMember(seed.Workspace.Id, seed.Agent.Id, WorkspaceRole.AGENT));
            await verify.SaveChangesAsync(Cancellation);
            var rejoinedReplay = await AcceptAsync(verify, seed, seed.Agent.Id, handoffId, Cancellation);
            Assert.Equal(ConversationResultStatus.Success, rejoinedReplay.Status);
            Assert.True(rejoinedReplay.Replayed);
            var afterReplay = await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
            Assert.Null(afterReplay.AssignedUserId);
            Assert.Equal(2, afterReplay.LastAssignmentSequence);
            var finalHistory = await verify.ConversationAssignments.AsNoTracking()
                .Where(a => a.ConversationId == seed.Conversation.Id).OrderBy(a => a.Sequence).ToArrayAsync(Cancellation);
            Assert.Equal(new long[] { 1, 2 }, finalHistory.Select(a => a.Sequence));
            Assert.Equal(AssignmentReason.MEMBER_REMOVED, finalHistory[1].Reason);
            Assert.Null(finalHistory[1].AssignedUserId);
            Assert.Equal(1, await verify.HandoffRequests.CountAsync(h => h.ConversationId == seed.Conversation.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Resolve_and_fresh_append_or_accept_observe_commit_order(bool accept, bool resolveFirst)
    {
        var seed = await SeedAsync();
        var originalMessageKey = Guid.NewGuid();
        Guid? handoffId = null;
        await using (var initial = fixture.CreateContext())
        {
            Assert.Equal(ConversationResultStatus.Success, (await new ConversationStore(initial).AppendAsync(
                seed.Agent.Id, seed.Workspace.Id, seed.Conversation.Id, originalMessageKey, "Original", Cancellation)).Status);
            if (accept)
            {
                var requested = await RequestAsync(initial, seed, seed.Owner.Id, Guid.NewGuid(), Cancellation);
                Assert.Equal(ConversationResultStatus.Success, requested.Status);
                handoffId = requested.Handoff!.Id;
            }
        }
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await LockConversationAsync(blocker, seed, timeout.Token);
        var firstName = Name("resolve-race-first");
        var secondName = Name("resolve-race-second");
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        Task<ConversationResult> MutateAsync(MaicDbContext db) => accept
            ? AcceptAsync(db, seed, seed.Agent.Id, handoffId!.Value, timeout.Token)
            : new ConversationStore(db).AppendAsync(seed.Agent.Id, seed.Workspace.Id,
                seed.Conversation.Id, Guid.NewGuid(), "Concurrent", timeout.Token);
        var first = resolveFirst ? ResolveAsync(firstDb, seed, timeout.Token) : MutateAsync(firstDb);
        Task<ConversationResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Conversations", timeout.Token);
            second = resolveFirst ? MutateAsync(secondDb) : ResolveAsync(secondDb, seed, timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);
            var firstResult = await first;
            var secondResult = await second;
            var resolution = resolveFirst ? firstResult : secondResult;
            var mutation = resolveFirst ? secondResult : firstResult;
            Assert.Equal(ConversationResultStatus.Success, resolution.Status);
            Assert.Equal(resolveFirst ? ConversationResultStatus.Conflict : ConversationResultStatus.Success, mutation.Status);
            await using var verify = fixture.CreateContext();
            var conversation = await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
            Assert.Equal(ConversationState.RESOLVED, conversation.Status);
            var assigned = accept && !resolveFirst;
            Assert.Equal(assigned ? seed.Agent.Id : (Guid?)null, conversation.AssignedUserId);
            Assert.Equal(assigned ? 1 : 0, conversation.LastAssignmentSequence);
            Assert.Equal(assigned ? 1 : 0,
                await verify.ConversationAssignments.CountAsync(a => a.ConversationId == seed.Conversation.Id, Cancellation));
            var messageCount = !accept && !resolveFirst ? 2 : 1;
            Assert.Equal(messageCount, conversation.LastMessageSequence);
            Assert.Equal(messageCount, await verify.Messages.CountAsync(m => m.ConversationId == seed.Conversation.Id, Cancellation));
            if (accept)
            {
                var handoff = await verify.HandoffRequests.AsNoTracking().SingleAsync(h => h.Id == handoffId, Cancellation);
                Assert.Equal(resolveFirst ? HandoffState.CANCELLED : HandoffState.ACCEPTED, handoff.Status);
                Assert.Equal(resolveFirst ? seed.Owner.Id : seed.Agent.Id, handoff.TerminalActorUserId);
            }
            var originalReplay = await new ConversationStore(verify).AppendAsync(seed.Agent.Id,
                seed.Workspace.Id, seed.Conversation.Id, originalMessageKey, "Original", Cancellation);
            Assert.Equal(ConversationResultStatus.Success, originalReplay.Status);
            Assert.True(originalReplay.Replayed);
            Assert.Equal(1, originalReplay.Message!.Sequence);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [Fact]
    public async Task Failed_member_deletion_rolls_back_cleanup_and_pending_request_survives_later_removal()
    {
        var seed = await SeedAsync();
        var requestKey = Guid.NewGuid();
        Guid handoffId;
        await using (var initial = fixture.CreateContext())
        {
            Assert.Equal(ConversationResultStatus.Success, (await AssignAsync(initial, seed, seed.Agent.Id, Cancellation)).Status);
            var requested = await RequestAsync(initial, seed, seed.Agent.Id, requestKey, Cancellation);
            Assert.Equal(ConversationResultStatus.Success, requested.Status);
            handoffId = requested.Handoff!.Id;
        }
        await using var guard = fixture.CreateContext();
        await guard.Database.ExecuteSqlRawAsync("""
            CREATE TABLE "HandoffRemovalGuard" (
                "WorkspaceId" uuid NOT NULL,
                "UserId" uuid NOT NULL,
                FOREIGN KEY ("WorkspaceId", "UserId")
                    REFERENCES "WorkspaceMembers" ("WorkspaceId", "UserId") ON DELETE RESTRICT)
            """, Cancellation);
        try
        {
            await guard.Database.ExecuteSqlInterpolatedAsync(
                $"INSERT INTO \"HandoffRemovalGuard\" (\"WorkspaceId\", \"UserId\") VALUES ({seed.Workspace.Id}, {seed.Agent.Id})", Cancellation);
            await using var failed = fixture.CreateContext();
            var error = await Assert.ThrowsAsync<PostgresException>(() => new MembershipStore(failed).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation));
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, error.SqlState);
            await using var verify = fixture.CreateContext();
            Assert.True(await verify.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Agent.Id, Cancellation));
            var conversation = await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
            Assert.Equal(seed.Agent.Id, conversation.AssignedUserId);
            Assert.Equal(1, conversation.LastAssignmentSequence);
            var original = Assert.Single(await verify.ConversationAssignments.AsNoTracking()
                .Where(a => a.ConversationId == seed.Conversation.Id).ToArrayAsync(Cancellation));
            Assert.Equal(AssignmentReason.MANUAL, original.Reason);
            Assert.Equal(seed.Agent.Id, original.AssignedUserId);
            Assert.Equal(HandoffState.PENDING, (await verify.HandoffRequests.AsNoTracking()
                .SingleAsync(h => h.Id == handoffId, Cancellation)).Status);
        }
        finally
        {
            await guard.Database.ExecuteSqlRawAsync("DROP TABLE \"HandoffRemovalGuard\"", Cancellation);
        }

        await using var cleanup = fixture.CreateContext();
        Assert.Equal(MembershipStatus.Success, (await new MembershipStore(cleanup).RemoveAsync(
            seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation)).Status);
        var cleared = await cleanup.Conversations.AsNoTracking()
            .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
        Assert.Null(cleared.AssignedUserId);
        Assert.Equal(2, cleared.LastAssignmentSequence);
        Assert.Equal(HandoffState.PENDING, (await cleanup.HandoffRequests.AsNoTracking()
            .SingleAsync(h => h.Id == handoffId, Cancellation)).Status);
        Assert.Equal(ConversationResultStatus.NotFound,
            (await RequestAsync(cleanup, seed, seed.Agent.Id, requestKey, Cancellation)).Status);
        Assert.Equal(ConversationResultStatus.Success,
            (await AcceptAsync(cleanup, seed, seed.OtherAgent.Id, handoffId, Cancellation)).Status);
        Assert.Equal(seed.OtherAgent.Id, (await cleanup.Conversations.AsNoTracking()
            .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation)).AssignedUserId);
    }

    private static Task<ConversationResult> AssignAsync(MaicDbContext db, Seed seed, Guid? assignedUserId, CancellationToken ct) =>
        new ConversationStore(db).AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, assignedUserId, ct);

    private static Task<ConversationResult> RequestAsync(MaicDbContext db, Seed seed, Guid actorId, Guid key, CancellationToken ct) =>
        new ConversationStore(db).RequestHandoffAsync(actorId, seed.Workspace.Id, seed.Conversation.Id, key, ct);

    private static Task<ConversationResult> AcceptAsync(MaicDbContext db, Seed seed, Guid actorId, Guid handoffId, CancellationToken ct) =>
        new ConversationStore(db).AcceptHandoffAsync(actorId, seed.Workspace.Id, seed.Conversation.Id, handoffId, ct);

    private static Task<ConversationResult> ResolveAsync(MaicDbContext db, Seed seed, CancellationToken ct) =>
        new ConversationStore(db).ResolveAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, ct);

    private static Task<int> LockConversationAsync(MaicDbContext db, Seed seed, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seed.Conversation.Id} FOR UPDATE", ct);

    private static string Name(string label) => "handoff-" + label + "-" + Guid.NewGuid().ToString("N");

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
        catch (Exception) { /* The primary assertion reports the failure; observe tasks during cleanup. */ }
    }

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var owner = new User();
        var agent = new User();
        var otherAgent = new User();
        var workspace = new Workspace("Handoff races");
        var channel = new Channel(workspace.Id, "Chat");
        var contact = new Contact(workspace.Id, "Visitor", [new("email", "visitor@example.com")]);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, owner.Id, Guid.NewGuid());
        db.Users.AddRange(owner, agent, otherAgent);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT),
            new WorkspaceMember(workspace.Id, otherAgent.Id, WorkspaceRole.AGENT));
        db.Channels.Add(channel);
        db.Contacts.Add(contact);
        db.Conversations.Add(conversation);
        await db.SaveChangesAsync(Cancellation);
        return new(workspace, owner, agent, otherAgent, conversation);
    }

    private sealed record Seed(Workspace Workspace, User Owner, User Agent, User OtherAgent, Conversation Conversation);
}
