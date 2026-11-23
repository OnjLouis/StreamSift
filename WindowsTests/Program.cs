using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Onj.SignedUpdater;
using StreamSift;

if (args is not [var directory] || Directory.Exists(directory))
    throw new ArgumentException("Pass a new disposable test directory.");
Directory.CreateDirectory(directory);
int count = 0;
void Check(bool valid, string label)
{
    if (!valid) throw new InvalidOperationException(label);
    count++;
}
void Reject(Action test, string label)
{
    try { test(); }
    catch (Exception e) when (e is IOException or InvalidDataException or CryptographicException)
    {
        count++;
        return;
    }
    throw new InvalidOperationException(label);
}

foreach (var resource in new[] { SearchSounds.SearchResource, SearchSounds.DoneResource, SearchSounds.FailureResource })
{
    using var sound = typeof(SearchSounds).Assembly.GetManifestResourceStream(resource);
    Check(sound != null && sound.Length is > 16000 and < 28000, $"Search cue embedded: {resource}");
    var header = new byte[12];
    sound!.ReadExactly(header);
    Check(header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8), "WAV format.");
}
Check(SearchSounds.CompletionResource(0) == SearchSounds.FailureResource &&
      SearchSounds.CompletionResource(1) == SearchSounds.DoneResource,
    "Empty and failed searches use a different sound from successful searches.");
using (var failureCue = typeof(SearchSounds).Assembly.GetManifestResourceStream(SearchSounds.FailureResource)!)
    Check(Convert.ToHexString(SHA256.HashData(failureCue)) ==
        "50CC0C1F6E69064155691E6F58E2B2131568389BCB2C3BFC2FA57D71A14A9F83", "Original failure WAV is unmodified.");

var prefsFolder = Path.Combine(directory, "prefs");
Directory.CreateDirectory(prefsFolder);
Check(Preferences.Load(prefsFolder) is { SearchSounds: true, CheckUpdatesAtStartup: true, PlaylistFolder: null,
        AutomaticFallback: false, MediaPlayer: PlayerChoice.Automatic } defaults &&
    defaults.ExportFolder == Preferences.DefaultPlaylistFolder, "New install defaults.");
File.WriteAllText(Path.Combine(prefsFolder, "SearchSounds.off"), "");
Check(!Preferences.Load(prefsFolder).SearchSounds, "Prior muted build stays muted.");
var exportFolder = Path.Combine(directory, "my playlists");
File.WriteAllText(Path.Combine(prefsFolder, "Preferences.json"), """{"SearchSounds":true,"CheckUpdatesAtStartup":true}""");
Check(Preferences.Load(prefsFolder) is { SearchSounds: true, CheckUpdatesAtStartup: true, AutomaticFallback: false,
        MediaPlayer: PlayerChoice.Automatic }, "Older preferences retain Automatic playback and do not opt in to automatic search fallback.");
string prefsFile = Path.Combine(prefsFolder, "Preferences.json");
File.WriteAllText(prefsFile, """{"SearchSounds":false,"CheckUpdatesAtStartup":false,"MediaPlayer":2}""");
Check(!Preferences.Load(prefsFolder).CheckUpdatesAtStartup, "An explicit startup update opt-out stays off.");
Preferences.Load(prefsFolder).Save(prefsFolder);
using (var saved = JsonDocument.Parse(File.ReadAllBytes(prefsFile)))
    Check(saved.RootElement.TryGetProperty("MediaPlayer", out var selectedPlayer) && selectedPlayer.GetInt32() == 2,
        "A specific media player survives reopening and saving Preferences.");
File.WriteAllText(prefsFile, """{"SearchSounds":false,"AutomaticFallback":true,"MediaPlayer":99}""");
Check(Preferences.Load(prefsFolder) is { SearchSounds: false, AutomaticFallback: true, MediaPlayer: PlayerChoice.Automatic },
    "An unknown player choice does not reset other preferences.");
Check(Preferences.Load(prefsFolder).CheckUpdatesAtStartup, "Use the default when there is no stored update setting.");
var settings = new Preferences(true, true, exportFolder, AutomaticFallback: true, MediaPlayer: PlayerChoice.Vlc);
settings.Save(prefsFolder);
Check(Preferences.Load(prefsFolder) == settings && !Directory.Exists(exportFolder), "Saved preferences override the old marker without creating the export folder.");
(settings with { SearchSounds = false }).Save(prefsFolder);
Check(Preferences.Load(prefsFolder) == (settings with { SearchSounds = false }) &&
    !Directory.EnumerateFiles(prefsFolder, "*.tmp").Any(), "Settings can be replaced without keeping temporary files.");
