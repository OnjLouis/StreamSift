using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.Serialization;
using System.Threading;

namespace Onj.SignedUpdater
{
    public sealed class InstallResult
    {
        public string Version { get; internal set; }
        public string RollbackArchive { get; internal set; }
    }

    public sealed class UpdateRecoveryException : IOException
    {
        public string RollbackArchive { get; private set; }
        internal UpdateRecoveryException(string archive, Exception update, Exception rollback)
            : base("Update and rollback both failed. Keep the recovery ZIP and retry Recover after releasing file locks.", new AggregateException(update, rollback)) { RollbackArchive = archive; }
    }

    [DataContract]
    internal sealed class RollbackRecord
    {
        [DataMember] public string Product;
        [DataMember] public string Platform;
        [DataMember] public string Target;
        [DataMember] public List<RollbackFile> Files;
        [DataMember] public List<string> CreatedDirectories;
    }

    [DataContract]
    internal sealed class RollbackFile
    {
        [DataMember] public string Path;
        [DataMember] public bool Existed;
        [DataMember] public long Size;
        [DataMember] public string Sha256;
        [DataMember] public int Mode;
    }

    [DataContract]
    internal sealed class PendingUpdate
    {
        [DataMember] public string Archive;
        [DataMember] public string Sha256;
    }

