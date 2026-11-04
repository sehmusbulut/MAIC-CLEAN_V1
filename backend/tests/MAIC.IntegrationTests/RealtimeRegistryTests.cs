using System.Security.Claims;
using MAIC.API.Realtime;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class RealtimeRegistryTests
{
    private static readonly Guid Workspace = Guid.NewGuid();
    private static readonly Guid Conversation = Guid.NewGuid();

    [Fact]
    public void Global_limit_counts_idle_connections_and_releases_removed_capacity()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions { GlobalConnectionLimit = 2 }, time);
        Assert.True(registry.TryRegister("first", Session(), () => { }));
        Assert.True(registry.TryRegister("second", Session(), () => { }));
        Assert.False(registry.TryRegister("third", Session(), () => { }));
        Assert.False(registry.TryRegister("first", Session(), () => { }));

        registry.Remove("first");
        Assert.True(registry.TryRegister("third", Session(), () => { }));
        Assert.Null(registry.Get("third")!.WorkspaceId);
    }

    [Fact]
    public void User_limit_counts_idle_connections_across_distinct_sessions()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions { UserConnectionLimit = 2 }, time);
        var user = Guid.NewGuid();
        Assert.True(registry.TryRegister("first", Session(user), () => { }));
        Assert.True(registry.TryRegister("second", Session(user), () => { }));
        Assert.False(registry.TryRegister("third", Session(user), () => { }));
        Assert.True(registry.TryRegister("other-user", Session(), () => { }));

        registry.Remove("first");
        Assert.True(registry.TryRegister("third", Session(user), () => { }));
    }

    [Fact]
    public void Workspace_limit_covers_all_conversations_and_releases_removed_capacity()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions { WorkspaceConnectionLimit = 1 }, time);
        Subscribe(registry, "first", Session());
        Assert.True(registry.TryRegister("second", Session(), () => { }));
        Assert.True(registry.TryBeginWatch("second"));
        Assert.False(registry.TrySubscribe("second", Workspace, Guid.NewGuid()));
        Subscribe(registry, "other-workspace", Session(), Guid.NewGuid(), Conversation);

        registry.Remove("first");
        Assert.True(registry.TryRegister("replacement", Session(), () => { }));
        Assert.True(registry.TryBeginWatch("replacement"));
        Assert.True(registry.TrySubscribe("replacement", Workspace, Conversation));
    }

    [Fact]
    public void Watch_can_only_be_claimed_once_and_subscription_scope_is_immutable()
    {
        using var registry = new RealtimeRegistry(new RealtimeOptions(), new ManualRealtimeTimeProvider());
        Assert.False(registry.TryBeginWatch("unknown"));
        Assert.True(registry.TryRegister("connection", Session(), () => { }));
        Assert.False(registry.TrySubscribe("connection", Workspace, Conversation));
        Assert.True(registry.TryBeginWatch("connection"));
        Assert.False(registry.TryBeginWatch("connection"));
        Assert.True(registry.TrySubscribe("connection", Workspace, Conversation));
        Assert.False(registry.TrySubscribe("connection", Workspace, Guid.NewGuid()));
        Assert.False(registry.TrySubscribe("connection", Guid.NewGuid(), Conversation));

        var connection = Assert.IsType<RealtimeConnection>(registry.Get("connection"));
        Assert.Equal(Workspace, connection.WorkspaceId);
        Assert.Equal(Conversation, connection.ConversationId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unsubscribed_connection_is_aborted_at_watch_deadline_even_if_watch_was_claimed(bool claimWatch)
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var aborts = 0;
        Assert.True(registry.TryRegister("connection", Session(), () => aborts++));
        if (claimWatch) Assert.True(registry.TryBeginWatch("connection"));

        time.Advance(TimeSpan.FromMilliseconds(9999));
        Assert.NotNull(registry.Get("connection"));
        Assert.Equal(0, aborts);
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Null(registry.Get("connection"));
        Assert.Equal(1, aborts);
        Assert.Equal(0, time.UndisposedTimerCount);
        Assert.False(registry.TrySubscribe("connection", Workspace, Conversation));
        time.Advance(TimeSpan.FromMinutes(5));
        Assert.Equal(1, aborts);
    }

    [Fact]
    public void Active_connection_lifetime_is_measured_from_admission_and_cannot_be_extended_by_mutation()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var aborts = 0;
        Assert.True(registry.TryRegister("connection", Session(), () => aborts++));
        time.Advance(TimeSpan.FromSeconds(9));
        Assert.True(registry.TryBeginWatch("connection"));
        Assert.True(registry.TrySubscribe("connection", Workspace, Conversation));
        Assert.Equal(1, time.UndisposedTimerCount);

        time.Advance(TimeSpan.FromSeconds(290));
        Assert.True(registry.Mutate("connection", true));
        Assert.NotNull(registry.Get("connection"));
        Assert.Equal(0, aborts);
        time.Advance(TimeSpan.FromSeconds(1));

        Assert.Null(registry.Get("connection"));
        Assert.Equal(1, aborts);
        Assert.Equal(0, time.UndisposedTimerCount);
    }

    [Fact]
    public void Removing_and_disposing_connections_releases_all_timers_and_aborts_at_most_once()
    {
        var time = new ManualRealtimeTimeProvider();
        var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var removedAborts = 0;
        var remainingAborts = 0;
        Assert.True(registry.TryRegister("removed", Session(), () => removedAborts++));
        Assert.True(registry.TryRegister("remaining", Session(), () => remainingAborts++));
        registry.Remove("removed");
        Assert.Equal(2, time.UndisposedTimerCount);
        registry.Dispose();
        registry.Dispose();
        time.Advance(TimeSpan.FromMinutes(6));

        Assert.Equal(0, removedAborts);
        Assert.Equal(1, remainingAborts);
        Assert.Equal(0, time.UndisposedTimerCount);
    }

    [Fact]
    public void Typing_expires_after_five_seconds_and_presence_after_thirty_seconds()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var session = Session();
        Subscribe(registry, "connection", session);
        Assert.True(registry.Mutate("connection", true));
        time.Advance(TimeSpan.FromMilliseconds(4999));
        Assert.True(Assert.Single(registry.Presence(Workspace, Conversation, ["connection"], [session.UserId])).IsTyping);

        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.False(Assert.Single(registry.Presence(Workspace, Conversation, ["connection"], [session.UserId])).IsTyping);
        time.Advance(TimeSpan.FromMilliseconds(24999));
        Assert.Single(registry.Candidates(Workspace, Conversation));
        time.Advance(TimeSpan.FromMilliseconds(1));

        Assert.Empty(registry.Candidates(Workspace, Conversation));
        Assert.Empty(registry.Presence(Workspace, Conversation, ["connection"], [session.UserId]));
        Assert.NotNull(registry.Get("connection"));
        Assert.True(registry.Mutate("connection", null));
        Assert.False(Assert.Single(registry.Presence(Workspace, Conversation, ["connection"], [session.UserId])).IsTyping);
    }

    [Fact]
    public void Multiple_connections_aggregate_per_user_and_disconnect_keeps_other_presence()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var user = Guid.NewGuid();
        Subscribe(registry, "quiet", Session(user));
        Subscribe(registry, "typing", Session(user));
        Assert.True(registry.Mutate("typing", true));
        var present = Assert.Single(registry.Presence(Workspace, Conversation, ["quiet", "typing"], [user]));
        Assert.Equal(user, present.UserId);
        Assert.True(present.IsTyping);

        registry.Remove("typing");
        Assert.False(Assert.Single(registry.Presence(Workspace, Conversation, ["quiet", "typing"], [user])).IsTyping);
        registry.Remove("quiet");
        Assert.Empty(registry.Presence(Workspace, Conversation, ["quiet", "typing"], [user]));
    }

    [Fact]
    public void Candidates_presence_and_pruning_are_scoped_to_checked_authorized_connections()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var allowed = Session();
        var revoked = Session();
        var uncheckedSession = Session();
        var revokedAborts = 0;
        Subscribe(registry, "allowed", allowed);
        Subscribe(registry, "unchecked", uncheckedSession);
        Subscribe(registry, "revoked", revoked, abort: () => revokedAborts++);
        Subscribe(registry, "other-workspace", Session(allowed.UserId), Guid.NewGuid(), Conversation);
        Subscribe(registry, "other-conversation", Session(revoked.UserId), Workspace, Guid.NewGuid());
        Assert.True(registry.TryRegister("idle", Session(allowed.UserId), () => { }));
        var candidates = registry.Candidates(Workspace, Conversation);
        Assert.Equal(new[] { "allowed", "revoked", "unchecked" }, candidates.Select(candidate => candidate.ConnectionId).Order().ToArray());

        var presence = registry.Presence(Workspace, Conversation,
            ["allowed", "revoked", "other-workspace", "other-conversation", "idle"], [allowed.UserId, uncheckedSession.UserId]);
        Assert.Equal(allowed.UserId, Assert.Single(presence).UserId);
        registry.PruneUnauthorized(candidates, [allowed.UserId, uncheckedSession.UserId]);

        Assert.Null(registry.Get("revoked"));
        Assert.Equal(1, revokedAborts);
        Assert.NotNull(registry.Get("allowed"));
        Assert.NotNull(registry.Get("unchecked"));
        Assert.NotNull(registry.Get("other-workspace"));
        Assert.NotNull(registry.Get("other-conversation"));
        Assert.NotNull(registry.Get("idle"));
    }

    [Fact]
    public void Mutation_rate_is_shared_across_user_connections_and_cleared_after_last_disconnect()
    {
        var time = new ManualRealtimeTimeProvider();
        using var registry = new RealtimeRegistry(new RealtimeOptions(), time);
        var user = Guid.NewGuid();
        Assert.False(registry.TryConsumeMutation("unknown"));
        Assert.True(registry.TryRegister("idle", Session(), () => { }));
        Assert.False(registry.TryConsumeMutation("idle"));
        Subscribe(registry, "first", Session(user));
        Subscribe(registry, "second", Session(user), Guid.NewGuid(), Guid.NewGuid());
        Subscribe(registry, "independent", Session());
        Assert.True(registry.TryConsumeMutation("first"));
        Assert.False(registry.TryConsumeMutation("second"));
        Assert.True(registry.TryConsumeMutation("independent"));
        time.Advance(TimeSpan.FromMilliseconds(999));
        Assert.False(registry.TryConsumeMutation("second"));
        time.Advance(TimeSpan.FromMilliseconds(1));
        Assert.True(registry.TryConsumeMutation("second"));
        Assert.False(registry.TryConsumeMutation("first"));

        registry.Remove("second");
        Assert.False(registry.TryConsumeMutation("first"));
        registry.Remove("first");
        Subscribe(registry, "reconnected", Session(user));
        Assert.True(registry.TryConsumeMutation("reconnected"));
    }

    private static RealtimeSession Session(Guid? user = null) =>
        new(user ?? Guid.NewGuid(), Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N"), new ClaimsPrincipal());

    private static void Subscribe(RealtimeRegistry registry, string connectionId, RealtimeSession session,
        Guid? workspace = null, Guid? conversation = null, Action? abort = null)
    {
        Assert.True(registry.TryRegister(connectionId, session, abort ?? (() => { })));
        Assert.True(registry.TryBeginWatch(connectionId));
        Assert.True(registry.TrySubscribe(connectionId, workspace ?? Workspace, conversation ?? Conversation));
    }
}

