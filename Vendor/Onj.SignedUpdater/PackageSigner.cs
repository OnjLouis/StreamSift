using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Onj.SignedUpdater
{
    public sealed class SignedMetadata
    {
        public byte[] ManifestBytes { get; internal set; }
        public string Signature { get; internal set; }
    }

    public static class PackageSigner
    {
        public static SignedMetadata Sign(string packagePath, string product, string platform, Version version, RSA privateKey, UpdatePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            policy = policy.Snapshot();
            SafePaths.ValidatePolicy(policy);
            SafePaths.NoLinks(packagePath);
            if (privateKey == null || privateKey.KeySize < 3072) throw new ArgumentException("An RSA private key of at least 3072 bits is required.");
            var size = new FileInfo(packagePath).Length;
            if (size <= 0 || size > policy.MaximumPackageBytes) throw new InvalidDataException("Package size exceeds the signing policy.");
            var manifest = new UpdateManifest { Schema = 1, Product = product, Platform = platform, Version = version.ToString(),
                Package = Path.GetFileName(packagePath), Size = size, Sha256 = Wire.HashFile(packagePath), Files = new List<UpdateFile>() };
            using (var archive = ZipFile.OpenRead(packagePath))
            {
                if (archive.Entries.Count > policy.MaximumEntries) throw new InvalidDataException("Too many ZIP entries.");
                long total = 0;
                foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal)))
                {
                    total = checked(total + entry.Length);
                    if (total > policy.MaximumExtractedBytes) throw new InvalidDataException("The extracted package exceeds the size limit.");
                    SafePaths.Managed(entry.FullName, policy);
                    string hash;
                    using (var input = entry.Open()) hash = Wire.HashExact(input, entry.Length);
                    var permissions = (PackageVerifier.ArchiveAttributes(entry) >> 16) & 4095;
                    if ((permissions & ~511) != 0) throw new InvalidDataException("Privileged ZIP permission bits are not supported.");
                    var mode = permissions == 0 ? 0 : (permissions & 73) != 0 ? FileModes.Executable : FileModes.Readable;
                    manifest.Files.Add(new UpdateFile { Path = entry.FullName, Size = entry.Length, Sha256 = hash, Mode = mode });
                }
            }
            manifest.Files = manifest.Files.OrderBy(f => f.Path, StringComparer.Ordinal).ToList();
            PackageVerifier.Validate(manifest, policy, version);
            // Signing performs the same archive preflight as installation; never bless a malformed ZIP.
            var stage = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(packagePath)), ".sign-check-" + Guid.NewGuid().ToString("N"));
            try { Directory.CreateDirectory(stage); PackageVerifier.Extract(packagePath, stage, manifest, policy); }
            finally { if (Directory.Exists(stage)) Directory.Delete(stage, true); }
            var bytes = Wire.Serialize(manifest);
            return new SignedMetadata { ManifestBytes = bytes, Signature = Convert.ToBase64String(privateKey.SignData(bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)) };
        }
    }
}