foreach (var invalid in new[] { @"relative\files", @"\\.\C:\files" })
{
    try
    {
        Preferences.NormalizeFolder(invalid);
        throw new InvalidOperationException("An unsafe export folder was accepted.");
    }
    catch (ArgumentException) { count++; }
}
Check(Preferences.NormalizeFolder(" ") == null, "Blank playlist folder restores the default.");

var historyFolder = Path.Combine(directory, "history");
var history = SearchHistory.Load(historyFolder);
Check(history.Length == 0 && !Directory.Exists(historyFolder), "History starts empty without creating a data folder.");
Check(ReferenceEquals(history, SearchHistory.Remember(history, "no-stream.example", 0)), "Only successful searches are saved.");
foreach (var input in new[] { "https://radio.example/live?token=private", "radio.example/live#private",
    "https://user:password@radio.example/live", "https://radio.example/" + new string('a', 1024), "file:///private" })
    Check(ReferenceEquals(history, SearchHistory.Remember(history, input, 1)), "Private and unsuitable inputs are not retained.");
for (int i = 0; i <= SearchHistory.Capacity; i++)
    history = SearchHistory.Remember(history, $"radio.example/{i}", 1);
Check(history.Length == SearchHistory.Capacity && history[0] == "radio.example/10" && history[^1] == "radio.example/1",
    "Newest search goes first and the oldest drops off the end.");
history = SearchHistory.Remember(history, " RADIO.EXAMPLE/5 ", 2);
Check(history[0] == "RADIO.EXAMPLE/5" && history.Length == SearchHistory.Capacity && !history.Contains("radio.example/5"),
    "Repeating an address moves it to the front without duplicating the host.");
Check(SearchHistory.Remember(["radio.example/A"], "radio.example/a", 1).Length == 2 &&
    SearchHistory.Remember(["radio.example:8000"], "https://radio.example:8000", 1).Length == 2 &&
    SearchHistory.Remember(["double tap"], "Double Tap", 1) is ["Double Tap"],
    "Path case and explicit schemes are preserved; station names deduplicate.");
SearchHistory.Save(historyFolder, history);
Check(SearchHistory.Load(historyFolder).SequenceEqual(history) && !Directory.EnumerateFiles(historyFolder, "*.tmp").Any(),
    "Recent searches persist in order without leftover staging.");
File.WriteAllText(Path.Combine(historyFolder, "SearchHistory.json"), "[");
Check(SearchHistory.Load(historyFolder).Length == 0, "Unreadable history does not stop the app.");
SearchHistory.Save(historyFolder, []);
Check(!Directory.EnumerateFiles(historyFolder).Any(), "Clearing history deletes only its file.");

var audio = new Uri("https://radio.example/stream");
var station = new Station("Lyd \u00e9 FM", audio, "MP3", 320, audio, false);
var quiet = station with { Name = "Able", Format = "AAC", Bitrate = 48, Listeners = 0, Url = new Uri("https://radio.example/quiet") };
var unknownRate = station with { Name = "Local", Bitrate = null };
Station[] mixed = [unknownRate, station, quiet];
Check(ResultView.Formats(mixed).SequenceEqual(["AAC", "MP3"]) &&
    ResultView.Formats([quiet]).SequenceEqual(["AAC"]), "Format choices come only from the current search.");
Check(new ResultView("AAC").Apply(mixed).SequenceEqual([quiet]) &&
    new ResultView("FLAC").Apply(mixed).Length == 0 &&
    default(ResultView).Apply(mixed).SequenceEqual(mixed), "Filtering is non-destructive and the default restores the search order.");
Check(new ResultView(Order: ResultOrder.BitrateDescending).Apply(mixed).SequenceEqual([station, quiet, unknownRate]) &&
    new ResultView(Order: ResultOrder.BitrateAscending).Apply(mixed).SequenceEqual([quiet, station, unknownRate]) &&
    new ResultView(Order: ResultOrder.NameAscending).Apply(mixed).SequenceEqual([quiet, unknownRate, station]),
    "Sort by name and bitrate, with unknown bitrates consistently last.");
