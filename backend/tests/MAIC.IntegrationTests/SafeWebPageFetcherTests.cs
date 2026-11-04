using System.Net;
using System.Text;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;
using MAIC.Infrastructure.Knowledge;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace MAIC.IntegrationTests;

public sealed class SafeWebPageFetcherTests
{
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("8.8.8.8", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("0.0.0.0", false)]
    [InlineData("0.1.2.3", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("100.127.255.255", false)]
    [InlineData("100.128.0.1", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("127.255.255.254", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.255.255", false)]
    [InlineData("172.32.0.1", true)]
    [InlineData("192.0.0.170", false)]
    [InlineData("192.0.2.1", false)]
    [InlineData("192.88.99.1", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("198.18.0.1", false)]
    [InlineData("198.19.255.255", false)]
    [InlineData("198.51.100.7", false)]
    [InlineData("203.0.113.9", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("240.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::", false)]
    [InlineData("::1", false)]
    [InlineData("::ffff:127.0.0.1", false)] // IPv4-mapped loopback
    [InlineData("::ffff:169.254.169.254", false)]
    [InlineData("::ffff:8.8.8.8", true)]
    [InlineData("::127.0.0.1", false)] // IPv4-compatible
    [InlineData("64:ff9b::a9fe:a9fe", false)] // NAT64
    [InlineData("fc00::1", false)]
    [InlineData("fd12:3456::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("fec0::1", false)]
    [InlineData("ff02::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2001::1", false)] // Teredo
    [InlineData("2002:a9fe:a9fe::1", false)] // 6to4 of 169.254.169.254
    [InlineData("2002:0808:0808::1", true)] // 6to4 of 8.8.8.8
    [InlineData("3fff::1", false)]
    public void Only_globally_routable_unicast_addresses_are_public(string address, bool expected) =>
        Assert.Equal(expected, SafeWebPageFetcher.IsPublicAddress(IPAddress.Parse(address)));

    private static async Task<IngestionException> FailsAsync(SafeWebPageFetcher fetcher, string url) =>
        await Assert.ThrowsAsync<IngestionException>(() => fetcher.FetchAsync(url, Cancellation));

    [Fact]
    public async Task Default_policy_blocks_loopback_and_localhost_before_any_request_is_sent()
    {
        await using var server = await TestWebServer.StartAsync(app => app.MapGet("/", () => "secret"));
        using var fetcher = new SafeWebPageFetcher();
        var port = new Uri(server.BaseUrl).Port;
        foreach (var url in new[] { server.BaseUrl + "/", $"http://localhost:{port}/", $"http://[::ffff:127.0.0.1]:{port}/",
                     $"http://2130706433:{port}/", $"http://0x7f.1:{port}/" })
        {
            var error = await FailsAsync(fetcher, url);
            Assert.Equal((IngestionFailure.FETCH_BLOCKED, false), (error.Reason, error.Transient));
        }
        // Even with loopback allowed, only ports 80 and 443 are reachable by default.
        using (var portLimited = new SafeWebPageFetcher(new WebFetchOptions { AddressAllowed = KnowledgeTestData.LoopbackOnly }))
            Assert.Equal(IngestionFailure.FETCH_BLOCKED, (await FailsAsync(portLimited, server.BaseUrl + "/")).Reason);
        foreach (var url in new[] { "http://example.com:8080/", "https://example.com:22/", "http://example.com:6379/" })
            Assert.Equal(IngestionFailure.FETCH_BLOCKED, (await FailsAsync(fetcher, url)).Reason);
        Assert.Equal(0, server.RequestCount);
        // Stored-URL rules are revalidated: credentials, fragments and other schemes never reach the network.
        foreach (var url in new[] { "https://user:pw@example.com/", "https://example.com/#x", "ftp://example.com/", "file:///etc/passwd" })
            Assert.Equal(IngestionFailure.FETCH_BLOCKED, (await FailsAsync(fetcher, url)).Reason);
    }

    [Fact]
    public async Task Redirects_are_followed_manually_and_every_hop_is_revalidated()
    {
        await using var server = await TestWebServer.StartAsync(app =>
        {
            app.MapGet("/start", () => Results.Redirect("/middle#ignored"));
            app.MapGet("/middle", (HttpContext c) => Results.Redirect($"http://127.0.0.1:{c.Request.Host.Port}/page", permanent: true));
            app.MapGet("/page", () => Results.Text("<html><head><title>T</title></head><body>Hi</body></html>", "text/html; charset=utf-8"));
            app.MapGet("/internal", (HttpContext c) => Results.Redirect($"http://127.0.0.2:{c.Request.Host.Port}/page"));
            app.MapGet("/credentials", () => Results.Redirect("http://user:pw@127.0.0.1/page"));
            app.MapGet("/loop", () => Results.Redirect("/loop"));
            app.MapGet("/nolocation", () => Results.StatusCode(302));
        });
        using var fetcher = new SafeWebPageFetcher(KnowledgeTestData.LoopbackOptions());

        var page = await fetcher.FetchAsync(server.BaseUrl + "/start", Cancellation);
        Assert.Equal((server.BaseUrl + "/page", "text/html", "utf-8"), (page.FinalUrl, page.ContentType, page.Charset));
        Assert.Contains("<title>T</title>", Encoding.UTF8.GetString(page.Body));

        var blocked = await FailsAsync(fetcher, server.BaseUrl + "/internal");
        Assert.Equal((IngestionFailure.FETCH_BLOCKED, false), (blocked.Reason, blocked.Transient));
        Assert.Equal(IngestionFailure.FETCH_BLOCKED, (await FailsAsync(fetcher, server.BaseUrl + "/credentials")).Reason);
        var loop = await FailsAsync(fetcher, server.BaseUrl + "/loop");
        Assert.Equal((IngestionFailure.FETCH_FAILED, false), (loop.Reason, loop.Transient));
        Assert.Equal(IngestionFailure.FETCH_FAILED, (await FailsAsync(fetcher, server.BaseUrl + "/nolocation")).Reason);
    }

    [Fact]
    public async Task Responses_are_classified_and_bounded()
    {
        await using var server = await TestWebServer.StartAsync(app =>
        {
            app.MapGet("/unavailable", () => Results.StatusCode(503));
            app.MapGet("/throttled", () => Results.StatusCode(429));
            app.MapGet("/missing", () => Results.NotFound());
            app.MapGet("/json", () => Results.Json(new { a = 1 }));
            app.MapGet("/untyped", async (HttpContext c) => await c.Response.Body.WriteAsync("x"u8.ToArray()));
            app.MapGet("/declared-large", (HttpContext c) =>
            {
                c.Response.ContentType = "text/plain";
                c.Response.ContentLength = 3000;
                return c.Response.Body.WriteAsync(new byte[3000]).AsTask();
            });
            app.MapGet("/streamed-large", async (HttpContext c) =>
            {
                c.Response.ContentType = "text/plain";
                for (var i = 0; i < 10; i++) await c.Response.Body.WriteAsync(new byte[500]);
            });
            app.MapGet("/slow", async (HttpContext c) =>
            {
                await Task.Delay(TimeSpan.FromSeconds(5), c.RequestAborted);
                return "late";
            });
            app.MapGet("/ok", () => Results.Text("plain body", "text/plain"));
        });
        using var fetcher = new SafeWebPageFetcher(KnowledgeTestData.LoopbackOptions(maxResponseBytes: 2048, timeoutSeconds: 1));

        Assert.Equal((IngestionFailure.FETCH_FAILED, true), Classify(await FailsAsync(fetcher, server.BaseUrl + "/unavailable")));
        Assert.Equal((IngestionFailure.FETCH_FAILED, true), Classify(await FailsAsync(fetcher, server.BaseUrl + "/throttled")));
        Assert.Equal((IngestionFailure.FETCH_FAILED, false), Classify(await FailsAsync(fetcher, server.BaseUrl + "/missing")));
        Assert.Equal((IngestionFailure.UNSUPPORTED_CONTENT, false), Classify(await FailsAsync(fetcher, server.BaseUrl + "/json")));
        Assert.Equal((IngestionFailure.UNSUPPORTED_CONTENT, false), Classify(await FailsAsync(fetcher, server.BaseUrl + "/untyped")));
        Assert.Equal((IngestionFailure.CONTENT_TOO_LARGE, false), Classify(await FailsAsync(fetcher, server.BaseUrl + "/declared-large")));
        Assert.Equal((IngestionFailure.CONTENT_TOO_LARGE, false), Classify(await FailsAsync(fetcher, server.BaseUrl + "/streamed-large")));
        Assert.Equal((IngestionFailure.FETCH_FAILED, true), Classify(await FailsAsync(fetcher, server.BaseUrl + "/slow")));
        Assert.Equal((IngestionFailure.FETCH_FAILED, true), Classify(await FailsAsync(fetcher, $"http://127.0.0.1:{FreePort()}/")));

        var page = await fetcher.FetchAsync(server.BaseUrl + "/ok", Cancellation);
        Assert.Equal(("text/plain", "plain body"), (page.ContentType, Encoding.UTF8.GetString(page.Body)));
    }

    [Fact]
    public async Task Blocked_networks_refuse_addresses_the_public_rule_would_allow()
    {
        await using var server = await TestWebServer.StartAsync(app => app.MapGet("/", () => Results.Text("ok", "text/plain")));
        var allowAll = new WebFetchOptions { AddressAllowed = _ => true, PortAllowed = _ => true };
        using (var open = new SafeWebPageFetcher(allowAll))
            Assert.Equal("ok", Encoding.UTF8.GetString((await open.FetchAsync(server.BaseUrl + "/", Cancellation)).Body));

        foreach (var cidr in new[] { "127.0.0.0/8", "127.0.0.1/32", "::ffff:127.0.0.0/104" })
        {
            using var fetcher = new SafeWebPageFetcher(new WebFetchOptions
            {
                AddressAllowed = _ => true, PortAllowed = _ => true, BlockedNetworks = [WebFetchOptions.ParseNetwork(cidr)]
            });
            Assert.Equal(IngestionFailure.FETCH_BLOCKED, (await FailsAsync(fetcher, server.BaseUrl + "/")).Reason);
        }
        Assert.Equal(1, server.RequestCount);
    }

    [Fact]
    public void Blocked_network_configuration_is_strict_and_includes_this_hosts_networks()
    {
        Assert.Equal(IPNetwork.Parse("10.0.0.0/8"), WebFetchOptions.ParseNetwork("10.1.2.3/8"));
        Assert.Equal(IPNetwork.Parse("2600:1f18:abcd::/48"), WebFetchOptions.ParseNetwork("2600:1f18:abcd:1::5/48"));
        foreach (var invalid in new[] { "", "10.0.0.0", "10.0.0.0/33", "::/129", "host/8", "10.0.0.0/-1", "10.0.0.0/x", "10/8", "10.1/16" })
            Assert.Throws<FormatException>(() => WebFetchOptions.ParseNetwork(invalid));

        var options = WebFetchOptions.ForDeployment(["2600:1f18:abcd::/48", "8.8.4.0/24"]);
        Assert.False(options.Allows(IPAddress.Parse("2600:1f18:abcd:12::7")));
        Assert.True(options.Allows(IPAddress.Parse("2606:4700:4700::1111")));
        // 6to4 addresses embedding a blocked IPv4 are blocked too.
        Assert.False(options.Allows(IPAddress.Parse("8.8.4.4")));
        Assert.False(options.Allows(IPAddress.Parse("::ffff:8.8.4.4")));
        Assert.False(options.Allows(IPAddress.Parse("2002:0808:0404::1")));
        Assert.True(options.Allows(IPAddress.Parse("2002:0808:0808::1")));
        // Every address of this host's own interfaces is refused, whatever range it is in.
        var own = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(i => i.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(i => i.GetIPProperties().UnicastAddresses).Select(u => u.Address)
            .Where(a => !IPAddress.IsLoopback(a) && !a.IsIPv6LinkLocal).ToArray();
        Assert.All(own, address => Assert.Contains(WebFetchOptions.LocalNetworks(), n => n.Contains(address)));
        Assert.All(own, address => Assert.False(new WebFetchOptions { AddressAllowed = _ => true,
            BlockedNetworks = WebFetchOptions.LocalNetworks() }.Allows(address)));
    }

    private static (IngestionFailure, bool) Classify(IngestionException error) => (error.Reason, error.Transient);

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    [Fact]
    public void Html_extraction_drops_non_content_elements_and_keeps_block_structure()
    {
        var extractor = new DocumentTextExtractor();
        var html = """
            <html><head><title>Help Center</title><style>.x{}</style><script>var secret = 1;</script></head>
            <body><nav>Menu</nav><h1>Returns</h1><p>Items can be returned within <b>30</b> days.</p>
            <script>alert('x')</script><noscript>Enable JS</noscript><div hidden>Hidden text</div>
            <ul><li>First</li><li>Second &amp; last</li></ul><template>Template</template></body></html>
            """;
        var text = KnowledgeChunker.Normalize(extractor.Extract("text/html", "utf-8", Encoding.UTF8.GetBytes(html)));
        // Block boundaries become paragraph breaks, which the chunker prefers as split points.
        Assert.Equal("Help Center\n\nMenu\n\nReturns\n\nItems can be returned within 30 days.\n\nFirst\n\nSecond & last", text);

        var tooManyTags = string.Concat(Enumerable.Repeat("<b>x</b>", DocumentTextExtractor.MaxHtmlTags / 2 + 1));
        Assert.Equal(IngestionFailure.CONTENT_TOO_LARGE,
            Assert.Throws<IngestionException>(() => extractor.Extract("text/html", null, Encoding.UTF8.GetBytes(tooManyTags))).Reason);

        var nested = string.Concat(Enumerable.Repeat("<div>", 20000)) + "deep" + string.Concat(Enumerable.Repeat("</div>", 20000));
        Assert.Contains("deep", extractor.Extract("text/html", null, Encoding.UTF8.GetBytes(nested)));
    }

    [Fact]
    public void Text_and_pdf_extraction_classify_invalid_content()
    {
        var extractor = new DocumentTextExtractor();
        Assert.Equal("Merhaba dünya", extractor.Extract("text/markdown", null, Encoding.UTF8.GetBytes("Merhaba dünya")));
        Assert.Equal(IngestionFailure.INVALID_CONTENT,
            Assert.Throws<IngestionException>(() => extractor.Extract("text/plain", null, [0xC3, 0x28])).Reason);
        Assert.Equal("\u00e9", extractor.Extract("text/plain", "iso-8859-1", [0xE9]));
        // Legacy code pages declared by fetched pages are decoded, not silently read as UTF-8.
        Assert.Equal("\u015f\u20ac", extractor.Extract("text/plain", "windows-1254", [0xFE, 0x80]));
        Assert.Equal("x", extractor.Extract("text/plain", "no-such-charset", "x"u8.ToArray()));

        var pdf = KnowledgeTestData.Pdf("First page text", "Second page text");
        var text = extractor.Extract("application/pdf", null, pdf);
        Assert.Contains("First page text", text);
        Assert.Contains("Second page text", text);
        var corrupt = Assert.Throws<IngestionException>(() => extractor.Extract("application/pdf", null, "%PDF-1.7 garbage"u8.ToArray()));
        Assert.Equal((IngestionFailure.INVALID_CONTENT, false), (corrupt.Reason, corrupt.Transient));
        Assert.Equal(IngestionFailure.UNSUPPORTED_CONTENT,
            Assert.Throws<IngestionException>(() => extractor.Extract("image/png", null, [1])).Reason);
    }
}
