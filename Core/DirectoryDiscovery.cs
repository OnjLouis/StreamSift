using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml;
using System.Xml.Linq;

namespace StreamSift;

public sealed class DirectoryDiscovery(HttpClient client, string? cacheDirectory = null)
{
    private const int MaxCatalogueBytes = 96 * 1024 * 1024, MaxMatches = 24;
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(24), CacheTimeout = TimeSpan.FromSeconds(45);
    private const string MirrorPool = "all.api.radio-browser.info", MirrorSuffix = ".api.radio-browser.info";
    private const int MaxDiscoveredMirrors = 3;
    private static readonly TimeSpan MirrorLookupLimit = TimeSpan.FromSeconds(4), MirrorHeaderLimit = TimeSpan.FromSeconds(6);
    private static readonly string[] FallbackMirrors = ["de1.api.radio-browser.info", "de2.api.radio-browser.info"];
    private static readonly Uri Catalogue = new("https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=70000");
    private static readonly Uri ProviderPage = new("https://www.radio-browser.info/");
    private static readonly Uri Iprd = new("https://iprd-org.github.io/iprd/site_data/metadata/catalog.json");
    private static readonly Uri IprdPage = new("https://iprd-org.github.io/iprd/");
    private static readonly Uri XiphPage = new("https://dir.xiph.org/");
    private static readonly Uri XiphExport = new("https://dir.xiph.org/yp.xml");
    private static readonly TimeSpan XiphFallbackLimit = TimeSpan.FromSeconds(25);

    internal Func<CancellationToken, Task<IReadOnlyList<string?>>> FindMirrors { get; init; } = DiscoverMirrorsAsync;

