using System.Net;
using System.Net.Sockets;

namespace StreamSift;

public static class PublicNetwork
{
    public static bool TryUrl(string text, Uri? parent, out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(text) || text.Length > 4096 ||
            text.Any(c => char.IsControl(c) || c == '\\')) return false;
        if (!(parent == null ? Uri.TryCreate(text.Trim(), UriKind.Absolute, out var parsed)
                            : Uri.TryCreate(parent, text.Trim(), out parsed))) return false;
        if (parsed.Scheme is not ("https" or "http") || parsed.UserInfo.Length != 0 ||
            parsed.HostNameType == UriHostNameType.Unknown || parsed.IsLoopback) return false;
        var host = parsed.IdnHost.TrimEnd('.');
        if (parsed.HostNameType is UriHostNameType.IPv4 or UriHostNameType.IPv6)
        {
            if (!IPAddress.TryParse(host.Trim('[', ']'), out var ip) || !IsPublic(ip)) return false;
        }
        else if (!host.Contains('.') || host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith(".local", StringComparison.OrdinalIgnoreCase) || host.EndsWith(".internal", StringComparison.OrdinalIgnoreCase) ||
                 host.EndsWith(".home.arpa", StringComparison.OrdinalIgnoreCase)) return false;
        uri = new UriBuilder(parsed) { Fragment = "" }.Uri;
        return true;
    }

    public static bool TryInput(string text, out Uri uri)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        return TryUrl(text, null, out uri);
    }

    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        if (b.Length == 4)
        {
            return b[0] != 0 && b[0] != 10 && b[0] != 127 && b[0] < 224 &&
                !(b[0] == 100 && b[1] is >= 64 and <= 127) &&
                !(b[0] == 169 && b[1] == 254) && !(b[0] == 172 && b[1] is >= 16 and <= 31) &&
                !(b[0] == 192 && (b[1] == 168 || (b[1] == 0 && b[2] is 0 or 2) || (b[1] == 88 && b[2] == 99))) &&
                !(b[0] == 198 && (b[1] is 18 or 19 || (b[1] == 51 && b[2] == 100))) &&
                !(b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        // Only global unicast IPv6; reject transition, documentation and special-purpose ranges.
        return (b[0] & 0xe0) == 0x20 && !(b[0] == 0x20 && (b[1] == 2 ||
            (b[1] == 1 && (b[2] < 2 || (b[2] == 0x0d && b[3] == 0xb8))))) &&
            !(b[0] == 0x3f && b[1] == 0xff);
    }

    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.All,
        ConnectTimeout = TimeSpan.FromSeconds(5),
        PooledConnectionLifetime = TimeSpan.FromMinutes(2),
        MaxResponseHeadersLength = 32,
        MaxResponseDrainSize = 0,
        ConnectCallback = Connect
    }) { Timeout = Timeout.InfiniteTimeSpan };

    private static async ValueTask<Stream> Connect(SocketsHttpConnectionContext context, CancellationToken token)
    {
        var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
        if (addresses.Length == 0 || addresses.Any(ip => !IsPublic(ip)))
            throw new HttpRequestException("The hostname does not resolve exclusively to public addresses.");
        // Connect to the checked address, not the name, so a DNS change cannot redirect this request.
        foreach (var address in addresses.Distinct().Take(6))
        {
            var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), token);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch (SocketException) { socket.Dispose(); }
            catch { socket.Dispose(); throw; }
        }
        throw new HttpRequestException("No public address was reachable.");
    }
}
