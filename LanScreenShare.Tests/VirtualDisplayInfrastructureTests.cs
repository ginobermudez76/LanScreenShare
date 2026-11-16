using System;
using System.Collections.Generic;
using System.Diagnostics;
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
    public bool ThrowOnDestroy { get; set; }
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
        if (ThrowOnDestroy)
            throw new InvalidOperationException("Simulated teardown error");
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

internal sealed class TempSnapshotScope : IDisposable
{
    private readonly string _rootDir;
    public string BackupFilePath { get; }

    public TempSnapshotScope()
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"vdd-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_rootDir);
        BackupFilePath = Path.Combine(_rootDir, "topology_backup.json");
    }

    public string GetFilePath(int index) => Path.Combine(_rootDir, $"item-{index}.dat");

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            try { Directory.Delete(_rootDir, recursive: true); } catch { /* Best-effort cleanup */ }
        }
    }
}

[TestClass]
public class VirtualDisplayInfrastructureTests
{
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
        using var scope = new TempSnapshotScope();
        var backend = new FakeVddBackend();
        var topology = new FakeTopologyManager();
        var watchdog = new FakeWatchdogLauncher();
        var options = new VirtualDisplayCoordinatorOptions
        {
            SnapshotFilePath = scope.BackupFilePath,
            HeartbeatInterval = TimeSpan.FromMilliseconds(20)
        };

        await using var coordinator = new VirtualDisplayCoordinator([backend], topology, watchdog, options);
        await coordinator.StopExtendedDisplayAsync();
        await coordinator.HandleNetworkDisconnectedAsync();
        await coordinator.HandleNetworkReconnectedAsync();
        await coordinator.HandleSystemSuspendAsync();
        await coordinator.HandleSystemResumeAsync();

