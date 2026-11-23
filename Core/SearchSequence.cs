namespace StreamSift;

public enum SearchMethod { Passive, Browser, Directory }

public sealed record SearchRun(SearchResult Result, SearchMethod Method, int Attempts, int FailedMethods);

public sealed class SearchSequence(HttpClient client, string? cacheDirectory = null)
{
    public Task<SearchRun> FindAsync(string input, SearchMethod first, bool fallback, CancellationToken cancel,
        Action<SearchMethod, int>? starting = null)
    {
        if (!PublicNetwork.TryInput(input, out var address)) throw new ArgumentException("Invalid public address.");
        address = PreferredVideoPage(address);
        var plain = ImplicitHttpAlternative(input, address);
        return RunAsync(first, fallback, (method, token) => RunMethod(address, plain, method, token), cancel, starting);
    }

    internal static Uri PreferredVideoPage(Uri address)
    {
        // NBC10 Boston's advertised domains redirect to its canonical homepage. The
        // station's public live player is on /watch/, which avoids a long news crawl.
        if (address.IsDefaultPort && address.AbsolutePath == "/" && address.Query.Length == 0 &&
            address.IdnHost is "nbc10boston.com" or "www.nbc10boston.com" or "nbcboston.com" or "www.nbcboston.com")
            return new Uri("https://www.nbcboston.com/watch/");
        if (address.IsDefaultPort && address.AbsolutePath == "/" && address.Query.Length == 0)
        {
            if (address.IdnHost is "cbsboston.com" or "www.cbsboston.com")
                return new Uri("https://www.cbsnews.com/boston/live/news-local-news/");
            if (address.IdnHost is "boston25.com" or "www.boston25.com" or "boston25news.com" or "www.boston25news.com")
                return new Uri("https://www.boston25news.com/video/");
            if (address.IdnHost is "wcvb.com" or "www.wcvb.com")
                return new Uri("https://www.wcvb.com/nowcast");
        }
        return address;
    }

    internal static Uri? ImplicitHttpAlternative(string input, Uri address)
    {
        if (input.Contains("://", StringComparison.Ordinal) || address.IsDefaultPort || address.Query.Length != 0)
            return null;
        var alternative = new UriBuilder(address) { Scheme = "http", Port = address.Port };
        return PublicNetwork.TryUrl(alternative.Uri.AbsoluteUri, null, out var safe) ? safe : null;
    }

    public static bool CanSearchStation(string input)
    {
        input = input.Trim();
        // A bare FM frequency such as 104.3 can also parse as a shortened IPv4 address.
        if (input.Length is >= 4 and <= 6 && input.Count(c => c == '.') == 1 &&
            input.All(c => char.IsAsciiDigit(c) || c == '.') &&
            decimal.TryParse(input, System.Globalization.NumberStyles.AllowDecimalPoint,
                System.Globalization.CultureInfo.InvariantCulture, out var frequency) && frequency is >= 76 and <= 108)
            return true;
        return input is { Length: >= 2 and <= 96 } && input.Any(char.IsLetterOrDigit) &&
            input.All(c => char.IsLetterOrDigit(c) || char.IsWhiteSpace(c) && !char.IsControl(c) || c is '.' or '-' or '\'' or '&') &&
            (!input.Contains('.') || input.Any(char.IsWhiteSpace)) &&
            !PublicNetwork.TryInput(input, out _);
    }

    public async Task<SearchRun> FindStationAsync(string name, CancellationToken cancel)
    {
        if (!CanSearchStation(name)) throw new ArgumentException("Invalid station name.");
        var result = await new DirectoryDiscovery(client, cacheDirectory).FindByNameAsync(name, cancel).ConfigureAwait(false);
        return new SearchRun(result, SearchMethod.Directory, 1, 0);
    }

    private Task<SearchResult> RunMethod(Uri address, Uri? plain, SearchMethod method, CancellationToken token) => method switch
    {
        SearchMethod.Passive => new Discovery(client).FindInferredAsync(address, plain, token),
        SearchMethod.Browser when BrowserDiscovery.IsAvailable => new BrowserDiscovery(client).FindAsync(address, token),
        SearchMethod.Browser => throw new InvalidOperationException("Browser search requires Chrome or Microsoft Edge."),
        SearchMethod.Directory => new DirectoryDiscovery(client, cacheDirectory).FindAsync(address, token),
        _ => throw new ArgumentOutOfRangeException(nameof(method))
    };

    internal static async Task<SearchRun> RunAsync(SearchMethod first, bool fallback,
        Func<SearchMethod, CancellationToken, Task<SearchResult>> scan, CancellationToken cancel,
        Action<SearchMethod, int>? starting = null)
    {
        IEnumerable<SearchMethod> methods = fallback ? new[] { first }.Concat(Enum.GetValues<SearchMethod>().Where(method => method != first)) : [first];
        int attempts = 0, failed = 0, requests = 0, unavailable = 0;
        bool limitReached = false;
        SearchMethod last = first;
        foreach (var method in methods)
        {
            cancel.ThrowIfCancellationRequested();
            last = method;
            attempts++;
            starting?.Invoke(method, attempts);
            try
            {
                var result = await scan(method, cancel).ConfigureAwait(false);
                cancel.ThrowIfCancellationRequested();
                requests += result.Requests;
                unavailable += result.Unreachable;
                limitReached |= result.LimitReached;
                if (result.Stations.Count != 0)
                    return new SearchRun(result with { Requests = requests, Unreachable = unavailable, LimitReached = limitReached }, method, attempts, failed);
            }
            catch (Exception error) when (fallback && !cancel.IsCancellationRequested && error is
                OperationCanceledException or HttpRequestException or IOException or InvalidOperationException or
                System.ComponentModel.Win32Exception or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        cancel.ThrowIfCancellationRequested();
        return new SearchRun(new SearchResult([], requests, unavailable, limitReached), last, attempts, failed);
    }
}
