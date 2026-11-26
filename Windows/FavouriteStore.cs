using System.Text.Json;

namespace StreamSift;

internal sealed class FavouriteStore(string? directory)
{
    private const string FileName = "Favourites.json";
    private const int MaxBytes = 4 * 1024 * 1024;
    internal Station[] Items { get; private set; } = Load(directory);

    internal bool Contains(Station row) => Items.Any(item => item.Url == row.Url);

    internal bool Toggle(Station row)
    {
        if (Contains(row))
        {
            Save(Items.Where(item => item.Url != row.Url).ToArray());
            return false;
        }
        if (Items.Length >= FavouritePlaylist.MaxEntries) throw new InvalidDataException("Favourites is full.");
        if (!FavouritePlaylist.CanSave(row)) throw new InvalidDataException("This address could not be saved to Favourites.");
        Save([.. Items, FavouritePlaylist.SafeSource(row)]);
        return true;
    }

    internal int Merge(IEnumerable<Station> incoming)
    {
        var updated = Safe(Items.Concat(incoming));
        int added = updated.Length - Items.Length;
        if (added > 0) Save(updated);
        return added;
    }

    private static Station[] Safe(IEnumerable<Station> entries) => entries
        .Where(row => FavouritePlaylist.CanSave(row)).DistinctBy(row => row.Url).Take(FavouritePlaylist.MaxEntries)
        .Select(FavouritePlaylist.SafeSource).ToArray();

    private static Station[] Load(string? path)
    {
        if (path == null) return [];
        try
        {
            using var file = File.OpenRead(Path.Combine(path, FileName));
            return file.Length <= MaxBytes ? Safe(JsonSerializer.Deserialize<Station[]>(file) ?? []) : [];
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException) { return []; }
    }

    private void Save(Station[] next)
    {
        if (directory != null)
        {
            byte[] data = JsonSerializer.SerializeToUtf8Bytes(next);
            if (data.Length > MaxBytes) throw new InvalidDataException("Favourites is full.");
            Directory.CreateDirectory(directory);
            WriteFile(Path.Combine(directory, FileName), data);
        }
        Items = next;
    }

    internal static void WriteFile(string path, byte[] content)
    {
        string stage = Path.Combine(Path.GetDirectoryName(path)!, ".favourites-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                output.Write(content);
            File.Move(stage, path, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
