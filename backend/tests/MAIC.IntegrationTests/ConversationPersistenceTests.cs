using MAIC.Application.Conversations;
using MAIC.Domain.Channels;
using MAIC.Domain.Contacts;
using MAIC.Domain.Conversations;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class ConversationPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Schema_enforces_tenant_bindings_idempotency_sequence_and_durable_sender()
    {
        var (workspace, actor, channel, contact) = await SeedAsync();
        var (other, _, otherChannel, otherContact) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
        Assert.Equal(ConversationResultStatus.Success, (await store.CreateAsync(actor.Id, conversation, Cancellation)).Status);
        var sibling = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
        Assert.Equal(ConversationResultStatus.Success, (await store.CreateAsync(actor.Id, sibling, Cancellation)).Status);
        var first = (await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "First", Cancellation)).Message!;
        var second = (await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "Second", Cancellation)).Message!;

        await RejectAsync($"UPDATE \"Conversations\" SET \"ChannelId\" = {otherChannel.Id} WHERE \"Id\" = {conversation.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"ContactId\" = {otherContact.Id} WHERE \"Id\" = {conversation.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"WorkspaceId\" = {other.Id} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"SenderUserId\" = {Guid.NewGuid()} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"ClientRequestId\" = {conversation.ClientRequestId} WHERE \"Id\" = {sibling.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"ClientMessageId\" = {first.ClientMessageId} WHERE \"Id\" = {second.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"Sequence\" = {first.Sequence} WHERE \"Id\" = {second.Id}", PostgresErrorCodes.UniqueViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"Sequence\" = {0L} WHERE \"Id\" = {second.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"Status\" = {"BAD"} WHERE \"Id\" = {conversation.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Conversations\" SET \"ClientRequestId\" = {Guid.Empty} WHERE \"Id\" = {conversation.Id}", PostgresErrorCodes.CheckViolation);
        await RejectAsync($"UPDATE \"Messages\" SET \"ClientMessageId\" = {Guid.Empty} WHERE \"Id\" = {first.Id}", PostgresErrorCodes.CheckViolation);

        await db.WorkspaceMembers.Where(m => m.WorkspaceId == workspace.Id && m.UserId == actor.Id).ExecuteDeleteAsync(Cancellation);
        Assert.Equal(actor.Id, (await db.Messages.AsNoTracking().SingleAsync(m => m.Id == first.Id, Cancellation)).SenderUserId);
        Assert.Equal(ConversationResultStatus.NotFound, (await store.GetAsync(actor.Id, workspace.Id, conversation.Id, Cancellation)).Status);
        Assert.False(db.Database.HasPendingModelChanges());
        Assert.Contains(await db.Database.GetAppliedMigrationsAsync(Cancellation), m => m.EndsWith("_ConversationsAndMessages"));

        async Task RejectAsync(FormattableString sql, string state)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Cancellation));
            Assert.Equal(state, error.SqlState);
        }
    }

    [Fact]
    public async Task Database_content_constraints_match_UTF16_multiline_and_control_policy()
    {
        var (workspace, actor, channel, contact) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
        await store.CreateAsync(actor.Id, conversation, Cancellation);
        var message = (await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "Text", Cancellation)).Message!;
        foreach (var invalid in new[] { "", " \r\n\t", "\u00a0\u2007", "Text\u0001", "Text\u007f", "Text\u0085", "Text\u009f",
                     string.Concat(Enumerable.Repeat("\ud83d\ude42", 2001)) })
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Messages\" SET \"Content\" = {invalid} WHERE \"Id\" = {message.Id}", Cancellation));
            Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        }
        foreach (var valid in new[] { "Text\r\n\tmore", new string('x', 4000), string.Concat(Enumerable.Repeat("\ud83d\ude42", 2000)) })
            Assert.Equal(1, await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE \"Messages\" SET \"Content\" = {valid} WHERE \"Id\" = {message.Id}", Cancellation));
    }

    [Fact]
    public async Task Constraint_conflict_rolls_back_counter_and_context_can_append_again()
    {
        var (workspace, actor, channel, contact) = await SeedAsync();
        await using var db = fixture.CreateContext();
        var store = new ConversationStore(db);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
        await store.CreateAsync(actor.Id, conversation, Cancellation);
        var first = (await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "First", Cancellation)).Message!;
        // Inject a counter inconsistency to force the database uniqueness recovery path.
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastMessageSequence\" = {0L} WHERE \"Id\" = {conversation.Id}", Cancellation);
        var collision = await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "Collision", Cancellation);
        Assert.Equal(ConversationResultStatus.Conflict, collision.Status);
        Assert.Single(await db.Messages.Where(m => m.ConversationId == conversation.Id).ToListAsync(Cancellation));
        Assert.Equal(0, (await db.Conversations.AsNoTracking().SingleAsync(c => c.Id == conversation.Id, Cancellation)).LastMessageSequence);
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastMessageSequence\" = {1L} WHERE \"Id\" = {conversation.Id}", Cancellation);
        var retry = await store.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), "Retry", Cancellation);
        Assert.Equal(ConversationResultStatus.Success, retry.Status);
        Assert.Equal(2, retry.Message!.Sequence);
        Assert.Equal(first.CreatedAt, (await store.ListMessagesAsync(actor.Id, workspace.Id, conversation.Id, 0, 100, Cancellation)).Messages![0].CreatedAt);
    }

    [Fact]
    public async Task Cursor_lists_are_SQL_bounded_and_authorize_in_a_single_snapshot()
    {
        var (workspace, actor, channel, contact) = await SeedAsync();
        var (_, outsider, _, _) = await SeedAsync();
        var conversations = new List<ConversationView>();
        await using (var setup = fixture.CreateContext())
        {
            var writer = new ConversationStore(setup);
            for (var i = 0; i < 3; i++)
            {
                var conversation = (await writer.CreateAsync(actor.Id,
                    new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid()), Cancellation)).Conversation!;
                conversations.Add(conversation);
                for (var j = 0; j < 3; j++)
                    await writer.AppendAsync(actor.Id, workspace.Id, conversation.Id, Guid.NewGuid(), $"Message {j}", Cancellation);
            }
        }
        conversations.Sort((a, b) => a.Id.CompareTo(b.Id));
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);
        var store = new ConversationStore(db);
        var page = await store.ListAsync(actor.Id, workspace.Id, null, 1, Cancellation);
        Assert.Equal(conversations[0].Id, Assert.Single(page.Items!).Id);
        Assert.Equal(conversations[0].Id, page.NextAfter);
        AssertBounded();
        page = await store.ListAsync(actor.Id, workspace.Id, page.NextAfter, 1, Cancellation);
        Assert.Equal(conversations[1].Id, Assert.Single(page.Items!).Id);
        AssertBounded();
        page = await store.ListAsync(actor.Id, workspace.Id, page.NextAfter, 1, Cancellation);
        Assert.Equal(conversations[2].Id, Assert.Single(page.Items!).Id);
        Assert.Null(page.NextAfter);
        AssertBounded();
        var messages = await store.ListMessagesAsync(actor.Id, workspace.Id, conversations[0].Id, 1, 1, Cancellation);
        Assert.Equal(2, Assert.Single(messages.Messages!).Sequence);
        Assert.Equal(2, messages.NextAfterSequence);
        AssertBounded();
        var empty = await store.ListMessagesAsync(actor.Id, workspace.Id, conversations[0].Id, long.MaxValue, 1, Cancellation);
        Assert.Equal(ConversationResultStatus.Success, empty.Status);
        Assert.Empty(empty.Messages!);
        Assert.Null(empty.NextAfterSequence);
        AssertBounded();
        Assert.Equal(ConversationResultStatus.NotFound, (await store.ListAsync(outsider.Id, workspace.Id, null, 1, Cancellation)).Status);
        AssertBounded();
        Assert.Equal(ConversationResultStatus.NotFound, (await store.ListMessagesAsync(outsider.Id, workspace.Id, conversations[0].Id, 0, 1, Cancellation)).Status);
        AssertBounded();

        void AssertBounded()
        {
            var sql = Assert.Single(commands);
            Assert.True(sql.Contains("LIMIT") || sql.Contains("ROW_NUMBER() OVER") && sql.Contains(".row <="), "Page must be bounded within SQL.");
            Assert.Contains("WorkspaceMembers", sql);
            commands.Clear();
        }
    }

    private async Task<(Workspace Workspace, User Actor, Channel Channel, Contact Contact)> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Conversation persistence");
        var actor = new User();
        var channel = new Channel(workspace.Id, "Web");
        var contact = new Contact(workspace.Id, "Visitor", []);
        db.AddRange(workspace, actor, channel, contact, new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Cancellation);
        return (workspace, actor, channel, contact);
    }
}
