using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MAIC.API.Identity;
using MAIC.Application.Widget;
using MAIC.Domain.Channels;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Identity;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class WidgetEndpointTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private WebApplicationFactory<Program> Factory(WidgetTestClock? clock = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ConnectionStrings:Maic", fixture.ConnectionString);
            builder.UseSetting("WidgetHttp:StartsPerIpPerMinute", "100");
            builder.UseSetting("WidgetHttp:StartRequestsPerMinute", "200");
            builder.UseSetting("WidgetHttp:MessagesPerCredentialPerMinute", "100");
            builder.UseSetting("Realtime:AllowedOrigins:0", "https://embed.example");
            if (clock is not null)
                builder.ConfigureServices(services => services.Replace(ServiceDescriptor.Singleton<TimeProvider>(clock)));
        });

    internal static HttpClient Client(WebApplicationFactory<Program> factory) => factory.CreateClient(new()
    {
        BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
    });
    internal static string Capability() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    internal static string Path(string channel) => $"/public/channels/{channel}/guest-session";

    internal static async Task<HttpResponseMessage> Send(HttpClient client, HttpMethod method, string path,
        string? capability, string? json = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (capability is not null) request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + capability);
        if (json is not null) request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        return await client.SendAsync(request, Ct);
    }

    internal static void AssertPrivate(HttpResponseMessage response)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Equal("no-referrer", Assert.Single(response.Headers.GetValues("Referrer-Policy")));
        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    private static async Task<JsonElement> Read(HttpResponseMessage response, HttpStatusCode status)
    {
        Assert.Equal(status, response.StatusCode);
        AssertPrivate(response);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return document.RootElement.Clone();
    }

    private static void Keys(JsonElement value, params string[] expected) =>
        Assert.Equal(expected.Order(), value.EnumerateObject().Select(property => property.Name).Order());

    private static string Message(Guid key, string content) => JsonSerializer.Serialize(new { clientMessageId = key, content });

    private async Task<(Workspace Workspace, User Actor, Channel Channel)> Seed()
    {
        await using var db = fixture.CreateContext();
        var workspace = new Workspace("Widget HTTP tests");
        var actor = new User();
        var channel = new Channel(workspace.Id, "Support");
        db.AddRange(workspace, actor, channel, new WorkspaceMember(workspace.Id, actor.Id, WorkspaceRole.OWNER));
        await db.SaveChangesAsync(Ct);
        return (workspace, actor, channel);
    }

    [Fact]
    public async Task Lost_session_and_message_responses_replay_with_safe_paginated_history_after_resolution()
    {
        var (workspace, actor, channel) = await Seed();
        await using var factory = Factory();
        using var client = Client(factory);
        var path = Path(channel.PublishableToken);
        var capability = Capability();
        using var created = await Send(client, HttpMethod.Post, path, capability, "{}");
        var session = await Read(created, HttpStatusCode.Created);
        Keys(session, "conversationId", "status", "expiresAt");
        Assert.Equal("OPEN", session.GetProperty("status").GetString());
        var conversation = session.GetProperty("conversationId").GetGuid();
        using var restarted = await Send(client, HttpMethod.Post, path, capability, "{}");
        Assert.Equal(session.GetRawText(), (await Read(restarted, HttpStatusCode.OK)).GetRawText());
        using var resumed = await Send(client, HttpMethod.Get, path, capability);
        Assert.Equal(session.GetRawText(), (await Read(resumed, HttpStatusCode.OK)).GetRawText());

        var key = Guid.NewGuid();
        const string text = "<img src=x onerror=alert(1)>\nGuest text";
        using var sent = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, text));
        var message = await Read(sent, HttpStatusCode.Created);
        Keys(message, "id", "clientMessageId", "sequence", "senderKind", "content", "createdAt");
        Assert.Equal(key, message.GetProperty("clientMessageId").GetGuid());
        Assert.Equal("GUEST", message.GetProperty("senderKind").GetString());
        Assert.Equal(text, message.GetProperty("content").GetString());
        Assert.Equal(1, message.GetProperty("sequence").GetInt64());
        using var retried = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, text));
        Assert.Equal(message.GetRawText(), (await Read(retried, HttpStatusCode.OK)).GetRawText());
        using var changed = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, "Changed"));
        Assert.Equal(HttpStatusCode.Conflict, changed.StatusCode);

        var memberKey = Guid.NewGuid();
        await using (var db = fixture.CreateContext())
        {
            var member = new ConversationStore(db);
            await member.AppendAsync(actor.Id, workspace.Id, conversation, memberKey, "Member reply", Ct);
            await member.ResolveAsync(actor.Id, workspace.Id, conversation, Ct);
        }
        using var first = await Send(client, HttpMethod.Get, path + "/messages?afterSequence=0&take=1", capability);
        var firstPage = await Read(first, HttpStatusCode.OK);
        Keys(firstPage, "status", "messages", "nextAfterSequence");
        Assert.Equal("RESOLVED", firstPage.GetProperty("status").GetString());
        Assert.Equal(message.GetRawText(), Assert.Single(firstPage.GetProperty("messages").EnumerateArray()).GetRawText());
        Assert.Equal(1, firstPage.GetProperty("nextAfterSequence").GetInt64());
        using var second = await Send(client, HttpMethod.Get, path + "/messages?afterSequence=1&take=1", capability);
        var secondPage = await Read(second, HttpStatusCode.OK);
        var reply = Assert.Single(secondPage.GetProperty("messages").EnumerateArray());
        Keys(reply, "id", "clientMessageId", "sequence", "senderKind", "content", "createdAt");
        Assert.Equal(JsonValueKind.Null, reply.GetProperty("clientMessageId").ValueKind);
        Assert.Equal("MEMBER", reply.GetProperty("senderKind").GetString());
        Assert.Equal("Member reply", reply.GetProperty("content").GetString());
        Assert.Equal(JsonValueKind.Null, secondPage.GetProperty("nextAfterSequence").ValueKind);
        using var resolvedReplay = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, text));
        Assert.Equal(message.GetRawText(), (await Read(resolvedReplay, HttpStatusCode.OK)).GetRawText());
        using var closed = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(Guid.NewGuid(), "Fresh"));
        Assert.Equal(HttpStatusCode.Conflict, closed.StatusCode);

        await using var verify = fixture.CreateContext();
        Assert.Single(await verify.GuestSessions.Where(s => s.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Single(await verify.Contacts.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Single(await verify.Conversations.Where(c => c.WorkspaceId == workspace.Id).ToListAsync(Ct));
        Assert.Equal(2, await verify.Messages.CountAsync(m => m.ConversationId == conversation, Ct));
    }

    [Fact]
    public async Task Capabilities_cannot_cross_channels_workspaces_guests_or_member_and_realtime_APIs()
    {
        var (workspace, _, channel) = await Seed();
        var (_, _, foreign) = await Seed();
        var sibling = new Channel(workspace.Id, "Sibling");
        await using (var db = fixture.CreateContext()) { db.Add(sibling); await db.SaveChangesAsync(Ct); }
        await using var factory = Factory();
        using var client = Client(factory);
        var capability = Capability();
        var path = Path(channel.PublishableToken);
        using var start = await Send(client, HttpMethod.Post, path, capability, "{}");
        var conversation = (await Read(start, HttpStatusCode.Created)).GetProperty("conversationId").GetGuid();
        var key = Guid.NewGuid();
        using var sent = await Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, "Private guest text"));
        Assert.Equal(HttpStatusCode.Created, sent.StatusCode);
        foreach (var other in new[] { sibling, foreign })
        {
            await AssertDenied(client, Path(other.PublishableToken), capability, HttpStatusCode.Unauthorized, key);
        }
        await AssertDenied(client, path, Capability(), HttpStatusCode.Unauthorized, key, includeStart: false);
        await AssertDenied(client, Path(Capability()), capability, HttpStatusCode.NotFound, key);

        var secondCapability = Capability();
        using var secondStart = await Send(client, HttpMethod.Post, path, secondCapability, "{}");
        var second = await Read(secondStart, HttpStatusCode.Created);
        Assert.NotEqual(conversation, second.GetProperty("conversationId").GetGuid());
        using var secondHistory = await Send(client, HttpMethod.Get, path + "/messages", secondCapability);
        Assert.Empty((await Read(secondHistory, HttpStatusCode.OK)).GetProperty("messages").EnumerateArray());
        using var secondMessage = await Send(client, HttpMethod.Post, path + "/messages", secondCapability, Message(key, "Other guest"));
        Assert.Equal(HttpStatusCode.Created, secondMessage.StatusCode);
        using var originalHistory = await Send(client, HttpMethod.Get, path + "/messages", capability);
        Assert.Equal("Private guest text", Assert.Single((await Read(originalHistory, HttpStatusCode.OK))
            .GetProperty("messages").EnumerateArray()).GetProperty("content").GetString());

        foreach (var memberPath in new[] { "/auth/me", $"/workspaces/{workspace.Id}/conversations",
            $"/workspaces/{workspace.Id}/conversations/{conversation}",
            $"/workspaces/{workspace.Id}/conversations/{conversation}/messages" })
        {
            using var denied = await Send(client, HttpMethod.Get, memberPath, capability);
            Assert.Equal(HttpStatusCode.Unauthorized, denied.StatusCode);
        }
        using var ai = await Send(client, HttpMethod.Post,
            $"/workspaces/{workspace.Id}/conversations/{conversation}/ai-executions", capability, "{}");
        Assert.Equal(HttpStatusCode.Unauthorized, ai.StatusCode);
        using var realtimeRequest = new HttpRequestMessage(HttpMethod.Get, "/realtime/conversations");
        realtimeRequest.Headers.Add("Origin", "https://embed.example");
        realtimeRequest.Headers.Add("Authorization", "Bearer " + capability);
        using var realtime = await client.SendAsync(realtimeRequest, Ct);
        Assert.Equal(HttpStatusCode.Unauthorized, realtime.StatusCode);
    }

    [Fact]
    public async Task Real_member_cookie_does_not_grant_guest_access_or_change_guest_provenance()
    {
        var (workspace, actor, channel) = await Seed();
        await using var factory = Factory();
        using var client = Client(factory);
        using (var scope = factory.Services.CreateScope())
        {
            var context = new DefaultHttpContext { RequestServices = scope.ServiceProvider };
            context.Request.Scheme = "https";
            var principal = new ClaimsPrincipal(new ClaimsIdentity([
                new Claim(HttpCurrentUser.UserIdClaim, actor.Id.ToString()),
                new Claim(ClaimTypes.NameIdentifier, actor.Id.ToString())], IdentitySetup.SessionScheme));
            await context.SignInAsync(IdentitySetup.SessionScheme, principal,
                new AuthenticationProperties { ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1) });
            client.DefaultRequestHeaders.Add("Cookie", Assert.Single(context.Response.Headers.SetCookie)!.Split(';')[0]);
        }
        using var me = await client.GetAsync("/auth/me", Ct);
        Assert.Equal(HttpStatusCode.OK, me.StatusCode);
        var path = Path(channel.PublishableToken);
        await AssertDenied(client, path, null, HttpStatusCode.Unauthorized, Guid.NewGuid());
        var capability = Capability();
        using var started = await Send(client, HttpMethod.Post, path, capability, "{}");
        var conversation = (await Read(started, HttpStatusCode.Created)).GetProperty("conversationId").GetGuid();
        await using var db = fixture.CreateContext();
        var stored = await db.Conversations.SingleAsync(c => c.Id == conversation, Ct);
        Assert.Equal(workspace.Id, stored.WorkspaceId);
        Assert.Null(stored.CreatedByUserId);
        Assert.NotNull(stored.CreatedByGuestSessionId);
    }

    [Fact]
    public async Task Revoked_expired_and_disable_generation_capabilities_cannot_be_reused()
    {
        var (workspace, actor, channel) = await Seed();
        var clock = new WidgetTestClock();
        await using var factory = Factory(clock);
        using var client = Client(factory);
        var path = Path(channel.PublishableToken);
        var revoked = Capability();
        using var first = await Send(client, HttpMethod.Post, path, revoked, "{}");
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            using var response = await Send(client, HttpMethod.Delete, path, revoked);
            Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            AssertPrivate(response);
        }
        await AssertDenied(client, path, revoked, HttpStatusCode.Unauthorized, Guid.NewGuid(), includeDelete: false);
        var expired = Capability();
        using var expiring = await Send(client, HttpMethod.Post, path, expired, "{}");
        clock.Now = (await Read(expiring, HttpStatusCode.Created)).GetProperty("expiresAt").GetDateTimeOffset();
        await AssertDenied(client, path, expired, HttpStatusCode.Unauthorized, Guid.NewGuid());

        var invalidated = Capability();
        using var live = await Send(client, HttpMethod.Post, path, invalidated, "{}");
        Assert.Equal(HttpStatusCode.Created, live.StatusCode);
        await using (var db = fixture.CreateContext())
            await new ChannelStore(db).UpdateAsync(actor.Id, workspace.Id, channel.Id, null, false, Ct);
        await AssertDenied(client, path, invalidated, HttpStatusCode.NotFound, Guid.NewGuid());
        using var bootstrap = await client.GetAsync($"/public/channels/{channel.PublishableToken}", Ct);
        Assert.Equal(HttpStatusCode.NotFound, bootstrap.StatusCode);
        await using (var db = fixture.CreateContext())
            await new ChannelStore(db).UpdateAsync(actor.Id, workspace.Id, channel.Id, null, true, Ct);
        await AssertDenied(client, path, invalidated, HttpStatusCode.Unauthorized, Guid.NewGuid());
        using var fresh = await Send(client, HttpMethod.Post, path, Capability(), "{}");
        Assert.Equal(HttpStatusCode.Created, fresh.StatusCode);
    }

    [Fact]
    public async Task Invalid_headers_URL_credentials_unknown_bindings_and_cursor_values_fail_closed()
    {
        var (workspace, _, channel) = await Seed();
        await using var factory = Factory();
        using var client = Client(factory);
        var path = Path(channel.PublishableToken);
        var capability = Capability();
        foreach (var invalid in new[] { "", "bad", capability + "=", new string('A', 42) + "B", capability + " ", " " + capability })
        {
            // HttpClient normalizes Authorization whitespace; set the raw header to exercise the server parser.
            using var body = new MemoryStream("{}"u8.ToArray());
            var context = await factory.Server.SendAsync(context =>
            {
                context.Request.Method = HttpMethods.Post;
                context.Request.Scheme = "https";
                context.Request.Path = path;
                context.Request.Headers.Authorization = "Bearer " + invalid;
                context.Request.ContentType = "application/json";
                context.Request.ContentLength = body.Length;
                context.Request.Body = body;
            }, Ct);
            Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
            Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
            Assert.Equal("no-referrer", context.Response.Headers["Referrer-Policy"].ToString());
            Assert.False(context.Response.Headers.ContainsKey("Set-Cookie"));
        }
        using (var request = new HttpRequestMessage(HttpMethod.Get, path))
        {
            request.Headers.TryAddWithoutValidation("Authorization", new[] { "Bearer " + capability, "Bearer " + capability });
            using var response = await client.SendAsync(request, Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        }
        foreach (var field in new[] { "workspaceId", "contactId", "conversationId", "guestSessionId", "userId", "channelId", "access_token" })
        {
            using var url = await Send(client, HttpMethod.Post, path + $"?{field}={capability}", null, "{}");
            Assert.Equal(HttpStatusCode.BadRequest, url.StatusCode);
            using var start = await Send(client, HttpMethod.Post, path, capability,
                JsonSerializer.Serialize(new Dictionary<string, object> { [field] = Guid.NewGuid() }));
            Assert.Equal(HttpStatusCode.BadRequest, start.StatusCode);
            using var message = await Send(client, HttpMethod.Post, path + "/messages", capability,
                JsonSerializer.Serialize(new Dictionary<string, object> { ["clientMessageId"] = Guid.NewGuid(), ["content"] = "Forged", [field] = Guid.NewGuid() }));
            Assert.Equal(HttpStatusCode.BadRequest, message.StatusCode);
        }
        foreach (var query in new[] { "afterSequence=-1", "take=0", "take=101", "afterSequence=9223372036854775808", "take=no", "access_token=" + capability })
        {
            using var response = await Send(client, HttpMethod.Get, path + "/messages?" + query, capability);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            AssertPrivate(response);
        }
        await using var db = fixture.CreateContext();
        Assert.False(await db.GuestSessions.AnyAsync(s => s.WorkspaceId == workspace.Id, Ct));
        Assert.False(await db.Contacts.AnyAsync(c => c.WorkspaceId == workspace.Id, Ct));
    }

    [Fact]
    public async Task Concurrent_HTTP_retries_create_one_session_and_one_message()
    {
        var (workspace, _, channel) = await Seed();
        await using var factory = Factory();
        using var client = Client(factory);
        var path = Path(channel.PublishableToken);
        var capability = Capability();
        var starts = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Send(client, HttpMethod.Post, path, capability, "{}")));
        try
        {
            Assert.Single(starts, r => r.StatusCode == HttpStatusCode.Created);
            Assert.Equal(3, starts.Count(r => r.StatusCode == HttpStatusCode.OK));
            var sessions = await Task.WhenAll(starts.Select(async r => (await Read(r, r.StatusCode)).GetProperty("conversationId").GetGuid()));
            Assert.Single(sessions.Distinct());
            var key = Guid.NewGuid();
            var messages = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ =>
                Send(client, HttpMethod.Post, path + "/messages", capability, Message(key, "One logical send"))));
            try
            {
                Assert.Single(messages, r => r.StatusCode == HttpStatusCode.Created);
                Assert.Equal(3, messages.Count(r => r.StatusCode == HttpStatusCode.OK));
                var ids = await Task.WhenAll(messages.Select(async r => (await Read(r, r.StatusCode)).GetProperty("id").GetGuid()));
                Assert.Single(ids.Distinct());
            }
            finally { foreach (var response in messages) response.Dispose(); }
            await using var db = fixture.CreateContext();
            Assert.Equal(1, await db.Conversations.CountAsync(c => c.WorkspaceId == workspace.Id, Ct));
            Assert.Equal(1, await db.Contacts.CountAsync(c => c.WorkspaceId == workspace.Id, Ct));
            Assert.Equal(1, await db.Messages.CountAsync(m => m.WorkspaceId == workspace.Id, Ct));
        }
        finally { foreach (var response in starts) response.Dispose(); }
    }

    private static async Task AssertDenied(HttpClient client, string path, string? capability, HttpStatusCode status,
        Guid key, bool includeStart = true, bool includeDelete = true)
    {
        var requests = new List<(HttpMethod Method, string Path, string? Body)>
        {
            (HttpMethod.Get, path, null), (HttpMethod.Get, path + "/messages", null),
            (HttpMethod.Post, path + "/messages", Message(key, "Rejected"))
        };
        if (includeStart) requests.Add((HttpMethod.Post, path, "{}"));
        if (includeDelete) requests.Add((HttpMethod.Delete, path, null));
        foreach (var (method, endpoint, body) in requests)
        {
            using var response = await Send(client, method, endpoint, capability, body);
            Assert.Equal(status, response.StatusCode);
            AssertPrivate(response);
        }
    }
}

internal sealed class WidgetTestClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = new(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => Now;
}
