using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics.Tracing;
using System.Text;
using StreamSift;

if (args.Length > 0)
{
    using var trace = args[0] == "--trace" ? new HttpTrace() : null;
    using var production = PublicNetwork.CreateClient();
    foreach (var input in trace == null ? args : args[1..])
    {
        if (!PublicNetwork.TryInput(input, out var address)) throw new ArgumentException("Invalid public address.");
        var live = await new Discovery(production).FindAsync(address, CancellationToken.None);
        Console.WriteLine($"{address.Host}: {live.Stations.Count} stream(s); {live.Requests} requests; {live.Unreachable} unavailable; limit {live.LimitReached}");
        foreach (var station in live.Stations) Console.WriteLine($"{station.Name} | {station.Format} | {station.Bitrate} | {station.Url} | {station.FoundOn}");
    }
    return;
}

int checks = 0;
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}
foreach (var text in new[] { "https://localhost/", "http://127.1/", "http://2130706433/", "http://[::ffff:127.0.0.1]/",
    "http://169.254.169.254/", "http://192.168.1.1/", "http://[fd01:234::1]/", "http://[2002:7f00:1::1]/",
    "http://100.100.100.100/", "file:///c:/secret", "https://user:pass@example.com", "http://mybox.local/",
    "https://example.com/a\\b", "https://example.com/a\r\nb" })
    Require(!PublicNetwork.TryInput(text, out _), "Accepted disallowed URL: " + text);
Require(PublicNetwork.TryInput("station.example/listen#player", out var bare) &&
    bare.AbsoluteUri == "https://station.example/listen", "Bare address/fragment.");
Require(PublicNetwork.IsPublic(IPAddress.Parse("1.1.1.1")), "Public IPv4.");
Require(PublicNetwork.IsPublic(IPAddress.Parse("2606:4700:4700::1111")), "Public IPv6.");
Require(!PublicNetwork.IsPublic(IPAddress.Parse("::ffff:10.0.0.3")), "Mapped private IPv4.");

var mp3 = new byte[900];
foreach (var i in new[] { 0, 417 }) new byte[] { 0xff, 0xfb, 0x90, 0x64 }.CopyTo(mp3, i);
var handler = new Fixture();
using var fixtureClient = new HttpClient(handler);
var engine = new Discovery(fixtureClient);
handler.Text("https://radio.example/", """
    <title>Example FM</title><link rel="stylesheet" href="/style.css">
    <a href="/how-to-listen">Ways to listen</a>
    <iframe src="https://player.radio.co/s1eb3875b8/default"></iframe>
    <script src="/player.js"></script><script>let ignored = "https://irrelevant.example/metrics";</script>
    <audio src="https://station.example/oldshow.mp3"></audio>
    """);
handler.Text("https://radio.example/how-to-listen", """
    <title>Listen</title><a href="/live.pls">Player playlist</a>
    <audio><source src="/audio/master.m3u8"><source src="/audio/vod.m3u8"></audio>
    <div data-dt-stream="https://streams.example/standard?channel=two&amp;region=uk"></div>
    <a href="https://localhost/admin">Stream</a>
    <a href="/wrong.mp3">Station</a>
    <a href="/blocked.mp3">Private redirect</a>
    """);
handler.Text("https://radio.example/player.js", """
    const config = {streamUrl: "https:\/\/streams.example\/opus", audioUrl: "/xml.xspf"};
    """, "application/javascript");
handler.Text("https://radio.example/live.pls",
    "[playlist]\nNumberOfEntries=2\nTitle1=Named Station\nFile1=https://streams.example/one\nLength1=-1\nFile2=https://streams.example/one#duplicate",
    "audio/x-scpls");
handler.Text("https://radio.example/xml.xspf",
    """<playlist xmlns="http://xspf.org/ns/0/"><trackList><track><title>XML Radio</title><location>https://streams.example/two</location></track></trackList></playlist>""",
    "application/xspf+xml");