Check(ResultView.Find(mixed, -1, "LYD E", false) == 1 && ResultView.Find(mixed, 1, "mp3", false) == 0 &&
    ResultView.Find(mixed, 0, "mp3", true) == 1 && ResultView.Find(mixed, -1, "48 kbps", false) == 2 &&
    ResultView.Find([quiet], -1, "mp3", false) == -1 && ResultView.Find([quiet], -1, "/quiet", false) == 0,
    "Find wraps in both directions and matches only visible names, codecs, bitrate and addresses.");
Station[] counted = [station, quiet, unknownRate with { Listeners = 12 }];
Check(new ResultView(Order: ResultOrder.ListenersDescending).Apply(counted).Select(s => s.Listeners).SequenceEqual([12, 0, null]) &&
    new ResultView(Order: ResultOrder.ListenersAscending).Apply(counted).Select(s => s.Listeners).SequenceEqual([0, 12, null]) &&
    ResultView.Find(counted, -1, "0 listeners", false) == 1, "Reported zero is distinct from an unknown listener count.");
var refreshView = new ResultView("AAC", ResultOrder.ListenersDescending);
Check(refreshView.RetainAvailable(counted) == refreshView &&
    refreshView.RetainAvailable([station]) == default &&
    new ResultView("AAC", ResultOrder.BitrateDescending).RetainAvailable([station]) == new ResultView(Order: ResultOrder.BitrateDescending),
    "Refresh retains available filter and sort choices, without hiding replacement formats.");
var sessionFolder = Path.Combine(directory, "last-results");
Check(LastResults.Load(sessionFolder) == null && !Directory.Exists(sessionFolder), "First launch does not create a results snapshot.");
var signedStream = station with { Url = new Uri("https://radio.example/live?token=fixture") };
var hlsStream = station with { Url = new Uri("https://radio.example/opaque-session/playlist.m3u8"), Format = "HLS", IsHls = true };
var videoStream = station with { Url = new Uri("https://radio.example/opaque-session/video.mp4"), Format = "MP4 video", IsVideo = true };
var unsafeSource = station with { FoundOn = new Uri("https://radio.example/list?token=fixture") };
var unsafeAddress = station with { Url = new Uri("http://127.0.0.1/stream") };
var snapshot = LastResults.Capture("radio.example:8000", SearchMethod.Passive,
    [signedStream, hlsStream, videoStream, unsafeSource, unsafeAddress, station, quiet, quiet], refreshView, quiet.Url)!;
Check(snapshot is not null && snapshot.Stations.SequenceEqual([station, quiet]), "Keep reusable streams only; filter temporary and local URLs.");
snapshot!.Save(sessionFolder);
Check(LastResults.Load(sessionFolder) is { } restored && restored.Input == "radio.example:8000" &&
    restored.Method == SearchMethod.Passive && restored.View == refreshView &&
    restored.Selection == quiet.Url && restored.View.Apply(restored.Stations).SequenceEqual([quiet]),
    "Reopen the stored query, method, order, format and selected row.");
Check(LastResults.Capture("https://radio.example/?token=fixture", SearchMethod.Passive, [station], default, null) == null &&
    LastResults.Capture("radio.example", SearchMethod.Passive, [], default, null) == null &&
    LastResults.Capture("radio.example", SearchMethod.Passive, [hlsStream], default, null) == null &&
    LastResults.Load(sessionFolder) != null, "Unsafe and empty scans cannot replace the saved results.");
var sessionPath = Path.Combine(sessionFolder, "LastResults.json");
Check(!File.ReadAllText(sessionPath).Contains("token=fixture") && !File.ReadAllText(sessionPath).Contains("opaque-session"),
    "Sensitive addresses are absent from stored bytes.");
LastResults.Clear(sessionFolder);
Check(LastResults.Load(sessionFolder) == null && !Directory.EnumerateFiles(sessionFolder).Any(), "New Search forgets saved results.");
File.WriteAllText(sessionPath, "{broken");
Check(LastResults.Load(sessionFolder) == null, "Malformed state does not prevent a clean launch.");
var playlist = PlaylistFile.SaveNew(exportFolder, [station]);
var secondPlaylist = PlaylistFile.SaveNew(exportFolder, [station]);
var expectedPlaylist = "#EXTM3U\r\n#EXTINF:-1,Lyd \u00e9 FM\r\nhttps://radio.example/stream\r\n";
Check(Path.GetFileName(playlist) == "Lyd \u00e9 FM.m3u8" && Path.GetFileName(secondPlaylist) == "Lyd \u00e9 FM (2).m3u8" &&
    File.ReadAllBytes(playlist).SequenceEqual(Encoding.UTF8.GetBytes(expectedPlaylist)), "Default-player exports are UTF-8 and never overwrite.");
