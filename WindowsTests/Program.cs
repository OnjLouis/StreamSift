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

foreach (var resource in new[] { SearchSounds.SearchResource, SearchSounds.DoneResource })
{
    using var sound = typeof(SearchSounds).Assembly.GetManifestResourceStream(resource);
    Check(sound != null && sound.Length is > 16000 and < 28000, "Search cue embedded.");
    var header = new byte[12];
    sound!.ReadExactly(header);
    Check(header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WAVE"u8), "WAV format.");
}

var prefsFolder = Path.Combine(directory, "prefs");
Directory.CreateDirectory(prefsFolder);
Check(Preferences.Load(prefsFolder) is { SearchSounds: true, CheckUpdatesAtStartup: false, PlaylistFolder: null, AutomaticFallback: false } defaults &&
    defaults.ExportFolder == Preferences.DefaultPlaylistFolder, "New install defaults.");
File.WriteAllText(Path.Combine(prefsFolder, "SearchSounds.off"), "");
Check(!Preferences.Load(prefsFolder).SearchSounds, "Prior muted build stays muted.");
var exportFolder = Path.Combine(directory, "my playlists");
File.WriteAllText(Path.Combine(prefsFolder, "Preferences.json"), """{"SearchSounds":true,"CheckUpdatesAtStartup":true}""");
Check(Preferences.Load(prefsFolder) is { SearchSounds: true, CheckUpdatesAtStartup: true, AutomaticFallback: false },
    "Older preferences do not opt in to automatic fallback.");
var settings = new Preferences(true, true, exportFolder, AutomaticFallback: true);
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

var audio = new Uri("https://radio.example/stream");
var station = new Station("Lyd \u00e9 FM", audio, "MP3", 320, audio, false);
var playlist = PlaylistFile.SaveNew(exportFolder, station);
var secondPlaylist = PlaylistFile.SaveNew(exportFolder, station);
var expectedPlaylist = "#EXTM3U\r\n#EXTINF:-1,Lyd \u00e9 FM\r\nhttps://radio.example/stream\r\n";
Check(Path.GetFileName(playlist) == "Lyd \u00e9 FM.m3u8" && Path.GetFileName(secondPlaylist) == "Lyd \u00e9 FM (2).m3u8" &&
    File.ReadAllBytes(playlist).SequenceEqual(Encoding.UTF8.GetBytes(expectedPlaylist)), "Default-player exports are UTF-8 and never overwrite.");
Check(PlaylistFile.SuggestedName("CON") == "_CON" &&
    PlaylistFile.Text(station with { Name = "Name\r\n#EXTINF" }).Split("\r\n").Length == 4, "Reserved names and extra playlist lines are blocked.");
Reject(() => PlaylistFile.Text(station with { Url = new Uri("file:///C:/not-a-stream") }), "A local path was accepted as a stream.");

var incoming = Path.Combine(directory, "incoming");
var handoff = FoobarHandoff.Stage(incoming, station);
var freshHandoff = FoobarHandoff.Stage(incoming, station);
var command = FoobarHandoff.Command(@"C:\Program Files\foobar2000\foobar2000.exe", handoff);
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
