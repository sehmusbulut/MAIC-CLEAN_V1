using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using MAIC.API.Identity;
using MAIC.API.Realtime;
using MAIC.Application.Conversations;
using MAIC.Application.Workspaces;
using MAIC.Domain.Identity;
using MAIC.Domain.Workspaces;
using MAIC.Infrastructure.Persistence;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using Xunit;

namespace MAIC.IntegrationTests;

// Exercise the actual cookie handler, antiforgery service and SignalR WebSocket protocol.
// Test authentication headers and direct hub calls would bypass the boundaries under test.
public sealed partial class RealtimeEndpointTests(PostgresFixture postgres) : IClassFixture<PostgresFixture>
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Committed_http_append_is_metadata_only_retry_is_silent_and_reconnect_resynchronizes()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var socket = await Wire.ConnectAsync(factory, browser);
        await socket.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        var initial = await socket.FrameAsync();
        Assert.True(initial.GetProperty("resyncRequired").GetBoolean());
        Assert.Equal(seed.Message.Sequence, initial.GetProperty("lastMessageSequence").GetInt64());
        AssertMetadata(initial, seed);

        var messageId = Guid.NewGuid();
        const string body = "Private message body must remain in authorized REST history.";
        using (var append = await browser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = messageId, content = body }, Ct))
            Assert.Equal(HttpStatusCode.Created, append.StatusCode);
        var committed = await socket.FrameUntilAsync(frame =>
            frame.GetProperty("lastMessageSequence").GetInt64() == seed.Message.Sequence + 1);
        Assert.False(committed.GetProperty("resyncRequired").GetBoolean());
        Assert.Contains("messages", Changes(committed));
        Assert.DoesNotContain(body, committed.GetRawText());
        AssertMetadata(committed, seed);

        using (var retry = await browser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = messageId, content = body }, Ct))
            Assert.Equal(HttpStatusCode.OK, retry.StatusCode);
        await socket.ObserveAsync(TimeSpan.FromMilliseconds(2200), frame =>
        {
            Assert.DoesNotContain("messages", Changes(frame));
            Assert.Equal(seed.Message.Sequence + 1, frame.GetProperty("lastMessageSequence").GetInt64());
        });
        socket.Dispose();

        // A committed write while disconnected is recovered by the next authoritative snapshot.
        using (var missed = await browser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = Guid.NewGuid(), content = "Committed while disconnected" }, Ct))
            Assert.Equal(HttpStatusCode.Created, missed.StatusCode);
        using var reconnected = await Wire.ConnectAsync(factory, browser);
        await reconnected.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        var resync = await reconnected.FrameAsync();
        Assert.True(resync.GetProperty("resyncRequired").GetBoolean());
        Assert.Equal(seed.Message.Sequence + 2, resync.GetProperty("lastMessageSequence").GetInt64());
        AssertMetadata(resync, seed);
    }

    [Theory]
    [InlineData("foreign-workspace")]
    [InlineData("foreign-conversation")]
    [InlineData("mixed-workspace")]
    [InlineData("missing-conversation")]
    public async Task Watch_rejects_foreign_and_mixed_tenant_identifiers_without_an_item(string target)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var foreign = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var socket = await Wire.ConnectAsync(factory, browser);
        var workspaceId = target is "foreign-workspace" or "mixed-workspace" ? foreign.Workspace.Id : seed.Workspace.Id;
        var conversationId = target switch
        {
            "foreign-workspace" or "foreign-conversation" => foreign.Conversation.Id,
            "missing-conversation" => Guid.NewGuid(),
            _ => seed.Conversation.Id
        };
        await socket.WatchAsync(workspaceId, conversationId, browser.CsrfToken);
        await socket.DeniedAsync();
    }

    [Theory]
    [InlineData("missing-token")]
    [InlineData("invalid-token")]
    [InlineData("missing-cookie")]
    [InlineData("another-user")]
    public async Task Watch_requires_the_cookie_and_user_bound_csrf_token(string fault)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        var cookies = browser.Cookies;
        string? token = browser.CsrfToken;
        if (fault == "missing-token") token = null;
        if (fault == "invalid-token") token = "invalid-csrf-token";
        if (fault == "missing-cookie") cookies = browser.SessionCookie;
        if (fault == "another-user")
        {
            var foreign = await AiExecutionPersistenceSeed.CreateAsync(postgres);
            using var otherBrowser = await Browser.CreateAsync(factory, foreign.Actor.Id);
            cookies = browser.SessionCookie + "; " + otherBrowser.CsrfCookie;
            token = otherBrowser.CsrfToken;
        }
        using var socket = await Wire.ConnectAsync(factory, browser, cookies: cookies);
        await socket.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, token);
        await socket.DeniedAsync();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("https://hostile.example")]
    [InlineData("https://localhost.hostile.example")]
    [InlineData("https://localhost/")]
    [InlineData("multiple")]
    public async Task Direct_websocket_upgrade_requires_one_exact_allowed_origin(string origin)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        string[] origins = origin switch
        {
            "missing" => [],
            "multiple" => ["https://localhost", "https://hostile.example"],
            _ => [origin]
        };
        var error = await Record.ExceptionAsync(async () =>
        {
            using var socket = await Wire.ConnectAsync(factory, browser, origins);
        });
        Assert.NotNull(error);
        Assert.True(error is InvalidOperationException or WebSocketException,
            $"Expected rejected WebSocket upgrade, got {error.GetType().Name}: {error.Message}");
    }

    [Fact]
    public async Task Direct_websocket_upgrade_without_the_real_session_cookie_is_unauthorized()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        var error = await Record.ExceptionAsync(async () =>
        {
            using var socket = await Wire.ConnectAsync(factory, browser, cookies: string.Empty);
        });
        Assert.NotNull(error);
        Assert.True(error is InvalidOperationException or WebSocketException,
            $"Expected rejected WebSocket upgrade, got {error.GetType().Name}: {error.Message}");
    }

    [Theory]
    [InlineData("SetTyping")]
    [InlineData("Heartbeat")]
    public async Task Transient_invocation_before_a_valid_watch_is_rejected(string method)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var socket = await Wire.ConnectAsync(factory, browser);
        await socket.InvokeAsync(method, "before-watch", method == "SetTyping" ? [true] : []);
        await socket.DeniedAsync();
    }

    [Fact]
    public async Task A_second_watch_cannot_change_the_connections_authorized_subscription()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var secondConversation = await AiExecutionPersistenceSeed.CreateAsync(postgres, seed);
        using var factory = Factory();
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var socket = await Wire.ConnectAsync(factory, browser);
        await socket.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        AssertMetadata(await socket.FrameAsync(), seed);
        await socket.WatchAsync(seed.Workspace.Id, secondConversation.Conversation.Id, browser.CsrfToken, "second-watch");
        await socket.TerminatedAsync(record => Assert.NotEqual(2, record.GetProperty("type").GetInt32()));
    }

    [Theory]
    [InlineData("logout", false)]
    [InlineData("logout", true)]
    [InlineData("removed-member", false)]
    [InlineData("removed-member", true)]
    public async Task Revocation_stops_existing_stream_and_presence_and_rejects_further_typing(string reason, bool invokeAfterRevocation)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var agent = await AddAgentAsync(seed.Workspace.Id);
        using var factory = Factory();
        using var ownerBrowser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var agentBrowser = await Browser.CreateAsync(factory, agent.Id);
        using var observer = await Wire.ConnectAsync(factory, ownerBrowser);
        await observer.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, ownerBrowser.CsrfToken);
        await observer.FrameAsync();
        using var subject = await Wire.ConnectAsync(factory, agentBrowser);
        await subject.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, agentBrowser.CsrfToken);
        await subject.FrameAsync();
        await subject.InvokeAndCompleteAsync("SetTyping", "typing", true);
        await observer.FrameUntilAsync(frame => IsTyping(frame, agent.Id));

        if (reason == "logout")
        {
            // A distinct HTTP request invalidates the server ticket captured by an existing WebSocket.
            using var logout = await agentBrowser.Client.PostAsync("/auth/logout", null, Ct);
            Assert.Equal(HttpStatusCode.NoContent, logout.StatusCode);
            using var replay = await agentBrowser.Client.GetAsync("/auth/me", Ct);
            Assert.Equal(HttpStatusCode.Unauthorized, replay.StatusCode);
        }
        else
        {
            await using var db = postgres.CreateContext();
            Assert.Equal(MembershipStatus.Success, (await new MembershipStore(db).RemoveAsync(
                seed.Actor.Id, seed.Workspace.Id, agent.Id, Ct)).Status);
        }

        using (var append = await ownerBrowser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = Guid.NewGuid(), content = "Committed after revocation" }, Ct))
            Assert.Equal(HttpStatusCode.Created, append.StatusCode);
        if (invokeAfterRevocation)
            await subject.TryInvokeAsync("SetTyping", "after-revocation", true);
        await subject.TerminatedAsync(record =>
        {
            if (record.GetProperty("type").GetInt32() == 2)
            {
                // An already-authorized in-flight frame is permitted; a later committed write is not.
                Assert.True(record.GetProperty("item").GetProperty("lastMessageSequence").GetInt64() <= seed.Message.Sequence);
            }
            if (record.GetProperty("type").GetInt32() == 3
                && record.GetProperty("invocationId").GetString() == "after-revocation")
                Assert.True(record.TryGetProperty("error", out _), "Typing succeeded after revocation.");
        });
        var remaining = await observer.FrameUntilAsync(frame =>
            frame.GetProperty("lastMessageSequence").GetInt64() == seed.Message.Sequence + 1
            && !Presence(frame).Any(person => person.GetProperty("userId").GetGuid() == agent.Id));
        Assert.Equal(seed.Actor.Id, Assert.Single(Presence(remaining)).GetProperty("userId").GetGuid());
    }

    [Fact]
    public async Task Multiple_connections_aggregate_presence_and_disconnect_clears_only_that_connections_typing()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var agent = await AddAgentAsync(seed.Workspace.Id);
        using var factory = Factory();
        using var ownerBrowser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var agentBrowser = await Browser.CreateAsync(factory, agent.Id);
        using var observer = await Wire.ConnectAsync(factory, ownerBrowser);
        await observer.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, ownerBrowser.CsrfToken);
        await observer.FrameAsync();
        using var first = await Wire.ConnectAsync(factory, agentBrowser);
        await first.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, agentBrowser.CsrfToken);
        await first.FrameAsync();
        using var second = await Wire.ConnectAsync(factory, agentBrowser);
        await second.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, agentBrowser.CsrfToken);
        var secondInitial = await second.FrameAsync();
        Assert.Equal(2, Presence(secondInitial).Length);
        Assert.Single(Presence(secondInitial), person => person.GetProperty("userId").GetGuid() == agent.Id);

        await first.InvokeAndCompleteAsync("SetTyping", "typing", true);
        var typing = await observer.FrameUntilAsync(frame => IsTyping(frame, agent.Id));
        Assert.Single(Presence(typing), person => person.GetProperty("userId").GetGuid() == agent.Id);
        Assert.Equal(new[] { "presence" }, Changes(typing));

        first.Dispose();
        var stillPresent = await observer.FrameUntilAsync(frame => Presence(frame).Length == 2 && !IsTyping(frame, agent.Id));
        Assert.Equal(new[] { "presence" }, Changes(stillPresent));
        second.Dispose();
        var absent = await observer.FrameUntilAsync(frame => Presence(frame).Length == 1);
        Assert.Equal(seed.Actor.Id, Assert.Single(Presence(absent)).GetProperty("userId").GetGuid());
        Assert.Equal(new[] { "presence" }, Changes(absent));
        Assert.Equal(seed.Message.Sequence, absent.GetProperty("lastMessageSequence").GetInt64());
        await using var db = postgres.CreateContext();
        Assert.Equal(1, await db.Messages.CountAsync(message => message.ConversationId == seed.Conversation.Id, Ct));
        Assert.False(await db.ConversationAssignments.AnyAsync(item => item.ConversationId == seed.Conversation.Id, Ct));
        Assert.False(await db.HandoffRequests.AnyAsync(item => item.ConversationId == seed.Conversation.Id, Ct));
    }

    [Fact]
    public async Task Nonreading_connection_lifetime_releases_capacity_and_reconnect_resynchronizes()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var time = new ControlledLifetimeTimeProvider();
        using var factory = Factory(time, globalConnectionLimit: 1);
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var stalled = await Wire.ConnectAsync(factory, browser);
        await stalled.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        Assert.True((await stalled.FrameAsync()).GetProperty("resyncRequired").GetBoolean());
        var registry = factory.Services.GetRequiredService<RealtimeRegistry>();
        var original = Assert.Single(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));

        // No further receives on this socket until after cancellation and registry removal.
        // This exercises a nonreading client, without claiming the transport buffer is saturated.
        using (var missed = await browser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = Guid.NewGuid(), content = "Committed while the client stopped reading" }, Ct))
            Assert.Equal(HttpStatusCode.Created, missed.StatusCode);
        Assert.Equal(1, time.ExpireConnectionLifetimes());
        Assert.Null(registry.Get(original.ConnectionId));
        Assert.Empty(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));
        await stalled.TerminatedAsync(_ => { });

        // The configured global cap is one: a fresh watch proves the old capacity was released.
        using var replacement = await ConnectAfterAdmissionReleaseAsync(factory, browser);
        await replacement.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        var resync = await replacement.FrameAsync();
        Assert.True(resync.GetProperty("resyncRequired").GetBoolean());
        Assert.Equal(seed.Message.Sequence + 1, resync.GetProperty("lastMessageSequence").GetInt64());
        AssertMetadata(resync, seed);
        Assert.NotEqual(original.ConnectionId,
            Assert.Single(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id)).ConnectionId);
    }

    [Fact]
    public async Task Cancel_invocation_ends_watch_releases_capacity_and_reconnect_resynchronizes()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory(globalConnectionLimit: 1);
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var canceled = await Wire.ConnectAsync(factory, browser);
        await canceled.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        await canceled.FrameAsync();
        var registry = factory.Services.GetRequiredService<RealtimeRegistry>();
        var original = Assert.Single(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));

        await canceled.CancelWatchAsync();
        await canceled.TerminatedAsync(_ => { });
        Assert.Null(registry.Get(original.ConnectionId));
        Assert.Empty(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));
        using (var missed = await browser.Client.PostAsJsonAsync(MessagesPath(seed),
                   new { clientMessageId = Guid.NewGuid(), content = "Committed after stream cancellation" }, Ct))
            Assert.Equal(HttpStatusCode.Created, missed.StatusCode);

        using var replacement = await ConnectAfterAdmissionReleaseAsync(factory, browser);
        await replacement.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        var resync = await replacement.FrameAsync();
        Assert.True(resync.GetProperty("resyncRequired").GetBoolean());
        Assert.Equal(seed.Message.Sequence + 1, resync.GetProperty("lastMessageSequence").GetInt64());
        AssertMetadata(resync, seed);
        Assert.NotEqual(original.ConnectionId,
            Assert.Single(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id)).ConnectionId);
    }

    private async Task<User> AddAgentAsync(Guid workspaceId)
    {
        await using var db = postgres.CreateContext();
        var agent = new User();
        db.AddRange(agent, new WorkspaceMember(workspaceId, agent.Id, WorkspaceRole.AGENT));
        await db.SaveChangesAsync(Ct);
        return agent;
    }

    private static JsonElement[] Presence(JsonElement frame) => frame.GetProperty("presence").EnumerateArray().ToArray();

    private static bool IsTyping(JsonElement frame, Guid userId) => Presence(frame).Any(person =>
        person.GetProperty("userId").GetGuid() == userId && person.GetProperty("isTyping").GetBoolean());

    private WebApplicationFactory<Program> Factory(TimeProvider? time = null, int? globalConnectionLimit = null) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
    {
        builder.UseSetting("ConnectionStrings:Maic", postgres.ConnectionString);
        builder.UseSetting("Realtime:AllowedOrigins:0", "https://localhost");
        if (globalConnectionLimit is { } limit)
            builder.UseSetting("Realtime:GlobalConnectionLimit", limit.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (time is not null) builder.ConfigureServices(services => services.AddSingleton(time));
    });

    private static string MessagesPath(AiExecutionPersistenceSeed seed) =>
        $"/workspaces/{seed.Workspace.Id}/conversations/{seed.Conversation.Id}/messages";

    private static string[] Changes(JsonElement frame) => frame.GetProperty("changes").EnumerateArray()
        .Select(value => value.GetString()!).ToArray();

    private static void AssertMetadata(JsonElement frame, AiExecutionPersistenceSeed seed)
    {
        Assert.Equal(seed.Workspace.Id, frame.GetProperty("workspaceId").GetGuid());
        Assert.Equal(seed.Conversation.Id, frame.GetProperty("conversationId").GetGuid());
        Assert.Equal(new[] { "changes", "conversationId", "lastAssignmentSequence", "lastHandoffSequence",
            "lastMessageSequence", "presence", "resyncRequired", "revision", "workspaceId" },
            frame.EnumerateObject().Select(property => property.Name).Order());
        foreach (var participant in frame.GetProperty("presence").EnumerateArray())
            Assert.Equal(new[] { "isTyping", "userId" }, participant.EnumerateObject().Select(property => property.Name).Order());
    }

    private sealed class Browser(HttpClient client, string sessionCookie, string csrfCookie, string csrfToken) : IDisposable
    {
        public HttpClient Client { get; } = client;
        public string SessionCookie { get; } = sessionCookie;
        public string CsrfCookie { get; } = csrfCookie;
        public string CsrfToken { get; } = csrfToken;
        public string Cookies => SessionCookie + "; " + CsrfCookie;

        public static async Task<Browser> CreateAsync(WebApplicationFactory<Program> factory, Guid actor)
        {
            var options = factory.Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
                .Get(IdentitySetup.SessionScheme);
            var properties = new AuthenticationProperties
            {
                IssuedUtc = DateTimeOffset.UtcNow,
                ExpiresUtc = DateTimeOffset.UtcNow.AddHours(1)
            };
            var principal = new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim(HttpCurrentUser.UserIdClaim, actor.ToString()),
                    new Claim(ClaimTypes.NameIdentifier, actor.ToString())], IdentitySetup.SessionScheme));
            var key = await options.SessionStore!.StoreAsync(new AuthenticationTicket(principal, properties, IdentitySetup.SessionScheme));
            // CookieAuthenticationHandler's outer ticket refers to the server-stored ticket.
            var outer = new AuthenticationTicket(new ClaimsPrincipal(new ClaimsIdentity(
                [new Claim("Microsoft.AspNetCore.Authentication.Cookies-SessionId", key)], IdentitySetup.SessionScheme)),
                new AuthenticationProperties(), IdentitySetup.SessionScheme);
            var sessionCookie = options.Cookie.Name + "=" + options.TicketDataFormat.Protect(outer);
            var client = factory.CreateClient(new()
            {
                BaseAddress = new Uri("https://localhost"), AllowAutoRedirect = false, HandleCookies = false
            });
            client.DefaultRequestHeaders.Add("Cookie", sessionCookie);
            var me = await client.GetFromJsonAsync<JsonElement>("/auth/me", Ct);
            Assert.Equal(actor, me.GetProperty("userId").GetGuid());
            using var csrf = await client.GetAsync("/auth/csrf", Ct);
            Assert.Equal(HttpStatusCode.OK, csrf.StatusCode);
            var token = (await csrf.Content.ReadFromJsonAsync<JsonElement>(Ct)).GetProperty("token").GetString()!;
            var csrfCookie = Assert.Single(csrf.Headers.GetValues("Set-Cookie"),
                value => value.StartsWith("__Host-maic-csrf=", StringComparison.Ordinal)).Split(';')[0];
            client.DefaultRequestHeaders.Remove("Cookie");
            client.DefaultRequestHeaders.Add("Cookie", sessionCookie + "; " + csrfCookie);
            client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", token);
            return new(client, sessionCookie, csrfCookie, token);
        }

        public void Dispose() => Client.Dispose();
    }

    private sealed class Wire(WebSocket socket) : IDisposable
    {
        private readonly List<byte> pending = [];
        private readonly byte[] buffer = new byte[4096];

        public static async Task<Wire> ConnectAsync(WebApplicationFactory<Program> factory, Browser browser,
            string[]? origins = null, string? cookies = null)
        {
            var client = factory.Server.CreateWebSocketClient();
            client.ConfigureRequest = request =>
            {
                request.Headers.Cookie = cookies ?? browser.Cookies;
                request.Headers.Origin = new StringValues(origins ?? ["https://localhost"]);
            };
            using var deadline = Deadline();
            var wire = new Wire(await client.ConnectAsync(new Uri("wss://localhost/realtime/conversations"), deadline.Token));
            try
            {
                await wire.SendAsync(new { protocol = "json", version = 1 });
                var handshake = await wire.ReadAsync(deadline.Token);
                Assert.True(handshake.HasValue, "The WebSocket closed before the SignalR handshake.");
                Assert.Empty(handshake.Value.EnumerateObject());
                return wire;
            }
            catch { wire.Dispose(); throw; }
        }

        public Task WatchAsync(Guid workspaceId, Guid conversationId, string? csrfToken, string invocationId = "watch") =>
            SendAsync(new { type = 4, invocationId, target = "Watch", arguments = new object?[] { workspaceId, conversationId, csrfToken } });

        public Task CancelWatchAsync(string invocationId = "watch") => SendAsync(new { type = 5, invocationId });

        public Task InvokeAsync(string method, string invocationId, params object[] arguments) =>
            SendAsync(new { type = 1, invocationId, target = method, arguments });

        public async Task TryInvokeAsync(string method, string invocationId, params object[] arguments)
        {
            try { await InvokeAsync(method, invocationId, arguments); }
            catch (WebSocketException) { }
        }

        public async Task InvokeAndCompleteAsync(string method, string invocationId, params object[] arguments)
        {
            await InvokeAsync(method, invocationId, arguments);
            using var deadline = Deadline();
            while (await ReadAsync(deadline.Token) is { } record)
            {
                var type = record.GetProperty("type").GetInt32();
                if (type is 2 or 6) continue;
                Assert.Equal(3, type);
                Assert.Equal(invocationId, record.GetProperty("invocationId").GetString());
                Assert.False(record.TryGetProperty("error", out _), record.GetRawText());
                return;
            }
            Assert.Fail("The socket ended before the command completed.");
        }

        private async Task SendAsync(object message)
        {
            using var deadline = Deadline();
            await socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message) + '\u001e')),
                WebSocketMessageType.Text, true, deadline.Token);
        }

        public Task<JsonElement> FrameAsync() => FrameUntilAsync(_ => true);

        public async Task<JsonElement> FrameUntilAsync(Func<JsonElement, bool> predicate)
        {
            using var deadline = Deadline();
            while (await ReadAsync(deadline.Token) is { } record)
            {
                if (record.GetProperty("type").GetInt32() == 6) continue;
                Assert.Equal(2, record.GetProperty("type").GetInt32());
                var item = record.GetProperty("item");
                if (predicate(item)) return item;
            }
            throw new InvalidOperationException("The stream ended before the expected item.");
        }

        public async Task DeniedAsync()
        {
            using var deadline = Deadline();
            while (await ReadAsync(deadline.Token) is { } record)
            {
                var type = record.GetProperty("type").GetInt32();
                if (type == 6) continue;
                Assert.NotEqual(2, type);
                Assert.True(type is 3 or 7, record.GetRawText());
                if (type == 3) Assert.True(record.TryGetProperty("error", out _), record.GetRawText());
                return;
            }
        }

        public async Task ObserveAsync(TimeSpan duration, Action<JsonElement> assertFrame)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(duration);
            try
            {
                while (await ReadAsync(deadline.Token) is { } record)
                {
                    var type = record.GetProperty("type").GetInt32();
                    if (type == 6) continue;
                    Assert.Equal(2, type);
                    assertFrame(record.GetProperty("item"));
                }
                Assert.Fail("The authorized stream ended during the observation window.");
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !Ct.IsCancellationRequested) { }
        }

        public async Task TerminatedAsync(Action<JsonElement> assertRecord)
        {
            using var deadline = Deadline();
            while (await ReadAsync(deadline.Token) is { } record)
            {
                var type = record.GetProperty("type").GetInt32();
                if (type == 6) continue;
                if (type == 7) return;
                assertRecord(record);
            }
        }

        private async Task<JsonElement?> ReadAsync(CancellationToken cancellation)
        {
            while (true)
            {
                var separator = pending.IndexOf(0x1e);
                if (separator >= 0)
                {
                    using var document = JsonDocument.Parse(pending.GetRange(0, separator).ToArray());
                    pending.RemoveRange(0, separator + 1);
                    return document.RootElement.Clone();
                }
                WebSocketReceiveResult result;
                try { result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellation); }
                catch (WebSocketException) { return null; }
                if (result.MessageType == WebSocketMessageType.Close) return null;
                Assert.Equal(WebSocketMessageType.Text, result.MessageType);
                pending.AddRange(buffer.AsSpan(0, result.Count).ToArray());
                Assert.True(pending.Count <= 65536, "A realtime frame exceeded the test's bounded receive buffer.");
            }
        }

        private static CancellationTokenSource Deadline()
        {
            var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(8));
            return deadline;
        }

        public void Dispose() => socket.Dispose();
    }

    // Only the five-minute connection timer is controlled. Poll, query, and watch deadlines
    // keep their real scheduling; advancing unrelated clocks could invalidate the real cookie.
    private sealed class ControlledLifetimeTimeProvider : TimeProvider
    {
        private readonly object gate = new();
        private readonly HashSet<LifetimeTimer> lifetimeTimers = [];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            if (dueTime != RealtimeOptions.ConnectionLifetime || period != Timeout.InfiniteTimeSpan)
                return TimeProvider.System.CreateTimer(callback, state, dueTime, period);
            lock (gate)
            {
                var timer = new LifetimeTimer(this, callback, state);
                lifetimeTimers.Add(timer);
                return timer;
            }
        }

        public int ExpireConnectionLifetimes()
        {
            LifetimeTimer[] pending;
            lock (gate) pending = lifetimeTimers.ToArray();
            return pending.Count(timer => timer.Fire());
        }

        private sealed class LifetimeTimer(ControlledLifetimeTimeProvider owner, TimerCallback callback, object? state) : ITimer
        {
            private bool disposed;

            public bool Fire()
            {
                lock (owner.gate)
                {
                    if (disposed || !owner.lifetimeTimers.Remove(this)) return false;
                }
                // Registry removal disposes this timer, so invoke after releasing the timer lock.
                callback(state);
                return true;
            }

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (owner.gate)
                {
                    if (disposed) return false;
                    if (period != Timeout.InfiniteTimeSpan
                        || (dueTime != Timeout.InfiniteTimeSpan && dueTime != RealtimeOptions.ConnectionLifetime))
                        throw new NotSupportedException("Only one-shot connection lifetime timers are controlled.");
                    if (dueTime == Timeout.InfiniteTimeSpan) owner.lifetimeTimers.Remove(this);
                    else owner.lifetimeTimers.Add(this);
                    return true;
                }
            }

            public void Dispose()
            {
                lock (owner.gate)
                {
                    disposed = true;
                    owner.lifetimeTimers.Remove(this);
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }
        }
    }
}
