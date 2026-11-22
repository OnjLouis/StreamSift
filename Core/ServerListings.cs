using System.Globalization;
using System.Text.Json;
using AngleSharp.Dom;

namespace StreamSift;

internal sealed record ServerMount(Uri Address, string? Name, int? Bitrate = null, int? Listeners = null);
internal sealed record ServerListing(Uri Page, IReadOnlyList<ServerMount> Mounts, bool Truncated);

internal static class ServerListings
{
    internal const int MaxMounts = 384;
    internal static readonly Uri Soundmap = new("https://locusonus.org/soundmap/list/active/json/name");
    private static readonly Uri GlobalCatalogueOrigin = new("https://media-ice.musicradio.com/");
    private const string IcecastStatus = "/status-json.xsl";
    private const string ShoutcastStatus = "/statistics";
    private const string ShoutcastQuery = "?json=1";

    internal static bool IsSoundmap(Uri page) =>
        page.IsDefaultPort && page.IdnHost is "locusonus.org" or "www.locusonus.org" &&
        page.Query.Length == 0 && page.AbsolutePath.TrimEnd('/') == "/soundmap";

    internal static bool IsStatusPage(Uri page) => page.Query.Length == 0 && (page.AbsolutePath is "/" or "/status.xsl");

    internal static bool IsJsonListing(Uri page) =>
        page == Soundmap || page.AbsolutePath == IcecastStatus && page.Query.Length == 0 ||
        page.AbsolutePath == ShoutcastStatus && page.Query == ShoutcastQuery;

    internal static Uri? AdvertisedStatus(Uri page, string? server, string? icyNotice)
    {
        if (!IsStatusPage(page)) return null;
        if (server?.StartsWith("Icecast", StringComparison.OrdinalIgnoreCase) == true ||
            server?.StartsWith("rocketstreamingserver", StringComparison.OrdinalIgnoreCase) == true)
            return new Uri(page, IcecastStatus);
        if (server?.StartsWith("SHOUTcast", StringComparison.OrdinalIgnoreCase) == true ||
            icyNotice?.StartsWith("SHOUTcast DNAS", StringComparison.OrdinalIgnoreCase) == true)
            return new Uri(page, ShoutcastStatus + ShoutcastQuery);
        return null;
    }

    internal static ServerListing? FromJson(string body, Uri page, CancellationToken token)
    {
        using var json = JsonDocument.Parse(body);
        var root = json.RootElement;
        var listing = new Builder(page);
        if (page == Soundmap)
        {
            foreach (var row in Rows(root))
            {
                token.ThrowIfCancellationRequested();
                if (GetString(row, "name") is not string name ||
                    !PublicNetwork.TryUrl(GetString(row, "url") ?? "", null, out var url) || url.Query.Length != 0) continue;
                if (!listing.Add(new ServerMount(url, Extraction.StationName(name)))) break;
            }
        }
        else if (page.AbsolutePath == IcecastStatus)
        {
            var source = Property(Property(root, "icestats"), "source");
            var rows = source.ValueKind == JsonValueKind.Object ? new[] { source } : Rows(source);
            foreach (var row in rows)
            {
                token.ThrowIfCancellationRequested();
                if (!AudioType(GetString(row, "server_type")) ||
                    !Uri.TryCreate(GetString(row, "listenurl"), UriKind.Absolute, out var advertised) ||
                    advertised.Scheme is not ("http" or "https") || advertised.UserInfo.Length != 0 ||
                    advertised.Query.Length != 0 || advertised.Fragment.Length != 0 ||
                    !TryMount(page, advertised.AbsolutePath, out var url)) continue;
                // The stats document can report a backend hostname or port. Test the public listener
                // that actually served these stats instead of sending requests to that backend.
                if (!listing.Add(new ServerMount(url, Extraction.StationName(GetString(row, "server_name")), Bitrate(row),
                    Nonnegative(IntValue(row, "listeners"))))) break;
            }
        }
        else
        {
            foreach (var row in Rows(Property(root, "streams")))
            {
                token.ThrowIfCancellationRequested();
                if (IntValue(row, "streamstatus") != 1 || !AudioType(GetString(row, "content")) ||
                    !TryMount(page, GetString(row, "streampath"), out var url, allowRoot: true)) continue;
                if (!listing.Add(new ServerMount(url, Extraction.StationName(GetString(row, "servertitle")), Bitrate(row),
                    Nonnegative(IntValue(row, "currentlisteners"))))) break;
            }
        }
        return listing.Result();
    }

