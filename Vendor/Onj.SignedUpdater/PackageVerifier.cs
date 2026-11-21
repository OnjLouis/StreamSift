using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;

namespace Onj.SignedUpdater
{
    public static class PackageVerifier
    {
        public static UpdateManifest VerifyManifest(byte[] manifest, string signature, UpdatePolicy policy, Version expectedVersion)
        {
            SafePaths.ValidatePolicy(policy);
            if (manifest == null || manifest.Length == 0 || manifest.Length > Wire.MaximumManifestBytes ||
                string.IsNullOrWhiteSpace(signature) || signature.Length > Wire.MaximumSignatureCharacters) throw new InvalidDataException("The signed metadata is missing or oversized.");
            byte[] signatureBytes;
            try { signatureBytes = Convert.FromBase64String(signature.Trim()); }
            catch (FormatException error) { throw new InvalidDataException("The update signature is malformed.", error); }
            using (var rsa = SigningKeys.ImportXml(policy.PublicKeyXml, false))
            {
                if (signatureBytes.Length != rsa.KeySize / 8 || !rsa.VerifyData(manifest, signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1))
                    throw new InvalidDataException("The update signature is invalid. No program files were changed.");
            }
            var parsed = Wire.Deserialize<UpdateManifest>(manifest);
            Validate(parsed, policy, expectedVersion);
            return parsed;
        }

        internal static void Validate(UpdateManifest manifest, UpdatePolicy policy, Version expectedVersion)
        {
            if (manifest == null || manifest.Schema != 1 || manifest.Product != policy.Product || manifest.Platform != policy.Platform ||
                manifest.Size <= 0 || manifest.Size > policy.MaximumPackageBytes || !Wire.ValidHash(manifest.Sha256) ||
                manifest.Files == null || manifest.Files.Count == 0 || manifest.Files.Count > policy.MaximumEntries)
                throw new InvalidDataException("The manifest identity, limits or schema are invalid.");
            if (Wire.Version(manifest.Version) != Wire.Version(expectedVersion.ToString())) throw new InvalidDataException("The signed version does not match the selected release.");
            SafePaths.Normalize(manifest.Package);
            if (manifest.Package.Contains("/") || !manifest.Package.EndsWith(".zip", StringComparison.Ordinal)) throw new InvalidDataException("The signed package name must be a ZIP filename.");
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var file in manifest.Files)
            {
                if (file == null || file.Size < 0 || !Wire.ValidHash(file.Sha256) || (file.Mode != 0 && file.Mode != FileModes.Readable && file.Mode != FileModes.Executable)) throw new InvalidDataException("Invalid signed file record.");
                SafePaths.Managed(file.Path, policy);
                if (!paths.Add(file.Path)) throw new InvalidDataException("The manifest contains duplicate file paths.");
                total = checked(total + file.Size);
                if (total > policy.MaximumExtractedBytes) throw new InvalidDataException("The extracted package exceeds the size limit.");
            }
            foreach (var path in paths)
                if (path.Split('/').Length > 1 && Ancestors(path).Any(paths.Contains)) throw new InvalidDataException("A package file conflicts with a directory.");
            if (policy.RequiredFiles.Any(p => !paths.Contains(p))) throw new InvalidDataException("The package is missing a required program file.");
        }

        internal static IEnumerable<string> Ancestors(string path)
        {
            for (var index = path.LastIndexOf('/'); index > 0; index = path.LastIndexOf('/', index - 1)) yield return path.Substring(0, index);
        }

        internal static void Extract(string package, string destination, UpdateManifest manifest, UpdatePolicy policy)
        {
            var expected = manifest.Files.ToDictionary(f => f.Path, StringComparer.OrdinalIgnoreCase);
            if (new FileInfo(package).Length != manifest.Size || Wire.HashFile(package) != manifest.Sha256) throw new InvalidDataException("The package does not match its signed size or SHA-256.");
            using (var archive = ZipFile.OpenRead(package))
            {
                if (archive.Entries.Count > policy.MaximumEntries) throw new InvalidDataException("Too many ZIP entries.");
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in archive.Entries)
                {
                    // Preflight every entry before extraction, including directory records and platform link flags.
                    bool directory = entry.FullName.EndsWith("/", StringComparison.Ordinal);
                    var path = SafePaths.Normalize(directory ? entry.FullName.Substring(0, entry.FullName.Length - 1) : entry.FullName);
                    var attributes = ArchiveAttributes(entry);
                    var type = (attributes >> 16) & 0xF000;
                    if ((type != 0 && type != (directory ? 0x4000 : 0x8000)) || (attributes & (int)FileAttributes.ReparsePoint) != 0 || !seen.Add(path))
                        throw new InvalidDataException("The ZIP contains a link, special file or duplicate path.");
                    UpdateFile file;
                    if (directory)
                    {
                        if (entry.Length != 0 || !expected.Keys.Any(p => p.StartsWith(path + "/", StringComparison.OrdinalIgnoreCase)) || expected.ContainsKey(path))
                            throw new InvalidDataException("Unexpected ZIP directory.");
                    }
                    else if (!expected.TryGetValue(path, out file) || file.Path != path || file.Size != entry.Length)
                        throw new InvalidDataException("The ZIP does not exactly match the signed file list.");
                }
                if (expected.Keys.Any(p => !seen.Contains(p))) throw new InvalidDataException("The ZIP is missing a signed file.");
                foreach (var entry in archive.Entries.Where(e => !e.FullName.EndsWith("/", StringComparison.Ordinal)))
                {
                    var target = SafePaths.At(destination, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    var file = expected[entry.FullName];
                    using (var input = entry.Open())
                    using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        CopyBounded(input, output, file.Size);
                    if (Wire.HashFile(target) != file.Sha256) throw new InvalidDataException("An extracted file failed SHA-256 verification.");
                    if (file.Mode != 0) FileModes.Apply(target, file.Mode);
                }
            }
        }

        internal static void CopyBounded(Stream input, Stream output, long expected)
        {
            var buffer = new byte[65536];
            long total = 0;
            int count;
            while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                total = checked(total + count);
                if (total > expected) throw new InvalidDataException("A stream exceeded its declared size.");
                output.Write(buffer, 0, count);
            }
            if (total != expected) throw new InvalidDataException("A stream ended before its declared size.");
        }

        internal static int ArchiveAttributes(ZipArchiveEntry entry)
        {
            // .NET Standard 2.0 omits this member from its reference surface. Supported runtimes expose it.
            var property = typeof(ZipArchiveEntry).GetProperty("ExternalAttributes");
            if (property == null) throw new PlatformNotSupportedException("This runtime cannot inspect ZIP link flags safely.");
            return (int)property.GetValue(entry, null);
        }
    }
}
