using System.Text.Json;
using System.Text.Json.Serialization;
using StreamSift;

if (args is not ["--json"])
{
    Console.Error.WriteLine("Usage: Scanner --json (one website address on standard input)");
    return 2;
}

// Use stdin so a stream address with temporary query parameters is not in the process command line.
char[] input = new char[4097];
int length = await Console.In.ReadBlockAsync(input);
if (length == input.Length || !PublicNetwork.TryInput(new string(input, 0, length), out var address))
    return 3;

using var client = PublicNetwork.CreateClient();
var found = await new Discovery(client).FindAsync(address, CancellationToken.None);
var output = new Reply(found.Stations.Select(s => new ResultRow(s.Name, s.Url.AbsoluteUri, s.Format, s.Bitrate,
    s.FoundOn.AbsoluteUri)).ToArray(), found.Requests, found.Unreachable, found.LimitReached);
await JsonSerializer.SerializeAsync(Console.OpenStandardOutput(), output, ScannerJson.Default.Reply);
return 0;

internal sealed record ResultRow(string Name, string Url, string Format, int? Bitrate, string FoundOn);
internal sealed record Reply(ResultRow[] Stations, int Requests, int Unreachable, bool LimitReached);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Reply))]
internal sealed partial class ScannerJson : JsonSerializerContext;
