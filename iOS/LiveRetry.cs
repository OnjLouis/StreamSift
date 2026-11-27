using System.Diagnostics;

namespace StreamSift.Mobile;

internal sealed class LiveRetry
{
    private static readonly TimeSpan StablePlayback = TimeSpan.FromSeconds(30);
    private const int MaxRetries = 5;
    private int attempts;
    private long? playingSince;

    internal void Playing() => playingSince ??= Stopwatch.GetTimestamp();

    internal void Buffering()
    {
        if (playingSince is long started && Stopwatch.GetElapsedTime(started) >= StablePlayback)
            attempts = 0;
        playingSince = null;
    }

    internal TimeSpan? NextDelay()
    {
        Buffering();
        return attempts < MaxRetries ? TimeSpan.FromSeconds(1 << attempts++) : null;
    }

    internal void Reset()
    {
        attempts = 0;
        playingSince = null;
    }
}
