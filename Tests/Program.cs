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
    const string clip = "https://video-fixture.example/example.webm";
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
            const video = document.createElement('video');
            video.muted = true;
            video.src = 'https://video-fixture.example/example.webm';
            document.body.append(video);
            video.play().catch(() => {});
        }
        </script>
        """);
    browserFixture.Redirect(mount, redirect);
    var frame = new byte[900];
    foreach (var offset in new[] { 0, 417 }) new byte[] { 0xff, 0xfb, 0x90, 0x64 }.CopyTo(frame, offset);
    browserFixture.Audio(redirect, frame, "Browser fixture");
    browserFixture.Text(htmlOnly, """<audio src="https://audio-fixture.example/decoy.mp3"></audio>""");
    browserFixture.Audio("https://audio-fixture.example/decoy.mp3", frame);
    browserFixture.Audio(clip, [0x1a, 0x45, 0xdf, 0xa3, 0x80, 0x42, 0x86, 0x81, 1, 0, 0, 0],
        finite: true, type: "video/webm");
    using var browserClient = new HttpClient(browserFixture);
    var browserMode = new BrowserDiscovery(browserClient);
    var found = await browserMode.FindAsync(new Uri(page), CancellationToken.None);
    if (!found.EntryWasWebsite || found.Stations is not [{ Name: "Browser fixture", Format: "MP3" }, { Format: "WebM video", IsVideo: true }] ||
        found.Stations[0].Url.AbsoluteUri != mount ||
        found.Stations[1].Url.AbsoluteUri != clip ||
        browserFixture.Seen.Any(url => url.Contains("127.0.0.1") || url.Contains("/change") || url.Contains("/decoy.mp3")))
        throw new InvalidOperationException("Browser guard, standalone verification or redirect preservation failed.");
    using var cancelBrowser = new CancellationTokenSource(TimeSpan.FromSeconds(1));
    try
    {
        await browserMode.FindAsync(new Uri(page), cancelBrowser.Token);
        throw new InvalidOperationException("Browser cancellation was ignored.");
    }
    catch (OperationCanceledException) when (cancelBrowser.IsCancellationRequested) { }
    Console.WriteLine("Browser audio and video discovery, private requests, write blocking, redirects and cancellation passed.");
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

int checks = MobilePlaybackChecks.Run() + await MobilePlaybackChecks.CheckSearchHeartbeatAsync() +
    await AndroidReleaseCheckChecks.RunAsync() + await TrackDownloadChecks.RunAsync() + await AndroidMediaInputChecks.RunAsync();
void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    checks++;
}

var fallbackUri = new Uri("https://radio.example/live");
var fallbackHit = new SearchResult([new Station("Verified", fallbackUri, "MP3", 128, fallbackUri, false)], 2, 0, false);
var fallbackMiss = new SearchResult([], 3, 1, true);
foreach (var first in Enum.GetValues<SearchMethod>())
{
    var order = new List<SearchMethod>();
    var result = await SearchSequence.RunAsync(first, true, (_, _) => Task.FromResult(order.Count == 3 ? fallbackHit : fallbackMiss),
        CancellationToken.None, (method, number) => { order.Add(method); Require(number == order.Count, "Attempt numbering."); });
    SearchMethod[] expected = first switch
    {
        SearchMethod.Passive => [SearchMethod.Passive, SearchMethod.Directory, SearchMethod.Browser],
        SearchMethod.Directory => [SearchMethod.Directory, SearchMethod.Passive, SearchMethod.Browser],
        _ => [SearchMethod.Browser, SearchMethod.Passive, SearchMethod.Directory]
    };
    Require(order.SequenceEqual(expected) &&
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
var directoryHit = await SearchSequence.RunAsync(SearchMethod.Passive, true, (mode, _) => mode switch
{
    SearchMethod.Passive => Task.FromResult(fallbackMiss),
    SearchMethod.Directory => Task.FromResult(fallbackHit),
    _ => throw new Exception("Browser must not start when the directory finds a stream.")
}, CancellationToken.None);
Require(directoryHit.Method == SearchMethod.Directory && directoryHit.Attempts == 2,
    "A directory hit after website search avoids starting a browser.");
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
var titleFixture = new Fixture();
const string titleAddress = "https://metadata.example/live";
byte[] metadata = Encoding.UTF8.GetBytes("StreamTitle='Björk - Jóga';");
int metadataBlocks = (metadata.Length + 15) / 16;
var icy = new byte[mp3.Length + 1 + metadataBlocks * 16];
mp3.CopyTo(icy, 0);
icy[mp3.Length] = (byte)metadataBlocks;
metadata.CopyTo(icy, mp3.Length + 1);
titleFixture.Audio(titleAddress, icy, name: "Night radio", metaint: mp3.Length);
using (var titleClient = new HttpClient(titleFixture))
{
    titleClient.DefaultRequestHeaders.TryAddWithoutValidation("Icy-MetaData", "1");
    var titled = await new Discovery(titleClient).FindAsync(new Uri(titleAddress), CancellationToken.None);
    Require(titled.Stations is [{ Name: "Night radio", ScanTitle: "Björk - Jóga" }] && titled.Requests == 1,
        "Capture the ICY title without replacing the station name or opening another connection.");
    titleFixture.Audio(titleAddress, mp3, name: "Night radio", metaint: mp3.Length);
    var partial = await new Discovery(titleClient).FindAsync(new Uri(titleAddress), CancellationToken.None);
    Require(partial.Stations is [{ Name: "Night radio", ScanTitle: null }],
        "An absent title does not hide a verified live stream.");
}
var engine = new Discovery(fixtureClient);

const string compatibilityPage = "https://harbourwaves.example/";
const string browserMount = "https://panel.example:2000/stream/harbourwaves";
const string playerMount = browserMount + "/stream.mp3";
const string publicPlayer = "https://panel.example:2000/public/harbourwaves";
handler.Text(compatibilityPage, $"<audio src='{browserMount}'></audio>");
handler.Audio(browserMount, mp3, "Harbour Waves", "320", publicPage: publicPlayer);
handler.Audio(playerMount, mp3, "Harbour Waves", "320", publicPage: publicPlayer);
int beforeCompatibility = handler.Seen.Count;
var compatible = await engine.FindAsync(new Uri(compatibilityPage), CancellationToken.None);
Require(compatible.Stations is [{ Name: "Harbour Waves", Format: "MP3", Bitrate: 320 }] &&
    compatible.Stations[0].Url.AbsoluteUri == playerMount && compatible.Stations[0].FoundOn.AbsoluteUri == compatibilityPage &&
    handler.Seen.Skip(beforeCompatibility).SequenceEqual([compatibilityPage, browserMount, playerMount]),
    "Verify and prefer the panel's equivalent media-player URL without a duplicate row.");

foreach (var alternate in new[] { "different station", "finite file", "redirect", "unavailable" })
{
    switch (alternate)
    {
        case "different station": handler.Audio(playerMount, mp3, "Not Harbour Waves", "320", publicPage: publicPlayer); break;
        case "finite file": handler.Audio(playerMount, mp3, "Harbour Waves", "320", finite: true, publicPage: publicPlayer); break;
        case "redirect": handler.Redirect(playerMount, "https://elsewhere.example/untrusted.mp3"); break;
        default: handler.Fail(playerMount, HttpStatusCode.NotFound); break;
    }
    int before = handler.Seen.Count;
    var original = await engine.FindAsync(new Uri(compatibilityPage), CancellationToken.None);
    Require(original.Stations.Count == 1 && original.Stations[0].Url.AbsoluteUri == browserMount && original.Unreachable == 0 &&
        handler.Seen.Skip(before).SequenceEqual([compatibilityPage, browserMount, playerMount]),
        $"Retain the verified original on an optional {alternate} response without following alternate redirects.");
}
handler.Audio(browserMount, mp3, "Harbour Waves", "320", publicPage: "https://other.example/public/harbourwaves");
int beforeUnrelatedPanel = handler.Seen.Count;
var unrelatedPanel = await engine.FindAsync(new Uri(compatibilityPage), CancellationToken.None);
Require(unrelatedPanel.Stations is [{ Format: "MP3" }] && unrelatedPanel.Stations[0].Url.AbsoluteUri == browserMount &&
    handler.Seen.Skip(beforeUnrelatedPanel).SequenceEqual([compatibilityPage, browserMount]),
    "Do not guess suffixes for a stream without matching same-origin public player metadata.");
Require(SearchSequence.CanSearchStation("HarbourWaves.mp3") && !SearchSequence.CanSearchStation("https://radio.example/a.mp3"),
    "Treat a bare audio filename as a station-name search, not a hostname or a full URL.");
handler.Audio(browserMount, mp3, "Harbour Waves", "320", publicPage: publicPlayer);
handler.Audio(playerMount, mp3, "Harbour Waves", "320", publicPage: publicPlayer);
foreach (var name in new[] { "harbourwaves", "Harbour Waves", "harbourwaves.mp3" })
{
    int before = handler.Seen.Count;
    var recentMatch = await new SearchSequence(fixtureClient).FindStationAsync(name, CancellationToken.None,
        ["https://harbourwaves.test/channel", "https://harbourwaves.test/?private-selector=1", compatibilityPage]);
    Require(recentMatch.Method == SearchMethod.Passive && recentMatch.Result.Stations.Single().Url.AbsoluteUri == playerMount &&
        handler.Seen.Skip(before).SequenceEqual([compatibilityPage, browserMount, playerMount]),
        "Reuse only an exact recent website match, and verify the stream again without a directory or guessed domain.");
}
handler.Seen.Clear();
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
Require(results.Stations.Count == 7 && results.Stations.Count(row => !row.IsTrack) == 6,
    $"Expected 6 distinct live results and one track, found {results.Stations.Count} results.");
Require(results.Stations.Any(x => x.Name == "Double Tap" && x.Bitrate == 192 && x.Format == "MP3"), "Provider redirect and metadata.");
Require(results.Stations.Any(x => x.Name == "Named Station"), "PLS title.");
Require(results.Stations.Any(x => x.Name == "XML Radio"), "XSPF title.");
Require(results.Stations.Any(x => x.Format == "Ogg Opus"), "Passive script and Opus.");
Require(results.Stations.Any(x => x.IsHls && x.Url.AbsolutePath.EndsWith("/audio.m3u8")), "Audio HLS rendition.");
Require(results.Stations.Any(x => x.Url.Query == "?channel=two&region=uk" && x.Name == "Example Live"), "HTML entity handling.");
Require(!handler.Seen.Any(x => x.Contains("seg.") || x.Contains("video.m3u8") || x.Contains("irrelevant") || x.Contains("localhost") ||
    x.Contains("127.0.0.1") || x.Contains("style.css")), "Fetched a segment, video, private URL or irrelevant resource.");
Require(results.Requests == handler.Seen.Count && !results.LimitReached, "Requests were not tracked correctly.");
Require(results.Stations.Single(row => row.IsTrack).Url.AbsolutePath == "/oldshow.mp3" &&
    results.Stations.All(x => !x.Url.AbsoluteUri.Contains("vod")), "Label the embedded recording as a track and skip video-on-demand manifests.");
Require(results.Unreachable == 1, "Blocked redirect not counted.");

const string directTrackUrl = "https://station.example/oldshow.mp3";
var directTrack = await engine.FindAsync(new Uri(directTrackUrl), CancellationToken.None);
Require(directTrack.Stations is [{ Format: "MP3", IsTrack: true, IsHls: false, IsVideo: false }] &&
    directTrack.Stations[0].Url.AbsoluteUri == directTrackUrl &&
    !directTrack.EntryWasWebsite && directTrack.Requests == 1,
    "A directly entered public audio file is a playable result.");
handler.Audio("https://station.example/audio?token=test", mp3, finite: true);
handler.Redirect("https://station.example/redirected.mp3", directTrackUrl);
handler.Audio("https://station.example/fixedlength.mp3", mp3, "ICY station", finite: true);
handler.Audio("https://station.example/directlive.mp3", mp3);
handler.Fail("https://station.example/permission.mp3", HttpStatusCode.Forbidden);
foreach (var path in new[] { "audio?token=test", "redirected.mp3", "fixedlength.mp3", "permission.mp3" })
{
    var excludedTrack = await engine.FindAsync(new Uri($"https://station.example/{path}"), CancellationToken.None);
    Require(excludedTrack.Stations.Count == 0, $"Do not treat {path} as a public direct track.");
}
var directLive = await engine.FindAsync(new Uri("https://station.example/directlive.mp3"), CancellationToken.None);
Require(directLive.Stations is [{ Format: "MP3", IsTrack: false }], "Direct live MP3 playback still works.");

const string interviewPage = "https://news.example/feature";
const string interviewUrl = "https://media.example/interview.mp3?source=site";
handler.Text(interviewPage, """
    <title>Public interview</title>
    <meta property="og:type" content="article">
    <link rel="alternate" type="application/rss+xml" href="/category.rss">
    <a href="https://media.example/interview.mp3?source=site">Listen</a>
    <audio src="clip"></audio><a href="permission.mp3">Restricted recording</a>
    """);
handler.Audio(interviewUrl, mp3, finite: true);
handler.Audio("https://news.example/clip", mp3, finite: true);
handler.Fail("https://news.example/permission.mp3", HttpStatusCode.Forbidden);
handler.Text("https://news.example/category.rss",
    """<rss><channel><item><enclosure url="other.mp3"/></item></channel></rss>""", "application/rss+xml");
var articleBefore = handler.Seen.Count;
var interview = await engine.FindAsync(new Uri(interviewPage), CancellationToken.None);
Require(interview.Stations.Count == 2 && interview.Stations.All(track => track.IsTrack && track.FoundOn.AbsoluteUri == interviewPage) &&
    interview.Stations.Any(track => track.Name == "Public interview" && track.Url.AbsoluteUri == interviewUrl) &&
    !handler.Seen.Skip(articleBefore).Contains("https://news.example/category.rss"),
    "Find verified audio links and audio elements on the page before choosing an unrelated RSS feed.");

const string sonicDemoPage = "https://www.soniccouture.com/en/products/test-demo/";
const string sonicPlaylist = "https://www.soniccouture.com/mp3player/ajax.php";
const string sonicDemo = "https://www.soniccouture.com/files/demos/Folder/Vibe%20%231.mp3";
handler.Text(sonicDemoPage, """<a class="play_demos" data-id="46">Play Audio</a>""");
handler.Text(sonicPlaylist, """
    {"playlist":[{"Name":"First vibraphone demo","MP3File":"Folder/Vibe #1.mp3"},
    {"Name":"Outside the demos folder","MP3File":"../escape.mp3"}]}
    """, "application/json");
handler.Audio(sonicDemo, mp3, finite: true);
var sonicBefore = handler.Seen.Count;
var sonic = await engine.FindAsync(new Uri(sonicDemoPage), CancellationToken.None);
Require(sonic.Stations is [{ Name: "First vibraphone demo", IsTrack: true, Format: "MP3" }] &&
    handler.Seen.Skip(sonicBefore).SequenceEqual([sonicDemoPage, sonicPlaylist, sonicDemo]) &&
    handler.Forms.TryGetValue(sonicPlaylist, out var sonicForm) && sonicForm == "id=46&mode=2",
    "Follow the public Soniccouture demo button and verify only safely resolved audio paths.");

const string niDemoPage = "https://www.native-instruments.com/pages/example-demos";
const string niPlaylist = "https://www.native-instruments.com/apps/awplayer/products/12345/tracks";
const string niDemo = "https://assets.awplayer.com/publictrack";
handler.Text(niDemoPage, """<title>Audio demos</title><!--""" + new string(' ', 790000) + """
    --><main><p>[play type=&quot;audio&quot; id=&quot;12345&quot; label=&quot;Demo&quot;]</p></main>
    """);
handler.Text(niPlaylist, """
    {"tracks":[
      {"title":"Full demo","url":"https://assets.awplayer.com/publictrack","duration":36,"previewDuration":36},
      {"title":"Limited preview","url":"https://assets.awplayer.com/limited","duration":90,"previewDuration":30},
      {"title":"Wrong host","url":"https://other.example/file.mp3","duration":12,"previewDuration":12}
    ]}
    """, "application/json");
handler.Audio(niDemo, mp3, finite: true);
var niBefore = handler.Seen.Count;
var ni = await engine.FindAsync(new Uri(niDemoPage), CancellationToken.None);
Require(ni.Stations is [{ Name: "Full demo", IsTrack: true, Format: "MP3" }] &&
    handler.Seen.Skip(niBefore).SequenceEqual([niDemoPage, niPlaylist, niDemo]),
    "Verify an advertised Native Instruments demo and skip media whose public player limits playback to a shorter preview.");

const string dataPlayerPage = "https://samples.example/preview";
const string dataPlayerAudio = "https://samples.example/public-preview.mp3";
handler.Text(dataPlayerPage, """<title>Sample Preview</title><div class="player" data-mp3="/public-preview.mp3"></div>""");
handler.Audio(dataPlayerAudio, mp3, finite: true);
var dataPlayer = await engine.FindAsync(new Uri(dataPlayerPage), CancellationToken.None);
Require(dataPlayer.Stations is [{ Name: "Sample Preview", IsTrack: true, Format: "MP3" }],
    "Verify a public MP3 advertised in a custom player element.");

const string musicPreviewPage = "https://magnatune.com/artists/albums/example-album/";
const string musicPreviewApi = "https://magnatune.com/api/albums/example-album";
const string musicPreviewAudio = "https://magnatune.com/music/Artist/Album/title_spoken.mp3";
handler.Text(musicPreviewApi, """
    {"ok":true,"album":{"sku":"example-album","tracks":[
      {"name":"Example song","preview_mp3":"https://magnatune.com/music/Artist/Album/title_spoken.mp3",
       "clean_url":"https://magnatune.com/api/albums/example-album/tracks/1/file?format=wav"},
      {"name":"Clean file","preview_mp3":"https://magnatune.com/music/Artist/Album/clean.mp3"},
      {"name":"Wrong host","preview_mp3":"https://other.example/song_spoken.mp3"}]}}
    """, "application/json");
handler.Audio(musicPreviewAudio, mp3, finite: true);
var musicBefore = handler.Seen.Count;
var musicPreview = await engine.FindAsync(new Uri(musicPreviewPage), CancellationToken.None);
Require(musicPreview.EntryWasWebsite &&
    musicPreview.Stations is [{ Name: "Example song preview", IsTrack: true, Format: "MP3" }] &&
    musicPreview.Stations[0].FoundOn.AbsoluteUri == musicPreviewPage &&
    handler.Seen.Skip(musicBefore).SequenceEqual([musicPreviewApi, musicPreviewAudio]),
    "Resolve only the public tagged MP3 preview from the documented Magnatune album response.");

const string fileIndex = "https://files.example/media/";
handler.Redirect(fileIndex.TrimEnd('/'), fileIndex);
handler.Text(fileIndex, """
    <title>Index of /media</title><h1>Index of /media</h1><table>
    <tr><th>Icon</th><th><a href="?C=N;O=D">Name</a></th><th>Last modified</th><th>Size</th></tr>
    <tr><td><a href="../">Parent Directory</a></td></tr>
    <tr><td><a href="?C=N;O=D">Name</a></td></tr>
    <tr><td></td><td><a href="%23Welcome%20Home.mp3">#Welcome Home.mp3</a></td><td>2020-10-15 11:42</td><td>631K</td></tr>
    <tr><td></td><td><a href="/media/Piano.M4A">Piano.M4A</a></td><td>2019-05-02 15:20</td><td>1.5M</td></tr>
    <tr><td><a href="https://files.example/media/Field%20Notes.flac">Field Notes.flac</a></td></tr>
    <tr><td><a href="%23Welcome%20Home.mp3">Duplicate</a></td></tr>
    <tr><td><a href="/elsewhere/other.mp3">Different directory</a></td></tr>
    <tr><td><a href="https://other.example/media/other.mp3">Different origin</a></td></tr>
    <tr><td><a href="sub%2Fescaped.mp3">Encoded nested path</a></td></tr>
    <tr><td><a href="sub/">Subfolder</a></td></tr>
    <tr><td><a href="archive.zip">Archive</a></td></tr>
    <tr><td><a href="signed.mp3?key=fixture">Signed URL</a></td></tr>
    </table>
    """ + string.Concat(Enumerable.Range(1, 30).Select(i => $"<a href='track{i}.wav'>Track {i}</a>")));
var indexBefore = handler.Seen.Count;
var indexed = await engine.FindAsync(new Uri(fileIndex.TrimEnd('/')), CancellationToken.None);
Require(indexed.Stations.Count == 33 && indexed.Stations.All(row => row.IsTrack && row.FoundOn.AbsoluteUri == fileIndex) &&
    indexed.Stations.Take(3).Select(row => (row.Name, row.Format)).SequenceEqual(
        [("#Welcome Home", "MP3"), ("Piano", "M4A"), ("Field Notes", "FLAC")]) &&
    handler.Seen.Skip(indexBefore).SequenceEqual([fileIndex.TrimEnd('/'), fileIndex]) && !indexed.LimitReached,
    "List only direct child audio from a public media index, in index order, without fetching the files.");
Require(indexed.EntryWasFileIndex && indexed.EntryWasWebsite, "Remember the public index and identify its track results.");
Require(indexed.Stations[0] is { FileSizeBytes: 646144, FileSizeLabel: "631 KiB" } &&
    indexed.Stations[0].DateModified == new DateTime(2020, 10, 15, 11, 42, 0) &&
    indexed.Stations[1] is { FileSizeBytes: 1572864, FileSizeLabel: "1.5 MiB" } &&
    indexed.Stations[2] is { FileSizeBytes: null, DateModified: null },
    "Read date and size from the index columns with no file requests.");
handler.Text("https://files.example/table/", """
    <title>Index of /table</title><h1>Index of /table</h1><table>
      <thead><tr><th scope="col">Name</th><th scope="col">Size</th><th scope="col">Last modified</th></tr></thead>
      <tbody><tr><td><a href="one.wav">One</a></td><td>5.5 MiB</td><td>15 October 2019, 11:42</td></tr>
      <tr><td><a href="unknown.mp3">Unknown</a></td><td>12345678901234567890 GiB</td><td>-</td></tr></tbody>
    </table>
    """);
var namedColumns = await engine.FindAsync(new Uri("https://files.example/table/"), CancellationToken.None);
Require(namedColumns.Stations is [{ FileSizeBytes: 5767168, FileSizeLabel: "5.5 MiB" }, { FileSizeBytes: null, DateModified: null }] &&
    namedColumns.Stations[0].DateModified == new DateTime(2019, 10, 15, 11, 42, 0),
    "Column labels allow size before date. Unusable metadata leaves a playable track with unknown values.");
handler.Text("https://files.example/large/", "<title>Index of /large/</title><h1>Index of /large/</h1>" +
    string.Concat(Enumerable.Range(1, 520).Select(i => $"<a href='track{i}.flac'>Track {i}</a>")));
var cappedIndex = await engine.FindAsync(new Uri("https://files.example/large/"), CancellationToken.None);
Require(cappedIndex.Stations.Count == 512 && cappedIndex.LimitReached && cappedIndex.Requests == 1,
    "Bound large index results without probing each file.");
handler.Text("https://files.example/empty/", "<title>Index of /empty</title><h1>Index of /empty</h1><a href='archive.zip'>Archive</a>");
var emptyIndex = await new SearchSequence(fixtureClient).FindAsync("https://files.example/empty/", SearchMethod.Passive, true, CancellationToken.None);
Require(emptyIndex.Result.EntryWasFileIndex && emptyIndex.Result.Stations.Count == 0 && emptyIndex.Attempts == 1,
    "An index with no supported files reports an empty track listing without other search methods.");

const string podcastPage = "https://podcast.example/show/";
const string podcastFeed = podcastPage + "index.php?xml_feed=y";
handler.Text(podcastPage, """
    <a href="preview.mp3">Preview</a>
    <a href="index.php?xml_feed=y">Click Here To Subscribe</a>
    """);
handler.Text(podcastFeed, """
    <?xml version="1.0"?>
    <rss version="2.0"><channel>
      <item><title>Zulu episode</title><enclosure url="z.mp3" length="1024" type="audio/mpeg"/></item>
      <item><title>Alpha episode</title><enclosure url="/episodes/2" length="2048" type="audio/mp4"/></item>
      <item><title>Private</title><enclosure url="http://127.0.0.1/audio.mp3" type="audio/mpeg"/></item>
      <item><title>Duplicate</title><enclosure url="z.mp3"/></item>
      <item><title>Video</title><enclosure url="clip.mp4" type="video/mp4"/></item>
    </channel></rss>
    """, "text/xml");
var podcastBefore = handler.Seen.Count;
var podcast = await engine.FindAsync(new Uri(podcastPage), CancellationToken.None);
Require(podcast.EntryWasFileIndex && podcast.PreferListedOrder && podcast.Stations is [
        { Name: "Zulu episode", Format: "MP3", IsTrack: true, FileSizeBytes: 1024 },
        { Name: "Alpha episode", Format: "M4A", IsTrack: true, FileSizeBytes: 2048 }] &&
    handler.Seen.Skip(podcastBefore).SequenceEqual([podcastPage, podcastFeed]),
    "Discover a podcast subscription link and list its audio enclosures in feed order without fetching episodes.");
var directPodcast = await engine.FindAsync(new Uri(podcastFeed), CancellationToken.None);
Require(directPodcast.PreferListedOrder && directPodcast.Stations.Select(track => track.Url)
    .SequenceEqual(podcast.Stations.Select(track => track.Url)),
    "A direct RSS feed lists the same episode links as its podcast page.");
Require(podcast.Stations.All(track => track.FoundOn.AbsoluteUri == podcastPage &&
    StreamSift.Mobile.SessionStore.CanSave(track)),
    "Episodes discovered through a public podcast page can be restored or added to mobile Favourites.");
handler.Text(podcastPage + "atom", """
    <feed xmlns="http://www.w3.org/2005/Atom"><entry><title>Field notes</title>
    <link rel="enclosure" href="recording.opus" length="800" type="audio/ogg"/>
    </entry></feed>
    """, "application/atom+xml");
var atom = await engine.FindAsync(new Uri(podcastPage + "atom"), CancellationToken.None);
Require(atom.Stations is [{ Name: "Field notes", Format: "Ogg Opus", FileSizeBytes: 800 }],
    "Recognize Atom audio enclosures.");
handler.Text(podcastPage + "unsafe.rss", """
    <!DOCTYPE rss SYSTEM "https://podcast.example/never-fetch.dtd"><rss><channel/></rss>
    """, "application/rss+xml");
var unsafeFeed = await engine.FindAsync(new Uri(podcastPage + "unsafe.rss"), CancellationToken.None);
Require(unsafeFeed is { EntryWasFileIndex: true, ListingNotice: not null, Stations.Count: 0 } &&
    !handler.Seen.Contains(podcastPage + "never-fetch.dtd"), "Reject a feed with a DOCTYPE without loading external XML.");

const string archiveItem = "https://archive.org/details/test_audio_item";
const string archiveMetadata = "https://archive.org/metadata/test_audio_item";
handler.Text(archiveMetadata, """
    {"metadata":{"identifier":"test_audio_item"},"files":[
     {"name":"folder/a # song.mp3","title":"An audio track","size":"500","mtime":"1700000000"},
     {"name":"hidden.mp3","private":1},{"name":"../escape.mp3"},{"name":"document.pdf"}]}
    """, "application/json");
var archiveBefore = handler.Seen.Count;
var archive = await engine.FindAsync(new Uri(archiveItem), CancellationToken.None);
Require(archive.EntryWasFileIndex && archive.Stations is [{ Name: "An audio track", IsTrack: true, FileSizeBytes: 500 }] &&
    archive.Stations[0].Url.AbsoluteUri == "https://archive.org/download/test_audio_item/folder/a%20%23%20song.mp3" &&
    archive.Stations[0].DateModified == DateTimeOffset.FromUnixTimeSeconds(1700000000).UtcDateTime &&
    handler.Seen.Skip(archiveBefore).SequenceEqual([archiveMetadata]),
    "Read only public audio from Internet Archive metadata and encode each file path segment.");
handler.Text(archiveMetadata, """
    {"metadata":{"identifier":"test_audio_item","access-restricted-item":"true"},"files":[{"name":"hidden.mp3"}]}
    """, "application/json");
var restricted = await engine.FindAsync(new Uri(archiveItem), CancellationToken.None);
Require(restricted is { EntryWasFileIndex: true, Stations.Count: 0, ListingNotice: not null },
    "Report an access-restricted Archive item without listing its audio.");

const string torrentPage = "https://files.example/audio.torrent";
static string TorrentString(string value) => $"{Encoding.UTF8.GetByteCount(value)}:{value}";
const string torrentPieces = "12:piece lengthi16384e6:pieces20:aaaaaaaaaaaaaaaaaaaa";
const string torrentFiles = "5:filesld6:lengthi600e4:pathl3:sub8:A #1.mp3eed6:lengthi70e4:pathl2:..10:escape.mp3eee";
var torrentInfo = "4:infod" + torrentFiles + "4:name5:Audio" + torrentPieces + "e";
var torrentBefore = handler.Seen.Count;
handler.Audio(torrentPage, Encoding.UTF8.GetBytes("d" + torrentInfo +
    "8:url-listl" + TorrentString("http://files.example/") + TorrentString("https://files.example/download/") + "ee"),
    type: "application/octet-stream", finite: true);
var torrent = await engine.FindAsync(new Uri(torrentPage), CancellationToken.None);
Require(torrent.EntryWasFileIndex && torrent.Stations is [{ Name: "A #1", IsTrack: true, FileSizeBytes: 600 }] &&
    torrent.Stations[0].Url.AbsoluteUri == "https://files.example/download/Audio/sub/A%20%231.mp3" &&
    handler.Seen.Skip(torrentBefore).SequenceEqual([torrentPage]),
    "Resolve a multi-file torrent through its HTTPS web seed without requesting audio or accepting traversal.");
handler.Audio(torrentPage, Encoding.UTF8.GetBytes("d4:infod6:lengthi300e4:name9:track.wav" + torrentPieces + "e" +
    "8:url-list" + TorrentString("https://files.example/static-track") + "e"), type: "application/x-bittorrent", finite: true);
var singleTorrent = await engine.FindAsync(new Uri(torrentPage), CancellationToken.None);
Require(singleTorrent.Stations is [{ Name: "track", Format: "WAV", FileSizeBytes: 300 }] &&
    singleTorrent.Stations[0].Url.AbsoluteUri == "https://files.example/static-track",
    "Use the exact HTTP web seed for a single-file torrent.");
handler.Audio(torrentPage, Encoding.UTF8.GetBytes("d" + torrentInfo + "e"), type: "application/x-bittorrent", finite: true);
var peerOnly = await engine.FindAsync(new Uri(torrentPage), CancellationToken.None);
Require(peerOnly is { EntryWasFileIndex: true, Stations.Count: 0, ListingNotice: not null } &&
    peerOnly.ListingNotice.Contains("web seed"), "Explain why a peer-only torrent cannot be played.");

handler.Text("https://tv.example/", """<title>Public TV</title><a href="/watch/">Watch live</a>""");
handler.Text("https://tv.example/watch/", """
    <title>Public TV</title>
    <div data-videos='[{"video":{"meta":{"mpx_is_livestream":"1","m3u8_url":"/master.m3u8"}}},
                       {"video":{"meta":{"mpx_is_livestream":"0","m3u8_url":"/replay.m3u8"}}}]'></div>
    <video src="/private.m3u8"></video>
    <video src="/encrypted.m3u8"></video><video src="/clip.mp4"></video>
    """);
handler.Text("https://tv.example/master.m3u8",
    "#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=800000,CODECS=\"avc1.64001f,mp4a.40.2\"\n/channel.m3u8", "application/vnd.apple.mpegurl");
handler.Text("https://tv.example/channel.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nnever-fetch.ts", "application/vnd.apple.mpegurl");
handler.Text("https://tv.example/private.m3u8", "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXTINF:6,\nhttp://127.0.0.1/private.ts", "application/vnd.apple.mpegurl");
handler.Text("https://tv.example/encrypted.m3u8",
    "#EXTM3U\n#EXT-X-TARGETDURATION:6\n#EXT-X-KEY:METHOD=AES-128,URI=\"never-fetch.key\"\n#EXTINF:6,\nnever-fetch.ts", "application/vnd.apple.mpegurl");
handler.Audio("https://tv.example/clip.mp4", [0, 0, 0, 20, 102, 116, 121, 112, 105, 115, 111, 109],
    finite: true, type: "video/mp4");
var publicTv = await engine.FindAsync(new Uri("https://tv.example/"), CancellationToken.None);
Require(publicTv.Stations.Count == 2 &&
    publicTv.Stations.Any(s => s.IsVideo && s.Format == "HLS video" && s.Url.AbsolutePath == "/channel.m3u8" &&
        s.FoundOn.AbsoluteUri == "https://tv.example/watch/") &&
    publicTv.Stations.Any(s => s.Format == "MP4 video" && s.Url.AbsolutePath == "/clip.mp4"),
    "Find public video and return the checked rendition, not an unchecked master.");
var directVideo = await engine.FindAsync(new Uri("https://tv.example/clip.mp4"), CancellationToken.None);
var directPlaylist = await engine.FindAsync(new Uri("https://tv.example/master.m3u8"), CancellationToken.None);
Require(publicTv.EntryWasWebsite && !directVideo.EntryWasWebsite && !directPlaylist.EntryWasWebsite &&
    directVideo.Stations is [{ IsVideo: true }] && directPlaylist.Stations is [{ IsVideo: true }],
    "Remember the website that provided video, not a direct video URL or its master playlist.");
Require(handler.Seen.All(url => !url.Contains("never-fetch") && !url.Contains("private.ts") && !url.Contains("/replay.m3u8")),
    "Video search never fetches segment, key, private address or unrelated recorded video metadata.");

handler.Text("https://radio.example/m3u", "#EXTM3U\n#EXTINF:-1,Relative station\n/ownstream\n", "audio/x-mpegurl");
handler.Audio("https://radio.example/ownstream", mp3);
var m3u = await engine.FindAsync(new Uri("https://radio.example/m3u"), CancellationToken.None);
Require(m3u.Stations is [{ Name: "Relative station" }], "Relative playlist entry.");

handler.Text("https://player.example/public/station", """
    <title>Station player</title><div id="public-radio-player"></div>
    <script>
    window.vueComponent('#public-radio-player', {"componentProps":{
        "downloadPlaylistUri":"\/public\/station\/playlist.pls",
        "downloadPlaylistUrl":"station/listen.m3u",
        "playlistHref":"/local-only.fpl",
        "playlistArtworkUrl":"/station-artwork.png",
        "anotherUrl":"/unrelated.pls"
    }});
    </script>
    """);
handler.Text("https://player.example/public/station/playlist.pls",
    "[playlist]\nFile1=/listen/live\nTitle1=Station PLS\nLength1=-1\nNumberOfEntries=1\nVersion=2", "audio/x-scpls");
handler.Text("https://player.example/public/station/listen.m3u",
    "#EXTM3U\n#EXTINF:-1,Station M3U\n/listen/alternate", "application/octet-stream");
handler.Audio("https://player.example/listen/live", mp3);
handler.Audio("https://player.example/listen/alternate", mp3);
var beforeEmbeddedPlaylists = handler.Seen.Count;
var embeddedPlaylists = await engine.FindAsync(new Uri("https://player.example/public/station"), CancellationToken.None);
Require(embeddedPlaylists.Stations.Select(s => s.Name).Order().SequenceEqual(["Station M3U", "Station PLS"]),
    "Discover relative playlist addresses in player configuration without running the browser.");
Require(handler.Seen.Skip(beforeEmbeddedPlaylists).All(url =>
    !url.EndsWith("/local-only.fpl") && !url.EndsWith("/station-artwork.png") && !url.EndsWith("/unrelated.pls")),
    "Only recognised playlist fields with supported playlist extensions are followed.");

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

const string tunePage = "https://tunein.com/radio/Test-FM-s12345/";
const string tuneApi = "https://opml.radiotime.com/Tune.ashx?id=s12345&partnerId=RadioTime&formats=mp3,aac,ogg,hls";
const string tuneMount = "https://broadcast.example/tune-in?quality=high";
const string tuneEdge = "https://broadcast.example/tune-edge?quality=high";
handler.Text(tuneApi, """
    https://broadcast.example/tune-in?quality=high&lat=1&lon=2&ads.%73essionId=private
    https://broadcast.example/promoted?ads.stationId=s55555
    http://127.0.0.1:8000/internal
    """, "text/plain");
handler.Redirect(tuneMount, tuneEdge + "&listener_id=private");
handler.Audio(tuneEdge, mp3, "Broadcaster Test FM");
var beforeTuneIn = handler.Seen.Count;
var tuneIn = await engine.FindAsync(new Uri(tunePage + "?sessionId=private"), CancellationToken.None);
Require(tuneIn.Stations is [{ Name: "Broadcaster Test FM", Format: "MP3" }] &&
    tuneIn.Stations[0].Url.AbsoluteUri == tuneEdge && tuneIn.Stations[0].FoundOn.AbsoluteUri == tunePage &&
    handler.Seen.Skip(beforeTuneIn).SequenceEqual([tuneApi, tuneMount, tuneEdge]),
    "TuneIn verifies the actual station and strips listener data from requests, redirects, and results.");
handler.Text(tuneApi, """
    https://broadcast.example/tune-list.m3u?session-id=private
    https://broadcast.example/tune-promo
    """, "text/plain");
handler.Text("https://broadcast.example/tune-list.m3u", "#EXTM3U\n/tune-in?quality=high&latitude=0", "audio/x-mpegurl");
handler.Redirect("https://broadcast.example/tune-promo", "https://broadcast.example/promoted?ads.station_id=s55555");
beforeTuneIn = handler.Seen.Count;
tuneIn = await engine.FindAsync(new Uri(tunePage), CancellationToken.None);
Require(tuneIn.Stations is [{ Name: "Broadcaster Test FM" }] &&
    handler.Seen.Skip(beforeTuneIn).Contains("https://broadcast.example/tune-list.m3u") &&
    !handler.Seen.Skip(beforeTuneIn).Any(url => url.Contains("/promoted") || url.Contains("private") || url.Contains("latitude")),
    "TuneIn cleanup follows nested playlists and redirects without probing another station.");
foreach (var other in new[] { "https://tunein.com/show/Test-FM-s12345/",
        "https://tunein.com:8000/radio/Test-FM-s12345/", "https://tunein.com.other.example/radio/Test-FM-s12345/" })
{
    beforeTuneIn = handler.Seen.Count;
    await engine.FindAsync(new Uri(other), CancellationToken.None);
    Require(handler.Seen.Skip(beforeTuneIn).SequenceEqual([other]), "Unrelated pages do not activate the TuneIn route.");
}

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

const string directoryApi = "https://de1.api.radio-browser.info/json/stations/search?hidebroken=true&order=votes&reverse=true&limit=70000";
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
      {"homepage":"https://directory-fixture.example/wtest","name":"WTEST-FM Harbour","url":"https://radio.example/wtest-live"},
      {"homepage":"https://shared-radio.example:8124/stream","name":"Other tenant","url":"https://shared-radio.example:8124/stream"}
    ]
    """, "application/json");