    public static class UpdateInstaller
    {
        public static InstallResult Install(string packagePath, byte[] manifestBytes, string signature, Version currentVersion,
            Version expectedVersion, UpdatePolicy policy, string targetDirectory, string scratchDirectory, string backupDirectory)
        {
            if (policy == null || manifestBytes == null) throw new ArgumentNullException("policy or manifestBytes");
            policy = policy.Snapshot(); manifestBytes = (byte[])manifestBytes.Clone();
            var manifest = PackageVerifier.VerifyManifest(manifestBytes, signature, policy, expectedVersion);
            if (Wire.Version(manifest.Version) <= Wire.Version(currentVersion.ToString())) throw new InvalidDataException("The update must be newer than the installed version.");
            var target = SafePaths.Root(targetDirectory);
            var scratch = SafePaths.Root(scratchDirectory);
            var backups = SafePaths.Root(backupDirectory);
            SafePaths.Separate(target, scratch); SafePaths.Separate(target, backups); SafePaths.Separate(scratch, backups);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The install folder does not exist.");
            SafePaths.NoLinks(packagePath);
            Directory.CreateDirectory(scratch); Directory.CreateDirectory(backups);
            using (AcquireLock(target))
            {
                if (File.Exists(SafePaths.At(target, SafePaths.Pending))) throw new InvalidOperationException("An interrupted update needs Recover before another update can start.");
                var previous = ReadReceipt(target, policy);
                if (previous != null && Wire.Version(previous.Version) != Wire.Version(currentVersion.ToString()))
                    throw new InvalidDataException("The supplied installed version disagrees with the verified update receipt.");
                var run = Path.Combine(scratch, "onj-update-" + Guid.NewGuid().ToString("N"));
                var stage = Path.Combine(run, "stage");
                Directory.CreateDirectory(stage);
                string backup = null;
                bool pending = false;
                try
                {
                    Report(policy, "Verifying and staging the signed package.");
                    // Work with a private snapshot, not a caller-controlled archive that can change during extraction.
                    var snapshot = Path.Combine(run, "package.zip");
                    using (var input = File.OpenRead(packagePath))
                    using (var output = new FileStream(snapshot, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                        PackageVerifier.CopyBounded(input, output, manifest.Size);
                    PackageVerifier.Extract(snapshot, stage, manifest, policy);
                    File.WriteAllBytes(SafePaths.At(stage, SafePaths.Receipt), manifestBytes);
                    File.WriteAllText(SafePaths.At(stage, SafePaths.ReceiptSignature), signature, Wire.Utf8);
                    var files = manifest.Files.Select(f => f.Path).ToList();
                    var removed = previous == null ? new List<string>() : previous.Files.Select(f => f.Path)
                        .Where(p => !files.Contains(p, StringComparer.OrdinalIgnoreCase)).ToList();
                    var changed = files.Concat(removed).Concat(new[] { SafePaths.Receipt, SafePaths.ReceiptSignature }).ToList();
                    foreach (var path in changed)
                    {
                        var destination = SafePaths.At(target, path);
                        if (Directory.Exists(destination)) throw new InvalidDataException("A program file collides with an existing directory: " + path);
                        foreach (var parent in PackageVerifier.Ancestors(path))
                            if (File.Exists(SafePaths.At(target, parent))) throw new InvalidDataException("A program directory collides with an existing file.");
                    }
                    Report(policy, "Saving the rollback ZIP.");
                    backup = CreateBackup(target, backups, changed, policy);
                    var journal = new PendingUpdate { Archive = Path.GetFileName(backup), Sha256 = Wire.HashFile(backup) };
                    using (var output = new FileStream(SafePaths.At(target, SafePaths.Pending), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    {
                        var bytes = Wire.Serialize(journal); output.Write(bytes, 0, bytes.Length); output.Flush(true);
                    }
                    pending = true;
                    Report(policy, "Replacing program files. User files are not touched.");
                    foreach (var path in files.Concat(new[] { SafePaths.Receipt, SafePaths.ReceiptSignature }))
                    {
                        var record = manifest.Files.FirstOrDefault(f => f.Path == path);
                        AtomicCopy(SafePaths.At(stage, path), SafePaths.At(target, path), policy, record?.Mode == 0 ? null : (int?)record?.Mode);
                        Report(policy, "Installed: " + path);
                    }
                    foreach (var path in removed) DeleteFile(SafePaths.At(target, path), policy);
                    foreach (var file in manifest.Files)
                        if (Wire.HashFile(SafePaths.At(target, file.Path)) != file.Sha256) throw new IOException("Installed file verification failed.");
                    DeleteFile(SafePaths.At(target, SafePaths.Pending), policy);
                    pending = false;
                    Report(policy, "Update complete.");
                    PruneBackups(backups, backup, policy);
                    return new InstallResult { Version = manifest.Version, RollbackArchive = backup };
                }
                catch (Exception error)
                {
                    if (pending)
                    {
                        Report(policy, "Update failed. Restoring the previous files.");
                        try { Restore(target, backups, scratch, policy); Report(policy, "Previous files restored."); }
                        catch (Exception restore) { throw new UpdateRecoveryException(backup, error, restore); }
                    }
                    throw;
                }
                finally
                {
                    try { SafePaths.NoLinks(run); Directory.Delete(run, true); }
                    catch (Exception error) { Report(policy, "Scratch cleanup could not finish: " + error.GetType().Name); }
                }
            }
        }

        public static bool Recover(string targetDirectory, string scratchDirectory, string backupDirectory, UpdatePolicy policy)
        {
            if (policy == null) throw new ArgumentNullException(nameof(policy));
            policy = policy.Snapshot();
            SafePaths.ValidatePolicy(policy);
            var target = SafePaths.Root(targetDirectory); var scratch = SafePaths.Root(scratchDirectory); var backups = SafePaths.Root(backupDirectory);
            SafePaths.Separate(target, scratch); SafePaths.Separate(target, backups); SafePaths.Separate(scratch, backups);
            if (!Directory.Exists(target)) throw new DirectoryNotFoundException("The install folder does not exist.");
            using (AcquireLock(target))
            {
                if (!File.Exists(SafePaths.At(target, SafePaths.Pending))) return false;
                Directory.CreateDirectory(scratch);
                Restore(target, backups, scratch, policy);
                Report(policy, "Interrupted update recovered.");
                return true;
            }
        }

        private static FileStream AcquireLock(string target)
        {
            return new FileStream(SafePaths.At(target, SafePaths.Lock), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }

        private static UpdateManifest ReadReceipt(string target, UpdatePolicy policy)
        {
            var path = SafePaths.At(target, SafePaths.Receipt); var sig = SafePaths.At(target, SafePaths.ReceiptSignature);
            if (!File.Exists(path) && !File.Exists(sig)) return null;
            var bytes = ReadBounded(path, Wire.MaximumManifestBytes);
            var untrusted = Wire.Deserialize<UpdateManifest>(bytes);
            return PackageVerifier.VerifyManifest(bytes, Wire.Utf8.GetString(ReadBounded(sig, Wire.MaximumSignatureCharacters)), policy, Wire.Version(untrusted.Version));
        }

        private static string CreateBackup(string target, string backups, List<string> changed, UpdatePolicy policy)
        {
            var record = new RollbackRecord { Product = policy.Product, Platform = policy.Platform, Target = target, Files = new List<RollbackFile>(), CreatedDirectories = new List<string>() };
            long total = 0;
            foreach (var path in changed)
            {
                var existing = SafePaths.At(target, path); var present = File.Exists(existing);
                var item = new RollbackFile { Path = path, Existed = present };
                if (present) { item.Size = new FileInfo(existing).Length; item.Sha256 = Wire.HashFile(existing); item.Mode = FileModes.Read(existing); total = checked(total + item.Size); }
                if (total > policy.MaximumExtractedBytes) throw new InvalidDataException("The rollback snapshot exceeds the configured size limit.");
                record.Files.Add(item);
                foreach (var parent in PackageVerifier.Ancestors(path))
                    if (!Directory.Exists(SafePaths.At(target, parent)) && !record.CreatedDirectories.Contains(parent, StringComparer.OrdinalIgnoreCase)) record.CreatedDirectories.Add(parent);
            }
            var archivePath = Path.Combine(backups, "onj-rollback-" + Guid.NewGuid().ToString("N") + ".zip");
            try
            {
                using (var output = new FileStream(archivePath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
                {
                    using (var zip = new ZipArchive(output, ZipArchiveMode.Create, true))
                    {
                        foreach (var file in record.Files.Where(f => f.Existed))
                        {
                            var entry = zip.CreateEntry("files/" + file.Path, CompressionLevel.Optimal);
                            using (var source = File.OpenRead(SafePaths.At(target, file.Path)))
                            using (var destination = entry.Open()) PackageVerifier.CopyBounded(source, destination, file.Size);
                        }
                        using (var destination = zip.CreateEntry("recovery.json").Open())
                        {
                            var bytes = Wire.Serialize(record); destination.Write(bytes, 0, bytes.Length);
                        }
                    }
                    output.Flush(true);
                }
                // Ensure the captured rollback bytes match the records before touching the live install.
                using (var zip = ZipFile.OpenRead(archivePath))
                {
                    foreach (var file in record.Files.Where(f => f.Existed))
                    using (var input = zip.GetEntry("files/" + file.Path).Open())
                        if (Wire.Hash(input) != file.Sha256) throw new IOException("A program file changed while its rollback copy was being made.");
                }
                return archivePath;
            }
            catch { if (File.Exists(archivePath)) File.Delete(archivePath); throw; }
        }

        private static void Restore(string target, string backups, string scratch, UpdatePolicy policy)
        {
            var pendingPath = SafePaths.At(target, SafePaths.Pending);
            var journal = Wire.Deserialize<PendingUpdate>(ReadBounded(pendingPath, 16384));
            if (journal.Archive == null || journal.Archive != Path.GetFileName(journal.Archive) || !journal.Archive.StartsWith("onj-rollback-", StringComparison.Ordinal) ||
                !journal.Archive.EndsWith(".zip", StringComparison.Ordinal) || !Wire.ValidHash(journal.Sha256)) throw new InvalidDataException("Invalid recovery journal.");
            var backup = SafePaths.At(backups, journal.Archive);
            if (new FileInfo(backup).Length > policy.MaximumExtractedBytes + Wire.MaximumManifestBytes || Wire.HashFile(backup) != journal.Sha256)
                throw new InvalidDataException("The recovery ZIP changed or exceeds its size limit.");
            var run = Path.Combine(scratch, "onj-recover-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(run);
            try
            {
                using (var zip = ZipFile.OpenRead(backup))
                {
                    if (zip.Entries.Count > policy.MaximumEntries + 3) throw new InvalidDataException("Too many recovery entries.");
                    var metadata = zip.Entries.Single(e => e.FullName == "recovery.json");
                    byte[] bytes;
                    using (var input = metadata.Open()) bytes = ReadBounded(input, Wire.MaximumManifestBytes);
                    var record = Wire.Deserialize<RollbackRecord>(bytes);
                    if (record.Product != policy.Product || record.Platform != policy.Platform || record.Target != target || record.Files == null || record.CreatedDirectories == null ||
                        record.Files.Count > policy.MaximumEntries + 2 || record.CreatedDirectories.Count > policy.MaximumEntries)
                        throw new InvalidDataException("The recovery ZIP belongs to a different install or policy.");
                    var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    long total = 0;
                    foreach (var file in record.Files)
                    {
                        if (file == null || !seen.Add(file.Path)) throw new InvalidDataException("Duplicated recovery path.");
                        if (file.Path != SafePaths.Receipt && file.Path != SafePaths.ReceiptSignature) SafePaths.Managed(file.Path, policy);
                        SafePaths.At(target, file.Path);
                        if (!file.Existed) continue;
                        if (file.Size < 0 || !Wire.ValidHash(file.Sha256) || file.Mode < 0 || file.Mode > 511) throw new InvalidDataException("Invalid recovery file.");
                        total = checked(total + file.Size);
                        if (total > policy.MaximumExtractedBytes) throw new InvalidDataException("Recovery exceeds its size limit.");
                        var entry = zip.Entries.Single(e => e.FullName == "files/" + file.Path);
                        var staged = SafePaths.At(run, file.Path); Directory.CreateDirectory(Path.GetDirectoryName(staged));
                        using (var input = entry.Open())
                        using (var output = new FileStream(staged, FileMode.CreateNew, FileAccess.Write, FileShare.None)) PackageVerifier.CopyBounded(input, output, file.Size);
                        if (Wire.HashFile(staged) != file.Sha256) throw new InvalidDataException("Recovery file hash mismatch.");
                    }
                    if (zip.Entries.Count != 1 + record.Files.Count(f => f.Existed)) throw new InvalidDataException("Unexpected recovery ZIP entries.");
                    foreach (var directory in record.CreatedDirectories)
                    {
                        SafePaths.Normalize(directory);
                        if (!record.Files.Any(f => f.Path.StartsWith(directory + "/", StringComparison.OrdinalIgnoreCase))) throw new InvalidDataException("Unexpected recovery directory.");
                    }
                    // Complete validation before making any rollback write. Restore absent files as absent.
                    foreach (var file in record.Files)
                    {
                        var destination = SafePaths.At(target, file.Path);
                        if (!file.Existed) DeleteFile(destination, policy);
                        else if (!File.Exists(destination) || Wire.HashFile(destination) != file.Sha256 || FileModes.Read(destination) != file.Mode) AtomicCopy(SafePaths.At(run, file.Path), destination, policy, file.Mode);
                    }
                    foreach (var directory in record.CreatedDirectories.OrderByDescending(p => p.Length))
                    {
                        var path = SafePaths.At(target, directory);
                        if (Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any()) Directory.Delete(path);
                    }
                }
                DeleteFile(pendingPath, policy);
            }
            finally { SafePaths.NoLinks(run); Directory.Delete(run, true); }
        }

        private static void AtomicCopy(string source, string target, UpdatePolicy policy, int? mode = null)
        {
            SafePaths.NoLinks(target); Directory.CreateDirectory(Path.GetDirectoryName(target));
            var temporary = Path.Combine(Path.GetDirectoryName(target), ".onj-update-write-" + Guid.NewGuid().ToString("N"));
            try
            {
                using (var input = File.OpenRead(source))
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { input.CopyTo(output); output.Flush(true); }
                FileModes.Apply(temporary, mode ?? (File.Exists(target) ? FileModes.Read(target) : FileModes.Read(temporary)));
                Retry(() =>
                {
                    SafePaths.NoLinks(target);
                    if (File.Exists(target)) File.Replace(temporary, target, null);
                    else File.Move(temporary, target);
                }, policy);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }

        private static void DeleteFile(string path, UpdatePolicy policy)
        {
            Retry(() => { SafePaths.NoLinks(path); if (File.Exists(path)) File.Delete(path); }, policy);
        }

        private static void Retry(Action operation, UpdatePolicy policy)
        {
            for (var attempt = 0; ; attempt++)
            {
                try { operation(); return; }
                catch (IOException) { if (attempt + 1 >= policy.RetryCount) throw; }
                catch (UnauthorizedAccessException) { if (attempt + 1 >= policy.RetryCount) throw; }
                Thread.Sleep(policy.RetryDelayMilliseconds);
            }
        }

        private static byte[] ReadBounded(string path, int maximum) { SafePaths.NoLinks(path); using (var input = File.OpenRead(path)) return ReadBounded(input, maximum); }
        private static byte[] ReadBounded(Stream input, int maximum)
        {
            using (var output = new MemoryStream())
            {
                var buffer = new byte[4096]; int count;
                while ((count = input.Read(buffer, 0, buffer.Length)) > 0)
                { if (output.Length + count > maximum) throw new InvalidDataException("Metadata size limit exceeded."); output.Write(buffer, 0, count); }
                return output.ToArray();
            }
        }

        private static void PruneBackups(string directory, string current, UpdatePolicy policy)
        {
            try
            {
                var currentTarget = WireTarget(current);
                // Only this product/install's records are eligible; shared backup folders may contain other apps.
                var ours = new List<string>();
                foreach (var file in Directory.EnumerateFiles(directory, "onj-rollback-*.zip").Take(1000))
                {
                    try
                    {
                        SafePaths.NoLinks(file);
                        if (new FileInfo(file).Length > policy.MaximumExtractedBytes + Wire.MaximumManifestBytes) continue;
                        using (var zip = ZipFile.OpenRead(file))
                        {
                            if (zip.Entries.Count > policy.MaximumEntries + 3) continue;
                            var entry = zip.GetEntry("recovery.json"); if (entry == null) continue;
                            RollbackRecord record;
                            using (var input = entry.Open()) record = Wire.Deserialize<RollbackRecord>(ReadBounded(input, Wire.MaximumManifestBytes));
                            if (record.Product == policy.Product && record.Platform == policy.Platform && record.Target == currentTarget) ours.Add(file);
                        }
                    }
                    catch (IOException) { }
                    catch (System.Runtime.Serialization.SerializationException) { }
                }
                foreach (var file in ours.Where(p => p != current).OrderByDescending(File.GetLastWriteTimeUtc).Skip(1)) File.Delete(file);
            }
            catch (Exception error) { Report(policy, "Backup retention could not finish: " + error.GetType().Name); }
        }

        private static string WireTarget(string archive)
        {
            using (var zip = ZipFile.OpenRead(archive))
            using (var input = zip.GetEntry("recovery.json").Open()) return Wire.Deserialize<RollbackRecord>(ReadBounded(input, Wire.MaximumManifestBytes)).Target;
        }

        internal static void Report(UpdatePolicy policy, string message) { try { policy.Status?.Invoke(message); } catch { } }
    }
}
