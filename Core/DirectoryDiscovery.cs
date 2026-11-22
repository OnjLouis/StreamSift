using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamSift;

public sealed class DirectoryDiscovery(HttpClient client, string? cacheDirectory = null)
{
    private const int MaxCatalogueBytes = 96 * 1024 * 1024, MaxMatches = 24;
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(24), CacheTimeout = TimeSpan.FromSeconds(45);
    private const string MirrorPool = "all.api.radio-browser.info", MirrorSuffix = ".api.radio-browser.info";
    private const int DiscoveredMirrors = 4;
    private static readonly TimeSpan MirrorLookupLimit = TimeSpan.FromSeconds(5);
    private static readonly string[] FallbackMirrors = ["de1.api.radio-browser.info", "de2.api.radio-browser.info"];
    private static readonly Uri Catalogue = new("https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=70000");
    private static readonly Uri ProviderPage = new("https://www.radio-browser.info/");
    private static readonly Uri Iprd = new("https://iprd-org.github.io/iprd/site_data/metadata/catalog.json");
    private static readonly Uri IprdPage = new("https://iprd-org.github.io/iprd/");

    internal Func<CancellationToken, Task<IReadOnlyList<string?>>> FindMirrors { get; init; } = DiscoverMirrorsAsync;

    public async Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null)
    {
        if (!PublicNetwork.TryUrl(start.AbsoluteUri, null, out start)) throw new ArgumentException("Invalid public address.");
        var startHost = HostKey(start);
        var startPath = start.AbsolutePath.TrimEnd('/');
        var matches = await FindLinksAsync(ProviderPage, (_, home) => HostKey(home) == startHost &&
            (startPath.Length == 0 || home.AbsolutePath.TrimEnd('/') == startPath) ? 2 : 0, cancel);
        return await new Discovery(client).VerifyListedAsync(start, matches.Select(match => match.Link), cancel, progress);
    }

    internal async Task<SearchResult> FindByNameAsync(string name, CancellationToken cancel)
    {
        string query = StationKey(name);
        if (query.Length == 0) return new SearchResult([], 0, 0, false);
        int Score(DirectoryEntry entry, Uri _)
        {
            var key = StationKey(entry.Name ?? "");
            if (key == query) return 2;
            return (" " + key + " ").Contains(" " + query + " ", StringComparison.Ordinal) ? 1 : 0;
        }

        var results = new List<Ranked>();
        int failed = 0;
        foreach (var provider in new[] { ProviderPage, IprdPage })
        {
            try { results.AddRange(await FindLinksAsync(provider, Score, cancel)); }
            catch (Exception error) when (!cancel.IsCancellationRequested && error is
                HttpRequestException or IOException or InvalidDataException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            { failed++; }
        }
        cancel.ThrowIfCancellationRequested();
        if (failed == 2) throw new HttpRequestException("Both public radio directories were unavailable.");
        var candidates = results.OrderByDescending(match => match.Score).Select(match => match.Link)
            .DistinctBy(link => link.Url.AbsoluteUri, StringComparer.Ordinal).Take(MaxMatches);
        var verified = await new Discovery(client).VerifyListedAsync(ProviderPage, candidates, cancel);
        return verified with { Unreachable = verified.Unreachable + failed };
    }

    private static string StationKey(string name) =>
        string.Join(' ', new string(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : ' ').ToArray())
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private sealed record Ranked(Link Link, int Score);

    private async Task<List<Ranked>> FindLinksAsync(Uri provider, Func<DirectoryEntry, Uri, int> score, CancellationToken cancel)
    {
        var root = cacheDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "StreamSift")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSift", "Cache"));
        bool secondary = provider == IprdPage;
        var cache = Path.Combine(root, secondary ? "iprd-v1.json.gz" : "radio-browser-v1.json.gz");
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
            var temp = Path.Combine(root, $"radio-browser-{Guid.NewGuid():N}.tmp");
            try
            {
                using var response = await OpenAsync();
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var zip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    using var writer = new Utf8JsonWriter(zip);
                    using var bounded = new BoundedInput(source);
                    writer.WriteStartArray();
                    int kept = 0;
                    await foreach (var entry in ReadEntriesAsync(bounded, secondary, token))
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

        async Task<HttpResponseMessage> OpenAsync() =>
            secondary ? await client.GetAsync(Iprd, HttpCompletionOption.ResponseHeadersRead, token) : await OpenCatalogueAsync(token);
    }

    private static async IAsyncEnumerable<DirectoryEntry?> ReadEntriesAsync(Stream source, bool secondary,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
    {
        if (!secondary)
        {
            await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable(source, CatalogueJson.Default.DirectoryEntry,
                cancellationToken: token))
                yield return entry;
            yield break;
        }
        var catalog = await JsonSerializer.DeserializeAsync(source, CatalogueJson.Default.IprdCatalogue, token);
        if (catalog?.Stations == null) throw new JsonException("The IPRD stations list is missing.");
        foreach (var station in catalog.Stations)
        {
            token.ThrowIfCancellationRequested();
            if (station?.Streams == null) continue;
            foreach (var stream in station.Streams.Take(12))
                yield return new DirectoryEntry(station.Name, station.Website, stream?.Url, null);
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
        await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable(bounded, CatalogueJson.Default.DirectoryEntry, cancellationToken: token))
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
        var hosts = MirrorOrder(await FindMirrors(token), Random.Shared);
        for (int i = 0; i < hosts.Count - 1; i++)
        {
            try
            {
                var response = await OpenAsync(new UriBuilder(Catalogue) { Host = hosts[i] }.Uri);
                if ((int)response.StatusCode < 500) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (!token.IsCancellationRequested) { }
        }
        return await OpenAsync(new UriBuilder(Catalogue) { Host = hosts[^1] }.Uri);

        async Task<HttpResponseMessage> OpenAsync(Uri endpoint)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("StreamSift/1.0 public-directory-lookup");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
    }

    // Approach adapted from QUILL Radio (Community-Access/quill, MIT).
    // Radio Browser documents its current servers as the reverse names of all.api.radio-browser.info.
    private static async Task<IReadOnlyList<string?>> DiscoverMirrorsAsync(CancellationToken cancel)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(MirrorLookupLimit);
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(MirrorPool, deadline.Token);
            var names = addresses.Where(PublicNetwork.IsPublic).Distinct().Take(DiscoveredMirrors * 2).Select(async address =>
            {
                try { return (await Dns.GetHostEntryAsync(address.ToString(), deadline.Token)).HostName; }
                catch (SocketException) { return null; }
            });
            return await Task.WhenAll(names).WaitAsync(deadline.Token);
        }
        catch (Exception error) when (!cancel.IsCancellationRequested &&
            error is SocketException or OperationCanceledException or ArgumentException) { return []; }
    }

    internal static List<string> MirrorOrder(IEnumerable<string?> discovered, Random random)
    {
        var hosts = discovered.Select(name => name?.TrimEnd('.').ToLowerInvariant())
            .OfType<string>().Where(IsMirror).Distinct(StringComparer.Ordinal).ToArray();
        random.Shuffle(hosts);
        // The long-standing servers remain the fallback when discovery fails or every discovered server is down.
        return hosts.Take(DiscoveredMirrors).Concat(FallbackMirrors).Distinct(StringComparer.Ordinal).ToList();

        static bool IsMirror(string host) => host.Length > MirrorSuffix.Length &&
            host.EndsWith(MirrorSuffix, StringComparison.Ordinal) &&
            host[..^MirrorSuffix.Length].All(c => char.IsAsciiLetterLower(c) || char.IsAsciiDigit(c) || c == '-') &&
            PublicNetwork.TryUrl($"https://{host}/", null, out _);
    }

    private static string HostKey(Uri address)
    {
        var host = address.IdnHost.TrimEnd('.').ToLowerInvariant();
        return host.StartsWith("www.", StringComparison.Ordinal) ? host[4..] : host;
    }

    private sealed class BoundedInput(Stream source) : Stream
    {
        private int read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Checked(source.Read(buffer, offset, count));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            Checked(await source.ReadAsync(buffer, cancellationToken));
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

[JsonSerializable(typeof(DirectoryEntry))]
[JsonSerializable(typeof(IprdCatalogue))]
internal sealed partial class CatalogueJson : JsonSerializerContext;
