namespace MAIC.Domain.Workspaces;

public sealed class ChannelIntegration
{
    public const string ProviderWhatsApp = "WHATSAPP";
    public const string ProviderInstagram = "INSTAGRAM";
    public const string ProviderTrendyol = "TRENDYOL";

    public const string StatusDisconnected = "DISCONNECTED";
    public const string StatusConnected = "CONNECTED";
    public const string StatusError = "ERROR";

    public static readonly string[] SupportedProviders =
    [
        ProviderWhatsApp,
        ProviderInstagram,
        ProviderTrendyol
    ];

    private ChannelIntegration() { }

    public ChannelIntegration(
        Guid workspaceId,
        string provider,
        string? webhookVerifyToken = null)
    {
        if (workspaceId == Guid.Empty) throw new ArgumentException("WorkspaceId is required.", nameof(workspaceId));
        ArgumentException.ThrowIfNullOrWhiteSpace(provider);

        var normalizedProvider = provider.Trim().ToUpperInvariant();
        if (!SupportedProviders.Contains(normalizedProvider, StringComparer.Ordinal))
        {
            throw new ArgumentOutOfRangeException(nameof(provider), "Unsupported channel integration provider.");
        }

        WorkspaceId = workspaceId;
        Provider = normalizedProvider;
        Status = StatusDisconnected;
        WebhookVerifyToken = string.IsNullOrWhiteSpace(webhookVerifyToken)
            ? GenerateVerifyToken()
            : webhookVerifyToken.Trim();
        VerifiedMetadataJson = "{}";
        CreatedAt = DateTimeOffset.UtcNow;
        UpdatedAt = CreatedAt;
    }

    public Guid Id { get; private set; } = Guid.CreateVersion7();
    public Guid WorkspaceId { get; private set; }
    public string Provider { get; private set; } = ProviderWhatsApp;
    public string Status { get; private set; } = StatusDisconnected;
    public string? DisplayName { get; private set; }
    public string ExternalAccountId { get; private set; } = string.Empty;
    public string? SecondaryAccountId { get; private set; }
    public string AccessToken { get; private set; } = string.Empty;
    public string? AppSecret { get; private set; }
    public string WebhookVerifyToken { get; private set; } = GenerateVerifyToken();
    public string VerifiedMetadataJson { get; private set; } = "{}";
    public DateTimeOffset? LastTestedAt { get; private set; }
    public DateTimeOffset? LastWebhookReceivedAt { get; private set; }
    public string? LastErrorMessage { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; private set; } = DateTimeOffset.UtcNow;

    public static string GenerateVerifyToken() => $"db_verify_{Guid.NewGuid():N}";

    public void MarkConnected(
        string displayName,
        string externalAccountId,
        string? secondaryAccountId,
        string accessToken,
        string? appSecret,
        string verifiedMetadataJson,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        Status = StatusConnected;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? Provider : displayName.Trim();
        ExternalAccountId = externalAccountId.Trim();
        SecondaryAccountId = string.IsNullOrWhiteSpace(secondaryAccountId) ? null : secondaryAccountId.Trim();
        AccessToken = accessToken.Trim();
        if (!string.IsNullOrWhiteSpace(appSecret))
        {
            AppSecret = appSecret.Trim();
        }
        VerifiedMetadataJson = string.IsNullOrWhiteSpace(verifiedMetadataJson) ? "{}" : verifiedMetadataJson.Trim();
        LastTestedAt = timestamp;
        LastErrorMessage = null;
        UpdatedAt = timestamp;
        if (string.IsNullOrWhiteSpace(WebhookVerifyToken))
        {
            WebhookVerifyToken = GenerateVerifyToken();
        }
    }

    public void MarkError(
        string errorMessage,
        string? externalAccountId = null,
        string? secondaryAccountId = null,
        DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        Status = StatusError;
        if (!string.IsNullOrWhiteSpace(externalAccountId))
        {
            ExternalAccountId = externalAccountId.Trim();
        }
        if (!string.IsNullOrWhiteSpace(secondaryAccountId))
        {
            SecondaryAccountId = secondaryAccountId.Trim();
        }
        LastTestedAt = timestamp;
        LastErrorMessage = errorMessage.Length > 2000 ? errorMessage[..2000] : errorMessage;
        UpdatedAt = timestamp;
    }

    public void MarkDisconnected(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        Status = StatusDisconnected;
        DisplayName = null;
        ExternalAccountId = string.Empty;
        SecondaryAccountId = null;
        AccessToken = string.Empty;
        AppSecret = null;
        VerifiedMetadataJson = "{}";
        LastErrorMessage = null;
        UpdatedAt = timestamp;
    }

    public void RecordWebhookReceived(DateTimeOffset? now = null)
    {
        var timestamp = now ?? DateTimeOffset.UtcNow;
        LastWebhookReceivedAt = timestamp;
        UpdatedAt = timestamp;
    }
}
