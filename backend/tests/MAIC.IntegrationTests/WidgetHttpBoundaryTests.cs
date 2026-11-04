using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using MAIC.API.Widget;
using MAIC.Domain.Channels;
using MAIC.Domain.Workspaces;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static MAIC.IntegrationTests.WidgetEndpointTests;

namespace MAIC.IntegrationTests;

public sealed class WidgetHttpBoundaryTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;
    private WebApplicationFactory<Program> Factory(WidgetTestClock clock, string? limitedSetting = null, int limit = 1) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
            builder.UseSetting("WidgetHttp:StartsPerIpPerMinute", "100");
            builder.UseSetting("WidgetHttp:StartRequestsPerMinute", "100");
            builder.UseSetting("WidgetHttp:MessagesPerCredentialPerMinute", "100");
            if (limitedSetting is not null) builder.UseSetting("WidgetHttp:" + limitedSetting, limit.ToString());
            builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        });

    private async Task<Channel> Seed()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Widget boundary HTTP tests");
        var channel = new Channel(workspace.Id, "Embed support");
        db.AddRange(workspace, channel);
        await db.SaveChangesAsync(Ct);
        return channel;
    }

    [Fact]
    public async Task Arbitrary_embed_origins_get_public_CORS_without_cookie_credentials_on_success_and_errors()
    {
        var channel = await Seed();
        await using var factory = Factory(new());
        using var client = Client(factory);
        var path = Path(channel.PublishableToken);
        foreach (var origin in new[] { "https://embed.example", "https://different.example", "null" })
        {
            foreach (var method in new[] { "GET", "POST", "DELETE" })
            {
                using var request = new HttpRequestMessage(HttpMethod.Options, path);
                request.Headers.Add("Origin", origin);
                request.Headers.Add("Access-Control-Request-Method", method);
                request.Headers.Add("Access-Control-Request-Headers", "authorization,content-type");
                using var preflight = await client.SendAsync(request, Ct);
                Assert.Equal(HttpStatusCode.NoContent, preflight.StatusCode);
                AssertCors(preflight);
                Assert.Contains(method, Assert.Single(preflight.Headers.GetValues("Access-Control-Allow-Methods")));
                Assert.Contains("authorization", Assert.Single(preflight.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
                Assert.Contains("content-type", Assert.Single(preflight.Headers.GetValues("Access-Control-Allow-Headers")), StringComparison.OrdinalIgnoreCase);
            }
        }
        client.DefaultRequestHeaders.Add("Origin", "https://embed.example");
        using var bootstrap = await client.GetAsync($"/public/channels/{channel.PublishableToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        AssertCors(bootstrap);
        AssertPrivate(bootstrap);
        using var unauthorized = await Send(client, HttpMethod.Get, path, null);
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        AssertCors(unauthorized);
        AssertPrivate(unauthorized);
        using var invalid = await Send(client, HttpMethod.Post, path, Capability(), "{\"workspaceId\":\"forged\"}");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        AssertCors(invalid);
        AssertPrivate(invalid);
        using var started = await Send(client, HttpMethod.Post, path, Capability(), "{}");
        Assert.Equal(HttpStatusCode.Created, started.StatusCode);
        AssertCors(started);
        AssertPrivate(started);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Byte_limits_accept_exact_boundary_and_reject_oversized_known_and_unknown_length_bodies(bool messages, bool unknownLength)
    {
        var channel = await Seed();
        await using var factory = Factory(new());
        using var client = Client(factory);
        var capability = Capability();
        var path = Path(channel.PublishableToken);
        if (messages)
        {
            using var start = await Send(client, HttpMethod.Post, path, capability, "{}");
            Assert.Equal(HttpStatusCode.Created, start.StatusCode);
            path += "/messages";
        }
        var limit = messages ? 32 * 1024 : 1024;
        var body = messages ? JsonSerializer.Serialize(new { clientMessageId = Guid.NewGuid(), content = "Bounded" }) : "{}";
        var exact = Encoding.UTF8.GetBytes(body.PadRight(limit));
        using var accepted = await SendBody(exact);
        Assert.Equal(HttpStatusCode.Created, accepted.StatusCode);
        AssertPrivate(accepted);
        using var rejected = await SendBody(Encoding.UTF8.GetBytes(body.PadRight(limit + 1)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, rejected.StatusCode);
        AssertPrivate(rejected);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.GuestSessions.CountAsync(s => s.ChannelId == channel.Id, Ct));
        Assert.Equal(messages ? 1 : 0, await db.Messages.CountAsync(m => m.WorkspaceId == channel.WorkspaceId, Ct));

        async Task<HttpResponseMessage> SendBody(byte[] bytes)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, path);
            request.Headers.Add("Authorization", "Bearer " + capability);
            request.Content = unknownLength ? new UnknownLengthContent(bytes) : new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            Assert.Equal(unknownLength ? null : (long?)bytes.Length, request.Content.Headers.ContentLength);
            return await client.SendAsync(request, Ct);
        }
    }

    [Theory]
    [InlineData("StartsPerIpPerMinute")]
    [InlineData("StartRequestsPerMinute")]
    public async Task Session_creation_admission_counts_retries_and_recovers_at_next_window(string setting)
    {
        var channel = await Seed();
        var clock = new WidgetTestClock();
        await using var factory = Factory(clock, setting);
        using var client = Client(factory);
        client.DefaultRequestHeaders.Add("Origin", "https://embed.example");
        var path = Path(channel.PublishableToken);
        var capability = Capability();
        using var first = await Send(client, HttpMethod.Post, path, capability, "{}");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var replay = await Send(client, HttpMethod.Post, path, capability, "{}");
        AssertLimited(replay);
        AssertCors(replay);
        Assert.Contains("Retry-After", Assert.Single(replay.Headers.GetValues("Access-Control-Expose-Headers")), StringComparison.OrdinalIgnoreCase);
        using var newCredential = await Send(client, HttpMethod.Post, path, Capability(), "{}");
        AssertLimited(newCredential);
        clock.Now = clock.Now.AddMinutes(1);
        using var recovered = await Send(client, HttpMethod.Post, path, capability, "{}");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.GuestSessions.CountAsync(s => s.ChannelId == channel.Id, Ct));
    }

    [Fact]
    public async Task Message_admission_counts_retries_and_recovers_at_next_window()
    {
        var channel = await Seed();
        var clock = new WidgetTestClock();
        await using var factory = Factory(clock, "MessagesPerCredentialPerMinute");
        using var client = Client(factory);
        var capability = Capability();
        var path = Path(channel.PublishableToken);
        using var start = await Send(client, HttpMethod.Post, path, capability, "{}");
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        var json = JsonSerializer.Serialize(new { clientMessageId = Guid.NewGuid(), content = "Once" });
        using var first = await Send(client, HttpMethod.Post, path + "/messages", capability, json);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var limited = await Send(client, HttpMethod.Post, path + "/messages", capability, json);
        AssertLimited(limited);
        clock.Now = clock.Now.AddMinutes(1);
        using var replay = await Send(client, HttpMethod.Post, path + "/messages", capability, json);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Messages.CountAsync(m => m.WorkspaceId == channel.WorkspaceId, Ct));
    }

    [Fact]
    public async Task Public_bootstrap_and_guest_requests_share_global_window_and_concurrency_admission()
    {
        var channel = await Seed();
        var clock = new WidgetTestClock();
        await using var factory = Factory(clock, "RequestsPerMinute", 2);
        using var client = Client(factory);
        using var bootstrap = await client.GetAsync($"/public/channels/{channel.PublishableToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, bootstrap.StatusCode);
        var capability = Capability();
        var path = Path(channel.PublishableToken);
        using var start = await Send(client, HttpMethod.Post, path, capability, "{}");
        Assert.Equal(HttpStatusCode.Created, start.StatusCode);
        using var blocked = await Send(client, HttpMethod.Get, path, capability);
        AssertLimited(blocked);
        clock.Now = clock.Now.AddMinutes(1);
        using var recovered = await Send(client, HttpMethod.Get, path, capability);
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);

        await using var concurrentFactory = Factory(clock, "ConcurrentRequests");
        using var concurrentClient = Client(concurrentFactory);
        var limiter = concurrentFactory.Services.GetRequiredService<WidgetRateLimiter>();
        using (var held = limiter.TryEnter())
        {
            Assert.NotNull(held);
            using var rejected = await concurrentClient.GetAsync($"/public/channels/{channel.PublishableToken}", Ct);
            AssertLimited(rejected);
        }
        using var released = await concurrentClient.GetAsync($"/public/channels/{channel.PublishableToken}", Ct);
        Assert.Equal(HttpStatusCode.OK, released.StatusCode);
        // Failure paths must release the same single admission lease.
        using var malformed = await Send(concurrentClient, HttpMethod.Post, path, capability, "{");
        Assert.Equal(HttpStatusCode.BadRequest, malformed.StatusCode);
        using var afterError = await Send(concurrentClient, HttpMethod.Get, path, capability);
        Assert.Equal(HttpStatusCode.OK, afterError.StatusCode);
    }

    private static void AssertLimited(HttpResponseMessage response)
    {
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.Equal(TimeSpan.FromSeconds(60), response.Headers.RetryAfter!.Delta);
        AssertPrivate(response);
    }

    private static void AssertCors(HttpResponseMessage response)
    {
        Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
        Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes, Ct).AsTask();
    }
}
