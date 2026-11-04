namespace MAIC.API.Realtime;

public sealed class RealtimeOptions
{
    public string[] AllowedOrigins { get; set; } = [];
    public int GlobalConnectionLimit { get; set; } = 256;
    public int UserConnectionLimit { get; set; } = 4;
    public int WorkspaceConnectionLimit { get; set; } = 64;

    public static readonly TimeSpan WatchDeadline = TimeSpan.FromSeconds(10);
    public static readonly TimeSpan ConnectionLifetime = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan QueryDeadline = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan PresenceLifetime = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan TypingLifetime = TimeSpan.FromSeconds(5);

    public RealtimeOptions Validate()
    {
        if (GlobalConnectionLimit is < 1 or > 256 || UserConnectionLimit is < 1 or > 4
            || WorkspaceConnectionLimit is < 1 or > 64)
            throw new InvalidOperationException("Realtime connection limits must be positive and cannot exceed 256 global, 4 per user and 64 per workspace.");
        if (AllowedOrigins.Any(origin => !IsCanonicalOrigin(origin)))
            throw new InvalidOperationException("Realtime AllowedOrigins must contain exact canonical HTTP(S) authorities without paths or trailing slashes.");
        return this;
    }

    public bool AllowsOrigin(string? origin) => origin is not null && IsCanonicalOrigin(origin)
        && AllowedOrigins.Contains(origin, StringComparer.Ordinal);

    private static bool IsCanonicalOrigin(string? origin) => origin is not null
        && Uri.TryCreate(origin, UriKind.Absolute, out var uri)
        && uri.Scheme is "http" or "https"
        && string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query) && string.IsNullOrEmpty(uri.Fragment)
        && string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal);
}
