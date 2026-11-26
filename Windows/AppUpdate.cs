using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Onj.SignedUpdater;

namespace StreamSift;

internal static class AppUpdate
{
    internal const string Product = "StreamSift";
#if STREAMSIFT_WINDOWS7
    internal const string TagPrefix = "win7-v";
#if STREAMSIFT_WINDOWS7_X86
    internal const string Platform = "windows7-x86", AssetTemplate = "StreamSift-Windows7-x86-{version}.zip";
    private const string InstalledProduct = "StreamSift for Windows 7 (win-x86)";
#else
    internal const string Platform = "windows7-x64", AssetTemplate = "StreamSift-Windows7-x64-{version}.zip";
    private const string InstalledProduct = "StreamSift for Windows 7 (win-x64)";
#endif
    private static string UpdatesDirectory => Path.Combine(Product, "Windows7", Platform, "Updates");
#else
    internal const string TagPrefix = "v";
    internal const string Platform = "windows-x64", AssetTemplate = "StreamSift-Windows-x64-{version}.zip";
    private const string InstalledProduct = Product;
    private static string UpdatesDirectory => Path.Combine(Product, "Updates");
#endif
    private const string Repository = "/OnjLouis/StreamSift";
    internal const string ReleasesUrl = "https://github.com" + Repository + "/releases";
    private const string AppFile = "StreamSift.exe", ManifestFile = "manifest.json", SignatureFile = "manifest.sig";
    private const int MaxManifest = 1024 * 1024, MaxSignature = 8192;
    internal static readonly string[] ManagedFiles = [AppFile, "StreamSift.dll", "StreamSift.deps.json",
        "StreamSift.runtimeconfig.json", "Core.dll", "AngleSharp.dll", "Onj.SignedUpdater.dll",
        "Manual.html", "AngleSharp-LICENSE.txt", "SignedUpdater-LICENSE.txt",
#if STREAMSIFT_WINDOWS7
        "System.Text.Encoding.CodePages.dll",
#endif
    ];

