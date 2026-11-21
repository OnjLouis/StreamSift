using System.Diagnostics;
using System.Text;

namespace StreamSift;

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

    internal static string Text(Station station)
    {
        if (!PublicNetwork.TryUrl(station.Url.AbsoluteUri, null, out var address))
            throw new InvalidDataException("The stream does not have a public HTTP or HTTPS address.");
        string title = string.Concat(station.Name.Take(160).Select(c => char.IsControl(c) ? ' ' : c)).Trim();
        return $"#EXTM3U\r\n#EXTINF:-1,{title}\r\n{address.AbsoluteUri}\r\n";
    }

    internal static void Write(string path, Station station, bool replace)
    {
        var payload = Text(station);
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

    internal static string SaveNew(string folder, Station station)
    {
        Directory.CreateDirectory(folder);
        var stem = SuggestedName(station.Name);
        for (int i = 1; i <= 100; i++)
        {
            var path = Path.Combine(folder, i == 1 ? stem + ".m3u8" : $"{stem} ({i}).m3u8");
            try { Write(path, station, replace: false); return path; }
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

    internal static string Stage(string folder, Station station)
    {
        if (Prune(folder, DateTime.UtcNow) >= MaxPending)
            throw new IOException("Too many playlists were sent recently. Try again in two minutes.");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "handoff-" + Guid.NewGuid().ToString("N") + ".m3u8");
        PlaylistFile.Write(path, station, replace: false);
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
