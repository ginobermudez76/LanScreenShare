using System.Diagnostics;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Security;
using LanScreenShare.Media;
using LanScreenShare.Media.SecureAgent;

namespace LanScreenShare.SecureHost;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (!SecureHostCliOptions.TryParseArgs(args, out var options))
            return 1;

        SecureDesktopPolicySanitizer.RestoreIfDisabled();

        if (options.Mode == SecureHostRunMode.Service)
        {
            if (!OperatingSystem.IsWindows())
                return 1;

            WindowsServiceDispatcher.Run(options.PipeName);
            return 0;
        }

        using var cts = new CancellationTokenSource();
        Task parentMonitor = StartParentMonitorIfRequested(options.ParentPid, cts);

        await using var server = new SecureAgentPipeServer(
            CaptureServiceFactory.Create,
            CreatePlatformInputSimulator,
            options.PipeName);

        await server.RunAsync(cts.Token).ConfigureAwait(false);
        await cts.CancelAsync().ConfigureAwait(false);
        try { await parentMonitor.ConfigureAwait(false); } catch { /* Best effort */ }
        return 0;
    }

    private static IInputSimulatorService CreatePlatformInputSimulator() =>
        OperatingSystem.IsWindows()
            ? new WindowsInputSimulatorService()
            : new NullInputSimulatorService();

    private static Task StartParentMonitorIfRequested(int parentPid, CancellationTokenSource cts)
    {
        if (parentPid <= 0)
            return Task.CompletedTask;

        return Task.Run(async () =>
        {
            try
            {
                using var parent = Process.GetProcessById(parentPid);
                await parent.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            }
            catch
            {
                // El proceso padre ya finalizó
            }
            finally
            {
                await cts.CancelAsync().ConfigureAwait(false);
            }
        }, CancellationToken.None);
    }

    private sealed class NullInputSimulatorService : IInputSimulatorService
    {
        public void SimulateInput(LanScreenShare.Core.Models.RemoteInputMessage input) { }
        public void SetTargetSource(LanScreenShare.Core.Models.CaptureSource source) { }
        public void ReleaseInjectedInputs() { }
        public void Dispose() { }
    }
}
