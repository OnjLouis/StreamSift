using AngleSharp.Dom;
using System.Globalization;

namespace StreamSift;

internal sealed record FileIndex(IReadOnlyList<Station> Tracks, bool Truncated, bool PreferListedOrder = false, string? Notice = null)
{
    internal const int TrackLimit = 512;
    private const long Kibibyte = 1024, Mebibyte = Kibibyte * Kibibyte, Gibibyte = Mebibyte * Kibibyte, Tebibyte = Gibibyte * Kibibyte;
    private static readonly string[] DateFormats =
        ["yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "dd-MMM-yyyy HH:mm", "d MMMM yyyy, HH:mm", "d MMMM yyyy HH:mm"];

    internal static FileIndex? Read(IDocument document, Uri page, CancellationToken token)
    {
        var folder = page.AbsolutePath;
        if (!folder.EndsWith('/')) return null;
        var heading = "Index of " + Uri.UnescapeDataString(folder).TrimEnd('/');
        if (document.Title?.Trim().TrimEnd('/') != heading ||
            document.QuerySelector("h1")?.TextContent.Trim().TrimEnd('/') != heading) return null;

        var tracks = new List<Station>();
        var seen = new HashSet<Uri>();
        var columnsByTable = new Dictionary<IElement, (int Date, int Size)>();
        foreach (var link in document.QuerySelectorAll("a[href]"))
        {
            token.ThrowIfCancellationRequested();
            var href = link.GetAttribute("href");
            if (href == null || href.Contains('?') || href.Contains('#') ||
                !PublicNetwork.TryUrl(href, page, out var address) ||
                !SameOrigin(page, address) || !address.AbsolutePath.StartsWith(folder, StringComparison.Ordinal) ||
                address.Segments.Length != page.Segments.Length + 1) continue;

            var file = Uri.UnescapeDataString(address.Segments[^1]);
            if (file.Contains('/') || file.Contains('\\') || file.Any(char.IsControl)) continue;
            var extension = Path.GetExtension(file);
            var format = AudioFormat(file);
            if (format == null || !seen.Add(address)) continue;
            if (tracks.Count == TrackLimit) return new(tracks, true);
            var name = Extraction.Label(file[..^extension.Length]) ?? file;
            var track = new Station(name, address, format, null, page, false, IsTrack: true);
            if (link.Closest("tr") is IElement row && row.Closest("table") is IElement table)
            {
                if (!columnsByTable.TryGetValue(table, out var columns))
                    columnsByTable[table] = columns = MetadataColumns(table);
                var cells = row.Children;
                if (columns.Date >= 0 && columns.Date < cells.Length &&
                    DateTime.TryParseExact(cells[columns.Date].TextContent.Trim(), DateFormats, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var modified))
                    track = track with { DateModified = modified };
                if (columns.Size >= 0 && columns.Size < cells.Length && FileSize(cells[columns.Size].TextContent) is { } size)
                    track = track with { FileSizeBytes = size.Bytes, FileSizeLabel = size.Label };
            }
            tracks.Add(track);
        }
        return new(tracks, false);
    }

    internal static string? AudioFormat(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".mp3" => "MP3",
        ".mp2" => "MP2",
        ".aac" => "AAC",
        ".m4a" => "M4A",
        ".flac" => "FLAC",
        ".wav" => "WAV",
        ".ogg" or ".oga" => "Ogg audio",
        ".opus" => "Ogg Opus",
        _ => null
    };

    internal static Station Track(Uri address, Uri source, string file, string format, string? title, long? bytes) =>
        new(Extraction.Label(title) ?? Extraction.Label(Path.GetFileNameWithoutExtension(file)) ?? file,
            address, format, null, source, false, IsTrack: true)
        {
            FileSizeBytes = bytes,
            FileSizeLabel = bytes is long size ? FormatSize(size) : null
        };

    private static string FormatSize(long bytes)
    {
        (long scale, string unit) = bytes switch
        {
            >= Gibibyte => (Gibibyte, "GiB"),
            >= Mebibyte => (Mebibyte, "MiB"),
            >= Kibibyte => (Kibibyte, "KiB"),
            1 => (1, "byte"),
            _ => (1, "bytes")
        };
        return ((decimal)bytes / scale).ToString("0.##", CultureInfo.InvariantCulture) + " " + unit;
    }

    internal static bool TryFilePath(IEnumerable<string> segments, out string path)
    {
        path = "";
        var parts = segments.ToArray();
        if (parts.Length is 0 or > 32 || parts.Any(part =>
            part.Length is 0 or > 255 || part is "." or ".." ||
            part.Any(character => character is '/' or '\\' || char.IsControl(character)))) return false;
        path = string.Join('/', parts.Select(Uri.EscapeDataString));
        return path.Length <= 2048;
    }

    private static (int Date, int Size) MetadataColumns(IElement table)
    {
        var headers = table.QuerySelector("thead tr") ?? table.QuerySelector("tr");
        var names = headers?.Children.Select(cell => cell.LocalName == "th" ? cell.TextContent.Trim().ToUpperInvariant() : "").ToArray() ?? [];
        return (Array.FindIndex(names, name => name is "LAST MODIFIED" or "DATE MODIFIED" or "MODIFIED"),
            Array.FindIndex(names, name => name is "SIZE" or "FILE SIZE"));
    }

    private static (long Bytes, string Label)? FileSize(string text)
    {
        text = text.Trim();
        int end = 0;
        while (end < text.Length && (Ascii.IsDigit(text[end]) || text[end] == '.')) end++;
        if (!decimal.TryParse(text.AsSpan(0, end), NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number)) return null;
        // Apache and Nginx use binary units when the suffix is only one letter.
        (long scale, string unit) = text[end..].Trim().ToUpperInvariant() switch
        {
            "" or "B" or "BYTES" => (1, number == 1 ? "byte" : "bytes"),
            "K" or "KIB" => (Kibibyte, "KiB"),
            "M" or "MIB" => (Mebibyte, "MiB"),
            "G" or "GIB" => (Gibibyte, "GiB"),
            "T" or "TIB" => (Tebibyte, "TiB"),
            "KB" => (1000, "KB"),
            "MB" => (1000000, "MB"),
            "GB" => (1000000000, "GB"),
            "TB" => (1000000000000, "TB"),
            _ => (0, "")
        };
        if (scale == 0 || number > long.MaxValue / (decimal)scale) return null;
        return ((long)(number * scale), text[..end] + " " + unit);
    }

    internal static bool SameOrigin(Uri left, Uri right) =>
        left.Scheme == right.Scheme && left.IdnHost == right.IdnHost && left.Port == right.Port;
}
