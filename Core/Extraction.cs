using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StreamSift;

internal static partial class Extraction
{
    [GeneratedRegex("""(?:https?://|(?<![:\w])//)[^\s<>"'\x60\\]{1,2048}""", RegexOptions.IgnoreCase, 300)]
    private static partial Regex WebUrls();
    [GeneratedRegex("""\b(?:[a-z0-9_-]{0,40}(?:stream|audio)_?(?:url|uri|src)|stream|audio|mount|hls(?:_?url)?)["']?\s*[:=]\s*["']([^"'<>]{1,2048})["']""",
        RegexOptions.IgnoreCase, 300)]
    private static partial Regex AssignedUrls();
    [GeneratedRegex("""<script\b[^>]{0,100}\bsrc\s*=\s*["']([^"'\s?<>]{1,1024}\.js)(?:[?"'])""", RegexOptions.IgnoreCase, 250)]
    private static partial Regex ScriptLiterals();
    [GeneratedRegex("""\bwindow\.open\(\s*["']([^"'\s?<>]{1,1024})(?:[?"'])""", RegexOptions.IgnoreCase, 250)]
    private static partial Regex WindowLiterals();
    [GeneratedRegex("""^[ \t]*//[^\r\n]*""", RegexOptions.Multiline, 250)]
    private static partial Regex CommentLines();
    [GeneratedRegex("""(?:^|,)([A-Z0-9-]+)=("[^"]*"|[^,]*)""", RegexOptions.None, 200)]
    private static partial Regex HlsAttributes();

    private const int MaxPageLinks = 100;
    internal sealed record Extracted(bool LiveHls, bool IsDirectory, List<Link> Links);