handler.Redirect("https://radio.example/main-mount", "https://radio.example/current-edge");
handler.Audio("https://radio.example/current-edge", mp3);
handler.Audio("https://radio.example/other-live", mp3);
handler.Audio("https://radio.example/wtest-live", mp3);
handler.Audio("https://radio.example/wtest-extra", mp3);
handler.Audio("https://shared-radio.example:8124/stream", mp3);
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
    int mirrorLookups = 0;
    var lookup = new DirectoryDiscovery(fixtureClient, directoryCache.FullName)
    {
        FindMirrors = _ =>
        {
            mirrorLookups++;
            return Task.FromResult<IReadOnlyList<string?>>([]);
        }
    };
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
        !handler.Seen.Skip(beforeCached).Contains(directoryApi) && mirrorLookups == 1,
        "A fresh public catalogue should be reused without a network request to the directory.");
    int beforeMissing = handler.Seen.Count;
    var missing = await lookup.FindAsync(new Uri("https://directory-fixture.example/unlisted-channel"), CancellationToken.None);
    Require(missing.Stations.Count == 0 && handler.Seen.Count == beforeMissing,
        "A missing channel must not offer another outlet from the same network.");
    var network = await lookup.FindAsync(new Uri("https://directory-fixture.example/"), CancellationToken.None);
    Require(network.Stations.Select(station => station.Name).ToHashSet().SetEquals(["Other outlet on same site", "Named local news", "WTEST-FM Harbour"]),
        "A network homepage can offer multiple named outlets.");
    int beforeSharedHost = handler.Seen.Count;
    foreach (var address in new[] { "https://shared-radio.example:2000/", "https://shared-radio.example/",
        "http://shared-radio.example:8124/" })
    {
        var unrelated = await lookup.FindAsync(new Uri(address), CancellationToken.None);
        Require(unrelated.Stations.Count == 0 && handler.Seen.Count == beforeSharedHost,
            "Directory fallback must not probe stations on another service at the same hostname.");
    }
    var samePort = await lookup.FindAsync(new Uri("https://shared-radio.example:8124/"), CancellationToken.None);
    Require(samePort.Stations is [{ Name: "Other tenant" }] &&
        handler.Seen.Skip(beforeSharedHost).SequenceEqual(["https://shared-radio.example:8124/stream"]),
        "A directory entry on the exact streaming server must still be verified normally.");
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
    var byFilename = await lookup.FindByNameAsync("W TEST.mp3", CancellationToken.None);
    Require(byFilename.Stations is [{ Name: "WTEST" }], "Match compact names with an optional audio filename suffix.");

    const string xiphExport = "https://dir.xiph.org/yp.xml";
    const string xiphMount = "https://radio.example/xiph-only";
    handler.Text(xiphExport, """
        <directory>
          <entry><server_name>Xiph only radio</server_name><listen_url>https://radio.example/xiph-only</listen_url></entry>
          <entry><server_name>Xiph only radio</server_name><listen_url>http://127.0.0.1/private</listen_url></entry>
          <entry><server_name>Xiph only radio</server_name><listen_url></listen_url></entry>
          <entry><server_name>Unrelated radio</server_name><listen_url>https://radio.example/unrelated</listen_url></entry>
        </directory>
        """, "application/xml");
    handler.Audio(xiphMount, mp3);
    int beforeXiph = handler.Seen.Count;
    var xiph = await lookup.FindByNameAsync("Xiph only radio", CancellationToken.None);
    Require(xiph.Stations is [{ Name: "Xiph only radio", IsTrack: false }] &&
        xiph.Stations[0].FoundOn.AbsoluteUri == "https://dir.xiph.org/" &&
        handler.Seen.Skip(beforeXiph).SequenceEqual([xiphExport, xiphMount]),
        "Use the public Icecast catalogue only after the other catalogues miss, verifying only safe matching streams.");
    int beforeWarmXiph = handler.Seen.Count;
    var warmXiph = await lookup.FindByNameAsync("xiph only radio", CancellationToken.None);
    Require(warmXiph.Stations.Count == 1 && handler.Seen.Skip(beforeWarmXiph).SequenceEqual([xiphMount]),
        "Reuse the public Icecast catalogue cache and keep station queries local.");
    var xiphCache = Path.Combine(directoryCache.FullName, "xiph-v1.json.gz");
    var previousXiph = File.ReadAllBytes(xiphCache);
    File.SetLastWriteTimeUtc(xiphCache, DateTime.UtcNow.AddDays(-2));
    handler.Text(xiphExport, """
        <!DOCTYPE directory SYSTEM "https://dir.xiph.org/never-fetch.dtd"><directory/>
        """, "application/xml");
    int beforeInvalidXiph = handler.Seen.Count;
    var invalidXiph = await lookup.FindByNameAsync("Xiph only radio", CancellationToken.None);
    Require(invalidXiph.Stations.Count == 0 && invalidXiph.Unreachable == 1 &&
        handler.Seen.Skip(beforeInvalidXiph).SequenceEqual([xiphExport]) &&
        File.ReadAllBytes(xiphCache).SequenceEqual(previousXiph),
        "Reject external XML declarations and preserve the last valid public catalogue.");

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

