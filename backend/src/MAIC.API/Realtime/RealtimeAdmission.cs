namespace MAIC.API.Realtime;

// Count the entire upgrade request, including clients which never complete the SignalR handshake.
// The registry separately bounds authorized workspace subscriptions and their transient state.
public sealed class RealtimeAdmission(RealtimeOptions options)
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, int> users = [];
    private int connections;

    public IDisposable? TryAcquire(Guid userId)
    {
        lock (gate)
        {
            var count = users.GetValueOrDefault(userId);
            if (connections >= options.GlobalConnectionLimit || count >= options.UserConnectionLimit) return null;
            connections++;
            users[userId] = count + 1;
            return new Lease(this, userId);
        }
    }

    private void Release(Guid userId)
    {
        lock (gate)
        {
            connections--;
            if (users[userId] == 1) users.Remove(userId);
            else users[userId]--;
        }
    }

    private sealed class Lease(RealtimeAdmission owner, Guid userId) : IDisposable
    {
        private RealtimeAdmission? remaining = owner;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Release(userId);
    }
}
