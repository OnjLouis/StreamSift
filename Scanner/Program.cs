using System.Text.Json;
using System.Text.Json.Serialization;
using System.Runtime.InteropServices;
using StreamSift;

bool browser = args is ["--browser-json"];
bool directory = args is ["--directory-json"];
if (args is not ["--json"] && !browser && !directory)
{
    Console.Error.WriteLine("Usage: Scanner --json | --browser-json | --directory-json (one website address on standard input)");
    return 2;
}

// Use stdin so a stream address with temporary query parameters is not in the process command line.
char[] input = new char[4097];
int length = await Console.In.ReadBlockAsync(input);
if (length == input.Length || !PublicNetwork.TryInput(new string(input, 0, length), out var address))
    return 3;

using var client = PublicNetwork.CreateClient();
if (browser && !BrowserDiscovery.IsAvailable) return 4;
using var cancellation = new CancellationTokenSource();
using var termination = OperatingSystem.IsMacOS()
    ? PosixSignalRegistration.Create(PosixSignal.SIGTERM, context => { context.Cancel = true; cancellation.Cancel(); })
    : null;
SearchResult found;
try
{
    found = browser ? await new BrowserDiscovery(client).FindAsync(address, cancellation.Token)
                    : directory ? await new DirectoryDiscovery(client).FindAsync(address, cancellation.Token)
                                : await new Discovery(client).FindAsync(address, cancellation.Token);
}
catch (OperationCanceledException)
{
    return 5;
}
catch (Exception error) when (browser && (error is IOException or HttpRequestException or InvalidOperationException or
    System.ComponentModel.Win32Exception or System.Net.WebSockets.WebSocketException or JsonException or UnauthorizedAccessException))
{
    Console.Error.WriteLine("Browser-assisted search could not finish.");
    return 6;
}
catch (Exception error) when (directory && (error is IOException or HttpRequestException or JsonException or UnauthorizedAccessException))
{
    Console.Error.WriteLine("Public directory lookup could not finish.");
    return 7;
}
var output = new Reply(found.Stations.Select(s => new ResultRow(s.Name, s.Url.AbsoluteUri, s.Format, s.Bitrate,
    s.FoundOn.AbsoluteUri)).ToArray(), found.Requests, found.Unreachable, found.LimitReached);
await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), output, ScannerJson.Default.Reply);
return 0;

internal sealed record ResultRow(string Name, string Url, string Format, int? Bitrate, string FoundOn);
internal sealed record Reply(ResultRow[] Stations, int Requests, int Unreachable, bool LimitReached);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Reply))]
internal sealed partial class ScannerJson : JsonSerializerContext;
