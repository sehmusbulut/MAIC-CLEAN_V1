using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using MAIC.Application.AI;
using MAIC.Infrastructure.AI;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>The generic OpenAI-compatible adapter against a loopback stand-in, plus configuration validation.</summary>
public sealed class EmbeddingProviderTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;
    private const string Secret = "sk-test-secret-value";

    private static EmbeddingSettings Settings(string endpoint, int dimensions = 3, Action<EmbeddingSettingsBuilder>? change = null)
    {
        var builder = new EmbeddingSettingsBuilder { Endpoint = endpoint, Dimensions = dimensions };
        change?.Invoke(builder);
        return builder.Build();
    }

    private sealed class EmbeddingSettingsBuilder
    {
        public EmbeddingProvider Provider = EmbeddingProvider.OpenAICompatible;
        public string? SpaceKey = "test-v1", Model = "test-embed", Endpoint, ApiKey = Secret, DocumentPrefix, QueryPrefix, Proxy,
            DocumentInputType, QueryInputType;
        public int Dimensions = 3, MaxBatchSize = 4, MaxInputLength = 4000, TimeoutSeconds = 5;
        public bool AllowInsecureHttp = true, SendDimensions;
        public EmbeddingSettings Build() => new()
        {
            Provider = Provider, SpaceKey = SpaceKey, Model = Model, Endpoint = Endpoint, ApiKey = ApiKey, Dimensions = Dimensions,
            MaxBatchSize = MaxBatchSize, MaxInputLength = MaxInputLength, TimeoutSeconds = TimeoutSeconds,
            AllowInsecureHttp = AllowInsecureHttp, SendDimensions = SendDimensions, DocumentPrefix = DocumentPrefix, QueryPrefix = QueryPrefix,
            Proxy = Proxy, DocumentInputType = DocumentInputType, QueryInputType = QueryInputType
        };
    }

    private sealed record Captured(string? Authorization, JsonElement Body)
    {
        public string[] Inputs => Body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
        public bool IsProbe => Inputs is [var query] && query.EndsWith(OpenAICompatibleEmbeddingGenerator.ProbeQuery, StringComparison.Ordinal)
            || (Inputs.Length > 0 && Inputs.All(i => i.EndsWith(OpenAICompatibleEmbeddingGenerator.ProbeDocument, StringComparison.Ordinal)));
    }

    private static List<Captured> Real(List<Captured> requests)
    {
        lock (requests) return requests.Where(r => !r.IsProbe).ToList();
    }

    private static object Echo(string[] inputs) => new
    {
        @object = "list", model = "test-embed",
        data = inputs.Select((text, i) => new { index = i, embedding = new float[] { text.Length, i, 1 }, @object = "embedding" }).Reverse()
    };

    /// <summary>
    /// Echoes one vector per input ([length, index, 1]) in reverse order, recording each request. A custom responder
    /// sees only real calls unless <paramref name="answerProbes"/> is false; the configuration probe is echoed.
    /// </summary>
    private static async Task<(TestWebServer Server, List<Captured> Requests)> StartAsync(
        Func<HttpContext, JsonElement, Task<IResult>>? respond = null, bool answerProbes = true)
    {
        var requests = new List<Captured>();
        var server = await TestWebServer.StartAsync(app => app.MapPost("/v1/embeddings", async (HttpContext context) =>
        {
            var body = await JsonSerializer.DeserializeAsync<JsonElement>(context.Request.Body);
            var captured = new Captured(context.Request.Headers.Authorization.ToString(), body);
            lock (requests) requests.Add(captured);
            if (respond is not null && !(answerProbes && captured.IsProbe)) return await respond(context, body);
            return Results.Json(Echo(captured.Inputs));
        }));
        return (server, requests);
    }

    private sealed class ManualTime : TimeProvider
    {
        public long Now;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now;
        public void Advance(TimeSpan by) => Now += by.Ticks;
    }

    private static async Task<EmbeddingFailure> FailureAsync(IEmbeddingGenerator generator, string input = "x") =>
        (await Assert.ThrowsAsync<EmbeddingException>(() => generator.EmbedAsync([input], EmbeddingPurpose.Document, Cancellation))).Failure;

    [Fact]
    public async Task Turkish_multi_input_probes_and_documents_send_a_content_length_without_chunked_framing()
    {
        var (server, requests) = await StartAsync((context, body) =>
        {
            Assert.True(context.Request.ContentLength > 0);
            Assert.False(context.Request.Headers.ContainsKey("Transfer-Encoding"));
            return Task.FromResult<IResult>(Results.Json(Echo(body.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToArray())));
        }, answerProbes: false);
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        await generator.EmbedAsync(["İade süresi 30 gündür.", "Müşteri destek saatleri hafta içi 09:00–18:00."], EmbeddingPurpose.Document, Cancellation);
        Assert.Equal(3, requests.Count);
        Assert.Equal(Enumerable.Repeat(OpenAICompatibleEmbeddingGenerator.ProbeDocument, 4), requests[1].Inputs);
        Assert.Equal(new[] { "İade süresi 30 gündür.", "Müşteri destek saatleri hafta içi 09:00–18:00." }, requests[2].Inputs);
        Assert.All(requests, r => Assert.False(r.Body.TryGetProperty("input_type", out JsonElement _)));
    }

    [Fact]
    public async Task Requests_follow_the_openai_embeddings_contract_and_results_are_ordered_by_index()
    {
        var (server, requests) = await StartAsync();
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1/", change: s =>
        {
            s.SendDimensions = true;
            s.DocumentPrefix = "passage: ";
            s.QueryPrefix = "query: ";
        }));
        Assert.Equal((new EmbeddingSpace("test-v1", 3), 4, 4000 - "passage: ".Length), (generator.Space, generator.MaxBatchSize, generator.MaxInputLength));

        var vectors = await generator.EmbedAsync(["a", "bbb"], EmbeddingPurpose.Document, Cancellation);
        Assert.Equal([["passage: a".Length, 0, 1], ["passage: bbb".Length, 1, 1]], vectors.Select(v => v.ToArray()));
        await generator.EmbedAsync(["q"], EmbeddingPurpose.Query, Cancellation);

        // The one-time probe runs first, with the configured prefixes and a full-size chunk; later calls do not repeat it.
        Assert.Equal(4, requests.Count);
        Assert.Equal(["query: " + OpenAICompatibleEmbeddingGenerator.ProbeQuery], requests[0].Inputs);
        Assert.Equal(Enumerable.Repeat("passage: " + OpenAICompatibleEmbeddingGenerator.ProbeDocument, 4), requests[1].Inputs);
        Assert.Equal(2000, OpenAICompatibleEmbeddingGenerator.ProbeDocument.Length);
        var (auth, body) = requests[2];
        Assert.Equal("Bearer " + Secret, auth);
        Assert.Equal("test-embed", body.GetProperty("model").GetString());
        Assert.Equal("float", body.GetProperty("encoding_format").GetString());
        Assert.All(requests, r => Assert.Equal(3, r.Body.GetProperty("dimensions").GetInt32()));
        Assert.All(requests, r => Assert.False(r.Body.TryGetProperty("input_type", out JsonElement _)));
        Assert.Equal(["passage: a", "passage: bbb"], requests[2].Inputs);
        Assert.Equal(["query: q"], requests[3].Inputs);
        Assert.Empty(await generator.EmbedAsync([], EmbeddingPurpose.Query, Cancellation));
        Assert.Equal(4, requests.Count);
    }

    [Fact]
    public async Task Provider_input_modes_apply_to_probes_indexing_and_retrieval_without_shortening_vectors()
    {
        var (server, requests) = await StartAsync((context, body) =>
        {
            Assert.Equal("float", body.GetProperty("encoding_format").GetString());
            Assert.False(body.TryGetProperty("dimensions", out JsonElement _));
            var expected = body.GetProperty("input")[0].GetString() == "question" ? "query" : "passage";
            Assert.Equal(expected, body.GetProperty("input_type").GetString());
            return Task.FromResult<IResult>(Results.Json(Echo(body.GetProperty("input").EnumerateArray().Select(x => x.GetString()!).ToArray())));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1", change: s =>
        {
            s.DocumentInputType = "passage";
            s.QueryInputType = "query";
        }));
        await generator.EmbedAsync(["document"], EmbeddingPurpose.Document, Cancellation);
        await generator.EmbedAsync(["question"], EmbeddingPurpose.Query, Cancellation);
        Assert.Equal(new[] { "query", "passage", "passage", "query" },
            requests.Select(r => r.Body.GetProperty("input_type").GetString()));
        Assert.All(requests, r => Assert.False(r.Body.TryGetProperty("dimensions", out JsonElement _)));
    }

    [Theory]
    [InlineData("passage", null)]
    [InlineData(null, "query")]
    [InlineData("query", "passage")]
    [InlineData("PASSAGE", "query")]
    [InlineData("passage", "query ")]
    [InlineData("", "")]
    [InlineData("passage", "unsupported")]
    public void Provider_input_modes_must_be_a_valid_indexing_and_retrieval_pair(string? document, string? query)
    {
        var settings = Settings("https://integrate.api.nvidia.com/v1", change: s =>
        {
            s.DocumentInputType = document;
            s.QueryInputType = query;
        });
        Assert.Throws<InvalidOperationException>(() => settings.Validate());
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMaicEmbeddings(settings));
    }

    [Fact]
    public async Task Works_end_to_end_through_the_knowledge_vector_index_without_an_api_key()
    {
        var (server, requests) = await StartAsync();
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1", change: s => s.ApiKey = null));
        var store = new InMemoryKnowledgeVectorStore();
        var index = new KnowledgeVectorIndex(generator, store);
        var (workspace, source, document) = (Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7());
        var chunks = Enumerable.Range(1, 6).Select(i => new KnowledgeChunkText(
            new KnowledgeVectorKey(workspace, source, document, Guid.CreateVersion7(), i), new string('x', i))).ToArray();
        await index.IndexDocumentAsync(workspace, document, chunks, Cancellation);
        Assert.Equal([4, 2], Real(requests).Select(r => r.Inputs.Length));
        Assert.All(requests, r => Assert.Equal("", r.Authorization));
        Assert.False(requests[0].Body.TryGetProperty("dimensions", out JsonElement _));
        Assert.Equal(5, (await index.SearchAsync(workspace, KnowledgeScopeFilter.All, "question", 5, Cancellation)).Count);
    }

    [Theory]
    [InlineData(429, EmbeddingFailure.UNAVAILABLE)]
    [InlineData(503, EmbeddingFailure.UNAVAILABLE)]
    [InlineData(500, EmbeddingFailure.UNAVAILABLE)]
    [InlineData(408, EmbeddingFailure.UNAVAILABLE)]
    [InlineData(401, EmbeddingFailure.MISCONFIGURED)]
    [InlineData(402, EmbeddingFailure.MISCONFIGURED)]
    [InlineData(403, EmbeddingFailure.MISCONFIGURED)]
    [InlineData(404, EmbeddingFailure.MISCONFIGURED)]
    [InlineData(302, EmbeddingFailure.MISCONFIGURED)]
    [InlineData(400, EmbeddingFailure.REJECTED_INPUT)]
    [InlineData(413, EmbeddingFailure.REJECTED_INPUT)]
    public async Task Provider_statuses_are_classified_without_leaking_payloads(int status, EmbeddingFailure expected)
    {
        var (server, _) = await StartAsync((context, _) =>
        {
            context.Response.Headers.RetryAfter = "7";
            if (status == 302) context.Response.Headers.Location = "http://127.0.0.1:1/elsewhere";
            return Task.FromResult(Results.Text($"provider said: {Secret} leaked input", statusCode: status));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        var error = await Assert.ThrowsAsync<EmbeddingException>(() => generator.EmbedAsync(["private input"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal(expected, error.Failure);
        Assert.Equal(expected == EmbeddingFailure.UNAVAILABLE ? TimeSpan.FromSeconds(7) : null, error.RetryAfter);
        Assert.DoesNotContain(Secret, error.ToString());
        Assert.DoesNotContain("private input", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("""{"data":[]}""")] // missing item
    [InlineData("""{"data":[{"index":0,"embedding":[1,2,3]},{"index":0,"embedding":[1,2,3]}]}""")] // duplicate index
    [InlineData("""{"data":[{"index":5,"embedding":[1,2,3]}]}""")] // index out of range
    [InlineData("""{"data":[{"embedding":[1,2,3]}]}""")] // no index
    [InlineData("""{"error":"x"}""")]
    [InlineData("not json")]
    public async Task Malformed_responses_are_invalid(string payload)
    {
        var (server, _) = await StartAsync((_, _) => Task.FromResult(Results.Text(payload, "application/json")));
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Fact]
    public async Task Well_formed_vectors_of_another_size_mean_the_dimensions_setting_is_wrong()
    {
        var (server, _) = await StartAsync((_, _) => Task.FromResult(Results.Text("""{"data":[{"index":0,"embedding":[1,2]}]}""", "application/json")));
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, await FailureAsync(generator));
    }

    public static TheoryData<string, int, string> ProbeRejections => new()
    {
        { "token limit below a chunk", 413, "" },
        { "model cannot embed a chunk", 400, "" },
        { "wrong key", 401, "" },
        { "unknown model", 404, "" },
        { "wrong dimensions", 200, """{"data":[{"index":0,"embedding":[1,2]}]}""" },
        { "not an embeddings API", 200, "<html>welcome</html>" }
    };

    [Theory]
    [MemberData(nameof(ProbeRejections))]
    public async Task A_rejected_probe_means_misconfiguration_and_is_rechecked_later(string reason, int status, string payload)
    {
        var healthy = false;
        var (server, requests) = await StartAsync((_, body) =>
        {
            var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return Task.FromResult(Volatile.Read(ref healthy) ? Results.Json(Echo(inputs)) : Results.Text(payload, "application/json", statusCode: status));
        }, answerProbes: false);
        await using var _ = server;
        var time = new ManualTime();
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"), time);

        Assert.Equal(EmbeddingFailure.MISCONFIGURED, await FailureAsync(generator, "private input"));
        var probes = requests.Count;
        Assert.InRange(probes, 1, 2);
        Assert.All(requests, r => Assert.True(r.IsProbe, reason)); // tenant content is never sent while misconfigured

        // Known misconfiguration is served from memory until the recheck interval passes.
        time.Advance(OpenAICompatibleEmbeddingGenerator.MisconfiguredRecheck - TimeSpan.FromSeconds(1));
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, await FailureAsync(generator));
        Assert.Equal(probes, requests.Count);

        // Once the provider is fixed, the next probe after the interval succeeds without a restart.
        Volatile.Write(ref healthy, true);
        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(await generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal(probes + 3, requests.Count);
    }

    [Fact]
    public async Task A_transient_probe_failure_is_remembered_briefly_and_then_retried()
    {
        var calls = 0;
        var (server, requests) = await StartAsync((context, body) =>
        {
            context.Response.Headers.RetryAfter = "3";
            var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Results.StatusCode(503) : Results.Json(Echo(inputs)));
        }, answerProbes: false);
        await using var _ = server;
        var time = new ManualTime();
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"), time);
        var error = await Assert.ThrowsAsync<EmbeddingException>(() => generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal((EmbeddingFailure.UNAVAILABLE, true, TimeSpan.FromSeconds(3)), (error.Failure, error.Transient, error.RetryAfter));

        // Within Retry-After, callers fail fast without traffic and learn the remaining delay.
        time.Advance(TimeSpan.FromSeconds(1));
        error = await Assert.ThrowsAsync<EmbeddingException>(() => generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal((EmbeddingFailure.UNAVAILABLE, TimeSpan.FromSeconds(2)), (error.Failure, error.RetryAfter));
        Assert.Single(requests);

        time.Advance(TimeSpan.FromSeconds(2));
        Assert.Single(await generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal([true, true, true, false], requests.Select(r => r.IsProbe));
    }

    [Fact]
    public async Task Concurrent_callers_share_one_probe_and_a_cancelled_caller_does_not_cancel_it()
    {
        var arrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, requests) = await StartAsync(async (context, body) =>
        {
            var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            if (inputs is [OpenAICompatibleEmbeddingGenerator.ProbeQuery])
            {
                arrived.TrySetResult();
                await release.Task.WaitAsync(context.RequestAborted);
            }
            return Results.Json(Echo(inputs));
        }, answerProbes: false);
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        using var impatient = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        var cancelled = generator.EmbedAsync(["x"], EmbeddingPurpose.Document, impatient.Token);
        var callers = Enumerable.Range(0, 20).Select(i => generator.EmbedAsync([$"text {i}"], EmbeddingPurpose.Document, Cancellation)).ToArray();

        // The caller leaves while the probe query is held; the probe itself keeps running for everyone else.
        await arrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        await impatient.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
        lock (requests) Assert.Single(requests);
        release.SetResult();

        Assert.All(await Task.WhenAll(callers), vectors => Assert.Single(vectors));
        Assert.Equal(2, requests.Count(r => r.IsProbe));
        Assert.Equal(20, Real(requests).Count);
    }

    [Fact]
    public async Task Cancelling_a_real_call_is_cancellation_not_an_outage_and_dispose_stops_a_running_probe()
    {
        var realArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (server, _) = await StartAsync(async (context, _) =>
        {
            realArrived.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Results.Ok();
        });
        await using var _ = server;
        using (var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1")))
        {
            using var caller = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
            var call = generator.EmbedAsync(["x"], EmbeddingPurpose.Document, caller.Token);
            await realArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
            await caller.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
        }

        var probeArrived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var (held, _) = await StartAsync(async (context, _) =>
        {
            probeArrived.TrySetResult();
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            return Results.Ok();
        }, answerProbes: false);
        await using var __ = held;
        var disposable = new OpenAICompatibleEmbeddingGenerator(Settings(held.BaseUrl + "/v1"));
        var waiting = disposable.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation);
        await probeArrived.Task.WaitAsync(TimeSpan.FromSeconds(10), Cancellation);
        disposable.Dispose();
        disposable.Dispose(); // idempotent
        Assert.Equal(EmbeddingFailure.UNAVAILABLE, (await Assert.ThrowsAsync<EmbeddingException>(() =>
            waiting.WaitAsync(TimeSpan.FromSeconds(10), Cancellation))).Failure);
    }

    [Theory]
    [InlineData(null, 5)] // no Retry-After: default backoff
    [InlineData("600", 60)] // capped
    public async Task The_probe_backoff_defaults_and_is_capped(string? retryAfter, int expectedSeconds)
    {
        var calls = 0;
        var (server, requests) = await StartAsync((context, body) =>
        {
            if (retryAfter is not null) context.Response.Headers.RetryAfter = retryAfter;
            var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return Task.FromResult(Interlocked.Increment(ref calls) == 1 ? Results.StatusCode(503) : Results.Json(Echo(inputs)));
        }, answerProbes: false);
        await using var _ = server;
        var time = new ManualTime();
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"), time);
        var expected = TimeSpan.FromSeconds(expectedSeconds);
        Assert.Equal(expected, (await Assert.ThrowsAsync<EmbeddingException>(() =>
            generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation))).RetryAfter);
        time.Advance(expected - TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), (await Assert.ThrowsAsync<EmbeddingException>(() =>
            generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation))).RetryAfter);
        Assert.Single(requests);
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Single(await generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
    }

    [Fact]
    public async Task A_misconfigured_answer_to_a_real_call_makes_the_next_call_probe_again()
    {
        var real = 0;
        var (server, requests) = await StartAsync((_, body) =>
        {
            var inputs = body.GetProperty("input").EnumerateArray().Select(e => e.GetString()!).ToArray();
            return Task.FromResult(Interlocked.Increment(ref real) == 1 ? Results.StatusCode(401) : Results.Json(Echo(inputs)));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, await FailureAsync(generator));
        Assert.Single(await generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Single(await generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation));
        Assert.Equal([true, true, false, true, true, false, false], requests.Select(r => r.IsProbe));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_timeout_covers_a_body_that_stalls_after_the_headers(bool duringProbe)
    {
        var (server, _) = await StartAsync((context, _) => Task.FromResult(Results.Stream(async stream =>
        {
            await stream.WriteAsync("""{"data":[{"index":0,"""u8.ToArray(), context.RequestAborted);
            await stream.FlushAsync(context.RequestAborted);
            await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
        }, "application/json")), answerProbes: !duringProbe);
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1", change: s => s.TimeoutSeconds = 1));
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(EmbeddingFailure.UNAVAILABLE, await FailureAsync(generator));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(10), $"took {started.Elapsed}");
    }

    [Theory]
    [InlineData("gzip")]
    [InlineData("deflate")]
    [InlineData("br")]
    public async Task Corrupt_compressed_bodies_are_invalid(string encoding)
    {
        var (server, _) = await StartAsync((context, _) =>
        {
            context.Response.Headers.ContentEncoding = encoding;
            return Task.FromResult(Results.Bytes(Encoding.ASCII.GetBytes("this is not a compressed stream at all, sorry"), "application/json"));
        });
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    [Fact]
    public async Task The_real_http_handler_does_not_follow_redirects_or_leak_the_key_to_another_host()
    {
        var (elsewhere, elsewhereRequests) = await StartAsync();
        await using var __ = elsewhere;
        var (server, _) = await StartAsync((context, _) =>
        {
            context.Response.Headers.Location = elsewhere.BaseUrl + "/v1/embeddings";
            return Task.FromResult(Results.StatusCode(307));
        }, answerProbes: false);
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, await FailureAsync(generator));
        Assert.Equal(0, elsewhere.RequestCount);
        Assert.Empty(elsewhereRequests);
    }

    [Theory]
    [InlineData("content-length")]
    [InlineData("chunked")]
    [InlineData("gzip-bomb")]
    [InlineData("huge-headers")]
    [InlineData("declared-length")]
    public async Task Oversized_responses_are_invalid_and_bounded_after_decompression(string shape)
    {
        // Bound for MaxBatchSize 4 x 3 dimensions: 64 KiB + 384 bytes.
        var padding = new string(' ', 8 * 1024 * 1024);
        var json = Encoding.UTF8.GetBytes("""{"data":[{"index":0,"embedding":[1,2,3]}]""" + padding + "}");
        var (server, _) = await StartAsync((context, _) => Task.FromResult<IResult>(shape switch
        {
            "content-length" => Results.Bytes(json, "application/json"),
            "chunked" => Results.Stream(async stream =>
            {
                for (var offset = 0; offset < json.Length; offset += 16 * 1024)
                {
                    await stream.WriteAsync(json.AsMemory(offset, Math.Min(16 * 1024, json.Length - offset)));
                    await stream.FlushAsync();
                }
            }, "application/json"),
            "gzip-bomb" => Gzip(context, json),
            // Refused from the declared length alone: the body never arrives, so reading it would time out instead.
            "declared-length" => Results.Stream(async stream =>
            {
                context.Response.ContentLength = json.Length;
                await stream.WriteAsync(json.AsMemory(0, 64), context.RequestAborted);
                await stream.FlushAsync(context.RequestAborted);
                await Task.Delay(TimeSpan.FromSeconds(30), context.RequestAborted);
            }, "application/json"),
            _ => HugeHeader(context)
        }));
        await using var _ = server;
        using var generator = new OpenAICompatibleEmbeddingGenerator(Settings(server.BaseUrl + "/v1"));
        Assert.Equal(EmbeddingFailure.INVALID_RESPONSE, await FailureAsync(generator));
    }

    private static IResult Gzip(HttpContext context, byte[] json)
    {
        using var compressed = new MemoryStream();
        using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) gzip.Write(json);
        Assert.True(compressed.Length < 64 * 1024); // small on the wire, 8 MiB once inflated
        context.Response.Headers.ContentEncoding = "gzip";
        return Results.Bytes(compressed.ToArray(), "application/json");
    }

    private static IResult HugeHeader(HttpContext context)
    {
        context.Response.Headers["X-Padding"] = new string('a', 100 * 1024);
        return Results.Json(Echo(["x"]));
    }

    [Theory]
    [InlineData(null, EmbeddingFailure.UNAVAILABLE)]
    [InlineData("HTTP/1.1 407 Proxy Authentication Required\r\nProxy-Authenticate: Basic realm=\"egress\"\r\nContent-Length: 0\r\n\r\n", EmbeddingFailure.MISCONFIGURED)]
    [InlineData("HTTP/1.1 502 Bad Gateway\r\nContent-Length: 0\r\n\r\n", EmbeddingFailure.UNAVAILABLE)]
    public async Task Only_an_explicitly_configured_proxy_is_used(string? answer, EmbeddingFailure expected)
    {
        using var proxy = new TcpListener(IPAddress.Loopback, 0);
        proxy.Start();
        var settings = Settings("https://embeddings.example.invalid/v1", change: s =>
        {
            s.AllowInsecureHttp = false;
            s.Proxy = $"http://127.0.0.1:{((IPEndPoint)proxy.LocalEndpoint).Port}";
        });
        using var generator = new OpenAICompatibleEmbeddingGenerator(settings);
        var call = FailureAsync(generator);
        using var accept = CancellationTokenSource.CreateLinkedTokenSource(Cancellation);
        accept.CancelAfter(TimeSpan.FromSeconds(10));
        using (var connection = await proxy.AcceptTcpClientAsync(accept.Token))
        {
            proxy.Stop(); // a retried connection is refused at once instead of waiting for the deadline
            var buffer = new byte[4096];
            var read = await connection.GetStream().ReadAsync(buffer, accept.Token);
            // Only the TLS tunnel request is visible to the proxy: no key, no content.
            var head = Encoding.ASCII.GetString(buffer, 0, read);
            Assert.StartsWith("CONNECT embeddings.example.invalid:443 HTTP/1.1", head);
            Assert.DoesNotContain(Secret, head);
            if (answer is not null) await connection.GetStream().WriteAsync(Encoding.ASCII.GetBytes(answer), accept.Token);
        }
        var started = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(expected, await call.WaitAsync(TimeSpan.FromSeconds(10), Cancellation));
        Assert.True(started.Elapsed < TimeSpan.FromSeconds(3), $"classified after {started.Elapsed}, not from the proxy's answer");
    }

    [Fact]
    public void The_handler_ignores_environment_proxies_redirects_and_cookies()
    {
        foreach (var proxy in new string?[] { null, "" })
        {
            var handler = OpenAICompatibleEmbeddingGenerator.CreateHandler(Settings("https://embeddings.example.com/v1", change: s => s.Proxy = proxy));
            Assert.Equal((false, null, false, false, false, null, 64), (handler.UseProxy, handler.Proxy, handler.AllowAutoRedirect,
                handler.UseCookies, handler.PreAuthenticate, handler.Credentials, handler.MaxResponseHeadersLength));
        }
        var configured = OpenAICompatibleEmbeddingGenerator.CreateHandler(
            Settings("https://embeddings.example.com/v1", change: s => s.Proxy = "http://proxy.internal:3128"));
        Assert.True(configured.UseProxy);
        Assert.Equal(new Uri("http://proxy.internal:3128/"), Assert.IsType<WebProxy>(configured.Proxy).Address);
    }

    private static (EmbeddingFailure, bool) Classify(EmbeddingException error) => (error.Failure, error.Transient);

    [Fact]
    public async Task Configuration_is_validated_at_startup_and_defaults_to_disabled()
    {
        var disabled = new EmbeddingSettings().Validate();
        Assert.False(disabled.Enabled);
        var generator = new ServiceCollection().AddMaicEmbeddings(disabled).BuildServiceProvider().GetRequiredService<IEmbeddingGenerator>();
        Assert.IsType<DisabledEmbeddingGenerator>(generator);
        Assert.Equal(EmbeddingFailure.MISCONFIGURED, (await Assert.ThrowsAsync<EmbeddingException>(() =>
            generator.EmbedAsync(["x"], EmbeddingPurpose.Document, Cancellation))).Failure);

        var configured = Settings("https://embeddings.example.com/v1", 1536, s => s.AllowInsecureHttp = false);
        using (var provider = new ServiceCollection().AddMaicEmbeddings(configured).BuildServiceProvider())
            Assert.Equal(new EmbeddingSpace("test-v1", 1536), Assert.IsType<OpenAICompatibleEmbeddingGenerator>(
                provider.GetRequiredService<IEmbeddingGenerator>()).Space);
        Assert.Equal(new Uri("https://embeddings.example.com/v1/embeddings"), configured.EmbeddingsUri());
        Assert.DoesNotContain(Secret, configured.ToString());
        Assert.DoesNotContain(Secret, JsonSerializer.Serialize(configured));

        // Plain http may carry the key only to loopback or private address literals; without a key any http host is allowed.
        foreach (var endpoint in new[] { "http://localhost:11434/v1", "http://127.0.0.1:8000/v1", "http://[::1]/v1", "http://10.1.2.3/v1",
                     "http://172.16.0.9/v1", "http://172.31.255.1/v1", "http://192.168.1.20:11434/v1", "http://[fd00::5]/v1" })
            Settings(endpoint).Validate();
        Settings("http://ollama:11434/v1", change: s => s.ApiKey = null).Validate();
        Settings("https://embeddings.example.com/v1", change: s => s.Proxy = "http://proxy.internal:3128").Validate();
        Settings("http://10.0.0.1/v1", change: s => s.Proxy = "").Validate(); // an empty variable means no proxy
        Settings("https://embeddings.example.com/v1", 4096, s => s.MaxBatchSize = 255).Validate();

        foreach (var change in new Action<EmbeddingSettingsBuilder>[]
                 {
                     s => s.ApiKey = "sk-ü", s => s.ApiKey = "sk\u0001x", s => s.Endpoint = "http://embeddings.example.com/v1",
                     s => s.Endpoint = "http://ollama:11434/v1", s => s.Endpoint = "http://172.32.0.1/v1", s => s.Endpoint = "http://8.8.8.8/v1",
                     s => s.Endpoint = "http://[2001:db8::1]/v1", s => s.Endpoint = "http://169.254.169.254/v1",
                     s => { s.Endpoint = "http://10.0.0.1/v1"; s.Proxy = "http://proxy.internal:3128"; }, s => s.Proxy = "socks5://proxy:1080",
                     s => s.Proxy = "http://user:pw@proxy:3128", s => s.Proxy = "http://proxy:3128/path", s => s.Proxy = "not a url",
                     s => { s.MaxBatchSize = 256; s.Dimensions = 4096; }, s => { s.MaxBatchSize = 2048; s.Dimensions = 1536; }
                 })
        {
            var invalid = Settings("https://embeddings.example.com/v1", change: s => { s.Endpoint = "https://embeddings.example.com/v1"; change(s); });
            Assert.Throws<InvalidOperationException>(() => invalid.Validate());
        }

        foreach (var change in new Action<EmbeddingSettingsBuilder>[]
                 {
                     s => s.SpaceKey = null, s => s.SpaceKey = "Bad Key", s => s.SpaceKey = "disabled", s => s.Dimensions = 0, s => s.Dimensions = 5000,
                     s => s.Model = " ", s => s.Endpoint = null, s => s.Endpoint = "not a url", s => s.Endpoint = "ftp://x/v1",
                     s => { s.Endpoint = "http://x/v1"; s.AllowInsecureHttp = false; }, s => s.Endpoint = "https://user:pw@x/v1",
                     s => s.Endpoint = "https://x/v1?key=1", s => s.ApiKey = "has space", s => s.MaxBatchSize = 0,
                     s => s.MaxInputLength = 1999, s => { s.MaxInputLength = 2005; s.DocumentPrefix = "passage: "; },
                     s => s.TimeoutSeconds = 0, s => s.Provider = (EmbeddingProvider)9
                 })
        {
            var invalid = Settings("https://embeddings.example.com/v1", change: change);
            Assert.Throws<InvalidOperationException>(() => invalid.Validate());
            Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddMaicEmbeddings(invalid));
        }
    }
}
