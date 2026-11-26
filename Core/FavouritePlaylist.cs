using System.Globalization;
using System.Text;

namespace StreamSift;

public sealed record FavouriteImport(Station[] Stations, int Skipped);

public static class FavouritePlaylist
{
    public const int MaxEntries = 512;
    private const int MaxBytes = 2 * 1024 * 1024;
    private const string FormatComment = "#STREAMSIFT-FORMAT:";
    private static readonly UTF8Encoding Utf8 = new(false, true);

    public static string ImportSummary(FavouriteImport list, int added)
    {
        int skipped = list.Skipped + list.Stations.Length - added;
        return $"Added {added} {(added == 1 ? "favourite" : "favourites")}." +
            (skipped > 0 ? $" Skipped {skipped} {(skipped == 1 ? "entry" : "entries")}." : "");
    }

    public static bool CanSave(Station? station, bool httpsOnly = false) =>
        station is { IsVideo: false, IsHls: false } && SafeText(station.Name, 512) && SafeText(station.Format, 32) &&
        !station.Format.Equals("HLS", StringComparison.OrdinalIgnoreCase) &&
        StableAddress(station.Url, httpsOnly) &&
        !station.Url.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase);

    public static Station SafeSource(Station station) =>
        station with { FoundOn = StableAddress(station.FoundOn, httpsOnly: false) ? station.FoundOn : station.Url, ScanTitle = null };

    public static byte[] Export(IEnumerable<Station> entries, bool httpsOnly = false)
    {
        var text = new StringBuilder("#EXTM3U\n");
        foreach (var entry in entries.Where(row => CanSave(row, httpsOnly)).DistinctBy(row => row.Url).Take(MaxEntries))
        {
            text.Append("#EXTINF:").Append(entry.IsTrack ? "0," : "-1,").Append(entry.Name).Append('\n');
            text.Append(FormatComment).Append(entry.Format).Append('\n');
            text.Append(entry.Url.AbsoluteUri).Append('\n');
        }
        var bytes = Utf8.GetBytes(text.ToString());
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The favourites playlist exceeds the file size limit.");
        return bytes;
    }

    public static async Task<FavouriteImport> ReadAsync(Stream input, bool httpsOnly = false, CancellationToken token = default) =>
        Parse(await ReadDocumentAsync(input, token).ConfigureAwait(false), httpsOnly);

    public static async Task<byte[]> ReadDocumentAsync(Stream input, CancellationToken token = default)
    {
        using var document = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (document.Length <= MaxBytes)
        {
            int count = await input.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, MaxBytes + 1 - (int)document.Length)), token)
                .ConfigureAwait(false);
            if (count == 0) return document.ToArray();
            document.Write(buffer, 0, count);
        }
        throw new InvalidDataException("The favourites playlist exceeds the file size limit.");
    }

    public static FavouriteImport Parse(ReadOnlySpan<byte> bytes, bool httpsOnly = false)
    {
        if (bytes.Length > MaxBytes) throw new InvalidDataException("The favourites playlist exceeds the file size limit.");
        string content;
        try { content = Utf8.GetString(bytes).TrimStart('\uFEFF'); }
        catch (DecoderFallbackException error) { throw new InvalidDataException("Choose a UTF-8 M3U8 playlist.", error); }

        using var reader = new StringReader(content);
        var entries = new List<Station>();
        var seen = new HashSet<Uri>();
        string? name = null, format = null;
        bool track = false;
        int skipped = 0;
        while (reader.ReadLine() is { } raw)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Choose a favourites playlist containing station or file addresses.");
            if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = line.IndexOf(',');
                name = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                int durationEnd = comma >= 0 ? comma : line.Length;
                track = double.TryParse(line.AsSpan(8, durationEnd - 8), NumberStyles.Float,
                    CultureInfo.InvariantCulture, out double seconds) && double.IsFinite(seconds) && seconds >= 0;
                format = null;
                continue;
            }
            if (line.StartsWith(FormatComment, StringComparison.OrdinalIgnoreCase))
            {
                format = line[FormatComment.Length..].Trim();
                continue;
            }
            if (line[0] == '#') continue;
            if (line.Length <= 2048 && PublicNetwork.TryUrl(line, null, out var url))
            {
                var title = SafeText(name, 512) ? name! : Extraction.Label(url.Segments.LastOrDefault()?.Trim('/')) ?? url.IdnHost;
                var station = new Station(title, url, SafeText(format, 32) ? format! : FormatFromAddress(url), null, url, false, IsTrack: track);
                if (entries.Count < MaxEntries && CanSave(station, httpsOnly) && seen.Add(url)) entries.Add(station);
                else skipped++;
            }
            else skipped++;
            name = format = null;
            track = false;
        }
        return new(entries.ToArray(), skipped);
    }

    private static string FormatFromAddress(Uri url) => Path.GetExtension(url.AbsolutePath).ToLowerInvariant() switch
    {
        ".mp3" => "MP3", ".mp2" => "MP2", ".aac" or ".aacp" or ".m4a" => "AAC", ".wav" => "WAV",
        ".ogg" or ".oga" => "Ogg", ".opus" => "Opus", ".flac" => "FLAC",
        _ => "Audio"
    };

    private static bool SafeText(string? value, int length) =>
        value is { Length: > 0 } && value.Length <= length && !value.Any(char.IsControl);

    private static bool StableAddress(Uri? url, bool httpsOnly) =>
        url is { IsAbsoluteUri: true, HostNameType: UriHostNameType.Dns } &&
        (!httpsOnly || url.Scheme == Uri.UriSchemeHttps) && url.AbsoluteUri.Length <= 2048 &&
        !url.AbsoluteUri.Any(character => character is '?' or '#' or '@') &&
        PublicNetwork.TryUrl(url.AbsoluteUri, null, out _);
}