// Callbacks run synchronously at their scheduled instants; disposal removes timer references.
// This exercises the actual lifecycle callbacks without wall-clock sleeps or extra dependencies.
internal sealed class ManualRealtimeTimeProvider(DateTimeOffset? initialTime = null) : TimeProvider
{
    private readonly HashSet<ManualTimer> timers = [];
    private DateTimeOffset now = initialTime ?? DateTimeOffset.UtcNow;

    public int UndisposedTimerCount => timers.Count;
    public override DateTimeOffset GetUtcNow() => now;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new ManualTimer(this, callback, state);
        timers.Add(timer);
        timer.Change(dueTime, period);
        return timer;
    }

    public void Advance(TimeSpan elapsed)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(elapsed, TimeSpan.Zero);
        var target = now + elapsed;
        while (timers.Where(timer => timer.DueAt is not null && timer.DueAt <= target)
                   .OrderBy(timer => timer.DueAt).FirstOrDefault() is { } next)
        {
            now = next.DueAt!.Value;
            next.Fire();
        }
        now = target;
    }

    private sealed class ManualTimer(ManualRealtimeTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        private bool disposed;
        private TimeSpan period;
        public DateTimeOffset? DueAt { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan timerPeriod)
        {
            if (disposed) return false;
            period = timerPeriod;
            DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            return true;
        }

        public void Fire()
        {
            DueAt = period > TimeSpan.Zero ? owner.now + period : null;
            callback(state);
        }

        public void Dispose()
        {
            disposed = true;
            DueAt = null;
            owner.timers.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
