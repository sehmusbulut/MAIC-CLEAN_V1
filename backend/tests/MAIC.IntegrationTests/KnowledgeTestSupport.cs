using System.Net;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;
using Xunit;

namespace MAIC.IntegrationTests;

/// <summary>A controllable clock for lease and backoff tests.</summary>
internal sealed class TestClock(DateTimeOffset now) : TimeProvider
{
    public DateTimeOffset Now { get; set; } = now;
    public override DateTimeOffset GetUtcNow() => Now;
}

/// <summary>A loopback HTTP server for fetcher tests. Only reachable when a test explicitly allows loopback.</summary>
internal sealed class TestWebServer : IAsyncDisposable
{
    private WebApplication app = null!;
    private int requests;

    public string BaseUrl { get; private set; } = "";
    public int RequestCount => Volatile.Read(ref requests);

    public static async Task<TestWebServer> StartAsync(Action<WebApplication> routes)
    {
        var server = new TestWebServer();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        server.app = builder.Build();
        server.app.Use(async (context, next) => { Interlocked.Increment(ref server.requests); await next(context); });
        routes(server.app);
        await server.app.StartAsync(TestContext.Current.CancellationToken);
        server.BaseUrl = server.app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!
            .Addresses.Single().TrimEnd('/');
        return server;
    }

    public async ValueTask DisposeAsync() => await app.DisposeAsync();
}

internal static class KnowledgeTestData
{
    public static readonly Func<IPAddress, bool> LoopbackOnly = address => address.Equals(IPAddress.Loopback);

    /// <summary>Loopback test servers listen on ephemeral ports, which the production port rule refuses.</summary>
    public static Infrastructure.Knowledge.WebFetchOptions LoopbackOptions(int maxResponseBytes = 2 * 1024 * 1024, double timeoutSeconds = 30) => new()
    {
        AddressAllowed = LoopbackOnly, PortAllowed = _ => true, MaxResponseBytes = maxResponseBytes,
        Timeout = TimeSpan.FromSeconds(timeoutSeconds)
    };

    public static byte[] Pdf(params string[] pages)
    {
        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        foreach (var text in pages)
        {
            var page = builder.AddPage(UglyToad.PdfPig.Content.PageSize.A4);
            page.AddText(text, 12, new UglyToad.PdfPig.Core.PdfPoint(50, 700), font);
        }
        return builder.Build();
    }

    public static async Task<IngestionOutcome?> ProcessAsync(IngestionProcessor processor) =>
        await processor.ProcessNextAsync(TestContext.Current.CancellationToken);
}