Check(PlaylistFile.SuggestedName("CON") == "_CON" &&
    PlaylistFile.Text([station with { Name = "Name\r\n#EXTINF" }]).Split("\r\n").Length == 4, "Reserved names and extra playlist lines are blocked.");
var invalidStation = station with { Url = new Uri("file:///C:/not-a-stream") };
Reject(() => PlaylistFile.Write(playlist, [station, invalidStation], replace: true), "A local path was accepted as a stream.");
Check(File.ReadAllText(playlist) == expectedPlaylist, "A rejected batch does not truncate an existing playlist.");
Reject(() => PlaylistFile.Text([]), "An empty selection was accepted.");

var batch = Enumerable.Range(1, 288).Select(i => station with {
    Name = $"Station {i}", Url = new Uri($"https://radio.example/{i}")
}).ToArray();
var batchFolder = Path.Combine(directory, "batch");
var batchFile = PlaylistFile.SaveNew(batchFolder, batch);
var batchLines = File.ReadAllLines(batchFile);
Check(Directory.GetFiles(batchFolder) is [var only] && only == batchFile &&
    Path.GetFileName(batchFile) == "Streams.m3u8" && batchLines[0] == "#EXTM3U" &&
    batchLines.Skip(1).SequenceEqual(batch.SelectMany(s => new[] { "#EXTINF:-1," + s.Name, s.Url.AbsoluteUri })),
    "One playlist exports all 288 selected streams in order.");

var incoming = Path.Combine(directory, "incoming");
var handoff = FoobarHandoff.Stage(incoming, [station]);
var freshHandoff = FoobarHandoff.Stage(incoming, batch);
Check(File.ReadAllLines(freshHandoff).SequenceEqual(batchLines), "Foobar receives the full selected set in one playlist.");
var command = FoobarHandoff.Command(@"C:\Program Files\foobar2000\foobar2000.exe", handoff);
var playerRoot = Path.Combine(directory, "players");
var vlcPath = Path.Combine(playerRoot, "VideoLAN", "VLC", "vlc.exe");
var foobarPath = Path.Combine(playerRoot, "foobar2000", "foobar2000.exe");
var fastPlayPath = Path.Combine(playerRoot, "FastPlay", "FastPlay.exe");
var portableFoobar = Path.Combine(directory, "portable", "foobar2000.exe");
var otherPlayer = Path.Combine(playerRoot, "wmplayer.exe");
foreach (var exe in new[] { vlcPath, foobarPath, fastPlayPath, portableFoobar, otherPlayer })
{
    Directory.CreateDirectory(Path.GetDirectoryName(exe)!);
    File.WriteAllText(exe, "");
}
Check(StreamPreview.ResolvePlayer(PlayerChoice.Automatic, portableFoobar, [playerRoot]) is { Executable: var preferred } &&
    preferred == portableFoobar, "Automatic prefers a supported default, including portable players.");
Check(StreamPreview.ResolvePlayer(PlayerChoice.Automatic, otherPlayer, [playerRoot]) is { ShellPlaylist: true, NeedsPlaylist: true },
    "Automatic uses the registered default playlist app even if it does not accept direct URL arguments.");
Check(StreamPreview.ResolvePlayer(PlayerChoice.Vlc, portableFoobar, [playerRoot]) is { Executable: var chosenVlc } && chosenVlc == vlcPath,
    "Explicit VLC ignores the Foobar2000 association.");
Check(StreamPreview.ResolvePlayer(PlayerChoice.Foobar2000, vlcPath, [playerRoot]) is { Executable: var chosenFoobar } &&
    chosenFoobar == foobarPath, "Explicit Foobar2000 ignores the VLC association.");
Check(StreamPreview.ResolvePlayer(PlayerChoice.Foobar2000, portableFoobar, [])?.Executable == portableFoobar,
    "Explicit Foobar2000 can use a matching portable default.");
Check(StreamPreview.ResolvePlayer(PlayerChoice.Automatic, fastPlayPath, [playerRoot])?.Kind == PlayerChoice.FastPlay &&
    StreamPreview.ResolvePlayer(PlayerChoice.FastPlay, portableFoobar, [playerRoot])?.Executable == fastPlayPath,
    "Automatic honors a FastPlay association and an explicit choice uses its installed copy.");
