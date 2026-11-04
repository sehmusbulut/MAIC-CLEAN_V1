using System.Security.Cryptography;
using MAIC.Application.Channels;
using MAIC.Application.Conversations;
using MAIC.Application.Widget;
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

public sealed class WidgetPersistenceTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private static string Digest() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }
    private static WidgetSessionStore Store(MaicDbContext db, TimeProvider? clock = null, WidgetLimits? limits = null) =>
        new(db, clock ?? TimeProvider.System, limits ?? new());

    [Fact]
    public async Task Session_is_atomic_idempotent_scoped_and_visible_to_authorized_members()
    {
        var (workspace, actor, channel) = await Seed();
        var (_, _, other) = await Seed();
        var digest = Digest();
        await using var db = fixture.CreateContext();
        var store = Store(db);
        var first = await store.StartAsync(channel.PublishableToken, digest, Ct);
        Assert.Equal(WidgetStatus.Success, first.Status);
        Assert.False(first.Replayed);
        var again = await store.StartAsync(channel.PublishableToken, digest, Ct);
        Assert.True(again.Replayed);
        Assert.Equal(first.Session, again.Session);
        var guest = Assert.Single(await db.GuestSessions.Where(s => s.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Single(await db.Contacts.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Ct));
        var conversation = Assert.Single(await db.Conversations.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Null(conversation.CreatedByUserId);
        Assert.Equal(guest.Id, conversation.CreatedByGuestSessionId);
        Assert.Equal(TimeSpan.FromHours(24), guest.ExpiresAt - guest.CreatedAt);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.GetAsync(other.PublishableToken, digest, Ct)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.StartAsync(other.PublishableToken, digest, Ct)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.GetAsync(channel.PublishableToken, Digest(), Ct)).Status);
        var sameWorkspace = new Channel(workspace.Id, "Sibling");
        db.Add(sameWorkspace);
        await db.SaveChangesAsync(Ct);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.StartAsync(sameWorkspace.PublishableToken, digest, Ct)).Status);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.ListMessagesAsync(sameWorkspace.PublishableToken, digest, 0, 10, Ct)).Status);
        var member = new ConversationStore(db);
        Assert.Equal(ConversationResultStatus.Success, (await member.GetAsync(actor.Id, workspace.Id, conversation.Id, Ct)).Status);
        var sent = await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Guest text", Ct);
        var history = await member.ListMessagesAsync(actor.Id, workspace.Id, conversation.Id, 0, 10, Ct);
        var item = Assert.Single(history.Messages!);
        Assert.Equal("GUEST", item.SenderKind);
        Assert.Null(item.SenderUserId);
        Assert.Null(item.AiExecutionId);
        Assert.Equal(sent.Message!.Id, item.Id);
        Assert.False(db.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task Revocation_expiry_and_generation_are_tombstones_and_replays_reauthorize()
    {
        var (workspace, actor, channel) = await Seed();
        var clock = new Clock();
        await using var db = fixture.CreateContext();
        var store = Store(db, clock);
        var revoked = Digest();
        await store.StartAsync(channel.PublishableToken, revoked, Ct);
        Assert.Equal(WidgetStatus.Success, (await store.RevokeAsync(channel.PublishableToken, revoked, Ct)).Status);
        Assert.True((await store.RevokeAsync(channel.PublishableToken, revoked, Ct)).Replayed);
        await AssertInvalid(revoked);
        var expired = Digest();
        var session = await store.StartAsync(channel.PublishableToken, expired, Ct);
        clock.Now = session.Session!.ExpiresAt;
        await AssertInvalid(expired);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.RevokeAsync(channel.PublishableToken, expired, Ct)).Status);
        var disabled = Digest();
        await store.StartAsync(channel.PublishableToken, disabled, Ct);
        var key = Guid.NewGuid();
        await store.AppendAsync(channel.PublishableToken, disabled, key, "Before", Ct);
        var channels = new ChannelStore(db);
        await channels.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, false, Ct);
        Assert.Equal(WidgetStatus.NotFound, (await store.GetAsync(channel.PublishableToken, disabled, Ct)).Status);
        Assert.Equal(WidgetStatus.NotFound, (await store.StartAsync(channel.PublishableToken, disabled, Ct)).Status);
        await channels.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, true, Ct);
        await AssertInvalid(disabled);
        Assert.Equal(WidgetStatus.Unauthenticated, (await store.AppendAsync(channel.PublishableToken, disabled, key, "Before", Ct)).Status);
        Assert.Equal(WidgetStatus.Success, (await store.StartAsync(channel.PublishableToken, Digest(), Ct)).Status);
        Assert.Equal(4, await db.GuestSessions.CountAsync(s => s.WorkspaceId == workspace.Id, Ct));

        async Task AssertInvalid(string hash)
        {
            Assert.Equal(WidgetStatus.Unauthenticated, (await store.GetAsync(channel.PublishableToken, hash, Ct)).Status);
            Assert.Equal(WidgetStatus.Unauthenticated, (await store.StartAsync(channel.PublishableToken, hash, Ct)).Status);
            Assert.Equal(WidgetStatus.Unauthenticated, (await store.ListMessagesAsync(channel.PublishableToken, hash, 0, 10, Ct)).Status);
            Assert.Equal(WidgetStatus.Unauthenticated, (await store.AppendAsync(channel.PublishableToken, hash, Guid.NewGuid(), "Rejected", Ct)).Status);
        }
    }

    [Fact]
    public async Task Safe_history_is_bounded_in_one_SQL_snapshot_and_retries_survive_resolution()
    {
        var (workspace, actor, channel) = await Seed();
        var digest = Digest();
        Guid conversationId;
        var key = Guid.NewGuid();
        await using (var setup = fixture.CreateContext())
        {
            var store = Store(setup);
            conversationId = (await store.StartAsync(channel.PublishableToken, digest, Ct)).Session!.ConversationId;
            await store.AppendAsync(channel.PublishableToken, digest, key, "Guest", Ct);
            await new ConversationStore(setup).AppendAsync(actor.Id, workspace.Id, conversationId, Guid.NewGuid(), "Agent", Ct);
            await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Guest again", Ct);
            await new ConversationStore(setup).ResolveAsync(actor.Id, workspace.Id, conversationId, Ct);
            Assert.True((await store.AppendAsync(channel.PublishableToken, digest, key, "Guest", Ct)).Replayed);
            Assert.Equal(WidgetStatus.Conflict, (await store.AppendAsync(channel.PublishableToken, digest, key, "Different", Ct)).Status);
            Assert.Equal(WidgetStatus.Conflict, (await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Fresh", Ct)).Status);
        }
        var commands = new List<string>();
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(fixture.ConnectionString)
            .LogTo(commands.Add, new[] { DbLoggerCategory.Database.Command.Name }, LogLevel.Information).Options);
        var reader = Store(db);
        var first = await reader.ListMessagesAsync(channel.PublishableToken, digest, 0, 1, Ct);
        Assert.Equal(key, Assert.Single(first.Messages!).ClientMessageId);
        Assert.Equal(1, first.NextAfterSequence);
        Assert.Equal("RESOLVED", first.ConversationStatus);
        AssertSql();
        var second = await reader.ListMessagesAsync(channel.PublishableToken, digest, 1, 1, Ct);
        Assert.Null(Assert.Single(second.Messages!).ClientMessageId);
        Assert.Equal("MEMBER", second.Messages![0].SenderKind);
        AssertSql();
        var third = await reader.ListMessagesAsync(channel.PublishableToken, digest, 2, 1, Ct);
        Assert.Single(third.Messages!);
        Assert.Null(third.NextAfterSequence);
        AssertSql();
        Assert.Equal(WidgetStatus.Unauthenticated, (await reader.ListMessagesAsync(channel.PublishableToken, Digest(), 0, 1, Ct)).Status);
        AssertSql();

        void AssertSql()
        {
            var sql = Assert.Single(commands);
            Assert.Contains("GuestSessions", sql);
            Assert.Contains("Channels", sql);
            Assert.True(sql.Contains("LIMIT") || sql.Contains("ROW_NUMBER() OVER"));
            commands.Clear();
        }
    }

    [Fact]
    public async Task Durable_quotas_count_only_new_sessions_and_new_guest_messages()
    {
        var (_, _, channel) = await Seed();
        var clock = new Clock();
        await using var db = fixture.CreateContext();
        var store = Store(db, clock, new() { SessionsPerChannel = 2, SessionsPerChannelDay = 1, GuestMessagesPerConversation = 1 });
        var digest = Digest();
        await store.StartAsync(channel.PublishableToken, digest, Ct);
        Assert.True((await store.StartAsync(channel.PublishableToken, digest, Ct)).Replayed);
        Assert.Equal(WidgetStatus.Limited, (await store.StartAsync(channel.PublishableToken, Digest(), Ct)).Status);
        var key = Guid.NewGuid();
        Assert.Equal(WidgetStatus.Success, (await store.AppendAsync(channel.PublishableToken, digest, key, "One", Ct)).Status);
        Assert.True((await store.AppendAsync(channel.PublishableToken, digest, key, "One", Ct)).Replayed);
        Assert.Equal(WidgetStatus.Limited, (await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Two", Ct)).Status);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal(WidgetStatus.Success, (await store.StartAsync(channel.PublishableToken, Digest(), Ct)).Status);
        clock.Now = clock.Now.AddDays(2);
        Assert.Equal(WidgetStatus.Limited, (await store.StartAsync(channel.PublishableToken, Digest(), Ct)).Status);
    }

    [Fact]
    public async Task Database_provenance_rejects_cross_scope_and_wrong_conversation_owner()
    {
        var (workspace, actor, channel) = await Seed();
        var (_, _, other) = await Seed();
        await using var db = fixture.CreateContext();
        var store = Store(db);
        var digest = Digest();
        var first = (await store.StartAsync(channel.PublishableToken, digest, Ct)).Session!;
        var sibling = (await store.StartAsync(channel.PublishableToken, Digest(), Ct)).Session!;
        var foreign = (await store.StartAsync(other.PublishableToken, Digest(), Ct)).Session!;
        var firstGuest = await db.GuestSessions.SingleAsync(s => s.CredentialHash == digest, Ct);
        var siblingConversation = await db.Conversations.SingleAsync(c => c.Id == sibling.ConversationId, Ct);
        var foreignConversation = await db.Conversations.SingleAsync(c => c.Id == foreign.ConversationId, Ct);
        var message = (await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Original", Ct)).Message!;
        await Reject($"UPDATE \"Messages\" SET \"SenderGuestSessionId\" = {siblingConversation.CreatedByGuestSessionId} WHERE \"Id\" = {message.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await Reject($"UPDATE \"GuestSessions\" SET \"ChannelId\" = {other.Id} WHERE \"Id\" = {firstGuest.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await Reject($"UPDATE \"Conversations\" SET \"ContactId\" = {siblingConversation.ContactId} WHERE \"Id\" = {first.ConversationId}", PostgresErrorCodes.ForeignKeyViolation);
        await Reject($"UPDATE \"Conversations\" SET \"CreatedByUserId\" = {actor.Id} WHERE \"Id\" = {first.ConversationId}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"Messages\" SET \"SenderUserId\" = {actor.Id} WHERE \"Id\" = {message.Id}", PostgresErrorCodes.CheckViolation);
        await Reject($"UPDATE \"Messages\" SET \"SenderGuestSessionId\" = {foreignConversation.CreatedByGuestSessionId} WHERE \"Id\" = {message.Id}", PostgresErrorCodes.ForeignKeyViolation);
        await Reject($"DELETE FROM \"GuestSessions\" WHERE \"Id\" = {firstGuest.Id}", PostgresErrorCodes.ForeignKeyViolation);
        Assert.Equal(workspace.Id, firstGuest.WorkspaceId);

        async Task Reject(FormattableString sql, string state)
        {
            var error = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(sql, Ct));
            Assert.Equal(state, error.SqlState);
        }
    }

    [Fact]
    public async Task Failed_insert_rolls_back_sequence_and_reused_context_recovers()
    {
        var (_, _, channel) = await Seed();
        var digest = Digest();
        await using var db = fixture.CreateContext();
        var store = Store(db);
        var session = (await store.StartAsync(channel.PublishableToken, digest, Ct)).Session!;
        await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "First", Ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastMessageSequence\" = {0L} WHERE \"Id\" = {session.ConversationId}", Ct);
        Assert.Equal(WidgetStatus.Conflict, (await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Collision", Ct)).Status);
        Assert.Equal(1, await db.Messages.CountAsync(m => m.ConversationId == session.ConversationId, Ct));
        Assert.DoesNotContain(db.ChangeTracker.Entries(), e => e.State is EntityState.Added or EntityState.Modified);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Conversations\" SET \"LastMessageSequence\" = {1L} WHERE \"Id\" = {session.ConversationId}", Ct);
        Assert.Equal(2, (await store.AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Recovered", Ct)).Message!.Sequence);
    }

    [Fact]
    public async Task Concurrent_starts_and_messages_commit_once_and_cross_workspace_digest_race_has_no_orphans()
    {
        var (workspace, _, channel) = await Seed();
        var digest = Digest();
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(async _ =>
        {
            await using var db = fixture.CreateContext();
            return await Store(db).StartAsync(channel.PublishableToken, digest, Ct);
        }));
        Assert.All(starts, r => Assert.Equal(WidgetStatus.Success, r.Status));
        Assert.Single(starts, r => !r.Replayed);
        Assert.Single(starts.Select(r => r.Session!.ConversationId).Distinct());
        var key = Guid.NewGuid();
        var messages = await Task.WhenAll(new[] { "First", "First", "Second", "Second" }.Select(async text =>
        {
            await using var db = fixture.CreateContext();
            return await Store(db).AppendAsync(channel.PublishableToken, digest, key, text, Ct);
        }));
        Assert.Equal(2, messages.Count(r => r.Status == WidgetStatus.Success));
        Assert.Equal(2, messages.Count(r => r.Status == WidgetStatus.Conflict));
        Assert.Single(messages, r => r.Status == WidgetStatus.Success && !r.Replayed);
        var (otherWorkspace, _, other) = await Seed();
        var shared = Digest();
        var raced = await Task.WhenAll(new[] { channel, other }.Select(async target =>
        {
            await using var db = fixture.CreateContext();
            return await Store(db).StartAsync(target.PublishableToken, shared, Ct);
        }));
        Assert.Single(raced, r => r.Status == WidgetStatus.Success);
        Assert.Single(raced, r => r.Status == WidgetStatus.Unauthenticated);
        await using var verify = fixture.CreateContext();
        Assert.Equal(2, await verify.GuestSessions.CountAsync(s => s.WorkspaceId == workspace.Id || s.WorkspaceId == otherWorkspace.Id, Ct));
        Assert.Equal(2, await verify.Contacts.CountAsync(s => s.WorkspaceId == workspace.Id || s.WorkspaceId == otherWorkspace.Id, Ct));
        Assert.Equal(2, await verify.Conversations.CountAsync(s => s.WorkspaceId == workspace.Id || s.WorkspaceId == otherWorkspace.Id, Ct));
    }

    [Theory]
    [InlineData("expiry")]
    [InlineData("revoke")]
    [InlineData("disable")]
    public async Task Waiting_replay_refreshes_authorization_after_workspace_lock(string change)
    {
        var (workspace, _, channel) = await Seed();
        var clock = new Clock();
        var digest = Digest();
        var key = Guid.NewGuid();
        var applicationName = "widget-wait-" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { ApplicationName = applicationName };
        await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection.ConnectionString).Options);
        var store = Store(db, clock);
        var session = (await store.StartAsync(channel.PublishableToken, digest, Ct)).Session!;
        await store.AppendAsync(channel.PublishableToken, digest, key, "Replay", Ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        await using var blocker = fixture.CreateContext();
        await using var transaction = await blocker.Database.BeginTransactionAsync(timeout.Token);
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM \"Workspaces\" WHERE \"Id\" = {workspace.Id} FOR UPDATE", timeout.Token);
        var pending = store.AppendAsync(channel.PublishableToken, digest, key, "Replay", timeout.Token);
        try
        {
            await using var observer = new NpgsqlConnection(fixture.ConnectionString);
            await observer.OpenAsync(timeout.Token);
            while (true)
            {
                Assert.False(pending.IsCompleted, "Writer must wait for the workspace lock.");
                await using var query = new NpgsqlCommand("SELECT EXISTS (SELECT 1 FROM pg_stat_activity WHERE application_name = @name AND wait_event_type = 'Lock')", observer);
                query.Parameters.AddWithValue("name", applicationName);
                if (await query.ExecuteScalarAsync(timeout.Token) is true) break;
                await Task.Delay(20, timeout.Token);
            }
            if (change == "expiry") clock.Now = session.ExpiresAt;
            else if (change == "revoke")
                await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"GuestSessions\" SET \"RevokedAt\" = {clock.Now.AddSeconds(1)} WHERE \"CredentialHash\" = {digest}", timeout.Token);
            else
                await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE \"Channels\" SET \"Enabled\" = FALSE, \"GuestAccessGeneration\" = \"GuestAccessGeneration\" + 1 WHERE \"Id\" = {channel.Id}", timeout.Token);
            await transaction.CommitAsync(timeout.Token);
            Assert.Equal(change == "disable" ? WidgetStatus.NotFound : WidgetStatus.Unauthenticated, (await pending).Status);
        }
        finally
        {
            await timeout.CancelAsync();
            try { await pending; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public async Task Channel_disable_refreshes_generation_even_when_context_previously_tracked_channel()
    {
        var (workspace, actor, channel) = await Seed();
        await using var stale = fixture.CreateContext();
        await stale.Channels.SingleAsync(c => c.Id == channel.Id, Ct);
        await using (var fresh = fixture.CreateContext())
        {
            var channels = new ChannelStore(fresh);
            await channels.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, false, Ct);
            await channels.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, true, Ct);
        }
        var digest = Digest();
        await Store(stale).StartAsync(channel.PublishableToken, digest, Ct);
        var writer = new ChannelStore(stale);
        await writer.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, false, Ct);
        await writer.UpdateAsync(actor.Id, workspace.Id, channel.Id, null, true, Ct);
        Assert.Equal(WidgetStatus.Unauthenticated, (await Store(stale).GetAsync(channel.PublishableToken, digest, Ct)).Status);
        Assert.Equal(2, (await stale.Channels.AsNoTracking().SingleAsync(c => c.Id == channel.Id, Ct)).GuestAccessGeneration);
    }

    [Fact]
    public async Task Schema_upgrade_preserves_member_history_and_downgrade_refuses_guest_history_atomically()
    {
        const string previous = "20260927235033_ConversationAiExecution";
        var database = "maic_widget_" + Guid.NewGuid().ToString("N");
        var connection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }.ConnectionString;
        await Admin($"CREATE DATABASE \"{database}\"");
        try
        {
            await using var db = new MaicDbContext(new DbContextOptionsBuilder<MaicDbContext>().UseNpgsql(connection).Options);
            await db.Database.MigrateAsync(Ct);
            var workspace = new Workspace("Old member history");
            var actor = new User();
            var channel = new Channel(workspace.Id, "Web");
            var contact = new Contact(workspace.Id, "Existing contact", []);
            var conversation = new Conversation(workspace.Id, channel.Id, contact.Id, actor.Id, Guid.NewGuid());
            var message = new Message(workspace.Id, conversation.Id, actor.Id, Guid.NewGuid(), conversation.AdvanceMessageSequence(), "Existing member text");
            db.AddRange(workspace, actor, channel, contact, conversation, message);
            await db.SaveChangesAsync(Ct);
            db.ChangeTracker.Clear();
            await db.GetService<IMigrator>().MigrateAsync(previous, Ct);
            Assert.Equal(actor.Id, await db.Database.SqlQuery<Guid>($"SELECT \"CreatedByUserId\" AS \"Value\" FROM \"Conversations\" WHERE \"Id\" = {conversation.Id}").SingleAsync(Ct));
            await db.Database.MigrateAsync(Ct);
            Assert.Equal("Existing member text", (await db.Messages.SingleAsync(m => m.Id == message.Id, Ct)).Content);
            Assert.Equal(actor.Id, (await db.Conversations.SingleAsync(c => c.Id == conversation.Id, Ct)).CreatedByUserId);
            var digest = Digest();
            var guest = (await Store(db).StartAsync(channel.PublishableToken, digest, Ct)).Session!;
            await Store(db).AppendAsync(channel.PublishableToken, digest, Guid.NewGuid(), "Durable guest history", Ct);
            db.ChangeTracker.Clear();
            var failure = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>().MigrateAsync(previous, Ct));
            Assert.Equal(PostgresErrorCodes.RaiseException, failure.SqlState);
            Assert.Contains("Cannot downgrade", failure.MessageText);
            Assert.True(await db.GuestSessions.AnyAsync(Ct));
            Assert.Equal("Durable guest history", (await db.Messages.SingleAsync(m => m.ConversationId == guest.ConversationId, Ct)).Content);
            Assert.False(db.Database.HasPendingModelChanges());
        }
        finally
        {
            await using (var pooled = new NpgsqlConnection(connection)) NpgsqlConnection.ClearPool(pooled);
            await Admin($"DROP DATABASE IF EXISTS \"{database}\" WITH (FORCE)");
        }

        async Task Admin(string sql)
        {
            await using var admin = new NpgsqlConnection(fixture.ConnectionString);
            await admin.OpenAsync(CancellationToken.None);
            await using var command = new NpgsqlCommand(sql, admin);
            await command.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    private async Task<(Workspace Workspace, User Actor, Channel Channel)> Seed()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Widget tests");
        var actor = new User();
        var channel = new Channel(workspace.Id, "Web");
        db.AddRange(workspace, actor, channel, new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Ct);
        return (workspace, actor, channel);
    }
}
