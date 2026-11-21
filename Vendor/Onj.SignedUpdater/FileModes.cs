using System;
using System.IO;
using System.Reflection;

namespace Onj.SignedUpdater
{
    internal static class FileModes
    {
        internal const int Readable = 420; // Unix 0644.
        internal const int Executable = 493; // Unix 0755.
        private static bool Windows => Environment.OSVersion.Platform == PlatformID.Win32NT;

        internal static int Read(string path)
        {
            if (Windows) return 0;
            var getter = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });
            if (getter == null) throw new PlatformNotSupportedException("This runtime cannot preserve Unix file permissions.");
            var mode = Convert.ToInt32(getter.Invoke(null, new object[] { path }));
            if ((mode & ~511) != 0) throw new InvalidDataException("Privileged Unix permission bits are not supported by this updater.");
            return mode;
        }

        internal static void Apply(string path, int mode)
        {
            if (Windows) return;
            var getter = typeof(File).GetMethod("GetUnixFileMode", new[] { typeof(string) });
            if (getter == null) throw new PlatformNotSupportedException("This runtime cannot preserve Unix file permissions.");
            var setter = typeof(File).GetMethod("SetUnixFileMode", new[] { typeof(string), getter.ReturnType });
            if (setter == null) throw new PlatformNotSupportedException("This runtime cannot set Unix file permissions.");
            setter.Invoke(null, new[] { (object)path, Enum.ToObject(getter.ReturnType, mode) });
        }
    }
}