    private static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), UpdatesDirectory);
    internal static Version Current => Version.Parse(Program.Version);

    internal static string? VerificationKey()
    {
        using var stream = typeof(AppUpdate).Assembly.GetManifestResourceStream("StreamSift.UpdatePublicKey.xml");
        if (stream == null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static UpdatePolicy Policy(string xml)
    {
        var element = XElement.Parse(xml);
        if (element.Name != "RSAKeyValue" || !element.Elements().Select(x => x.Name.LocalName).SequenceEqual(["Modulus", "Exponent"]))
            throw new InvalidDataException("An RSA public-only verification key is required.");
        using var rsa = RSA.Create();
        rsa.FromXmlString(xml);
        if (rsa.KeySize < 3072) throw new InvalidDataException("The update verification key is invalid.");
        return new UpdatePolicy
        {
            Product = Product, Platform = Platform, PublicKeyXml = xml,
            ManagedPaths = ManagedFiles, RequiredFiles = ManagedFiles,
            MaximumPackageBytes = 16 * 1024 * 1024, MaximumExtractedBytes = 32 * 1024 * 1024,
            MaximumEntries = 32, RetryCount = 10, RetryDelayMilliseconds = 300
        };
    }

    internal static GitHubUpdates Client() => new(new UpdateTransport(), TimeSpan.FromSeconds(25));
    internal static Task<UpdateOffer> CheckAsync(GitHubUpdates client, CancellationToken token) =>
        client.CheckAsync("OnjLouis", Product, TagPrefix, AssetTemplate, Current, token);

    internal static Task<UpdateOffer> ReadLatestReleaseAsync(GitHubUpdates client, CancellationToken token) =>
        client.CheckAsync("OnjLouis", Product, TagPrefix, AssetTemplate, new Version(0, 0, 0), token);

    internal static async Task StageAndStartAsync(GitHubUpdates client, UpdateOffer offer, UpdatePolicy policy, CancellationToken token)
    {
        var target = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        var currentPath = Path.Combine(target, AppFile);
        using var process = Process.GetCurrentProcess();
        if (!string.Equals(process.MainModule?.FileName, currentPath, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Open StreamSift normally before installing an update.");
        CheckOfferAddresses(offer);
        var job = Path.Combine(Root, "job-" + Guid.NewGuid().ToString("N"));
        NoReparse(job);
        Directory.CreateDirectory(job);
        try
        {
            var download = await client.DownloadAsync(offer, policy, Path.Combine(job, "download"), token).ConfigureAwait(false);
            File.Move(download.PackagePath, Path.Combine(job, "package.zip"));
            File.WriteAllBytes(Path.Combine(job, ManifestFile), download.ManifestBytes);
            File.WriteAllText(Path.Combine(job, SignatureFile), download.Signature);
            foreach (var name in ManagedFiles)
            {
                token.ThrowIfCancellationRequested();
                NoReparse(Path.Combine(target, name));
                File.Copy(Path.Combine(target, name), Path.Combine(job, name));
            }
            var launch = new ProcessStartInfo(Path.Combine(job, AppFile))
            { WorkingDirectory = job, UseShellExecute = false, CreateNoWindow = true };
            launch.ArgumentList.Add("--apply-update");
            launch.ArgumentList.Add(target);
            launch.ArgumentList.Add(process.Id.ToString(CultureInfo.InvariantCulture));
            launch.ArgumentList.Add(process.StartTime.ToUniversalTime().Ticks.ToString(CultureInfo.InvariantCulture));
            launch.ArgumentList.Add(offer.Version.ToString());
            token.ThrowIfCancellationRequested();
            using var worker = Process.Start(launch) ?? throw new IOException("The update helper could not start.");
        }
        catch { TryClean(job); throw; }
    }

    internal static bool Run(string[] args)
    {
        if (args is ["--cleanup-update", var jobName])
        {
            if (IsJobName(jobName))
            {
                var previousJob = Path.Combine(Root, jobName);
                _ = Task.Run(async () =>
                {
                    for (int attempt = 0; attempt < 20; attempt++)
                    {
                        if (TryClean(previousJob)) break;
                        await Task.Delay(400).ConfigureAwait(false);
                    }
                });
            }
            return false;
        }
        if (args.FirstOrDefault() != "--apply-update") return false;
        var job = Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory);
        string? restartPath = null;
        try
        {
            if (args is not [_, var path, var pid, var ticks, var expected] || !IsOwnJob(job))
                throw new InvalidDataException("The updater was not launched from its staging folder.");
            var policy = Policy(VerificationKey() ?? throw new InvalidDataException("The update verification key is missing."));
            var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            var exe = Path.Combine(target, AppFile);
            NoReparse(target);
            NoReparse(exe);
            if (Path.GetFullPath(exe).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staging folder cannot be updated.");
            if (!MatchesInstalledBuild(FileVersionInfo.GetVersionInfo(exe), Current))
                throw new InvalidDataException("The installed program changed before the update.");
            ProcessGuard.WaitForExit(int.Parse(pid, CultureInfo.InvariantCulture), exe,
                new DateTime(long.Parse(ticks, CultureInfo.InvariantCulture), DateTimeKind.Utc), TimeSpan.FromSeconds(30), CancellationToken.None);
            restartPath = exe;
            var backupId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(target.ToUpperInvariant())))[..24];
            var backup = Path.Combine(Root, "Backups", backupId);
            InstallStaged(job, target, backup, policy, Current, Version.Parse(expected));
        }
        catch (Exception error)
        {
            if (error is UpdateRecoveryException) restartPath = null;
            using var dialog = new TextDialog("Update could not finish", error.Message);
            dialog.ShowDialog();
        }
        if (restartPath != null)
        {
            try
            {
                var action = new ProcessStartInfo(restartPath) { UseShellExecute = false };
                action.ArgumentList.Add("--cleanup-update");
                action.ArgumentList.Add(Path.GetFileName(job));
                using var restarted = Process.Start(action) ?? throw new IOException("The program did not reopen.");
            }
            catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                using var dialog = new TextDialog("Open StreamSift", "Open StreamSift again from its usual folder.");
                dialog.ShowDialog();
            }
        }
        return true;
    }

    internal static InstallResult InstallStaged(string job, string target, string backup, UpdatePolicy policy,
        Version current, Version expected)
    {
        var package = Path.Combine(job, "package.zip");
        var manifest = ReadSmall(Path.Combine(job, ManifestFile), MaxManifest);
        var signature = Encoding.UTF8.GetString(ReadSmall(Path.Combine(job, SignatureFile), MaxSignature));
        var scratch = Path.Combine(job, "install");
        // Validate the downloaded bytes before touching a pending transaction.
        var verified = PackageVerifier.VerifyManifest(manifest, signature, policy, expected);
        NoReparse(package);
        using (var archive = File.OpenRead(package))
        using (var sha = SHA256.Create())
        {
            if (archive.Length != verified.Size ||
                !Convert.ToHexString(sha.ComputeHash(archive)).Equals(verified.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The staged update package changed.");
        }
        UpdateInstaller.Recover(target, scratch, backup, policy);
        return UpdateInstaller.Install(package, manifest, signature, current, expected, policy, target, scratch, backup);
    }

    private static byte[] ReadSmall(string path, int limit)
    {
        NoReparse(path);
        using var stream = File.OpenRead(path);
        if (stream.Length < 1 || stream.Length > limit) throw new InvalidDataException("Invalid update metadata size.");
        int length = (int)stream.Length;
        using var reader = new BinaryReader(stream);
        byte[] data = reader.ReadBytes(length);
        return data.Length == length ? data : throw new InvalidDataException("Truncated update metadata.");
    }

    internal static bool MatchesInstalledBuild(FileVersionInfo file, Version current) =>
        file.ProductName == InstalledProduct && Version.TryParse(file.FileVersion, out var version) &&
        version.Build >= 0 && version.ToString(3) == current.ToString(3);

    private static bool IsOwnJob(string job) => string.Equals(Path.GetDirectoryName(job), Root, StringComparison.OrdinalIgnoreCase) &&
        IsJobName(Path.GetFileName(job));
    private static bool IsJobName(string name) =>
        name.StartsWith("job-", StringComparison.Ordinal) && Guid.TryParseExact(name[4..], "N", out _);

    private static void NoReparse(string path)
    {
        for (string? entry = Path.GetFullPath(path); entry != null; entry = Path.GetDirectoryName(entry))
            if ((File.Exists(entry) || Directory.Exists(entry)) && (File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("An update path points at a redirected file or folder.");
    }

    private static bool TryClean(string job)
    {
        if (!IsOwnJob(job)) return false;
        try
        {
            NoReparse(job);
            if (!Directory.Exists(job)) return true;
            var pending = new Queue<string>();
            pending.Enqueue(job);
            int count = 0;
            while (pending.TryDequeue(out var folder))
                foreach (var entry in Directory.EnumerateFileSystemEntries(folder))
                {
                    var flags = File.GetAttributes(entry);
                    if (++count > 160 || (flags & FileAttributes.ReparsePoint) != 0) return false;
                    if ((flags & FileAttributes.Directory) != 0) pending.Enqueue(entry);
                }
            Directory.Delete(job, recursive: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    internal static void CheckOfferAddresses(UpdateOffer offer)
    {
        string prefix = Repository + "/releases/download/" + TagPrefix + offer.Version.ToString(3) + "/";
        foreach (var asset in new[] { offer.Package, offer.Manifest, offer.Signature })
        {
            var path = new Uri(asset.Url);
            if (!IsTrustedUrl(path) || path.Host != "github.com" || path.AbsolutePath != prefix + asset.Name)
                throw new InvalidDataException("An update asset does not belong to the StreamSift release feed.");
        }
    }

    internal static bool IsTrustedUrl(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == "https" && uri.IsDefaultPort && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0 &&
        ((uri.Host == "api.github.com" && uri.AbsolutePath == "/repos" + Repository + "/releases") ||
         (uri.Host == "github.com" && uri.Query.Length == 0 && uri.AbsolutePath.StartsWith(Repository + "/releases/download/", StringComparison.Ordinal)) ||
         (uri.Host == "release-assets.githubusercontent.com" && uri.AbsolutePath.StartsWith("/github-production-release-asset/", StringComparison.Ordinal)));

    private sealed class UpdateTransport : DelegatingHandler
    {
        internal UpdateTransport() : base(new HttpClientHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
#if STREAMSIFT_WINDOWS7
            SslProtocols = System.Security.Authentication.SslProtocols.Tls12
#endif
        }) { }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            request.RequestUri is Uri uri && IsTrustedUrl(uri) ? base.SendAsync(request, token) :
            throw new InvalidDataException("The update feed redirected to an unexpected address.");
    }
}
