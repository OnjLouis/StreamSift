using System.Net;
using System.Net.Http.Headers;
using System.Diagnostics.Tracing;
using System.IO.Compression;
using System.Text;
using StreamSift;

if (args.Length >= 3 && args[0] == "--directory-live")
{
    using var directoryClient = PublicNetwork.CreateClient();
    var mode = new DirectoryDiscovery(directoryClient, args[1]);
    foreach (var input in args[2..])
    {
        var found = SearchSequence.CanSearchStation(input) ? await mode.FindByNameAsync(input, CancellationToken.None) :
            PublicNetwork.TryInput(input, out var website) ? await mode.FindAsync(website, CancellationToken.None) :
            throw new ArgumentException("Invalid public address or station name.");
        Console.WriteLine($"{input}: {found.Stations.Count} verified directory match(es); {found.Unreachable} unavailable.");
        foreach (var station in found.Stations.Take(6))
            Console.WriteLine($"  {station.Name}, {station.Format}, {station.FoundOn.IdnHost}");
    }
    return;
}

if (args is ["--browser-self-test"])
{
    if (!BrowserDiscovery.IsAvailable) throw new InvalidOperationException("Browser test requires Chrome or Edge.");
    const string page = "https://browser-fixture.example/";
    const string mount = "https://audio-fixture.example/ephemeral";
    const string redirect = "https://audio-fixture.example/live";
    const string htmlOnly = "https://audio-fixture.example/not-a-stream";
    var browserFixture = new Fixture();
    browserFixture.Text(page, """
        <title>Browser fixture</title>
        <button onclick="start()">Play</button>
        <script>
        fetch('http://127.0.0.1/scan').catch(() => {});
        fetch('/change',{method:'POST',body:'not allowed'}).catch(() => {});
        function start() {
            const media = new Audio('https://audio-fixture.example/ephemeral');
            media.play().catch(() => {});
            const htmlOnly = new Audio('https://audio-fixture.example/not-a-stream');
            htmlOnly.play().catch(() => {});
        }
        </script>
        """);
    browserFixture.Redirect(mount, redirect);
    var frame = new byte[900];
    foreach (var offset in new[] { 0, 417 }) new byte[] { 0xff, 0xfb, 0x90, 0x64 }.CopyTo(frame, offset);
    browserFixture.Audio(redirect, frame, "Browser fixture");
    browserFixture.Text(htmlOnly, """<audio src="https://audio-fixture.example/decoy.mp3"></audio>""");
    browserFixture.Audio("https://audio-fixture.example/decoy.mp3", frame);
    using var browserClient = new HttpClient(browserFixture);
    var browserMode = new BrowserDiscovery(browserClient);
    var found = await browserMode.FindAsync(new Uri(page), CancellationToken.None);
    if (found.Stations is not [{ Name: "Browser fixture", Format: "MP3" }] ||
        found.Stations[0].Url.AbsoluteUri != mount ||
        browserFixture.Seen.Any(url => url.Contains("127.0.0.1") || url.Contains("/change") || url.Contains("/decoy.mp3")))
        throw new InvalidOperationException("Browser guard, standalone verification or redirect preservation failed.");
    using var cancelBrowser = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try
    {
        await browserMode.FindAsync(new Uri(page), cancelBrowser.Token);
        throw new InvalidOperationException("Browser cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancelBrowser.IsCancellationRequested) { }
    Console.WriteLine("Browser discovery, private requests, write blocking, redirects and cancellation passed.");
    return;
}

if (args.Length > 0)
{
    using var trace = args[0] == "--trace" ? new HttpTrace() : null;
    using var production = PublicNetwork.CreateClient();
    foreach (var input in trace == null ? args : args[1..])
    {
        if (!PublicNetwork.TryInput(input, out var address)) throw new ArgumentException("Invalid public address.");
        var live = (await new SearchSequence(production).FindAsync(input, SearchMethod.Passive, false, CancellationToken.None)).Result;
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
static Task<IReadOnlyList<string?>> NoMirrors(CancellationToken _) => Task.FromResult<IReadOnlyList<string?>>([]);

var fallbackUri = new Uri("https://radio.example/live");
var fallbackHit = new SearchResult([new Station("Verified", fallbackUri, "MP3", 128, fallbackUri, false)], 2, 0, false);
var fallbackMiss = new SearchResult([], 3, 1, true);
foreach (var first in Enum.GetValues<SearchMethod>())
{
    var order = new List<SearchMethod>();
    var result = await SearchSequence.RunAsync(first, true, (_, _) => Task.FromResult(order.Count == 3 ? fallbackHit : fallbackMiss),
        CancellationToken.None, (method, number) => { order.Add(method); Require(number == order.Count, "Attempt numbering."); });
    Require(order.SequenceEqual(new[] { first }.Concat(Enum.GetValues<SearchMethod>().Where(method => method != first))) &&
        result.Method == order[2] && result.Attempts == 3 && result.FailedMethods == 0 &&
        result.Result.Stations.Count == 1 && result.Result.Requests == 8 && result.Result.Unreachable == 2 && result.Result.LimitReached,
        "Opt-in fallback tries both other methods in order and preserves the verified source and counts.");
}

int fallbackCalls = 0;
var manual = await SearchSequence.RunAsync(SearchMethod.Directory, false, (_, _) =>
    { fallbackCalls++; return Task.FromResult(fallbackMiss); }, CancellationToken.None);
Require(manual.Attempts == 1 && fallbackCalls == 1 && manual.Result.Stations.Count == 0, "Manual mode never falls back.");
var early = await SearchSequence.RunAsync(SearchMethod.Passive, true, (_, _) =>
    { fallbackCalls++; return Task.FromResult(fallbackHit); }, CancellationToken.None);
Require(early.Attempts == 1 && fallbackCalls == 2, "A first-method hit stops fallback.");
var recovered = await SearchSequence.RunAsync(SearchMethod.Browser, true, (mode, _) => mode switch
{
    SearchMethod.Browser => throw new InvalidOperationException("Browser not installed."),
    SearchMethod.Passive => throw new OperationCanceledException("Method deadline elapsed."),
    _ => Task.FromResult(fallbackHit)
}, CancellationToken.None);
Require(recovered.Attempts == 3 && recovered.FailedMethods == 2 && recovered.Method == SearchMethod.Directory &&
    recovered.Result.Stations.Count == 1, "Recover from unavailable browser or method timeout.");
var unavailable = await SearchSequence.RunAsync(SearchMethod.Passive, true, (_, _) => throw new HttpRequestException(), CancellationToken.None);
Require(unavailable.Result.Stations.Count == 0 && unavailable.FailedMethods == 3, "All failed methods are reported without claiming a verified result.");
using (var stop = new CancellationTokenSource())
{
    int attempts = 0;
    try
    {
        await SearchSequence.RunAsync(SearchMethod.Passive, true, (_, _) =>
            { attempts++; stop.Cancel(); return Task.FromResult(fallbackMiss); }, stop.Token);
        throw new Exception("Cancelled search returned a result.");
    }
    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    Require(attempts == 1, "Cancelling prevents every further method.");
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

handler.Text("https://www.example.fm/", """
    <a href="https://play.example.fm/radio-player/#main">Listen now</a>
    <a href="https://play.example.fm/radio-player/#rock">Listen to rock</a>
    <a href="https://play.example.fm.unrelated.example/radio-player/">Listen to an unrelated player</a>
    <a href="https://news.example.fm/radio-player/">Listen to an unrelated subdomain</a>
    <a href="https://play.example.fm/contact">Contact</a>
    """);
handler.Text("https://play.example.fm/radio-player/", """<script src="/assets/player.js?v=1"></script>""");
handler.Text("https://play.example.fm/assets/player.js?v=1",
    """window.Player = {streamUrl: "https:\/\/broadcast.example\/main.mp3"};""", "application/javascript");
handler.Audio("https://broadcast.example/main.mp3", mp3, "Main FM", "128");
var beforePlayer = handler.Seen.Count;
var separatePlayer = await engine.FindAsync(new Uri("https://www.example.fm/"), CancellationToken.None);
Require(separatePlayer.Stations is [{ Name: "Main FM", Format: "MP3", Bitrate: 128 }],
    "Player subdomain link did not discover its stream.");
Require(handler.Seen.Skip(beforePlayer).SequenceEqual(new[]
    {
        "https://www.example.fm/", "https://play.example.fm/radio-player/",
        "https://play.example.fm/assets/player.js?v=1", "https://broadcast.example/main.mp3"
    }), "Player subdomain navigation fetched a duplicate, non-player or unrelated page.");

handler.Text("https://multiplex.example/", """
    <aside id="player" class="audio-player"
        data-src="https://broadcast.example/live.m3u8, https://broadcast.example/high.aac,https://broadcast.example/main-live.mp3?regions=se,no">
    </aside>
    <div data-src="https://broadcast.example/unrelated.mp3,ffmpeg://ignored"></div>
    """);
handler.Text("https://broadcast.example/live.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nchunk.aac", "application/vnd.apple.mpegurl");
handler.Audio("https://broadcast.example/high.aac", mp3);
handler.Audio("https://broadcast.example/main-live.mp3?regions=se,no", mp3);
var multiplex = await engine.FindAsync(new Uri("https://multiplex.example/"), CancellationToken.None);
Require(multiplex.Stations.Count == 3 && multiplex.Stations.Any(s => s.IsHls) &&
    multiplex.Stations.Any(s => s.Url.Query == "?regions=se,no") && multiplex.Requests == 4,
    "Audio player source list should be split without corrupting commas in URLs.");

handler.Text("https://broadcaster.example/", """
    <a href="/listen">Listen</a><a href="/help">Help</a><a href="/contact">Contact</a>
    """ + string.Concat(Enumerable.Range(0, 45).Select(i => $"<a href='/podcasts/episode{i}.mp3'>Podcast</a>")));
handler.Text("https://broadcaster.example/listen", """
    <a href="/streaming-urls/">Live streaming URLs</a>
    """);
handler.Text("https://broadcaster.example/help", "<p>Listener help</p>");
handler.Text("https://broadcaster.example/streaming-urls/", """
    <a href="https://broadcast.example/news.aac">AAC 128 kbps</a>
    """);
handler.Audio("https://broadcast.example/news.aac", mp3, "The News");
var beforeNews = handler.Seen.Count;
var news = await engine.FindAsync(new Uri("https://broadcaster.example/"), CancellationToken.None);
Require(news.Stations is [{ Name: "The News" }] &&
    handler.Seen.Skip(beforeNews).Take(5).Any(url => url.EndsWith("/news.aac", StringComparison.Ordinal)) &&
    !handler.Seen.Skip(beforeNews).Any(url => url.EndsWith("/contact", StringComparison.Ordinal)),
    "Explicit live and listener-help links should outrank incidental on-demand audio.");

handler.Text("https://entry.example/", """
    <script src="https://unrelated.example/main.js"></script>
    <script src="/assets/vendor.js"></script>
    <script src="/assets/main.682523bb5ef4cd39.js"></script>
    """);
handler.Text("https://entry.example/assets/main.682523bb5ef4cd39.js", """
    const first = 'https://broadcast.example/entry.aac';
    """, "application/javascript");
handler.Audio("https://broadcast.example/entry.aac", mp3, "The Entry");
var beforeEntry = handler.Seen.Count;
var entryScript = await engine.FindAsync(new Uri("https://entry.example/"), CancellationToken.None);
Require(entryScript.Stations is [{ Name: "The Entry" }] &&
    handler.Seen.Skip(beforeEntry).SequenceEqual(new[] { "https://entry.example/",
        "https://entry.example/assets/main.682523bb5ef4cd39.js", "https://broadcast.example/entry.aac" }),
    "Inspect a bounded same-site application entry script, not all JavaScript.");

const string rgPage = "https://radio.garden/listen/test-station/A1b2C3_D";
const string rgAudio = "https://radio.garden/api/ara/content/listen/A1b2C3_D/channel.mp3";
handler.Text(rgPage, "<title>Radio Garden station player</title>");
handler.Redirect(rgAudio, "https://broadcast.example/garden");
handler.Audio("https://broadcast.example/garden", mp3, "Test Station");
var beforeGarden = handler.Seen.Count;
var garden = await engine.FindAsync(new Uri(rgPage), CancellationToken.None);
Require(garden.Stations is [{ Name: "Test Station", Format: "MP3" }] &&
    handler.Seen.Skip(beforeGarden).SequenceEqual(new[] { rgAudio, "https://broadcast.example/garden" }),
    "A Radio Garden station link should use its verified public playback route.");
handler.Fail(rgAudio, HttpStatusCode.Forbidden);
var beforeGardenDenied = handler.Seen.Count;
var gardenDenied = await engine.FindAsync(new Uri(rgPage), CancellationToken.None);
Require(gardenDenied.Stations.Count == 0 &&
    handler.Seen.Skip(beforeGardenDenied).SequenceEqual(new[] { rgAudio, rgPage }),
    "A denied Radio Garden stream should not use a proxy or alternate playback route.");
var beforeLookalike = handler.Seen.Count;
var lookalike = await engine.FindAsync(new Uri("https://radio.garden.other.example/listen/test-station/A1b2C3_D"), CancellationToken.None);
Require(lookalike.Stations.Count == 0 && handler.Seen.Count == beforeLookalike + 1,
    "An unrelated host cannot trigger the Radio Garden provider.");

handler.Text("https://stationhub.example/", """
    <a href="https://stream.stationhub.example/">Stream Endpoint List</a>
    <a href="https://stream.stationhub.example.unrelated.example/">Stream Endpoint List</a>
    """);
handler.Text("https://stream.stationhub.example/", """
    <a href="/1" title="Direct stream (original)">Original</a>
    <a href="/2" title="Direct stream (lossless, requires subscription)">Lossless</a>
    """);
handler.Audio("https://stream.stationhub.example/1", mp3, "Station Hub");
var beforeHub = handler.Seen.Count;
var hub = await engine.FindAsync(new Uri("https://stationhub.example/"), CancellationToken.None);
Require(hub.Stations is [{ Name: "Station Hub" }] &&
    handler.Seen.Skip(beforeHub).SequenceEqual(new[] { "https://stationhub.example/",
        "https://stream.stationhub.example/", "https://stream.stationhub.example/1" }),
    "Follow an advertised stream directory on the station's own subdomain and verify extensionless links.");

handler.Text("https://mixedsource.example/", """
    <a href="https://stream.mixedsource.example/stream">Live MP3</a>
    """ + string.Concat(Enumerable.Range(0, 45).Select(i => $"<script>let file='https://cdn.example/{i}/hls.m3u8'</script>")));
handler.Audio("https://stream.mixedsource.example/stream", mp3, "Main Radio");
var mixedSources = await engine.FindAsync(new Uri("https://mixedsource.example/"), CancellationToken.None);
Require(mixedSources.Stations is [{ Name: "Main Radio" }], "Large lists of speculative playlists must not starve direct live mounts.");

handler.Text("https://global-fixture.example/", """
    <a href="/radio/how-to-listen/getting-started/">How to listen</a>
    """);
handler.Text("https://global-fixture.example/radio/how-to-listen/getting-started/", """
    <a href="https://www.globalplayer.com/live/fixture/uk/">Listen Live</a>
    <a href="https://www.globalplayer.com/podcasts/123/">Podcasts</a>
    """);
handler.Text("https://www.globalplayer.com/live/fixture/uk/", """
    <title>Global fixture</title>
    <script type="application/json" id="__NEXT_DATA__">
    {"props":{"pageProps":{"playable":{"playback":[
        {"url":"https://restricted.example/premium.m3u8","canUse":"user.logged_in"},
        {"url":"https://open.example/fixture-live","canUse":"true"},
        {"url":"https://unrelated.example/recording.mp3"}
    ]}}}}
    </script>
    """);
handler.Audio("https://open.example/fixture-live", mp3, "Global fixture");
var beforeGlobal = handler.Seen.Count;
var global = await engine.FindAsync(new Uri("https://global-fixture.example/"), CancellationToken.None);
Require(global.Stations is [{ Name: "Global fixture" }] &&
    handler.Seen.Skip(beforeGlobal).SequenceEqual(new[] {"https://global-fixture.example/",
        "https://global-fixture.example/radio/how-to-listen/getting-started/", "https://www.globalplayer.com/live/fixture/uk/",
        "https://open.example/fixture-live"}),
    "Follow only the advertised Global Player live route and probe only its public streams.");

const string secureHome = "https://country-fixture.example/";
const string securePlayer = "https://streamdb9web.securenetsystems.net/cirruscontent/TESTFM";
const string secureStable = "https://ice42.securenetsystems.net/TESTFM";
handler.Text(secureHome, """
    <title>Country 99</title><a href="https://streamdb9web.securenetsystems.net/cirruscontent/TESTFM">Listen Live</a>
    <iframe src="https://streamdb9web.securenetsystems.net.evil.example/cirruscontent/TESTFM"></iframe>
    """);
handler.Text(securePlayer, """
    <script>
    var streamSRC = "https://ice42.securenetsystems.net/TESTFM?playSessionID=fixture";
    var streamSrcDB = 'https://ice42.securenetsystems.net/TESTFM';
    </script>
    """);
handler.Audio(secureStable, mp3, "Generic hosting service");
int beforeSecure = handler.Seen.Count;
var secure = await engine.FindAsync(new Uri(secureHome), CancellationToken.None);
Require(secure.Stations is [{ Name: "Country 99" }] && secure.Stations[0].Url.AbsoluteUri == secureStable &&
    handler.Seen.Skip(beforeSecure).SequenceEqual([secureHome, securePlayer, secureStable]),
    "Resolve an official SecureNet player to its verified stable stream, retaining the homepage station name.");

const string tritonPage = "https://player.listenlive.co/12345";
const string tritonApi = "https://playerservices.streamtheworld.com/api/livestream?version=1.9&station=TESTFM";
const string tritonRoot = "https://playerservices.streamtheworld.com/api/livestream-redirect/";
handler.Text(tritonPage, """<meta name="application-name" content="TESTFM"><title>TESTFM</title>""");
handler.Text(tritonApi, """
    <live_stream_config xmlns="http://provisioning.streamtheworld.com/player/livestream-1.9"><mountpoints>
      <mountpoint>
        <status><status-code>200</status-code></status><mount>TESTFM</mount>
        <media-format><audio codec="mp3"/></media-format><authentication>0</authentication>
        <servers><server><ip>unrelated.example</ip></server></servers>
      </mountpoint>
      <mountpoint>
        <status><status-code>200</status-code></status><mount>TESTFMAAC</mount>
        <media-format><audio codec="heaacv2"/></media-format><authentication>0</authentication>
      </mountpoint>
      <mountpoint>
        <status><status-code>200</status-code></status><mount>TESTPRIVATE</mount>
        <media-format><audio codec="mp3"/></media-format><authentication>1</authentication>
      </mountpoint>
    </mountpoints></live_stream_config>
    """, "application/xml");
handler.Redirect(tritonRoot + "TESTFM.mp3", "https://edge.live.streamtheworld.com/TESTFM.mp3");
handler.Audio("https://edge.live.streamtheworld.com/TESTFM.mp3", mp3);
handler.Fail(tritonRoot + "TESTFMAAC.aac", HttpStatusCode.Forbidden);
int beforeTriton = handler.Seen.Count;
var triton = await engine.FindAsync(new Uri(tritonPage), CancellationToken.None);
Require(triton.Stations is [{ Name: "TESTFM", Format: "MP3" }] &&
    triton.Stations[0].Url.AbsoluteUri == tritonRoot + "TESTFM.mp3" &&
    handler.Seen.Skip(beforeTriton).SequenceEqual([tritonPage, tritonApi, tritonRoot + "TESTFM.mp3",
        "https://edge.live.streamtheworld.com/TESTFM.mp3", tritonRoot + "TESTFMAAC.aac"]),
    "Only verify public advertised Triton mounts; keep the durable provider address, not a CDN edge.");

const string tuneInPage = "https://tunein.com/radio/Test-FM-s12345/";
const string tuneInApi = "https://opml.radiotime.com/Tune.ashx?id=s12345&partnerId=RadioTime&formats=mp3,aac,ogg,hls";
handler.Text(tuneInPage, """<audio src="https://unrelated.example/page-player.mp3"></audio>""");
handler.Text(tuneInApi, """
    https://stream.tunein-fixture.example/live.mp3
    http://127.0.0.1/private.mp3
    https://promo.tunein-fixture.example/other.mp3?aw_0_1st.stationId=s999
    https://stream.tunein-fixture.example/recording.mp3?aw_0_1st.stationId=s12345
    """, "audio/x-mpegurl");
handler.Audio("https://stream.tunein-fixture.example/live.mp3", mp3);
handler.Audio("https://stream.tunein-fixture.example/recording.mp3?aw_0_1st.stationId=s12345", mp3, finite: true);
int beforeTuneIn = handler.Seen.Count;
var tuneIn = await engine.FindAsync(new Uri(tuneInPage), CancellationToken.None);
Require(tuneIn.Stations is [{ Name: "Test FM", Format: "MP3" }] &&
    tuneIn.Stations[0].Url.AbsoluteUri == "https://stream.tunein-fixture.example/live.mp3" &&
    handler.Seen.Skip(beforeTuneIn).SequenceEqual([tuneInApi, "https://stream.tunein-fixture.example/live.mp3",
        "https://stream.tunein-fixture.example/recording.mp3?aw_0_1st.stationId=s12345"]),
    "Resolve a TuneIn station page through the public Tune service, verifying only this station's live public streams.");
int beforeTuneInId = handler.Seen.Count;
var tuneInId = await engine.FindAsync(new Uri("https://tunein.com/embed/player/s12345"), CancellationToken.None);
Require(tuneInId.Stations is [{ Format: "MP3" }] && handler.Seen[beforeTuneInId] == tuneInApi,
    "A TuneIn address that ends in a station id uses the same public Tune service.");
const string tuneInMissing = "https://opml.radiotime.com/Tune.ashx?id=s99&partnerId=RadioTime&formats=mp3,aac,ogg,hls";
handler.Text(tuneInMissing, """<?xml version="1.0"?><opml version="1"><head><status>404</status></head><body/></opml>""", "text/xml");
handler.Text("https://tunein.com/radio/s99/", "<title>Unknown</title>");
int beforeTuneInMissing = handler.Seen.Count;
var tuneInUnknown = await engine.FindAsync(new Uri("https://tunein.com/radio/s99/"), CancellationToken.None);
Require(tuneInUnknown.Stations.Count == 0 &&
    handler.Seen.Skip(beforeTuneInMissing).SequenceEqual([tuneInMissing, "https://tunein.com/radio/s99/"]),
    "An unknown TuneIn station lists nothing and falls back to the page itself.");
int beforeTuneInLookalike = handler.Seen.Count;
await engine.FindAsync(new Uri("https://tunein.com.unrelated.example/radio/Test-FM-s12345/"), CancellationToken.None);
Require(handler.Seen.Skip(beforeTuneInLookalike).SequenceEqual(["https://tunein.com.unrelated.example/radio/Test-FM-s12345/"]),
    "An unrelated host cannot trigger the TuneIn provider.");

const string directoryApi ="https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=70000";
const string iprdApi = "https://iprd-org.github.io/iprd/site_data/metadata/catalog.json";
const string directoryHome = "https://www.directory-fixture.example/news";
handler.Text(directoryApi, """
    [
      {"homepage":"http://directory-fixture.example/","name":"Other outlet on same site","url":"https://radio.example/other-live"},
      {"homepage":"http://directory-fixture.example/news/","name":"Named local news","url":"https://radio.example/main-mount"},
      {"homepage":"https://directory-fixture.example.evil.example/news","name":"Lookalike","url":"https://radio.example/wrong-live"},
      {"homepage":"https://directory-fixture.example/news","name":"Private","url":"http://127.0.0.1/stream"},
      {"homepage":"https://www.di.fm/trance","name":"Premium","url":"https://listen.di.fm/premium_high/trance.pls?listen_key=fixture"},
      {"homepage":"https://www.di.fm/trance","name":"Other broadcaster","url":"https://somewhere.example/trance"},
      {"homepage":"https://www.di.fm/trance","name":"Public candidate","url":"https://listen.di.fm/public3/trance.pls","url_resolved":"https://prem1.di.fm/trance?fixture"},
      {"homepage":"https://directory-fixture.example/wtest","name":"WTEST-FM Harbour","url":"https://radio.example/wtest-live"}
    ]
    """, "application/json");
handler.Redirect("https://radio.example/main-mount", "https://radio.example/current-edge");
handler.Audio("https://radio.example/current-edge", mp3);
handler.Audio("https://radio.example/other-live", mp3);
handler.Audio("https://radio.example/wtest-live", mp3);
handler.Audio("https://radio.example/wtest-extra", mp3);
handler.Text(iprdApi, """
    {"stations":[
      {"name":"WTEST","website":"https://harbour-fixture.example/","streams":[{"url":"https://radio.example/wtest-extra"}]},
      {"name":"AWTEST","website":"https://directory-fixture.example/","streams":[{"url":"https://radio.example/wrong-live"}]},
      {"name":"WTEST Plus","website":"https://harbour-fixture.example/","streams":[{"url":"https://radio.example/wtest-live"}]}
    ]}
    """, "application/json");
var directoryCache = Directory.CreateTempSubdirectory("streamsift-directory-test-");
try
{
    var lookup = new DirectoryDiscovery(fixtureClient, directoryCache.FullName) { FindMirrors = NoMirrors };
    int beforeDirectory = handler.Seen.Count;
    var listed = await lookup.FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(listed.Stations is [{ Name: "Named local news", Format: "MP3" }] &&
        listed.Stations[0].Url.AbsolutePath == "/main-mount" &&
        handler.Seen.Skip(beforeDirectory).SequenceEqual(new[] {directoryApi,
            "https://radio.example/main-mount", "https://radio.example/current-edge"}),
        "Directory mode must confirm the exact host and channel, preserve the name and retain the playable redirect.");
    int beforeCached = handler.Seen.Count;
    var cachedDirectory = await lookup.FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(cachedDirectory.Stations is [{ Name: "Named local news" }] &&
        !handler.Seen.Skip(beforeCached).Contains(directoryApi),
        "A fresh public catalogue should be reused without a network request to the directory.");
    int beforeMissing = handler.Seen.Count;
    var missing = await lookup.FindAsync(new Uri("https://directory-fixture.example/unlisted-channel"), CancellationToken.None);
    Require(missing.Stations.Count == 0 && handler.Seen.Count == beforeMissing,
        "A missing channel must not offer another outlet from the same network.");
    var network = await lookup.FindAsync(new Uri("https://directory-fixture.example/"), CancellationToken.None);
    Require(network.Stations.Select(station => station.Name).ToHashSet().SetEquals(["Other outlet on same site", "Named local news", "WTEST-FM Harbour"]),
        "A network homepage can offer multiple named outlets.");
    handler.Fail("https://listen.di.fm/public3/trance.pls", HttpStatusCode.NotFound);
    int beforeDi = handler.Seen.Count;
    var di = await lookup.FindAsync(new Uri("https://www.di.fm/trance"), CancellationToken.None);
    Require(di.Stations.Count == 0 && handler.Seen.Skip(beforeDi).SequenceEqual(["https://listen.di.fm/public3/trance.pls"]),
        "Neither subscriber entries nor unrelated directory rows count as DI.FM streams, even from cache.");
    Require(!PublicNetwork.TryUrl("https://prem2.di.fm/trance?fixture", null, out _) &&
        !PublicNetwork.TryUrl("https://listen.di.fm/trance?%6cisten_key=fixture", null, out _) &&
        PublicNetwork.TryUrl("https://elsewhere.example/live?token=public-selector", null, out _),
        "Keep known DI.FM subscriber routes out of verification without stripping public stream parameters.");
    int beforeName = handler.Seen.Count;
    var byName = await lookup.FindByNameAsync("wtest", CancellationToken.None);
    Require(byName.Stations.Select(station => station.Name).ToHashSet().SetEquals(["WTEST", "WTEST-FM Harbour"]) &&
        byName.Stations[0].FoundOn.IdnHost == "iprd-org.github.io" &&
        handler.Seen.Skip(beforeName).SequenceEqual([iprdApi, "https://radio.example/wtest-extra", "https://radio.example/wtest-live"]),
        "Search both catalogues locally, rank exact matches first, preserve the source and do not resend duplicate streams.");
    int beforeWarmName = handler.Seen.Count;
    await lookup.FindByNameAsync("WTEST", CancellationToken.None);
    Require(!handler.Seen.Skip(beforeWarmName).Contains(iprdApi) &&
        SearchSequence.CanSearchStation("104.3") && SearchSequence.CanSearchStation("BBC Radio 4") &&
        !SearchSequence.CanSearchStation("https://127.0.0.1/path") && !SearchSequence.CanSearchStation("prem1.di.fm"),
        "Reuse the secondary catalogue and distinguish station names from addresses.");

    // Files from the released version may predate the new writer-side validation.
    var oldCachePath = Path.Combine(directoryCache.FullName, "radio-browser-v1.json.gz");
    using (var file = File.Create(oldCachePath))
    using (var zip = new GZipStream(file, CompressionMode.Compress))
    using (var writer = new StreamWriter(zip))
        writer.Write("""
            [
              {"homepage":"https://www.di.fm/trance","name":"Old subscriber entry","url":"https://prem1.di.fm/trance?fixture"},
              {"homepage":"https://www.di.fm/trance","name":"Wrong broadcaster","url":"https://somewhere.example/live"},
              {"homepage":"https://www.di.fm/trance","name":"Old alternate","url":"https://listen.di.fm/public3/trance.pls","url_resolved":"https://prem2.di.fm/trance?fixture"}
            ]
            """);
    int beforeOldCache = handler.Seen.Count;
    var fromOldCache = await lookup.FindAsync(new Uri("https://www.di.fm/trance"), CancellationToken.None);
    Require(fromOldCache.Stations.Count == 0 &&
        handler.Seen.Skip(beforeOldCache).SequenceEqual(["https://listen.di.fm/public3/trance.pls"]),
        "Existing caches cannot reintroduce member feeds or mismatched station identities.");
}
finally { directoryCache.Delete(recursive: true); }

handler.Fail(directoryApi, HttpStatusCode.BadGateway);
var directoryMirror = new UriBuilder(directoryApi) { Host = "de2.api.radio-browser.info" }.Uri.AbsoluteUri;
handler.Text(directoryMirror,
    """[{"homepage":"https://www.directory-fixture.example/news","name":"Mirror station","url":"https://radio.example/main-mount"}]""",
    "application/json");
var mirrorCache = Directory.CreateTempSubdirectory("streamsift-directory-test-");
try
{
    var viaMirror = await new DirectoryDiscovery(fixtureClient, mirrorCache.FullName) { FindMirrors = NoMirrors }
        .FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(viaMirror.Stations is [{ Name: "Mirror station" }] && handler.Seen.Contains(directoryMirror),
        "Retry a failing directory server on its public mirror.");
}
finally { mirrorCache.Delete(recursive: true); }

Require(DirectoryDiscovery.MirrorOrder([], new Random(1)).SequenceEqual(["de1.api.radio-browser.info", "de2.api.radio-browser.info"]),
    "Without discovered servers, try the long-standing Radio Browser servers in order.");
var mirrorOrder = DirectoryDiscovery.MirrorOrder(["fi1.api.radio-browser.info.", "DE2.api.radio-browser.info", null, "evil.example",
    "at1.api.radio-browser.info.evil.example", "a.b.api.radio-browser.info", "api.radio-browser.info", "fi1.api.radio-browser.info"], new Random(1));
Require(mirrorOrder.Count == 3 && mirrorOrder.Take(2).ToHashSet().SetEquals(["fi1.api.radio-browser.info", "de2.api.radio-browser.info"]) &&
    mirrorOrder[2] == "de1.api.radio-browser.info",
    "Keep only discovered Radio Browser server names, without duplicates, ahead of the fallback servers.");
var discoveredApi = new UriBuilder(directoryApi) { Host = "fi1.api.radio-browser.info" }.Uri.AbsoluteUri;
handler.Text(discoveredApi,
    """[{"homepage":"https://www.directory-fixture.example/news","name":"Discovered server station","url":"https://radio.example/main-mount"}]""",
    "application/json");
var discoveredCache = Directory.CreateTempSubdirectory("streamsift-directory-test-");
try
{
    int beforeDiscovered = handler.Seen.Count;
    var viaDiscovered = await new DirectoryDiscovery(fixtureClient, discoveredCache.FullName)
        { FindMirrors = _ => Task.FromResult<IReadOnlyList<string?>>(["fi1.api.radio-browser.info"]) }
        .FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(viaDiscovered.Stations is [{ Name: "Discovered server station" }] && handler.Seen[beforeDiscovered] == discoveredApi &&
        !handler.Seen.Skip(beforeDiscovered).Contains(directoryApi),
        "Use a discovered Radio Browser server before the fixed fallback servers.");
    handler.Fail(discoveredApi, HttpStatusCode.ServiceUnavailable);
    File.Delete(Path.Combine(discoveredCache.FullName, "radio-browser-v1.json.gz"));
    int beforeDiscoveredDown = handler.Seen.Count;
    var discoveredDown = await new DirectoryDiscovery(fixtureClient, discoveredCache.FullName)
        { FindMirrors = _ => Task.FromResult<IReadOnlyList<string?>>(["fi1.api.radio-browser.info"]) }
        .FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(discoveredDown.Stations is [{ Name: "Mirror station" }] &&
        handler.Seen.Skip(beforeDiscoveredDown).Take(3).SequenceEqual([discoveredApi, directoryApi, directoryMirror]),
        "Fall back to de1 and then de2 when every discovered server fails.");
}
finally { discoveredCache.Delete(recursive: true); }

const string srPage = "https://www.sverigesradio.se/kanaler";
const string srCatalogue = "https://api.sr.se/api/v2/channels?format=json&pagination=false";
const string srP1 = "https://www.sverigesradio.se/topsy/direkt/srapi/132.mp3";
const string srP4 = "https://www.sverigesradio.se/topsy/direkt/srapi/221.mp3";
handler.Fail(srPage, HttpStatusCode.Forbidden);
handler.Text(srCatalogue, """
    {"channels":[
        {"name":"P1","channeltype":"Rikskanal","liveaudio":{"url":"https://www.sverigesradio.se/topsy/direkt/srapi/132.mp3"}},
        {"name":"P4 \u00d6rebro","channeltype":"Lokal kanal","liveaudio":{"url":"https://www.sverigesradio.se/topsy/direkt/srapi/221.mp3"}},
        {"name":"SR Extra01","channeltype":"Extrakanaler","liveaudio":{"url":"https://www.sverigesradio.se/topsy/direkt/srapi/2383.mp3"}},
        {"name":"Offline","liveaudio":null},
        {"name":"Unrelated","liveaudio":{"url":"https://www.sverigesradio.se.unrelated.example/track.mp3"}},
        {"name":"Private","liveaudio":{"url":"http://127.0.0.1/track.mp3"}}
    ]}
    """, "application/json");
handler.Redirect(srP1, "https://live1.sr.se/p1-mp3-96");
handler.Audio("https://live1.sr.se/p1-mp3-96", mp3, br: "96");
handler.Audio(srP4, mp3, br: "96");
var beforeSweden = handler.Seen.Count;
var sweden = await engine.FindAsync(new Uri(srPage), CancellationToken.None);
Require(sweden.Stations.Select(s => s.Name).SequenceEqual(new[] { "P1", "P4 \u00d6rebro" }),
    "Sveriges Radio directory should find named stations even when the website is unavailable.");
Require(handler.Seen.Skip(beforeSweden).SequenceEqual(new[] { srCatalogue, srP1, "https://live1.sr.se/p1-mp3-96", srP4 }),
    "Sveriges Radio directory requested an off-air slot, unrelated address or unnecessary web page.");
handler.Text(srCatalogue, """{"channels":[null],"invalid": """, "application/json");
handler.Text(srPage, """<audio src="https://live1.sr.se/p1-mp3-96"></audio>""");
var beforeBrokenDirectory = handler.Seen.Count;
var brokenDirectory = await engine.FindAsync(new Uri(srPage), CancellationToken.None);
Require(brokenDirectory.Stations is [{ Format: "MP3" }] &&
    handler.Seen.Skip(beforeBrokenDirectory).SequenceEqual(new[] { srCatalogue, srPage, "https://live1.sr.se/p1-mp3-96" }),
    "A malformed official directory should fall back to the original web page.");

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

const string customRoot = "https://stations.example/";
const int customCount = 288;
string Card(string path, string? title = null, string? direct = null, string? playlist = null) => $"""
    <div class="stream-container">
        <div><div class="stream-name">Mount Point {path}</div><a href="{playlist ?? path + ".m3u"}">M3U</a></div>
        <table class="stream"><tr><td>Stream Title:</td><td>{title ?? $"Named {path}"}</td></tr>
        <tr><td>Content Type:</td><td>audio/mpeg</td></tr>
        <tr><td>Stream URL:</td><td><a href="{direct ?? path}">Direct</a></td></tr></table>
    </div>
    """;
handler.Text(customRoot, string.Concat(Enumerable.Range(1, customCount).Select(i => Card($"/channel{i}"))) +
    Card("//not-the-listing.example/foreign") + Card("/admin/stats"));
for (int i = 1; i <= customCount; i++)
    handler.Audio(customRoot + $"channel{i}", mp3, name: "Generic metadata");
handler.Audio(customRoot + "channel29", mp3, finite: true);
int beforeCustom = handler.Seen.Count;
var custom = await engine.FindAsync(new Uri(customRoot), CancellationToken.None);
Require(custom.Stations.Count == customCount - 1 && custom.Requests == customCount + 1 && !custom.LimitReached &&
    custom.Stations.Any(station => station.Name == "Named /channel288") &&
    handler.Seen.Skip(beforeCustom).All(url => !url.EndsWith(".m3u") && !url.Contains("/admin") && url.StartsWith(customRoot)),
    "Recognize a large custom server listing, reject non-live entries, and skip wrappers and unrelated addresses.");
const string namesRoot = "https://named-listing.example/";
handler.Text(namesRoot, Card("/CapitalCymru", "Capital North Wales") + Card("/CapitalLondon", "Capital London") +
    Card("/CapitalNewcastle", "Capital North East"));
foreach (var mount in new[] { "CapitalCymru", "CapitalLondon", "CapitalNewcastle" })
    handler.Audio(namesRoot + mount, mp3, name: mount);
var namedListing = await engine.FindAsync(new Uri(namesRoot), CancellationToken.None);
Require(namedListing.Stations.Select(s => s.Name).SequenceEqual(["Capital London", "Capital North East", "Capital North Wales"]),
    "Server listing results sort by displayed station name, not mount order.");

const string globalTls = "https://media-ssl.musicradio.com/";
const string globalAdvertised = "https://media-ice.musicradio.com";
string GlobalCard(string path, string? title = null) =>
    Card(path, title, globalAdvertised + path, globalAdvertised + path + ".m3u");
var globalPaths = Enumerable.Range(1, 27).Select(i => $"/channel{i}").Append("/SmoothChillMP3").ToArray();
handler.Text(globalTls, string.Concat(globalPaths.Select(p => GlobalCard(p, p == "/SmoothChillMP3" ? "Smooth Chill" : null))) +
    Card("/wrong", direct: globalAdvertised + "/not-the-mount", playlist: globalAdvertised + "/wrong.m3u") +
    Card("/wrongport", direct: globalAdvertised + ":8443/wrongport", playlist: globalAdvertised + "/wrongport.m3u") +
    Card("/wrongplaylist", direct: globalAdvertised + "/wrongplaylist", playlist: "https://third-party.example/wrongplaylist.m3u"));
foreach (var mount in globalPaths)
    handler.Audio(globalTls + mount[1..], mp3);
int beforeGlobalAlias = handler.Seen.Count;
var globalListing = await engine.FindAsync(new Uri(globalTls), CancellationToken.None);
Require(globalListing.Stations.Count == globalPaths.Length && !globalListing.LimitReached &&
    globalListing.Stations.Any(s => s.Name == "Smooth Chill" && s.Url.AbsoluteUri == globalTls + "SmoothChillMP3") &&
    handler.Seen.Skip(beforeGlobalAlias).Order().SequenceEqual(globalPaths.Select(p => globalTls + p[1..]).Append(globalTls).Order()),
    "Global's HTTPS alias accepts the canonical listing links, but probes only exact mounts on the requested host.");

const string otherServer = "https://other-listing.example/";
var otherAddress = new Uri(otherServer);
var otherListing = Extraction.Read(GlobalCard("/SmoothChillMP3"), "text/html", otherAddress,
    new Link(otherAddress, otherAddress, LinkKind.Player), otherAddress, CancellationToken.None);
Require(otherListing.Server == null,
    "Global's advertised host does not relax server listing validation on unrelated websites.");

handler.Fail(customRoot + "channel1", HttpStatusCode.TooManyRequests);
var rateLimited = await engine.FindAsync(new Uri(customRoot), CancellationToken.None);
Require(rateLimited is { Requests: 5, LimitReached: true } && rateLimited.Stations.Count == 3,
    "A rate-limited server stops after the current batch and reports a partial search.");

const string jsonIce = "https://status-fixture.example:9443/";
handler.Fail(jsonIce, HttpStatusCode.NotFound, "Icecast 2.4.4");
handler.Text(jsonIce + "status-json.xsl", """
    {"icestats":{"source":[
      {"listenurl":"http://localhost:9001/farm.ogg","server_name":"Farm","title":"Current song","server_type":"application/ogg","listeners":0},
      {"listenurl":"http://status-fixture.example:9001/radio","server_name":"Local Radio","server_type":"audio/mpeg","bitrate":"128","listeners":"12"},
      {"listenurl":"https://unrelated.example/protected?key=hidden","server_type":"audio/mpeg"},
      {"listenurl":"http://localhost:9001/admin/stats","server_type":"audio/mpeg"},
      {"listenurl":"http://localhost:9001/video","server_type":"video/webm"}
    ]}}
    """, "application/json");
handler.Audio(jsonIce + "farm.ogg", "OggS    vorbis"u8.ToArray(), type: "application/ogg");
handler.Audio(jsonIce + "radio", mp3);
int beforeJsonIce = handler.Seen.Count;
var iceJson = await engine.FindAsync(new Uri(jsonIce), CancellationToken.None);
Require(iceJson.Stations is [{ Name: "Farm", Format: "Ogg Vorbis", Listeners: 0 }, { Name: "Local Radio", Bitrate: 128, Listeners: 12 }] &&
    handler.Seen.Skip(beforeJsonIce).SequenceEqual([jsonIce, jsonIce + "status-json.xsl", jsonIce + "farm.ogg", jsonIce + "radio"]),
    "Recognize Icecast even if its status homepage is missing, and use the public origin instead of a published backend.");
var singleStream = await engine.FindAsync(new Uri(jsonIce + "radio"), CancellationToken.None);
Require(singleStream is { Requests: 1 } && singleStream.Stations is [{ Listeners: null }],
    "An exact mount never enumerates its server's other streams.");

const string barePort = "port-fixture.example:8000";
const string plainPort = "http://port-fixture.example:8000/";
const string securePort = "https://port-fixture.example:8000/";
handler.NetworkFailure(securePort);
handler.Text(plainPort, """
    <title>Icecast Streaming Media Server</title>
    <div class="roundcont"><div class="newscontent">
      <div class="streamheader"><h3>Mount Point /jamulus: Unspecified name</h3><a href="/jamulus.m3u">Listen</a></div>
      <table><tr><td>Stream Title:</td><td>Unspecified name</td></tr>
      <tr><td>Content Type:</td><td>audio/mpeg</td></tr>
      <tr><td>Current Listeners:</td><td>4</td></tr><tr><td>Peak Listeners:</td><td>99</td></tr></table>
    </div></div>
    """);
handler.Audio(plainPort + "jamulus", mp3, "Jamulus fixture");
var sequence = new SearchSequence(fixtureClient);
int beforePlain = handler.Seen.Count;
var inferred = await sequence.FindAsync(barePort, SearchMethod.Passive, false, CancellationToken.None);
Require(inferred.Result.Stations is [{ Name: "Jamulus fixture", Url.Port: 8000, Listeners: 4 }] &&
    handler.Seen.Skip(beforePlain).SequenceEqual([securePort, plainPort, plainPort + "jamulus"]),
    "A bare nonstandard port can retry HTTP after TLS fails and read a legacy Icecast listing.");
int beforeExplicit = handler.Seen.Count;
var noDowngrade = await sequence.FindAsync(securePort, SearchMethod.Passive, false, CancellationToken.None);
Require(noDowngrade.Result.Stations.Count == 0 && handler.Seen.Skip(beforeExplicit).SequenceEqual([securePort]),
    "An explicitly HTTPS address must never be retried over HTTP.");
int beforeQuery = handler.Seen.Count;
handler.NetworkFailure(securePort + "?selector=fixture");
_ = await sequence.FindAsync(barePort + "?selector=fixture", SearchMethod.Passive, false, CancellationToken.None);
Require(handler.Seen.Skip(beforeQuery).SequenceEqual([securePort + "?selector=fixture"]),
    "Never send an entered query string to a lower-security scheme.");

const string shoutStatus = "https://shout-fixture.example:2530/statistics?json=1";
handler.Text(shoutStatus, """
    {"version":"2.6.1.777","streams":[
      {"streamstatus":1,"streampath":"/","servertitle":"Unique fixture","content":"audio/mpeg","bitrate":128,"currentlisteners":6,"peaklisteners":99},
      {"streamstatus":0,"streampath":"/offline","servertitle":"Off Air","content":"audio/mpeg"},
      {"streamstatus":1,"streampath":"//unrelated.example/stream","content":"audio/mpeg"}
    ]}
    """, "application/json");
handler.Audio("https://shout-fixture.example:2530/", mp3);
var shout = await engine.FindAsync(new Uri(shoutStatus), CancellationToken.None);
Require(shout.Stations is [{ Name: "Unique fixture", Bitrate: 128, Listeners: 6 }] && shout.Requests == 2,
    "SHOUTcast public status selects advertised active streams only.");

const string locusMap = "https://locusonus.org/soundmap/";
const string locusApi = "https://locusonus.org/soundmap/list/active/json/name";
handler.Text(locusApi, """
    [{"name":"Active microphone","url":"https://locus.creacast.com:9443/site.mp3","ext":"mp3"},
     {"name":"Invalid entry","url":"http://127.0.0.1/no.mp3","ext":"mp3"}]
    """, "application/json");
handler.Audio("https://locus.creacast.com:9443/site.mp3", mp3);
int beforeLocus = handler.Seen.Count;
var locus = await engine.FindAsync(new Uri(locusMap), CancellationToken.None);
Require(locus.Stations is [{ Name: "Active microphone" }] &&
    handler.Seen.Skip(beforeLocus).SequenceEqual([locusApi, "https://locus.creacast.com:9443/site.mp3"]),
    "Use the soundmap's own list, including off-site public streams, without requiring a browser.");

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
    public void Fail(string uri, HttpStatusCode status, string? server = null) => fixtures[uri] = () =>
    {
        var response = new HttpResponseMessage(status);
        if (server != null) response.Headers.TryAddWithoutValidation("Server", server);
        return response;
    };
    public void NetworkFailure(string uri) => fixtures[uri] = () =>
        throw new HttpRequestException(HttpRequestError.SecureConnectionError, "Fixture HTTPS handshake failed.");

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
