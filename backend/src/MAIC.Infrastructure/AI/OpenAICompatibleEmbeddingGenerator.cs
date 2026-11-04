using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using MAIC.Application.AI;

namespace MAIC.Infrastructure.AI;

/// <summary>
/// Embeds through any OpenAI-compatible <c>POST {Endpoint}/embeddings</c> API (OpenAI, Mistral, Ollama, vLLM, LM Studio,
/// LocalAI and similar). The endpoint is operator configuration, not user input. Inputs, outputs and provider error
/// bodies are never logged or attached to exceptions; failures are classified into <see cref="EmbeddingFailure"/>.
/// <para>
/// Before tenant content is sent, one shared probe embeds a query and a full batch of benign, dense, full-size chunks.
/// If the provider refuses them or answers with the wrong shape or dimensions, the configuration (model, dimensions,
/// token or batch limits, SendDimensions, key) is wrong: calls fail with MISCONFIGURED without sending content, and the
/// probe is repeated after <see cref="MisconfiguredRecheck"/> so a fixed provider recovers without a restart. A
/// transient probe failure is remembered briefly (honouring Retry-After). A MISCONFIGURED answer to a real call makes the
/// next call probe again, so only benign probe content decides that the settings are wrong. Callers wait for the probe
/// with their own cancellation; cancelling does not stop it. Redirects, cookies and environment proxies are never used,
/// and <see cref="EmbeddingSettings.TimeoutSeconds"/> covers connecting, headers and the whole body.
/// </para>
/// </summary>
public sealed class OpenAICompatibleEmbeddingGenerator : IEmbeddingGenerator, IDisposable
{
    public const string ProbeQuery = "maic embedding probe";
    /// <summary>
    /// A realistic worst-case chunk of <see cref="Domain.Knowledge.KnowledgeChunker.MaxChunkLength"/> characters: Turkish
    /// text with order numbers, dates and amounts, which tokenizes densely, so token limits below a chunk are caught.
    /// </summary>
    public static readonly string ProbeDocument = BuildProbeDocument();
    public static readonly TimeSpan MisconfiguredRecheck = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan DefaultProbeBackoff = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan MaxProbeBackoff = TimeSpan.FromMinutes(1);

    private readonly EmbeddingSettings settings;
    private readonly TimeProvider time;
    private readonly HttpClient client;
    private readonly Uri uri;
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource disposal = new();
    private readonly CancellationToken disposing;
    private readonly Lock probeLock = new();
    private Task<EmbeddingException?>? probe; // guarded by probeLock
    private EmbeddingFailure? blocked; // guarded by probeLock
    private long blockedUntil; // TimeProvider timestamp; guarded by probeLock
    private volatile bool verified;
    private int disposed;

    public OpenAICompatibleEmbeddingGenerator(EmbeddingSettings settings, TimeProvider? timeProvider = null)
    {
        this.settings = settings.Validate();
        if (settings.Provider != EmbeddingProvider.OpenAICompatible) throw new InvalidOperationException("Provider must be OpenAICompatible.");
        time = timeProvider ?? TimeProvider.System;
        disposing = disposal.Token;
        Space = settings.Space();
        uri = settings.EmbeddingsUri();
        timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        // The per-call deadline in SendAsync replaces HttpClient.Timeout, which would not cover a streamed body.
        client = new HttpClient(CreateHandler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(settings.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    /// <summary>No redirects, cookies, credentials or environment proxies; only an explicitly configured proxy.</summary>
    internal static SocketsHttpHandler CreateHandler(EmbeddingSettings settings)
    {
        var proxy = settings.ProxyUri();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            UseProxy = proxy is not null,
            Proxy = proxy is null ? null : new WebProxy(proxy) { BypassProxyOnLocal = false },
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
    }

    public EmbeddingSpace Space { get; }
    public int MaxBatchSize => settings.MaxBatchSize;
    public int MaxInputLength => settings.MaxInputLength - Math.Max(settings.DocumentPrefix?.Length ?? 0, settings.QueryPrefix?.Length ?? 0);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 1) return;
        disposal.Cancel();
        disposal.Dispose();
        client.Dispose();
    }

    public async Task<IReadOnlyList<ReadOnlyMemory<float>>> EmbedAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        if (inputs.Count == 0) return [];
        if (inputs.Count > MaxBatchSize || inputs.Any(i => string.IsNullOrEmpty(i) || i.Length > MaxInputLength))
            throw new EmbeddingException(EmbeddingFailure.REJECTED_INPUT);
        await EnsureConfiguredAsync(cancellationToken);
        try
        {
            return await SendAsync(inputs, purpose, cancellationToken);
        }
        catch (EmbeddingException error) when (error.Failure == EmbeddingFailure.MISCONFIGURED)
        {
            // Revoked key, removed model or moved endpoint: verify again with the benign probe before sending more content.
            verified = false;
            throw;
        }
    }