handler.Text("https://radio.example/audio/master.m3u8",
    "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=500000,CODECS=\"avc1.123,mp4a.40.2\"\nvideo.m3u8\n#EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID=\"audio\",URI=\"audio.m3u8\"\n",
    "application/vnd.apple.mpegurl");
handler.Text("https://radio.example/audio/audio.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.ts", "application/vnd.apple.mpegurl");
handler.Text("https://radio.example/audio/vod.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nseg.aac\n#EXT-X-ENDLIST", "application/vnd.apple.mpegurl");
handler.Audio("https://streams.example/one", mp3);
handler.Audio("https://streams.example/two", mp3);
handler.Audio("https://streams.example/standard?channel=two&region=uk", mp3, "Example Live", "192");
handler.Audio("https://streams.example/opus", Encoding.ASCII.GetBytes("OggS    OpusHead test"), type: "audio/ogg");
handler.Audio("https://station.example/oldshow.mp3", mp3, finite: true);
handler.Audio("https://radio.example/wrong.mp3", "this is not audio"u8.ToArray());
handler.Redirect("https://radio.example/blocked.mp3", "http://127.0.0.1/");
handler.Redirect("https://streaming.radio.co/s1eb3875b8/listen", "https://s2.radio.co/s1eb3875b8/listen");
handler.Audio("https://s2.radio.co/s1eb3875b8/listen", mp3, "Double Tap", "192");
var results = await engine.FindAsync(new Uri("https://radio.example/"), CancellationToken.None);
Require(results.Stations.Count == 6, $"Expected 6 distinct live results, found {results.Stations.Count}.");
Require(results.Stations.Any(x => x.Name == "Double Tap" && x.Bitrate == 192 && x.Format == "MP3"), "Provider redirect and metadata.");
Require(results.Stations.Any(x => x.Name == "Named Station"), "PLS title.");
Require(results.Stations.Any(x => x.Name == "XML Radio"), "XSPF title.");
Require(results.Stations.Any(x => x.Format == "Ogg Opus"), "Passive script and Opus.");
Require(results.Stations.Any(x => x.IsHls && x.Url.AbsolutePath.EndsWith("/audio.m3u8")), "Audio HLS rendition.");
Require(results.Stations.Any(x => x.Url.Query == "?channel=two&region=uk" && x.Name == "Example Live"), "HTML entity handling.");
Require(!handler.Seen.Any(x => x.Contains("seg.") || x.Contains("video.m3u8") || x.Contains("irrelevant") || x.Contains("localhost") ||
    x.Contains("127.0.0.1") || x.Contains("style.css")), "Fetched a segment, video, private URL or irrelevant resource.");
Require(results.Requests == handler.Seen.Count && !results.LimitReached, "Requests were not tracked correctly.");
Require(results.Stations.All(x => !x.Url.AbsoluteUri.Contains("oldshow") && !x.Url.AbsoluteUri.Contains("vod")), "Recording returned as live.");
Require(results.Unreachable == 1, "Blocked redirect not counted.");

handler.Text("https://radio.example/m3u", "#EXTM3U\n#EXTINF:-1,Relative station\n/ownstream\n", "audio/x-mpegurl");
handler.Audio("https://radio.example/ownstream", mp3);
var m3u = await engine.FindAsync(new Uri("https://radio.example/m3u"), CancellationToken.None);
Require(m3u.Stations is [{ Name: "Relative station" }], "Relative playlist entry.");

var mp2 = new byte[1300];
foreach (var i in new[] { 0, 626 }) new byte[] { 0xff, 0xfd, 0xa0, 0x04 }.CopyTo(mp2, i);
handler.Audio("https://radio.example/dab", mp2, "DAB radio", "192");
var dab = await engine.FindAsync(new Uri("https://radio.example/dab"), CancellationToken.None);
Require(dab.Stations is [{ Format: "MP2", Bitrate: 192 }], "MPEG Layer II audio.");

handler.Text("https://radio.example/mixed", """
    <audio src="/protected.m3u8"></audio>
    <audio src="/invalid.m3u8"></audio>
    """);
