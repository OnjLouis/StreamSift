using System.Text.Json;

namespace StreamSift;

internal sealed record LastResults(string Input, SearchMethod Method, Station[] Stations, ResultView View, Uri? Selection)
{
    private const int MaxStations = 512, MaxFileBytes = 2 * 1024 * 1024;
    private const string FileName = "LastResults.json";

    internal static LastResults? Capture(string input, SearchMethod method, IEnumerable<Station> stations, ResultView view, Uri? selection)
    {
        input = input.Trim();
        if (!SearchHistory.CanRemember(input) || !Enum.IsDefined(method) ||
            (!SearchSequence.CanSearchStation(input) && (!PublicNetwork.TryInput(input, out var source) || !StableAddress(source))))
            return null;
        // HLS servers can embed session identifiers in a path, not just the query string.
        var safe = stations.Where(s => s != null && !s.IsHls && !s.IsVideo && !s.IsTrack && s.Format != "HLS" &&
                SafeText(s.Name, 512) && SafeText(s.Format, 32) && StableAddress(s.Url) && StableAddress(s.FoundOn))
            .DistinctBy(s => s.Url).Take(MaxStations).ToArray();
        if (safe.Length == 0) return null;
        if (!Enum.IsDefined(view.Order)) view = view with { Order = ResultOrder.Search };
        view = view.RetainAvailable(safe);
        return new LastResults(input, method, safe, view, view.Apply(safe).Any(s => s.Url == selection) ? selection : null);
    }

    private static bool SafeText(string? text, int length) => text is { Length: > 0 } &&
        text.Length <= length && !text.Any(char.IsControl);

    private static bool StableAddress(Uri? url) => url is { IsAbsoluteUri: true, HostNameType: UriHostNameType.Dns } &&
        url.AbsoluteUri.Length <= 2048 && !url.AbsoluteUri.Any(ch => ch is '?' or '#' or '@') &&
        PublicNetwork.TryUrl(url.AbsoluteUri, null, out _);

    internal static LastResults? Load(string directory)
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(directory, FileName));
            if (file.Length > MaxFileBytes || JsonSerializer.Deserialize<LastResults>(file) is not
                { Input: not null, Stations: not null } saved) return null;
            return Capture(saved.Input, saved.Method, saved.Stations, saved.View, saved.Selection);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return null; }
    }

    internal void Save(string directory)
    {
        var data = JsonSerializer.SerializeToUtf8Bytes(this);
        if (data.Length > MaxFileBytes) return;
        Directory.CreateDirectory(directory);
        var stage = Path.Combine(directory, ".last-results-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                file.Write(data);
            File.Move(stage, Path.Combine(directory, FileName), overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }

    internal static void Clear(string directory)
    {
        if (Directory.Exists(directory)) File.Delete(Path.Combine(directory, FileName));
    }
}
