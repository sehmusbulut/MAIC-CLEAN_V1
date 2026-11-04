using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using MAIC.Application.AI;

namespace MAIC.Infrastructure.AI;

/// <summary>
/// Configured chat-completions transport. Returns raw text; Application validates the answer contract and citations.
/// No retries, content probes, logging, cookies, redirects, or implicit egress proxies. The deadline covers the full body.
/// </summary>
public sealed class OpenAICompatibleTextGenerator : ITextGenerator, IDisposable
{
    private readonly TextGenerationSettings settings;
    private readonly HttpClient client;
    private readonly Uri uri;
    private int disposed;

    public OpenAICompatibleTextGenerator(TextGenerationSettings settings)
    {
        this.settings = settings.Validate();
        if (settings.Provider != TextGenerationProvider.OpenAICompatible) throw new InvalidOperationException("Provider must be OpenAICompatible.");
        uri = settings.ChatCompletionsUri();
        client = new HttpClient(CreateHandler(settings)) { Timeout = Timeout.InfiniteTimeSpan };
        if (!string.IsNullOrEmpty(settings.ApiKey))
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    internal static SocketsHttpHandler CreateHandler(TextGenerationSettings settings)
    {
        var proxy = settings.ProxyUri();
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, Credentials = null, PreAuthenticate = false,
            UseProxy = proxy is not null, Proxy = proxy is null ? null : new WebProxy(proxy) { BypassProxyOnLocal = false },
            MaxResponseHeadersLength = 64, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.All
        };
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) client.Dispose();
    }

    public async Task<TextGenerationResult> GenerateAsync(TextGenerationRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateRequest(request);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
        try
        {
            var body = new Dictionary<string, object>
            {
                ["model"] = settings.Model(request.Tier),
                ["messages"] = request.Messages.Select(m => new { role = Role(m.Role), content = m.Content }).ToArray(),
                ["stream"] = false,
                ["n"] = 1,
                ["store"] = false,
                [settings.TokenLimitParameter == TextGenerationTokenLimitParameter.MaxCompletionTokens ? "max_completion_tokens" : "max_tokens"] = request.MaxOutputTokens
            };
            if (settings.EnableThinking is { } enableThinking)
                body["chat_template_kwargs"] = new { enable_thinking = enableThinking };
            using var message = new HttpRequestMessage(HttpMethod.Post, uri) { Content = JsonContent.Create(body) };
            // NVIDIA's hosted ingress can stall large chunked POSTs; buffer validated inputs to send Content-Length.
            await message.Content.LoadIntoBufferAsync(deadline.Token);
            using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            var status = (int)response.StatusCode;
            if (status is 408 or 409 or 425 or 429 || status >= 500)
                throw new TextGenerationException(TextGenerationFailure.UNAVAILABLE, RetryAfter(response));
            if (status is 401 or 402 or 403 or 404 or 405 or 407 || status is >= 300 and < 400)
                throw new TextGenerationException(TextGenerationFailure.MISCONFIGURED);
            if (status is < 200 or > 299) throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);

            var bytes = await ReadBoundedAsync(response.Content, deadline.Token);
            return Parse(bytes, request.MaxOutputTokens);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (TextGenerationException) { throw; }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ConfigurationLimitExceeded)
        {
            throw new TextGenerationException(TextGenerationFailure.INVALID_RESPONSE);
        }
        catch (HttpRequestException error) when (error.HttpRequestError == HttpRequestError.ProxyTunnelError
                                                 && error.StatusCode == HttpStatusCode.ProxyAuthenticationRequired)
        {
            throw new TextGenerationException(TextGenerationFailure.MISCONFIGURED);
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or KeyNotFoundException
                                      or InvalidOperationException and not ObjectDisposedException)
        {
            throw new TextGenerationException(TextGenerationFailure.INVALID_RESPONSE);
        }
        catch (Exception)
        {
            // Includes deadline, disposal and network failures. Never retain transport/body/key text or an inner exception.
            throw new TextGenerationException(TextGenerationFailure.UNAVAILABLE);
        }
    }

    private void ValidateRequest(TextGenerationRequest request)
    {
        if (request is null || !Enum.IsDefined(request.Tier) || request.MaxOutputTokens < 1 || request.MaxOutputTokens > settings.MaxOutputTokens
            || request.Messages is not { Count: > 0 and <= 64 }) throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);
        long characters = 0;
        foreach (var message in request.Messages)
        {
            if (message is null || !Enum.IsDefined(message.Role) || string.IsNullOrWhiteSpace(message.Content))
                throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);
            characters += message.Content.Length;
            if (characters > settings.MaxInputCharacters) throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);
        }
    }

    private static string Role(TextGenerationRole role) => role switch
    {
        TextGenerationRole.System => "system", TextGenerationRole.User => "user", TextGenerationRole.Assistant => "assistant",
        _ => throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT)
    };

    private static TextGenerationResult Parse(byte[] bytes, int maxOutputTokens)
    {
        using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
        var root = Object(document.RootElement);
        var choices = root.GetProperty("choices");
        if (choices.ValueKind != JsonValueKind.Array || choices.GetArrayLength() != 1) throw InvalidResponse();
        var choice = Object(choices[0]);
        if (!choice.GetProperty("index").TryGetInt32(out var index) || index != 0) throw InvalidResponse();
        var finish = choice.GetProperty("finish_reason").GetString();
        if (finish == "content_filter") throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);
        if (finish != "stop") throw InvalidResponse(); // no partial answers or tool/function execution
        var message = Object(choice.GetProperty("message"));
        if (message.GetProperty("role").GetString() != "assistant") throw InvalidResponse();
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind != JsonValueKind.Null)
        {
            if (refusal.ValueKind != JsonValueKind.String) throw InvalidResponse();
            if (!string.IsNullOrEmpty(refusal.GetString())) throw new TextGenerationException(TextGenerationFailure.REJECTED_INPUT);
        }
        if (message.TryGetProperty("tool_calls", out var tools) && tools.ValueKind != JsonValueKind.Null
            && (tools.ValueKind != JsonValueKind.Array || tools.GetArrayLength() != 0)) throw InvalidResponse();
        if (message.TryGetProperty("function_call", out var function) && function.ValueKind != JsonValueKind.Null) throw InvalidResponse();
        var text = message.GetProperty("content").GetString();
        if (string.IsNullOrWhiteSpace(text) || text.Length > TextGenerationSettings.MaxOutputTextCharacters) throw InvalidResponse();
        var usage = Object(root.GetProperty("usage"));
        var input = TokenCount(usage, "prompt_tokens", 4_000_000);
        var output = TokenCount(usage, "completion_tokens", maxOutputTokens);
        var total = TokenCount(usage, "total_tokens", 4_000_000L + maxOutputTokens);
        if (total != input + output) throw InvalidResponse();
        return new TextGenerationResult(text, new TextGenerationUsage(input, output));
    }

    private static JsonElement Object(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) throw InvalidResponse();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject()) if (!names.Add(property.Name)) throw InvalidResponse();
        return element;
    }

    private static long TokenCount(JsonElement usage, string name, long maximum)
    {
        if (!usage.TryGetProperty(name, out var property) || !property.TryGetInt64(out var count) || count < 0 || count > maximum)
            throw InvalidResponse();
        return count;
    }

    private static TextGenerationException InvalidResponse() => new(TextGenerationFailure.INVALID_RESPONSE);

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > TextGenerationSettings.MaxResponseBytes) throw InvalidResponse();
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            if (buffer.Length + read > TextGenerationSettings.MaxResponseBytes) throw InvalidResponse();
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private static TimeSpan? RetryAfter(HttpResponseMessage response) => response.Headers.RetryAfter switch
    {
        { Delta: { } delta } when delta > TimeSpan.Zero => delta > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delta,
        { Date: { } date } when date > DateTimeOffset.UtcNow => date - DateTimeOffset.UtcNow > TimeSpan.FromMinutes(1)
            ? TimeSpan.FromMinutes(1) : date - DateTimeOffset.UtcNow,
        _ => null
    };
}
