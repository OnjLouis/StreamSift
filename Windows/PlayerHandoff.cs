using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace StreamSift;

internal enum PlayerChoice { Automatic, Vlc, Foobar2000, FastPlay }

internal static class StreamPreview
{
    internal sealed record Player(string Executable, PlayerChoice Kind)
    {
        internal string Name => DisplayName(Kind);
    }

    internal static string DisplayName(PlayerChoice choice) => choice switch
    {
        PlayerChoice.Vlc => "VLC",
        PlayerChoice.Foobar2000 => "Foobar2000",
        PlayerChoice.FastPlay => "FastPlay",
        _ => "Automatic"
    };

    internal static Player? FindPlayer(PlayerChoice choice, string? custom = null)
    {
        const uint associatedExecutable = 2;
        var associated = new StringBuilder(4096);
        uint length = (uint)associated.Capacity;
        string? association = AssocQueryString(0, associatedExecutable, ".m3u8", null, associated, ref length) == 0
            ? associated.ToString() : null;
        return ResolvePlayer(choice, association, ProgramRoots(), custom);
    }

    private static IEnumerable<string> ProgramRoots() => new[] { Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86) }.Where(root => root.Length != 0).Distinct();

    internal static string? FindFoobar(string? custom) => ResolveFoobar(custom, ProgramRoots());

    internal static string? ResolveFoobar(string? custom, IEnumerable<string> roots) =>
        custom != null && Supported(custom) is { Kind: PlayerChoice.Foobar2000 } chosen ? chosen.Executable :
        roots.Select(root => Path.Combine(root, "foobar2000", "foobar2000.exe")).FirstOrDefault(File.Exists);

    internal static Player? ResolvePlayer(PlayerChoice choice, string? association, IEnumerable<string> roots, string? custom = null)
    {
        bool Matches(PlayerChoice kind) => choice == PlayerChoice.Automatic || choice == kind;
        // A player chosen in Preferences covers portable copies outside Program Files.
        if (custom != null && Supported(custom) is Player chosen && Matches(chosen.Kind)) return chosen;
        if (association != null && Supported(association) is Player preferred && Matches(preferred.Kind)) return preferred;

        (PlayerChoice Kind, string Relative)[] candidates =
            [(PlayerChoice.Vlc, Path.Combine("VideoLAN", "VLC", "vlc.exe")),
             (PlayerChoice.Foobar2000, Path.Combine("foobar2000", "foobar2000.exe")),
             (PlayerChoice.FastPlay, Path.Combine("FastPlay", "FastPlay.exe"))];
        foreach (var (kind, relative) in candidates)
        {
            if (!Matches(kind)) continue;
            foreach (var root in roots)
            {
                string path = Path.Combine(root, relative);
                if (Supported(path) is Player installed) return installed;
            }
        }
        return null;
    }

    internal static Player? Supported(string path)
    {
        if (!Path.IsPathFullyQualified(path) || !File.Exists(path)) return null;
        return KindOf(path) is PlayerChoice kind ? new Player(path, kind) : null;
    }

    internal static PlayerChoice? KindOf(string path) => Path.GetFileName(path).ToLowerInvariant() switch
    {
        "vlc.exe" => PlayerChoice.Vlc,
        "foobar2000.exe" => PlayerChoice.Foobar2000,
        "fastplay.exe" => PlayerChoice.FastPlay,
        _ => null
    };

    internal static ProcessStartInfo Command(Player player, Station station, string? preparedPlaylist = null)
    {
        if (!PublicNetwork.TryUrl(station.Url.AbsoluteUri, null, out var address))
            throw new InvalidDataException("The stream does not have a public HTTP or HTTPS address.");
        var start = new ProcessStartInfo(player.Executable) { UseShellExecute = false };
        if (player.Kind == PlayerChoice.FastPlay)
        {
            if (preparedPlaylist == null || !Path.IsPathFullyQualified(preparedPlaylist) ||
                !Path.GetExtension(preparedPlaylist).Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A temporary playlist is required for FastPlay.");
            start.ArgumentList.Add(preparedPlaylist);
            return start;
        }
        if (player.Kind == PlayerChoice.Foobar2000) start.ArgumentList.Add("/immediate");
        start.ArgumentList.Add(address.AbsoluteUri);
        return start;
    }

    [DllImport("shlwapi.dll", EntryPoint = "AssocQueryStringW", CharSet = CharSet.Unicode)]
    private static extern int AssocQueryString(uint flags, uint query, string association, string? verb,
        StringBuilder output, ref uint characters);
}