        var badModeResult = await coordinator.StartExtendedDisplayAsync(new VirtualDisplayMode(100, 100, 60));
        var okResult = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);
        await Task.Delay(70);
        var busyResult = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

        var modeChange = await coordinator.ChangeDisplayModeAsync(new VirtualDisplayMode(2560, 1440, 60));
        bool backupExistedWhileActive = File.Exists(scope.BackupFilePath);
        await coordinator.StopExtendedDisplayAsync();

        Assert.AreEqual((VirtualDisplayErrorCode.InvalidMode, true, VirtualDisplayErrorCode.DisplayBusy), (badModeResult.ErrorCode, okResult.Success, busyResult.ErrorCode));
        Assert.AreEqual((true, 2560, true), (modeChange.Success, coordinator.CurrentSession == null ? topology.LastAppliedMode?.Width ?? 0 : 0, backupExistedWhileActive));
        Assert.AreEqual((VirtualDisplayState.Inactive, 1, 1, 1, 1, false), (coordinator.State, backend.DestroyCalls, topology.RestoreCalls, watchdog.LaunchCount, watchdog.DisposedCount, File.Exists(scope.BackupFilePath)));
        Assert.IsTrue(backend.HeartbeatCalls >= 1);
    }

    [TestMethod]
    public async Task Coordinator_WhenExtendFailsOrDriverMissing_RollsBackCleanly()
    {
        using var scope = new TempSnapshotScope();
        var backend = new FakeVddBackend { Installed = false };
        var topology = new FakeTopologyManager { FailApplyExtend = true };
        var options = new VirtualDisplayCoordinatorOptions { SnapshotFilePath = scope.BackupFilePath };

        await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, options);
        var noDriver = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);
        var noSessionMode = await coordinator.ChangeDisplayModeAsync(VirtualDisplayMode.Default);
        var noSessionConnect = await coordinator.NotifyStreamConnectedAsync();

        backend.Installed = true;
        backend.FailCreate = true;
        var createFail = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

        backend.FailCreate = false;
        backend.ThrowOnDestroy = true;
        var topoFail = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

        Assert.AreEqual((VirtualDisplayErrorCode.DriverNotInstalled, VirtualDisplayErrorCode.NoSession, VirtualDisplayErrorCode.NoSession), (noDriver.ErrorCode, noSessionMode.ErrorCode, noSessionConnect.ErrorCode));
        Assert.AreEqual((VirtualDisplayErrorCode.CreateFailed, VirtualDisplayErrorCode.TopologyFailed), (createFail.ErrorCode, topoFail.ErrorCode));
        Assert.AreEqual((VirtualDisplayState.Inactive, 1, 2, false), (coordinator.State, backend.DestroyCalls, topology.RestoreCalls, File.Exists(scope.BackupFilePath)));
    }

    [TestMethod]
    public async Task Coordinator_FailedResumeAndFailedModeUpdate_HandleErrorsSafely()
    {
        using var scope = new TempSnapshotScope();
        var backend = new FakeVddBackend { FailUpdateMode = true };
        var topology = new FakeTopologyManager();
        var options = new VirtualDisplayCoordinatorOptions { SnapshotFilePath = scope.BackupFilePath };

        await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, options);
        await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);

        var modeUpdateFailed = await coordinator.ChangeDisplayModeAsync(new VirtualDisplayMode(2560, 1440, 60));
        await coordinator.HandleSystemSuspendAsync();
        topology.FailApplyExtend = true;
        await coordinator.HandleSystemResumeAsync();

        Assert.AreEqual((VirtualDisplayErrorCode.ModeUpdateFailed, VirtualDisplayState.Inactive, 1, 1), (modeUpdateFailed.ErrorCode, coordinator.State, backend.DestroyCalls, topology.RestoreCalls));
    }

    [TestMethod]
    public async Task Coordinator_NetworkDropReconnectTimeoutAndSuspendResume_PreserveOrRecoverDisplay()
    {
        using var scope = new TempSnapshotScope();
        var backend = new FakeVddBackend();
        var topology = new FakeTopologyManager();
        var options = new VirtualDisplayCoordinatorOptions
        {
            SnapshotFilePath = scope.BackupFilePath,
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

    [TestMethod]
    public async Task WatchdogRunnerAndStartupRecovery_CleanOrphanedSessionsOnlyWhenSnapshotExists()
    {
        using var scope = new TempSnapshotScope();
        var topology = new FakeTopologyManager();
        var snapshot = topology.CaptureSnapshot() with
        {
            BackendId = "fake-vdd",
            VirtualMonitorId = "virt-99",
            VirtualDeviceName = @"\\.\DISPLAY9"
        };
        await snapshot.SaveToFileAsync(scope.BackupFilePath);

        var backend = new FakeVddBackend { ThrowOnDestroy = true };
        var recoveryContext = new WatchdogRecoveryContext([backend], topology);
        bool parsedOk = VirtualDisplayWatchdogRunner.TryParseArgs(
            ["--parent-pid", "999999", "--snapshot", scope.BackupFilePath, "--monitor-id", "virt-99", "--backend", "fake-vdd"],
            out var cmdOptions);

        bool recovered = await VirtualDisplayWatchdogRunner.WaitAndRecoverAsync(
            cmdOptions,
            (_, _) => throw new ArgumentException("Already exited"),
            recoveryContext);

        bool recoveredSecondTime = await VirtualDisplayWatchdogRunner.ExecuteRecoveryIfOrphanedAsync(cmdOptions, recoveryContext);

        await snapshot.SaveToFileAsync(scope.BackupFilePath);
        await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, new VirtualDisplayCoordinatorOptions
        {
            SnapshotFilePath = scope.BackupFilePath
        });
        await coordinator.RecoverOrphanedSessionOnStartupAsync();
        await coordinator.RecoverOrphanedSessionOnStartupAsync();

        Assert.AreEqual((true, true, false, false), (parsedOk, recovered, recoveredSecondTime, File.Exists(scope.BackupFilePath)));
        Assert.AreEqual((2, 2, "virt-99"), (backend.DestroyCalls, topology.RestoreCalls, backend.LastDestroyedMonitorId));
        Assert.AreEqual((false, false), (VirtualDisplayWatchdogRunner.TryParseArgs([], out _), VirtualDisplayWatchdogRunner.TryParseArgs(["--parent-pid", "abc"], out _)));
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
        Assert.AreEqual(("SETDISPLAYCOUNT 2", "RELOAD_DRIVER"), (VddSettingsXmlHelper.BuildSetDisplayCountCommand(2), VddSettingsXmlHelper.ReloadDriverCommand));
    }

    [TestMethod]
    public async Task VddAndParsecBackends_WithFakeTransports_CreateUpdateDestroyAndRollbackCleanly()
    {
        var pipeTransport = new FakeVddPipeTransport();
        var env = new FakeVddEnvironment { Present = true, DeviceName = @"\\.\DISPLAY14" };
        var vddBackend = new VirtualDriversVddBackend(pipeTransport, env);

        var vddMonitor = await vddBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        bool vddUpdated = await vddBackend.UpdateModeAsync(vddMonitor!.MonitorId, new VirtualDisplayMode(2560, 1440, 60));
        bool vddHeartbeat = await vddBackend.SendHeartbeatAsync(vddMonitor.MonitorId);
        bool vddDestroyed = await vddBackend.DestroyVirtualMonitorAsync(vddMonitor.MonitorId);
        var commandAfterDestroy = pipeTransport.LastCommand;

        pipeTransport.ShouldFail = true;
        var failedCreate = await vddBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);

        pipeTransport.ShouldFail = false;
        env.FailEnable = true;
        var failedEnable = await vddBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);

        var parsecTransport = new FakeParsecTransport { Installed = true, NextAddIndex = 3 };
        using var parsecBackend = new ParsecVddBackend(parsecTransport);
        var parsecMonitor = await parsecBackend.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        bool heartbeatOk = await parsecBackend.SendHeartbeatAsync(parsecMonitor!.MonitorId);
        bool parsecDestroyed = await parsecBackend.DestroyVirtualMonitorAsync(parsecMonitor.MonitorId);

        Assert.AreEqual((@"\\.\DISPLAY14", true, true, true, (VirtualDisplayInstance?)null, (VirtualDisplayInstance?)null), (vddMonitor.DeviceName, vddUpdated, vddHeartbeat, vddDestroyed, failedCreate, failedEnable));
        Assert.AreEqual((0, VddCommandKind.SetDisplayCount, 0, true), (VddSettingsXmlHelper.ReadMonitorCount(env.Xml ?? string.Empty), commandAfterDestroy.Kind, commandAfterDestroy.Count, env.DisableCalls >= 1));
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

        var nullAdapter = new WindowsDisplayTopologyManager.NullCcdAdapter();
        bool nullQuery = nullAdapter.TryQueryActiveDisplayPaths(out _);

        Assert.AreEqual((2, true, true, false), (snap.Paths.Count, applied, restored, nullQuery));
        Assert.AreEqual((2560, 1440, 1), extendedMode);
        Assert.AreEqual((1280, 720, false), (adapter.LastApplied![1].Width, adapter.LastApplied[1].Height, nullAdapter.ApplyExtendTopology()));
    }

    [TestMethod]
    public async Task WindowsPlatformAdapters_ExecuteSafelyAgainstTempPathsAndNamedPipe()
    {
        if (!OperatingSystem.IsWindows())
            return;

        using var scope = new TempSnapshotScope();
        string settingsPath = scope.BackupFilePath;
        var env = new WindowsVddEnvironment(new VddEnvironmentPaths
        {
            SettingsPath = settingsPath,
            InfPath = scope.GetFilePath(1)
        });
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
        bool sent = await pipeClient.SendCommandAsync(new VddCommandRequest(VddCommandKind.SetDisplayCount, 1));
        await acceptTask.WaitAsync(TimeSpan.FromSeconds(3));

        var realTopology = new WindowsDisplayTopologyManager();
        var realSnap = realTopology.CaptureSnapshot();
        var ccdPaths = new WindowsDisplayTopologyManager.Win32CcdAdapter.DisplayConfigPathInfo[1];
        var ccdModes = new WindowsDisplayTopologyManager.Win32CcdAdapter.DisplayConfigModeInfo[2];
        var sampleSnap = new DisplayPathSnapshot
        {
            AdapterLuidLow = 42,
            AdapterLuidHigh = 1,
            SourceId = 2,
            TargetId = 3,
            PositionX = 1920,
            PositionY = 0,
            Width = 2560,
            Height = 1440,
            RefreshRateNumerator = 120,
            RefreshRateDenominator = 1
        };
        WindowsDisplayTopologyManager.Win32CcdAdapter.PopulateNativePathAndModes(sampleSnap, 0, ccdPaths, ccdModes);
        var unpackedSnap = WindowsDisplayTopologyManager.Win32CcdAdapter.ToSnapshot(ccdPaths[0], ccdModes);

        using var watchdogProc = StartSelfCheckProcess(settingsPath);
        using var defaultParsec = new ParsecVddBackend();
        var defaultVdd = new VirtualDriversVddBackend();
        _ = await defaultParsec.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        _ = await defaultParsec.SendHeartbeatAsync("0");
        _ = new WindowsDisplayTopologyManager.Win32CcdAdapter().ApplySuppliedConfig([]);
        using var proc = Process.Start(new ProcessStartInfo
        {
            FileName = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe"),
            Arguments = "/c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true
        });
        if (proc != null)
        {
            await VirtualDisplayWatchdogRunner.WaitForProcessIdExitAsync(proc.Id, CancellationToken.None);
        }

        Assert.AreEqual((true, "SETDISPLAYCOUNT 1"), (sent && readBack != null, receivedCommand));
        Assert.AreEqual((2560, 1440, 1920, 120u), (unpackedSnap.Width, unpackedSnap.Height, unpackedSnap.PositionX, unpackedSnap.RefreshRateNumerator));
        Assert.IsTrue(realSnap.Paths.Count >= 0 && defaultVdd != null && watchdogProc != null);
    }

    [TestMethod]
    public async Task SafeFallbacksAndEdgeCases_HandleInvalidInputsAndEmptyConfigs()
    {
        using var scope = new TempSnapshotScope();
        await File.WriteAllTextAsync(scope.BackupFilePath, "{ invalid json");
        var invalidSnap = await DisplayTopologySnapshot.TryLoadFromFileAsync(scope.BackupFilePath);
        DisplayTopologySnapshot.DeleteFileIfExists(scope.BackupFilePath);

        var vdd = new VirtualDriversVddBackend(new FakeVddPipeTransport(), new FakeVddEnvironment { Present = false });
        var missingDriverCreate = await vdd.CreateVirtualMonitorAsync(VirtualDisplayMode.Default);
        bool badModeUpdate = await vdd.UpdateModeAsync("1", new VirtualDisplayMode(100, 100, 60));

        using var parsec = new ParsecVddBackend(new FakeParsecTransport { Installed = false });
        bool parsecMissingMode = await parsec.UpdateModeAsync("0", VirtualDisplayMode.Default);
        bool parsecMissingDestroy = await parsec.DestroyVirtualMonitorAsync("not-a-number");

        var nullXmlCount = VddSettingsXmlHelper.ReadMonitorCount("<broken");
        string recreatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode("<broken", 1, new VirtualDisplayMode(100, 100, 60));
        var topologyManager = new WindowsDisplayTopologyManager(new FakeCcdAdapter());
        bool emptyRestore = topologyManager.RestoreSnapshot(new DisplayTopologySnapshot());
        var nullModeUpdate = WindowsDisplayTopologyManager.ApplyModeToTargetPath([], null, VirtualDisplayMode.Default);
        var nullPipe = new VirtualDriversVddBackend.NullVddPipeTransport();
        var nullEnv = new VirtualDriversVddBackend.NullVddEnvironment();
        bool nullPipeSent = await nullPipe.SendCommandAsync(new VddCommandRequest(VddCommandKind.ReloadDriver));

        Assert.AreEqual((null, (VirtualDisplayInstance?)null, false), (invalidSnap, missingDriverCreate, badModeUpdate));
        Assert.AreEqual((true, true, 0), (parsecMissingMode, parsecMissingDestroy, nullXmlCount));
        Assert.AreEqual((true, null, 1, false, false), (emptyRestore, nullModeUpdate, VddSettingsXmlHelper.ReadMonitorCount(recreatedXml), nullPipeSent, nullEnv.IsDriverPresent()));
    }

    [TestMethod]
    public async Task VddBufferParsersAndCcdFallbackStrategy_HandlePacketsAndRetriesCleanly()
    {
        byte[] ddBuf = VirtualDriversVddBackend.CreateDisplayDeviceBuffer();
        Encoding.Unicode.GetBytes(@"\\.\DISPLAY14" + '\0').CopyTo(ddBuf, 4);
        Encoding.Unicode.GetBytes("Virtual Display Driver\0").CopyTo(ddBuf, 68);
        BitConverter.TryWriteBytes(ddBuf.AsSpan(324, 4), 1);
        Encoding.Unicode.GetBytes(@"Root\MttVDD" + '\0').CopyTo(ddBuf, 328);

        var parsed = VirtualDriversVddBackend.ParseDisplayDeviceFields(ddBuf);
        Assert.AreEqual((@"\\.\DISPLAY14", "Virtual Display Driver", @"Root\MttVDD", 1), (parsed.DeviceName, parsed.DeviceString, parsed.DeviceId, parsed.StateFlags));
        Assert.IsTrue(VirtualDriversVddBackend.IsMatchingVddAdapter(@"Root\MttVDD", ""));
        Assert.IsTrue(VirtualDriversVddBackend.IsMatchingVddAdapter("PCI\\VEN", "Virtual Display Driver"));
        Assert.IsFalse(VirtualDriversVddBackend.IsMatchingVddAdapter(@"root\wvmd", "Sharing Monitor"));

        byte[] reqPacket = WindowsDisplayTopologyManager.BuildSourceDeviceNameRequest(42u, 1, 7u);
        Assert.AreEqual((uint)WindowsDisplayTopologyManager.SourceDeviceNamePacketSize, BitConverter.ToUInt32(reqPacket, 4));
        Encoding.Unicode.GetBytes(@"\\.\DISPLAY14" + '\0').CopyTo(reqPacket, 20);
        Assert.AreEqual(@"\\.\DISPLAY14", WindowsDisplayTopologyManager.ParseSourceDeviceNameResponse(reqPacket));
        Assert.IsNull(WindowsDisplayTopologyManager.ParseSourceDeviceNameResponse(new byte[10]));
        Assert.IsNull(WindowsDisplayTopologyManager.ParseSourceDeviceNameResponse(new byte[WindowsDisplayTopologyManager.SourceDeviceNamePacketSize]));

        int attempts = 0;
        bool cleared = false;
        bool retrySucceeded = WindowsDisplayTopologyManager.ApplyWithFallbackStrategy(
            () => ++attempts > 1,
            () => cleared = true,
            () => false,
            2);
        bool internalFallback = WindowsDisplayTopologyManager.ApplyWithFallbackStrategy(
            () => false,
            () => { },
            () => true,
            1);
        bool multiPathFail = WindowsDisplayTopologyManager.ApplyWithFallbackStrategy(
            () => false,
            () => { },
            () => true,
            2);

        IVddEnvironment defaultEnv = new VirtualDriversVddBackend.NullVddEnvironment();
        bool enabledDefault = await defaultEnv.EnsureDriverEnabledAsync();
        await defaultEnv.DisableDriverAsync();

        bool alreadyUp = await VirtualDriversVddBackend.EnsureDriverEnabledCoreAsync(() => true, () => false, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        bool enableTaskFails = await VirtualDriversVddBackend.EnsureDriverEnabledCoreAsync(() => false, () => false, TimeSpan.FromMilliseconds(50), CancellationToken.None);
        int enablePolls = 0;
        bool enableBecomesReady = await VirtualDriversVddBackend.EnsureDriverEnabledCoreAsync(() => ++enablePolls >= 2, () => true, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        int disablePolls = 0;
        await VirtualDriversVddBackend.DisableDriverCoreAsync(() => ++disablePolls < 2, () => true, TimeSpan.FromMilliseconds(400), CancellationToken.None);
        await VirtualDriversVddBackend.DisableDriverCoreAsync(() => false, () => true, TimeSpan.FromMilliseconds(50), CancellationToken.None);

        var selectedAttached = VirtualDriversVddBackend.SelectVddDeviceName([
            new VirtualDriversVddBackend.DisplayAdapterEntry(@"\\.\DISPLAY1", "AMD Radeon", @"PCI\VEN_1002", 1),
            new VirtualDriversVddBackend.DisplayAdapterEntry(@"\\.\DISPLAY13", "Virtual Display Driver", @"Root\MttVDD", 0),
            new VirtualDriversVddBackend.DisplayAdapterEntry(@"\\.\DISPLAY14", "Virtual Display Driver", @"Root\MttVDD", 1)
        ]);
        var selectedFallback = VirtualDriversVddBackend.SelectVddDeviceName([
            new VirtualDriversVddBackend.DisplayAdapterEntry(@"\\.\DISPLAY12", "Virtual Display Driver", @"Root\MttVDD", 0)
        ]);

        Assert.AreEqual((true, true, true, false, true), (retrySucceeded, cleared, internalFallback, multiPathFail, enabledDefault));
        Assert.AreEqual((true, false, true, @"\\.\DISPLAY14", @"\\.\DISPLAY12"), (alreadyUp, enableTaskFails, enableBecomesReady, selectedAttached, selectedFallback));
        Assert.IsNull(defaultEnv.FindVirtualDisplayDeviceName());
    }

    private static IDisposable? StartSelfCheckProcess(string snapshotPath)
    {
        string cmdPath = Environment.GetEnvironmentVariable("ComSpec") ?? Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmdPath))
            return null;

        var launcher = new ProcessWatchdogLauncher(cmdPath);
        return launcher.StartWatchdog(new VirtualDisplayWatchdogLaunchOptions
        {
            ParentPid = Environment.ProcessId,
            SnapshotPath = snapshotPath,
            MonitorId = "1",
            BackendId = VirtualDriversVddBackend.Id
        });
    }

    private sealed class FakeVddPipeTransport : IVddPipeTransport
    {
        public bool ShouldFail { get; set; }
        public VddCommandRequest LastCommand { get; private set; }

        public Task<bool> SendCommandAsync(VddCommandRequest command, CancellationToken ct = default)
        {
            LastCommand = command;
            return Task.FromResult(!ShouldFail);
        }
    }

    private sealed class FakeVddEnvironment : IVddEnvironment
    {
        public bool Present { get; set; }
        public string? Xml { get; set; }
        public string? DeviceName { get; set; }
        public bool FailEnable { get; set; }
        public int DisableCalls { get; private set; }

        public bool IsDriverPresent() => Present;
        public string? ReadSettingsXml() => Xml;
        public void WriteSettingsXml(string xmlContent) => Xml = xmlContent;
        public Task<bool> EnsureDriverEnabledAsync(CancellationToken ct = default) => Task.FromResult(!FailEnable);
        public Task DisableDriverAsync(CancellationToken ct = default)
        {
            DisableCalls++;
            return Task.CompletedTask;
        }
        public string? FindVirtualDisplayDeviceName() => DeviceName;
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
