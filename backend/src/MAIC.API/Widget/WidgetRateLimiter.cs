using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;

namespace MAIC.API.Widget;

public sealed class WidgetHttpOptions
{
    public int ConcurrentRequests { get; set; } = 16;
    public int RequestsPerMinute { get; set; } = 1200;
    public int StartRequestsPerMinute { get; set; } = 60;
    public int StartsPerIpPerMinute { get; set; } = 5;
    public int MessagesPerCredentialPerMinute { get; set; } = 30;

    public WidgetHttpOptions Validate()
    {
        if (ConcurrentRequests is < 1 or > 256 || RequestsPerMinute is < 1 or > 120_000
            || StartRequestsPerMinute is < 1 or > 10_000 || StartsPerIpPerMinute is < 1 or > 1000
            || MessagesPerCredentialPerMinute is < 1 or > 1000)
            throw new InvalidOperationException("Widget HTTP limits must be positive and within the supported bounds.");
        return this;
    }
}

// Fixed-size buckets cannot grow with attacker-controlled IPs or credentials. Hash collisions can
// only apply a stricter limit; these counters never authenticate anyone or contain a raw capability.
public sealed class WidgetRateLimiter(WidgetHttpOptions options, TimeProvider time)
{
    private readonly object gate = new();
    private readonly Bucket[] starts = new Bucket[1024];
    private readonly Bucket[] messages = new Bucket[1024];
    private Bucket requests;
    private Bucket allStarts;
    private int active;

    public IDisposable? TryEnter()
    {
        lock (gate)
        {
            Reset(ref requests);
            if (active >= options.ConcurrentRequests || requests.Count >= options.RequestsPerMinute) return null;
            requests.Count++;
            active++;
            return new Lease(this);
        }
    }

    public bool TryStart(IPAddress? address)
    {
        var slot = Slot(address?.MapToIPv6().GetAddressBytes() ?? [0]);
        lock (gate)
        {
            Reset(ref allStarts);
            Reset(ref starts[slot]);
            if (allStarts.Count >= options.StartRequestsPerMinute || starts[slot].Count >= options.StartsPerIpPerMinute)
                return false;
            allStarts.Count++;
            starts[slot].Count++;
            return true;
        }
    }

    public bool TryMessage(string credentialHash)
    {
        var slot = Slot(Encoding.ASCII.GetBytes(credentialHash));
        lock (gate)
        {
            Reset(ref messages[slot]);
            if (messages[slot].Count >= options.MessagesPerCredentialPerMinute) return false;
            messages[slot].Count++;
            return true;
        }
    }

    public int RetryAfterSeconds => 60 - time.GetUtcNow().Second;
    private static int Slot(ReadOnlySpan<byte> value) => (int)(BinaryPrimitives.ReadUInt32BigEndian(SHA256.HashData(value)) % 1024);
    private void Reset(ref Bucket bucket)
    {
        var window = time.GetUtcNow().UtcTicks / TimeSpan.TicksPerMinute;
        if (bucket.Window == window) return;
        bucket.Window = window;
        bucket.Count = 0;
    }
    private void Release() { lock (gate) active--; }
    private struct Bucket { public long Window; public int Count; }
    private sealed class Lease(WidgetRateLimiter owner) : IDisposable
    {
        private WidgetRateLimiter? remaining = owner;
        public void Dispose() => Interlocked.Exchange(ref remaining, null)?.Release();
    }
}
