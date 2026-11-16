using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LanScreenShare.Core.Display
{
    public sealed record WatchdogCommandOptions
    {
        public int ParentPid { get; init; }
        public string SnapshotPath { get; init; } = string.Empty;
        public string MonitorId { get; init; } = string.Empty;
        public string BackendId { get; init; } = string.Empty;
    }

    public sealed record WatchdogRecoveryContext(
        IReadOnlyList<IVirtualDisplayBackend> Backends,
        IDisplayTopologyManager TopologyManager);

    public static class VirtualDisplayWatchdogRunner
    {
        private sealed class ArgsAccumulator
        {
            public int Pid;
            public string Snapshot = string.Empty;
            public string MonitorId = string.Empty;
            public string BackendId = string.Empty;
        }

        public static bool TryParseArgs(string[] args, out WatchdogCommandOptions options)
        {
            options = new WatchdogCommandOptions();
            if (args.Length == 0)
                return false;

            var acc = new ArgsAccumulator();
            for (int i = 0; i < args.Length - 1; i++)
            {
                ApplyArgPair(acc, args[i], args[i + 1]);
            }

            if (acc.Pid <= 0 || string.IsNullOrWhiteSpace(acc.Snapshot))
                return false;

            options = new WatchdogCommandOptions
            {
                ParentPid = acc.Pid,
                SnapshotPath = acc.Snapshot,
                MonitorId = acc.MonitorId,
                BackendId = acc.BackendId
            };
            return true;
        }

        private static void ApplyArgPair(ArgsAccumulator acc, string key, string value)
        {
            if (string.Equals(key, "--parent-pid", StringComparison.OrdinalIgnoreCase) && int.TryParse(value, out int parsed))
            {
                acc.Pid = parsed;
                return;
            }
            if (string.Equals(key, "--snapshot", StringComparison.OrdinalIgnoreCase))
            {
                acc.Snapshot = value;
                return;
            }
            if (string.Equals(key, "--monitor-id", StringComparison.OrdinalIgnoreCase))
            {
                acc.MonitorId = value;
                return;
            }
            if (string.Equals(key, "--backend", StringComparison.OrdinalIgnoreCase))
            {
                acc.BackendId = value;
            }
        }

        public static async Task<bool> WaitAndRecoverAsync(
            WatchdogCommandOptions options,
            Func<int, CancellationToken, Task> waitForParentExitAsync,
            WatchdogRecoveryContext recoveryContext,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(options);
            ArgumentNullException.ThrowIfNull(waitForParentExitAsync);
            ArgumentNullException.ThrowIfNull(recoveryContext);

            try
            {
                await waitForParentExitAsync(options.ParentPid, ct);
            }
            catch (ArgumentException)
            {
                // Parent process has already exited
            }

            return await ExecuteRecoveryIfOrphanedAsync(options, recoveryContext, ct);
        }

        public static async Task<bool> ExecuteRecoveryIfOrphanedAsync(
            WatchdogCommandOptions options,
            WatchdogRecoveryContext recoveryContext,
            CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(recoveryContext);
            var snapshot = await DisplayTopologySnapshot.TryLoadFromFileAsync(options.SnapshotPath, ct);
            if (snapshot == null)
                return false;

            string backendId = ResolveString(options.BackendId, snapshot.BackendId);
            string monitorId = ResolveString(options.MonitorId, snapshot.VirtualMonitorId);

            if (!string.IsNullOrWhiteSpace(monitorId))
            {
                var matching = recoveryContext.Backends.FirstOrDefault(b =>
                    string.Equals(b.Capabilities.BackendId, backendId, StringComparison.OrdinalIgnoreCase));
                if (matching != null)
                {
                    await TryDestroyQuietlyAsync(matching, monitorId, ct);
                }
            }

            recoveryContext.TopologyManager.RestoreSnapshot(snapshot);
            DisplayTopologySnapshot.DeleteFileIfExists(options.SnapshotPath);
            return true;
        }

        public static async Task WaitForProcessIdExitAsync(int pid, CancellationToken ct)
        {
            using var proc = Process.GetProcessById(pid);
            if (!proc.HasExited)
            {
                await proc.WaitForExitAsync(ct);
            }
        }

        private static string ResolveString(string primary, string? fallback) =>
            !string.IsNullOrWhiteSpace(primary) ? primary : fallback ?? string.Empty;

        private static async Task TryDestroyQuietlyAsync(
            IVirtualDisplayBackend backend,
            string monitorId,
            CancellationToken ct)
        {
            try
            {
                await backend.DestroyVirtualMonitorAsync(monitorId, ct);
            }
            catch
            {
                // Best-effort cleanup after unclean exit
            }
        }
    }
}
