namespace StreamSift.Mobile;

internal static class SearchHeartbeat
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    internal static async Task RunAsync(Action pulse, CancellationToken stop)
    {
        using var timer = new PeriodicTimer(Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stop).ConfigureAwait(false))
            {
                if (stop.IsCancellationRequested) return;
                pulse();
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }
}
