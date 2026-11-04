using System.Security.Cryptography;

namespace MAIC.Domain.Channels;

public enum ChannelType
{
    WEB
}

public sealed class Channel
{
    private Channel() { }

    public Channel(Guid workspaceId, string name, Guid? defaultAiAgentId = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("Workspace is required.", nameof(workspaceId));
        if (!IsValidName(name)) throw new ArgumentException("Channel name must contain 1–200 characters without control characters.", nameof(name));
        if (defaultAiAgentId == Guid.Empty) throw new ArgumentException("Default AI agent ID cannot be empty.", nameof(defaultAiAgentId));

        WorkspaceId = workspaceId;
        Name = name.Trim();
        PublishableToken = CreatePublishableToken();
        DefaultAiAgentId = defaultAiAgentId;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public ChannelType Type { get; private set; } = ChannelType.WEB;
    public string Name { get; private set; } = null!;
    public string PublishableToken { get; private set; } = null!;
    public bool Enabled { get; private set; } = true;
    public Guid? DefaultAiAgentId { get; private set; }
    public long GuestAccessGeneration { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static bool IsValidName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 200) return false;
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsControl(name[i])) return false;
            if (char.IsHighSurrogate(name[i]))
            {
                if (++i >= name.Length || !char.IsLowSurrogate(name[i])) return false;
            }
            else if (char.IsLowSurrogate(name[i])) return false;
        }
        return true;
    }

    public static bool IsValidPublishableToken(string? token) => token is { Length: 43 }
        && token.All(c => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_');

    public void Rename(string name)
    {
        if (!IsValidName(name)) throw new ArgumentException("Channel name must contain 1–200 characters without control characters.", nameof(name));
        Name = name.Trim();
    }

    public void SetEnabled(bool enabled)
    {
        if (Enabled && !enabled) GuestAccessGeneration = checked(GuestAccessGeneration + 1);
        Enabled = enabled;
    }

    public void SetDefaultAiAgent(Guid? aiAgentId)
    {
        if (aiAgentId == Guid.Empty) throw new ArgumentException("Default AI agent ID cannot be empty.", nameof(aiAgentId));
        DefaultAiAgentId = aiAgentId;
    }

    private static string CreatePublishableToken()
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }
}
