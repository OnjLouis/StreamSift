using System.Net;
using System.Text;

namespace StreamSift;

public sealed record Station(string Name, Uri Url, string Format, int? Bitrate, Uri FoundOn, bool IsHls, int? Listeners = null,
    bool IsVideo = false);
public sealed record SearchResult(IReadOnlyList<Station> Stations, int Requests, int Unreachable, bool LimitReached,
    bool EntryWasWebsite = false);

internal enum LinkKind { Audio, Video, Playlist, Player, DedicatedPlayer, Script, Catalogue, ServerStatus }
internal enum NameOrigin { Inferred, Directory, Playlist }
internal sealed record Link(Uri Url, Uri FoundOn, LinkKind Kind, string? Name = null, bool AudioHint = false,
    NameOrigin NameOrigin = NameOrigin.Inferred, Uri? Wrapper = null, bool KeepAddress = false, int? Listeners = null,
    string? TuneInId = null, bool VideoHint = false, Uri? VideoMaster = null);

public sealed class Discovery(HttpClient client)
{
    private const int PageRequests = 36, PageResults = 24, CatalogueRequests = 116, CatalogueResults = 96;
    private const int MaxLinks = 200, MaxRedirects = 5;
    private const int TextLimit = 768 * 1024, WatchPageTextLimit = 2 * 1024 * 1024, AudioProbeLimit = 4096;
    private const int ServerProbeBatch = 4, ServerRequestLimit = 600;
    private const int CompatibilityProbeCap = 4;
    private static readonly TimeSpan ScanLimit = TimeSpan.FromSeconds(38), RequestLimit = TimeSpan.FromSeconds(7);
    private static readonly TimeSpan ServerScanLimit = TimeSpan.FromSeconds(65), ServerProbeLimit = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan CompatibilityProbeLimit = TimeSpan.FromSeconds(3);

