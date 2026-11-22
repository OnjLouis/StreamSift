using System.Text.Json;

namespace StreamSift;

internal sealed record Preferences(bool SearchSounds = true, bool CheckUpdatesAtStartup = false,
    string? PlaylistFolder = null, bool AutomaticFallback = false, PlayerChoice MediaPlayer = PlayerChoice.Automatic,
    string? PlayerPath = null)
{
    internal static string UserDirectory => Path.Combine(AppContext.BaseDirectory, "User");
    internal static string DefaultPlaylistFolder => Path.Combine(UserDirectory, "Playlists");
    internal string ExportFolder => PlaylistFolder ?? DefaultPlaylistFolder;

    internal static Preferences Load(string directory)
    {
        var fallback = new Preferences(SearchSounds: !File.Exists(Path.Combine(directory, "SearchSounds.off")));
        try
        {
            var path = Path.Combine(directory, "Preferences.json");
            if (!File.Exists(path)) return fallback;
            using var file = File.OpenRead(path);
            if (file.Length > 16384) return fallback;
            var saved = JsonSerializer.Deserialize<Preferences>(file);
            return saved == null ? fallback : saved with
            {
                PlaylistFolder = NormalizeFolder(saved.PlaylistFolder),
                MediaPlayer = Enum.IsDefined(saved.MediaPlayer) ? saved.MediaPlayer : PlayerChoice.Automatic,
                // A portable player can be on a drive that is not connected yet, so only its name is checked here.
                PlayerPath = saved.PlayerPath is string player && Path.IsPathFullyQualified(player) &&
                    !player.Any(char.IsControl) && StreamPreview.KindOf(player) != null ? player : null
            };
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return fallback;
        }
    }

    internal static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        folder = folder.Trim();
        if (!Path.IsPathFullyQualified(folder) || folder.Any(char.IsControl) ||
            folder.StartsWith(@"\\?\", StringComparison.Ordinal) || folder.StartsWith(@"\\.\", StringComparison.Ordinal))
            throw new ArgumentException("Enter a full playlist folder path or choose Browse.");
        return Path.GetFullPath(folder);
    }

    internal static string? NormalizePlayer(string? player)
    {
        if (string.IsNullOrWhiteSpace(player)) return null;
        player = player.Trim().Trim('"');
        if (!Path.IsPathFullyQualified(player) || player.Any(char.IsControl) ||
            player.StartsWith(@"\\?\", StringComparison.Ordinal) || player.StartsWith(@"\\.\", StringComparison.Ordinal) ||
            StreamPreview.Supported(Path.GetFullPath(player)) is not StreamPreview.Player chosen)
            throw new ArgumentException("Choose vlc.exe, foobar2000.exe or FastPlay.exe, or leave the custom player blank.");
        return chosen.Executable;
    }

    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "Preferences.json");
        var stage = Path.Combine(directory, ".preferences-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var output = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                JsonSerializer.Serialize(output, this);
            File.Move(stage, path, overwrite: true);
        }
        finally { if (File.Exists(stage)) File.Delete(stage); }
    }
}
