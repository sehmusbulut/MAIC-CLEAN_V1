namespace MAIC.Domain.Widget;

public sealed class GuestSession
{
    private GuestSession() { }

    public GuestSession(Guid workspaceId, Guid channelId, Guid contactId, string credentialHash,
        long channelGeneration, DateTimeOffset createdAt)
    {
        if (workspaceId == Guid.Empty || channelId == Guid.Empty || contactId == Guid.Empty)
            throw new ArgumentException("Guest session bindings are required.");
        if (!IsValidCredentialHash(credentialHash)) throw new ArgumentException("A canonical SHA-256 digest is required.", nameof(credentialHash));
        if (channelGeneration < 0) throw new ArgumentOutOfRangeException(nameof(channelGeneration));
        WorkspaceId = workspaceId;
        ChannelId = channelId;
        ContactId = contactId;
        CredentialHash = credentialHash;
        ChannelGeneration = channelGeneration;
        CreatedAt = new DateTimeOffset(createdAt.UtcTicks / 10 * 10, TimeSpan.Zero);
        ExpiresAt = CreatedAt.AddHours(24);
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public Guid ChannelId { get; private set; }
    public Guid ContactId { get; private set; }
    public string CredentialHash { get; private set; } = null!;
    public long ChannelGeneration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }

    public static bool IsValidCredentialHash(string? hash) => hash is { Length: 64 }
        && hash.All(c => c is >= '0' and <= '9' or >= 'A' and <= 'F');

    public bool IsCurrent(DateTimeOffset now, long generation, bool allowRevoked = false) =>
        ExpiresAt > now && ChannelGeneration == generation && (allowRevoked || RevokedAt is null);

    public void Revoke(DateTimeOffset now)
    {
        if (now < CreatedAt) throw new ArgumentOutOfRangeException(nameof(now));
        RevokedAt ??= new DateTimeOffset(now.UtcTicks / 10 * 10, TimeSpan.Zero);
    }
}
