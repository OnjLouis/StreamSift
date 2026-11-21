using System;
using System.IO;
using System.Linq;

namespace Onj.SignedUpdater
{
    internal static class SafePaths
    {
        internal const string Receipt = ".onj-update-manifest.json";
        internal const string ReceiptSignature = ".onj-update-manifest.sig";
        internal const string Pending = ".onj-update-pending.json";
        internal const string Lock = ".onj-update.lock";

        internal static string Normalize(string path)
        {
            if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains("\\") || path.StartsWith("/", StringComparison.Ordinal))
                throw new InvalidDataException("An update path is unsafe.");
            foreach (var part in path.Split('/'))
            {
                if (part.Length == 0 || part == "." || part == ".." || part.Length > 255 || part.EndsWith(".", StringComparison.Ordinal) || part.EndsWith(" ", StringComparison.Ordinal) ||
                    part.Any(c => c < 32 || "<>:\"|?*".IndexOf(c) >= 0)) throw new InvalidDataException("An update path is unsafe.");
                var stem = part.Split('.')[0].ToUpperInvariant();
                if (new[] { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$" }.Contains(stem) ||
                    (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789".Contains(stem[3])) ||
                    stem.StartsWith(".ONJ-UPDATE", StringComparison.Ordinal)) throw new InvalidDataException("An update path uses a reserved name.");
            }
            return path;
        }

        internal static bool Within(string path, string prefix)
        {
            return path.Equals(prefix, StringComparison.OrdinalIgnoreCase) || path.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase);
        }

        internal static string Root(string path)
        {
            var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (string.Equals(full, Path.GetPathRoot(full).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("A filesystem root cannot be an update folder.");
            NoLinks(full);
            return full;
        }

        internal static string At(string root, string relative)
        {
            var result = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!result.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("An update path escaped its root.");
            NoLinks(result);
            return result;
        }

        internal static void NoLinks(string path)
        {
            var full = Path.GetFullPath(path);
            var current = Path.GetPathRoot(full);
            foreach (var part in full.Substring(current.Length).Split(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar }, StringSplitOptions.RemoveEmptyEntries))
            {
                current = Path.Combine(current, part);
                try
                {
                    if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Filesystem links are not allowed in update paths.");
                }
                catch (FileNotFoundException) { }
                catch (DirectoryNotFoundException) { }
            }
        }

        internal static void Separate(string left, string right)
        {
            if (left.Equals(right, StringComparison.OrdinalIgnoreCase) || left.StartsWith(right + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                right.StartsWith(left + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Install, scratch and backup folders must be separate, non-nested folders.");
        }

        internal static void Managed(string path, UpdatePolicy policy)
        {
            Normalize(path);
            if (!policy.ManagedPaths.Any(p => Within(path, p)) || policy.ProtectedPaths.Any(p => Within(path, p)))
                throw new InvalidDataException("The package attempted to change an unmanaged or protected path: " + path);
        }

        internal static void ValidatePolicy(UpdatePolicy policy)
        {
            if (policy == null || string.IsNullOrWhiteSpace(policy.Product) || string.IsNullOrWhiteSpace(policy.Platform) ||
                policy.ManagedPaths == null || policy.ManagedPaths.Length == 0 || policy.RequiredFiles == null || policy.RequiredFiles.Length == 0 || policy.ProtectedPaths == null ||
                policy.MaximumPackageBytes <= 0 || policy.MaximumExtractedBytes <= 0 || policy.MaximumEntries <= 0 || policy.MaximumEntries > 10000 ||
                policy.RetryCount < 1 || policy.RetryCount > 30 || policy.RetryDelayMilliseconds < 0 || policy.RetryDelayMilliseconds > 5000)
                throw new ArgumentException("The update policy is incomplete or has invalid limits.");
            foreach (var path in policy.ManagedPaths.Concat(policy.RequiredFiles).Concat(policy.ProtectedPaths)) Normalize(path);
            foreach (var path in policy.RequiredFiles) Managed(path, policy);
        }
    }
}
