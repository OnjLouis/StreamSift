using AngleSharp.Dom;
using System.Globalization;
using System.Xml;
using System.Xml.Linq;

namespace StreamSift;

internal static class PodcastFeed
{
    private static readonly XNamespace Atom = "http://www.w3.org/2005/Atom";
    internal const string EmptyNotice = "The podcast feed lists no supported audio enclosures.";

    internal static bool IsFeedType(string? type) => type?.Split(';')[0].Trim().ToLowerInvariant() is
        "application/rss+xml" or "application/atom+xml";

    internal static bool LooksLikeFeed(string text) =>
        text.TrimStart('\uFEFF', ' ', '\t', '\r', '\n').StartsWith('<') &&
        (text.Contains("<rss", StringComparison.OrdinalIgnoreCase) || text.Contains(Atom.NamespaceName, StringComparison.Ordinal));

    internal static IEnumerable<(Uri Address, bool Subscription)> Advertised(IDocument document, Uri page)
    {
        foreach (var link in document.QuerySelectorAll("link[rel][type][href], a[href]"))
        {
            if (!PublicNetwork.TryUrl(link.GetAttribute("href") ?? "", page, out var address)) continue;
            bool typed = IsFeedType(link.GetAttribute("type"));
            if (link.LocalName == "link")
            {
                var rel = link.GetAttribute("rel")?.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (typed && rel?.Contains("alternate", StringComparer.OrdinalIgnoreCase) == true) yield return (address, false);
            }
            else if (typed || FileIndex.SameOrigin(page, address) &&
                (link.TextContent.Contains("subscribe", StringComparison.OrdinalIgnoreCase) ||
                 link.TextContent.Contains("RSS", StringComparison.OrdinalIgnoreCase)) &&
                (Path.GetExtension(address.AbsolutePath).ToLowerInvariant() is ".xml" or ".rss" or ".atom" ||
                 address.Query.TrimStart('?').Split('&').Any(field =>
                     field.StartsWith("xml_feed=", StringComparison.OrdinalIgnoreCase))))
                yield return (address, true);
        }
    }

    internal static FileIndex? Read(byte[] content, Uri address, Uri foundOn, CancellationToken token)
    {
        try
        {
            using var reader = XmlReader.Create(new MemoryStream(content, writable: false), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, CloseInput = true,
                MaxCharactersInDocument = Discovery.MetadataLimit
            });
            var root = XDocument.Load(reader).Root;
            if (root == null) return null;
            bool atom = root.Name == Atom + "feed";
            IEnumerable<XElement>? episodes = atom ? root.Elements(Atom + "entry") :
                root.Name == "rss" ? root.Element("channel")?.Elements("item") : null;
            if (episodes == null) return null;

            var tracks = new List<Station>();
            var seen = new HashSet<Uri>();
            foreach (var episode in episodes)
            {
                token.ThrowIfCancellationRequested();
                var links = atom ? episode.Elements(Atom + "link").Where(link => (string?)link.Attribute("rel") == "enclosure") :
                    episode.Elements("enclosure");
                foreach (var link in links)
                {
                    if (!PublicNetwork.TryUrl((string?)link.Attribute(atom ? "href" : "url") ?? "", address, out var media)) continue;
                    var file = Uri.UnescapeDataString(media.Segments.LastOrDefault() ?? "");
                    var type = ((string?)link.Attribute("type"))?.Split(';')[0].Trim().ToLowerInvariant();
                    if (type?.StartsWith("video/", StringComparison.Ordinal) == true) continue;
                    var format = FileIndex.AudioFormat(file) ?? (type switch
                    {
                        "audio/mpeg" or "audio/mp3" => "MP3", "audio/mp2" => "MP2", "audio/aac" => "AAC",
                        "audio/mp4" or "audio/x-m4a" => "M4A", "audio/flac" or "audio/x-flac" => "FLAC",
                        "audio/wav" or "audio/x-wav" => "WAV", "audio/ogg" => "Ogg audio", "audio/opus" => "Ogg Opus",
                        _ => null
                    });
                    if (format == null || !seen.Add(media)) continue;
                    if (tracks.Count == FileIndex.TrackLimit) return new(tracks, true, PreferListedOrder: true);
                    var title = episode.Element(atom ? Atom + "title" : "title")?.Value;
                    long? size = long.TryParse((string?)link.Attribute("length"), NumberStyles.None,
                        CultureInfo.InvariantCulture, out long bytes) && bytes > 0 ? bytes : null;
                    tracks.Add(FileIndex.Track(media, foundOn, file, format, title, size));
                }
            }
            return new(tracks, false, PreferListedOrder: true, Notice: tracks.Count == 0 ? EmptyNotice : null);
        }
        catch (XmlException) { return null; }
    }
}
