using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace StreamSift.Mobile;

internal sealed record SavedResults(Guid Id, string Input, Station[] Stations, bool FileIndex)
{
    private const int MaxResults = 2048;

    internal static SavedResults? Capture(string input, IEnumerable<Station> stations, bool fileIndex, Guid? id = null)
    {
        var safe = stations.Where(SessionStore.CanSave).DistinctBy(row => row.Url).Take(MaxResults)
            .Select(SessionStore.WithoutScanTitle).ToArray();
        return safe.Length == 0 ? null : new SavedResults(id ?? Guid.NewGuid(), input.Trim(), safe, fileIndex);
    }
}

internal sealed record SavedPlace(Guid ResultsId, ResultView View, Uri? Focus, Station? Media, double Seconds,
    bool WasPlaying = false, bool FavouritesQueue = false);

internal sealed class SessionStore(string directory)
{
    private const int MaxResultsBytes = 4 * 1024 * 1024;
    private const int MaxPlaceBytes = 16 * 1024;
    private const string ResultsFile = "Results.json";
    private const string PlaceFile = "Place.json";
    private const string FavouritesFile = "Favourites.json";
    private const string AlarmFile = "Alarm.json";
    private const int MaxFavourites = FavouritePlaylist.MaxEntries;
    private SavedPlace? writtenPlace;
    private Station[]? favourites;

    internal RadioAlarm? ReadAlarm()
    {
        var alarm = Read(AlarmFile, MaxPlaceBytes, SessionJson.Default.RadioAlarm);
        return alarm is { Station.IsTrack: false } && alarm.Id != Guid.Empty && CanSave(alarm.Station)
            ? alarm with { Station = WithoutScanTitle(alarm.Station) } : null;
    }

    internal bool WriteAlarm(RadioAlarm alarm) => alarm is { Station.IsTrack: false } && alarm.Id != Guid.Empty &&
        CanSave(alarm.Station) && Write(AlarmFile, alarm with { Station = WithoutScanTitle(alarm.Station) },
            MaxPlaceBytes, SessionJson.Default.RadioAlarm);

    internal bool ClearAlarm()
    {
        try
        {
            File.Delete(Path.Combine(directory, AlarmFile));
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return false; }
    }

    internal Station[] ReadFavourites() => favourites ??=
        (Read(FavouritesFile, MaxResultsBytes, SessionJson.Default.StationArray) ?? [])
            .Where(CanSave).DistinctBy(row => row.Url).Take(MaxFavourites).Select(WithoutScanTitle).ToArray();

    internal bool IsFavourite(Station row) => ReadFavourites().Any(item => item.Url == row.Url);

    internal bool SetFavourite(Station row, bool enabled)
    {
        if (!CanSave(row)) return false;
        var current = ReadFavourites();
        if (IsFavourite(row) == enabled) return true;
        if (enabled && current.Length >= MaxFavourites) return false;
        Station[] updated = enabled ? [.. current, WithoutScanTitle(row)] : current.Where(item => item.Url != row.Url).ToArray();
        if (!Write(FavouritesFile, updated, MaxResultsBytes, SessionJson.Default.StationArray)) return false;
        favourites = updated;
        return true;
    }

    internal int ImportFavourites(IEnumerable<Station> incoming)
    {
        var current = ReadFavourites();
        var updated = current.Concat(incoming.Where(CanSave).Select(WithoutScanTitle))
            .DistinctBy(row => row.Url).Take(MaxFavourites).ToArray();
        if (updated.Length == current.Length) return 0;
        if (!Write(FavouritesFile, updated, MaxResultsBytes, SessionJson.Default.StationArray))
            throw new IOException("Favourites could not be saved.");
        favourites = updated;
        return updated.Length - current.Length;
    }

    internal SavedResults? ReadResults()
    {
        var saved = Read(ResultsFile, MaxResultsBytes, SessionJson.Default.SavedResults);
        return saved is { Input: not null, Stations: not null }
            ? SavedResults.Capture(saved.Input, saved.Stations, saved.FileIndex, saved.Id)
            : null;
    }

    internal SavedPlace? ReadPlace()
    {
        var saved = Read(PlaceFile, MaxPlaceBytes, SessionJson.Default.SavedPlace);
        return writtenPlace = saved == null ? null : Sanitize(saved);
    }

    internal bool WriteResults(SavedResults data) => Write(ResultsFile, data, MaxResultsBytes, SessionJson.Default.SavedResults);

    internal void ClearResults()
    {
        try
        {
            File.Delete(Path.Combine(directory, ResultsFile));
            File.Delete(Path.Combine(directory, ResultsFile + ".tmp"));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal void WritePlace(SavedPlace place)
    {
        var safe = Sanitize(place);
        if (safe != writtenPlace && Write(PlaceFile, safe, MaxPlaceBytes, SessionJson.Default.SavedPlace))
            writtenPlace = safe;
    }

    internal static bool CanSave(Station? row) => row is { IsVideo: false, IsHls: false } &&
        SafeText(row.Name, 512) && SafeText(row.Format, 32) && row.Format != "HLS" &&
        (row.FileSizeLabel == null || SafeText(row.FileSizeLabel, 64)) &&
        StableAddress(row.Url) && StableAddress(row.FoundOn);

    internal static Station WithoutScanTitle(Station row) => row.ScanTitle == null ? row : row with { ScanTitle = null };

    private static SavedPlace Sanitize(SavedPlace place)
    {
        var row = CanSave(place.Media) ? WithoutScanTitle(place.Media!) : null;
        return place with
        {
            Focus = StableAddress(place.Focus) ? place.Focus : null,
            Media = row,
            WasPlaying = row != null && place.WasPlaying,
            FavouritesQueue = row != null && place.FavouritesQueue,
            Seconds = row?.IsTrack == true && double.IsFinite(place.Seconds) && place.Seconds > 0 ? place.Seconds : 0
        };
    }

    private static bool SafeText(string? text, int length) =>
        text is { Length: > 0 } && text.Length <= length && !text.Any(char.IsControl);

    // Signed media addresses can carry a session in the query or in an HLS path.
    private static bool StableAddress(Uri? url) => url is
        { IsAbsoluteUri: true, Scheme: "https", HostNameType: UriHostNameType.Dns } &&
        url.AbsoluteUri.Length <= 2048 && url.Query.Length == 0 && url.Fragment.Length == 0 &&
        PublicNetwork.TryUrl(url.AbsoluteUri, null, out _);

    private T? Read<T>(string name, int limit, JsonTypeInfo<T> type) where T : class
    {
        try
        {
            using var file = File.OpenRead(Path.Combine(directory, name));
            return file.Length <= limit ? JsonSerializer.Deserialize(file, type) : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private bool Write<T>(string name, T value, int limit, JsonTypeInfo<T> type)
    {
        string stage = Path.Combine(directory, name + ".tmp");
        try
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(value, type);
            if (data.Length > limit) return false;
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(stage, data);
            File.Move(stage, Path.Combine(directory, name), overwrite: true);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
        finally
        {
            try { File.Delete(stage); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }
}

[JsonSerializable(typeof(SavedResults))]
[JsonSerializable(typeof(SavedPlace))]
[JsonSerializable(typeof(Station[]))]
[JsonSerializable(typeof(RadioAlarm))]
internal sealed partial class SessionJson : JsonSerializerContext;