handler.Text("https://radio.example/protected.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=SAMPLE-AES,URI=\"https://key.example/key\"\n#EXTINF:6,\npart.aac", "application/vnd.apple.mpegurl");
handler.Text("https://radio.example/invalid.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nhttp://127.0.0.1/part.aac", "application/vnd.apple.mpegurl");
var unsafeHls = await engine.FindAsync(new Uri("https://radio.example/mixed"), CancellationToken.None);
Require(unsafeHls.Stations.Count == 0 && handler.Seen.All(url => !url.Contains("key.example")), "Encrypted or non-public HLS.");

handler.Text("https://radio.example/popup", """<script>document.write('<script src="/js/audio.js?v=' + Date.now() + '"><\/script>');</script>""");
handler.Text("https://radio.example/js/audio.js", """
    // window.open('/obsolete/player.html');
    window.open('player/window.html?v=' + Date.now());
    """, "application/javascript");
handler.Text("https://radio.example/player/window.html", """<script>document.write('<script src="player.js?v=' + Date.now() + '"><\/script>');</script>""");
handler.Text("https://radio.example/player/player.js", """let tuned = {pinnedStreamURL: 'https://streams.example/port/;'};""", "application/javascript");
handler.Audio("https://streams.example/port/;", mp3);
var popup = await engine.FindAsync(new Uri("https://radio.example/popup"), CancellationToken.None);
Require(popup.Stations is [{ Url.AbsolutePath: "/port/;" }] && popup.Requests == 5, "Passive popup discovery and semicolon mount.");

handler.Text("https://radio.example/mirrors", """
    #EXTM3U
    http://ice1.somafm.com/test
    https://ice2.somafm.com/test
    https://ice3.somafm.com/test
    """, "audio/x-mpegurl");
handler.Audio("http://ice1.somafm.com/test", mp3);
handler.Audio("https://ice2.somafm.com/test", mp3);
var mirrors = await engine.FindAsync(new Uri("https://radio.example/mirrors"), CancellationToken.None);
Require(mirrors.Stations is [{ Url.Scheme: "https" }] && mirrors.Requests == 3, "SomaFM mirror coalescing and HTTPS preference.");

const int mountCount = 41;
handler.Text("https://icecast.example/", """
    <a href="guide.html">Programme guide</a>
    """ + string.Concat(Enumerable.Range(1, mountCount).Select(i => $"""
    <div class="roundbox"><div class="mounthead"><h3 class="mount">Mount Point /radio{i}</h3>
    <a href="/radio{i}.m3u">M3U</a><a href="/radio{i}.xspf">XSPF</a></div>
    <table><tr><td>Stream Name:</td><td>Unspecified name</td></tr></table></div>
    """)));
handler.Text("https://icecast.example/guide.html", """
    <a href="/stations.m3u">Open streams in media player (M3U)</a>
    <a data-mount="radio1" data-channel="Radio One" data-stream-url="/browser-stream/radio1" href="/browser-stream/radio1">Radio One</a>
    """);
handler.Text("https://icecast.example/stations.m3u", "#EXTM3U\n#EXTINF:-1,Radio One\n/radio1\n#EXTINF:-1,Radio Two\n/radio2\n", "audio/x-mpegurl");
for (int i = 1; i <= mountCount; i++)
    handler.Audio($"https://icecast.example/radio{i}", i == 2 ? "OggS    FLAC test"u8.ToArray() : mp3,
        name: "Generic fallback", type: i == 2 ? "application/ogg" : "audio/mpeg");
var directory = await engine.FindAsync(new Uri("https://icecast.example/"), CancellationToken.None);
Require(directory.Stations.Count == mountCount && !directory.LimitReached && directory.Requests == mountCount + 3,
    $"Icecast directory: {directory.Stations.Count} mounts over {directory.Requests} requests.");