var fastPlay = new StreamPreview.Player(fastPlayPath, PlayerChoice.FastPlay);
Reject(() => StreamPreview.Command(fastPlay, station), "FastPlay was given a raw URL.");
Check(StreamPreview.Command(fastPlay, station, handoff).ArgumentList.SequenceEqual([handoff]),
    "FastPlay opens only the one-stream temporary playlist.");
File.Delete(vlcPath);
Check(StreamPreview.ResolvePlayer(PlayerChoice.Vlc, portableFoobar, [playerRoot])?.Executable == portableFoobar &&
    StreamPreview.ResolvePlayer(PlayerChoice.Automatic, vlcPath, [playerRoot])?.Executable == foobarPath,
    "A missing explicit player can use the registered default.");
var shellPlayer = StreamPreview.ResolvePlayer(PlayerChoice.Automatic, otherPlayer, [])!;
Reject(() => StreamPreview.Command(shellPlayer, videoStream), "The system default was given a raw URL.");
var shellCommand = StreamPreview.Command(shellPlayer, videoStream, handoff);
Check(shellCommand.UseShellExecute && shellCommand.FileName == handoff && shellCommand.ArgumentList.Count == 0,
    "The default player receives only a temporary playlist, not an exported file or a browser URL.");
var preview = StreamPreview.Command(new StreamPreview.Player(vlcPath, PlayerChoice.Vlc), station);
Check(!preview.UseShellExecute && preview.ArgumentList.SequenceEqual([station.Url.AbsoluteUri]),
    "Preview sends exactly the raw stream argument to the player without creating a playlist.");
var directFoobar = StreamPreview.Command(new StreamPreview.Player(foobarPath, PlayerChoice.Foobar2000), station);
Check(directFoobar.ArgumentList.SequenceEqual(["/immediate", station.Url.AbsoluteUri]),
    "Foobar preview uses regular open, without an add or play-existing command.");
Check(StreamPreview.Command(new StreamPreview.Player(foobarPath, PlayerChoice.Foobar2000), videoStream)
    .ArgumentList.SequenceEqual(["/immediate", videoStream.Url.AbsoluteUri]), "Video can also go directly to Foobar2000.");
Check(command.ArgumentList.SequenceEqual(new[] { "/add", handoff }) && !command.UseShellExecute &&
    File.ReadAllText(handoff) == expectedPlaylist, "Foobar2000 receives a local one-stream playlist without playback or playlist selection switches.");
var unrelatedPlaylist = Path.Combine(incoming, "handoff-important.m3u8");
File.Copy(handoff, unrelatedPlaylist);
var now = DateTime.UtcNow;
File.SetLastWriteTimeUtc(handoff, now.AddMinutes(-3));
File.SetLastWriteTimeUtc(unrelatedPlaylist, now.AddMinutes(-3));
Check(FoobarHandoff.Prune(incoming, now) == 1 && !File.Exists(handoff) &&
    File.Exists(freshHandoff) && File.Exists(unrelatedPlaylist), "Cleanup removes only expired owned handoffs.");