    private async Task EnsureConfiguredAsync(CancellationToken cancellationToken)
    {
        if (verified) return;
        Task<EmbeddingException?> current;
        lock (probeLock)
        {
            if (verified) return;
            var now = time.GetTimestamp();
            if (blocked is { } failure && now < blockedUntil)
                throw new EmbeddingException(failure,
                    retryAfter: failure == EmbeddingFailure.UNAVAILABLE ? time.GetElapsedTime(now, blockedUntil) : null);
            if (probe is null || probe.IsCompleted) probe = StartProbe();
            current = probe;
        }
        if (await current.WaitAsync(cancellationToken) is { } probeFailure)
            throw new EmbeddingException(probeFailure.Failure, retryAfter: probeFailure.RetryAfter);
    }

    /// <summary>Starts the probe without the first caller's execution context (activity, log scopes, async locals).</summary>
    private Task<EmbeddingException?> StartProbe()
    {
        if (ExecutionContext.IsFlowSuppressed()) return Task.Run(() => ProbeAsync(disposing), CancellationToken.None);
        using (ExecutionContext.SuppressFlow()) return Task.Run(() => ProbeAsync(disposing), CancellationToken.None);
    }

    /// <summary>Runs detached from any caller; returns the failure to report, and never faults.</summary>
    private async Task<EmbeddingException?> ProbeAsync(CancellationToken cancellationToken)
    {
        EmbeddingException? failure = null;
        try
        {
            await SendAsync([ProbeQuery], EmbeddingPurpose.Query, cancellationToken);
            await SendAsync(Enumerable.Repeat(ProbeDocument, MaxBatchSize).ToArray(), EmbeddingPurpose.Document, cancellationToken);
        }
        catch (EmbeddingException error)
        {
            failure = error;
        }
        catch (Exception)
        {
            failure = new EmbeddingException(EmbeddingFailure.UNAVAILABLE); // disposed while probing
        }

        lock (probeLock)
        {
            if (failure is null)
            {
                blocked = null;
                verified = true;
                return null;
            }
            // Benign input of the configured maximum size was refused or answered wrongly: the settings are wrong.
            var reported = failure.Transient ? EmbeddingFailure.UNAVAILABLE : EmbeddingFailure.MISCONFIGURED;
            var wait = failure.Transient ? Clamp(failure.RetryAfter ?? DefaultProbeBackoff) : MisconfiguredRecheck;
            blocked = reported;
            blockedUntil = time.GetTimestamp() + (long)(wait.TotalSeconds * time.TimestampFrequency);
            return new EmbeddingException(reported, retryAfter: failure.Transient ? wait : null);
        }
    }

    private static TimeSpan Clamp(TimeSpan wait) =>
        wait < TimeSpan.FromSeconds(1) ? TimeSpan.FromSeconds(1) : wait > MaxProbeBackoff ? MaxProbeBackoff : wait;