Require(directory.Stations.Any(x => x.Url.AbsolutePath == "/radio1" && x.Name == "Radio One"), "Curated name over fallback metadata.");
Require(directory.Stations.Any(x => x.Name == "Radio Two" && x.Format == "Ogg FLAC"), "Ogg application MIME.");
Require(directory.Stations.Any(x => x.Url.AbsolutePath == "/radio3" && x.Name == "radio3"), "Unknown Icecast mount gets a distinct name.");
Require(!handler.Seen.Any(x => x.StartsWith("https://icecast.example/radio") && (x.EndsWith(".m3u") || x.EndsWith(".xspf"))) &&
    !handler.Seen.Any(x => x.Contains("/browser-stream/")), "Duplicated playlist wrappers or browser transcodes were fetched.");

var guide = await engine.FindAsync(new Uri("https://icecast.example/guide.html"), CancellationToken.None);
Require(guide.Stations.Count == 2 && !handler.Seen.Any(x => x.Contains("/browser-stream/")), "Opening the guide directly uses the published media-player playlist.");

handler.Text("https://proxy.example/", """
    <div class="roundbox"><div class="mounthead"><h3 class="mount">Mount Point /radio</h3>
    <a href="/radio.m3u">M3U</a><a href="/radio.xspf">XSPF</a></div>
    <table><tr><td>Stream Name:</td><td>Example Radio</td></tr></table></div>
    """);
handler.Text("https://proxy.example/radio.m3u", "http://relay.example:8000/radio\n", "audio/x-mpegurl");
handler.Audio("http://relay.example:8000/radio", mp3, "Unspecified name");
var proxy = await engine.FindAsync(new Uri("https://proxy.example/"), CancellationToken.None);
Require(proxy.Stations is [{ Name: "Example Radio", Url.Port: 8000 }] && proxy.Requests == 4,
    "Directory with unproxied mount falls back to one advertised playlist.");

handler.Text("https://radio.example/large", "<title>Many links</title>" + string.Concat(Enumerable.Range(0, 150)
    .Select(i => $"<audio src='/stream{i}.mp3'></audio>")));
var bounded = await engine.FindAsync(new Uri("https://radio.example/large"), CancellationToken.None);
Require(bounded is { Requests: 36, LimitReached: true }, "Request budget.");

using var cancellation = new CancellationTokenSource();
handler.OnRequest = () => cancellation.Cancel();
try
{
    await engine.FindAsync(new Uri("https://radio.example/"), cancellation.Token);
    throw new Exception("Caller cancellation was not propagated.");
}
catch (OperationCanceledException) { checks++; }
Console.WriteLine($"{checks} functional checks passed.");

sealed class HttpTrace : EventListener
{
    protected override void OnEventSourceCreated(EventSource source)
    {
        if (source.Name == "System.Net.Http") EnableEvents(source, EventLevel.Informational);
    }

    protected override void OnEventWritten(EventWrittenEventArgs e)
    {
        if (e.EventName == "RequestStart" && e.Payload is { Count: > 3 } data)
            Console.Error.WriteLine($"{DateTime.UtcNow:HH:mm:ss} {data[0]}://{data[1]}:{data[2]}{data[3]?.ToString()?.Split('?')[0]}");
    }
}

sealed class Fixture : HttpMessageHandler
{
    private readonly Dictionary<string, Func<HttpResponseMessage>> fixtures = new(StringComparer.Ordinal);
    public List<string> Seen { get; } = [];
    public Action? OnRequest { get; set; }
    public void Text(string uri, string text, string type = "text/html") => fixtures[uri] = () =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };
    public void Audio(string uri, byte[] bytes, string? name = null, string? br = null,
        string type = "audio/mpeg", bool finite = false) => fixtures[uri] = () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = finite ? new ByteArrayContent(bytes) : new StreamContent(new NetworkBody(bytes))
        };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (name != null) r.Headers.TryAddWithoutValidation("icy-name", name);
        if (br != null) r.Headers.TryAddWithoutValidation("icy-br", br);
        return r;
    };
    public void Redirect(string uri, string to) => fixtures[uri] = () =>
        new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(to) } };

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        OnRequest?.Invoke();
        token.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsoluteUri;
        Seen.Add(path);
        var response = fixtures.TryGetValue(path, out var create) ? create() : new(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return Task.FromResult(response);
    }

    private sealed class NetworkBody(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