const string insecureMount = "http://radio.example/secure-mount";
const string secureMount = "https://radio.example/secure-mount";
const string insecureOnlyMount = "http://radio.example/without-tls";
handler.Audio(insecureMount, mp3, name: "HTTPS fixture");
handler.Audio(secureMount, mp3, name: "HTTPS fixture");
handler.Audio(insecureOnlyMount, mp3, name: "HTTP fixture");
handler.Redirect("https://radio.example/without-tls", insecureOnlyMount);
var publicDirectory = new Uri("https://directory.example");
Link[] schemes = [new(new Uri(insecureMount), publicDirectory, LinkKind.Audio),
    new(new Uri(insecureOnlyMount), publicDirectory, LinkKind.Audio)];
var schemeDiscovery = new Discovery(fixtureClient);
var desktopSchemes = await schemeDiscovery.VerifyListedAsync(publicDirectory, schemes, CancellationToken.None);
var mobileSchemes = await schemeDiscovery.VerifyListedAsync(publicDirectory, schemes, CancellationToken.None, preferHttpsPlayback: true);
Require(desktopSchemes.Stations.All(row => row.Url.Scheme == "http") &&
    mobileSchemes.Stations.Select(row => row.Url.AbsoluteUri).SequenceEqual([secureMount, insecureOnlyMount]) &&
    mobileSchemes.Requests == desktopSchemes.Requests + 2,
    "Mobile station lookup verifies a public TLS counterpart and keeps HTTP-only results identifiable without accepting a downgrade.");

