using System.Net;
using System.Text;

namespace StreamSift;

public sealed record Station(string Name, Uri Url, string Format, int? Bitrate, Uri FoundOn, bool IsHls);
public sealed record SearchResult(IReadOnlyList<Station> Stations, int Requests, int Unreachable, bool LimitReached);

internal enum LinkKind { Audio, Playlist, Player, Script, Catalogue }
internal enum NameOrigin { Inferred, Directory, Playlist }
internal sealed record Link(Uri Url, Uri FoundOn, LinkKind Kind, string? Name = null, bool AudioHint = false,
    NameOrigin NameOrigin = NameOrigin.Inferred, Uri? Wrapper = null);

public sealed class Discovery(HttpClient client)
{
    private const int PageRequests = 36, PageResults = 24, CatalogueRequests = 116, CatalogueResults = 96;
    private const int MaxLinks = 200, MaxRedirects = 5;
    private const int TextLimit = 768 * 1024, AudioProbeLimit = 4096;
    private static readonly TimeSpan ScanLimit = TimeSpan.FromSeconds(38), RequestLimit = TimeSpan.FromSeconds(7);

    public async Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null)
    {
        var todo = new PriorityQueue<(Link Link, int Depth), (int Rank, int Order)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, Link>(StringComparer.Ordinal);
        var results = new Dictionary<string, Station>(StringComparer.Ordinal);
        int requests = 0, failures = 0;
        int requestLimit = PageRequests, resultLimit = PageResults;
        Uri? site = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(ScanLimit);
        Queue(new Link(start, start, LinkKind.Player), 0);

        try
        {
            while (requests < requestLimit && results.Count < resultLimit && todo.TryDequeue(out var item, out _))
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var requestTime = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                requestTime.CancelAfter(RequestLimit);
                var token = requestTime.Token;
                bool verified = false;
                try
                {
                    if (!PublicNetwork.TryUrl(item.Link.Url.AbsoluteUri, null, out var uri)) continue;
                    if (SomaMount(uri) is string mount && results.TryGetValue(mount, out var existing) &&
                        (existing.Url.Scheme == "https" || uri.Scheme == "http")) continue;
                    using var response = await Fetch(uri, () =>
                    {
                        if (requests == requestLimit) throw new HttpRequestException("Request limit reached.");
                        requests++;
                        progress?.Report(requests);
                    }, token);
                    if (!response.IsSuccessStatusCode) { failures++; continue; }
                    var final = response.RequestMessage?.RequestUri ?? uri;
                    site ??= final;
                    seen.Add(final.AbsoluteUri);
                    var kind = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? "";
                    var stationName = Header(response, "icy-name");
                    var bitrate = int.TryParse(Header(response, "icy-br"), out int kbps) && kbps is > 0 and <= 10000
                        ? (int?)kbps : null;
                    await using var body = await response.Content.ReadAsStreamAsync(token);

                    // Some Icecast mounts have no extension, and some playlists have a generic MIME type.
                    if (IsAudioType(kind) || kind is "application/octet-stream" or "application/ogg" || stationName != null || kind.Length == 0)
                    {
                        var prefix = await Read(body, AudioProbeLimit, token, stopOnAudio: true);
                        var codec = Sniff(prefix);
                        if (codec != null)
                        {
                            if (response.Content.Headers.ContentLength == null)
                            {
                                Add(final, item.Link, stationName, codec, bitrate, false);
                                verified = true;
                            }
                            continue; // A finite MP3 or podcast is not a live station.
                        }
                        if (!Extraction.LooksLikeText(prefix)) continue;
                        var rest = await Read(body, TextLimit - prefix.Length, token);
                        Evaluate(Encoding.UTF8.GetString([..prefix, ..rest]), kind, final, item.Link, item.Depth);
                    }
                    else if (kind.StartsWith("text/") || kind.Contains("json") || kind.Contains("xml") ||
                             kind.Contains("mpegurl") || kind.Contains("scpls") || kind.Contains("javascript") ||
                             kind == "video/x-ms-asf")
                    {
                        Evaluate(Encoding.UTF8.GetString(await Read(body, TextLimit, token)), kind, final, item.Link, item.Depth);
                    }
                }
                catch (OperationCanceledException) when (!deadline.IsCancellationRequested) { failures++; }
                catch (Exception e) when (e is HttpRequestException or IOException) { failures++; }
                finally
                {
                    // A reverse proxy can serve the directory without exposing the corresponding mount.
                    if (!verified && item.Link.Wrapper is Uri wrapper)
                    {
                        var named = names.GetValueOrDefault(item.Link.Url.AbsoluteUri) ?? item.Link;
                        Queue(named with { Url = wrapper, Kind = LinkKind.Playlist, Wrapper = null }, item.Depth);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        cancel.ThrowIfCancellationRequested();
        return new(results.Values.OrderBy(s => s.IsHls).ThenBy(s => s.Url.Scheme != "https").ToArray(), requests, failures,
            deadline.IsCancellationRequested || (todo.Count > 0 && (requests >= requestLimit || results.Count >= resultLimit)) ||
            seen.Count >= MaxLinks);

        void Queue(Link link, int depth)
        {
            var key = link.Url.AbsoluteUri;
            if (link.NameOrigin != NameOrigin.Inferred && link.Name != null &&
                (!names.TryGetValue(key, out var current) || link.NameOrigin > current.NameOrigin)) names[key] = link;
            if (depth > 4 || seen.Count >= MaxLinks || !seen.Add(link.Url.AbsoluteUri)) return;
            int rank = link.Kind switch { LinkKind.Catalogue => -1, LinkKind.Audio => 0, LinkKind.Playlist => 1, LinkKind.Script => 2, _ => 3 };
            todo.Enqueue((link, depth), (rank, seen.Count));
        }

        void Evaluate(string text, string type, Uri address, Link source, int depth)
        {
            var info = Extraction.Read(text, type, address, source, site!, deadline.Token);
            if (info.IsDirectory)
            {
                requestLimit = CatalogueRequests;
                resultLimit = CatalogueResults;
            }
            if (info.LiveHls) Add(address, source, null, "HLS", null, true);
            foreach (var link in info.Links) Queue(link, depth + 1);
        }

        void Add(Uri address, Link source, string? name, string format, int? bitrate, bool hls)
        {
            var stable = names.GetValueOrDefault(source.Url.AbsoluteUri) ?? names.GetValueOrDefault(address.AbsoluteUri);
            var title = stable?.Name ?? Extraction.StationName(name) ?? Extraction.StationName(source.Name) ??
                Extraction.Label(address.Segments.LastOrDefault()?.Trim('/')) ?? address.Host;
            var key = SomaMount(address) ?? address.AbsoluteUri;
            var station = new Station(title, address, format, bitrate, stable?.FoundOn ?? source.FoundOn, hls);
            if (!results.TryAdd(key, station) && address.Scheme == "https" && results[key].Url.Scheme == "http") results[key] = station;
        }
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

    private async Task<HttpResponseMessage> Fetch(Uri uri, Action count, CancellationToken token)
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
            if ((int)response.StatusCode is not (301 or 302 or 303 or 307 or 308)) return response;
            using (response)
            {
                if (response.Headers.Location is not Uri location || !PublicNetwork.TryUrl(location.OriginalString, uri, out var next))
                    throw new HttpRequestException("The redirect does not lead to a public HTTP or HTTPS address.");
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
