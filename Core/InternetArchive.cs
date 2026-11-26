using System.Globalization;
using System.Text.Json;

namespace StreamSift;

internal static class InternetArchive
{
    internal static bool TryItem(Uri address, out string identifier)
    {
        identifier = "";
        if (!address.IsDefaultPort || address.IdnHost is not ("archive.org" or "www.archive.org") ||
            address.AbsolutePath.TrimEnd('/').Split('/') is not ["", "details" or "download" or "metadata", var item] ||
            item.Length is < 1 or > 128 || item is "." or ".." ||
            !item.All(character => Ascii.IsLetterOrDigit(character) || character is '-' or '_' or '.')) return false;
        identifier = item;
        return true;
    }

    internal static Uri MetadataAddress(string identifier) => new("https://archive.org/metadata/" + identifier);

    internal static FileIndex Read(byte[] content, string identifier, CancellationToken token)
    {
        using var document = JsonDocument.Parse(content);
        var root = document.RootElement;
        var metadata = Field(root, "metadata");
        if (Flag(root, "is_dark") || Flag(metadata, "is_dark") || Flag(metadata, "access-restricted-item") ||
            Flag(root, "access-restricted-item"))
            return new([], false, Notice: "Internet Archive marks this item as restricted.");
        var files = Field(root, "files");
        if (files.ValueKind != JsonValueKind.Array || Text(metadata, "identifier") is not string id || id != identifier)
            return new([], false, Notice: "Internet Archive did not return a public file list for this item.");

        var source = new Uri("https://archive.org/details/" + identifier);
        var download = new Uri("https://archive.org/download/" + identifier + "/");
        var tracks = new List<Station>();
        var seen = new HashSet<Uri>();
        foreach (var entry in files.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            if (Flag(entry, "private") || Text(entry, "name") is not string path) continue;
            var parts = path.Split('/');
            if (FileIndex.AudioFormat(parts[^1]) is not string format || !FileIndex.TryFilePath(parts, out var escaped)) continue;
            var address = new Uri(download, escaped);
            if (!seen.Add(address)) continue;
            if (tracks.Count == FileIndex.TrackLimit) return new(tracks, true);
            long? bytes = PositiveInteger(entry, "size");
            long? modified = PositiveInteger(entry, "mtime");
            var track = FileIndex.Track(address, source, parts[^1], format, Text(entry, "title"), bytes);
            if (modified is > 0 and <= 253402300799)
                track = track with { DateModified = DateTimeOffset.FromUnixTimeSeconds(modified.Value).UtcDateTime };
            tracks.Add(track);
        }
        return new(tracks, false, Notice: tracks.Count == 0 ? "This Internet Archive item lists no supported public audio files." : null);
    }

    private static JsonElement Field(JsonElement node, string name) =>
        node.ValueKind == JsonValueKind.Object && node.TryGetProperty(name, out var value) ? value : default;

    private static string? Text(JsonElement node, string name) =>
        Field(node, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    private static bool Flag(JsonElement node, string name) =>
        Field(node, name).ValueKind == JsonValueKind.True ||
        Field(node, name) is { ValueKind: JsonValueKind.Number } number && number.TryGetInt32(out int flag) && flag == 1 ||
        Text(node, name) is string text && (text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1");

    private static long? PositiveInteger(JsonElement node, string name)
    {
        var value = Field(node, name);
        return (value.ValueKind == JsonValueKind.Number ? value.TryGetInt64(out long number) :
            long.TryParse(Text(node, name), NumberStyles.None, CultureInfo.InvariantCulture, out number)) &&
            number > 0 ? number : null;
    }
}
