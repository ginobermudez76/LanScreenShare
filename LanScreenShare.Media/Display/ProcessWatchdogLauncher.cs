using System;
using System.Diagnostics;
using System.IO;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Media.Display
{
    public sealed class ProcessWatchdogLauncher : IWatchdogProcessLauncher
    {
        public const string ExecutableName = "LanScreenShare.DisplayWatchdog.exe";

        private readonly string? _executablePathOverride;

        public ProcessWatchdogLauncher(string? executablePathOverride = null)
        {
            _executablePathOverride = executablePathOverride;
        }

        public static string ResolveExecutablePath(string? baseDir = null)
        {
            string root = baseDir ?? AppContext.BaseDirectory;
            return Path.Combine(root, ExecutableName);
        }

        public static string BuildArguments(VirtualDisplayWatchdogLaunchOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            return $"--parent-pid {options.ParentPid} --snapshot \"{options.SnapshotPath}\" --monitor-id \"{options.MonitorId}\" --backend \"{options.BackendId}\"";
        }

        public IDisposable? StartWatchdog(VirtualDisplayWatchdogLaunchOptions options)
        {
            ArgumentNullException.ThrowIfNull(options);
            string exePath = _executablePathOverride ?? ResolveExecutablePath();
            if (!File.Exists(exePath))
                return null;

            try
            {
                var startInfo = new ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = BuildArguments(options),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };

                var proc = Process.Start(startInfo);
                return proc != null ? new WatchdogProcessHandle(proc) : null;
            }
            catch
            {
                return null;
            }
        }

        private sealed class WatchdogProcessHandle(Process process) : IDisposable
        {
            private bool _disposed;

            public void Dispose()
            {
                if (_disposed)
                    return;

                _disposed = true;
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                    }
                }
                catch
                {
                    // Watchdog already exited
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