    public Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null) =>
        FindCoreAsync(start, null, cancel, progress);

    internal Task<SearchResult> FindInferredAsync(Uri start, Uri? plain, CancellationToken cancel) =>
        FindCoreAsync(start, null, cancel, null, plain);

    internal Task<SearchResult> VerifyObservedAsync(Uri start, IEnumerable<Uri> candidates,
        CancellationToken cancel, IProgress<int>? progress = null) =>
        VerifyListedAsync(start, candidates.Select(candidate => new Link(candidate, start, LinkKind.Audio, start.IdnHost,
            !candidate.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))),
            cancel, progress);

    internal Task<SearchResult> VerifyListedAsync(Uri start, IEnumerable<Link> candidates,
        CancellationToken cancel, IProgress<int>? progress = null) =>
        FindCoreAsync(start, candidates, cancel, progress);

    private async Task<SearchResult> FindCoreAsync(Uri start, IEnumerable<Link>? observed,
        CancellationToken cancel, IProgress<int>? progress, Uri? plain = null)
    {
        var todo = new PriorityQueue<(Link Link, int Depth), (int Rank, int Order)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, Link>(StringComparer.Ordinal);
        var results = new Dictionary<string, Station>(StringComparer.Ordinal);
        var verifiedVideoMasters = new HashSet<Uri>();
        int requests = 0, serverRequests = 0, failures = 0, compatibilityProbes = 0;
        int requestLimit = PageRequests, resultLimit = PageResults;
        Uri? site = null;
        ServerListing? server = null;
        bool serverLimit = false, usedServerListing = false, entryWasWebsite = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(ScanLimit);
        Link? preferred = null;
        if (observed == null)
        {
            var catalogue = Extraction.OfficialCatalogue(start);
            preferred = catalogue != null ? new Link(catalogue, start, LinkKind.Catalogue) : Extraction.PublicPlayerSource(start);
            if (preferred?.TuneInId != null) start = preferred.FoundOn;
            if (preferred != null) Queue(preferred, 0);
            Queue(new Link(start, start, LinkKind.Player, TuneInId: preferred?.TuneInId), 0);
        }
        else
        {
            foreach (var candidate in observed.Take(PageResults))
                Queue(candidate, 0);
        }

        try
        {
            while (true)
            {
                deadline.Token.ThrowIfCancellationRequested();
                if (server != null)
                {
                    deadline.CancelAfter(ServerScanLimit);
                    await VerifyServerAsync(server);
                    server = null;
                    if (results.Count > 0 || serverLimit)
                    {
                        usedServerListing = true;
                        break;
                    }
                }
                if (requests >= requestLimit || results.Count >= resultLimit || !todo.TryDequeue(out var item, out _)) break;
                if (preferred != null && item.Depth == 0 && item.Link.Url == start && results.Count > 0)
                    continue; // The public playback route supplied playable audio; no web player is needed.
                bool entry = item.Depth == 0 && (item.Link.Url == start || item.Link.Url == plain);
                int beforeFetch = requests;
                using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestTime.CancelAfter(RequestLimit);
                var token = requestTime.Token;
                bool verified = false;
                try
                {
                    if (!PublicNetwork.TryUrl(item.Link.Url.AbsoluteUri, null, out var uri)) continue;
                    if (item.Link.VideoMaster is Uri master && verifiedVideoMasters.Contains(master)) continue;
                    if (SomaMount(uri) is string mount && results.TryGetValue(mount, out var existing) &&
                        (existing.Url.Scheme == "https" || uri.Scheme == "http")) continue;
                    using var response = await Fetch(uri, () =>
                    {
                        if (requests == requestLimit) throw new HttpRequestException("Request limit reached.");
                        requests++;
                        progress?.Report(requests + serverRequests);
                    }, token, item.Link.TuneInId);
                    var final = response.RequestMessage?.RequestUri ?? uri;
                    var status = observed == null && entry && item.Link.TuneInId == null
                        ? ServerListings.AdvertisedStatus(final, Header(response, "Server"), Header(response, "icy-notice2")) : null;
                    if (status != null)
                        Queue(new Link(status, final, LinkKind.ServerStatus), 1);
                    if (!response.IsSuccessStatusCode)
                    {
                        // A streaming server can publish JSON even when it does not serve an HTML index.
                        if (status == null) failures++;
                        continue;
                    }
                    if (entry) site ??= final;
                    seen.Add(final.AbsoluteUri);
                    var kind = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                    if (observed == null && entry && kind is "text/html" or "application/xhtml+xml") entryWasWebsite = true;
                    var stationName = Header(response, "icy-name");
                    var bitrate = int.TryParse(Header(response, "icy-br"), out int kbps) && kbps is > 0 and <= 10000
                        ? (int?)kbps : null;
                    await using var body = await response.Content.ReadAsStreamAsync(token);

                    // Some Icecast mounts have no extension, and some playlists have a generic MIME type.
                    if (kind is "video/mp4" or "video/webm")
                    {
                        var prefix = await Read(body, 12, token);
                        bool valid = kind == "video/mp4" ? prefix.Length == 12 && prefix.AsSpan(4, 4).SequenceEqual("ftyp"u8) :
                            prefix.AsSpan().StartsWith(new byte[] { 0x1a, 0x45, 0xdf, 0xa3 });
                        if (valid)
                        {
                            Add(final, item.Link, null, kind == "video/mp4" ? "MP4 video" : "WebM video", null, false, video: true);
                            verified = true;
                        }
                    }
                    else if (IsAudioType(kind) || kind is "application/octet-stream" or "application/ogg" || stationName != null || kind.Length == 0)
                    {
                        var prefix = await Read(body, AudioProbeLimit, token, stopOnAudio: true);
                        var codec = Sniff(prefix);
                        if (codec != null)
                        {
                            if (response.Content.Headers.ContentLength == null)
                            {
                                Uri? compatible = null;
                                if (codec == "MP3" && bitrate is int rate && stationName is { Length: > 0 } &&
                                    !item.Link.KeepAddress && item.Link.TuneInId == null &&
                                    (observed == null || final == item.Link.Url) && requests < requestLimit &&
                                    compatibilityProbes < CompatibilityProbeCap &&
                                    CompatibilityAddress(final, Header(response, "icy-url")) is Uri alternative &&
                                    seen.Add(alternative.AbsoluteUri))
                                {
                                    compatibilityProbes++;
                                    requests++;
                                    progress?.Report(requests + serverRequests);
                                    if (await VerifyCompatibilityAsync(alternative, stationName, rate, deadline.Token))
                                        compatible = alternative;
                                }
                                Add(final, item.Link, stationName, codec, bitrate, false, compatible);
                                verified = true;
                            }
                            continue; // A finite MP3 or podcast is not a live station.
                        }
                        if (!Extraction.LooksLikeText(prefix)) continue;
                        var textCap = Extraction.IsWatchPage(final) ? WatchPageTextLimit : TextLimit;
                        var rest = await Read(body, textCap - prefix.Length + 1, token);
                        await Evaluate([..prefix, ..rest], textCap, kind, final, item.Link, item.Depth, token);
                    }
                    else if (kind.StartsWith("text/") || kind.Contains("json") || kind.Contains("xml") ||
                             kind.Contains("mpegurl") || kind.Contains("scpls") || kind.Contains("javascript") ||
                             kind == "video/x-ms-asf")
                    {
                        var textCap = Extraction.IsWatchPage(final) ? WatchPageTextLimit : TextLimit;
                        await Evaluate(await Read(body, textCap + 1, token), textCap, kind, final, item.Link, item.Depth, token);
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested)
                {
                    if (!TryPlain()) failures++;
                }
                catch (HttpRequestException e)
                {
                    if ((e.HttpRequestError is not (HttpRequestError.ConnectionError or HttpRequestError.SecureConnectionError or
                        HttpRequestError.InvalidResponse or HttpRequestError.ResponseEnded)) || !TryPlain()) failures++;
                }
                catch (IOException) { failures++; }
                finally
                {
                    // A reverse proxy can serve the directory without exposing the corresponding mount.
                    if (!verified && item.Link.Wrapper is Uri wrapper)
                    {
                        var named = names.GetValueOrDefault(item.Link.Url.AbsoluteUri) ?? item.Link;
                        Queue(named with { Url = wrapper, Kind = LinkKind.Playlist, Wrapper = null }, item.Depth);
                    }
                }

                bool TryPlain()
                {
                    // Retry only a scheme-less address with an explicit port when HTTPS could not connect.
                    if (plain == null || !entry || item.Link.Url != start || site != null || requests - beforeFetch != 1)
                        return false;
                    Queue(new Link(plain, plain, LinkKind.Player), 0);
                    return true;
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        cancel.ThrowIfCancellationRequested();
        var ordered = usedServerListing ? results.Values.OrderBy(s => s.Name, StringComparer.InvariantCultureIgnoreCase) :
            results.Values.OrderBy(s => s.IsVideo).ThenBy(s => s.IsHls).ThenBy(s => s.Url.Scheme != "https");
        return new(ordered.ToArray(), requests + serverRequests, failures,
            deadline.IsCancellationRequested || serverLimit || (!usedServerListing &&
            ((todo.Count > 0 && (requests >= requestLimit || results.Count >= resultLimit)) || seen.Count >= MaxLinks)),
            entryWasWebsite);

        void Queue(Link link, int depth)
        {
            var key = link.Url.AbsoluteUri;
            if (link.NameOrigin != NameOrigin.Inferred && link.Name != null &&
                (!names.TryGetValue(key, out var current) || link.NameOrigin > current.NameOrigin)) names[key] = link;
            if (depth > 4 || seen.Count >= MaxLinks || !seen.Add(link.Url.AbsoluteUri)) return;
            int rank = link.Kind switch
            {
                LinkKind.ServerStatus => -3,
                LinkKind.Catalogue => -2,
                LinkKind.Audio when link.AudioHint => -1,
                LinkKind.DedicatedPlayer or LinkKind.Script => 0,
                LinkKind.Audio => 1,
                LinkKind.Playlist or LinkKind.Video => 2,
                _ => 3
            };
            todo.Enqueue((link, depth), (rank, seen.Count));
        }

        async Task Evaluate(byte[] content, int textCap, string type, Uri address, Link source, int depth, CancellationToken requestToken)
        {
            var text = Encoding.UTF8.GetString(content, 0, Math.Min(content.Length, textCap));
            if (observed != null && !type.Contains("mpegurl") && !type.Contains("scpls") &&
                !type.Contains("xspf") && type != "video/x-ms-asf" &&
                !text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t').StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase))
                return; // Browser candidates are media, not new starting points for the website crawl.
            if (source.Kind == LinkKind.Video && !text.TrimStart('\uFEFF', ' ', '\r', '\n', '\t')
                    .StartsWith("#EXTM3U", StringComparison.OrdinalIgnoreCase)) return;
            var info = Extraction.Read(text, type, address, source, site ?? start, deadline.Token);
            if (observed == null && info.Server != null && (depth == 0 || source.Kind == LinkKind.ServerStatus))
                server = info.Server;
            if (info.IsDirectory && observed == null)
            {
                requestLimit = CatalogueRequests;
                resultLimit = CatalogueResults;
            }
            if (info.LiveHls) Add(address, source, null, "HLS", null, true);
            if (content.Length <= textCap && info.VideoCdnHosts is string[] hosts &&
                await PublicNetwork.ArePublicMediaHostsAsync(hosts, requestToken))
            {
                Add(address, source, null, "HLS video", null, true, video: true);
                if (source.VideoMaster != null) verifiedVideoMasters.Add(source.VideoMaster);
            }
            foreach (var link in info.Links) Queue(link, depth + 1);
        }

        async Task VerifyServerAsync(ServerListing listing)
        {
            bool limited = listing.Truncated;
            foreach (var mount in listing.Mounts)
                if (mount.Listeners is int count && results.TryGetValue(mount.Address.AbsoluteUri, out var saved))
                    results[mount.Address.AbsoluteUri] = saved with { Listeners = count };
            var remaining = listing.Mounts.Where(m => !results.ContainsKey(m.Address.AbsoluteUri));
            foreach (var batch in remaining.Chunk(ServerProbeBatch))
            {
                if (deadline.IsCancellationRequested || serverRequests >= ServerRequestLimit) { limited = true; break; }
                var probes = await Task.WhenAll(batch.Select(mount => ProbeMountAsync(mount, listing.Page, CountRequest, deadline.Token)));
                foreach (var probe in probes)
                {
                    if (probe.Station != null) results.TryAdd(probe.Station.Url.AbsoluteUri, probe.Station);
                    else failures++;
                    limited |= probe.RateLimited;
                }
                progress?.Report(requests + Math.Min(serverRequests, ServerRequestLimit));
                if (probes.Any(probe => probe.RateLimited)) break;
            }
            serverLimit |= limited || serverRequests > ServerRequestLimit;
            serverRequests = Math.Min(serverRequests, ServerRequestLimit);

            void CountRequest()
            {
                if (Interlocked.Increment(ref serverRequests) > ServerRequestLimit)
                    throw new HttpRequestException("Server listing request limit reached.");
            }
        }

        void Add(Uri address, Link source, string? name, string format, int? bitrate, bool hls, Uri? compatible = null,
            bool video = false)
        {
            var stable = names.GetValueOrDefault(source.Url.AbsoluteUri) ?? names.GetValueOrDefault(address.AbsoluteUri);
            var title = stable?.Name ?? Extraction.StationName(name) ?? Extraction.StationName(source.Name) ??
                Extraction.Label(address.Segments.LastOrDefault()?.Trim('/')) ?? address.Host;
            var key = SomaMount(address) ?? address.AbsoluteUri;
            var playbackAddress = !video && (source.KeepAddress || (observed != null && source.Kind == LinkKind.Audio))
                ? source.Url : address;
            var station = new Station(title, compatible ?? playbackAddress, format, bitrate, stable?.FoundOn ?? source.FoundOn, hls,
                stable?.Listeners ?? source.Listeners, video);
            if (!results.TryAdd(key, station) && address.Scheme == "https" && results[key].Url.Scheme == "http") results[key] = station;
        }
    }

    private static Uri? CompatibilityAddress(Uri stream, string? publicPage)
    {
        // This panel advertises the matching public player in ICY metadata. Its browser
        // route can have a media-player alias; do not guess on ordinary Icecast mounts.
        if (stream.Query.Length != 0 || publicPage == null || !PublicNetwork.TryUrl(publicPage, null, out var player) ||
            player.GetLeftPart(UriPartial.Authority) != stream.GetLeftPart(UriPartial.Authority) || player.Query.Length != 0)
            return null;
        var path = stream.AbsolutePath.TrimEnd('/');
        const string prefix = "/stream/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var slug = path[prefix.Length..];
        if (slug.Length is 0 or > 80 || !slug.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') ||
            !player.AbsolutePath.TrimEnd('/').Equals("/public/" + slug, StringComparison.OrdinalIgnoreCase)) return null;
        return new UriBuilder(stream) { Path = path + "/stream.mp3" }.Uri;
    }

    private async Task<bool> VerifyCompatibilityAsync(Uri alternative, string name, int bitrate, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(CompatibilityProbeLimit);
        try
        {
            using var response = await Fetch(alternative, static () => { }, deadline.Token, followRedirects: false);
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentLength.HasValue ||
                !string.Equals(Header(response, "icy-name"), name, StringComparison.OrdinalIgnoreCase) ||
                !int.TryParse(Header(response, "icy-br"), out var rate) || rate != bitrate) return false;
            await using var body = await response.Content.ReadAsStreamAsync(deadline.Token);
            return Sniff(await Read(body, AudioProbeLimit, deadline.Token, stopOnAudio: true)) == "MP3";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or OperationCanceledException)
        {
            cancel.ThrowIfCancellationRequested();
            return false;
        }
    }

    private async Task<(Station? Station, bool RateLimited)> ProbeMountAsync(ServerMount mount, Uri listing,
        Action count, CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(ServerProbeLimit);
        try
        {
            using var response = await Fetch(mount.Address, count, deadline.Token);
            if (response.StatusCode == HttpStatusCode.TooManyRequests) return (null, true);
            var content = response.Content;
            var kind = content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
            if (!response.IsSuccessStatusCode || content.Headers.ContentLength.HasValue ||
                !(ServerListings.AudioType(kind) || kind is "" or "application/octet-stream")) return (null, false);
            await using var body = await content.ReadAsStreamAsync(deadline.Token);
            string? format = Sniff(await Read(body, AudioProbeLimit, deadline.Token, stopOnAudio: true));
            if (format == null) return (null, false);
            string name = mount.Name ?? Extraction.StationName(Header(response, "icy-name")) ??
                Extraction.Label(mount.Address.AbsolutePath.Trim('/')) ?? mount.Address.Host;
            var bitrate = int.TryParse(Header(response, "icy-br"), out var kbps) && kbps is > 0 and <= 10000
                ? kbps : mount.Bitrate;
            return (new Station(name, mount.Address, format, bitrate, listing, false, mount.Listeners), false);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException) { return (null, false); }
    }

    private static string? SomaMount(Uri uri)
    {
        const string suffix = ".somafm.com";
        var host = uri.IdnHost;
        // SomaFM publishes numbered mirrors of each mount in its playlists.
        if (!uri.IsDefaultPort || !host.StartsWith("ice", StringComparison.Ordinal) ||
            !host.EndsWith(suffix, StringComparison.Ordinal)) return null;
        var mirror = host[3..^suffix.Length];
        return mirror.All(char.IsAsciiDigit) ? suffix + uri.PathAndQuery : null;
    }

    private async Task<HttpResponseMessage> Fetch(Uri uri, Action count, CancellationToken token, string? tuneInId = null,
        bool followRedirects = true)
    {
        for (var hop = 0; hop <= MaxRedirects; hop++)
        {
            count();
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version11,
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
            request.Headers.UserAgent.ParseAdd("StreamSift/0.1");
            request.Headers.Accept.ParseAdd("*/*");
            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
            if (!followRedirects || (int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not Uri location || !PublicNetwork.TryUrl(location.OriginalString, uri, out var next))
                    throw new HttpRequestException("The redirect does not lead to a public HTTP or HTTPS address.");
                if (tuneInId != null && !TuneIn.TryClean(next, tuneInId, out next))
                    throw new HttpRequestException("The redirect points to a different TuneIn station.");
                uri = next;
            }
        }
        throw new HttpRequestException("Too many redirects.");
    }

    private static string? Header(HttpResponseMessage r, string name) =>
        r.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static bool IsAudioType(string type) => type.StartsWith("audio/") &&
        !type.Contains("mpegurl") && !type.Contains("scpls") && !type.Contains("xspf");

    private static async Task<byte[]> Read(Stream source, int cap, CancellationToken token, bool stopOnAudio = false)
    {
        using var value = new MemoryStream();
        var buffer = new byte[8192];
        while (value.Length < cap)
        {
            var size = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, cap - value.Length)), token);
            if (size == 0) break;
            value.Write(buffer, 0, size);
            if (stopOnAudio && Sniff(value.GetBuffer().AsSpan(0, (int)value.Length)) != null) break;
        }
        return value.ToArray();
    }

    private static string? Sniff(ReadOnlySpan<byte> b)
    {
        if (b.StartsWith("ID3"u8) && b.Length >= 10)
        {
            int n = (b[6] << 21) | (b[7] << 14) | (b[8] << 7) | b[9];
            if (n > b.Length - 10) return null;
            b = b[(n + 10)..];
        }
        if (b.StartsWith("OggS"u8))
        {
            if (b.IndexOf("OpusHead"u8) >= 0) return "Ogg Opus";
            if (b.IndexOf("vorbis"u8) >= 0) return "Ogg Vorbis";
            if (b.IndexOf("FLAC"u8) >= 0) return "Ogg FLAC";
            return null;
        }
        if (b.StartsWith("fLaC"u8)) return "FLAC";
        if (b.Length >= 12 && b.StartsWith("RIFF"u8) && b[8..].StartsWith("WAVE"u8)) return "WAV";
        for (int i = 0; i < Math.Min(1024, b.Length - 7); i++)
        {
            if (b[i] != 0xff || (b[i + 1] & 0xe0) != 0xe0) continue;
            if ((b[i + 1] & 0xf6) == 0xf0 && ((b[i + 2] >> 2) & 15) <= 12)
            {
                int len = ((b[i + 3] & 3) << 11) | (b[i + 4] << 3) | (b[i + 5] >> 5);
                if (len >= 7 && i + len + 1 < b.Length && b[i + len] == 0xff &&
                    (b[i + len + 1] & 0xf6) == 0xf0) return "AAC";
                continue;
            }
            int version = (b[i + 1] >> 3) & 3, layer = (b[i + 1] >> 1) & 3;
            int rateIndex = (b[i + 2] >> 2) & 3, bitrate = b[i + 2] >> 4;
            if (version == 1 || layer is not (1 or 2) || rateIndex == 3 || bitrate is 0 or 15) continue;
            ReadOnlySpan<int> v1l3 = [0,32,40,48,56,64,80,96,112,128,160,192,224,256,320];
            ReadOnlySpan<int> v1l2 = [0,32,48,56,64,80,96,112,128,160,192,224,256,320,384];
            ReadOnlySpan<int> v2 = [0,8,16,24,32,40,48,56,64,80,96,112,128,144,160];
            ReadOnlySpan<int> rates = [44100,48000,32000];
            int rate = rates[rateIndex] >> (version == 3 ? 0 : version == 2 ? 1 : 2);
            int frameKbps = version != 3 ? v2[bitrate] : layer == 2 ? v1l2[bitrate] : v1l3[bitrate];
            int length = (version != 3 && layer == 1 ? 72000 : 144000) * frameKbps / rate + ((b[i + 2] >> 1) & 1);
            int j = i + length;
            if (j + 2 < b.Length && b[j] == 0xff && (b[j + 1] & 0xfe) == (b[i + 1] & 0xfe) &&
                (b[j + 2] & 12) == (b[i + 2] & 12)) return layer == 2 ? "MP2" : "MP3";
        }
        return null;
    }
}
