using System.Net;
using System.Net.Sockets;
using System.Text.Json.Serialization;
using MAIC.Application.AI;

namespace MAIC.Infrastructure.AI;

public enum EmbeddingProvider
{
    /// <summary>No provider configured: embeddings are disabled and every call fails with MISCONFIGURED.</summary>
    None,
    /// <summary>Any service exposing an OpenAI-compatible POST {Endpoint}/embeddings API (hosted or self-hosted).</summary>
    OpenAICompatible
}

/// <summary>
/// Configuration section <c>Embeddings</c>. The provider is chosen by configuration, not code. The API key comes from
/// the environment or a secret store (<c>Embeddings__ApiKey</c>), never from a committed file, and is never printed:
/// this is a class without a generated ToString, and the key is excluded from JSON serialization.
/// </summary>
public sealed class EmbeddingSettings
{
    public EmbeddingProvider Provider { get; init; } = EmbeddingProvider.None;
    /// <summary>Vector space key; change it whenever the model or its settings change, then re-index.</summary>
    public string? SpaceKey { get; init; }
    public int Dimensions { get; init; }
    /// <summary>Provider model name, for example text-embedding-3-small, nomic-embed-text or bge-m3.</summary>
    public string? Model { get; init; }
    /// <summary>Base URL; requests go to {Endpoint}/embeddings, for example https://api.openai.com/v1.</summary>
    public string? Endpoint { get; init; }
    /// <summary>Alias for <see cref="Endpoint"/> so both BaseUrl and Endpoint configuration keys bind cleanly.</summary>
    public string? BaseUrl
    {
        get => Endpoint;
        init => Endpoint = value;
    }
    /// <summary>Bearer token; visible ASCII only. Over plain http it is sent only to a loopback or private IP literal.</summary>
    [JsonIgnore]
    public string? ApiKey { get; init; }
    /// <summary>Plain http is refused unless explicitly allowed (for example a self-hosted model on a private network).</summary>
    public bool AllowInsecureHttp { get; init; }
    /// <summary>Optional egress proxy (http/https URL) for an https endpoint; environment proxy variables are never used.</summary>
    public string? Proxy { get; init; }
    /// <summary>Send "dimensions" in the request (models that support shortening, such as OpenAI text-embedding-3).</summary>
    public bool SendDimensions { get; init; }
    /// <summary>Prepended to stored passages / search queries for models that expect instructions (e5: "passage: ").</summary>
    public string? DocumentPrefix { get; init; }
    public string? QueryPrefix { get; init; }
    /// <summary>Optional provider input_type for indexing. Set passage together with QueryInputType=query.</summary>
    public string? DocumentInputType { get; init; }
    /// <summary>Optional provider input_type for retrieval; both modes are omitted by default for OpenAI compatibility.</summary>
    public string? QueryInputType { get; init; }
    public int MaxBatchSize { get; init; } = 64;
    /// <summary>Longest input in UTF-16 units including the prefix; must fit a whole 2000-unit chunk.</summary>
    public int MaxInputLength { get; init; } = 8000;
    public int TimeoutSeconds { get; init; } = 30;
    /// <summary>
    /// True: API/Worker register the space and create its index at startup (the runtime role then needs DDL rights on
    /// KnowledgeChunkVectors). False: they only verify, and an operator runs <c>MAIC.Worker register-embedding-space</c>
    /// with migration credentials first.
    /// </summary>
    public bool ManageSchema { get; init; } = true;
    /// <summary>Spaces no host has used for this many days are deleted with their vectors by the Worker; 0 keeps them.</summary>
    public int RetiredSpaceRetentionDays { get; init; } = 7;
    /// <summary>Chunks one workspace may index per day in this space (bounds provider cost per tenant).</summary>
    public int MaxIndexedChunksPerWorkspacePerDay { get; init; } = 200_000;
    /// <summary>Chunks all Workers together send to the provider for indexing per minute (queries have their own budget).</summary>
    public int MaxIndexingChunksPerMinute { get; init; } = 3_000;

    public bool Enabled => Provider != EmbeddingProvider.None;

    /// <summary>The space of the disabled generator; never registered or stored.</summary>
    public const string ReservedSpaceKey = EmbeddingSpace.DisabledKey;

    /// <summary>Hard cap on one response body (after decompression).</summary>
    public const long MaxResponseBytesCap = 32L * 1024 * 1024;

    /// <summary>Generous bound for one batch of float vectors as JSON text (32 bytes per value plus 64 KiB).</summary>
    public long MaxResponseBytes => 64L * 1024 + (long)MaxBatchSize * Dimensions * 32;

