namespace StreamSift;

public sealed class BrowserDiscovery
{
    public BrowserDiscovery(HttpClient client) { }

    public static bool IsAvailable => false;

    public Task<SearchResult> FindAsync(Uri start, CancellationToken cancel, IProgress<int>? progress = null) =>
        throw new PlatformNotSupportedException("Browser-assisted search requires a current version of Chrome or Edge.");
}
