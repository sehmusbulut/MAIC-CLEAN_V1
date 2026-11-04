using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using MAIC.Application.Knowledge;
using MAIC.Domain.Knowledge;

namespace MAIC.Infrastructure.Knowledge;

public sealed class WebFetchOptions
{
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxResponseBytes { get; init; } = 2 * 1024 * 1024;
    public int MaxRedirects { get; init; } = 5;
    /// <summary>Destination ports allowed on every hop. Defaults to 80 and 443 so the worker cannot probe other services.</summary>
    public Func<int, bool> PortAllowed { get; init; } = port => port is 80 or 443;
    /// <summary>Decides which resolved addresses may be connected to. Defaults to public unicast addresses only.</summary>
    public Func<IPAddress, bool> AddressAllowed { get; init; } = SafeWebPageFetcher.IsPublicAddress;
    /// <summary>
    /// Networks refused in addition to <see cref="AddressAllowed"/>: the deployment's own ranges. Global IPv6 (and any
    /// public IPv4) inside a VPC looks public to the address rule, so these ranges close that gap.
    /// </summary>
    public IReadOnlyList<IPNetwork> BlockedNetworks { get; init; } = [];
    /// <summary>Networks re-evaluated on every check (for example this host's interfaces, which can change at runtime).</summary>
    public Func<IReadOnlyList<IPNetwork>>? DynamicBlockedNetworks { get; init; }

    /// <summary>
    /// Production options: public addresses only, minus the configured CIDR ranges and, by default, every network this
    /// host is attached to (so the worker's own subnet, including a VPC's global IPv6 prefix, is never reachable).
    /// </summary>
    public static WebFetchOptions ForDeployment(IEnumerable<string>? blockedCidrs, bool blockLocalNetworks = true) => new()
    {
        BlockedNetworks = [.. (blockedCidrs ?? []).Select(ParseNetwork)],
        DynamicBlockedNetworks = blockLocalNetworks ? CachedLocalNetworks() : null
    };

    /// <summary>Local networks refreshed at most once a minute, so renumbering or new interfaces are picked up.</summary>
    private static Func<IReadOnlyList<IPNetwork>> CachedLocalNetworks()
    {
        var cache = (Networks: LocalNetworks(), At: DateTime.UtcNow);
        var gate = new object();
        return () =>
        {
            lock (gate)
            {
                if (DateTime.UtcNow - cache.At > TimeSpan.FromMinutes(1)) cache = (LocalNetworks(), DateTime.UtcNow);
                return cache.Networks;
            }
        };
    }

    /// <summary>Parses a CIDR such as 10.0.0.0/8 or 2600:1f18:abcd::/48; host bits are cleared. Invalid input throws.</summary>
    public static IPNetwork ParseNetwork(string cidr)
    {
        var separator = cidr.IndexOf('/');
        // IPAddress.TryParse accepts IPv4 shorthand such as "10" (0.0.0.10); require dotted quads or IPv6 text.
        if (separator <= 0 || (!cidr[..separator].Contains(':') && cidr[..separator].Count(c => c == '.') != 3)
            || !IPAddress.TryParse(cidr[..separator], out var address)
            || !int.TryParse(cidr[(separator + 1)..], out var prefix)
            || prefix < 0 || prefix > (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128))
            throw new FormatException($"Invalid blocked network '{cidr}'.");
        // An IPv4-mapped range (::ffff:a.b.c.d/96+) is stored as IPv4, because addresses are compared in IPv4 form.
        return address.IsIPv4MappedToIPv6 && prefix >= 96 ? Network(address.MapToIPv4(), prefix - 96) : Network(address, prefix);
    }

