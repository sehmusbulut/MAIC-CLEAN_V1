using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using MAIC.Application.AI;
using MAIC.Domain.AI;
using MAIC.Infrastructure.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class TextGenerationProviderTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private const string Secret = "sk-test-generation-secret";
    private const string ValidResponse = """{"choices":[{"index":0,"finish_reason":"stop","message":{"role":"assistant","content":"raw answer","refusal":null}}],"usage":{"prompt_tokens":11,"completion_tokens":3,"total_tokens":14}}""";
    private static TextGenerationRequest Request(AiModelTier tier = AiModelTier.BALANCED) =>
        new(tier, [new(TextGenerationRole.System, "system policy"), new(TextGenerationRole.User, "private input")], 128);

    private sealed class SettingsBuilder
    {
        public TextGenerationProvider Provider = TextGenerationProvider.OpenAICompatible;
        public string? Endpoint, ApiKey = Secret, Proxy, FastModel = "server-fast", BalancedModel = "server-balanced", QualityModel = "server-quality";
        public bool AllowInsecureHttp = true;
        public bool? EnableThinking;
        public int TimeoutSeconds = 5, MaxInputCharacters = 64_000, MaxOutputTokens = 4096;
        public TextGenerationTokenLimitParameter TokenLimitParameter;
        public TextGenerationSettings Build() => new()
        {
            Provider = Provider, Endpoint = Endpoint, ApiKey = ApiKey, Proxy = Proxy, FastModel = FastModel,
            BalancedModel = BalancedModel, QualityModel = QualityModel, AllowInsecureHttp = AllowInsecureHttp,
            TimeoutSeconds = TimeoutSeconds, MaxInputCharacters = MaxInputCharacters, MaxOutputTokens = MaxOutputTokens,
            TokenLimitParameter = TokenLimitParameter, EnableThinking = EnableThinking
        };
    }

    private static TextGenerationSettings Settings(string endpoint, Action<SettingsBuilder>? change = null)
    {
        var builder = new SettingsBuilder { Endpoint = endpoint };
        change?.Invoke(builder);
        return builder.Build();
    }

    private sealed record Captured(string Authorization, string Cookie, JsonElement Body);

    private static async Task<(TestWebServer Server, List<Captured> Requests)> StartAsync(Func<HttpContext, Task<IResult>>? respond = null)
    {
        var requests = new List<Captured>();
        var server = await TestWebServer.StartAsync(app => app.MapPost("/v1/chat/completions", async (HttpContext context) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body, cancellationToken: context.RequestAborted);
            lock (requests) requests.Add(new(context.Request.Headers.Authorization.ToString(), context.Request.Headers.Cookie.ToString(), body));
            return respond is null ? Results.Text(ValidResponse, "application/json") : await respond(context);
        }));
        return (server, requests);
    }

    private static async Task<TextGenerationFailure> FailureAsync(ITextGenerator generator, TextGenerationRequest? request = null) =>
        (await Assert.ThrowsAsync<TextGenerationException>(() => generator.GenerateAsync(request ?? Request(), Cancellation))).Failure;

    [Fact]
    public async Task Turkish_chat_context_sends_a_content_length_without_chunked_framing()
    {
        var (server, requests) = await StartAsync(context =>
        {
            Assert.True(context.Request.ContentLength > 0);
            Assert.False(context.Request.Headers.ContainsKey("Transfer-Encoding"));
            return Task.FromResult<IResult>(Results.Text(ValidResponse, "application/json"));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        var context = string.Concat(Enumerable.Repeat(OpenAICompatibleEmbeddingGenerator.ProbeDocument, 4));
        var request = Request() with { Messages = [new(TextGenerationRole.System, context), new(TextGenerationRole.User, "İade süreniz kaç gün?")] };
        var result = await generator.GenerateAsync(request, Cancellation);
        Assert.Equal("raw answer", result.Text);
        var body = Assert.Single(requests).Body;
        Assert.Equal(context, body.GetProperty("messages")[0].GetProperty("content").GetString());
        Assert.False(body.TryGetProperty("chat_template_kwargs", out JsonElement _));
        Assert.True(body.TryGetProperty("max_completion_tokens", out JsonElement _));
    }

    [Theory]
    [InlineData(TextGenerationTokenLimitParameter.MaxCompletionTokens, "max_completion_tokens", "max_tokens")]
    [InlineData(TextGenerationTokenLimitParameter.MaxTokens, "max_tokens", "max_completion_tokens")]
    public async Task Tiers_and_roles_are_mapped_by_server_configuration_with_an_explicit_token_limit(
        TextGenerationTokenLimitParameter parameter, string expectedField, string absentField)
    {
        var (server, requests) = await StartAsync();
        await using var _ = server;
        using var provider = new ServiceCollection().AddMaicTextGeneration(Settings(server.BaseUrl + "/v1/", s => s.TokenLimitParameter = parameter))
            .BuildServiceProvider();
        var generator = provider.GetRequiredService<ITextGenerator>();
        Assert.True(generator.Enabled);
        Assert.Empty(requests); // resolving the provider must never trigger a paid probe
        foreach (var tier in Enum.GetValues<AiModelTier>())
        {
            var request = Request(tier) with { Messages = [new(TextGenerationRole.System, "policy"), new(TextGenerationRole.User, "question"), new(TextGenerationRole.Assistant, "prior answer")] };
            var result = await generator.GenerateAsync(request, Cancellation);
            Assert.Equal("raw answer", result.Text);
            Assert.Equal(new TextGenerationUsage(11, 3), result.Usage);
        }
        Assert.Equal(3, requests.Count);
        Assert.Equal(new[] { "server-fast", "server-balanced", "server-quality" }, requests.Select(r => r.Body.GetProperty("model").GetString()));
        foreach (var capture in requests)
        {
            Assert.Equal("Bearer " + Secret, capture.Authorization);
            Assert.Equal(128, capture.Body.GetProperty(expectedField).GetInt32());
            Assert.False(capture.Body.TryGetProperty(absentField, out JsonElement _));
            Assert.False(capture.Body.GetProperty("stream").GetBoolean());
            Assert.False(capture.Body.GetProperty("store").GetBoolean());
            Assert.Equal(1, capture.Body.GetProperty("n").GetInt32());
            Assert.False(capture.Body.TryGetProperty("tools", out JsonElement _));
            Assert.False(capture.Body.TryGetProperty("chat_template_kwargs", out JsonElement _));
            Assert.Equal(new[] { "system", "user", "assistant" }, capture.Body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("role").GetString()));
            Assert.Equal(new[] { "policy", "question", "prior answer" }, capture.Body.GetProperty("messages").EnumerateArray().Select(m => m.GetProperty("content").GetString()));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Optional_thinking_mode_uses_the_provider_extension_with_server_owned_model_routing(bool enableThinking)
    {
        var (server, requests) = await StartAsync();
        await using var _ = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1", s =>
        {
            s.FastModel = "nvidia/nemotron-3.5-lightning-30b-a3b";
            s.BalancedModel = s.QualityModel = "nvidia/nemotron-3-super-120b-a12b";
            s.TokenLimitParameter = TextGenerationTokenLimitParameter.MaxTokens;
            s.EnableThinking = enableThinking;
        }));
        foreach (var tier in Enum.GetValues<AiModelTier>())
            await generator.GenerateAsync(Request(tier), Cancellation);
        Assert.Equal(new[] { "nvidia/nemotron-3.5-lightning-30b-a3b", "nvidia/nemotron-3-super-120b-a12b", "nvidia/nemotron-3-super-120b-a12b" },
            requests.Select(r => r.Body.GetProperty("model").GetString()));
        Assert.All(requests, r =>
        {
            Assert.Equal(enableThinking, r.Body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
            Assert.Equal(128, r.Body.GetProperty("max_tokens").GetInt32());
            Assert.False(r.Body.TryGetProperty("max_completion_tokens", out JsonElement _));
        });
    }

    [Theory]
    [InlineData(408, TextGenerationFailure.UNAVAILABLE)]
    [InlineData(409, TextGenerationFailure.UNAVAILABLE)]
    [InlineData(425, TextGenerationFailure.UNAVAILABLE)]
    [InlineData(429, TextGenerationFailure.UNAVAILABLE)]
    [InlineData(503, TextGenerationFailure.UNAVAILABLE)]
    [InlineData(401, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(402, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(403, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(404, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(405, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(307, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(400, TextGenerationFailure.REJECTED_INPUT)]
    [InlineData(413, TextGenerationFailure.REJECTED_INPUT)]
    public async Task Provider_failures_are_classified_without_body_key_or_input_leaks(int status, TextGenerationFailure expected)
    {
        var (server, requests) = await StartAsync(context =>
        {
            context.Response.Headers.RetryAfter = "600";
            return Task.FromResult(Results.Text($"{Secret} private input", statusCode: status));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        var error = await Assert.ThrowsAsync<TextGenerationException>(() => generator.GenerateAsync(Request(), Cancellation));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(expected == TextGenerationFailure.UNAVAILABLE ? TimeSpan.FromMinutes(1) : null, error.RetryAfter);
        Assert.Null(error.InnerException);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain("private input", error.ToString());
        Assert.Single(requests); // no automatic retry can incur a second charge
    }

    [Theory]
    [InlineData("missing-usage")]
    [InlineData("null-usage")]
    [InlineData("missing-prompt")]
    [InlineData("missing-completion")]
    [InlineData("missing-total")]
    [InlineData("negative-prompt")]
    [InlineData("negative-completion")]
    [InlineData("fractional-prompt")]
    [InlineData("string-prompt")]
    [InlineData("huge-prompt")]
    [InlineData("excess-output")]
    [InlineData("wrong-total")]
    [InlineData("missing-choices")]
    [InlineData("empty-choices")]
    [InlineData("many-choices")]
    [InlineData("wrong-index")]
    [InlineData("missing-index")]
    [InlineData("wrong-role")]
    [InlineData("missing-role")]
    [InlineData("missing-finish")]
    [InlineData("truncated")]
    [InlineData("tool-finish")]
    [InlineData("tool-calls")]
    [InlineData("function-call")]
    [InlineData("wrong-tools-type")]
    [InlineData("wrong-refusal-type")]
    [InlineData("empty-content")]
    [InlineData("null-content")]
    [InlineData("array-content")]
    [InlineData("missing-content")]
    [InlineData("oversize-content")]
    public async Task Malformed_or_incomplete_answers_never_become_success_or_zero_usage(string shape)
    {
        var payload = JsonNode.Parse(ValidResponse)!;
        var usage = payload["usage"]!.AsObject();
        var choice = payload["choices"]![0]!.AsObject();
        var message = choice["message"]!.AsObject();
        switch (shape)
        {
            case "missing-usage": payload.AsObject().Remove("usage"); break;
            case "null-usage": payload["usage"] = null; break;
            case "missing-prompt": usage.Remove("prompt_tokens"); break;
            case "missing-completion": usage.Remove("completion_tokens"); break;
            case "missing-total": usage.Remove("total_tokens"); break;
            case "negative-prompt": usage["prompt_tokens"] = -1; break;
            case "negative-completion": usage["completion_tokens"] = -1; break;
            case "fractional-prompt": usage["prompt_tokens"] = 1.5; break;
            case "string-prompt": usage["prompt_tokens"] = "11"; break;
            case "huge-prompt": usage["prompt_tokens"] = long.MaxValue; break;
            case "excess-output": usage["completion_tokens"] = 129; usage["total_tokens"] = 140; break;
            case "wrong-total": usage["total_tokens"] = 13; break;
            case "missing-choices": payload.AsObject().Remove("choices"); break;
            case "empty-choices": payload["choices"] = new JsonArray(); break;
            case "many-choices": payload["choices"]!.AsArray().Add(choice.DeepClone()); break;
            case "wrong-index": choice["index"] = 1; break;
            case "missing-index": choice.Remove("index"); break;
            case "wrong-role": message["role"] = "user"; break;
            case "missing-role": message.Remove("role"); break;
            case "missing-finish": choice.Remove("finish_reason"); break;
            case "truncated": choice["finish_reason"] = "length"; break;
            case "tool-finish": choice["finish_reason"] = "tool_calls"; break;
            case "tool-calls": message["tool_calls"] = new JsonArray(new JsonObject { ["id"] = "call_1" }); break;
            case "function-call": message["function_call"] = new JsonObject { ["name"] = "do_something" }; break;
            case "wrong-tools-type": message["tool_calls"] = "bad"; break;
            case "wrong-refusal-type": message["refusal"] = true; break;
            case "empty-content": message["content"] = " "; break;
            case "null-content": message["content"] = null; break;
            case "array-content": message["content"] = new JsonArray("answer"); break;
            case "missing-content": message.Remove("content"); break;
            case "oversize-content": message["content"] = new string('a', TextGenerationSettings.MaxOutputTextCharacters + 1); break;
            default: throw new InvalidOperationException();
        }
        var (server, _) = await StartAsync(_ => Task.FromResult(Results.Text(payload.ToJsonString(), "application/json")));
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(TextGenerationFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("trailing-json")]
    [InlineData("trailing-garbage")]
    [InlineData("duplicate-root")]
    [InlineData("duplicate-content")]
    [InlineData("duplicate-usage")]
    [InlineData("deep-json")]
    public async Task Invalid_json_and_ambiguous_duplicate_fields_are_rejected(string shape)
    {
        var payload = shape switch
        {
            "not-json" => "provider private input",
            "trailing-json" => ValidResponse + "{}",
            "trailing-garbage" => ValidResponse + "private input",
            "duplicate-root" => ValidResponse.Insert(1, "\"choices\":[],"),
            "duplicate-content" => ValidResponse.Replace("\"content\":", "\"content\":\"first\",\"content\":", StringComparison.Ordinal),
            "duplicate-usage" => ValidResponse.Replace("\"prompt_tokens\":11", "\"prompt_tokens\":0,\"prompt_tokens\":11", StringComparison.Ordinal),
            _ => new string('[', 40) + "0" + new string(']', 40)
        };
        var (server, _) = await StartAsync(_ => Task.FromResult(Results.Text(payload, "application/json")));
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(TextGenerationFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Refusal_and_content_filter_never_publish_an_answer(bool explicitRefusal)
    {
        var payload = explicitRefusal ? ValidResponse.Replace("\"refusal\":null", "\"refusal\":\"declined\"", StringComparison.Ordinal)
            : ValidResponse.Replace("\"stop\"", "\"content_filter\"", StringComparison.Ordinal);
        var (server, _) = await StartAsync(_ => Task.FromResult(Results.Text(payload, "application/json")));
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(TextGenerationFailure.REJECTED_INPUT, await FailureAsync(generator));
    }

    [Fact]
    public async Task Invalid_requests_are_rejected_before_any_network_call()
    {
        var (server, requests) = await StartAsync();
        await using var _ = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1", s => s.MaxInputCharacters = 32));
        var invalid = new[]
        {
            Request() with { Tier = (AiModelTier)99 }, Request() with { MaxOutputTokens = 0 }, Request() with { MaxOutputTokens = 4097 },
            Request() with { Messages = [] }, Request() with { Messages = [new((TextGenerationRole)99, "content")] },
            Request() with { Messages = [new(TextGenerationRole.User, " ")] },
            Request() with { Messages = [new(TextGenerationRole.User, new string('a', 20)), new(TextGenerationRole.User, new string('b', 13))] },
            Request() with { Messages = Enumerable.Repeat(new TextGenerationMessage(TextGenerationRole.User, "x"), 65).ToArray() }
        };
        foreach (var request in invalid) Assert.Equal(TextGenerationFailure.REJECTED_INPUT, await FailureAsync(generator, request));
        Assert.Empty(requests);
    }

    [Fact]
    public async Task The_deadline_covers_a_stalled_body_after_headers()
    {
        var (server, _) = await StartAsync(context => Task.FromResult(Results.Stream(async stream =>
        {
            await stream.WriteAsync("{\"choices\":"u8.ToArray(), context.RequestAborted);
            await stream.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        }, "application/json")));
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1", s => s.TimeoutSeconds = 1));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(TextGenerationFailure.UNAVAILABLE, await FailureAsync(generator));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10));
    }

    [Fact]
    public async Task Only_caller_cancellation_escapes_and_disposal_is_classified()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _) = await StartAsync(async context =>
        {
            arrived.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Results.Ok();
        });
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var pending = generator.GenerateAsync(Request(), caller.Token);
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await caller.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
        generator.Dispose();
        generator.Dispose();
        Assert.Equal(TextGenerationFailure.UNAVAILABLE, await FailureAsync(generator));
    }

    [Theory]
    [InlineData("content-length")]
    [InlineData("chunked")]
    [InlineData("gzip-bomb")]
    [InlineData("declared-length")]
    [InlineData("headers")]
    public async Task Oversized_provider_responses_are_bounded_including_after_decompression(string shape)
    {
        var bytes = Encoding.UTF8.GetBytes(ValidResponse + new string(' ', TextGenerationSettings.MaxResponseBytes));
        var (server, _) = await StartAsync(context =>
        {
            if (shape == "headers")
            {
                context.Response.Headers["X-Padding"] = new string('a', 100 * 1024);
                return Task.FromResult(Results.Text(ValidResponse, "application/json"));
            }
            if (shape == "gzip-bomb")
            {
                using var compressed = new MemoryStream();
                using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(bytes);
                context.Response.Headers.ContentEncoding = "gzip";
                return Task.FromResult(Results.Bytes(compressed.ToArray(), "application/json"));
            }
            if (shape == "content-length") return Task.FromResult(Results.Bytes(bytes, "application/json"));
            return Task.FromResult(Results.Stream(async stream =>
            {
                if (shape == "declared-length")
                {
                    context.Response.ContentLength = bytes.Length;
                    await stream.WriteAsync(bytes.AsMemory(0, 32), context.RequestAborted);
                    await stream.FlushAsync(context.RequestAborted);
                    await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
                }
                else
                {
                    for (var offset = 0; offset < bytes.Length; offset += 16384)
                    {
                        await stream.WriteAsync(bytes.AsMemory(offset, Math.Min(16384, bytes.Length - offset)), context.RequestAborted);
                        await stream.FlushAsync(context.RequestAborted);
                    }
                }
            }, "application/json"));
        });
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(TextGenerationFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task Invalid_compressed_bodies_are_invalid_responses(string encoding)
    {
        var (server, _) = await StartAsync(context =>
        {
            context.Response.Headers.ContentEncoding = encoding;
            return Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes("this is not a compressed stream at all, sorry"), "application/json"));
        });
        await using var cleanup = server;
        using var generator = new OpenAICompatibleTextGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(TextGenerationFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Fact]
    public async Task Redirects_do_not_forward_credentials_and_cookies_are_not_replayed()
    {
        var (elsewhere, _) = await StartAsync();
        await using var elsewhereCleanup = elsewhere;
        var (redirecting, _) = await StartAsync(context =>
        {
            context.Response.Headers.Location = elsewhere.BaseUrl + "/v1/chat/completions";
            return Task.FromResult(Results.StatusCode(307));
        });
        await using var redirectCleanup = redirecting;
        using (var generator = new OpenAICompatibleTextGenerator(Settings(redirecting.BaseUrl + "/v1")))
            Assert.Equal(TextGenerationFailure.MISCONFIGURED, await FailureAsync(generator));
        Assert.Equal(0, elsewhere.RequestCount);

        var (cookies, requests) = await StartAsync(context =>
        {
            context.Response.Headers.SetCookie = "session=private-cookie; Path=/";
            return Task.FromResult(Results.Text(ValidResponse, "application/json"));
        });
        await using var cookiesCleanup = cookies;
        using var cookieGenerator = new OpenAICompatibleTextGenerator(Settings(cookies.BaseUrl + "/v1", s => s.ApiKey = null));
        await cookieGenerator.GenerateAsync(Request(), Cancellation);
        await cookieGenerator.GenerateAsync(Request(), Cancellation);
        Assert.All(requests, r => { Assert.Empty(r.Cookie); Assert.Empty(r.Authorization); });
    }

    [Theory]
    [InlineData(407, TextGenerationFailure.MISCONFIGURED)]
    [InlineData(502, TextGenerationFailure.UNAVAILABLE)]
    public async Task An_explicit_proxy_only_receives_the_tls_connect_not_credentials_or_content(int status, TextGenerationFailure expected)
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        using var generator = new OpenAICompatibleTextGenerator(Settings("https://generation.example.invalid/v1", s =>
            s.Proxy = $"http://127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}"));
        var pending = FailureAsync(generator);
        using var accept = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        accept.CancelAfter(TimeSpan.FromSeconds(10));
        using (var connection = await proxy.AcceptTcpClientAsync(accept.Token))
        {
            proxy.Stop();
            var buffer = new byte[4096];
            var count = await connection.GetStream().ReadAsync(buffer, accept.Token);
            var header = Encoding.ASCII.GetString(buffer, 0, count);
            Assert.StartsWith("CONNECT generation.example.invalid:443 HTTP/1.1", header);
            Assert.DoesNotContain(Secret, header);
            Assert.DoesNotContain("private input", header);
            await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} Proxy Failure\r\nContent-Length: 0\r\n\r\n"), accept.Token);
        }
        Assert.Equal(expected, await pending.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
    }

    [Fact]
    public void Transport_ignores_environment_proxies_redirects_cookies_and_credentials()
    {
        foreach (var proxy in new string?[] { null, "" })
        {
            using var handler = OpenAICompatibleTextGenerator.CreateHandler(Settings("https://generation.example.com/v1", s => s.Proxy = proxy));
            Assert.Equal((false, null, false, false, false, null, 64), (handler.UseProxy, handler.Proxy, handler.AllowAutoRedirect,
                handler.UseCookies, handler.PreAuthenticate, handler.Credentials, handler.MaxResponseHeadersLength));
        }
        using var configured = OpenAICompatibleTextGenerator.CreateHandler(Settings("https://generation.example.com/v1", s => s.Proxy = "http://proxy.internal:3128"));
        Assert.True(configured.UseProxy);
        Assert.Equal(new Uri("http://proxy.internal:3128/"), Assert.IsType<WebProxy>(configured.Proxy).Address);
    }

    [Fact]
    public async Task Configuration_defaults_to_disabled_and_rejects_invalid_enabled_settings_at_registration()
    {
        using var provider = new ServiceCollection().AddMaicTextGeneration(new TextGenerationSettings()).BuildServiceProvider();
        var disabled = provider.GetRequiredService<ITextGenerator>();
        Assert.False(disabled.Enabled);
        Assert.Equal(TextGenerationFailure.MISCONFIGURED, await FailureAsync(disabled));
        var valid = Settings("https://generation.example.com/v1");
        Assert.Equal(new Uri("https://generation.example.com/v1/chat/completions"), valid.ChatCompletionsUri());
        Assert.DoesNotContain(Secret, valid.ToString());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(valid));
        foreach (var endpoint in new[] { "http://localhost:11434/v1", "http://127.0.0.1/v1", "http://[::1]/v1", "http://10.1.2.3/v1",
                     "http://172.16.0.9/v1", "http://172.31.255.1/v1", "http://192.168.1.20/v1", "http://[fd00::5]/v1" })
            Settings(endpoint).Validate();
        Settings("http://self-hosted:11434/v1", s => s.ApiKey = null).Validate();

        foreach (var change in new Action<SettingsBuilder>[]
                 {
                     s => s.Provider = (TextGenerationProvider)99, s => s.FastModel = null, s => s.BalancedModel = " ", s => s.QualityModel = "line\nmodel",
                     s => s.Endpoint = null, s => s.Endpoint = "not-url", s => s.Endpoint = "ftp://host/v1", s => s.Endpoint = "https://user:secret@host/v1",
                     s => s.Endpoint = "https://host/v1?key=secret", s => s.Endpoint = "https://host/v1#fragment",
                     s => { s.Endpoint = "http://127.0.0.1/v1"; s.AllowInsecureHttp = false; }, s => s.ApiKey = "has space", s => s.ApiKey = "key-ü",
                     s => s.Endpoint = "http://public.example/v1", s => s.Endpoint = "http://8.8.8.8/v1", s => s.Endpoint = "http://169.254.169.254/v1",
                     s => s.Endpoint = "http://172.32.0.1/v1", s => s.Endpoint = "http://[2001:db8::1]/v1",
                     s => s.Proxy = "socks5://proxy:1080", s => s.Proxy = "http://user:pw@proxy:3128", s => s.Proxy = "http://proxy/path",
                     s => { s.Endpoint = "http://10.0.0.1/v1"; s.Proxy = "http://proxy:3128"; }, s => s.Proxy = "invalid",
                     s => s.TimeoutSeconds = 0, s => s.TimeoutSeconds = 121, s => s.MaxInputCharacters = 0, s => s.MaxInputCharacters = 1_000_001,
                     s => s.MaxOutputTokens = 0, s => s.MaxOutputTokens = 16_385, s => s.TokenLimitParameter = (TextGenerationTokenLimitParameter)99
                 })
        {
            var invalid = Settings("https://generation.example.com/v1", change);
            var error = Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMaicTextGeneration(invalid));
            Assert.DoesNotContain(Secret, error.ToString());
            Assert.Null(error.InnerException);
        }
    }
}