handler.Fail(directoryApi, HttpStatusCode.BadGateway);
var directoryMirror = new UriBuilder(directoryApi) { Host = "de2.api.radio-browser.info" }.Uri.AbsoluteUri;
var discoveredMirror = new UriBuilder(directoryApi) { Host = "test1.api.radio-browser.info" }.Uri.AbsoluteUri;
handler.Fail(discoveredMirror, HttpStatusCode.TooManyRequests);
handler.Text(directoryMirror,
    """[{"homepage":"https://www.directory-fixture.example/news","name":"Mirror station","url":"https://radio.example/main-mount"}]""",
    "application/json");
var mirrorCache = Directory.CreateTempSubdirectory("streamsift-directory-test-");
try
{
    var lookup = new DirectoryDiscovery(fixtureClient, mirrorCache.FullName)
    {
        FindMirrors = _ => Task.FromResult<IReadOnlyList<string?>>(["TEST1.api.radio-browser.info.", "test1.api.radio-browser.info",
            "de2.api.radio-browser.info", "hostile.example", "a.b.api.radio-browser.info", "-invalid.api.radio-browser.info"])
    };
    var beforeMirror = handler.Seen.Count;
    var viaMirror = await lookup.FindAsync(new Uri(directoryHome), CancellationToken.None);
    Require(viaMirror.Stations is [{ Name: "Mirror station" }] &&
        handler.Seen.Skip(beforeMirror).Take(3).SequenceEqual([discoveredMirror, directoryApi, directoryMirror]),
        "Try an approved discovered mirror once, then ordered fallbacks if it is rate-limited.");
}
finally { mirrorCache.Delete(recursive: true); }

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
    public Dictionary<string, string> Forms { get; } = [];
    public Action? OnRequest { get; set; }
    public void Text(string uri, string text, string type = "text/html") => fixtures[uri] = () =>
        new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, type) };
    public void Audio(string uri, byte[] bytes, string? name = null, string? br = null,
        string type = "audio/mpeg", bool finite = false, string? publicPage = null, int? metaint = null) => fixtures[uri] = () =>
    {
        var r = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = finite ? new ByteArrayContent(bytes) : new StreamContent(new NetworkBody(bytes))
        };
        r.Content.Headers.ContentType = new MediaTypeHeaderValue(type);
        if (name != null) r.Headers.TryAddWithoutValidation("icy-name", name);
        if (br != null) r.Headers.TryAddWithoutValidation("icy-br", br);
        if (publicPage != null) r.Headers.TryAddWithoutValidation("icy-url", publicPage);
        if (metaint.HasValue) r.Headers.TryAddWithoutValidation("icy-metaint", metaint.Value.ToString());
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

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        OnRequest?.Invoke();
        token.ThrowIfCancellationRequested();
        var path = request.RequestUri!.AbsoluteUri;
        Seen.Add(path);
        if (request.Method == HttpMethod.Post && request.Content != null)
            Forms[path] = await request.Content.ReadAsStringAsync(token);
        var response = fixtures.TryGetValue(path, out var create) ? create() : new(HttpStatusCode.NotFound);
        response.RequestMessage = request;
        return response;
    }

    private sealed class NetworkBody(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
