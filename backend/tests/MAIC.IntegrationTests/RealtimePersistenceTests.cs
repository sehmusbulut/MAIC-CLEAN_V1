using MAIC.Application.AI;
using MAIC.Application.Conversations;
using MAIC.Application.Realtime;
using MAIC.Application.Workspaces;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class RealtimePersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Snapshot_and_candidate_audience_use_one_scoped_metadata_only_statement()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>()
            .UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, [DbLoggerCategory.Database.Command.Name], LogLevel.Information).Options);
        var store = new ConversationRealtimeStore(db);
        Guid[] candidates = [seed.Owner.Id, seed.Agent.Id, other.Owner.Id, other.Agent.Id, Guid.NewGuid(), seed.Agent.Id];

        var result = Assert.IsType<RealtimeReadResult>(await store.ReadAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        Assert.Equal(seed.Workspace.Id, result.Snapshot.WorkspaceId);
        Assert.Equal(seed.Conversation.Id, result.Snapshot.ConversationId);
        Assert.Equal(0L, result.Snapshot.LastMessageSequence);
        Assert.Null(result.Snapshot.LatestHandoff);
        Assert.Equal(new[] { seed.Owner.Id, seed.Agent.Id }.Order(), result.AuthorizedUserIds.Order());
        Assert.Empty(db.ChangeTracker.Entries());
        AssertScopedStatement();

        Assert.Null(await store.ReadAsync(other.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        AssertScopedStatement();
        Assert.Null(await store.ReadAsync(seed.Owner.Id, seed.Workspace.Id, other.Conversation.Id, candidates, Ct));
        AssertScopedStatement();
        Assert.Null(await store.ReadAsync(seed.Owner.Id, other.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        AssertScopedStatement();
        Assert.Null(await store.ReadAsync(seed.Owner.Id, seed.Workspace.Id, Guid.NewGuid(), candidates, Ct));
        AssertScopedStatement();
        Assert.Empty(Assert.IsType<RealtimeReadResult>(await store.ReadAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, [], Ct)).AuthorizedUserIds);
        AssertScopedStatement();

        void AssertScopedStatement()
        {
            var sql = Assert.Single(commands);
            Assert.Contains("WorkspaceMembers", sql);
            Assert.Contains("Conversations", sql);
            Assert.Contains("HandoffRequests", sql);
            Assert.DoesNotContain("\"Messages\"", sql);
            Assert.DoesNotContain("\"Content\"", sql);
            Assert.DoesNotContain("FOR UPDATE", sql);
            Assert.True(sql.Contains("LIMIT") || sql.Contains("ROW_NUMBER() OVER"), "Latest handoff must be bounded in SQL.");
            commands.Clear();
        }
    }

    [Fact]
    public async Task Membership_removal_revokes_actor_and_audience_and_exposes_assignment_cleanup_on_reused_reader()
    {
        var seed = await SeedAsync();
        await using var writerDb = fixture.CreateContext();
        var writer = new ConversationStore(writerDb);
        Assert.Equal(ConversationResultStatus.Success, (await writer.AssignAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, seed.Agent.Id, Ct)).Status);
        await using var readerDb = fixture.CreateContext();
        // Previously tracked data must never authorize a subsequent poll after revocation.
        await readerDb.WorkspaceMembers.Where(m => m.WorkspaceId == seed.Workspace.Id).LoadAsync(Ct);
        await readerDb.Conversations.Where(c => c.Id == seed.Conversation.Id).LoadAsync(Ct);
        var reader = new ConversationRealtimeStore(readerDb);
        Guid[] candidates = [seed.Owner.Id, seed.Agent.Id];
        var before = Assert.IsType<RealtimeReadResult>(await reader.ReadAsync(seed.Agent.Id,
            seed.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        Assert.Equal(seed.Agent.Id, before.Snapshot.AssignedUserId);
        Assert.Contains(seed.Agent.Id, before.AuthorizedUserIds);

        Assert.Equal(MembershipStatus.Success, (await new MembershipStore(writerDb).RemoveAsync(
            seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Ct)).Status);

        Assert.Null(await reader.ReadAsync(seed.Agent.Id, seed.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        var after = Assert.IsType<RealtimeReadResult>(await reader.ReadAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, candidates, Ct));
        Assert.Equal(seed.Owner.Id, Assert.Single(after.AuthorizedUserIds));
        Assert.Null(after.Snapshot.AssignedUserId);
        Assert.Equal(before.Snapshot.LastAssignmentSequence + 1, after.Snapshot.LastAssignmentSequence);
    }

    [Fact]
    public async Task Uncommitted_message_assignment_handoff_and_lifecycle_are_invisible_and_rollback_keeps_snapshot()
    {
        var seed = await SeedAsync();
        await using var readerDb = fixture.CreateContext();
        var reader = new ConversationRealtimeStore(readerDb);
        var before = (await ReadAsync(reader, seed)).Snapshot;
        await using var writerDb = fixture.CreateContext();
        await using var transaction = await writerDb.Database.BeginTransactionAsync(Ct);
        var conversation = await writerDb.Conversations.SingleAsync(c => c.Id == seed.Conversation.Id, Ct);
        writerDb.Messages.Add(new Message(seed.Workspace.Id, conversation.Id, seed.Owner.Id, Guid.NewGuid(),
            conversation.AdvanceMessageSequence(), "Never committed"));
        writerDb.ConversationAssignments.Add(conversation.Assign(seed.Agent.Id, seed.Owner.Id, AssignmentReason.MANUAL)!);
        var handoff = conversation.RequestHandoff(seed.Owner.Id, Guid.NewGuid());
        handoff.Complete(seed.Owner.Id, HandoffTerminalReason.RESOLVED);
        writerDb.HandoffRequests.Add(handoff);
        conversation.Resolve();
        await writerDb.SaveChangesAsync(Ct);

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));
        var during = Assert.IsType<RealtimeReadResult>(await reader.ReadAsync(seed.Owner.Id,
            seed.Workspace.Id, conversation.Id, [seed.Owner.Id], readTimeout.Token));
        Assert.Equal(before, during.Snapshot);
        await transaction.RollbackAsync(Ct);
        Assert.Equal(before, (await ReadAsync(reader, seed)).Snapshot);
        Assert.False(await readerDb.Messages.AnyAsync(m => m.ConversationId == conversation.Id, Ct));
        Assert.False(await readerDb.HandoffRequests.AnyAsync(h => h.ConversationId == conversation.Id, Ct));
    }

    [Fact]
    public async Task Committed_append_is_fresh_with_tracked_entities_while_retry_and_conflict_leave_snapshot_unchanged()
    {
        var seed = await SeedAsync();
        await using var readerDb = fixture.CreateContext();
        await readerDb.Conversations.Where(c => c.Id == seed.Conversation.Id).LoadAsync(Ct);
        var reader = new ConversationRealtimeStore(readerDb);
        var before = (await ReadAsync(reader, seed)).Snapshot;
        await using var writerDb = fixture.CreateContext();
        var writer = new ConversationStore(writerDb);
        var messageId = Guid.NewGuid();
        Assert.Equal(ConversationResultStatus.Success, (await writer.AppendAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, messageId, "Committed", Ct)).Status);
        var committed = (await ReadAsync(reader, seed)).Snapshot;
        Assert.Equal(before.LastMessageSequence + 1, committed.LastMessageSequence);

        Assert.True((await writer.AppendAsync(seed.Owner.Id, seed.Workspace.Id,
            seed.Conversation.Id, messageId, "Committed", Ct)).Replayed);
        Assert.Equal(committed, (await ReadAsync(reader, seed)).Snapshot);
        Assert.Equal(ConversationResultStatus.Conflict, (await writer.AppendAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, messageId, "Conflicting retry", Ct)).Status);
        Assert.Equal(committed, (await ReadAsync(reader, seed)).Snapshot);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Handoff_terminal_transition_changes_fingerprint_without_advancing_any_conversation_counter(bool accept)
    {
        var seed = await SeedAsync();
        await using var writerDb = fixture.CreateContext();
        var writer = new ConversationStore(writerDb);
        Assert.Equal(ConversationResultStatus.Success, (await writer.AssignAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, seed.Owner.Id, Ct)).Status);
        var request = await writer.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id,
            seed.Conversation.Id, Guid.NewGuid(), Ct);
        Assert.Equal(ConversationResultStatus.Success, request.Status);
        await using var readerDb = fixture.CreateContext();
        var reader = new ConversationRealtimeStore(readerDb);
        var pending = (await ReadAsync(reader, seed)).Snapshot;
        Assert.Equal("PENDING", pending.LatestHandoff!.Status);

        Assert.Equal(ConversationResultStatus.Success, (await CompleteAsync()).Status);
        var terminal = (await ReadAsync(reader, seed)).Snapshot;
        Assert.NotEqual(pending, terminal);
        Assert.Equal(pending with { LatestHandoff = terminal.LatestHandoff }, terminal);
        Assert.Equal(request.Handoff!.Id, terminal.LatestHandoff!.Id);
        Assert.Equal(accept ? "ACCEPTED" : "CANCELLED", terminal.LatestHandoff.Status);
        Assert.Equal(accept ? "ACCEPTED" : "MANUAL_ASSIGNMENT", terminal.LatestHandoff.TerminalReason);
        Assert.Equal(seed.Owner.Id, terminal.LatestHandoff.TerminalActorUserId);
        Assert.NotNull(terminal.LatestHandoff.TerminalAt);
        Assert.Equal(ConversationResultStatus.Success, (await CompleteAsync()).Status);
        Assert.Equal(terminal, (await ReadAsync(reader, seed)).Snapshot);

        var next = await writer.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id,
            seed.Conversation.Id, Guid.NewGuid(), Ct);
        Assert.Equal(ConversationResultStatus.Success, next.Status);
        var latest = (await ReadAsync(reader, seed)).Snapshot;
        Assert.Equal(terminal.LastHandoffSequence + 1, latest.LastHandoffSequence);
        Assert.Equal(next.Handoff!.Id, latest.LatestHandoff!.Id);
        Assert.Equal("PENDING", latest.LatestHandoff.Status);
        Assert.Null(latest.LatestHandoff.TerminalAt);

        Task<ConversationResult> CompleteAsync() => accept
            ? writer.AcceptHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, request.Handoff!.Id, Ct)
            : writer.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Owner.Id, Ct);
    }

    [Fact]
    public async Task Resolve_then_reopen_between_reads_advances_revision_even_when_final_status_matches()
    {
        var seed = await SeedAsync();
        await using var readerDb = fixture.CreateContext();
        var reader = new ConversationRealtimeStore(readerDb);
        var before = (await ReadAsync(reader, seed)).Snapshot;
        await using var writerDb = fixture.CreateContext();
        var writer = new ConversationStore(writerDb);
        Assert.Equal(ConversationResultStatus.Success, (await writer.ResolveAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, Ct)).Status);
        Assert.Equal(ConversationResultStatus.Success, (await writer.ReopenAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, Ct)).Status);
        var reopened = (await ReadAsync(reader, seed)).Snapshot;
        Assert.Equal(before.Status, reopened.Status);
        Assert.Equal(before with { Revision = before.Revision + 2 }, reopened);
        Assert.Equal(ConversationResultStatus.Success, (await writer.ReopenAsync(seed.Owner.Id,
            seed.Workspace.Id, seed.Conversation.Id, Ct)).Status);
        Assert.Equal(reopened, (await ReadAsync(reader, seed)).Snapshot);
    }

    [Fact]
    public async Task Ai_commit_atomically_exposes_message_and_handoff_but_claim_and_replay_do_not_change_snapshot()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(fixture);
        await using var readerDb = fixture.CreateContext();
        var reader = new ConversationRealtimeStore(readerDb);
        var before = await ReadSnapshotAsync();
        Assert.Equal(seed.Agent.Id, before.AiAgentId);
        await using var writerDb = fixture.CreateContext();
        var writer = new ConversationAiExecutionStore(writerDb);
        var claim = (await writer.BeginAsync(seed.Actor.Id, seed.Request, new(), Ct)).Claim!;
        Assert.NotNull(claim);
        Assert.Equal(before, await ReadSnapshotAsync());

        var result = await writer.CommitAsync(claim, new(AiExecutionOutcome.FALLBACK,
            "Please continue with a support agent.", true, [], []), new(), Ct);
        Assert.Equal(AiExecutionStatus.Success, result.Status);
        var committed = await ReadSnapshotAsync();
        Assert.Equal(before.LastMessageSequence + 1, committed.LastMessageSequence);
        Assert.Equal(before.LastHandoffSequence + 1, committed.LastHandoffSequence);
        Assert.Equal(result.Handoff!.Id, committed.LatestHandoff!.Id);
        Assert.Equal(result.Message!.Sequence, committed.LastMessageSequence);
        Assert.Equal("PENDING", committed.LatestHandoff.Status);
        var replay = await writer.BeginAsync(seed.Actor.Id, seed.Request, new(), Ct);
        Assert.Null(replay.Claim);
        Assert.Equal(result.Message.Id, replay.Result.Message!.Id);
        Assert.Equal(committed, await ReadSnapshotAsync());

        async Task<ConversationRealtimeSnapshot> ReadSnapshotAsync() => Assert.IsType<RealtimeReadResult>(
            await reader.ReadAsync(seed.Actor.Id, seed.Workspace.Id, seed.Conversation.Id, [seed.Actor.Id], Ct)).Snapshot;
    }

    private static async Task<RealtimeReadResult> ReadAsync(ConversationRealtimeStore store, Seed seed) =>
        Assert.IsType<RealtimeReadResult>(await store.ReadAsync(seed.Owner.Id, seed.Workspace.Id,
            seed.Conversation.Id, [seed.Owner.Id, seed.Agent.Id], Ct));

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Realtime persistence");
        var owner = new User();
        var agent = new User();
        var channel = new Channel(workspace.Id, "Web");
        var contact = new Contact(workspace.Id, "Visitor", []);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, owner.Id, Guid.NewGuid());
        db.AddRange(workspace, owner, agent, channel, contact, conversation,
            new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Ct);
        return new(workspace, owner, agent, conversation);
    }

    private sealed record Seed(Workspace Workspace, User Owner, User Agent, Conversation Conversation);
}