    internal static Extracted Read(string text, string mime, Uri page, Link source, Uri site, CancellationToken token)
    {
        var links = new List<Link>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var trim = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        bool hls = false;
        bool directory = false;
        try
        {
            if (trim.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
                HasExtension(page, ".m3u", ".m3u8") && !trim.StartsWith('<'))
                M3u(trim);
            else if (trim.StartsWith("[playlist]", StringComparison.OrdinalIgnoreCase))
                Pls(trim);
            else if (HasExtension(page, ".xspf", ".asx") || mime is "application/xspf+xml" or "video/x-ms-asf")
                XmlPlaylist(trim);
            else if (source.Kind == LinkKind.Script || mime.Contains("javascript") || mime.Contains("json"))
                ScanText(trim, source.Name);
            else
                Html(trim);
        }
        catch (Exception e) when (e is RegexMatchTimeoutException or XmlException) { }
        return new(hls, directory, links);

        void Add(string? value, LinkKind kind, string? name = null, bool hint = false, Uri? relative = null,
            NameOrigin nameOrigin = NameOrigin.Inferred, Uri? wrapper = null)
        {
            token.ThrowIfCancellationRequested();
            if (value == null || links.Count >= MaxPageLinks || value.TrimStart().StartsWith('#') ||
                !PublicNetwork.TryUrl(WebUtility.HtmlDecode(value), relative ?? page, out var uri) ||
                uri == page || !seen.Add(uri.AbsoluteUri)) return;
            links.Add(new Link(uri, page, kind, name, hint, nameOrigin, wrapper));
        }

        void Candidate(string raw, string? name, bool explicitAudio = false, Uri? relative = null)
        {
            raw = raw.Trim();
            if (!PublicNetwork.TryUrl(WebUtility.HtmlDecode(raw), relative ?? page, out var uri)) return;
            if (Provider(uri) is Uri configured) Add(configured.AbsoluteUri, LinkKind.Audio, name, true);
            if (Playlist(uri)) Add(raw, LinkKind.Playlist, name, explicitAudio, relative);
            else if (explicitAudio || LikelyAudio(uri)) Add(raw, LinkKind.Audio, name, explicitAudio, relative);
        }

        void Html(string body)
        {
            var document = new HtmlParser().ParseDocument(body);
            var title = Label(document.Title) ?? source.Name;
            var baseUrl = page;
            if (document.QuerySelector("base[href]")?.GetAttribute("href") is string baseHref &&
                PublicNetwork.TryUrl(baseHref, page, out var declared) && SameSite(declared, page)) baseUrl = declared;

            bool isStreamGuide = document.QuerySelector("a[data-mount][data-channel][data-stream-url]") != null;
            if (source.Kind == LinkKind.Catalogue || isStreamGuide)
            {
                // This companion guide is useful for its official media-player playlist.
                // Its interactive preview URLs can start browser-only transcodes.
                foreach (var href in document.QuerySelectorAll("a[href]").Take(500).Select(a => a.GetAttribute("href")))
                    if (href != null && PublicNetwork.TryUrl(href, baseUrl, out var uri) &&
                        SameOrigin(page, uri) && HasExtension(uri, ".m3u", ".xspf", ".pls"))
                    {
                        Add(href, LinkKind.Catalogue, relative: baseUrl);
                        if (links.Count >= 8) break;
                    }
                if (source.Kind == LinkKind.Catalogue || links.Count > 0) return;
            }

            foreach (var mount in document.QuerySelectorAll(".roundbox .mounthead h3.mount"))
            {
                var heading = mount.TextContent.Trim();
                int slash = heading.IndexOf('/');
                if (slash < 0 || !heading.StartsWith("Mount Point ", StringComparison.OrdinalIgnoreCase) ||
                    heading[slash..].StartsWith("//") ||
                    !PublicNetwork.TryUrl(heading[slash..], page, out var direct) || !SameOrigin(page, direct)) continue;
                var card = mount.Closest(".roundbox");
                var wrapper = card?.QuerySelectorAll(".mounthead a[href]")
                    .Select(a => a.GetAttribute("href"))
                    .Select(href => PublicNetwork.TryUrl(href ?? "", page, out var link) ? link : null)
                    .FirstOrDefault(link => link != null && SameOrigin(page, link) && link.AbsolutePath == direct.AbsolutePath + ".m3u");
                if (wrapper == null) continue;
                string? name = null;
                foreach (var row in card!.QuerySelectorAll("tr"))
                {
                    var cells = row.QuerySelectorAll("td");
                    if (cells.Length >= 2 && cells[0].TextContent.Trim().Equals("Stream Name:", StringComparison.OrdinalIgnoreCase))
                    {
                        name = StationName(cells[1].TextContent);
                        break;
                    }
                }
                var mountName = Uri.UnescapeDataString(direct.AbsolutePath.Trim('/'));
                Add(direct.AbsoluteUri, LinkKind.Audio, name ?? Label(mountName), true, nameOrigin: NameOrigin.Directory, wrapper: wrapper);
                directory = true;
            }
            if (directory)
            {
                foreach (var anchor in document.QuerySelectorAll("a[href]"))
                {
                    var href = anchor.GetAttribute("href")!;
                    if (!PublicNetwork.TryUrl(href, baseUrl, out var uri) || !SameOrigin(page, uri)) continue;
                    bool wrapper = HasExtension(uri, ".m3u", ".xspf") &&
                        links.Any(l => l.Kind == LinkKind.Audio && uri.AbsolutePath[..uri.AbsolutePath.LastIndexOf('.')] == l.Url.AbsolutePath);
                    bool guide = uri.AbsolutePath.Split('/').Last().StartsWith("guide.", StringComparison.OrdinalIgnoreCase) ||
                        anchor.TextContent.Contains("Programme guide", StringComparison.OrdinalIgnoreCase);
                    if (!wrapper && (guide || HasExtension(uri, ".m3u", ".xspf", ".pls")))
                    {
                        Add(href, LinkKind.Catalogue, relative: baseUrl);
                        if (links.Count(l => l.Kind == LinkKind.Catalogue) >= 8) break;
                    }
                }
                return;
            }

            foreach (var element in document.QuerySelectorAll("audio[src], audio source[src]"))
                Candidate(element.GetAttribute("src")!, title, true, baseUrl);

            foreach (var element in document.QuerySelectorAll("a[href], link[href], iframe[src], script[src], meta[content], " +
                         "[data-src], [data-url], [data-stream], [data-stream-url], [data-stream-src], " +
                         "[data-audio], [data-audio-url], [data-audio-src], [data-dt-stream]"))
            {
                var raw = element.GetAttribute("href") ?? element.GetAttribute("src") ?? element.GetAttribute("content");
                if (raw != null && PublicNetwork.TryUrl(raw, baseUrl, out var uri))
                {
                    var label = Label(element.TextContent) ?? Label(element.GetAttribute("title")) ?? title;
                    var type = element.GetAttribute("type") ?? "";
                    var property = element.GetAttribute("property") ?? "";
                    var descriptor = uri.AbsolutePath + " " + label;
                    Candidate(raw, label, type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) ||
                        property.StartsWith("og:audio", StringComparison.OrdinalIgnoreCase), baseUrl);
                    if (element.LocalName == "iframe" && ListenerLink(uri.AbsolutePath + " " + element.GetAttribute("title")) &&
                        !HostIs(uri, "youtube.com") && !HostIs(uri, "spotify.com")) Add(raw, LinkKind.Player, label, relative: baseUrl);
                    if (element.LocalName == "a" && SameSite(uri, site) && ListenerLink(descriptor) &&
                        !LikelyAudio(uri) && !Playlist(uri)) Add(raw, LinkKind.Player, label, relative: baseUrl);
                    if (element.LocalName == "script" && SameSite(uri, site) &&
                        ListenerLink(uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..]))
                        Add(raw, LinkKind.Script, title, relative: baseUrl);
                }

                foreach (var attribute in element.Attributes)
                {
                    if (!attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)) continue;
                    bool audio = attribute.Name.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.Contains("audio", StringComparison.OrdinalIgnoreCase);
                    if (audio || attribute.Name is "data-src" or "data-url")
                        Candidate(attribute.Value, title, audio, baseUrl);
                }
            }
            foreach (var script in document.Scripts.Where(s => !s.HasAttribute("src")))
                ScanText(script.TextContent, title);
            // Some station sites print the direct address as text instead of a link.
            ScanText(document.Body?.TextContent ?? "", title, playerCode: false);
        }

        void ScanText(string value, string? name, bool playerCode = true)
        {
            if (playerCode) value = CommentLines().Replace(value, "");
            value = value.Replace("\\/", "/").Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003A", ":", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            foreach (Match match in AssignedUrls().Matches(value)) Candidate(match.Groups[1].Value, name, true);
            foreach (Match match in WebUrls().Matches(value)) Candidate(match.Value.TrimEnd(')', ']', ','), name);
            if (!playerCode) return;
            foreach (Match match in ScriptLiterals().Matches(value)) AddPlayerLiteral(match, LinkKind.Script);
            foreach (Match match in WindowLiterals().Matches(value)) AddPlayerLiteral(match, LinkKind.Player);

            void AddPlayerLiteral(Match match, LinkKind kind)
            {
                var path = match.Groups[1].Value;
                var baseUri = source.Kind == LinkKind.Script ? source.FoundOn : page;
                if (PublicNetwork.TryUrl(path, baseUri, out var target) && SameSite(target, site) && ListenerLink(target.AbsolutePath))
                    Add(path, kind, name, relative: baseUri);
            }
        }

        void M3u(string body)
        {
            var lines = body.Split('\n').Select(s => s.Trim()).ToArray();
            var isHls = lines.Any(s => s.StartsWith("#EXT-X-", StringComparison.Ordinal));
            if (!isHls)
            {
                string? name = source.Name;
                bool explicitTitle = false;
                foreach (string line in lines)
                {
                    if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                    {
                        name = line.IndexOf(',') is int comma && comma >= 0 ? StationName(line[(comma + 1)..]) : null;
                        explicitTitle = name != null;
                    }
                    else if (line.Length > 0 && !line.StartsWith('#'))
                    {
                        Add(line, LinkKind.Audio, name ?? source.Name, true,
                            nameOrigin: explicitTitle ? NameOrigin.Playlist : source.NameOrigin);
                        name = source.Name;
                        explicitTitle = false;
                    }
                }
                directory = links.Count > 24;
                return;
            }
            if (lines.Any(l => (l.StartsWith("#EXT-X-KEY:", StringComparison.Ordinal) ||
                                l.StartsWith("#EXT-X-SESSION-KEY:", StringComparison.Ordinal)) && Attr(l, "METHOD") != "NONE"))
                return;
            bool audioOnlyVariant = false;
            foreach (string line in lines)
            {
                if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal))
                {
                    var codes = Attr(line, "CODECS")?.Split(',') ?? [];
                    audioOnlyVariant = codes.Length > 0 && codes.All(IsAudioCodec);
                }
                else if (line.StartsWith("#EXT-X-MEDIA:", StringComparison.Ordinal) && Attr(line, "TYPE") == "AUDIO")
                    Add(Attr(line, "URI"), LinkKind.Playlist, source.Name, true);
                else if (line.Length != 0 && !line.StartsWith('#') && audioOnlyVariant)
                {
                    Add(line, LinkKind.Playlist, source.Name, true);
                    audioOnlyVariant = false;
                }
            }
            var segments = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
            hls = lines.Any(l => l.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)) &&
                  !lines.Contains("#EXT-X-ENDLIST") && !lines.Contains("#EXT-X-PLAYLIST-TYPE:VOD") &&
                  !lines.Any(l => l.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) &&
                  lines.Where(l => l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                                   l.StartsWith("#EXT-X-PART:", StringComparison.Ordinal) ||
                                   l.StartsWith("#EXT-X-PRELOAD-HINT:", StringComparison.Ordinal))
                       .All(l => PublicNetwork.TryUrl(Attr(l, "URI") ?? "", page, out _)) &&
                  segments.Length > 0 && segments.All(l => PublicNetwork.TryUrl(l, page, out var part) &&
                      (source.AudioHint || HasExtension(part, ".aac", ".mp3", ".ac3", ".ec3")));
        }

        void Pls(string body)
        {
            var entries = new Dictionary<int, (string? Url, string? Name)>();
            foreach (var line in body.Split('\n'))
            {
                var pair = line.Trim().Split('=', 2);
                if (pair.Length != 2) continue;
                string key = pair[0].Trim();
                bool file = key.StartsWith("File", StringComparison.OrdinalIgnoreCase);
                bool title = key.StartsWith("Title", StringComparison.OrdinalIgnoreCase);
                if (!(file || title) || !int.TryParse(key[(file ? 4 : 5)..], out var n) || n is < 1 or > 1000) continue;
                entries.TryGetValue(n, out var row);
                entries[n] = file ? (pair[1], row.Name) : (row.Url, pair[1]);
            }
            foreach (var pair in entries.OrderBy(p => p.Key))
                Add(pair.Value.Url, LinkKind.Audio, StationName(pair.Value.Name), true, nameOrigin: NameOrigin.Playlist);
            directory = links.Count > 24;
        }

        void XmlPlaylist(string body)
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                MaxCharactersInDocument = 800 * 1024
            });
            var xml = XDocument.Load(reader);
            foreach (var track in xml.Descendants().Where(x => x.Name.LocalName is "track" or "entry"))
            {
                var name = StationName(track.Elements().FirstOrDefault(x => x.Name.LocalName == "title")?.Value);
                foreach (var node in track.Elements())
                {
                    if (node.Name.LocalName == "location") Add(node.Value, LinkKind.Audio, name, true, nameOrigin: NameOrigin.Playlist);
                    if (node.Name.LocalName == "ref") Add(node.Attribute("href")?.Value, LinkKind.Audio, name, true, nameOrigin: NameOrigin.Playlist);
                }
            }
            directory = links.Count > 24;
        }
    }

    private static Uri? Provider(Uri uri)
    {
        // A documented Radio.co embed points at player.radio.co/<station>/{default,mini,...}.
        if (uri.IdnHost != "player.radio.co") return null;
        var id = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return id?.Length == 10 && id[0] == 's' && id[1..].All(Uri.IsHexDigit)
            ? new Uri("https://streaming.radio.co/" + id + "/listen") : null;
    }

    private static bool SameSite(Uri a, Uri b) =>
        string.Equals(a.IdnHost, b.IdnHost, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(a.IdnHost.TrimEnd('.'), "www." + b.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase) ||
        string.Equals(b.IdnHost.TrimEnd('.'), "www." + a.IdnHost.TrimEnd('.'), StringComparison.OrdinalIgnoreCase);
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Port == b.Port && a.IdnHost == b.IdnHost;
    private static bool HostIs(Uri uri, string host) => uri.IdnHost == host || uri.IdnHost.EndsWith("." + host, StringComparison.Ordinal);
    private static bool ListenerLink(string value) => new[] { "listen", "player", "stream", "live", "radio", "audio", "playout", "webcast", "tune-in", "tune in", "on-air" }
        .Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
    private static bool LikelyAudio(Uri uri) =>
        HasExtension(uri, ".mp3", ".mp2", ".aac", ".aacp", ".flac", ".ogg", ".opus", ".wav") ||
        HostIs(uri, "radio.co") && uri.AbsolutePath.Contains("/listen", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Contains("icecast", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.Contains("shoutcast", StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.StartsWith("stream", StringComparison.OrdinalIgnoreCase) &&
            (ListenerLink(uri.AbsolutePath) || uri.AbsolutePath.EndsWith("/;", StringComparison.Ordinal)) ||
        uri.AbsolutePath.TrimEnd('/').EndsWith("/stream", StringComparison.OrdinalIgnoreCase) ||
        uri.Query.Contains("type=mp3", StringComparison.OrdinalIgnoreCase);
    private static bool Playlist(Uri uri) => HasExtension(uri, ".m3u", ".m3u8", ".pls", ".xspf", ".asx");
    private static bool HasExtension(Uri uri, params string[] suffixes) =>
        suffixes.Any(suffix => uri.AbsolutePath.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
    private static bool IsAudioCodec(string codec) => new[] { "mp4a", "ac-3", "ec-3", "ac-4", "opus", "flac", "mp3", "alac" }
        .Any(value => codec.Trim().StartsWith(value, StringComparison.OrdinalIgnoreCase));
    private static string? Attr(string line, string name) =>
        HlsAttributes().Matches(line[(line.IndexOf(':') + 1)..]).Cast<Match>()
            .FirstOrDefault(m => m.Groups[1].Value == name)?.Groups[2].Value.Trim('"');

    internal static bool LooksLikeText(byte[] bytes) =>
        bytes.Length > 0 && !bytes.Take(256).Any(x => x < 32 && x is not (9 or 10 or 13));

    internal static string? StationName(string? value)
    {
        var label = Label(value);
        return label?.ToUpperInvariant() is "UNSPECIFIED NAME" or "UNKNOWN" or "NO NAME" or
            "M3U" or "M3U8" or "PLS" or "XSPF" or "ASX" ? null : label;
    }

    internal static string? Label(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var words = WebUtility.HtmlDecode(value).Take(512)
            .Select(c => char.IsControl(c) || char.IsWhiteSpace(c) ? ' ' : c).ToArray();
        return string.Join(' ', new string(words).Split(' ', StringSplitOptions.RemoveEmptyEntries)) is { Length: > 0 } label
            ? label[..Math.Min(120, label.Length)] : null;
    }
}
