using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Media.Display;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

internal sealed class FakeVddBackend : IVirtualDisplayBackend
{
    public bool Installed { get; set; } = true;
    public bool FailCreate { get; set; }
    public bool FailUpdateMode { get; set; }
    public int CreateCalls { get; private set; }
    public int DestroyCalls { get; private set; }
    public int HeartbeatCalls { get; private set; }
    public string? LastDestroyedMonitorId { get; private set; }

    public VirtualDisplayBackendCapabilities Capabilities { get; init; } = new()
    {
        BackendId = "fake-vdd",
        HasNativeKernelWatchdog = true,
        NativeWatchdogTimeout = TimeSpan.FromSeconds(1),
        SupportsDynamicModeChange = true
    };

    public bool IsDriverInstalled() => Installed;

    public Task<VirtualDisplayInstance?> CreateVirtualMonitorAsync(VirtualDisplayMode mode, CancellationToken ct = default)
    {
        CreateCalls++;
        if (!Installed || FailCreate)
            return Task.FromResult<VirtualDisplayInstance?>(null);

        return Task.FromResult<VirtualDisplayInstance?>(new VirtualDisplayInstance
        {
            BackendId = Capabilities.BackendId,
            MonitorId = "virt-1",
            DeviceName = @"\\.\DISPLAY9",
            Mode = mode,
            MonitorIndex = 1
        });
    }

    public Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default) =>
        Task.FromResult(!FailUpdateMode);

    public Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default)
    {
        DestroyCalls++;
        LastDestroyedMonitorId = monitorId;
        return Task.FromResult(true);
    }

    public Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default)
    {
        HeartbeatCalls++;
        return Task.FromResult(true);
    }
}

internal sealed class FakeTopologyManager : IDisplayTopologyManager
{
    public DisplayTopologySnapshot SnapshotToCapture { get; set; } = new()
    {
        Paths =
        [
            new DisplayPathSnapshot
            {
                AdapterLuidLow = 1,
                SourceId = 0,
                TargetId = 0,
                Width = 1920,
                Height = 1080,
                RefreshRateNumerator = 60,
                RefreshRateDenominator = 1,
                DeviceName = @"\\.\DISPLAY1"
            }
        ]
    };

    public bool FailApplyExtend { get; set; }
    public int ApplyExtendCalls { get; private set; }
    public int RestoreCalls { get; private set; }
    public VirtualDisplayMode? LastAppliedMode { get; private set; }

    public DisplayTopologySnapshot CaptureSnapshot() => SnapshotToCapture;

    public bool ApplyExtendTopology(string? preferredDeviceName, VirtualDisplayMode mode)
    {
        ApplyExtendCalls++;
        LastAppliedMode = mode;
        return !FailApplyExtend;
    }

    public bool RestoreSnapshot(DisplayTopologySnapshot snapshot)
    {
        RestoreCalls++;
        return true;
    }
}

internal sealed class FakeWatchdogLauncher : IWatchdogProcessLauncher
{
    public int LaunchCount { get; private set; }
    public int DisposedCount { get; private set; }
    public VirtualDisplayWatchdogLaunchOptions? LastOptions { get; private set; }

    public IDisposable StartWatchdog(VirtualDisplayWatchdogLaunchOptions options)
    {
        LaunchCount++;
        LastOptions = options;
        return new TrackingDisposable(this);
    }

    private sealed class TrackingDisposable(FakeWatchdogLauncher owner) : IDisposable
    {
        public void Dispose() => owner.DisposedCount++;
    }
}

