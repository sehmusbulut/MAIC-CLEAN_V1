using MAIC.Application.Channels;
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

public sealed class ConversationConcurrencyTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Overlapping_create_retries_commit_one_conversation_and_recheck_replay_authorization()
    {
        var seed = await SeedAsync();
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {seed.Workspace.Id} FOR UPDATE", timeout.Token);
        var firstName = "conversation-create-first-" + Guid.NewGuid();
        var secondName = "conversation-create-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var retry = NewConversation(seed, seed.Conversation.ClientRequestId);
        var first = new ConversationStore(firstDb).CreateAsync(seed.Agent.Id, seed.Conversation, timeout.Token);
        Task<ConversationResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Workspaces", timeout.Token);
            second = new ConversationStore(secondDb).CreateAsync(seed.Agent.Id, retry, timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await transaction.CommitAsync(timeout.Token);

            var created = await first;
            var replayed = await second;
            Assert.Equal(ConversationResultStatus.Success, created.Status);
            Assert.False(created.Replayed);
            Assert.Equal(ConversationResultStatus.Success, replayed.Status);
            Assert.True(replayed.Replayed);
            Assert.Equal(created.Conversation!.Id, replayed.Conversation!.Id);
            await using var verify = fixture.CreateContext();
            var persisted = Assert.Single(await verify.Conversations.AsNoTracking()
                .Where(c => c.WorkspaceId == seed.Workspace.Id).ToListAsync(Cancellation));
            Assert.Equal(seed.Conversation.Id, persisted.Id);
            Assert.False(await verify.Conversations.AnyAsync(c => c.Id == retry.Id, Cancellation));

            Assert.Equal(ChannelStatus.Success, (await new ChannelStore(verify).UpdateAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Channel.Id, seed.Channel.Name, false, Cancellation)).Status);
            var disabledReplay = await new ConversationStore(verify).CreateAsync(
                seed.Agent.Id, NewConversation(seed, seed.Conversation.ClientRequestId), Cancellation);
            Assert.Equal(ConversationResultStatus.Success, disabledReplay.Status);
            Assert.True(disabledReplay.Replayed);
            Assert.Equal(persisted.Id, disabledReplay.Conversation!.Id);
            Assert.Equal(MembershipStatus.Success, (await new MembershipStore(verify).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation)).Status);
            Assert.Equal(ConversationResultStatus.NotFound, (await new ConversationStore(verify).CreateAsync(
                seed.Agent.Id, NewConversation(seed, seed.Conversation.ClientRequestId), Cancellation)).Status);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (second is not null) await DrainAsync(second);
        }
    }

    [Fact]
    public async Task Overlapping_append_retries_preserve_one_message_and_conflicts_do_not_advance_sequence()
    {
        var seed = await SeedAsync(persistConversation: true);
        var messageKey = Guid.NewGuid();
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seed.Conversation.Id} FOR UPDATE", timeout.Token);
        var firstName = "message-first-" + Guid.NewGuid();
        var retryName = "message-retry-" + Guid.NewGuid();
        var conflictName = "message-conflict-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var retryDb = NamedContext(retryName);
        await using var conflictDb = NamedContext(conflictName);
        var first = AppendAsync(firstDb, seed, messageKey, "Original", timeout.Token);
        Task<ConversationResult>? retry = null;
        Task<ConversationResult>? conflict = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Conversations", timeout.Token);
            retry = AppendAsync(retryDb, seed, messageKey, "Original", timeout.Token);
            await ObserveLockAsync(retryName, retry, "Workspaces", timeout.Token, firstName);
            conflict = AppendAsync(conflictDb, seed, messageKey, "Changed", timeout.Token);
            // The retry owns the queued tuple lock while waiting on the first writer's transaction.
            await ObserveLockAsync(conflictName, conflict, "Workspaces", timeout.Token, retryName);
            await transaction.CommitAsync(timeout.Token);

            var created = await first;
            var replayed = await retry;
            Assert.Equal(ConversationResultStatus.Success, created.Status);
            Assert.False(created.Replayed);
            Assert.Equal(ConversationResultStatus.Success, replayed.Status);
            Assert.True(replayed.Replayed);
            Assert.Equal(created.Message!.Id, replayed.Message!.Id);
            Assert.Equal(ConversationResultStatus.Conflict, (await conflict).Status);
            await using var verify = fixture.CreateContext();
            var persisted = Assert.Single(await verify.Messages.AsNoTracking()
                .Where(m => m.ConversationId == seed.Conversation.Id).ToListAsync(Cancellation));
            Assert.Equal(created.Message.Id, persisted.Id);
            Assert.Equal("Original", persisted.Content);
            Assert.Equal(1, persisted.Sequence);
            Assert.Equal(1, (await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation)).LastMessageSequence);
            var next = await AppendAsync(verify, seed, Guid.NewGuid(), "Next", Cancellation);
            Assert.Equal(ConversationResultStatus.Success, next.Status);
            Assert.Equal(2, next.Message!.Sequence);

            Assert.Equal(ChannelStatus.Success, (await new ChannelStore(verify).UpdateAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Channel.Id, seed.Channel.Name, false, Cancellation)).Status);
            var disabledReplay = await AppendAsync(verify, seed, messageKey, "Original", Cancellation);
            Assert.Equal(ConversationResultStatus.Success, disabledReplay.Status);
            Assert.True(disabledReplay.Replayed);
            Assert.Equal(persisted.Id, disabledReplay.Message!.Id);
            Assert.Equal(MembershipStatus.Success, (await new MembershipStore(verify).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, Cancellation)).Status);
            Assert.Equal(ConversationResultStatus.NotFound,
                (await AppendAsync(verify, seed, messageKey, "Original", Cancellation)).Status);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(first);
            if (retry is not null) await DrainAsync(retry);
            if (conflict is not null) await DrainAsync(conflict);
        }
    }

    [Fact]
    public async Task Concurrent_distinct_appends_commit_in_sequence_and_cursor_reads_skip_no_message()
    {
        var seed = await SeedAsync(persistConversation: true);
        await using (var initialDb = fixture.CreateContext())
            Assert.Equal(ConversationResultStatus.Success,
                (await AppendAsync(initialDb, seed, Guid.NewGuid(), "Initial", Cancellation)).Status);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seed.Conversation.Id} FOR UPDATE", timeout.Token);
        var firstName = "sequence-first-" + Guid.NewGuid();
        var secondName = "sequence-second-" + Guid.NewGuid();
        await using var firstDb = NamedContext(firstName);
        await using var secondDb = NamedContext(secondName);
        var first = AppendAsync(firstDb, seed, Guid.NewGuid(), "Second", timeout.Token);
        Task<ConversationResult>? second = null;
        try
        {
            await ObserveLockAsync(firstName, first, "Conversations", timeout.Token);
            second = new ConversationStore(secondDb).AppendAsync(seed.Owner.Id, seed.Workspace.Id,
                seed.Conversation.Id, Guid.NewGuid(), "Third", timeout.Token);
            await ObserveLockAsync(secondName, second, "Workspaces", timeout.Token, firstName);
            await using var reader = fixture.CreateContext();
            var beforeCommit = await new ConversationStore(reader).ListMessagesAsync(seed.Agent.Id,
                seed.Workspace.Id, seed.Conversation.Id, 0, 1, timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, beforeCommit.Status);
            var visible = Assert.Single(beforeCommit.Messages!);
            Assert.Equal(1, visible.Sequence);
            Assert.Null(beforeCommit.NextAfterSequence);
            var uncommitted = await new ConversationStore(reader).ListMessagesAsync(seed.Agent.Id,
                seed.Workspace.Id, seed.Conversation.Id, visible.Sequence, 1, timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, uncommitted.Status);
            Assert.Empty(uncommitted.Messages!);
            await transaction.CommitAsync(timeout.Token);

            var firstResult = await first;
            var secondResult = await second;
            Assert.Equal(ConversationResultStatus.Success, firstResult.Status);
            Assert.Equal(ConversationResultStatus.Success, secondResult.Status);
            Assert.Equal(2, firstResult.Message!.Sequence);
            Assert.Equal(3, secondResult.Message!.Sequence);
            var secondPage = await new ConversationStore(reader).ListMessagesAsync(seed.Agent.Id,
                seed.Workspace.Id, seed.Conversation.Id, visible.Sequence, 1, timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, secondPage.Status);
            Assert.Equal(firstResult.Message.Id, Assert.Single(secondPage.Messages!).Id);
            Assert.Equal(2, secondPage.NextAfterSequence);
            var finalPage = await new ConversationStore(reader).ListMessagesAsync(seed.Agent.Id,
                seed.Workspace.Id, seed.Conversation.Id, secondPage.NextAfterSequence!.Value, 1, timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, finalPage.Status);
            Assert.Equal(secondResult.Message.Id, Assert.Single(finalPage.Messages!).Id);
            Assert.Null(finalPage.NextAfterSequence);
            Assert.Equal(new long[] { 1, 2, 3 }, await reader.Messages.AsNoTracking()
                .Where(m => m.ConversationId == seed.Conversation.Id).OrderBy(m => m.Sequence)
                .Select(m => m.Sequence).ToArrayAsync(Cancellation));
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
    public async Task Fresh_write_queued_behind_removal_rechecks_membership(bool append)
    {
        var seed = await SeedAsync(persistConversation: append);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {seed.Workspace.Id} AND \"UserId\" = {seed.Agent.Id} FOR UPDATE", timeout.Token);
        var removerName = "conversation-remove-" + Guid.NewGuid();
        var writerName = "conversation-write-" + Guid.NewGuid();
        await using var remover = NamedContext(removerName);
        await using var writer = NamedContext(writerName);
        var removal = new MembershipStore(remover).RemoveAsync(
            seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, timeout.Token);
        Task<ConversationResult>? mutation = null;
        try
        {
            await ObserveLockAsync(removerName, removal, "WorkspaceMembers", timeout.Token);
            mutation = FreshWriteAsync(writer, seed, append, timeout.Token);
            await ObserveLockAsync(writerName, mutation, "Workspaces", timeout.Token, removerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            Assert.Equal(ConversationResultStatus.NotFound, (await mutation).Status);
            await AssertNoWriteAsync(seed, append);
            await using var verify = fixture.CreateContext();
            Assert.False(await verify.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Agent.Id, Cancellation));
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
    public async Task Fresh_write_queued_behind_channel_disable_rechecks_enabled_state(bool append)
    {
        var seed = await SeedAsync(persistConversation: append);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM \"Channels\" WHERE \"Id\" = {seed.Channel.Id} FOR UPDATE", timeout.Token);
        var disablerName = "conversation-disable-" + Guid.NewGuid();
        var writerName = "conversation-after-disable-" + Guid.NewGuid();
        await using var disabler = NamedContext(disablerName);
        await using var writer = NamedContext(writerName);
        var disabling = new ChannelStore(disabler).UpdateAsync(
            seed.Owner.Id, seed.Workspace.Id, seed.Channel.Id, seed.Channel.Name, false, timeout.Token);
        Task<ConversationResult>? mutation = null;
        try
        {
            await ObserveLockAsync(disablerName, disabling, "Channels", timeout.Token);
            mutation = FreshWriteAsync(writer, seed, append, timeout.Token);
            await ObserveLockAsync(writerName, mutation, "Workspaces", timeout.Token, disablerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(ChannelStatus.Success, (await disabling).Status);
            Assert.Equal(ConversationResultStatus.Conflict, (await mutation).Status);
            await AssertNoWriteAsync(seed, append);
            await using var verify = fixture.CreateContext();
            Assert.False((await verify.Channels.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Channel.Id, Cancellation)).Enabled);
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(disabling);
            if (mutation is not null) await DrainAsync(mutation);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Write_retains_workspace_and_actor_locks_until_commit(bool append)
    {
        var seed = await SeedAsync(persistConversation: append);
        using var timeout = Timeout();
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        if (append)
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Conversations\" WHERE \"Id\" = {seed.Conversation.Id} FOR UPDATE", timeout.Token);
        else
            // Creating the conversation waits on the channel FK while retaining actor authorization.
            await blocker.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"Channels\" WHERE \"Id\" = {seed.Channel.Id} FOR UPDATE", timeout.Token);
        var writerName = "conversation-writer-" + Guid.NewGuid();
        var probeName = "conversation-member-probe-" + Guid.NewGuid();
        var removerName = "conversation-remover-" + Guid.NewGuid();
        await using var writer = NamedContext(writerName);
        await using var memberProbe = NamedContext(probeName);
        await using var remover = NamedContext(removerName);
        await using var probeTransaction = await memberProbe.Database.BeginTransactionAsync(timeout.Token);
        var mutation = append
            ? AppendAsync(writer, seed, Guid.NewGuid(), "Authorized until commit", timeout.Token)
            : new ConversationStore(writer).CreateAsync(seed.Agent.Id, seed.Conversation, timeout.Token);
        Task? probe = null;
        Task<MembershipResult>? removal = null;
        try
        {
            await ObserveLockAsync(writerName, mutation, "Conversations", timeout.Token);
            probe = memberProbe.Database.ExecuteSqlInterpolatedAsync(
                $"SELECT 1 FROM \"WorkspaceMembers\" WHERE \"WorkspaceId\" = {seed.Workspace.Id} AND \"UserId\" = {seed.Agent.Id} FOR UPDATE", timeout.Token);
            await ObserveLockAsync(probeName, probe, "WorkspaceMembers", timeout.Token, writerName);
            removal = new MembershipStore(remover).RemoveAsync(
                seed.Owner.Id, seed.Workspace.Id, seed.Agent.Id, timeout.Token);
            await ObserveLockAsync(removerName, removal, "Workspaces", timeout.Token, writerName);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(ConversationResultStatus.Success, (await mutation).Status);
            await probe;
            await probeTransaction.CommitAsync(timeout.Token);
            Assert.Equal(MembershipStatus.Success, (await removal).Status);
            await using var verify = fixture.CreateContext();
            var persistedConversation = await verify.Conversations.AsNoTracking()
                .SingleAsync(c => c.Id == seed.Conversation.Id, Cancellation);
            Assert.Equal(seed.Agent.Id, persistedConversation.CreatedByUserId);
            var messages = await verify.Messages.AsNoTracking()
                .Where(m => m.ConversationId == seed.Conversation.Id).ToListAsync(Cancellation);
            if (append)
            {
                var persisted = Assert.Single(messages);
                Assert.Equal("Authorized until commit", persisted.Content);
                Assert.Equal(seed.Agent.Id, persisted.SenderUserId);
                Assert.Equal(1, persisted.Sequence);
            }
            else Assert.Empty(messages);
            Assert.False(await verify.WorkspaceMembers.AnyAsync(m =>
                m.WorkspaceId == seed.Workspace.Id && m.UserId == seed.Agent.Id, Cancellation));
        }
        finally
        {
            await timeout.CancelAsync();
            await DrainAsync(mutation);
            if (probe is not null) await DrainAsync(probe);
            if (removal is not null) await DrainAsync(removal);
        }
    }

    private async Task AssertNoWriteAsync(Seed seed, bool existingConversation)
    {
        await using var verify = fixture.CreateContext();
        Assert.Empty(await verify.Messages.AsNoTracking()
            .Where(m => m.WorkspaceId == seed.Workspace.Id).ToListAsync(Cancellation));
        var conversations = await verify.Conversations.AsNoTracking()
            .Where(c => c.WorkspaceId == seed.Workspace.Id).ToListAsync(Cancellation);
        if (existingConversation)
        {
            var persisted = Assert.Single(conversations);
            Assert.Equal(seed.Conversation.Id, persisted.Id);
            Assert.Equal(0, persisted.LastMessageSequence);
        }
        else Assert.Empty(conversations);
    }

    private static Task<ConversationResult> FreshWriteAsync(
        MaicDbContext db, Seed seed, bool append, CancellationToken ct) => append
        ? AppendAsync(db, seed, Guid.NewGuid(), "Must not commit", ct)
        : new ConversationStore(db).CreateAsync(seed.Agent.Id, seed.Conversation, ct);

    private static Task<ConversationResult> AppendAsync(
        MaicDbContext db, Seed seed, Guid messageKey, string content, CancellationToken ct) =>
        new ConversationStore(db).AppendAsync(seed.Agent.Id, seed.Workspace.Id,
            seed.Conversation.Id, messageKey, content, ct);

    private static Conversation NewConversation(Seed seed, Guid requestKey) => new(
        seed.Workspace.Id, seed.Channel.Id, seed.Contact.Id, seed.Agent.Id, requestKey);

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

    private async Task<Seed> SeedAsync(bool persistConversation = false)
    {
        await using var db = fixture.CreateContext();
        var owner = new User();
        var agent = new User();
        var workspace = new Workspace("Conversation races");
        var channel = new Channel(workspace.Id, "Chat");
        var contact = new Contact(workspace.Id, "Visitor", [new("email", "visitor@example.com")]);
        var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, agent.Id, Guid.NewGuid());
        db.Users.AddRange(owner, agent);
        db.Workspaces.Add(workspace);
        db.WorkspaceMembers.AddRange(new WorkspaceMember(workspace.Id, owner.Id, WorkspaceRole.OWNER),
            new WorkspaceMember(workspace.Id, agent.Id, WorkspaceRole.AGENT));
        db.Channels.Add(channel);
        db.Contacts.Add(contact);
        if (persistConversation) db.Conversations.Add(conversation);
        await db.SaveChangesAsync(Cancellation);
        return new(workspace, owner, agent, channel, contact, conversation);
    }

    private sealed record Seed(Workspace Workspace, User Owner, User Agent, Channel Channel,
        Contact Contact, Conversation Conversation);
}
