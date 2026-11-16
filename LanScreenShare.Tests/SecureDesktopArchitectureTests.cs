using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Media;
using LanScreenShare.Media.SecureAgent;
using LanScreenShare.SecureHost;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class SecureDesktopArchitectureTests
{
    [TestMethod]
    public void SecureDesktopPolicySanitizer_DetectsAndRestoresDisabledPolicyOnlyWhenZero()
    {
        Assert.IsTrue(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(0));
        Assert.IsTrue(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(0L));
        Assert.IsTrue(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(0u));
        Assert.IsTrue(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(" 0 "));
        Assert.IsFalse(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(1));
        Assert.IsFalse(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop(null));
        Assert.IsFalse(SecureDesktopPolicySanitizer.ShouldRestorePromptOnSecureDesktop("invalid"));

        var zeroStore = new FakePromptPolicyStore(0);
        var defaultStore = new FakePromptPolicyStore(null);
        var failingStore = new FakePromptPolicyStore(0, throwOnWrite: true);

        Assert.AreEqual(SecureDesktopPolicyCheckResult.RestoredToDefault, SecureDesktopPolicySanitizer.RestoreIfDisabled(zeroStore));
        Assert.AreEqual(1, zeroStore.WriteCount);
        Assert.AreEqual(SecureDesktopPolicyCheckResult.AlreadySecureOrDefault, SecureDesktopPolicySanitizer.RestoreIfDisabled(defaultStore));
        Assert.AreEqual(0, defaultStore.WriteCount);
        Assert.AreEqual(SecureDesktopPolicyCheckResult.InsufficientPermissionsOrError, SecureDesktopPolicySanitizer.RestoreIfDisabled(failingStore));
    }

    [TestMethod]
    public void SecureHostCliOptions_ValidatesServiceAndSessionAgentArguments()
    {
        bool svcOk = SecureHostCliOptions.TryParseArgs(["--service"], out var svcOpts);
        bool agentOk = SecureHostCliOptions.TryParseArgs(["--session-agent", "--parent-pid", "4242", "--pipe", "CustomPipe"], out var agentOpts);
        bool badEmpty = SecureHostCliOptions.TryParseArgs([], out _);
        bool badUnknown = SecureHostCliOptions.TryParseArgs(["--service", "--unknown"], out _);
        bool badPid = SecureHostCliOptions.TryParseArgs(["--session-agent", "--parent-pid", "-5"], out _);
        bool badPipe = SecureHostCliOptions.TryParseArgs(["--session-agent", "--pipe", "   "], out _);

        Assert.AreEqual((true, SecureHostRunMode.Service, 0, SecureAgentProtocol.PipeName), (svcOk, svcOpts.Mode, svcOpts.ParentPid, svcOpts.PipeName));
        Assert.AreEqual((true, SecureHostRunMode.SessionAgent, 4242, "CustomPipe"), (agentOk, agentOpts.Mode, agentOpts.ParentPid, agentOpts.PipeName));
        Assert.AreEqual((false, false, false, false), (badEmpty, badUnknown, badPid, badPipe));
    }

    [TestMethod]
    public async Task SessionAgentSupervisor_MaintainsSingleAgentPerActiveConsoleSession()
    {
        var launcher = new FakeSessionLauncher { NextSessionId = 1 };
        using var supervisor = new SessionAgentSupervisor(launcher, "TestPipe", 100);

        supervisor.ReconcileOnce();
        var first = launcher.SpawnedAgents[0];

        supervisor.ReconcileOnce();
        Assert.AreEqual(1, launcher.SpawnedAgents.Count);

        launcher.NextSessionId = 2;
        supervisor.ReconcileOnce();
        Assert.AreEqual((2, true, 2u), (launcher.SpawnedAgents.Count, first.Terminated, supervisor.ActiveSessionId));

        launcher.SpawnedAgents[1].HasExited = true;
        supervisor.ReconcileOnce();
        Assert.AreEqual(3, launcher.SpawnedAgents.Count);

        launcher.NextSessionId = SessionAgentSupervisor.InvalidSessionId;
        supervisor.ReconcileOnce();
        Assert.IsTrue(launcher.SpawnedAgents[2].Terminated);

        launcher.NextSessionId = 4;
        using var cts = new CancellationTokenSource();
        var runTask = supervisor.RunAsync(cts.Token, pollIntervalMs: 30);
        await Task.Delay(50);
        supervisor.NotifySessionChanged();
        await Task.Delay(30);
        cts.Cancel();
        await runTask;

        Assert.IsTrue(launcher.SpawnedAgents.Count >= 4 && launcher.SpawnedAgents[^1].Terminated);
    }

    [TestMethod]
    public async Task SecureAgentProtocol_SerializesPacketsFramesAndInputsWithoutLoss()
    {
        var src = new CaptureSource
        {
            Type = CaptureSourceType.Screen,
            Id = "screen:1",
            Name = "Secundario",
            Handle = new IntPtr(9876),
            MonitorIndex = 1,
            DeviceName = @"\\.\DISPLAY2",
            Width = 1920,
            Height = 1080
        };
        var dto = CaptureSourceDto.FromModel(src).ToModel();
        Assert.AreEqual((src.Id, src.Handle, src.DeviceName), (dto.Id, dto.Handle, dto.DeviceName));

        var frame = new CapturedFrame
        {
            Data = [1, 2, 3, 4, 5],
            Width = 1280,
            Height = 720,
            Timestamp = 123456789L,
            Format = "h264",
            HostFps = 60,
            HostFrameTimeMs = 4
        };
        var decodedFrame = SecureAgentProtocol.DecodeFramePayload(SecureAgentProtocol.EncodeFramePayload(frame));
        Assert.IsNotNull(decodedFrame);
        Assert.AreEqual((1280, 720, "h264", 60, 5), (decodedFrame.Width, decodedFrame.Height, decodedFrame.Format, decodedFrame.HostFps, decodedFrame.Data.Length));

        var input = new RemoteInputMessage
        {
            Type = InputEventType.MouseDown,
            Button = RemoteMouseButton.Right,
            WheelDelta = -120,
            KeyCode = 65,
            NormalizedX = 0.25f,
            NormalizedY = 0.75f
        };
        var decodedInput = SecureAgentProtocol.DecodeInputPayload(SecureAgentProtocol.EncodeInputPayload(input));
        Assert.IsNotNull(decodedInput);
        Assert.AreEqual((input.Type, input.Button, input.WheelDelta, input.KeyCode, input.NormalizedX, input.NormalizedY),
            (decodedInput.Type, decodedInput.Button, decodedInput.WheelDelta, decodedInput.KeyCode, decodedInput.NormalizedX, decodedInput.NormalizedY));

        using var ms = new MemoryStream();
        await SecureAgentProtocol.WritePacketAsync(ms, SecureAgentMessageType.Ping, ReadOnlyMemory<byte>.Empty);
        await SecureAgentProtocol.WritePacketAsync(ms, SecureAgentMessageType.SimulateInput, SecureAgentProtocol.EncodeInputPayload(input));
        ms.Position = 0;

        var p1 = await SecureAgentProtocol.ReadPacketAsync(ms);
        var p2 = await SecureAgentProtocol.ReadPacketAsync(ms);
        var p3 = await SecureAgentProtocol.ReadPacketAsync(ms);

        Assert.AreEqual((SecureAgentMessageType.Ping, 0, SecureAgentMessageType.SimulateInput, 16, true),
            (p1!.Value.Type, p1.Value.Payload.Length, p2!.Value.Type, p2.Value.Payload.Length, p3 == null));
        Assert.IsNull(SecureAgentProtocol.DecodeFramePayload(new byte[3]));
        Assert.IsNull(SecureAgentProtocol.DecodeInputPayload(new byte[3]));
    }

    [TestMethod]
    public async Task SecureAgentClientAndServer_StreamFramesDesktopStateAndInputsOverPipe()
    {
        string pipeName = $"LssAgentTest_{Guid.NewGuid():N}";
        FakeCaptureEngine? activeCapture = null;
        var fakeInput = new FakeInputEngine();

        await using var server = new SecureAgentPipeServer(
            (src, engine) => activeCapture = new FakeCaptureEngine(),
            () => fakeInput,
            pipeName);

        using var serverCts = new CancellationTokenSource();
        Task serverTask = server.RunAsync(serverCts.Token);

        using var client = new SecureAgentClient(pipeName);
        bool connected = await ConnectWithRetryAsync(client);
        Assert.IsTrue(connected);

        using var fallbackCapture = new FakeCaptureEngine();
        using var fallbackInput = new FakeInputEngine();
        using var proxyCapture = new AgentBackedScreenCaptureService(
            client,
            new CaptureSource { Id = "screen:0", Name = "Principal", Width = 1920, Height = 1080 },
            "dxgi",
            () => fallbackCapture);
        using var proxyInput = new AgentBackedInputSimulatorService(client, fallbackInput);

        CapturedFrame? receivedFrame = null;
        bool? receivedSecureState = null;
        var frameSignal = new ManualResetEventSlim(false);
        var secureSignal = new ManualResetEventSlim(false);
        proxyCapture.FrameCaptured += (_, f) => { receivedFrame = f; frameSignal.Set(); };
        proxyCapture.SecureDesktopChanged += (_, s) => { receivedSecureState = s; secureSignal.Set(); };

        proxyCapture.StartCapture(new CaptureProfile { TargetFps = 60, BitrateMbps = 10, Codec = "h264" });
        await WaitUntilAsync(() => activeCapture != null);

        proxyCapture.UpdateSettings(new CaptureProfile { TargetFps = 45, Quality = 80, BitrateMbps = 12, Codec = "h264" });
        proxyCapture.RequestKeyframe();
        proxyInput.SimulateInput(new RemoteInputMessage { Type = InputEventType.KeyDown, KeyCode = 13 });
        proxyInput.ReleaseInjectedInputs();

        activeCapture!.EmitFrame(new CapturedFrame { Width = 1920, Height = 1080, Format = "h264", Data = [9, 8, 7] });
        activeCapture.EmitSecureDesktop(true);

        Assert.IsTrue(frameSignal.Wait(2000));
        Assert.IsTrue(secureSignal.Wait(2000));
        await WaitUntilAsync(() => fakeInput.ReleaseCalls >= 1 && fakeInput.SimulatedInputs.Count >= 1);

        proxyCapture.StopCapture();
        await WaitUntilAsync(() => !activeCapture.IsCapturing);

        Assert.AreEqual((1920, 1080, true, 13, true),
            (receivedFrame?.Width ?? 0, receivedFrame?.Height ?? 0, receivedSecureState, fakeInput.SimulatedInputs[0].KeyCode, activeCapture.KeyframeRequested));

        serverCts.Cancel();
        try { await serverTask; } catch { /* Esperado al cancelar */ }
    }

    [TestMethod]
    public void AgentBackedServicesAndDesktopClassifiers_FallbackSafelyWithoutPipe()
    {
        using var offlineClient = new SecureAgentClient($"LssMissingPipe_{Guid.NewGuid():N}");
        using var fallbackCapture = new FakeCaptureEngine();
        using var fallbackInput = new FakeInputEngine();

        using var proxyCapture = new AgentBackedScreenCaptureService(
            offlineClient,
            new CaptureSource { Id = "screen:0" },
            "dxgi",
            () => fallbackCapture);
        using var proxyInput = new AgentBackedInputSimulatorService(offlineClient, fallbackInput);

        proxyCapture.StartCapture(new CaptureProfile { TargetFps = 30, Codec = "jpeg" });
        proxyCapture.UpdateSettings(new CaptureProfile { TargetFps = 24, Codec = "jpeg" });
        proxyCapture.RequestKeyframe();
        proxyInput.SetTargetSource(new CaptureSource { Id = "screen:1" });
        proxyInput.SimulateInput(new RemoteInputMessage { Type = InputEventType.MouseMove, NormalizedX = 0.5f, NormalizedY = 0.5f });
        proxyInput.ReleaseInjectedInputs();
        proxyCapture.StopCapture();

        Assert.AreEqual((true, 1, 1, 1),
            (fallbackCapture.KeyframeRequested, fallbackInput.SimulatedInputs.Count, fallbackInput.ReleaseCalls, fallbackInput.TargetChanges));
        Assert.AreEqual(DesktopSwitchOutcome.AccessDenied, DesktopSwitchClassifier.ClassifyAttachFailure(DesktopSwitchClassifier.ErrorAccessDenied));
        Assert.AreEqual(DesktopSwitchOutcome.ThreadBusyOrHasGui, DesktopSwitchClassifier.ClassifyAttachFailure(DesktopSwitchClassifier.ErrorBusy));
        Assert.AreEqual(DesktopSwitchOutcome.Failed, DesktopSwitchClassifier.ClassifyAttachFailure(999));
        Assert.AreEqual(DxgiDesktopDisposition.PauseUnauthorizedSecureDesktop,
            DesktopSwitchClassifier.ResolveDxgiDisposition(isSecureDesktop: true, attachedToInputDesktop: false));
        Assert.AreEqual(DxgiDesktopDisposition.CaptureOnDesktop,
            DesktopSwitchClassifier.ResolveDxgiDisposition(isSecureDesktop: true, attachedToInputDesktop: true));
    }

    [TestMethod]
    public async Task SecureHostServiceManager_HandlesInstalledAndPortableLifecycleCleanly()
    {
        var backend = new FakeServiceControlBackend { Status = SecureHostServiceStatus.Stopped };
        var manager = new SecureHostServiceManager(backend, "TestSvc");

        bool installedStarted = await manager.EnsureServiceRunningAsync(null);
        Assert.AreEqual((true, SecureHostServiceStatus.Running, 1, 0),
            (installedStarted, manager.GetStatus(), backend.StartCalls, backend.ElevatedCalls));

        bool stopped = await manager.StopServiceAsync(removeIfTemporary: true);
        Assert.AreEqual((true, SecureHostServiceStatus.Stopped, 1), (stopped, manager.GetStatus(), backend.StopCalls));

        string tempExe = Path.GetTempFileName();
        try
        {
            backend.Status = SecureHostServiceStatus.NotInstalled;
            bool portableStarted = await manager.EnsureServiceRunningAsync(tempExe);
            bool portableStopped = await manager.StopServiceAsync(removeIfTemporary: true);

            Assert.AreEqual((true, true, false, 2),
                (portableStarted, portableStopped, manager.RegisteredTemporarily, backend.ElevatedCalls));
        }
        finally
        {
            File.Delete(tempExe);
        }

        Assert.AreEqual(SecureHostServiceStatus.NotInstalled, SecureHostServiceManager.ParseScQueryOutput(1060, ""));
        Assert.AreEqual(SecureHostServiceStatus.Running, SecureHostServiceManager.ParseScQueryOutput(0, "STATE : 4 RUNNING"));
        Assert.AreEqual(SecureHostServiceStatus.Stopped, SecureHostServiceManager.ParseScQueryOutput(0, "STATE : 1 STOPPED"));
    }

    [TestMethod]
    public async Task SecureHostServiceManager_HandlesFallbacksTimeoutsAndDefaultBackendSafely()
    {
        var fallbackBackend = new FakeServiceControlBackend
        {
            Status = SecureHostServiceStatus.Stopped,
            FailUnelevatedStartStop = true
        };
        var manager = new SecureHostServiceManager(fallbackBackend, "TestSvc");

        bool startedElevated = await manager.EnsureServiceRunningAsync(null);
        bool alreadyRunning = await manager.EnsureServiceRunningAsync(null);
        bool stoppedElevated = await manager.StopServiceAsync(removeIfTemporary: false);

        fallbackBackend.Status = SecureHostServiceStatus.NotInstalled;
        bool missingExe = await manager.EnsureServiceRunningAsync("C:\\missing-file-12345.exe");
        bool stopNotInstalled = await manager.StopServiceAsync(removeIfTemporary: true);

        using var fallbackInput = new FakeInputEngine();
        using var swappableInput = new AgentBackedInputSimulatorService(fallbackInput);
        swappableInput.SetClient(null);
        swappableInput.SimulateInput(new RemoteInputMessage { Type = InputEventType.KeyUp, KeyCode = 27 });

        var defaultManager = new SecureHostServiceManager(serviceName: $"LssMissingSvc_{Guid.NewGuid():N}");
        var realStatus = defaultManager.GetStatus();

        Assert.AreEqual((true, true, true, false, true), (startedElevated, alreadyRunning, stoppedElevated, missingExe, stopNotInstalled));
        Assert.AreEqual((2, 1, false), (fallbackBackend.ElevatedCalls, fallbackInput.SimulatedInputs.Count, realStatus == SecureHostServiceStatus.Running));
    }

    private static async Task<bool> ConnectWithRetryAsync(SecureAgentClient client)
    {
        for (int i = 0; i < 15; i++)
        {
            if (await client.TryConnectAsync(150))
                return true;
            await Task.Delay(30);
        }
        return false;
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, int timeoutMs = 2000)
    {
        DateTime deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (!predicate() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private sealed class FakePromptPolicyStore(object? initialValue, bool throwOnWrite = false) : IPromptOnSecureDesktopStore
    {
        public object? CurrentValue { get; private set; } = initialValue;
        public int WriteCount { get; private set; }

        public object? ReadValue() => CurrentValue;

        public void WriteEnabled()
        {
            if (throwOnWrite)
                throw new UnauthorizedAccessException("Access denied");

            CurrentValue = 1;
            WriteCount++;
        }
    }

    private sealed class FakeSpawnedAgent : ISpawnedAgentProcess
    {
        public bool HasExited { get; set; }
        public bool Terminated { get; private set; }

        public void Terminate()
        {
            Terminated = true;
            HasExited = true;
        }

        public void Dispose() { }
    }

    private sealed class FakeSessionLauncher : ISessionAgentLauncher
    {
        public uint NextSessionId { get; set; } = 1;
        public List<FakeSpawnedAgent> SpawnedAgents { get; } = new();

        public uint GetActiveConsoleSessionId() => NextSessionId;

        public ISpawnedAgentProcess LaunchSessionAgent(uint sessionId, int parentPid, string pipeName)
        {
            var proc = new FakeSpawnedAgent();
            SpawnedAgents.Add(proc);
            return proc;
        }
    }

    private sealed class FakeCaptureEngine : IScreenCaptureService
    {
        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;

        public bool IsCapturing { get; private set; }
        public int TargetFps { get; set; } = 30;
        public int Quality { get; set; } = 75;
        public int BitrateMbps { get; set; } = 8;
        public double ResolutionScale { get; set; } = 1.0;
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public string Codec { get; set; } = "h264";
        public bool KeyframeRequested { get; private set; }

        public void StartCapture(CaptureProfile profile)
        {
            UpdateSettings(profile);
            IsCapturing = true;
        }

        public void StopCapture() => IsCapturing = false;

        public void UpdateSettings(CaptureProfile profile)
        {
            TargetFps = profile.TargetFps;
            Quality = profile.Quality;
            BitrateMbps = profile.BitrateMbps;
            Codec = profile.Codec;
        }

        public void RequestKeyframe() => KeyframeRequested = true;
        public void EmitFrame(CapturedFrame frame) => FrameCaptured?.Invoke(this, frame);
        public void EmitSecureDesktop(bool isSecure) => SecureDesktopChanged?.Invoke(this, isSecure);
        public void Dispose() => IsCapturing = false;
    }

    private sealed class FakeInputEngine : IInputSimulatorService
    {
        public List<RemoteInputMessage> SimulatedInputs { get; } = new();
        public int ReleaseCalls { get; private set; }
        public int TargetChanges { get; private set; }

        public void SimulateInput(RemoteInputMessage input) => SimulatedInputs.Add(input);
        public void SetTargetSource(CaptureSource source) => TargetChanges++;
        public void ReleaseInjectedInputs() => ReleaseCalls++;
        public void Dispose() { }
    }

    private sealed class FakeServiceControlBackend : IServiceControlBackend
    {
        public SecureHostServiceStatus Status { get; set; } = SecureHostServiceStatus.NotInstalled;
        public bool FailUnelevatedStartStop { get; set; }
        public int StartCalls { get; private set; }
        public int StopCalls { get; private set; }
        public int ElevatedCalls { get; private set; }

        public SecureHostServiceStatus QueryStatus(string serviceName) => Status;

        public bool TryStartService(string serviceName)
        {
            if (FailUnelevatedStartStop)
                return false;

            StartCalls++;
            Status = SecureHostServiceStatus.Running;
            return true;
        }

        public bool TryStopService(string serviceName)
        {
            if (FailUnelevatedStartStop)
                return false;

            StopCalls++;
            Status = SecureHostServiceStatus.Stopped;
            return true;
        }

        public bool TryRunElevatedCommand(in ElevatedProcessCommand command)
        {
            ElevatedCalls++;
            if (command.Arguments.Contains("create", StringComparison.OrdinalIgnoreCase) ||
                command.Arguments.Contains("start", StringComparison.OrdinalIgnoreCase))
            {
                Status = SecureHostServiceStatus.Running;
            }
            else if (command.Arguments.Contains("stop", StringComparison.OrdinalIgnoreCase))
            {
                Status = SecureHostServiceStatus.Stopped;
            }
            else if (command.Arguments.Contains("delete", StringComparison.OrdinalIgnoreCase))
            {
                Status = SecureHostServiceStatus.NotInstalled;
            }
            return true;
        }

        public bool IsAgentPipeReady() => Status == SecureHostServiceStatus.Running;
    }
}
