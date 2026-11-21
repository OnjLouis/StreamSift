using System;
using System.Collections.Generic;
using System.Runtime.Serialization;

namespace Onj.SignedUpdater
{
    [DataContract]
    public sealed class UpdateManifest
    {
        [DataMember(Name = "schema", IsRequired = true)] public int Schema { get; set; }
        [DataMember(Name = "product", IsRequired = true)] public string Product { get; set; }
        [DataMember(Name = "platform", IsRequired = true)] public string Platform { get; set; }
        [DataMember(Name = "version", IsRequired = true)] public string Version { get; set; }
        [DataMember(Name = "package", IsRequired = true)] public string Package { get; set; }
        [DataMember(Name = "size", IsRequired = true)] public long Size { get; set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        [DataMember(Name = "files", IsRequired = true)] public List<UpdateFile> Files { get; set; }
    }

    [DataContract]
    public sealed class UpdateFile
    {
        [DataMember(Name = "path", IsRequired = true)] public string Path { get; set; }
        [DataMember(Name = "size", IsRequired = true)] public long Size { get; set; }
        [DataMember(Name = "sha256", IsRequired = true)] public string Sha256 { get; set; }
        [DataMember(Name = "mode", EmitDefaultValue = false)] public int Mode { get; set; }
    }

    public sealed class UpdatePolicy
    {
        public string Product { get; set; }
        public string Platform { get; set; }
        public string PublicKeyXml { get; set; }
        public string[] ManagedPaths { get; set; } = new string[0];
        public string[] RequiredFiles { get; set; } = new string[0];
        public string[] ProtectedPaths { get; set; } = new[] { "User", "Settings", "Config", "Logs", "Backups" };
        public long MaximumPackageBytes { get; set; } = 512L * 1024 * 1024;
        public long MaximumExtractedBytes { get; set; } = 2L * 1024 * 1024 * 1024;
        public int MaximumEntries { get; set; } = 10000;
        public int RetryCount { get; set; } = 6;
        public int RetryDelayMilliseconds { get; set; } = 200;
        public Action<string> Status { get; set; }

        internal UpdatePolicy Snapshot()
        {
            return new UpdatePolicy { Product = Product, Platform = Platform, PublicKeyXml = PublicKeyXml,
                ManagedPaths = ManagedPaths == null ? null : (string[])ManagedPaths.Clone(), RequiredFiles = RequiredFiles == null ? null : (string[])RequiredFiles.Clone(),
                ProtectedPaths = ProtectedPaths == null ? null : (string[])ProtectedPaths.Clone(), MaximumPackageBytes = MaximumPackageBytes,
                MaximumExtractedBytes = MaximumExtractedBytes, MaximumEntries = MaximumEntries, RetryCount = RetryCount,
                RetryDelayMilliseconds = RetryDelayMilliseconds, Status = Status };
        }
    }
}
