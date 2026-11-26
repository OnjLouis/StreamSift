using System.Media;
using System.Reflection;

namespace StreamSift;

internal sealed class SearchSounds : IDisposable
{
    internal const string SearchResource = "StreamSift.Sounds.search.wav";
    internal const string DoneResource = "StreamSift.Sounds.done.wav";
    internal const string FailureResource = "StreamSift.Sounds.error.wav";
    private const int RepeatIntervalMilliseconds = 2000;
    private readonly SoundPlayer? start = Load(SearchResource);
    private readonly SoundPlayer? done = Load(DoneResource);
    private readonly SoundPlayer? failure = Load(FailureResource);
    private readonly System.Windows.Forms.Timer repeat = new() { Interval = RepeatIntervalMilliseconds };
    private CancellationToken search;
    internal bool Enabled { get; set; } = true;

    internal SearchSounds()
    {
        repeat.Tick += (_, _) =>
        {
            if (search.IsCancellationRequested) SearchStopped();
            else Play(start);
        };
    }

    internal void SearchStarted(CancellationToken cancellation)
    {
        search = cancellation;
        Play(start);
        if (Enabled && start != null) repeat.Start();
    }

    internal void SearchStopped() => repeat.Stop();
    internal static string CompletionResource(int verifiedStreams) => verifiedStreams > 0 ? DoneResource : FailureResource;
    internal void SearchCompleted(int verifiedStreams)
    {
        SearchStopped();
        Play(CompletionResource(verifiedStreams) == DoneResource ? done : failure);
    }

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
        repeat.Dispose();
        foreach (var player in new[] { start, done, failure })
        {
            if (player == null) continue;
            player.Stream?.Dispose();
            player.Dispose();
        }
    }
}
