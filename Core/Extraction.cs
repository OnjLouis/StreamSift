using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace StreamSift;

internal static class Extraction
{
    private static readonly Regex WebUrls = new("""(?:https?://|(?<![:\w])//)[^\s<>"'\x60\\]{1,2048}""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300));
    private static readonly Regex AssignedUrls = new("""\b(?:[a-z0-9_-]{0,40}(?:stream|audio)_?(?:url|uri|src)|stream|audio|mount|hls(?:_?url)?)["']?\s*[:=]\s*["']([^"'<>]{1,2048})["']""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300));
    private static readonly Regex AssignedPlaylists = new("""\b(?:download[_-]?)?playlist(?:[_-]?download)?(?:[_-]?(?:url|uri|src|href))?["']?\s*[:=]\s*["']([^"'<>]{1,2048})["']""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(300));
    private static readonly Regex ScriptLiterals = new("""<script\b[^>]{0,100}\bsrc\s*=\s*["']([^"'\s?<>]{1,1024}\.js)(?:[?"'])""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
    private static readonly Regex WindowLiterals = new("""\bwindow\.open\(\s*["']([^"'\s?<>]{1,1024})(?:[?"'])""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(250));
    private static readonly Regex CommentLines = new("""^[ \t]*//[^\r\n]*""", RegexOptions.Multiline, TimeSpan.FromMilliseconds(250));
    private static readonly Regex HlsAttributes = new("""(?:^|,)([A-Z0-9-]+)=("[^"]*"|[^,]*)""", RegexOptions.None, TimeSpan.FromMilliseconds(200));
    private static readonly Regex AudioSourceSeparator = new(""",\s*(?=https?://)""", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex AppEntryFile = new(@"^(?:main|app|index|bundle)(?:[.-][a-f0-9]{8,32})?(?:\.min)?\.(?:m?js)$", RegexOptions.IgnoreCase, TimeSpan.FromMilliseconds(200));
    private static readonly Regex SecureNetStream = new("""\b(?:var|let|const)\s+streamSrcDB\s*=\s*['"]([^'"]{1,2048})['"]""", RegexOptions.None, TimeSpan.FromMilliseconds(200));

    private const int MaxPageLinks = 100;
    private const int MaxVideoMetadataCharacters = 1024 * 1024, MaxVideoMetadataRows = 48;
    private const int MaxVideoCdnHosts = 8;
    private static readonly Uri SwedishRadioChannels = new("https://api.sr.se/api/v2/channels?format=json&pagination=false");
    private const string TritonHost = "playerservices.streamtheworld.com";
    private const string TritonProvisioning = "/api/livestream";
    private const string TritonRedirect = "/api/livestream-redirect/";
    private static readonly XNamespace TritonSchema = "http://provisioning.streamtheworld.com/player/livestream-1.9";
    internal sealed record Extracted(bool LiveHls, string[]? VideoCdnHosts, bool IsDirectory, List<Link> Links,
        ServerListing? Server, FileIndex? FileIndex, bool IsArticle);

    internal static Uri? OfficialCatalogue(Uri page)
    {
        if (ServerListings.IsSoundmap(page)) return ServerListings.Soundmap;
        return page.IsDefaultPort && IsSwedishRadioSite(page) && page.AbsolutePath.TrimEnd('/') is "" or "/kanaler"
            ? SwedishRadioChannels : null;
    }

    internal static Link? PublicPlayerSource(Uri page)
    {
        if (TuneIn.Source(page) is Link tuneIn) return tuneIn;
        if (page.IdnHost != "radio.garden" || !page.IsDefaultPort) return null;
        string[] path = page.AbsolutePath.Trim('/').Split('/');
        if (path is not ["listen", { Length: > 0 } slug, { Length: 8 } id] ||
            !id.All(c => Ascii.IsLetterOrDigit(c) || c is '-' or '_')) return null;
        var audio = new Uri($"https://radio.garden/api/ara/content/listen/{id}/channel.mp3");
        return new Link(audio, page, LinkKind.Audio, Label(Uri.UnescapeDataString(slug).Replace('-', ' ')), true);
    }

    internal static Extracted Read(string text, string mime, Uri page, Link source, Uri site, CancellationToken token,
        bool allowFileIndex = false, bool discoverPageMedia = false)
    {
        var links = new List<Link>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var trim = text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t');
        bool hls = false;
        string[]? videoCdnHosts = null;
        bool directory = false, article = false;
        ServerListing? server = null;
        FileIndex? fileIndex = null;
        try
        {
            if (source.TuneInId != null && TuneIn.IsService(page))
            {
                foreach (var entry in trim.Split('\n').Select(line => line.Trim())
                             .Where(line => line.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                                            line.StartsWith("http://", StringComparison.OrdinalIgnoreCase)).Take(TuneIn.MaxStreams))
                {
                    if (PublicNetwork.TryUrl(entry, null, out var stream))
                        Add(entry, Playlist(stream) ? LinkKind.Playlist : LinkKind.Audio, source.Name, true);
                }
            }
            else if (source.TuneInId == null && ServerListings.IsJsonListing(page))
                server = ServerListings.FromJson(trim, page, token);
            else if (page == SwedishRadioChannels)
                SwedishRadio(trim);
            else if (page.Scheme == "https" && page.IsDefaultPort && page.IdnHost == TritonHost &&
                     page.AbsolutePath == TritonProvisioning)
                TritonMounts(trim);
            else if (trim.StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase) ||
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
        catch (Exception e) when (e is RegexMatchTimeoutException or XmlException or JsonException) { }
        return new(hls, videoCdnHosts, directory, links, server, fileIndex, article);

        void Add(string? value, LinkKind kind, string? name = null, bool hint = false, Uri? relative = null,
            NameOrigin nameOrigin = NameOrigin.Inferred, Uri? wrapper = null, bool keepAddress = false, int? listeners = null,
            bool videoHint = false, Uri? videoMaster = null, bool pageAudio = false)
        {
            token.ThrowIfCancellationRequested();
            if (value == null || links.Count >= MaxPageLinks || value.TrimStart().StartsWith('#') ||
                !PublicNetwork.TryUrl(WebUtility.HtmlDecode(value), relative ?? page, out var uri) ||
                source.TuneInId != null && !TuneIn.TryClean(uri, source.TuneInId, out uri) ||
                uri == page || !seen.Add(uri.AbsoluteUri)) return;
            if (source.TuneInId != null && wrapper != null)
                wrapper = TuneIn.TryClean(wrapper, source.TuneInId, out var cleanWrapper) ? cleanWrapper : null;
            links.Add(new Link(uri, source.TuneInId == null && videoMaster == null ? page : source.FoundOn, kind, name, hint, nameOrigin, wrapper,
                keepAddress, listeners, source.TuneInId, videoHint, videoMaster, pageAudio));
        }

        void Candidate(string raw, string? name, bool explicitAudio = false, Uri? relative = null, bool explicitVideo = false,
            bool pageAudio = false)
        {
            raw = raw.Trim();
            if (!PublicNetwork.TryUrl(WebUtility.HtmlDecode(raw), relative ?? page, out var uri)) return;
            if (Provider(uri) is Uri configured) Add(configured.AbsoluteUri, LinkKind.Audio, name, true);
            if (Playlist(uri)) Add(raw, LinkKind.Playlist, name, explicitAudio, relative, videoHint: explicitVideo);
            else if (explicitVideo) Add(raw, LinkKind.Video, name, relative: relative, videoHint: true);
            else if (explicitAudio || pageAudio || LikelyAudio(uri))
                Add(raw, LinkKind.Audio, name, explicitAudio, relative, pageAudio: pageAudio);
        }

        void SwedishRadio(string body)
        {
            using var json = JsonDocument.Parse(body);
            if (json.RootElement.ValueKind != JsonValueKind.Object ||
                !json.RootElement.TryGetProperty("channels", out var channels) ||
                channels.ValueKind != JsonValueKind.Array) return;
            foreach (var channel in channels.EnumerateArray())
            {
                token.ThrowIfCancellationRequested();
                // The open directory includes event overflow slots that are usually off air.
                if (channel.ValueKind != JsonValueKind.Object || StringProperty(channel, "channeltype") == "Extrakanaler" ||
                    !channel.TryGetProperty("liveaudio", out var audio) || audio.ValueKind != JsonValueKind.Object ||
                    !PublicNetwork.TryUrl(StringProperty(audio, "url") ?? "", null, out var uri) ||
                    uri.Scheme != "https" || !uri.IsDefaultPort || !(IsSwedishRadioSite(uri) || uri.IdnHost == "live1.sr.se"))
                    continue;
                Add(uri.AbsoluteUri, LinkKind.Audio, StationName(StringProperty(channel, "name")), true,
                    nameOrigin: NameOrigin.Directory);
            }
            directory = links.Count > 0;

            static string? StringProperty(JsonElement item, string key) =>
                item.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }

        void Html(string body)
        {
            var document = new HtmlParser().ParseDocument(body);
            if (allowFileIndex && (fileIndex = FileIndex.Read(document, page, token)) != null) return;
            if (discoverPageMedia)
            {
                article = document.QuerySelector("meta[property='og:type']")?.GetAttribute("content") == "article";
                foreach (var demo in PublicDemos.Advertised(document, page))
                    if (seen.Add(demo.Url.AbsoluteUri)) links.Add(demo);
                foreach (var feed in PodcastFeed.Advertised(document, page)
                             .OrderByDescending(feed => feed.Subscription).DistinctBy(feed => feed.Address).Take(4))
                    Add(feed.Address.AbsoluteUri, feed.Subscription ? LinkKind.PodcastSubscription : LinkKind.PodcastFeed);
            }
            var title = Label(document.Title) ?? source.Name;
            int appEntryScripts = 0;
            if (source.Kind == LinkKind.Player && ServerListings.IsStatusPage(page))
            {
                server = ServerListings.FromHtml(document, page, token);
                if (server != null) return;
                if (ServerListings.LinkedIcecastStatus(document, page) is Uri status)
                    Add(status.AbsoluteUri, LinkKind.ServerStatus);
            }
            if (SecureNetCallSign(page) is string callSign)
            {
                foreach (var script in document.Scripts.Where(s => !s.HasAttribute("src")))
                {
                    var match = SecureNetStream.Match(script.TextContent);
                    if (!match.Success || !PublicNetwork.TryUrl(match.Groups[1].Value, page, out var audio) ||
                        !HostIs(audio, "securenetsystems.net") || !audio.IsDefaultPort || audio.Scheme != "https" ||
                        audio.Query.Length != 0 || !audio.AbsolutePath.Trim('/').Equals(callSign, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Add(audio.AbsoluteUri, LinkKind.Audio, source.Name ?? callSign, true, nameOrigin: NameOrigin.Directory,
                        keepAddress: true);
                    return;
                }
                return;
            }
            if (IsTritonPlayer(page))
            {
                string? station = document.QuerySelector("meta[name='application-name']")?.GetAttribute("content");
                if (ProviderIdentifier(station))
                    Add($"https://{TritonHost}{TritonProvisioning}?version=1.9&station={station}",
                        LinkKind.DedicatedPlayer, source.Name ?? station);
                return;
            }
            if (IsGlobalPlayer(page))
            {
                if (document.QuerySelector("script#__NEXT_DATA__") is IElement config)
                {
                    using var state = JsonDocument.Parse(config.TextContent);
                    var playable = Property(Property(Property(state.RootElement, "props"), "pageProps"), "playable");
                    ReadPlayerSources(Property(playable, "playback"), title, requirePublicFlag: true);
                }
                return;
            }
            foreach (var config in document.QuerySelectorAll("[data-widget-radioplayer] script[type='application/json']").Take(4))
            {
                try
                {
                    using var state = JsonDocument.Parse(config.TextContent);
                    ReadPlayerSources(Property(state.RootElement, "fallback"), title, requirePublicFlag: false);
                }
                catch (JsonException) { }
            }
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
                int? listeners = null;
                foreach (var row in card!.QuerySelectorAll("tr"))
                {
                    var cells = row.QuerySelectorAll("td");
                    if (cells.Length < 2) continue;
                    string field = cells[0].TextContent.Trim();
                    if (field.Equals("Stream Name:", StringComparison.OrdinalIgnoreCase)) name = StationName(cells[1].TextContent);
                    listeners ??= ServerListings.HtmlListeners(field, cells[1].TextContent);
                }
                var mountName = Uri.UnescapeDataString(direct.AbsolutePath.Trim('/'));
                Add(direct.AbsoluteUri, LinkKind.Audio, name ?? Label(mountName), true, nameOrigin: NameOrigin.Directory,
                    wrapper: wrapper, listeners: listeners);
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
                Candidate(element.GetAttribute("src")!, title, true, baseUrl, pageAudio: discoverPageMedia && source.TuneInId == null);
            if (discoverPageMedia && source.TuneInId == null)
            {
                foreach (var element in document.QuerySelectorAll("[data-mp3]"))
                {
                    var raw = element.GetAttribute("data-mp3")!;
                    if (PublicNetwork.TryUrl(raw, baseUrl, out var audio) && FileIndex.AudioFormat(audio.AbsolutePath) == "MP3")
                    {
                        var name = Label(element.GetAttribute("data-title")) ?? title;
                        if (name != null && audio.IdnHost == "cdn.freesound.org" &&
                            audio.AbsolutePath.StartsWith("/previews/", StringComparison.Ordinal))
                            name += " preview";
                        Candidate(raw, name, true, baseUrl, pageAudio: true);
                    }
                }
            }
            foreach (var element in document.QuerySelectorAll("video[src], video source[src]"))
                Candidate(element.GetAttribute("src")!, title, relative: baseUrl, explicitVideo: true);
            foreach (var element in document.QuerySelectorAll("[data-videos]").Take(4))
            {
                if (element.GetAttribute("data-videos") is not { Length: > 0 and <= MaxVideoMetadataCharacters } metadata) continue;
                try
                {
                    using var videos = JsonDocument.Parse(metadata, new JsonDocumentOptions { MaxDepth = 32 });
                    if (videos.RootElement.ValueKind != JsonValueKind.Array) continue;
                    foreach (var row in videos.RootElement.EnumerateArray().Take(MaxVideoMetadataRows))
                    {
                        if (row.ValueKind != JsonValueKind.Object || !row.TryGetProperty("video", out var video) ||
                            video.ValueKind != JsonValueKind.Object || !video.TryGetProperty("meta", out var media) ||
                            media.ValueKind != JsonValueKind.Object || !media.TryGetProperty("mpx_is_livestream", out var live) ||
                            !(live.ValueKind == JsonValueKind.String && live.GetString() == "1" || live.ValueKind == JsonValueKind.True ||
                              live.ValueKind == JsonValueKind.Number && live.TryGetInt32(out int value) && value == 1) ||
                            !media.TryGetProperty("m3u8_url", out var link) || link.ValueKind != JsonValueKind.String ||
                            link.GetString() is not string address) continue;
                        Candidate(address, title, relative: baseUrl, explicitVideo: true);
                    }
                }
                catch (JsonException) { }
            }

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
                    var linkTitle = element.GetAttribute("title") ?? "";
                    bool publishedStream = element.LocalName == "a" && LikelyAudio(uri) &&
                        page.AbsolutePath.Trim('/').ToLowerInvariant() is "streaming-urls" or "stream-links" or "streamadressen";
                    bool directLink = element.LocalName == "a" && (SameSite(uri, site) || IsPlayerSubdomain(uri, site)) &&
                        linkTitle.StartsWith("Direct stream", StringComparison.OrdinalIgnoreCase);
                    if (directLink && linkTitle.Contains("subscription", StringComparison.OrdinalIgnoreCase)) continue;
                    bool audioType = type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase);
                    bool pageAudio = discoverPageMedia && source.TuneInId == null && element.LocalName == "a" &&
                        (audioType || FileIndex.AudioFormat(uri.AbsolutePath) != null);
                    var mediaName = pageAudio && article && label != null &&
                        (label.StartsWith("Listen", StringComparison.OrdinalIgnoreCase) || label.StartsWith("Play", StringComparison.OrdinalIgnoreCase))
                        ? title ?? label : label;
                    Candidate(raw, mediaName, audioType ||
                        property.StartsWith("og:audio", StringComparison.OrdinalIgnoreCase) || publishedStream || directLink, baseUrl,
                        type is "video/mp4" or "video/webm" ||
                        property is "og:video:url" or "og:video:secure_url" && (Playlist(uri) || HasExtension(uri, ".mp4", ".webm")),
                        pageAudio: pageAudio);
                    if (element.LocalName is "a" or "iframe" && (IsGlobalPlayer(uri) || IsGlobalRadioplayer(uri)))
                        Add(raw, LinkKind.DedicatedPlayer, label, relative: baseUrl);
                    else if (element.LocalName is "a" or "iframe" && (SecureNetCallSign(uri) != null || IsTritonPlayer(uri)))
                        Add(raw, LinkKind.DedicatedPlayer, title, relative: baseUrl);
                    if (element.LocalName == "iframe" && ListenerLink(uri.AbsolutePath + " " + element.GetAttribute("title")) &&
                        !HostIs(uri, "youtube.com") && !HostIs(uri, "spotify.com")) Add(raw, LinkKind.Player, label, relative: baseUrl);
                    if (element.LocalName == "a" && (PrimaryListeningPage(uri) || ListenerLink(descriptor) || ListenerHelp(uri, label)) &&
                        !LikelyAudio(uri) && !Playlist(uri)) AddPlayer(raw, uri, label, baseUrl);
                    if (element.LocalName == "script" && IsSiteScript(uri))
                    {
                        var filename = uri.AbsolutePath[(uri.AbsolutePath.LastIndexOf('/') + 1)..];
                        if (ListenerLink(filename)) Add(raw, LinkKind.Script, title, relative: baseUrl);
                        else if (uri.IsDefaultPort && page.AbsolutePath == "/" && SameSite(uri, page) &&
                                 appEntryScripts < 2 && AppEntryFile.IsMatch(filename))
                        {
                            Add(raw, LinkKind.Script, title, relative: baseUrl);
                            appEntryScripts++;
                        }
                    }
                }

                foreach (var attribute in element.Attributes)
                {
                    if (!attribute.Name.StartsWith("data-", StringComparison.OrdinalIgnoreCase)) continue;
                    bool audio = attribute.Name.Contains("stream", StringComparison.OrdinalIgnoreCase) ||
                        attribute.Name.Contains("audio", StringComparison.OrdinalIgnoreCase);
                    if (audio || attribute.Name is "data-src" or "data-url")
                    {
                        bool playerSource = audio || element.LocalName is "audio" or "source" ||
                            PlayerContainer(element.GetAttribute("class")) || PlayerContainer(element.Id);
                        if (playerSource)
                        {
                            var sources = AudioSourceSeparator.Split(attribute.Value, 9);
                            if (sources.Length > 1)
                            {
                                foreach (var address in sources.Take(8)) Candidate(address, title, true, baseUrl);
                                continue;
                            }
                        }
                        Candidate(attribute.Value, title, audio, baseUrl);
                    }
                }
            }
            foreach (var script in document.Scripts.Where(s => !s.HasAttribute("src")))
                ScanText(script.TextContent, title, playlistBase: baseUrl);
            // Some station sites print the direct address as text instead of a link.
            ScanText(document.Body?.TextContent ?? "", title, playerCode: false);
        }

        void TritonMounts(string body)
        {
            using var reader = XmlReader.Create(new StringReader(body), new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null
            });
            var root = XDocument.Load(reader).Root;
            if (root?.Name != TritonSchema + "live_stream_config") return;
            foreach (var mount in root.Element(TritonSchema + "mountpoints")?.Elements(TritonSchema + "mountpoint").Take(8) ?? [])
            {
                string? name = (string?)mount.Element(TritonSchema + "mount");
                var auth = (string?)mount.Element(TritonSchema + "authentication");
                if (!ProviderIdentifier(name) ||
                    (string?)mount.Element(TritonSchema + "status")?.Element(TritonSchema + "status-code") != "200" ||
                    auth != "0")
                    continue;
                string? codec = (string?)mount.Element(TritonSchema + "media-format")
                    ?.Element(TritonSchema + "audio")?.Attribute("codec");
                var extension = codec?.ToLowerInvariant() switch
                {
                    "mp3" => ".mp3",
                    "aaclc" or "heaacv1" or "heaacv2" => ".aac",
                    _ => null
                };
                // Keep the documented redirector instead of freezing the current edge server.
                if (extension != null)
                    Add($"https://{TritonHost}{TritonRedirect}{name}{extension}", LinkKind.Audio,
                        source.Name ?? name, true, nameOrigin: NameOrigin.Directory, keepAddress: true);
            }
        }

        void ReadPlayerSources(JsonElement values, string? title, bool requirePublicFlag)
        {
            if (values.ValueKind != JsonValueKind.Array) return;
            foreach (var entry in values.EnumerateArray().Take(12))
            {
                var capability = Property(entry, "canUse");
                if (requirePublicFlag && (capability.ValueKind != JsonValueKind.String || capability.GetString() != "true"))
                    continue;
                var address = Property(entry, "url");
                if (address.ValueKind == JsonValueKind.String) Candidate(address.GetString()!, title, true);
            }
        }

        void ScanText(string value, string? name, bool playerCode = true, Uri? playlistBase = null)
        {
            if (playerCode) value = CommentLines.Replace(value, "");
            value = value.Replace("\\/", "/").Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003A", ":", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase);
            foreach (Match match in AssignedUrls.Matches(value)) Candidate(match.Groups[1].Value, name, true);
            foreach (Match match in WebUrls.Matches(value)) Candidate(match.Value.TrimEnd(')', ']', ','), name);
            if (!playerCode) return;
            playlistBase ??= source.Kind == LinkKind.Script ? source.FoundOn : page;
            foreach (Match match in AssignedPlaylists.Matches(value))
            {
                if (PublicNetwork.TryUrl(WebUtility.HtmlDecode(match.Groups[1].Value), playlistBase, out var playlist) && Playlist(playlist))
                    Add(playlist.AbsoluteUri, LinkKind.Playlist, name);
            }
            foreach (Match match in ScriptLiterals.Matches(value)) AddPlayerLiteral(match, LinkKind.Script);
            foreach (Match match in WindowLiterals.Matches(value)) AddPlayerLiteral(match, LinkKind.Player);

            void AddPlayerLiteral(Match match, LinkKind kind)
            {
                var path = match.Groups[1].Value;
                var baseUri = source.Kind == LinkKind.Script ? source.FoundOn : page;
                if (!PublicNetwork.TryUrl(path, baseUri, out var target) || !ListenerLink(target.AbsolutePath)) return;
                if (kind == LinkKind.Script && IsSiteScript(target)) Add(path, kind, name, relative: baseUri);
                else if (kind == LinkKind.Player) AddPlayer(path, target, name, baseUri);
            }
        }

        void AddPlayer(string raw, Uri target, string? name, Uri relative)
        {
            if (IsPlayerSubdomain(target, site) ||
                SameSite(target, site) && (PrimaryListeningPage(target) || ListenerHelp(target, name)))
                Add(raw, LinkKind.DedicatedPlayer, name, relative: relative);
            else if (SameSite(target, site)) Add(raw, LinkKind.Player, name, relative: relative);
        }

        bool IsSiteScript(Uri target) => SameSite(target, site) ||
            (IsPlayerSubdomain(page, site) && SameSite(target, page));

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
            const string streamTag = "#EXT-X-STREAM-INF:";
            const string mediaTag = "#EXT-X-MEDIA:";
            bool master = lines.Any(l => l.StartsWith(streamTag, StringComparison.Ordinal));
            bool hasAudioRendition = lines.Any(l => l.StartsWith(mediaTag, StringComparison.Ordinal) && Attr(l, "TYPE") == "AUDIO" ||
                l.StartsWith(streamTag, StringComparison.Ordinal) && Attr(l, "CODECS")?.Split(',') is { Length: > 0 } codecs &&
                codecs.All(IsAudioCodec));
            bool lookForVideo = source.VideoHint || !source.AudioHint && !hasAudioRendition;
            bool audioOnlyVariant = false, videoVariant = false;
            foreach (string line in lines)
            {
                if (line.StartsWith(streamTag, StringComparison.Ordinal))
                {
                    var codes = Attr(line, "CODECS")?.Split(',') ?? [];
                    audioOnlyVariant = codes.Length > 0 && codes.All(IsAudioCodec);
                    // A checked media playlist is usable on its own only when it does not depend on a separate audio group.
                    videoVariant = lookForVideo && Attr(line, "AUDIO") == null &&
                        (codes.Any(IsVideoCodec) || source.VideoHint && codes.Length == 0);
                }
                else if (line.StartsWith(mediaTag, StringComparison.Ordinal) && Attr(line, "TYPE") == "AUDIO")
                    Add(Attr(line, "URI"), LinkKind.Playlist, source.Name, true);
                else if (line.Length != 0 && !line.StartsWith('#'))
                {
                    if (audioOnlyVariant) Add(line, LinkKind.Playlist, source.Name, true);
                    else if (videoVariant)
                        Add(line, LinkKind.Playlist, source.Name, videoHint: true, videoMaster: source.VideoMaster ?? page);
                    audioOnlyVariant = videoVariant = false;
                }
            }
            var segments = lines.Where(l => l.Length > 0 && !l.StartsWith('#')).ToArray();
            bool liveMedia = !master && lines.Any(l => l.StartsWith("#EXT-X-TARGETDURATION:", StringComparison.Ordinal)) &&
                !lines.Contains("#EXT-X-ENDLIST") && !lines.Contains("#EXT-X-PLAYLIST-TYPE:VOD") && segments.Length > 0;
            if (source.VideoHint && liveMedia &&
                !lines.Any(l => l.StartsWith("#EXT-X-DEFINE:", StringComparison.Ordinal) ||
                                l.StartsWith("#EXT-X-CONTENT-STEERING:", StringComparison.Ordinal) ||
                                l == "#EXT-X-I-FRAMES-ONLY"))
                videoCdnHosts = VideoHosts(segments.Concat(lines
                    .Where(l => l.StartsWith("#EXT-X-", StringComparison.Ordinal) && l.Contains("URI=", StringComparison.Ordinal))
                    .Select(l => Attr(l, "URI") ?? "")));
            hls = !source.VideoHint && liveMedia &&
                  lines.Where(l => l.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal) ||
                                   l.StartsWith("#EXT-X-PART:", StringComparison.Ordinal) ||
                                   l.StartsWith("#EXT-X-PRELOAD-HINT:", StringComparison.Ordinal))
                       .All(l => PublicNetwork.TryUrl(Attr(l, "URI") ?? "", page, out _)) &&
                  segments.All(l => PublicNetwork.TryUrl(l, page, out var part) &&
                      (source.AudioHint || HasExtension(part, ".aac", ".mp3", ".ac3", ".ec3")));

            string[]? VideoHosts(IEnumerable<string> references)
            {
                var hosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var raw in references)
                {
                    token.ThrowIfCancellationRequested();
                    if (!PublicNetwork.TryUrl(raw, page, out var media) || (page.Scheme == "https" && media.Scheme != "https"))
                        return null;
                    if (media.HostNameType == UriHostNameType.Dns && media.IdnHost != page.IdnHost &&
                        hosts.Add(media.IdnHost) && hosts.Count > MaxVideoCdnHosts) return null;
                }
                return hosts.ToArray();
            }
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

    private static bool IsPlayerSubdomain(Uri candidate, Uri site)
    {
        var siteHost = site.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (siteHost.StartsWith("www.", StringComparison.Ordinal)) siteHost = siteHost[4..];
        var host = candidate.IdnHost.TrimEnd('.').ToLowerInvariant();
        // Do not infer a shared parent domain or follow arbitrary sibling subdomains.
        return host == "play." + siteHost || host == "player." + siteHost ||
               host == "listen." + siteHost || host == "radio." + siteHost ||
               host == "stream." + siteHost || host == "streams." + siteHost;
    }
    private static bool SameOrigin(Uri a, Uri b) => a.Scheme == b.Scheme && a.Port == b.Port && a.IdnHost == b.IdnHost;
    private static bool IsSwedishRadioSite(Uri uri) => uri.IdnHost is "sverigesradio.se" or "www.sverigesradio.se";
    private static bool IsGlobalPlayer(Uri uri) => uri.IsDefaultPort && uri.IdnHost is "globalplayer.com" or "www.globalplayer.com" &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["live", _, ..];
    private static bool IsGlobalRadioplayer(Uri uri) => uri.IsDefaultPort && uri.IdnHost == "radioplayer.musicradio.com" &&
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries) is ["radio", _, ..];
    private static JsonElement Property(JsonElement value, string key) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out var property) ? property : default;

    private static bool PlayerContainer(string? value) => value != null &&
        value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Any(part =>
            part.Equals("player", StringComparison.OrdinalIgnoreCase) ||
            part.EndsWith("-player", StringComparison.OrdinalIgnoreCase));

    private static bool PrimaryListeningPage(Uri uri) =>
        uri.AbsolutePath.Trim('/').ToLowerInvariant() is
            "listen" or "listen-live" or "how-to-listen" or "ways-to-listen" or "listen-online" or
            "live" or "live-now" or "live-stream" or "livestream" or "livestreams" or "streaming-urls" or
            "streams" or "watch" or "watch-live" or "player" or "radio-player" or "en-direct" or "direct" or "ao-vivo" or "en-vivo" or "direkt" ||
        uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Contains("how-to-listen", StringComparer.OrdinalIgnoreCase);

    internal static bool IsWatchPage(Uri uri) => uri.AbsolutePath.Trim('/').ToLowerInvariant() is "watch" or "watch-live";

    private static bool ListenerHelp(Uri uri, string? name) =>
        uri.AbsolutePath.Trim('/').ToLowerInvariant() is "help" or "faq" &&
        name?.ToLowerInvariant() is "help" or "faq";
    private static bool HostIs(Uri uri, string host) => uri.IdnHost == host || uri.IdnHost.EndsWith("." + host, StringComparison.Ordinal);

    private static bool ProviderIdentifier(string? value) =>
        value is { Length: > 0 and <= 40 } && value.All(c => Ascii.IsLetterOrDigit(c) || c is '_' or '-');

    private static string? SecureNetCallSign(Uri uri)
    {
        if (!uri.IsDefaultPort || !HostIs(uri, "securenetsystems.net")) return null;
        return uri.AbsolutePath.Trim('/').Split('/') is ["cirruscontent" or "v5", var callSign] &&
            ProviderIdentifier(callSign) ? callSign : null;
    }

    private static bool IsTritonPlayer(Uri uri) =>
        uri.IsDefaultPort && uri.IdnHost == "player.listenlive.co" &&
        uri.AbsolutePath.Trim('/') is { Length: > 0 and <= 12 } id && id.All(Ascii.IsDigit);
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
    private static bool IsVideoCodec(string codec) => new[] { "avc1", "avc3", "hvc1", "hev1", "av01", "vp09", "vp9" }
        .Any(value => codec.Trim().StartsWith(value, StringComparison.OrdinalIgnoreCase));
    private static string? Attr(string line, string name) =>
        HlsAttributes.Matches(line[(line.IndexOf(':') + 1)..]).Cast<Match>()
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
