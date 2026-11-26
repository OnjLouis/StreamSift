using System.Globalization;
using System.Text;

namespace StreamSift;

internal static class TorrentWebSeeds
{
    private static readonly Encoding Utf8 = new UTF8Encoding(false, true);
    internal static bool IsTorrent(Uri address, string mediaType) =>
        mediaType == "application/x-bittorrent" ||
        address.AbsolutePath.EndsWith(".torrent", StringComparison.OrdinalIgnoreCase);

    internal static FileIndex Read(byte[] content, Uri source, CancellationToken token)
    {
        try
        {
            var reader = new Bencode(content);
            if (reader.Value(0) is not Dictionary<string, object> root || !reader.Complete ||
                root.GetValueOrDefault("info") is not Dictionary<string, object> info)
                return new([], false, Notice: "This torrent file could not be read.");
            if (info.GetValueOrDefault("piece length") is not long pieceLength || pieceLength <= 0 ||
                info.GetValueOrDefault("pieces") is not ReadOnlyMemory<byte> pieces || pieces.Length == 0 || pieces.Length % 20 != 0)
                return new([], false, Notice: "This torrent uses an unsupported file layout.");
            if (Text(info, "name") is not string name || !FileIndex.TryFilePath([name], out var escapedName))
                return new([], false, Notice: "This torrent has an invalid file name.");

            bool multiple = info.GetValueOrDefault("files") is List<object>;
            var seed = Seeds(root.GetValueOrDefault("url-list")).Where(value =>
                !multiple || value.AbsolutePath.EndsWith('/'))
                .OrderByDescending(value => value.Scheme == Uri.UriSchemeHttps).FirstOrDefault();
            if (seed == null)
                return new([], false, Notice: "This torrent has no public HTTP or HTTPS web seed.");

            if (info.GetValueOrDefault("files") is not List<object> files)
            {
                if (info.GetValueOrDefault("length") is not long bytes || bytes <= 0 ||
                    FileIndex.AudioFormat(name) is not string format)
                    return Empty();
                var address = seed.AbsolutePath.EndsWith('/') ? new Uri(seed, escapedName) : seed;
                return new([FileIndex.Track(address, source, name, format, null, bytes)], false);
            }

            var tracks = new List<Station>();
            var seen = new HashSet<Uri>();
            foreach (var entry in files)
            {
                token.ThrowIfCancellationRequested();
                if (entry is not Dictionary<string, object> file || file.GetValueOrDefault("length") is not long bytes ||
                    bytes <= 0 || (file.GetValueOrDefault("path.utf-8") ?? file.GetValueOrDefault("path")) is not List<object> path)
                    continue;
                var parts = path.Select(Decode).ToArray();
                if (parts.Length == 0 || parts.Any(part => part == null) ||
                    FileIndex.AudioFormat(parts[^1]!) is not string format ||
                    !FileIndex.TryFilePath(parts.OfType<string>(), out var escaped)) continue;
                var address = new Uri(seed, escapedName + "/" + escaped);
                if (!seen.Add(address)) continue;
                if (tracks.Count == FileIndex.TrackLimit) return new(tracks, true);
                tracks.Add(FileIndex.Track(address, source, parts[^1]!, format, null, bytes));
            }
            return tracks.Count == 0 ? Empty() : new(tracks, false);
        }
        catch (InvalidDataException) { return new([], false, Notice: "This torrent file could not be read."); }
    }

    private static FileIndex Empty() => new([], false, Notice: "This torrent lists no supported audio files.");

    private static IEnumerable<Uri> Seeds(object? value)
    {
        if (value == null) yield break;
        IEnumerable<object> entries = value is List<object> list ? list.Take(32) : [value];
        foreach (var entry in entries)
            if (Decode(entry) is string text && !text.Any(part => part is '#' or '?') &&
                PublicNetwork.TryUrl(text, null, out var seed))
                yield return seed;
    }

    private static string? Text(Dictionary<string, object> fields, string key) =>
        Decode(fields.GetValueOrDefault(key + ".utf-8")) ?? Decode(fields.GetValueOrDefault(key));

    private static string? Decode(object? value)
    {
        if (value is not ReadOnlyMemory<byte> bytes || bytes.Length > 4096) return null;
        try { return Utf8.GetString(bytes.Span); }
        catch (DecoderFallbackException) { return null; }
    }

    private sealed class Bencode(byte[] data)
    {
        private int position, nodes;
        internal bool Complete => position == data.Length;
        private byte Next => position < data.Length ? data[position] : throw new InvalidDataException();

        internal object Value(int depth)
        {
            if (depth > 24 || ++nodes > 65536) throw new InvalidDataException();
            switch (Next)
            {
                case (byte)'d':
                    position++;
                    var fields = new Dictionary<string, object>(StringComparer.Ordinal);
                    while (Next != 'e')
                    {
                        var key = Bytes();
                        if (key.Length > 128 || !fields.TryAdd(Encoding.Latin1.GetString(key.Span), Value(depth + 1)))
                            throw new InvalidDataException();
                    }
                    position++;
                    return fields;
                case (byte)'l':
                    position++;
                    var values = new List<object>();
                    while (Next != 'e') values.Add(Value(depth + 1));
                    position++;
                    return values;
                case (byte)'i':
                    position++;
                    if (!long.TryParse(Number((byte)'e'), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out long number))
                        throw new InvalidDataException();
                    return number;
                default:
                    return Bytes();
            }
        }

        private ReadOnlyMemory<byte> Bytes()
        {
            if (!int.TryParse(Number((byte)':'), NumberStyles.None, CultureInfo.InvariantCulture, out int length) ||
                length > data.Length - position) throw new InvalidDataException();
            var bytes = data.AsMemory(position, length);
            position += length;
            return bytes;
        }

        private string Number(byte end)
        {
            int start = position;
            if (end == 'e' && Next == '-') position++;
            while (Next is >= (byte)'0' and <= (byte)'9')
            {
                if (++position - start > 20) throw new InvalidDataException();
            }
            if (Next != end) throw new InvalidDataException();
            return Encoding.ASCII.GetString(data, start, position++ - start);
        }
    }
}
