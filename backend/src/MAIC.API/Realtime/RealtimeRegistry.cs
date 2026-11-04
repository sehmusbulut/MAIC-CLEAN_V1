namespace MAIC.API.Realtime;

public sealed record RealtimeConnection(string ConnectionId, Guid UserId, string SessionKey,
    Guid? WorkspaceId, Guid? ConversationId);

// Bounded process-local lifecycle and transient state. Never an authorization source.
public sealed class RealtimeRegistry(RealtimeOptions options, TimeProvider time) : IDisposable
{
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, DateTimeOffset> lastMutation = [];

    public bool TryRegister(string connectionId, RealtimeSession session, Action abort)
    {
        lock (gate)
        {
            if (entries.Count >= options.GlobalConnectionLimit || entries.ContainsKey(connectionId)
                || entries.Values.Count(entry => entry.UserId == session.UserId) >= options.UserConnectionLimit) return false;
            var entry = new Entry(connectionId, session.UserId, session.Key, abort, time.GetUtcNow());
            entries.Add(connectionId, entry);
            entry.WatchTimer = time.CreateTimer(_ => Remove(connectionId, abort: true, unwatchedOnly: true), null,
                RealtimeOptions.WatchDeadline, Timeout.InfiniteTimeSpan);
            entry.LifetimeTimer = time.CreateTimer(_ => Drop(connectionId), null, RealtimeOptions.ConnectionLifetime, Timeout.InfiniteTimeSpan);
            return true;
        }
    }

    public RealtimeConnection? Get(string connectionId)
    {
        lock (gate) return entries.TryGetValue(connectionId, out var entry) ? entry.Snapshot() : null;
    }

    public bool TryBeginWatch(string connectionId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(connectionId, out var entry) || entry.WatchClaimed) return false;
            entry.WatchClaimed = true;
            return true;
        }
    }

    public bool TrySubscribe(string connectionId, Guid workspaceId, Guid conversationId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(connectionId, out var entry) || !entry.WatchClaimed || entry.WorkspaceId is not null
                || time.GetUtcNow() - entry.CreatedAt >= RealtimeOptions.WatchDeadline
                || entries.Values.Count(candidate => candidate.WorkspaceId == workspaceId) >= options.WorkspaceConnectionLimit)
                return false;
            entry.WorkspaceId = workspaceId;
            entry.ConversationId = conversationId;
            entry.LastSeen = time.GetUtcNow();
            entry.WatchTimer?.Dispose();
            entry.WatchTimer = null;
            return true;
        }
    }

    public bool TryConsumeMutation(string connectionId)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(connectionId, out var entry) || entry.WorkspaceId is null) return false;
            var now = time.GetUtcNow();
            if (lastMutation.TryGetValue(entry.UserId, out var last) && now - last < RealtimeOptions.PollInterval) return false;
            lastMutation[entry.UserId] = now;
            return true;
        }
    }

    public bool Mutate(string connectionId, bool? typing)
    {
        lock (gate)
        {
            if (!entries.TryGetValue(connectionId, out var entry) || entry.WorkspaceId is null) return false;
            entry.LastSeen = time.GetUtcNow();
            if (typing.HasValue)
                entry.TypingUntil = typing.Value ? entry.LastSeen + RealtimeOptions.TypingLifetime : DateTimeOffset.MinValue;
            return true;
        }
    }

    public RealtimeConnection[] Candidates(Guid workspaceId, Guid conversationId)
    {
        lock (gate)
        {
            var earliest = time.GetUtcNow() - RealtimeOptions.PresenceLifetime;
            return entries.Values.Where(entry => entry.WorkspaceId == workspaceId && entry.ConversationId == conversationId
                && entry.LastSeen > earliest).Select(entry => entry.Snapshot()).ToArray();
        }
    }

    public RealtimePresence[] Presence(Guid workspaceId, Guid conversationId,
        IReadOnlyCollection<string> checkedConnections, IReadOnlyCollection<Guid> authorizedUserIds)
    {
        lock (gate)
        {
            var now = time.GetUtcNow();
            var allowedUsers = authorizedUserIds.ToHashSet();
            var allowedConnections = checkedConnections.ToHashSet(StringComparer.Ordinal);
            return entries.Values.Where(entry => entry.WorkspaceId == workspaceId && entry.ConversationId == conversationId
                    && allowedConnections.Contains(entry.ConnectionId) && allowedUsers.Contains(entry.UserId)
                    && entry.LastSeen > now - RealtimeOptions.PresenceLifetime)
                .GroupBy(entry => entry.UserId).OrderBy(group => group.Key)
                .Select(group => new RealtimePresence(group.Key, group.Any(entry => entry.TypingUntil > now))).ToArray();
        }
    }

    public void PruneUnauthorized(IReadOnlyCollection<RealtimeConnection> candidates, IReadOnlyCollection<Guid> authorizedUserIds)
    {
        var allowed = authorizedUserIds.ToHashSet();
        foreach (var candidate in candidates)
            if (!allowed.Contains(candidate.UserId)) Drop(candidate.ConnectionId);
    }

    public void Drop(string connectionId) => Remove(connectionId, abort: true);
    public void Remove(string connectionId, bool abort = false) => Remove(connectionId, abort, unwatchedOnly: false);

    private void Remove(string connectionId, bool abort, bool unwatchedOnly)
    {
        Entry? entry;
        lock (gate)
        {
            if (!entries.TryGetValue(connectionId, out entry) || (unwatchedOnly && entry.WorkspaceId is not null)) return;
            entries.Remove(connectionId);
            if (!entries.Values.Any(candidate => candidate.UserId == entry.UserId)) lastMutation.Remove(entry.UserId);
        }
        entry.WatchTimer?.Dispose();
        entry.LifetimeTimer?.Dispose();
        if (abort) entry.Abort();
    }

    public void Dispose()
    {
        string[] remaining;
        lock (gate) remaining = entries.Keys.ToArray();
        foreach (var connection in remaining) Drop(connection);
    }

    private sealed class Entry(string connectionId, Guid userId, string sessionKey, Action abort, DateTimeOffset createdAt)
    {
        public string ConnectionId { get; } = connectionId;
        public Guid UserId { get; } = userId;
        public string SessionKey { get; } = sessionKey;
        public Action Abort { get; } = abort;
        public DateTimeOffset CreatedAt { get; } = createdAt;
        public Guid? WorkspaceId { get; set; }
        public Guid? ConversationId { get; set; }
        public bool WatchClaimed { get; set; }
        public DateTimeOffset LastSeen { get; set; }
        public DateTimeOffset TypingUntil { get; set; }
        public ITimer? WatchTimer { get; set; }
        public ITimer? LifetimeTimer { get; set; }
        public RealtimeConnection Snapshot() => new(ConnectionId, UserId, SessionKey, WorkspaceId, ConversationId);
    }
}