    /// <summary>Validates the configuration at startup; a wrong setting stops the host instead of failing every document.</summary>
    public EmbeddingSettings Validate()
    {
        if (!Enum.IsDefined(Provider)) throw Invalid("Provider");
        if (!Enabled) return this;
        if ((DocumentInputType is not null || QueryInputType is not null)
            && (DocumentInputType != "passage" || QueryInputType != "query"))
            throw Invalid("DocumentInputType/QueryInputType (set both to passage/query or leave both unset)");
        if (Space().Key == ReservedSpaceKey) throw Invalid("SpaceKey (\"disabled\" is reserved)");
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 200 || Model.Any(char.IsControl)) throw Invalid("Model");
        if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var endpoint) || endpoint.UserInfo.Length > 0
            || !string.IsNullOrEmpty(endpoint.Query) || !string.IsNullOrEmpty(endpoint.Fragment)
            || !(endpoint.Scheme == Uri.UriSchemeHttps || (endpoint.Scheme == Uri.UriSchemeHttp && AllowInsecureHttp)))
            throw Invalid("Endpoint (absolute https URL without credentials, query or fragment; http needs AllowInsecureHttp)");
        if (ApiKey is not null && (ApiKey.Length > 4096 || ApiKey.Any(c => c is < '!' or > '~'))) throw Invalid("ApiKey (visible ASCII)");
        if (!string.IsNullOrEmpty(ApiKey) && endpoint.Scheme == Uri.UriSchemeHttp && !IsLoopbackOrPrivateLiteral(endpoint))
            throw Invalid("ApiKey (over http only to a loopback or private IP address; use https)");
        if (!string.IsNullOrEmpty(Proxy) && (ProxyUri() is null || endpoint.Scheme != Uri.UriSchemeHttps))
            throw Invalid("Proxy (absolute http/https URL without credentials, path, query or fragment; https endpoints only)");
        if (MaxBatchSize is < 1 or > 2048) throw Invalid("MaxBatchSize");
        if (MaxResponseBytes > MaxResponseBytesCap) throw Invalid("MaxBatchSize * Dimensions (a batch response must fit 32 MiB)");
        var prefix = Math.Max(DocumentPrefix?.Length ?? 0, QueryPrefix?.Length ?? 0);
        if (MaxInputLength - prefix < Domain.Knowledge.KnowledgeChunker.MaxChunkLength || MaxInputLength > 1_000_000) throw Invalid("MaxInputLength");
        if (TimeoutSeconds is < 1 or > 600) throw Invalid("TimeoutSeconds");
        if (RetiredSpaceRetentionDays is < 0 or > 3650) throw Invalid("RetiredSpaceRetentionDays");
        if (MaxIndexedChunksPerWorkspacePerDay < 1) throw Invalid("MaxIndexedChunksPerWorkspacePerDay");
        if (MaxIndexingChunksPerMinute < 1) throw Invalid("MaxIndexingChunksPerMinute");
        return this;
    }

    public EmbeddingSpace Space()
    {
        try
        {
            return new EmbeddingSpace(SpaceKey!, Dimensions);
        }
        catch (Exception error) when (error is ArgumentException or NullReferenceException)
        {
            throw Invalid("SpaceKey/Dimensions");
        }
    }

    public Uri EmbeddingsUri() => new(Endpoint!.TrimEnd('/') + "/embeddings");

    /// <summary>The configured proxy, or null when none is set (empty counts as unset) or it is not a plain http/https authority.</summary>
    public Uri? ProxyUri() =>
        !string.IsNullOrEmpty(Proxy) && Uri.TryCreate(Proxy, UriKind.Absolute, out var proxy)
        && (proxy.Scheme == Uri.UriSchemeHttp || proxy.Scheme == Uri.UriSchemeHttps) && proxy.UserInfo.Length == 0
        && proxy.AbsolutePath == "/" && string.IsNullOrEmpty(proxy.Query) && string.IsNullOrEmpty(proxy.Fragment)
            ? proxy
            : null;

    /// <summary>Loopback (including "localhost") or an RFC 1918 / IPv6 unique-local address literal; no DNS lookup.</summary>
    private static bool IsLoopbackOrPrivateLiteral(Uri endpoint)
    {
        if (endpoint.IsLoopback) return true;
        if (!IPAddress.TryParse(endpoint.DnsSafeHost, out var address)) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        if (address.AddressFamily == AddressFamily.InterNetworkV6) return address.IsIPv6UniqueLocal;
        var b = address.GetAddressBytes();
        return b[0] == 10 || (b[0] == 172 && (b[1] & 0xF0) == 16) || (b[0] == 192 && b[1] == 168);
    }

    private static InvalidOperationException Invalid(string setting) => new($"Embeddings configuration is invalid: {setting}.");
}