    /// <summary>Networks of this host's active interfaces (loopback and link-local are already refused).</summary>
    public static IReadOnlyList<IPNetwork> LocalNetworks()
    {
        var networks = new List<IPNetwork>();
        foreach (var networkInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var unicast in networkInterface.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                if (IPAddress.IsLoopback(address) || address.IsIPv6LinkLocal || unicast.PrefixLength <= 0) continue;
                networks.Add(Network(address, unicast.PrefixLength));
            }
        }
        return networks;
    }

    private static IPNetwork Network(IPAddress address, int prefix)
    {
        var bytes = address.GetAddressBytes();
        for (var bit = prefix; bit < bytes.Length * 8; bit++) bytes[bit / 8] &= (byte)~(0x80 >> (bit % 8));
        return new IPNetwork(new IPAddress(bytes), prefix);
    }

    /// <summary>True when the address passes the address rule and lies outside every blocked network.</summary>
    public bool Allows(IPAddress address)
    {
        if (!AddressAllowed(address)) return false;
        // Compare every form an address can take: itself, its IPv4-mapped IPv4 and the IPv4 embedded in 6to4 (2002::/16).
        var forms = new List<IPAddress> { address };
        if (address.IsIPv4MappedToIPv6) forms.Add(address.MapToIPv4());
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 16 && bytes[0] == 0x20 && bytes[1] == 0x02) forms.Add(new IPAddress(bytes[2..6]));
        var dynamic = DynamicBlockedNetworks?.Invoke() ?? [];
        return !forms.Any(form => BlockedNetworks.Any(n => n.Contains(form)) || dynamic.Any(n => n.Contains(form)));
    }
}

/// <summary>
/// Fetches one web page for ingestion without trusting the stored URL. For every hop the URL is revalidated, the host
/// is resolved, every resolved address must be allowed (public unicast by default, so loopback, private, link-local,
/// metadata, CGNAT, multicast and reserved ranges fail, including IPv4-mapped/NAT64/6to4 forms) and outside the
/// deployment's blocked networks, and the socket
/// connects to that vetted address. Redirects are followed manually; proxies, cookies and credentials are never used.
/// </summary>
public sealed class SafeWebPageFetcher : IWebPageFetcher, IDisposable
{
    private static readonly HashSet<string> AllowedMediaTypes =
        new(StringComparer.OrdinalIgnoreCase) { KnowledgeDocument.TextHtml, "application/xhtml+xml", KnowledgeDocument.TextPlain };

    private readonly WebFetchOptions options;
    private readonly HttpClient client;

