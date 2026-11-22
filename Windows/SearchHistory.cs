using System.Text.Json;

namespace StreamSift;

internal static class SearchHistory
{
    internal const int Capacity = 10;
    private const int MaxEntryLength = 1024, MaxFileBytes = 64 * 1024;
    private const string FileName = "SearchHistory.json";

    internal static string[] Load(string directory)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(directory, FileName));
            if (file.Length > MaxFileBytes) return [];
            var entries = JsonSerializer.Deserialize<string?[]>(file) ?? [];
            return entries.Select(value => value?.Trim()).OfType<string>()
                .Select(value => (Value: value, Key: Key(value))).Where(item => item.Key != null)
                .DistinctBy(item => item.Key).Take(Capacity).Select(item => item.Value).ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { return []; }
    }

    internal static string[] Remember(string[] entries, string input, int verifiedStreams)
    {
        if (verifiedStreams <= 0) return entries;
        input = input.Trim();
        var key = Key(input);
        if (key == null || entries.FirstOrDefault() == input) return entries;
        return entries.Where(value => Key(value) != key).Prepend(input).Take(Capacity).ToArray();
    }

    private static string? Key(string value)
    {
        if (value.Length is 0 or > MaxEntryLength || value.Any(c => char.IsControl(c) || c is '?' or '#' or '@')) return null;
        if (SearchSequence.CanSearchStation(value)) return "name:" + value.ToUpperInvariant();
        if (!PublicNetwork.TryInput(value, out var url)) return null;
        // A bare address and an explicit HTTPS address search differently on a custom port.
        return (value.Contains("://", StringComparison.Ordinal) ? "url:" : "bare:") + url.AbsoluteUri;
    }

    internal static void Save(string directory, string[] entries)
    {
        var path = Path.Combine(directory, FileName);
        if (entries.Length == 0)
        {
            if (Directory.Exists(directory)) File.Delete(path);
            return;
        }
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".history-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(file, entries);
            File.Move(stage, path, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
