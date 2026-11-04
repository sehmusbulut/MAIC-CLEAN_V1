using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using MAIC.Domain.AI;

namespace MAIC.Infrastructure.AI;

public enum TextGenerationProvider { None, OpenAICompatible }

/// <summary>Older compatible servers may require max_tokens; selection is operator configuration, never client input.</summary>
public enum TextGenerationTokenLimitParameter { MaxCompletionTokens, MaxTokens }

/// <summary>
/// Server-owned TextGeneration configuration. Tier names never select a provider or arbitrary model.
/// Supply ApiKey through TextGeneration__ApiKey or a secret store; neither JSON nor ToString exposes it.
/// </summary>
public sealed class TextGenerationSettings
{
    public TextGenerationProvider Provider { get; init; } = TextGenerationProvider.None;
    public string? FastModel { get; init; }
    public string? BalancedModel { get; init; }
    public string? QualityModel { get; init; }
    /// <summary>Base URL; requests use POST {Endpoint}/chat/completions.</summary>
    public string? Endpoint { get; init; }
    /// <summary>Alias for <see cref="Endpoint"/> so both BaseUrl and Endpoint configuration keys bind cleanly.</summary>
    public string? BaseUrl
    {
        get => Endpoint;
        init => Endpoint = value;
    }
    [JsonIgnore]
    public string? ApiKey { get; init; }
    public bool AllowInsecureHttp { get; init; }
    /// <summary>Explicit http/https proxy for an https endpoint. Environment proxies are ignored.</summary>
    public string? Proxy { get; init; }
    public int TimeoutSeconds { get; init; } = 30;
    public int MaxInputCharacters { get; init; } = 64_000;
    public int MaxOutputTokens { get; init; } = 4096;
    public TextGenerationTokenLimitParameter TokenLimitParameter { get; init; } = TextGenerationTokenLimitParameter.MaxCompletionTokens;
    /// <summary>Optional chat_template_kwargs.enable_thinking for compatible models; null omits the extension.</summary>
    public bool? EnableThinking { get; init; }
    public bool Enabled => Provider != TextGenerationProvider.None;
    public const int MaxResponseBytes = 1024 * 1024;
    public const int MaxOutputTextCharacters = 65_536;

    public TextGenerationSettings Validate()
    {
        if (!Enum.IsDefined(Provider)) throw Invalid("Provider");
        if (!Enabled) return this;
        foreach (var (name, value) in new[] { (nameof(FastModel), FastModel), (nameof(BalancedModel), BalancedModel), (nameof(QualityModel), QualityModel) })
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(char.IsControl)) throw Invalid(name);
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length > 0
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && AllowInsecureHttp)))
            throw Invalid("Endpoint (absolute https URL without credentials, query or fragment; http needs AllowInsecureHttp)");
        if (ApiKey is not null && (ApiKey.Length > 4096 || ApiKey.Any(c => c is < '!' or > '~'))) throw Invalid("ApiKey (visible ASCII)");
        if (!string.IsNullOrEmpty(ApiKey) && endpoint.Scheme == Uri.UriSchemeHttp && !IsLoopbackOrPrivateLiteral(endpoint))
            throw Invalid("ApiKey (over http only to a loopback or private IP address; use https)");
        if (!string.IsNullOrEmpty(Proxy) && (ProxyUri() is null || endpoint.Scheme != Uri.UriSchemeHttps))
            throw Invalid("Proxy (absolute http/https authority without credentials; https endpoints only)");
        if (TimeoutSeconds is < 1 or > 120) throw Invalid(nameof(TimeoutSeconds));
        if (MaxInputCharacters is < 1 or > 1_000_000) throw Invalid(nameof(MaxInputCharacters));
        if (MaxOutputTokens is < 1 or > 16_384) throw Invalid(nameof(MaxOutputTokens));
        if (!Enum.IsDefined(TokenLimitParameter)) throw Invalid(nameof(TokenLimitParameter));
        return this;
    }

    public string Model(AiModelTier tier) => tier switch
    {
        AiModelTier.FAST => FastModel!,
        AiModelTier.BALANCED => BalancedModel!,
        AiModelTier.QUALITY => QualityModel!,
        _ => throw new ArgumentOutOfRangeException(nameof(tier))
    };

    public Uri ChatCompletionsUri() => new(Endpoint!.TrimEnd('/') + "/chat/completions");

    public Uri? ProxyUri() =>
        !string.IsNullOrEmpty(Proxy) && Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy)
        && (proxy.Scheme == Uri.UriSchemeHttp || proxy.Scheme == Uri.UriSchemeHttps) && proxy.UserInfo.Length == 0
        && proxy.AbsolutePath == "/" && string.IsNullOrEmpty(proxy.Query) && string.IsNullOrEmpty(proxy.Fragment)
            ? proxy : null;

    private static bool IsLoopbackOrPrivateLiteral(Uri endpoint)
    {
        if (endpoint.IsLoopback) return true;
        if (!IPAddress.TryParse(endpoint.DnsSafeHost, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6UniqueLocal;
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 || (bytes[0] == 172 && (bytes[1] & 0xF0) == 16) || (bytes[0] == 192 && bytes[1] == 168);
    }

    private static InvalidOperationException Invalid(string setting) => new($"TextGeneration configuration is invalid: {setting}.");
}
