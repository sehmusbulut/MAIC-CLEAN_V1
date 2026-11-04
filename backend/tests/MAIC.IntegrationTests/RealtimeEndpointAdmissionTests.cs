using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using MAIC.API.Realtime;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed partial class RealtimeEndpointTests
{
    [Fact]
    public async Task Repeated_negotiate_and_connection_id_requests_are_rejected_without_reserving_admission()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory(globalConnectionLimit: 1);
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        for (var attempt = 0; attempt < 8; attempt++)
        {
            using var negotiate = new HttpRequestMessage(HttpMethod.Post,
                "/realtime/conversations/negotiate?negotiateVersion=1");
            negotiate.Headers.Add("Origin", "https://localhost");
            using var negotiation = await browser.Client.SendAsync(negotiate, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, negotiation.StatusCode);
            var failure = await negotiation.Content.ReadAsStringAsync(Ct);
            Assert.DoesNotContain("\"connectionId\"", failure);
            Assert.DoesNotContain("\"connectionToken\"", failure);

            using var claimedConnection = new HttpRequestMessage(HttpMethod.Get,
                "/realtime/conversations?id=unclaimed-connection");
            claimedConnection.Headers.Add("Origin", "https://localhost");
            using var rejected = await browser.Client.SendAsync(claimedConnection, Ct);
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        }

        // With a cap of one, a successful authenticated Watch proves those requests retained no lease.
        using var socket = await Wire.ConnectAsync(factory, browser);
        await socket.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        Assert.True((await socket.FrameAsync()).GetProperty("resyncRequired").GetBoolean());
    }

    [Theory]
    [InlineData("global")]
    [InlineData("user")]
    public async Task Connections_without_a_signalr_handshake_consume_global_and_user_quota_until_disposal(string limit)
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        var foreign = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory(globalConnectionLimit: limit == "global" ? 1 : 2)
            .WithWebHostBuilder(builder => builder.UseSetting("Realtime:UserConnectionLimit", "1"));
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var anotherSession = await Browser.CreateAsync(factory, seed.Actor.Id);
        using var foreignBrowser = await Browser.CreateAsync(factory, foreign.Actor.Id);
        using var pending = await ConnectWithoutHandshakeAsync(factory, browser);
        var registry = factory.Services.GetRequiredService<RealtimeRegistry>();
        Assert.Empty(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));

        // The global case uses another tenant's user; the user case uses a distinct live session
        // for the same actor. Neither changing cookie nor starting a handshake bypasses admission.
        var rejectedBrowser = limit == "global" ? foreignBrowser : anotherSession;
        await AssertUpgradeQuotaRejectedAsync(factory, rejectedBrowser);
        var handshakeError = await Record.ExceptionAsync(async () =>
        {
            using var rejected = await Wire.ConnectAsync(factory, rejectedBrowser);
        });
        Assert.True(IsQuotaRejection(handshakeError), handshakeError?.ToString());

        using var independent = limit == "user" ? await ConnectWithoutHandshakeAsync(factory, foreignBrowser) : null;
        Assert.Empty(registry.Candidates(foreign.Workspace.Id, foreign.Conversation.Id));
        pending.Dispose();

        using var recovered = await ConnectAfterAdmissionReleaseAsync(factory, browser);
        await recovered.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        Assert.True((await recovered.FrameAsync()).GetProperty("resyncRequired").GetBoolean());
        // An admitted active Watch continues to consume the same transport quota.
        await AssertUpgradeQuotaRejectedAsync(factory, rejectedBrowser);
    }

    [Fact]
    public async Task Signalr_handshake_timeout_releases_pending_admission_without_client_disposal()
    {
        var seed = await AiExecutionPersistenceSeed.CreateAsync(postgres);
        using var factory = Factory(globalConnectionLimit: 1);
        using var browser = await Browser.CreateAsync(factory, seed.Actor.Id);
        var elapsed = Stopwatch.StartNew();
        using var pending = await ConnectWithoutHandshakeAsync(factory, browser);
        var registry = factory.Services.GetRequiredService<RealtimeRegistry>();
        Assert.Empty(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));
        await AssertUpgradeQuotaRejectedAsync(factory, browser);

        using var receiveDeadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        receiveDeadline.CancelAfter(TimeSpan.FromSeconds(15));
        var buffer = new byte[1024];
        var received = new StringBuilder();
        while (true)
        {
            WebSocketReceiveResult result;
            try { result = await pending.ReceiveAsync(new ArraySegment<byte>(buffer), receiveDeadline.Token); }
            catch (WebSocketException) { break; }
            if (result.MessageType == WebSocketMessageType.Close)
            {
                // Acknowledge the server-initiated close; do not dispose the client to release quota.
                if (pending.State == WebSocketState.CloseReceived)
                    await pending.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", receiveDeadline.Token);
                break;
            }
            Assert.Equal(WebSocketMessageType.Text, result.MessageType);
            received.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
            Assert.True(received.Length <= 4096, "The handshake failure response must remain bounded.");
        }
        Assert.Contains("\"error\"", received.ToString());
        Assert.True(elapsed.Elapsed >= TimeSpan.FromSeconds(9), "The pending socket ended before the configured handshake timeout.");
        Assert.Empty(registry.Candidates(seed.Workspace.Id, seed.Conversation.Id));

        // Keep pending undisposed until replacement admission has demonstrably recovered.
        using var recovered = await ConnectAfterAdmissionReleaseAsync(factory, browser);
        await recovered.WatchAsync(seed.Workspace.Id, seed.Conversation.Id, browser.CsrfToken);
        Assert.True((await recovered.FrameAsync()).GetProperty("resyncRequired").GetBoolean());
    }

    private static async Task<WebSocket> ConnectWithoutHandshakeAsync(WebApplicationFactory<Program> factory, Browser browser)
    {
        var client = factory.Server.CreateWebSocketClient();
        client.ConfigureRequest = request =>
        {
            request.Headers.Cookie = browser.Cookies;
            request.Headers.Origin = "https://localhost";
        };
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        return await client.ConnectAsync(new Uri("wss://localhost/realtime/conversations"), deadline.Token);
    }

    private static async Task AssertUpgradeQuotaRejectedAsync(WebApplicationFactory<Program> factory, Browser browser)
    {
        var error = await Record.ExceptionAsync(async () =>
        {
            using var rejected = await ConnectWithoutHandshakeAsync(factory, browser);
        });
        Assert.True(IsQuotaRejection(error), error?.ToString() ?? "The upgrade bypassed the admission quota.");
    }

    private static bool IsQuotaRejection(Exception? error) =>
        error is InvalidOperationException && error.Message.Contains("status code: 429", StringComparison.Ordinal);

    private static async Task<Wire> ConnectAfterAdmissionReleaseAsync(WebApplicationFactory<Program> factory, Browser browser)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        while (true)
        {
            deadline.Token.ThrowIfCancellationRequested();
            try { return await Wire.ConnectAsync(factory, browser); }
            catch (InvalidOperationException error) when (IsQuotaRejection(error))
            {
                // A close frame may arrive before the request unwinds its admission lease.
                // Retry only quota rejection, with a bounded interval and overall deadline.
                await Task.Delay(TimeSpan.FromMilliseconds(50), deadline.Token);
            }
        }
    }
}

public sealed class RealtimeAdmissionTests
{
    [Fact]
    public async Task Concurrent_admission_never_exceeds_global_capacity_and_duplicate_disposal_does_not_release_other_leases()
    {
        var admission = new RealtimeAdmission(new RealtimeOptions { GlobalConnectionLimit = 4 });
        var attempts = await Task.WhenAll(Enumerable.Range(0, 32).Select(_ => Task.Run(() => admission.TryAcquire(Guid.NewGuid()))));
        var leases = attempts.Where(lease => lease is not null).Select(lease => lease!).ToArray();
        Assert.Equal(4, leases.Length);
        try
        {
            leases[0].Dispose();
            await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(leases[0].Dispose)));
            using var replacement = admission.TryAcquire(Guid.NewGuid());
            Assert.NotNull(replacement);
            Assert.Null(admission.TryAcquire(Guid.NewGuid()));
        }
        finally
        {
            foreach (var lease in leases) lease.Dispose();
        }
        using var afterCleanup = admission.TryAcquire(Guid.NewGuid());
        Assert.NotNull(afterCleanup);
    }
}
