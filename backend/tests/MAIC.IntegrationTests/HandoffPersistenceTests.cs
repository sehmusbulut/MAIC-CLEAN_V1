using MAIC.Application.Conversations;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class HandoffPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schema_enforces_current_membership_tenant_history_keys_sequences_and_durable_users()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        await store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Agent.Id, Cancellation);
        var first = Assert.Single((await store.ListAssignmentsAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, 0, 100, Cancellation)).Assignments!);
        await store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Owner.Id, Cancellation);
        var second = (await store.ListAssignmentsAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, 0, 100, Cancellation)).Assignments![1];
        var request = (await store.RequestHandoffAsync(seed.Agent.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation)).Handoff!;
        await RejectAsync($"UPDATE \"Conversations\" SET \"AssignedUserId\" = {other.Agent.Id} WHERE \"Id\" = {seed.Conversation.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"DELETE FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {seed.Workspace.Id} AND \"UserId\" = {seed.Owner.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"WorkspaceId\" = {other.Workspace.Id} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"WorkspaceId\" = {other.Workspace.Id} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"ActorUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"AssignedUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"PreviousAssignedUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"RequesterUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"Sequence\" = {first.Sequence} WHERE \"Id\" = {second.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"Sequence\" = {0L} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"PreviousAssignedUserId\" = \"AssignedUserId\" WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"Reason\" = {"BAD"} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"Reason\" = {"MEMBER_REMOVED"} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"ConversationAssignments\" SET \"Reason\" = {"HANDOFF_ACCEPTED"} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"LastAssignmentSequence\" = {-1L} WHERE \"Id\" = {seed.Conversation.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"LastHandoffSequence\" = {-1L} WHERE \"Id\" = {seed.Conversation.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"Sequence\" = {0L} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"ClientRequestId\" = {Guid.Empty} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"Status\" = {"ACCEPTED"} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalActorUserId\" = {seed.Owner.Id} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"Status\" = {"BAD"} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);

        await store.AcceptHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, request.Id, Cancellation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalActorUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalReason\" = NULL WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalAt\" = NULL WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalActorUserId\" = NULL WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalReason\" = {"RESOLVED"} WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"TerminalAt\" = \"CreatedAt\" - INTERVAL '1 second' WHERE \"Id\" = {request.Id}", PostgresErrorCodes.CheckViolation);
        var pending = (await store.RequestHandoffAsync(seed.Agent.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation)).Handoff!;
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"Sequence\" = {request.Sequence} WHERE \"Id\" = {pending.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"ClientRequestId\" = {request.ClientRequestId} WHERE \"Id\" = {pending.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"HandoffRequests\" SET \"Status\" = 'PENDING', \"TerminalActorUserId\" = NULL, \"TerminalAt\" = NULL, \"TerminalReason\" = NULL WHERE \"Id\" = {request.Id}", PostgresErrorCodes.UniqueViolation);
        await db.WorkspaceMembers.Where(m => m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Agent.Id).ExecuteDeleteAsync(Cancellation);
        Assert.Equal(seed.Agent.Id, (await db.HandoffRequests.AsNoTracking().SingleAsync(h => h.Id == pending.Id, Cancellation)).RequesterUserId);
        Assert.Equal(seed.Agent.Id, (await db.ConversationAssignments.AsNoTracking().SingleAsync(a => a.Id == first.Id, Cancellation)).AssignedUserId);
        await RejectAsync($"DELETE FROM \"Users\" WHERE \"Id\" = {seed.Agent.Id}", PostgresErrorCodes.ForeignKeyViolation);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_ConversationHandoff"));

        async Task RejectAsync(FormattableString sql, string state)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Cancellation));
            Assert.Equal(state, error.SqlState);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Assignment_collision_rolls_back_pending_transition_assignee_and_counter_then_context_recovers(bool accept)
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        await store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Agent.Id, Cancellation);
        var pending = (await store.RequestHandoffAsync(seed.Agent.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation)).Handoff!;
        // Force the next assignment history insert to violate its unique sequence constraint.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastAssignmentSequence\" = {0L} WHERE \"Id\" = {seed.Conversation.Id}", Cancellation);
        Task<ConversationResult> MutateAsync() => accept
            ? store.AcceptHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, pending.Id, Cancellation)
            : store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Owner.Id, Cancellation);
        Assert.Equal(ConversationResultStatus.Conflict, (await MutateAsync()).Status);
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
        Assert.Equal(seed.Agent.Id, conversation.AssignedUserId);
        Assert.Equal(0, conversation.LastAssignmentSequence);
        var request = await db.HandoffRequests.AsNoTracking().SingleAsync(h => h.Id == pending.Id, Cancellation);
        Assert.Equal(HandoffState.PENDING, request.Status);
        Assert.Null(request.TerminalActorUserId);
        Assert.Null(request.TerminalAt);
        Assert.Equal(1, await db.ConversationAssignments.CountAsync(a => a.ConversationId == seed.Conversation.Id, Cancellation));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        await db.SaveChangesAsync(Cancellation);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastAssignmentSequence\" = {1L} WHERE \"Id\" = {seed.Conversation.Id}", Cancellation);
        Assert.Equal(ConversationResultStatus.Success, (await MutateAsync()).Status);
        Assert.Equal(seed.Owner.Id, (await store.GetAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Cancellation)).Conversation!.AssignedUserId);
        Assert.Equal(new long[] { 1, 2 }, (await store.ListAssignmentsAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, 0, 100, Cancellation)).Assignments!.Select(a => a.Sequence));
        Assert.Equal(accept ? HandoffState.ACCEPTED : HandoffState.CANCELLED,
            (await db.HandoffRequests.AsNoTracking().SingleAsync(h => h.Id == pending.Id, Cancellation)).Status);
    }

    [Fact]
    public async Task Request_collision_rolls_back_counter_without_leaving_pending_insert()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        var first = (await store.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation)).Handoff!;
        await store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, null, Cancellation);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastHandoffSequence\" = {0L} WHERE \"Id\" = {seed.Conversation.Id}", Cancellation);
        var key = Guid.NewGuid();
        Assert.Equal(ConversationResultStatus.Conflict,
            (await store.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, key, Cancellation)).Status);
        Assert.Equal(0, (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation)).LastHandoffSequence);
        Assert.Equal(first.Id, Assert.Single(await db.HandoffRequests.AsNoTracking().Where(h => h.ConversationId == seed.Conversation.Id).ToArrayAsync(Cancellation)).Id);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastHandoffSequence\" = {1L} WHERE \"Id\" = {seed.Conversation.Id}", Cancellation);
        var next = await store.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, key, Cancellation);
        Assert.Equal(ConversationResultStatus.Success, next.Status);
        Assert.Equal(2, next.Handoff!.Sequence);
    }

    [Fact]
    public async Task Both_history_cursors_are_SQL_bounded_and_authorize_in_the_same_statement()
    {
        var seed = await SeedAsync();
        var other = await SeedAsync();
        await using (var setup = fixture.CreateContext())
        {
            var writer = new ConversationStore(setup);
            for (var i = 0; i < 3; i++)
            {
                await writer.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation);
                await writer.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, i % 2 == 0 ? seed.Agent.Id : seed.Owner.Id, Cancellation);
            }
        }
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);
        var store = new ConversationStore(db);
        foreach (var handoffs in new[] { false, true })
        {
            for (var sequence = 1; sequence <= 3; sequence++)
            {
                var page = await ReadAsync(seed.Owner.Id, sequence - 1);
                Assert.Equal(sequence, handoffs ? Assert.Single(page.Handoffs!).Sequence : Assert.Single(page.Assignments!).Sequence);
                Assert.Equal(sequence < 3 ? sequence : (long?)null, page.NextAfterSequence);
                AssertBounded();
            }
            var empty = await ReadAsync(seed.Owner.Id, long.MaxValue);
            Assert.Equal(ConversationResultStatus.Success, empty.Status);
            Assert.True(handoffs ? empty.Handoffs!.Count == 0 : empty.Assignments!.Count == 0);
            Assert.Null(empty.NextAfterSequence);
            AssertBounded();
            Assert.Equal(ConversationResultStatus.NotFound, (await ReadAsync(other.Owner.Id, 0)).Status);
            AssertBounded();

            Task<ConversationResult> ReadAsync(Guid actor, long sequence) => handoffs
                ? store.ListHandoffsAsync(actor, seed.Workspace.Id, seed.Conversation.Id, sequence, 1, Cancellation)
                : store.ListAssignmentsAsync(actor, seed.Workspace.Id, seed.Conversation.Id, sequence, 1, Cancellation);
        }

        void AssertBounded()
        {
            var sql = Assert.Single(commands);
            Assert.True(sql.Contains("LIMIT") || sql.Contains("ROW_NUMBER() OVER") && sql.Contains(".row <="), "Page must be bounded within SQL.");
            Assert.Contains("WorkspaceMembers", sql);
            commands.Clear();
        }
    }

    [Fact]
    public async Task Migration_downgrade_reopens_resolved_conversations_and_preserves_messages_then_upgrades_cleanly()
    {
        var seed = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        var message = (await store.AppendAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), "Durable message", Cancellation)).Message!;
        await store.AssignAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, seed.Agent.Id, Cancellation);
        await store.RequestHandoffAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Guid.NewGuid(), Cancellation);
        await store.ResolveAsync(seed.Owner.Id, seed.Workspace.Id, seed.Conversation.Id, Cancellation);
        db.ChangeTracker.Clear();
        await db.GetService<IMigrator>().MigrateAsync("20260925143843_ConversationsAndMessages", Cancellation);
        try
        {
            Assert.Equal("OPEN", await db.Database.SqlQuery<string>($"SELECT \"Status\" AS \"Value\" FROM \"Conversations\" WHERE \"Id\" = {seed.Conversation.Id}").SingleAsync(Cancellation));
            Assert.Equal(message.Content, await db.Database.SqlQuery<string>($"SELECT \"Content\" AS \"Value\" FROM \"Messages\" WHERE \"Id\" = {message.Id}").SingleAsync(Cancellation));
        }
        finally { await db.Database.MigrateAsync(Cancellation); }
        var conversation = await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
        Assert.Equal(ConversationState.OPEN, conversation.Status);
        Assert.Null(conversation.AssignedUserId);
        Assert.Equal(0, conversation.LastAssignmentSequence);
        Assert.Equal(0, conversation.LastHandoffSequence);
        Assert.Equal(1, conversation.LastMessageSequence);
        Assert.Equal(0, await db.ConversationAssignments.CountAsync(a => a.ConversationId == seed.Conversation.Id, Cancellation));
        Assert.Equal(0, await db.HandoffRequests.CountAsync(h => h.ConversationId == seed.Conversation.Id, Cancellation));
        Assert.False(db.Database.HasPendingModelChanges());
    }

    private async Task<Seed> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Handoff persistence");
        var owner = new User();
        var agent = new User();
        var channel = new Channel(workspace.Id, "Web");
        var contact = new Contact(workspace.Id, "Visitor", []);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, owner.Id, Guid.NewGuid());
        db.AddRange(workspace, owner, agent, channel, contact, conversation,
            new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Cancellation);
        return new(workspace, owner, agent, conversation);
    }

    private sealed record Seed(Workspace Workspace, User Owner, User Agent, Conversation Conversation);
}
