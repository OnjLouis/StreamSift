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

static ResultRow ScannerRow(Station row) => new(row.Name, row.Url.AbsoluteUri, row.Format, row.Bitrate,
    row.FoundOn.AbsoluteUri, row.Listeners, row.IsVideo, row.IsHls, row.IsTrack, row.DateModified, row.DateModifiedLabel,
    row.FileSizeBytes, row.FileSizeLabel, row.ScanTitle);

if (args is ["--import-favourites"] or ["--export-favourites"])
{
    try
    {
        using var playlistInput = Console.OpenStandardInput();
        using var playlistOutput = Console.OpenStandardOutput();
        if (args[0] == "--import-favourites")
        {
            var imported = await FavouritePlaylist.ReadAsync(playlistInput);
            var reply = new FavouriteReply(imported.Stations.Select(ScannerRow).ToArray(), imported.Skipped);
            await JsonSerializer.SerializeAsync(playlistOutput, reply, ScannerJson.Default.FavouriteReply);
        }
        else
        {
            var data = await FavouritePlaylist.ReadDocumentAsync(playlistInput);
            var entries = JsonSerializer.Deserialize(data, ScannerJson.Default.StationArray) ??
                throw new InvalidDataException("No favourites were supplied.");
            await playlistOutput.WriteAsync(FavouritePlaylist.Export(entries));
        }
        return 0;
    }
    catch (Exception error) when (error is IOException or InvalidDataException or JsonException or ArgumentException or UnauthorizedAccessException)
    {
        Console.Error.WriteLine("The favourites playlist could not be read or written.");
        return 8;
    }
}

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

// Use stdin for the search and optional recent websites, never command-line arguments.
const int SearchLength = 4096, HistoryEntries = 10, HistoryEntryLength = 1024;
char[] input = new char[SearchLength + 2 + HistoryEntries * (HistoryEntryLength + 2) + 1];
int length = await Console.In.ReadBlockAsync(input);
if (length == input.Length) return 3;
var lines = new string(input, 0, length).TrimEnd('\r', '\n').Split('\n');
var searchText = lines[0].TrimEnd('\r');
if (searchText.Length > SearchLength || lines.Length > HistoryEntries + 1 ||
    lines.Skip(1).Any(line => line.TrimEnd('\r').Length > HistoryEntryLength)) return 3;
var recentSites = method == SearchMethod.Passive ? lines.Skip(1).Select(line => line.TrimEnd('\r')) : null;
bool nameInput = SearchSequence.CanSearchStation(searchText);
bool hasAddress = PublicNetwork.TryInput(searchText, out _) && !nameInput;
bool stationSearch = nameInput && method != SearchMethod.Browser;
if (!hasAddress && !stationSearch) return 3;
if (stationSearch) method = SearchMethod.Directory;

using var client = PublicNetwork.CreateClient(readStreamTitles: true);
if (method == SearchMethod.Browser && !fallback && !BrowserDiscovery.IsAvailable) return 4;
using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsMacOS()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); })
    : null;
SearchRun run;
try
{
    var sequence = new SearchSequence(client);
    run = stationSearch ? await sequence.FindStationAsync(searchText, cancellation.Token, recentSites) :
        await sequence.FindAsync(searchText, method, fallback, cancellation.Token, (next, number) =>
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
var output = new Reply(found.Stations.Select(ScannerRow).ToArray(), found.Requests, found.Unreachable, found.LimitReached,
    Argument(run.Method), run.Attempts, run.FailedMethods, found.EntryWasWebsite, found.EntryWasFileIndex,
    found.PreferListedOrder, found.ListingNotice);
await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), output, ScannerJson.Default.Reply);
return 0;

internal sealed record ResultRow(string Name, string Url, string Format, int? Bitrate, string FoundOn, int? Listeners,
    bool IsVideo, bool IsHls, bool IsTrack, DateTime? DateModified, string? DateModifiedLabel, long? FileSizeBytes,
    string? FileSizeLabel, string? ScanTitle);
internal sealed record FavouriteReply(ResultRow[] Stations, int Skipped);
internal sealed record Reply(ResultRow[] Stations, int Requests, int Unreachable, bool LimitReached, string Method, int Attempts, int FailedMethods,
    bool EntryWasWebsite, bool EntryWasFileIndex, bool PreferListedOrder, string? ListingNotice);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Reply))]
[JsonSerializable(typeof(FavouriteReply))]
[JsonSerializable(typeof(Station[]))]
internal sealed partial class ScannerJson : JsonSerializerContext;