    public SafeWebPageFetcher(WebFetchOptions? options = null)
    {
        this.options = options ?? new WebFetchOptions();
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false,
            UseCookies = false,
            Credentials = null,
            PreAuthenticate = false,
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(10),
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            MaxResponseHeadersLength = 64,
            ConnectCallback = ConnectAsync
        };
        client = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MAIC-KnowledgeFetcher/1.0");
        client.DefaultRequestHeaders.Accept.ParseAdd("text/html, application/xhtml+xml;q=0.9, text/plain;q=0.8");
    }

    public void Dispose() => client.Dispose();

    public async Task<FetchedWebPage> FetchAsync(string url, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.Timeout);
        var current = url;
        try
        {
            for (var hop = 0; ; hop++)
            {
                if (!KnowledgeSource.IsValidWebsiteUrl(current) || !Uri.TryCreate(current, UriKind.Absolute, out var uri)
                    || !options.PortAllowed(uri.Port))
                    throw new IngestionException(IngestionFailure.FETCH_BLOCKED, false);
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                var status = (int)response.StatusCode;
                if (status is 301 or 302 or 303 or 307 or 308)
                {
                    if (hop >= options.MaxRedirects || response.Headers.Location is not { } location)
                        throw new IngestionException(IngestionFailure.FETCH_FAILED, false);
                    // Resolve relative locations and drop fragments; the next hop is validated like the original URL.
                    if (!Uri.TryCreate(uri, location, out var next)) throw new IngestionException(IngestionFailure.FETCH_BLOCKED, false);
                    current = next.GetComponents(UriComponents.AbsoluteUri & ~UriComponents.Fragment,
                        UriFormat.UriEscaped);
                    continue;
                }
                if (status is 408 or 425 or 429 || status >= 500) throw new IngestionException(IngestionFailure.FETCH_FAILED, true);
                if (status is < 200 or > 299) throw new IngestionException(IngestionFailure.FETCH_FAILED, false);

                var mediaType = response.Content.Headers.ContentType?.MediaType;
                if (mediaType is null || !AllowedMediaTypes.Contains(mediaType))
                    throw new IngestionException(IngestionFailure.UNSUPPORTED_CONTENT, false);
                var body = await ReadBoundedAsync(response.Content, timeout.Token);
                return new(current, mediaType.ToLowerInvariant(), response.Content.Headers.ContentType?.CharSet, body);
            }
        }
        catch (IngestionException)
        {
            throw;
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IngestionException(IngestionFailure.FETCH_FAILED, true, error);
        }
        catch (HttpRequestException error) when (FindBlocked(error) is { } blocked)
        {
            throw new IngestionException(IngestionFailure.FETCH_BLOCKED, false, blocked);
        }
        catch (Exception error) when (error is HttpRequestException or IOException or SocketException)
        {
            throw new IngestionException(IngestionFailure.FETCH_FAILED, true, error);
        }
        catch (Exception error) when (error is not OperationCanceledException and not OutOfMemoryException)
        {
            // Anything else (malformed responses, invalid redirect targets) is a permanent fetch failure.
            throw new IngestionException(IngestionFailure.FETCH_FAILED, false, error);
        }
    }

    private async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > options.MaxResponseBytes)
            throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
        await using var stream = await content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
        {
            // Counted after decompression, so compressed bombs are bounded too.
            if (buffer.Length + read > options.MaxResponseBytes) throw new IngestionException(IngestionFailure.CONTENT_TOO_LARGE, false);
            buffer.Write(chunk, 0, read);
        }
        return buffer.ToArray();
    }

    private async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var addresses = IPAddress.TryParse(host.Trim('[', ']'), out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, cancellationToken);
        // Every answer must be allowed, so a hostname that mixes public and internal records is rejected outright.
        if (addresses.Length == 0 || addresses.Any(a => !options.Allows(a))) throw new BlockedDestinationException();

        var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(new IPEndPoint(addresses[0], context.DnsEndPoint.Port), cancellationToken);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private static BlockedDestinationException? FindBlocked(Exception? error)
    {
        for (; error is not null; error = error.InnerException)
            if (error is BlockedDestinationException blocked) return blocked;
        return null;
    }

    /// <summary>True only for globally routable unicast addresses; embedded IPv4 forms are checked as IPv4.</summary>
    public static bool IsPublicAddress(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) return IsPublicIPv4(address.MapToIPv4());
        if (address.AddressFamily == AddressFamily.InterNetwork) return IsPublicIPv4(address);
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return false;

        var b = address.GetAddressBytes();
        // Only global unicast 2000::/3 is considered.
        if ((b[0] & 0xE0) != 0x20) return false;
        // 2001::/32 Teredo, 2001:db8::/32 documentation, 2001:10::/28 ORCHID, 2001:20::/28 ORCHIDv2, 2001::/23 IETF protocols.
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] < 0x02) return false;
        if (b[0] == 0x20 && b[1] == 0x01 && b[2] == 0x0d && b[3] == 0xb8) return false;
        // 2002::/16 6to4 embeds an IPv4 address.
        if (b[0] == 0x20 && b[1] == 0x02) return IsPublicIPv4(new IPAddress(b[2..6]));
        // 3fff::/20 documentation.
        if (b[0] == 0x3f && b[1] == 0xff && (b[2] & 0xF0) == 0) return false;
        return true;
    }

    private static bool IsPublicIPv4(IPAddress address)
    {
        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 or >= 224 // this network, private, loopback, multicast, reserved, broadcast
            || (b[0] == 100 && (b[1] & 0xC0) == 64) // 100.64.0.0/10 shared address space (CGNAT)
            || (b[0] == 169 && b[1] == 254) // link-local, including cloud metadata
            || (b[0] == 172 && (b[1] & 0xF0) == 16) // 172.16.0.0/12
            || (b[0] == 192 && b[1] == 168) // 192.168.0.0/16
            || (b[0] == 192 && b[1] == 0 && b[2] is 0 or 2) // IETF assignments, TEST-NET-1
            || (b[0] == 192 && b[1] == 88 && b[2] == 99) // 6to4 relay anycast
            || (b[0] == 198 && (b[1] & 0xFE) == 18) // 198.18.0.0/15 benchmarking
            || (b[0] == 198 && b[1] == 51 && b[2] == 100) // TEST-NET-2
            || (b[0] == 203 && b[1] == 0 && b[2] == 113)); // TEST-NET-3
    }

    private sealed class BlockedDestinationException() : Exception("The destination address is not allowed.");
}