using var rsa = RSA.Create(3072);
using var unrelated = RSA.Create(3072);
string key = rsa.ToXmlString(false);
var policy = AppUpdate.Policy(key);
Reject(() => AppUpdate.Policy(rsa.ToXmlString(true)), "A private key was accepted as a verification resource.");
Check(AppUpdate.IsTrustedUrl(new Uri("https://api.github.com/repos/OnjLouis/StreamSift/releases?per_page=100&page=1")), "Trusted catalog.");
Check(AppUpdate.IsTrustedUrl(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?signature=abc")), "Trusted GitHub CDN.");
foreach (var address in new[] { "https://github.com/elsewhere/StreamSift/releases/download/v1/file.zip",
    "https://github.com.evil.example/OnjLouis/StreamSift/releases/download/v1/file.zip",
    "http://github.com/OnjLouis/StreamSift/releases/download/v1/file.zip",
    "https://api.github.com/repos/OnjLouis/StreamSift/contents/",
    "https://user@github.com/OnjLouis/StreamSift/releases/download/v1/file.zip" })
    Check(!AppUpdate.IsTrustedUrl(new Uri(address)), "Untrusted update URL.");

var current = AppUpdate.Current;
var next = new Version(current.Major, current.Minor, current.Build + 1);
string tag = "v" + next;
string asset = AppUpdate.AssetTemplate.Replace("{version}", next.ToString());
string zip = Path.Combine(directory, asset);
using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    foreach (var file in AppUpdate.ManagedFiles)
    {
        using var writer = new StreamWriter(archive.CreateEntry(file).Open());
        writer.Write("New version " + file);
    }
var signed = PackageSigner.Sign(zip, AppUpdate.Product, AppUpdate.Platform, next, rsa, policy);
var assets = new Dictionary<string, byte[]>
{
    [asset] = File.ReadAllBytes(zip), [asset + ".json"] = signed.ManifestBytes,
    [asset + ".json.sig"] = Encoding.UTF8.GetBytes(signed.Signature)
};
string url = $"https://github.com/OnjLouis/StreamSift/releases/download/{tag}/";
var api = new Fixture(assets, url, tag);
using var client = new GitHubUpdates(api, TimeSpan.FromSeconds(5));
var offer = await AppUpdate.CheckAsync(client, CancellationToken.None);
Check(offer != null && offer.Version.ToString(3) == next.ToString(), "Fetched correct stable release family.");
AppUpdate.CheckOfferAddresses(offer!);
Check(GitHubUpdates.Select([new GitHubRelease { Tag = tag, Prerelease = true }], "v", AppUpdate.AssetTemplate, current) == null,
    "Ignored prerelease.");
Check(GitHubUpdates.Select([new GitHubRelease { Tag = tag + "-preview", Draft = true }], "v", AppUpdate.AssetTemplate, current) == null,
    "Ignored draft.");

var stage = Path.Combine(directory, "job");
var downloaded = await client.DownloadAsync(offer!, policy, stage, CancellationToken.None);
File.Move(downloaded.PackagePath, Path.Combine(stage, "package.zip"));
File.WriteAllBytes(Path.Combine(stage, "manifest.json"), downloaded.ManifestBytes);
string signaturePath = Path.Combine(stage, "manifest.sig");
File.WriteAllText(signaturePath, signed.Signature);
var target = Path.Combine(directory, "target");
var backups = Path.Combine(directory, "backups");
Directory.CreateDirectory(Path.Combine(target, "User"));
foreach (var file in AppUpdate.ManagedFiles) File.WriteAllText(Path.Combine(target, file), "Original " + file);
settings.Save(Path.Combine(target, "User"));
var unknown = Path.Combine(target, "other-file.txt");
File.WriteAllText(unknown, "keep unknown");

File.WriteAllText(signaturePath, Convert.ToBase64String(unrelated.SignData(signed.ManifestBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
Reject(() => AppUpdate.InstallStaged(stage, target, backups, policy, current, next), "Accepted unrelated key.");
Check(File.ReadAllText(Path.Combine(target, "Manual.html")) == "Original Manual.html", "Rejected signature did not change program.");
File.WriteAllText(signaturePath, signed.Signature);
Reject(() => AppUpdate.InstallStaged(stage, target, backups, policy, next, next), "Accepted stale package.");
var changedZip = File.ReadAllBytes(Path.Combine(stage, "package.zip"));
changedZip[^1] ^= 1;
File.WriteAllBytes(Path.Combine(stage, "package.zip"), changedZip);
Reject(() => AppUpdate.InstallStaged(stage, target, backups, policy, current, next), "Tampered ZIP accepted.");
Check(File.ReadAllText(Path.Combine(target, "Manual.html")) == "Original Manual.html", "Tampering did not change program.");
File.Copy(zip, Path.Combine(stage, "package.zip"), overwrite: true);

var result = AppUpdate.InstallStaged(stage, target, backups, policy, current, next);
Check(File.Exists(result.RollbackArchive), "Transactional backup exists.");
Check(AppUpdate.ManagedFiles.All(file => File.ReadAllText(Path.Combine(target, file)) == "New version " + file), "All installed files match.");
Check(Preferences.Load(Path.Combine(target, "User")) == settings &&
    File.ReadAllText(unknown) == "keep unknown", "Preferences and unowned files preserved.");
Console.WriteLine($"{count} Windows and signed-update checks passed.");

sealed class Fixture(Dictionary<string, byte[]> assets, string url, string tag) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var uri = request.RequestUri!;
        byte[] bytes = uri.Host == "api.github.com"
            ? JsonSerializer.SerializeToUtf8Bytes(new[] {
                new { tag_name = tag, draft = false, prerelease = false, body = "Example",
                    assets = assets.Select(a => new { name = a.Key, browser_download_url = url + a.Key, size = a.Value.Length }) }
            })
            : assets[Path.GetFileName(uri.AbsolutePath)];
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { RequestMessage = request, Content = new ByteArrayContent(bytes) });
    }
}