    private async Task<IReadOnlyList<ReadOnlyMemory<float>>> SendAsync(IReadOnlyList<string> inputs, EmbeddingPurpose purpose,
        CancellationToken cancellationToken)
    {
        var prefix = (purpose == EmbeddingPurpose.Query ? settings.QueryPrefix : settings.DocumentPrefix) ?? "";
        var request = new EmbeddingRequest(settings.Model!, inputs.Select(i => prefix + i).ToArray(), "float",
            settings.SendDimensions ? settings.Dimensions : null,
            purpose == EmbeddingPurpose.Query ? settings.QueryInputType : settings.DocumentInputType);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        try
        {
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(request, options: SerializerOptions) };
            // NVIDIA's hosted ingress can stall large chunked POSTs; buffer validated inputs to send Content-Length.
            await message.Content.LoadIntoBufferAsync(deadline.Token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            if (status is 408 or 409 or 425 or 429 || status >= 500)
                throw new EmbeddingException(EmbeddingFailure.UNAVAILABLE, retryAfter: RetryAfter(response));
            // 402: billing or quota exhausted on some gateways; an operator must act, the content is not at fault.
            if (status is 401 or 402 or 403 or 404 or 405 || (status is >= 300 and < 400))
                throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
            if (status is < 200 or > 299) throw new EmbeddingException(EmbeddingFailure.REJECTED_INPUT);

            var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
            return Order(JsonSerializer.Deserialize<EmbeddingResponse>(bytes, SerializerOptions), inputs.Count);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new EmbeddingException(EmbeddingFailure.UNAVAILABLE); // deadline: connect, headers or body too slow
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE); // oversized headers or trailers
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ProxyTunnelError
                                                 && error.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED); // the configured proxy wants credentials
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            // Connection failures; the exception text may contain the endpoint, so it is not attached.
            throw new EmbeddingException(EmbeddingFailure.UNAVAILABLE);
        }
        catch (Exception error) when (error is JsonException or NotSupportedException or InvalidDataException
                                      || (error is InvalidOperationException and not ObjectDisposedException))
        {
            throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE); // malformed JSON or corrupt compression
        }
    }

    /// <summary>Orders results by their "index"; any gap, duplicate or wrong shape is an invalid response.</summary>
    private ReadOnlyMemory<float>[] Order(EmbeddingResponse? body, int count)
    {
        if (body?.Data is not { } data || data.Count != count) throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE);
        var vectors = new ReadOnlyMemory<float>[count];
        foreach (var item in data)
        {
            if (item?.Embedding is not { } embedding || item.Index is not { } index || index < 0 || index >= count
                || !vectors[index].IsEmpty)
                throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE);
            // A well-formed vector of another size means Dimensions does not match the model: configuration, not content.
            if (embedding.Length != Space.Dimensions) throw new EmbeddingException(EmbeddingFailure.MISCONFIGURED);
            vectors[index] = embedding;
        }
        return vectors;
    }

    /// <summary>An oversized body is a deterministic provider/configuration problem, not a transient one.</summary>
    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        var maxResponseBytes = settings.MaxResponseBytes;
        if (content.Headers.ContentLength > maxResponseBytes) throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > maxResponseBytes) throw new EmbeddingException(EmbeddingFailure.INVALID_RESPONSE);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static string BuildProbeDocument()
    {
        const string sentence = "Sipariş TR-48213-XK (12.05.2025) için iade talebi: 3 ürün, toplam 1.249,90 TL; kargo takip no 7731-0094-2210, "
                                + "fatura FTR2025/000871. Müşteri destek ekibi 48 saat içinde yanıt verir. ";
        var builder = new System.Text.StringBuilder();
        while (builder.Length < Domain.Knowledge.KnowledgeChunker.MaxChunkLength) builder.Append(sentence);
        return builder.ToString(0, Domain.Knowledge.KnowledgeChunker.MaxChunkLength);
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } when delta > TimeSpan.Zero && delta < TimeSpan.FromHours(1) => delta,
        { Date: { } date } when date > DateTimeOffset.UtcNow && date - DateTimeOffset.UtcNow < TimeSpan.FromHours(1) => date - DateTimeOffset.UtcNow,
        _ => null
    };

    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private sealed record EmbeddingRequest(
        [property: JsonPropertyName("model")] string Model,
        [property: JsonPropertyName("input")] string[] Input,
        [property: JsonPropertyName("encoding_format")] string EncodingFormat,
        [property: JsonPropertyName("dimensions")] int? Dimensions,
        [property: JsonPropertyName("input_type")] string? InputType);

    private sealed record EmbeddingResponse([property: JsonPropertyName("data")] List<EmbeddingItem?>? Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int? Index,
        [property: JsonPropertyName("embedding")] float[]? Embedding);
}