    internal static ServerListing? FromHtml(IDocument document, Uri page, CancellationToken token)
    {
        var listing = new Builder(page);
        bool icecastPage = document.Title?.Contains("Icecast", StringComparison.OrdinalIgnoreCase) == true;
        // Global's HTTPS alias lists the same mounts using its canonical media-ice links.
        var advertisedOrigin = page is { Scheme: "https", IsDefaultPort: true, IdnHost: "media-ssl.musicradio.com" }
            ? GlobalCatalogueOrigin : null;
        foreach (var card in document.QuerySelectorAll(icecastPage ? ".stream-container, .roundcont" : ".stream-container"))
        {
            token.ThrowIfCancellationRequested();
            const string prefix = "Mount Point ";
            bool legacy = card.ClassList.Contains("roundcont");
            string? heading = card.QuerySelector(legacy ? ".streamheader h3" : ".stream-name")?.TextContent.Trim();
            if (heading?.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) != true) continue;
            string path = heading[prefix.Length..];
            if (legacy && path.IndexOf(": ", StringComparison.Ordinal) is int separator && separator >= 0)
                path = path[..separator];
            if (!TryMount(page, path, out var url)) continue;
            string? name = null, mime = null, direct = null;
            int? listeners = null;
            foreach (var row in card.QuerySelectorAll(legacy ? "table tr" : "table.stream tr"))
            {
                var cells = row.QuerySelectorAll("td");
                if (cells.Length != 2) continue;
                string field = cells[0].TextContent.Trim();
                listeners ??= HtmlListeners(field, cells[1].TextContent);
                switch (field)
                {
                    case "Stream Title:": name = Extraction.StationName(cells[1].TextContent); break;
                    case "Content Type:": mime = cells[1].TextContent.Trim(); break;
                    case "Stream URL:": direct = cells[1].QuerySelector("a[href]")?.GetAttribute("href"); break;
                }
            }
            if (!AudioType(mime) ||
                (!legacy && !MatchesLink(direct, url)) ||
                !card.QuerySelectorAll("a[href]").Any(a => MatchesLink(a.GetAttribute("href"),
                    new Uri(page, url.AbsolutePath + ".m3u")))) continue;
            if (!listing.Add(new ServerMount(url, name, Listeners: listeners))) break;
        }
        return listing.Result();

        bool MatchesLink(string? link, Uri expected) =>
            PublicNetwork.TryUrl(link ?? "", page, out var address) &&
            (address == expected || (advertisedOrigin != null && address == new Uri(advertisedOrigin, expected.AbsolutePath)));
    }

    internal static Uri? LinkedIcecastStatus(IDocument document, Uri page)
    {
        if (!IsStatusPage(page) || document.Title?.Contains("Icecast", StringComparison.OrdinalIgnoreCase) != true)
            return null;
        var status = new Uri(page, IcecastStatus);
        return document.QuerySelectorAll("a[href]").Any(a =>
            PublicNetwork.TryUrl(a.GetAttribute("href") ?? "", page, out var url) && url == status) ? status : null;
    }

    private static bool TryMount(Uri page, string? path, out Uri url, bool allowRoot = false)
    {
        url = null!;
        if (path == null || path.Length > 2048 || !path.StartsWith('/') || path.StartsWith("//") ||
            path == "/" && !allowRoot || path.Contains('?') || path.Contains('#')) return false;
        // Don't turn status or administration routes (even with escaped delimiters) into audio probes.
        string decoded = Uri.UnescapeDataString(path);
        var segments = decoded.Split('/');
        if (decoded.StartsWith("//") || decoded.Contains('\\') || segments.Length < 2 ||
            segments.Any(part => part is "." or ".." || part.Equals("admin", StringComparison.OrdinalIgnoreCase)) ||
            decoded.Equals(IcecastStatus, StringComparison.OrdinalIgnoreCase) ||
            decoded.Equals(ShoutcastStatus, StringComparison.OrdinalIgnoreCase)) return false;
        return PublicNetwork.TryUrl(path, page, out url) && page.Scheme == url.Scheme && page.Authority == url.Authority;
    }

    internal static bool AudioType(string? mime) =>
        mime != null && (mime.Equals("application/ogg", StringComparison.OrdinalIgnoreCase) ||
            mime.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) &&
            !mime.Contains("mpegurl", StringComparison.OrdinalIgnoreCase) &&
            !mime.Contains("scpls", StringComparison.OrdinalIgnoreCase) &&
            !mime.Contains("xspf", StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<JsonElement> Rows(JsonElement array) =>
        array.ValueKind == JsonValueKind.Array ? array.EnumerateArray() : Enumerable.Empty<JsonElement>();

    private static JsonElement Property(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var property) ? property : default;

    private static string? GetString(JsonElement row, string name) =>
        Property(row, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static int? IntValue(JsonElement row, string name)
    {
        var value = Property(row, name);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number :
            int.TryParse(GetString(row, name), out number) ? number : null;
    }

    private static int? Bitrate(JsonElement row) => IntValue(row, "bitrate") is > 0 and <= 10000 and var rate ? rate : null;
    private static int? Nonnegative(int? value) => value is >= 0 ? value : null;

    internal static int? HtmlListeners(string field, string value) =>
        field.Trim().TrimEnd(':').ToUpperInvariant() is "CURRENT LISTENERS" or "LISTENERS (CURRENT)" &&
        int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int count) ? count : null;

    private sealed class Builder(Uri page)
    {
        private readonly List<ServerMount> mounts = [];
        private readonly HashSet<Uri> seen = [];
        private bool truncated;

        internal bool Add(ServerMount mount)
        {
            if (!seen.Add(mount.Address)) return true;
            if (mounts.Count == MaxMounts)
            {
                truncated = true;
                return false;
            }
            mounts.Add(mount);
            return true;
        }

        internal ServerListing? Result() => mounts.Count == 0 ? null : new(page, mounts, truncated);
    }
}
