using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using StreamSift;

static string Argument(SearchMethod method) => method switch
{
    SearchMethod.Browser => "--browser-json",
    SearchMethod.Directory => "--directory-json",
    _ => "--json"
};

SearchMethod? selected = args.FirstOrDefault() switch
{
    "--json" => SearchMethod.Passive,
    "--browser-json" => SearchMethod.Browser,
    "--directory-json" => SearchMethod.Directory,
    _ => null
};
bool fallback = args is [_, "--fallback"];
if (selected is not SearchMethod method || (args.Length != 1 && !fallback))
{
    Console.Error.WriteLine("Usage: Scanner (--json | --browser-json | --directory-json) [--fallback] (one website address on standard input)");
    return 2;
}

// Use stdin so a stream address with temporary query parameters is not in the process command line.
char[] input = new char[4097];
int length = await Console.In.ReadBlockAsync(input);
if (length == input.Length) return 3;
var searchText = new string(input, 0, length);
bool nameInput = SearchSequence.CanSearchStation(searchText);
bool hasAddress = PublicNetwork.TryInput(searchText, out var address) && !nameInput;
bool stationSearch = nameInput && method != SearchMethod.Browser;
if (!hasAddress && !stationSearch) return 3;
if (stationSearch) method = SearchMethod.Directory;

using var client = PublicNetwork.CreateClient();
if (method == SearchMethod.Browser && !fallback && !BrowserDiscovery.IsAvailable) return 4;
using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsMacOS()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); })
    : null;
SearchRun run;
try
{
    var sequence = new SearchSequence(client);
    run = stationSearch ? await sequence.FindStationAsync(searchText, cancellation.Token) :
        await sequence.FindAsync(address, method, fallback, cancellation.Token, (next, number) =>
    {
        if (number > 1)
        {
            // Fallback emits short progress lines before the final compact JSON reply.
            Console.Out.WriteLine(Argument(next));
            Console.Out.Flush();
        }
    });
}
catch (OperationCanceledException)
{
    return 5;
}
catch (Exception error) when (method == SearchMethod.Browser && (error is IOException or HttpRequestException or InvalidOperationException or
    System.ComponentModel.Win32Exception or System.Net.WebSockets.WebSocketException or JsonException or UnauthorizedAccessException))
{
    Console.Error.WriteLine("Browser-assisted search could not finish.");
    return 6;
}
catch (Exception error) when (method == SearchMethod.Directory && (error is IOException or HttpRequestException or JsonException or UnauthorizedAccessException))
{
    Console.Error.WriteLine("Public directory lookup could not finish.");
    return 7;
}
var found = run.Result;
var output = new Reply(found.Stations.Select(s => new ResultRow(s.Name, s.Url.AbsoluteUri, s.Format, s.Bitrate,
    s.FoundOn.AbsoluteUri)).ToArray(), found.Requests, found.Unreachable, found.LimitReached, Argument(run.Method), run.Attempts, run.FailedMethods);
await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), output, ScannerJson.Default.Reply);
return 0;

internal sealed record ResultRow(string Name, string Url, string Format, int? Bitrate, string FoundOn);
internal sealed record Reply(ResultRow[] Stations, int Requests, int Unreachable, bool LimitReached, string Method, int Attempts, int FailedMethods);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Reply))]
internal sealed partial class ScannerJson : JsonSerializerContext;