internal static class PlaylistFile
{
    private static readonly UTF8Encoding Utf8 = new(false);

    internal static string SuggestedName(string value)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = string.Concat(value.Take(96).Select(c => invalid.Contains(c) || char.IsControl(c) ? '_' : c)).Trim(' ', '.');
        if (name.Length == 0) return "Stream";
        var first = name.Split('.')[0].TrimEnd().ToUpperInvariant();
        if (first is "CON" or "PRN" or "AUX" or "NUL" ||
            first.Length == 4 && (first.StartsWith("COM", StringComparison.Ordinal) || first.StartsWith("LPT", StringComparison.Ordinal)) &&
            (char.IsAsciiDigit(first[3]) || first[3] is '\u00b9' or '\u00b2' or '\u00b3'))
            return "_" + name;
        return name;
    }

    internal static string SuggestedName(IReadOnlyList<Station> stations) =>
        stations.Count == 1 ? SuggestedName(stations[0].Name) : "Streams";

    internal static string Text(IReadOnlyList<Station> stations)
    {
        if (stations.Count == 0) throw new InvalidDataException("Select at least one stream.");
        var playlist = new StringBuilder("#EXTM3U\r\n");
        foreach (var station in stations)
        {
            if (!PublicNetwork.TryUrl(station.Url.AbsoluteUri, null, out var address))
                throw new InvalidDataException("A stream does not have a public HTTP or HTTPS address.");
            string title = string.Concat(station.Name.Take(160).Select(c => char.IsControl(c) ? ' ' : c)).Trim();
            playlist.Append("#EXTINF:-1,").Append(title).Append("\r\n").Append(address.AbsoluteUri).Append("\r\n");
        }
        return playlist.ToString();
    }

    internal static void Write(string path, IReadOnlyList<Station> stations, bool replace)
    {
        var payload = Text(stations);
        using var file = new FileStream(path, replace ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.Read);
        try { using var writer = new StreamWriter(file, Utf8, leaveOpen: true); writer.Write(payload); }
        catch
        {
            // A failed new export must not leave an empty or partial file.
            file.Dispose();
            if (!replace) try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static string SaveNew(string folder, IReadOnlyList<Station> stations)
    {
        Directory.CreateDirectory(folder);
        var stem = SuggestedName(stations);
        for (int i = 1; i <= 100; i++)
        {
            var path = Path.Combine(folder, i == 1 ? stem + ".m3u8" : $"{stem} ({i}).m3u8");
            try { Write(path, stations, replace: false); return path; }
            catch (IOException) when (File.Exists(path)) { }
        }
        throw new IOException("There are too many playlists with that name in the selected folder.");
    }
}

internal static class FoobarHandoff
{
    private static readonly TimeSpan Retention = TimeSpan.FromMinutes(2);
    private const int MaxPending = 24;
    internal static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), Program.Name, "FoobarHandoffs");

    internal static string Stage(string folder, IReadOnlyList<Station> stations)
    {
        if (Prune(folder, DateTime.UtcNow) >= MaxPending)
            throw new IOException("Too many playlists were sent recently. Try again in two minutes.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "handoff-" + Guid.NewGuid().ToString("N") + ".m3u8");
        PlaylistFile.Write(path, stations, replace: false);
        return path;
    }

    internal static ProcessStartInfo Command(string executable, string playlistPath)
    {
        var launch = new ProcessStartInfo(executable) { UseShellExecute = false };
        launch.ArgumentList.Add("/add");
        launch.ArgumentList.Add(playlistPath);
        return launch;
    }

    internal static int Prune(string folder, DateTime now)
    {
        for (string? parent = Path.GetFullPath(folder); parent != null; parent = Path.GetDirectoryName(parent))
            if (Directory.Exists(parent) && (File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The Foobar2000 handoff folder is redirected.");
        if (!Directory.Exists(folder)) return 0;
        int remaining = 0;
        foreach (var path in Directory.EnumerateFiles(folder, "handoff-*.m3u8").Take(MaxPending * 3))
        {
            var name = Path.GetFileNameWithoutExtension(path);
            if (name.Length != 40 || !Guid.TryParseExact(name[8..], "N", out _)) continue;
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
            // Foobar's command-line relay can exit before the existing instance reads the file.
            if (File.GetLastWriteTimeUtc(path) <= now - Retention)
            {
                try { File.Delete(path); continue; }
                catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
            remaining++;
        }
        return remaining;
    }
}
