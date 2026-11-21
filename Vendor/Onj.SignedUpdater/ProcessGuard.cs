using System;
using System.Diagnostics;
using System.IO;
using System.Threading;

namespace Onj.SignedUpdater
{
    public static class ProcessGuard
    {
        public static void WaitForExit(int processId, string expectedExecutable, DateTime expectedStartUtc, TimeSpan timeout, CancellationToken cancellation)
        {
            if (processId <= 0 || timeout < TimeSpan.Zero || timeout > TimeSpan.FromMinutes(5)) throw new ArgumentException("Invalid process wait parameters.");
            Process process;
            try { process = Process.GetProcessById(processId); } catch (ArgumentException) { return; }
            using (process)
            {
                if (process.HasExited) return;
                if (!Path.GetFullPath(process.MainModule.FileName).Equals(Path.GetFullPath(expectedExecutable), StringComparison.OrdinalIgnoreCase) ||
                    process.StartTime.ToUniversalTime() != expectedStartUtc.ToUniversalTime()) throw new InvalidOperationException("The process identity changed. No process was stopped.");
                var watch = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    cancellation.ThrowIfCancellationRequested();
                    if (watch.Elapsed >= timeout) throw new TimeoutException("The application did not exit. No program files were changed.");
                    process.WaitForExit((int)Math.Min(100, Math.Max(1, (timeout - watch.Elapsed).TotalMilliseconds)));
                }
            }
        }
    }
}
