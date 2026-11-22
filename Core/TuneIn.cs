using System.Text.RegularExpressions;

namespace StreamSift;

// Station-page route adapted from Jeff Bishop's QUILL Radio contribution.
internal static partial class TuneIn
{
    private const string Service = "https://opml.radiotime.com/Tune.ashx";
    internal const int MaxStreams = 12;
    private static readonly HashSet<string> ListenerKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "lat", "lon", "long", "latitude", "longitude", "session-id", "sessionid",
        "session_id", "listenerid", "listener-id", "listener_id"
    };

    [GeneratedRegex(@"^(?:(?<name>[^/]{1,200})-)?(?<id>s[0-9]{2,12})$", RegexOptions.IgnoreCase, 200)]
    private static partial Regex StationSegment();

    internal static Link? Source(Uri page)
    {
        if (!page.IsDefaultPort || page.IdnHost is not ("tunein.com" or "www.tunein.com") ||
            page.AbsolutePath.TrimEnd('/').Split('/') is not ["", "radio", var segment]) return null;
        var match = StationSegment().Match(Uri.UnescapeDataString(segment));
        if (!match.Success) return null;
        var id = match.Groups["id"].Value.ToLowerInvariant();
        var address = new Uri($"{Service}?id={id}&partnerId=RadioTime&formats=mp3,aac,ogg,hls");
        return new Link(address, new Uri(page.GetLeftPart(UriPartial.Path)), LinkKind.DedicatedPlayer,
            Extraction.Label(match.Groups["name"].Value.Replace('-', ' ')), TuneInId: id);
    }

    internal static bool IsService(Uri address) => address.GetLeftPart(UriPartial.Path) == Service;

    internal static bool TryClean(Uri address, string stationId, out Uri cleaned)
    {
        cleaned = address;
        if (address.Query.Length == 0) return true;
        var kept = new List<string>();
        bool changed = false;
        foreach (var field in address.Query[1..].Split('&'))
        {
            var parts = field.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            key = key[(key.LastIndexOf('.') + 1)..];
            if (key.Equals("stationid", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("station-id", StringComparison.OrdinalIgnoreCase) ||
                key.Equals("station_id", StringComparison.OrdinalIgnoreCase))
            {
                if (parts.Length != 2 ||
                    !Uri.UnescapeDataString(parts[1]).Equals(stationId, StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (ListenerKeys.Contains(key)) changed = true;
            else kept.Add(field);
        }
        if (changed) cleaned = new UriBuilder(address) { Query = string.Join('&', kept) }.Uri;
        return true;
    }
}
