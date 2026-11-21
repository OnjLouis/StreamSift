using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Onj.SignedUpdater
{
    [DataContract]
    public sealed class GitHubRelease
    {
        [DataMember(Name = "tag_name")] public string Tag { get; set; }
        [DataMember(Name = "draft")] public bool Draft { get; set; }
        [DataMember(Name = "prerelease")] public bool Prerelease { get; set; }
        [DataMember(Name = "body")] public string Notes { get; set; }
        [DataMember(Name = "assets")] public List<GitHubAsset> Assets { get; set; }
    }

    [DataContract]
    public sealed class GitHubAsset
    {
        [DataMember(Name = "name")] public string Name { get; set; }
        [DataMember(Name = "browser_download_url")] public string Url { get; set; }
        [DataMember(Name = "size")] public long Size { get; set; }
    }

    public sealed class UpdateOffer
    {
        public Version Version { get; internal set; }
        public string Notes { get; internal set; }
        public GitHubAsset Package { get; internal set; }
        public GitHubAsset Manifest { get; internal set; }
        public GitHubAsset Signature { get; internal set; }
    }

    public sealed class DownloadedUpdate
    {
        public string PackagePath { get; internal set; }
        public byte[] ManifestBytes { get; internal set; }
        public string Signature { get; internal set; }
    }

    public sealed class GitHubUpdates : IDisposable
    {
        private readonly HttpClient client;
        private readonly TimeSpan timeout;

        public GitHubUpdates() : this(new HttpClientHandler { AllowAutoRedirect = false }, TimeSpan.FromMinutes(5)) { }
        public GitHubUpdates(HttpMessageHandler handler, TimeSpan requestTimeout)
        {
            if (handler == null || requestTimeout <= TimeSpan.Zero || requestTimeout > TimeSpan.FromHours(1)) throw new ArgumentException("Invalid HTTP handler or timeout.");
            client = new HttpClient(handler, true) { Timeout = Timeout.InfiniteTimeSpan };
            timeout = requestTimeout;
        }

        public async Task<UpdateOffer> CheckAsync(string owner, string repository, string tagPrefix, string packageTemplate, Version currentVersion, CancellationToken cancellation)
        {
            ValidateIdentifier(owner); ValidateIdentifier(repository);
            var releases = new List<GitHubRelease>();
            bool complete = false;
            for (var page = 1; page <= 5; page++)
            {
                using (var output = new MemoryStream())
                {
                    await GetAsync(new Uri("https://api.github.com/repos/" + owner + "/" + repository + "/releases?per_page=100&page=" + page), output, Wire.MaximumManifestBytes, cancellation).ConfigureAwait(false);
                    var batch = Wire.Deserialize<List<GitHubRelease>>(output.ToArray());
                    if (batch == null) throw new InvalidDataException("Invalid release catalog.");
                    releases.AddRange(batch);
                    if (batch.Count < 100) { complete = true; break; }
                }
            }
            if (!complete) throw new InvalidDataException("The release catalog exceeded its bounded scan. Use a dedicated release repository.");
            return Select(releases, tagPrefix, packageTemplate, currentVersion);
        }

        public static UpdateOffer Select(IEnumerable<GitHubRelease> releases, string tagPrefix, string packageTemplate, Version currentVersion)
        {
            if (string.IsNullOrEmpty(tagPrefix) || string.IsNullOrEmpty(packageTemplate) || currentVersion == null) throw new ArgumentException("Release family, asset template and installed version are required.");
            var candidates = new List<KeyValuePair<Version, GitHubRelease>>();
            foreach (var release in releases)
            {
                if (release == null || release.Draft || release.Prerelease || release.Tag == null || !release.Tag.StartsWith(tagPrefix, StringComparison.Ordinal)) continue;
                Version version;
                try { version = Wire.Version(release.Tag.Substring(tagPrefix.Length)); } catch (InvalidDataException) { continue; }
                candidates.Add(new KeyValuePair<Version, GitHubRelease>(version, release));
            }
            if (candidates.Count == 0) return null;
            var selected = candidates.OrderByDescending(c => c.Key).First();
            if (selected.Key <= Wire.Version(currentVersion.ToString())) return null;
            if (candidates.Count(c => c.Key == selected.Key) != 1) throw new InvalidDataException("The release catalog contains an ambiguous version.");
            var versionText = selected.Value.Tag.Substring(tagPrefix.Length);
            var name = packageTemplate.Replace("{version}", versionText);
            SafePaths.Normalize(name);
            if (name.Contains("/") || !name.EndsWith(".zip", StringComparison.Ordinal)) throw new ArgumentException("The asset template must produce a ZIP filename.");
            return new UpdateOffer { Version = selected.Key, Notes = selected.Value.Notes ?? "",
                Package = Asset(selected.Value, name), Manifest = Asset(selected.Value, name + ".json"), Signature = Asset(selected.Value, name + ".json.sig") };
        }

        public async Task<DownloadedUpdate> DownloadAsync(UpdateOffer offer, UpdatePolicy policy, string newDirectory, CancellationToken cancellation)
        {
            if (offer == null) throw new ArgumentNullException(nameof(offer));
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            policy = policy.Snapshot();
            var root = SafePaths.Root(newDirectory);
            if (Directory.Exists(root) || File.Exists(root)) throw new IOException("Download staging must be a new directory.");
            Directory.CreateDirectory(root);
            try
            {
                byte[] manifest; string signature;
                using (var output = new MemoryStream())
                {
                    await GetAsync(new Uri(offer.Manifest.Url), output, Wire.MaximumManifestBytes, cancellation).ConfigureAwait(false);
                    manifest = output.ToArray();
                }
                using (var output = new MemoryStream())
                {
                    await GetAsync(new Uri(offer.Signature.Url), output, Wire.MaximumSignatureCharacters, cancellation).ConfigureAwait(false);
                    signature = Wire.Utf8.GetString(output.ToArray());
                }
                var verified = PackageVerifier.VerifyManifest(manifest, signature, policy, offer.Version);
                if (verified.Package != offer.Package.Name || verified.Size != offer.Package.Size) throw new InvalidDataException("The signed package identity does not match the release asset.");
                var package = SafePaths.At(root, verified.Package);
                using (var output = new FileStream(package, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    await GetAsync(new Uri(offer.Package.Url), output, verified.Size, cancellation).ConfigureAwait(false);
                if (new FileInfo(package).Length != verified.Size || Wire.HashFile(package) != verified.Sha256) throw new InvalidDataException("Downloaded package hash or size is incorrect.");
                return new DownloadedUpdate { PackagePath = package, ManifestBytes = manifest, Signature = signature };
            }
            catch { SafePaths.NoLinks(root); Directory.Delete(root, true); throw; }
        }

        private async Task GetAsync(Uri url, Stream output, long maximum, CancellationToken cancellation)
        {
            using (var bounded = CancellationTokenSource.CreateLinkedTokenSource(cancellation))
            {
                bounded.CancelAfter(timeout);
                for (var redirect = 0; redirect <= 5; redirect++)
                {
                    Https(url);
                    using (var request = new HttpRequestMessage(HttpMethod.Get, url))
                    {
                        request.Headers.UserAgent.ParseAdd("Onj-SignedUpdater/0.1");
                        request.Headers.Accept.ParseAdd("application/vnd.github+json");
                        using (var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bounded.Token).ConfigureAwait(false))
                        {
                            var status = (int)response.StatusCode;
                            if (new[] { 301, 302, 303, 307, 308 }.Contains(status))
                            {
                                if (response.Headers.Location == null) throw new InvalidDataException("A download redirect has no location.");
                                url = new Uri(url, response.Headers.Location); continue;
                            }
                            response.EnsureSuccessStatusCode();
                            Https(response.RequestMessage?.RequestUri ?? url);
                            if (response.Content.Headers.ContentLength > maximum) throw new InvalidDataException("The download exceeds its size limit.");
                            using (var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                var buffer = new byte[65536]; long total = 0; int count;
                                while ((count = await input.ReadAsync(buffer, 0, buffer.Length, bounded.Token).ConfigureAwait(false)) > 0)
                                {
                                    total = checked(total + count);
                                    if (total > maximum) throw new InvalidDataException("The download exceeded its streaming limit.");
                                    await output.WriteAsync(buffer, 0, count, bounded.Token).ConfigureAwait(false);
                                }
                            }
                            return;
                        }
                    }
                }
            }
            throw new InvalidDataException("Too many download redirects.");
        }

        private static GitHubAsset Asset(GitHubRelease release, string name)
        {
            var assets = (release.Assets ?? new List<GitHubAsset>()).Where(a => a != null && a.Name == name).ToArray();
            if (assets.Length != 1 || assets[0].Size <= 0) throw new InvalidDataException("The newest release is missing or duplicates a required signed asset: " + name);
            Https(new Uri(assets[0].Url)); return assets[0];
        }

        private static void Https(Uri uri)
        {
            if (uri == null || !uri.IsAbsoluteUri || uri.Scheme != Uri.UriSchemeHttps || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("Update addresses and redirects must use HTTPS without embedded credentials.");
        }

        private static void ValidateIdentifier(string value)
        {
            if (string.IsNullOrEmpty(value) || value.Length > 100 || value.Any(c => !(char.IsLetterOrDigit(c) && c < 128) && c != '-' && c != '_' && c != '.')) throw new ArgumentException("Invalid GitHub owner or repository.");
        }

        public void Dispose() { client.Dispose(); }
    }
}