    public async Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null)
    {
        if (!PublicNetwork.TryUrl(start.AbsoluteUri, null, out start)) throw new ArgumentException("Invalid public address.");
        var startHost = HostKey(start);
        var startPath = start.AbsolutePath.TrimEnd('/');
        // Default HTTP/HTTPS sites can be equivalent, but custom ports may be different radio tenants.
        var matches = await FindLinksAsync(ProviderPage, (_, home) => HostKey(home) == startHost &&
            (start.IsDefaultPort ? home.IsDefaultPort : home.Scheme == start.Scheme && home.Port == start.Port) &&
            (startPath.Length == 0 || home.AbsolutePath.TrimEnd('/') == startPath) ? 2 : 0, cancel);
        return await new Discovery(client).VerifyListedAsync(start, matches.Select(match => match.Link), cancel, progress);
    }

    internal async Task<SearchResult> FindByNameAsync(string name, CancellationToken cancel, bool preferHttpsPlayback = false)
    {
        string query = StationKey(SearchSequence.StationText(name));
        if (query.Length == 0) return new SearchResult([], 0, 0, false);
        var compactQuery = SearchSequence.CompactStationKey(query);
        int Score(DirectoryEntry entry, Uri _)
        {
            var key = StationKey(entry.Name ?? "");
            if (key == query) return 2;
            if (compactQuery.Length >= 4 && SearchSequence.CompactStationKey(key) == compactQuery) return 2;
            return (" " + key + " ").Contains(" " + query + " ", StringComparison.Ordinal) ? 1 : 0;
        }

        var results = new List<Ranked>();
        int failed = 0;
        foreach (var provider in new[] { ProviderPage, IprdPage })
        {
            try { results.AddRange(await FindLinksAsync(provider, Score, cancel)); }
            catch (Exception error) when (!cancel.IsCancellationRequested && IsDirectoryFailure(error))
            { failed++; }
        }
        cancel.ThrowIfCancellationRequested();
        var candidates = results.OrderByDescending(match => match.Score).Select(match => match.Link)
            .DistinctBy(link => link.Url.AbsoluteUri, StringComparer.Ordinal).Take(MaxMatches).ToArray();
        var discovery = new Discovery(client);
        var verified = await discovery.VerifyListedAsync(ProviderPage, candidates, cancel,
            preferHttpsPlayback: preferHttpsPlayback);
        if (verified.Stations.Any(station => !preferHttpsPlayback || station.Url.Scheme == Uri.UriSchemeHttps))
            return verified with { Unreachable = verified.Unreachable + failed };

        using var fallback = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        fallback.CancelAfter(XiphFallbackLimit);
        try
        {
            var attempted = candidates.Select(link => link.Url).ToHashSet();
            var matches = await FindLinksAsync(XiphPage, Score, fallback.Token);
            var xiph = await discovery.VerifyListedAsync(XiphPage,
                matches.Select(match => match.Link).Where(link => !attempted.Contains(link.Url)),
                fallback.Token, preferHttpsPlayback: preferHttpsPlayback);
            return new SearchResult(xiph.Stations.Concat(verified.Stations).DistinctBy(station => station.Url).Take(MaxMatches).ToArray(),
                verified.Requests + xiph.Requests, verified.Unreachable + xiph.Unreachable + failed,
                verified.LimitReached || xiph.LimitReached);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested && IsDirectoryFailure(error))
        {
            if (failed == 2) throw new HttpRequestException("The public radio directories were unavailable.", error);
            return verified with
            {
                Unreachable = verified.Unreachable + failed + 1,
                LimitReached = verified.LimitReached || fallback.IsCancellationRequested
            };
        }
    }

    private static bool IsDirectoryFailure(Exception error) => error is
        HttpRequestException or IOException or InvalidDataException or JsonException or XmlException or
        OperationCanceledException or UnauthorizedAccessException;

    private static string StationKey(string name) =>
        string.Join(' ', new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private sealed record Ranked(Link Link, int Score);

    private async Task<List<Ranked>> FindLinksAsync(Uri provider, Func<DirectoryEntry, Uri, int> score, CancellationToken cancel)
    {
        var root = cacheDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "StreamSift")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSift", "Cache"));
        var cache = Path.Combine(root, provider == XiphPage ? "xiph-v1.json.gz" :
            provider == IprdPage ? "iprd-v1.json.gz" : "radio-browser-v1.json.gz");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(CacheTimeout);
        var token = deadline.Token;
        bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheAge;
        List<Ranked> matches = [];
        if (fresh)
        {
            try { matches = await MatchAsync(cache, provider, score, token); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { fresh = false; }
        }
        if (!fresh)
        {
            Directory.CreateDirectory(root);
            var temp = Path.Combine(root, $"station-catalogue-{Guid.NewGuid():N}.tmp");
            try
            {
                using var response = await OpenAsync();
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var zip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    using var writer = new Utf8JsonWriter(zip);
                    using var bounded = new BoundedInput(source, token);
                    writer.WriteStartArray();
                    int kept = 0;
                    await foreach (var entry in ReadEntriesAsync(bounded, provider, token))
                    {
                        if (entry == null || !PublicNetwork.TryUrl(entry.Homepage ?? "", null, out var home) ||
                            !TryStream(entry.Url, home, out var stream)) continue;
                        writer.WriteStartObject();
                        writer.WriteString("name", Extraction.StationName(entry.Name));
                        writer.WriteString("homepage", entry.Homepage);
                        writer.WriteString("url", entry.Url);
                        if (TryStream(entry.Resolved, home, out var resolved) && resolved != stream)
                            writer.WriteString("url_resolved", entry.Resolved);
                        writer.WriteEndObject();
                        if (++kept % 128 == 0) await writer.FlushAsync(token);
                    }
                    writer.WriteEndArray();
                    await writer.FlushAsync(token);
                }
                // Keep the last complete catalogue if this download is incomplete or invalid.
                matches = await MatchAsync(temp, provider, score, token, validateAll: true);
                File.Move(temp, cache, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return matches;

        Task<HttpResponseMessage> OpenAsync()
        {
            if (provider == XiphPage) return client.GetAsync(XiphExport, HttpCompletionOption.ResponseHeadersRead, token);
            if (provider == IprdPage) return client.GetAsync(Iprd, HttpCompletionOption.ResponseHeadersRead, token);
            return OpenCatalogueAsync(token);
        }
    }

    private static async IAsyncEnumerable<DirectoryEntry?> ReadEntriesAsync(Stream source, Uri provider,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (provider == XiphPage)
        {
            await foreach (var entry in ReadXiphEntriesAsync(source, token))
                yield return entry;
            yield break;
        }
        if (provider != IprdPage)
        {
            await foreach (var entry in ReadRadioBrowserEntries(source, token))
                yield return entry;
            yield break;
        }
#if NET6_0
        var catalog = await JsonSerializer.DeserializeAsync<IprdCatalogue>(source, cancellationToken: token);
#else
        var catalog = await JsonSerializer.DeserializeAsync(source, CatalogueJson.Default.IprdCatalogue, token);
#endif
        if (catalog?.Stations == null) throw new JsonException("The IPRD stations list is missing.");
        foreach (var station in catalog.Stations)
        {
            token.ThrowIfCancellationRequested();
            if (station?.Streams == null) continue;
            foreach (var stream in station.Streams.Take(12))
                yield return new DirectoryEntry(station.Name, station.Website, stream?.Url, null);
        }
    }

    private static async IAsyncEnumerable<DirectoryEntry> ReadXiphEntriesAsync(Stream source,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        using var reader = XmlReader.Create(source, new XmlReaderSettings
        {
            Async = true, DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null,
            MaxCharactersInDocument = MaxCatalogueBytes
        });
        if (await reader.MoveToContentAsync() != XmlNodeType.Element || reader.Name != "directory")
            throw new XmlException("The Icecast directory document is missing.");
        while (await reader.ReadAsync())
        {
            token.ThrowIfCancellationRequested();
            if (reader.NodeType != XmlNodeType.Element || reader.Depth != 1 || reader.Name != "entry") continue;
            using var item = reader.ReadSubtree();
            var entry = await XElement.LoadAsync(item, LoadOptions.None, token);
            var name = Extraction.StationName((string?)entry.Element("server_name"));
            var address = (string?)entry.Element("listen_url");
            if (name != null && !string.IsNullOrWhiteSpace(address))
                yield return new DirectoryEntry(name, XiphPage.AbsoluteUri, address, null);
        }
    }

    private static async Task<List<Ranked>> MatchAsync(string path, Uri provider, Func<DirectoryEntry, Uri, int> score,
        CancellationToken token, bool validateAll = false)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (file.Length > MaxCatalogueBytes) throw new InvalidDataException("The cached directory exceeds the size limit.");
        await using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var bounded = new BoundedInput(zip);
        var exact = new Dictionary<string, Ranked>(StringComparer.Ordinal);
        var partial = new Dictionary<string, Ranked>(StringComparer.Ordinal);
        await foreach (var entry in ReadRadioBrowserEntries(bounded, token))
        {
            if (exact.Count == MaxMatches || entry == null ||
                !PublicNetwork.TryUrl(entry.Homepage ?? "", null, out var home)) continue;
            int rank = score(entry, home);
            if (rank == 0 || !TryStream(entry.Url, home, out var stream)) continue;
            var group = rank == 2 ? exact : partial;
            if (group.Count == MaxMatches) continue;
            Uri? resolved = TryStream(entry.Resolved, home, out var alternate) && alternate != stream ? alternate : null;
            group.TryAdd(stream.AbsoluteUri, new Ranked(new Link(stream, provider, LinkKind.Audio, Extraction.StationName(entry.Name),
                true, NameOrigin.Directory, resolved), rank));
            if (!validateAll && exact.Count == MaxMatches) break;
        }
        return exact.Values.Concat(partial.Values).DistinctBy(match => match.Link.Url.AbsoluteUri, StringComparer.Ordinal)
            .Take(MaxMatches).ToList();
    }

    private static bool TryStream(string? raw, Uri home, out Uri stream) =>
        PublicNetwork.TryUrl(raw ?? "", null, out stream) &&
        (!PublicNetwork.IsDiFm(home) || PublicNetwork.IsDiFm(stream));

    private async Task<HttpResponseMessage> OpenCatalogueAsync(CancellationToken token)
    {
        var hosts = MirrorOrder(await FindMirrors(token));
        foreach (var host in hosts)
        {
            token.ThrowIfCancellationRequested();
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(MirrorHeaderLimit);
            var endpoint = new UriBuilder(Catalogue) { Host = host }.Uri;
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("StreamSift/1.0 public-directory-lookup");
            try
            {
                var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
                if (response.IsSuccessStatusCode) return response;
                response.Dispose();
            }
            catch (Exception error) when (!token.IsCancellationRequested &&
                error is HttpRequestException or OperationCanceledException) { }
        }
        throw new HttpRequestException("No Radio Browser mirror responded successfully.");
    }

    // Radio Browser's DNS mirror discovery, suggested by Jeff Bishop (QUILL Radio).
    private static async Task<IReadOnlyList<string?>> DiscoverMirrorsAsync(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(MirrorLookupLimit);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(MirrorPool, deadline.Token);
            var names = addresses.Where(PublicNetwork.IsPublic).Distinct().Take(MaxDiscoveredMirrors * 2)
                .Select(async address =>
                {
                    try { return (await Dns.GetHostEntryAsync(address.ToString(), deadline.Token)).HostName; }
                    catch (SocketException) { return null; }
                });
            return await Task.WhenAll(names).WaitAsync(deadline.Token);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested &&
            error is SocketException or OperationCanceledException or ArgumentException) { return []; }
    }

    private static string[] MirrorOrder(IEnumerable<string?> discovered)
    {
        var hosts = discovered.OfType<string>().Select(name => name.TrimEnd('.').ToLowerInvariant())
            .Where(IsMirror).Distinct(StringComparer.Ordinal).ToArray();
        for (int index = hosts.Length - 1; index > 0; index--)
        {
            int other = Random.Shared.Next(index + 1);
            (hosts[index], hosts[other]) = (hosts[other], hosts[index]);
        }
        return [..hosts.Take(MaxDiscoveredMirrors), ..FallbackMirrors];

        static bool IsMirror(string host) => host.EndsWith(MirrorSuffix, StringComparison.Ordinal) &&
            !FallbackMirrors.Contains(host, StringComparer.Ordinal) &&
            host[..^MirrorSuffix.Length] is { Length: > 0 and <= 63 } label &&
            label[0] != '-' && label[^1] != '-' &&
            label.All(c => Ascii.IsLower(c) || Ascii.IsDigit(c) || c == '-');
    }

    private static string HostKey(Uri address)
    {
        var host = address.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private static IAsyncEnumerable<DirectoryEntry?> ReadRadioBrowserEntries(Stream stream, CancellationToken token) =>
#if NET6_0
        JsonSerializer.DeserializeAsyncEnumerable<DirectoryEntry>(stream, cancellationToken: token);
#else
        JsonSerializer.DeserializeAsyncEnumerable(stream, CatalogueJson.Default.DirectoryEntry, cancellationToken: token);
#endif

    private sealed class BoundedInput(Stream source, CancellationToken readToken = default) : Stream
    {
        private int read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            readToken.ThrowIfCancellationRequested();
            return Checked(source.Read(buffer, offset, count));
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Checked(await source.ReadAsync(buffer, cancellationToken.CanBeCanceled ? cancellationToken : readToken));
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private int Checked(int count)
        {
            read += count;
            if (read > MaxCatalogueBytes) throw new InvalidDataException("The public directory exceeds the size limit.");
            return count;
        }
    }
}

internal sealed record DirectoryEntry(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("homepage")] string? Homepage,
    [property: JsonPropertyName("url")] string? Url,
    [property: JsonPropertyName("url_resolved")] string? Resolved);

internal sealed record IprdCatalogue([property: JsonPropertyName("stations")] IprdStation?[]? Stations);
internal sealed record IprdStation(
    [property: JsonPropertyName("name")] string? Name,
    [property: JsonPropertyName("website")] string? Website,
    [property: JsonPropertyName("streams")] IprdStream?[]? Streams);
internal sealed record IprdStream([property: JsonPropertyName("url")] string? Url);

#if !NET6_0
[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(IprdCatalogue))]
internal sealed partial class CatalogueJson : JsonSerializerContext;
#endif