[TestClass]
public class VirtualDisplayInfrastructureTests
{
    private static string CreateTempSnapshotPath(string prefix) =>
        Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}", "topology_backup.json");

    private static void CleanupSnapshotDir(string snapshotPath)
    {
        string? dir = Path.GetDirectoryName(snapshotPath);
        if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* Best-effort test cleanup */ }
        }
    }

    [TestMethod]
    public void ModeValidator_EnforcesWhitelistAndSelectsBestSupportedMode()
    {
        var tooSmall = new VirtualDisplayMode(320, 240, 60);
        var tooFast = new VirtualDisplayMode(1920, 1080, 240);
        var hd = new VirtualDisplayMode(1280, 720, 60);
        var fhd = new VirtualDisplayMode(1920, 1080, 60);
        var uhd = new VirtualDisplayMode(3840, 2160, 60);

        var bestWithoutPreference = VirtualDisplayModeValidator.SelectBestSupportedMode([tooSmall, hd, fhd, tooFast]);
        var bestWithPreference = VirtualDisplayModeValidator.SelectBestSupportedMode([hd, fhd, uhd], preferred: fhd);
        var noneValid = VirtualDisplayModeValidator.SelectBestSupportedMode([tooSmall, tooFast]);

        Assert.AreEqual((false, false, true), (VirtualDisplayModeValidator.IsSupported(tooSmall), VirtualDisplayModeValidator.IsSupported(tooFast), VirtualDisplayModeValidator.IsSupported(uhd)));
        Assert.AreEqual((fhd, fhd, null), (bestWithoutPreference, bestWithPreference, noneValid));
    }

    [TestMethod]
    public async Task Coordinator_LifecycleSingleSessionModeChangeAndRollback_WorksCorrectly()
    {
        string snapshotPath = CreateTempSnapshotPath("vdd-life");
        try
        {
            var backend = new FakeVddBackend();
            var topology = new FakeTopologyManager();
            var watchdog = new FakeWatchdogLauncher();
            var options = new VirtualDisplayCoordinatorOptions
            {
                SnapshotFilePath = snapshotPath,
                HeartbeatInterval = TimeSpan.FromMilliseconds(20)
            };

            await using var coordinator = new VirtualDisplayCoordinator([backend], topology, watchdog, options);
            var badModeResult = await coordinator.StartExtendedDisplayAsync(new VirtualDisplayMode(100, 100, 60));
            var okResult = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);
            await Task.Delay(70);
            var busyResult = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

            var modeChange = await coordinator.ChangeDisplayModeAsync(new VirtualDisplayMode(2560, 1440, 60));
            bool backupExistedWhileActive = File.Exists(snapshotPath);
            await coordinator.StopExtendedDisplayAsync();

            Assert.AreEqual(("ERROR_INVALID_MODE", true, "ERROR_DISPLAY_BUSY"), (badModeResult.ErrorCode, okResult.Success, busyResult.ErrorCode));
            Assert.AreEqual((true, 2560, true), (modeChange.Success, coordinator.CurrentSession == null ? topology.LastAppliedMode?.Width ?? 0 : 0, backupExistedWhileActive));
            Assert.AreEqual((VirtualDisplayState.Inactive, 1, 1, 1, 1, false), (coordinator.State, backend.DestroyCalls, topology.RestoreCalls, watchdog.LaunchCount, watchdog.DisposedCount, File.Exists(snapshotPath)));
            Assert.IsTrue(backend.HeartbeatCalls >= 1);
        }
        finally
        {
            CleanupSnapshotDir(snapshotPath);
        }
    }

    [TestMethod]
    public async Task Coordinator_WhenExtendFailsOrDriverMissing_RollsBackCleanly()
    {
        string snapshotPath = CreateTempSnapshotPath("vdd-rollback");
        try
        {
            var backend = new FakeVddBackend { Installed = false };
            var topology = new FakeTopologyManager { FailApplyExtend = true };
            var options = new VirtualDisplayCoordinatorOptions { SnapshotFilePath = snapshotPath };

            await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, options);
            var noDriver = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

            backend.Installed = true;
            var topoFail = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

            Assert.AreEqual(("ERROR_DRIVER_NOT_INSTALLED", "ERROR_TOPOLOGY_FAILED"), (noDriver.ErrorCode, topoFail.ErrorCode));
            Assert.AreEqual((VirtualDisplayState.Inactive, 1, 1, false), (coordinator.State, backend.DestroyCalls, topology.RestoreCalls, File.Exists(snapshotPath)));
        }
        finally
        {
            CleanupSnapshotDir(snapshotPath);
        }
    }

    [TestMethod]
    public async Task Coordinator_NetworkDropReconnectTimeoutAndSuspendResume_PreserveOrRecoverDisplay()
    {
        string snapshotPath = CreateTempSnapshotPath("vdd-net");
        try
        {
            var backend = new FakeVddBackend();
            var topology = new FakeTopologyManager();
            var options = new VirtualDisplayCoordinatorOptions
            {
                SnapshotFilePath = snapshotPath,
                ReconnectTimeout = TimeSpan.FromMilliseconds(90),
                AutoEnterStreamingState = false
            };

            await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, options);
            await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);
            var connectingState = coordinator.State;
            await coordinator.NotifyStreamConnectedAsync();

            await coordinator.HandleNetworkDisconnectedAsync();
            var reconnectingState = coordinator.State;
            await coordinator.HandleNetworkReconnectedAsync();
            var restoredStreaming = coordinator.State;

            await coordinator.HandleSystemSuspendAsync();
            var suspendedState = coordinator.State;
            await coordinator.HandleSystemResumeAsync();
            var resumedState = coordinator.State;

            await coordinator.HandleNetworkDisconnectedAsync();
            await Task.Delay(260);

            Assert.AreEqual((VirtualDisplayState.ConnectingStream, VirtualDisplayState.Reconnecting, VirtualDisplayState.Streaming), (connectingState, reconnectingState, restoredStreaming));
            Assert.AreEqual((VirtualDisplayState.Suspended, VirtualDisplayState.Streaming, VirtualDisplayState.Inactive), (suspendedState, resumedState, coordinator.State));
            Assert.AreEqual((1, 1), (backend.DestroyCalls, topology.RestoreCalls));
        }
        finally
        {
            CleanupSnapshotDir(snapshotPath);
        }
    }

    [TestMethod]
    public async Task WatchdogRunnerAndStartupRecovery_CleanOrphanedSessionsOnlyWhenSnapshotExists()
    {
        string snapshotPath = CreateTempSnapshotPath("vdd-orphan");
        try
        {
            var topology = new FakeTopologyManager();
            var snapshot = topology.CaptureSnapshot() with
            {
                BackendId = "fake-vdd",
                VirtualMonitorId = "virt-99",
                VirtualDeviceName = @"\\.\DISPLAY9"
            };
            await snapshot.SaveToFileAsync(snapshotPath);

            var backend = new FakeVddBackend();
            var recoveryContext = new WatchdogRecoveryContext([backend], topology);
            bool parsedOk = VirtualDisplayWatchdogRunner.TryParseArgs(
                ["--parent-pid", "999999", "--snapshot", snapshotPath, "--monitor-id", "virt-99", "--backend", "fake-vdd"],
                out var cmdOptions);

            bool recovered = await VirtualDisplayWatchdogRunner.WaitAndRecoverAsync(
                cmdOptions,
                (_, _) => Task.CompletedTask,
                recoveryContext);

            bool recoveredSecondTime = await VirtualDisplayWatchdogRunner.ExecuteRecoveryIfOrphanedAsync(cmdOptions, recoveryContext);

            await snapshot.SaveToFileAsync(snapshotPath);
            await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, new VirtualDisplayCoordinatorOptions
            {
                SnapshotFilePath = snapshotPath
            });
            await coordinator.RecoverOrphanedSessionOnStartupAsync();

            Assert.AreEqual((true, true, false, false), (parsedOk, recovered, recoveredSecondTime, File.Exists(snapshotPath)));
            Assert.AreEqual((2, 2, "virt-99"), (backend.DestroyCalls, topology.RestoreCalls, backend.LastDestroyedMonitorId));
            Assert.IsFalse(VirtualDisplayWatchdogRunner.TryParseArgs(["--parent-pid", "abc"], out _));
        }
        finally
        {
            CleanupSnapshotDir(snapshotPath);
        }
    }

    [TestMethod]
    public void VddSettingsXmlHelper_PreservesExistingOptionsAndAvoidsDuplicateModes()
    {
        const string initialXml = """
            <vdd_settings>
              <monitors><count>0</count></monitors>
              <options><HardwareCursor>true</HardwareCursor></options>
            </vdd_settings>
            """;

        string updated = VddSettingsXmlHelper.UpdateMonitorCountAndMode(initialXml, 1, new VirtualDisplayMode(2560, 1440, 60));
        string updatedTwice = VddSettingsXmlHelper.UpdateMonitorCountAndMode(updated, 2, new VirtualDisplayMode(2560, 1440, 60));

        Assert.AreEqual((0, 2), (VddSettingsXmlHelper.ReadMonitorCount(initialXml), VddSettingsXmlHelper.ReadMonitorCount(updatedTwice)));
        Assert.IsTrue(updatedTwice.Contains("<HardwareCursor>true</HardwareCursor>", StringComparison.Ordinal));
        Assert.AreEqual(("SETDISPLAYCOUNT 2", "RELOAD_DRIVER"), (VddSettingsXmlHelper.BuildSetDisplayCountCommand(2), VddSettingsXmlHelper.BuildReloadDriverCommand()));
    }

    [TestMethod]
    public async Task VddAndParsecBackends_WithFakeTransports_CreateUpdateDestroyAndRollbackCleanly()
    {
        var pipeTransport = new FakeVddPipeTransport();
        var env = new FakeVddEnvironment { Present = true };
        var vddBackend = new VirtualDriversVddBackend(pipeTransport, env);

        var vddMonitor = await vddBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        bool vddUpdated = await vddBackend.UpdateModeAsync(vddMonitor!.MonitorId, new VirtualDisplayMode(2560, 1440, 60));
        bool vddDestroyed = await vddBackend.DestroyVirtualMonitorAsync(vddMonitor.MonitorId);
        string? commandAfterDestroy = pipeTransport.LastCommand;

        pipeTransport.ShouldFail = true;
        var failedCreate = await vddBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);

        var parsecTransport = new FakeParsecTransport { Installed = true, NextAddIndex = 3 };
        using var parsecBackend = new ParsecVddBackend(parsecTransport);
        var parsecMonitor = await parsecBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        bool heartbeatOk = await parsecBackend.SendHeartbeatAsync(parsecMonitor!.MonitorId);
        bool parsecDestroyed = await parsecBackend.DestroyVirtualMonitorAsync(parsecMonitor.MonitorId);

        Assert.AreEqual((true, true, null), (vddUpdated, vddDestroyed, failedCreate));
        Assert.AreEqual((0, "SETDISPLAYCOUNT 0"), (VddSettingsXmlHelper.ReadMonitorCount(env.Xml ?? string.Empty), commandAfterDestroy));
        Assert.AreEqual(("3", true, true, 3), (parsecMonitor.MonitorId, heartbeatOk, parsecDestroyed, parsecTransport.LastRemovedIndex));
    }

    [TestMethod]
    public void WindowsTopologyMapper_UpdatesTargetPathWithoutMutatingOthers()
    {
        var adapter = new FakeCcdAdapter
        {
            Paths =
            [
                new DisplayPathSnapshot { DeviceName = @"\\.\DISPLAY1", Width = 1920, Height = 1080 },
                new DisplayPathSnapshot { DeviceName = @"\\.\DISPLAY2", Width = 1280, Height = 720 }
            ]
        };

        var manager = new WindowsDisplayTopologyManager(adapter);
        var snap = manager.CaptureSnapshot();
        bool applied = manager.ApplyExtendTopology(@"\\.\DISPLAY2", new VirtualDisplayMode(2560, 1440, 60));
        var extendedMode = (adapter.LastApplied![1].Width, adapter.LastApplied[1].Height, adapter.ExtendCalls);
        bool restored = manager.RestoreSnapshot(snap);

        Assert.AreEqual((2, true, true), (snap.Paths.Count, applied, restored));
        Assert.AreEqual((2560, 1440, 1), extendedMode);
        Assert.AreEqual((1280, 720), (adapter.LastApplied![1].Width, adapter.LastApplied[1].Height));
    }

    [TestMethod]
    public async Task WindowsPlatformAdapters_ExecuteSafelyAgainstTempPathsAndNamedPipe()
    {
        if (!OperatingSystem.IsWindows())
            return;

        string tempDir = Path.Combine(Path.GetTempPath(), $"vdd-win-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string settingsPath = Path.Combine(tempDir, "vdd_settings.xml");
            var env = new WindowsVddEnvironment(settingsPath, Path.Combine(tempDir, "missing.inf"));
            env.WriteSettingsXml(VddSettingsXmlHelper.UpdateMonitorCountAndMode(null, 1, VirtualDisplayMode.Default));
            string? readBack = env.ReadSettingsXml();

            string pipeName = $"LssTestPipe_{Guid.NewGuid():N}";
            string? receivedCommand = null;
            using var server = new NamedPipeServerStream(pipeName, PipeDirection.In);
            var acceptTask = Task.Run(async () =>
            {
                await server.WaitForConnectionAsync();
                using var reader = new StreamReader(server, Encoding.Unicode);
                receivedCommand = await reader.ReadLineAsync();
            });

            var pipeClient = new NamedPipeVddTransport(pipeName, connectTimeoutMs: 1500);
            bool sent = await pipeClient.SendCommandAsync("SETDISPLAYCOUNT 1");
            await acceptTask.WaitAsync(TimeSpan.FromSeconds(3));

            var realTopology = new WindowsDisplayTopologyManager();
            var realSnap = realTopology.CaptureSnapshot();
            string args = ProcessWatchdogLauncher.BuildArguments(new VirtualDisplayWatchdogLaunchOptions
            {
                ParentPid = 123,
                SnapshotPath = settingsPath,
                MonitorId = "1",
                BackendId = VirtualDriversVddBackend.Id
            });

            Assert.AreEqual((true, "SETDISPLAYCOUNT 1"), (sent && readBack != null, receivedCommand));
            Assert.IsTrue(realSnap.Paths.Count >= 0);
            Assert.IsTrue(args.Contains("--parent-pid 123", StringComparison.Ordinal));
        }
        finally
        {
            CleanupSnapshotDir(Path.Combine(tempDir, "dummy"));
        }
    }

    private sealed class FakeVddPipeTransport : IVddPipeTransport
    {
        public bool ShouldFail { get; set; }
        public string? LastCommand { get; private set; }

        public Task<bool> SendCommandAsync(string command, CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(!ShouldFail);
        }
    }

    private sealed class FakeVddEnvironment : IVddEnvironment
    {
        public bool Present { get; set; }
        public string? Xml { get; set; }

        public bool IsDriverPresent() => Present;
        public string? ReadSettingsXml() => Xml;
        public void WriteSettingsXml(string xmlContent) => Xml = xmlContent;
    }

    private sealed class FakeParsecTransport : IParsecDeviceTransport
    {
        public bool Installed { get; set; }
        public int NextAddIndex { get; set; }
        public int LastRemovedIndex { get; private set; } = -1;

        public bool IsDriverInstalled() => Installed;
        public bool TryOpenDevice() => Installed;
        public int AddDisplay() => NextAddIndex;

        public bool RemoveDisplay(int displayIndex)
        {
            LastRemovedIndex = displayIndex;
            return true;
        }

        public bool SendUpdateHeartbeat() => true;
        public void CloseDevice() { }
        public void Dispose() { }
    }

    private sealed class FakeCcdAdapter : ICcdPlatformAdapter
    {
        public IReadOnlyList<DisplayPathSnapshot> Paths { get; set; } = Array.Empty<DisplayPathSnapshot>();
        public IReadOnlyList<DisplayPathSnapshot>? LastApplied { get; private set; }
        public int ExtendCalls { get; private set; }

        public bool TryQueryActiveDisplayPaths(out IReadOnlyList<DisplayPathSnapshot> paths)
        {
            paths = Paths;
            return true;
        }

        public bool ApplyExtendTopology()
        {
            ExtendCalls++;
            return true;
        }

        public bool ApplySuppliedConfig(IReadOnlyList<DisplayPathSnapshot> paths)
        {
            LastApplied = paths;
            return true;
        }
    }
}
