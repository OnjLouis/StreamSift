namespace StreamSift;

public enum SearchMethod { Passive, Browser, Directory }

public sealed record SearchRun(SearchResult Result, SearchMethod Method, int Attempts, int FailedMethods);

public sealed class SearchSequence(HttpClient client, string? cacheDirectory = null)
{
    private static readonly SearchMethod[] FallbackOrder = [SearchMethod.Passive, SearchMethod.Directory, SearchMethod.Browser];

    public Task<SearchRun> FindAsync(string input, SearchMethod first, bool fallback, CancellationToken cancel,
        Action<SearchMethod, int>? starting = null)
    {
        if (!PublicNetwork.TryInput(input, out var address)) throw new ArgumentException("Invalid public address.");
        var plain = ImplicitHttpAlternative(input, address);
        return RunAsync(first, fallback, (method, token) => RunMethod(address, plain, method, token), cancel, starting);
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
        input = StationText(input);
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

    internal static string StationText(string input)
    {
        input = input.Trim();
        // A bare filename is a station-name hint, not a website or a codec filter.
        int dot = input.IndexOf('.');
        if (dot > 0 && input[dot..].ToLowerInvariant() is ".mp3" or ".aac" or ".flac" or ".ogg" or ".opus" &&
            !input.Contains('/') && !input.Contains('\\') && !input.Contains(':')) return input[..dot].TrimEnd();
        return input;
    }

    public async Task<SearchRun> FindStationAsync(string name, CancellationToken cancel,
        IEnumerable<string>? recentSearches = null)
    {
        if (!CanSearchStation(name)) throw new ArgumentException("Invalid station name.");
        SearchResult? fromWebsite = null;
        if (FindRecentSite(StationText(name), recentSearches) is Uri site)
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            try
            {
                fromWebsite = await new Discovery(client).FindAsync(site, deadline.Token).ConfigureAwait(false);
                if (fromWebsite.Stations.Count > 0) return new SearchRun(fromWebsite, SearchMethod.Passive, 1, 0);
            }
            catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { }
        }
        var result = await new DirectoryDiscovery(client, cacheDirectory).FindByNameAsync(name, cancel).ConfigureAwait(false);
        return new SearchRun(result with { Requests = result.Requests + (fromWebsite?.Requests ?? 0),
            Unreachable = result.Unreachable + (fromWebsite?.Unreachable ?? 0),
            LimitReached = result.LimitReached || (fromWebsite?.LimitReached ?? false) },
            SearchMethod.Directory, fromWebsite != null ? 2 : 1, 0);
    }

    private static Uri? FindRecentSite(string name, IEnumerable<string>? searched)
    {
        var key = CompactStationKey(name);
        if (key.Length < 3 || searched == null) return null;
        foreach (var entry in searched.Take(10))
        {
            if (!PublicNetwork.TryInput(entry, out var site) || site.HostNameType != UriHostNameType.Dns ||
                !site.IsDefaultPort || site.AbsolutePath != "/" || site.Query.Length != 0 || entry.Contains('#')) continue;
            var host = site.IdnHost.StartsWith("www.", StringComparison.Ordinal) ? site.IdnHost[4..] : site.IdnHost;
            int dot = host.IndexOf('.');
            if (dot > 0 && CompactStationKey(host[..dot]) == key) return site;
        }
        return null;
    }

    internal static string CompactStationKey(string name) => new(name.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

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
        IEnumerable<SearchMethod> methods = fallback ? new[] { first }.Concat(FallbackOrder.Where(method => method != first)) : [first];
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
