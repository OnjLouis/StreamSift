using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamSift;

public sealed class DirectoryDiscovery(HttpClient client, string? cacheDirectory = null)
{
    private const int MaxCatalogueBytes = 96 * 1024 * 1024, MaxMatches = 24;
    private static readonly TimeSpan CacheAge = TimeSpan.FromHours(24), CacheTimeout = TimeSpan.FromSeconds(45);
    private static readonly Uri Catalogue = new("https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=70000");
    private static readonly Uri Mirror = new UriBuilder(Catalogue) { Host = "de2.api.radio-browser.info" }.Uri;
    private static readonly Uri ProviderPage = new("https://www.radio-browser.info/");

    public async Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null)
    {
        if (!PublicNetwork.TryUrl(start.AbsoluteUri, null, out start)) throw new ArgumentException("Invalid public address.");
        var root = cacheDirectory ?? (OperatingSystem.IsMacOS()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Caches", "StreamSift")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StreamSift", "Cache"));
        var cache = Path.Combine(root, "radio-browser-v1.json.gz");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        deadline.CancelAfter(CacheTimeout);
        var token = deadline.Token;
        bool fresh = File.Exists(cache) && DateTime.UtcNow - File.GetLastWriteTimeUtc(cache) < CacheAge;
        List<Link> matches = [];
        if (fresh)
        {
            try { matches = await MatchAsync(cache, start, token); }
            catch (Exception error) when (error is JsonException or InvalidDataException or IOException) { fresh = false; }
        }
        if (!fresh)
        {
            Directory.CreateDirectory(root);
            var temp = Path.Combine(root, $"radio-browser-{Guid.NewGuid():N}.tmp");
            try
            {
                using var response = await OpenCatalogueAsync(token);
                response.EnsureSuccessStatusCode();
                await using (var source = await response.Content.ReadAsStreamAsync(token))
                await using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
                await using (var zip = new GZipStream(file, CompressionLevel.Fastest))
                {
                    using var writer = new Utf8JsonWriter(zip);
                    using var bounded = new BoundedInput(source);
                    writer.WriteStartArray();
                    int kept = 0;
                    await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable(
                        bounded, CatalogueJson.Default.DirectoryEntry, cancellationToken: token))
                    {
                        if (entry == null || !PublicNetwork.TryUrl(entry.Homepage ?? "", null, out _) ||
                            !PublicNetwork.TryUrl(entry.Url ?? "", null, out _)) continue;
                        writer.WriteStartObject();
                        writer.WriteString("name", Extraction.StationName(entry.Name));
                        writer.WriteString("homepage", entry.Homepage);
                        writer.WriteString("url", entry.Url);
                        if (PublicNetwork.TryUrl(entry.Resolved ?? "", null, out _) && entry.Resolved != entry.Url)
                            writer.WriteString("url_resolved", entry.Resolved);
                        writer.WriteEndObject();
                        if (++kept % 128 == 0) await writer.FlushAsync(token);
                    }
                    writer.WriteEndArray();
                    await writer.FlushAsync(token);
                }
                // Keep the last complete catalogue if this download is incomplete or invalid.
                matches = await MatchAsync(temp, start, token, validateAll: true);
                File.Move(temp, cache, overwrite: true);
            }
            finally { if (File.Exists(temp)) File.Delete(temp); }
        }
        return await new Discovery(client).VerifyListedAsync(start, matches, cancel, progress);
    }

    private static async Task<List<Link>> MatchAsync(string path, Uri start, CancellationToken token, bool validateAll = false)
    {
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete, 65536, true);
        if (file.Length > MaxCatalogueBytes) throw new InvalidDataException("The cached directory exceeds the size limit.");
        await using var zip = new GZipStream(file, CompressionMode.Decompress);
        using var bounded = new BoundedInput(zip);
        var matches = new List<Link>();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        var startHost = HostKey(start);
        var startPath = start.AbsolutePath.TrimEnd('/');
        await foreach (var entry in JsonSerializer.DeserializeAsyncEnumerable(bounded, CatalogueJson.Default.DirectoryEntry, cancellationToken: token))
        {
            if (matches.Count == MaxMatches || entry == null ||
                !PublicNetwork.TryUrl(entry.Homepage ?? "", null, out var home) || HostKey(home) != startHost ||
                (startPath.Length != 0 && home.AbsolutePath.TrimEnd('/') != startPath) ||
                !PublicNetwork.TryUrl(entry.Url ?? "", null, out var stream) || !unique.Add(stream.AbsoluteUri)) continue;
            Uri? resolved = PublicNetwork.TryUrl(entry.Resolved ?? "", null, out var alternate) && alternate != stream ? alternate : null;
            matches.Add(new Link(stream, ProviderPage, LinkKind.Audio, Extraction.StationName(entry.Name), true,
                NameOrigin.Directory, resolved));
            if (!validateAll && matches.Count == MaxMatches) break;
        }
        return matches;
    }

    private async Task<HttpResponseMessage> OpenCatalogueAsync(CancellationToken token)
    {
        try
        {
            var primary = await OpenAsync(Catalogue);
            if ((int)primary.StatusCode < 500) return primary;
            primary.Dispose();
        }
        catch (HttpRequestException) when (!token.IsCancellationRequested) { }
        return await OpenAsync(Mirror);

        async Task<HttpResponseMessage> OpenAsync(Uri endpoint)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.UserAgent.ParseAdd("StreamSift/1.0 public-directory-lookup");
            return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
        }
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

[JsonSerializable(typeof(DirectoryEntry))]
internal sealed partial class CatalogueJson : JsonSerializerContext;
