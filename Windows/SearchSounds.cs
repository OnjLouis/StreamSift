using System.Media;
using System.Reflection;

namespace StreamSift;

internal sealed class SearchSounds : IDisposable
{
    internal const string SearchResource = "StreamSift.Sounds.search.wav";
    internal const string DoneResource = "StreamSift.Sounds.done.wav";
    private readonly SoundPlayer? start = Load(SearchResource);
    private readonly SoundPlayer? done = Load(DoneResource);
    internal bool Enabled { get; set; } = true;

    internal void SearchStarted() => Play(start);
    internal void SearchCompleted() => Play(done);

    private void Play(SoundPlayer? player)
    {
        if (!Enabled || player == null) return;
        try { player.Play(); }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException) { }
    }

    private static SoundPlayer? Load(string resource)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resource);
        if (stream == null) return null;
        using var bytes = new MemoryStream();
        stream.CopyTo(bytes);
        var player = new SoundPlayer(new MemoryStream(bytes.ToArray(), writable: false));
        try { player.Load(); return player; }
        catch (Exception e) when (e is InvalidOperationException or IOException or TimeoutException)
        {
            player.Stream?.Dispose();
            player.Dispose();
            return null;
        }
    }

    public void Dispose()
    {
        foreach (var player in new[] { start, done })
        {
            if (player == null) continue;
            player.Stream?.Dispose();
            player.Dispose();
        }
    }
}
