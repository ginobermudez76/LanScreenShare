using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Core.Services;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class CoreModelsAndServicesTests
{
    [TestMethod]
    public void DeviceInfo_NotifiesOnlyWhenValueChanges()
    {
        var device = new DeviceInfo();
        var changed = new List<string?>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.AreEqual("Ver pantalla", device.ActionButtonText);

        device.Name = "Host-A";
        device.Name = "Host-A";
        device.IPAddress = "192.168.1.10";
        device.IPAddress = "192.168.1.10";
        var now = DateTime.UtcNow;
        device.LastSeen = now;
        device.LastSeen = now;
        device.IsRemoteControlAllowed = false;
        device.IsRemoteControlAllowed = false;
        device.IsViewing = true;
        device.IsViewing = true;

        Assert.AreEqual("Viendo", device.ActionButtonText);
        CollectionAssert.Contains(changed, nameof(DeviceInfo.Name));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IPAddress));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.LastSeen));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IsRemoteControlAllowed));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IsViewing));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.ActionButtonText));
        Assert.AreEqual(6, changed.Count);
    }

    [TestMethod]
    public void FileTransferModels_CalculatePercentAndStoreFields()
    {
        var offer = new FileTransferOffer { TransferId = "t1", FileName = "doc.pdf", FileSize = 2048 };
        Assert.AreEqual("t1", offer.TransferId);
        Assert.AreEqual("doc.pdf", offer.FileName);
        Assert.AreEqual(2048, offer.FileSize);

        var zeroProgress = new FileTransferProgress { BytesTransferred = 100, TotalBytes = 0 };
        Assert.AreEqual(0.0, zeroProgress.Percent);

        var halfProgress = new FileTransferProgress
        {
            TransferId = "t1",
            FileName = "doc.pdf",
            BytesTransferred = 500,
            TotalBytes = 1000,
            SpeedBytesPerSec = 10000,
            IsUpload = true
        };
        Assert.AreEqual(50.0, halfProgress.Percent, 0.001);
        Assert.IsTrue(halfProgress.IsUpload);

        var overProgress = new FileTransferProgress { BytesTransferred = 1500, TotalBytes = 1000 };
        Assert.AreEqual(100.0, overProgress.Percent);

        var completed = new FileTransferCompleted { TransferId = "t1", FileName = "doc.pdf", FilePath = "C:\\doc.pdf", FileSize = 1000, IsUpload = false };
        Assert.AreEqual("C:\\doc.pdf", completed.FilePath);

        var error = new FileTransferError { TransferId = "t1", FileName = "doc.pdf", ErrorMessage = "Canceled", IsUpload = true };
        Assert.AreEqual("Canceled", error.ErrorMessage);
    }

    [TestMethod]
    public void CaptureSourceAndDtos_ReportExpectedTypeFlags()
    {
        var screen = new CaptureSource
        {
            Type = CaptureSourceType.Screen,
            Id = "screen:1",
            Name = "Monitor 2",
            Subtitle = "1920x1080",
            Handle = new IntPtr(123),
            MonitorIndex = 1,
            DeviceName = "\\\\.\\DISPLAY2",
            ProcessName = "dwm",
            ProcessId = 42,
            X = 1920,
            Y = 0,
            Width = 1920,
            Height = 1080,
            IsPrimary = false
        };
        Assert.IsTrue(screen.IsSingleScreen);
        Assert.IsFalse(screen.IsAllScreens);

        var allScreens = new CaptureSource { Type = CaptureSourceType.AllScreens };
        Assert.IsTrue(allScreens.IsAllScreens);
        Assert.IsFalse(allScreens.IsSingleScreen);

        var catalog = new RemoteMonitorCatalogDto
        {
            ActiveSourceId = "screen:1",
            ActiveSourceName = "Monitor 2",
            Sources =
            {
                new RemoteSourceItemDto { Id = "screen:1", Name = "Monitor 2", Subtitle = "1080p", Category = "screen" }
            }
        };
        Assert.AreEqual(1, catalog.Sources.Count);
        Assert.AreEqual("screen", catalog.Sources[0].Category);

        var decoded = new DecodedVideoFrame(new byte[] { 1, 2, 3, 4 }, 2, 1);
        Assert.AreEqual(2, decoded.Width);
        Assert.AreEqual(1, decoded.Height);
        Assert.AreEqual(4, decoded.Pixels.Length);
    }

    [TestMethod]
    public void StreamTelemetry_EvaluatesAllNetworkQualityTiers()
    {
        var unknown = new StreamTelemetry();
        Assert.AreEqual(NetworkQuality.Unknown, unknown.Quality);
        Assert.AreEqual("Calculando...", unknown.QualityText);
        Assert.AreEqual("#94A3B8", unknown.QualityColorHex);

        var excellent = new StreamTelemetry { RttMs = 12, HostFps = 60, ViewerFps = 60, HostFrameTimeMs = 4, BitrateMbps = 15, DroppedFrames = 0, AvDriftMs = 2, AudioBufferedMs = 20 };
        Assert.AreEqual(NetworkQuality.Excellent, excellent.Quality);
        Assert.AreEqual("Excelente", excellent.QualityText);
        Assert.AreEqual("#10B981", excellent.QualityColorHex);

        var good = new StreamTelemetry { RttMs = 35, DroppedFrames = 1 };
        Assert.AreEqual(NetworkQuality.Good, good.Quality);
        Assert.AreEqual("Buena", good.QualityText);
        Assert.AreEqual("#3B82F6", good.QualityColorHex);

        var fair = new StreamTelemetry { RttMs = 70, DroppedFrames = 10 };
        Assert.AreEqual(NetworkQuality.Fair, fair.Quality);
        Assert.AreEqual("Moderada", fair.QualityText);
        Assert.AreEqual("#F59E0B", fair.QualityColorHex);

        var poor = new StreamTelemetry { RttMs = 150, DroppedFrames = 25 };
        Assert.AreEqual(NetworkQuality.Poor, poor.Quality);
        Assert.AreEqual("Inestable", poor.QualityText);
        Assert.AreEqual("#EF4444", poor.QualityColorHex);
    }

    [TestMethod]
    public void SecurityValueObjects_ValidateEmptyAndConversions()
    {
        ClientIpAddress emptyIp = "  ";
        Assert.IsTrue(emptyIp.IsEmpty);
        Assert.AreEqual("  ", (string)emptyIp);
        Assert.AreEqual(string.Empty, new ClientIpAddress(null!).ToString());

        AttachmentTokenId token = "tok-1";
        Assert.IsFalse(token.IsEmpty);
        Assert.AreEqual("tok-1", (string)token);
        Assert.AreEqual("tok-1", token.ToString());
        Assert.IsTrue(new AttachmentTokenId("").IsEmpty);

        DeviceFingerprint fp = "SHA256:ABC";
        Assert.IsFalse(fp.IsEmpty);
        Assert.AreEqual("SHA256:ABC", (string)fp);
        Assert.IsTrue(new DeviceFingerprint("").IsEmpty);

        DeviceName dn = "Laptop";
        Assert.IsFalse(dn.IsEmpty);
        Assert.AreEqual("Laptop", (string)dn);
        Assert.IsTrue(new DeviceName(" ").IsEmpty);

        SessionId sid = "sess-1";
        Assert.IsFalse(sid.IsEmpty);
        Assert.AreEqual("sess-1", (string)sid);
        Assert.IsTrue(new SessionId("").IsEmpty);
    }

    [TestMethod]
    public async Task CrossPlatformScreenCaptureService_ClampsPropertiesAndEmitsFrames()
    {
        using var service = new CrossPlatformScreenCaptureService();
        var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int frames = 0;
        service.FrameCaptured += (_, frame) =>
        {
            if (frame.Data.Length >= 2 && frame.Data[0] == 0xFF && frame.Data[1] == 0xD8)
            {
                frames++;
                firstFrameTcs.TrySetResult(true);
            }
        };

        service.TargetFps = 200;
        Assert.AreEqual(120, service.TargetFps);
        service.Quality = 5;
        Assert.AreEqual(10, service.Quality);
        service.BitrateMbps = 100;
        Assert.AreEqual(50, service.BitrateMbps);
        service.ResolutionScale = 5.0;
        Assert.AreEqual(2.0, service.ResolutionScale);
        service.TargetWidth = -10;
        Assert.AreEqual(0, service.TargetWidth);
        service.TargetHeight = -10;
        Assert.AreEqual(0, service.TargetHeight);
        Assert.AreEqual("CrossPlatform Universal Engine", service.ActiveEncoderDescription);
        Assert.IsFalse(service.IsHardwareAccelerated);

        service.StartCapture(new CaptureProfile { TargetFps = 60, Quality = 80, BitrateMbps = 10, TargetWidth = 0, TargetHeight = 0, Codec = "jpeg" });
        service.StartCapture(new CaptureProfile());
        Assert.IsTrue(service.IsCapturing);

        Assert.IsTrue(await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        service.UpdateSettings(new CaptureProfile { TargetFps = 30, Quality = 70, BitrateMbps = 6, TargetWidth = 640, TargetHeight = 360, ResolutionScale = 0.5, Codec = "h264" });
        service.RequestKeyframe();
        service.StopCapture();

        Assert.IsFalse(service.IsCapturing);
        Assert.IsTrue(frames > 0);
    }

    [TestMethod]
    public void NullServicesAndFactories_OperateWithoutThrowing()
    {
        using var audioCapture = new NullAudioCaptureService();
        audioCapture.StartCapture();
        audioCapture.StartCapture(1234);
        Assert.IsTrue(audioCapture.IsCapturing);
        audioCapture.StopCapture();
        Assert.IsFalse(audioCapture.IsCapturing);

        using var audioPlayer = new NullAudioPlayerService();
        audioPlayer.Volume = 0.5f;
        audioPlayer.IsMuted = false;
        audioPlayer.PlayChunk(new CapturedAudioChunk { Data = new byte[] { 1, 2, 3, 4 } });
        Assert.IsTrue(audioPlayer.IsPlaying);
        audioPlayer.IsMuted = true;
        audioPlayer.ClearBuffer();
        audioPlayer.Stop();
        Assert.IsFalse(audioPlayer.IsPlaying);
        Assert.AreEqual(0, audioPlayer.CurrentBufferedDurationMs);

        using var inputSim = new NullInputSimulatorService();
        inputSim.SetTargetSource(new CaptureSource());
        inputSim.SimulateInput(new RemoteInputMessage { Type = InputEventType.KeyDown, KeyCode = 0x41 });
        inputSim.ReleaseInjectedInputs();
        Assert.AreEqual(1, inputSim.SimulatedInputsCount);

        Assert.IsNotNull(IdentityKeyStorageFactory.Create());
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.AllScreens }, "gdi"));
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.Screen, X = 0, Y = 0, Width = 800, Height = 600 }, "gdi"));
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.Window, Handle = new IntPtr(1) }, "dxgi"));
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.Screen }, "dxgi"));

        using var cap = AudioServiceFactory.CreateAudioCapture();
        using var mic = AudioServiceFactory.CreateMicCapture();
        using var player = AudioServiceFactory.CreateAudioPlayer();
        Assert.IsNotNull(cap);
        Assert.IsNotNull(mic);
        Assert.IsNotNull(player);
    }

    [TestMethod]
    public void SessionAuthority_PruneAndRateLimitAndControlConflicts_WorkAsExpected()
    {
        var authority = new SessionAuthority();
        Assert.ThrowsException<ArgumentException>(() => authority.CreateSession(new DeviceFingerprint(""), "peer"));

        var expiredSession = authority.CreateSession("fp-1", "Peer1", TimeSpan.FromMilliseconds(-10));
        var activeSession = authority.CreateSession("fp-2", "Peer2", TimeSpan.FromHours(1));
        var secondSession = authority.CreateSession("fp-3", "Peer3", TimeSpan.FromHours(1));

        var expiredToken = authority.IssueChannelToken(activeSession.SessionId, ChannelType.Audio, TimeSpan.FromMilliseconds(-10));
        var validToken = authority.IssueChannelToken(activeSession.SessionId, ChannelType.RemoteControl, TimeSpan.FromMinutes(5));

        Assert.AreEqual(AttachmentStatus.TokenExpired, authority.TryAttachChannel(expiredToken.TokenId, activeSession.SessionId, "fp-2", ChannelType.Audio));
        Assert.AreEqual(AttachmentStatus.TokenBindingMismatch, authority.TryAttachChannel(validToken.TokenId, activeSession.SessionId, "wrong-fp", ChannelType.RemoteControl));
        Assert.AreEqual(AttachmentStatus.ChannelMismatch, authority.TryAttachChannel(validToken.TokenId, activeSession.SessionId, "fp-2", ChannelType.FileTransfer));

        Assert.IsTrue(authority.TryAttachChannel(new ChannelAttachmentRequest(validToken.TokenId, activeSession.SessionId.Value, "fp-2", ChannelType.RemoteControl), out string? fail));
        Assert.IsNull(fail);
        Assert.IsFalse(authority.TryAttachChannel(new ChannelAttachmentRequest(validToken.TokenId, activeSession.SessionId.Value, "fp-2", ChannelType.RemoteControl), out string? failAfter));
        Assert.AreEqual(AttachmentStatus.TokenNotFound.ToString(), failAfter);

        authority.PruneExpired();
        Assert.IsNull(authority.GetSession(expiredSession.SessionId));

        Assert.IsTrue(authority.TryAcquireControl(activeSession.SessionId, out _));
        Assert.AreEqual(activeSession.SessionId.Value, authority.ActiveControllerSessionId);
        Assert.AreEqual(RemoteControlState.DeniedConflict, authority.RequestRemoteControl(secondSession.SessionId));
        Assert.IsFalse(authority.TryAcquireControl(secondSession.SessionId, out string? conflictReason));
        Assert.AreEqual(RemoteControlState.DeniedConflict.ToString(), conflictReason);

        authority.ReleaseRemoteControl(activeSession.SessionId);
        Assert.IsNull(authority.GetActiveControllerSessionId());
        Assert.IsTrue(authority.TryAcquireControl(secondSession.SessionId, out _));
        authority.RevokeControl(secondSession.SessionId);
        authority.CloseSession(secondSession.SessionId);

        Assert.IsFalse(authority.IsPairingBlocked(""));
        authority.RecordPairingAttempt("192.168.1.20", false);
        authority.RecordPairingAttempt("192.168.1.20", false);
        Assert.IsFalse(authority.IsPairingBlocked("192.168.1.20"));
        authority.RecordPairingAttempt("192.168.1.20", false);
        Assert.IsTrue(authority.IsPairingBlocked("192.168.1.20"));
        authority.RecordPairingAttempt("192.168.1.20", true);
        Assert.IsFalse(authority.IsPairingBlocked("192.168.1.20"));
    }

    [TestMethod]
    public void AdaptiveBitrateController_ClampsLimitsAndHandlesDisabledAndRecovery()
    {
        long fakeNow = 1000;
        var abr = new AdaptiveBitrateController(initialBitrateMbps: 10, baseFps: 60, timeProvider: () => fakeNow);

        abr.MinBitrateMbps = 12;
        Assert.AreEqual(12, abr.CurrentBitrateMbps);
        abr.MaxBitrateMbps = 10;
        Assert.AreEqual(12, abr.MaxBitrateMbps);
        abr.MaxBitrateMbps = 20;
        abr.CurrentBitrateMbps = 18;
        abr.MaxBitrateMbps = 16;
        Assert.AreEqual(16, abr.CurrentBitrateMbps);

        abr.MinFps = 30;
        abr.BaseFps = 45;
        Assert.AreEqual(45, abr.BaseFps);

        abr.IsEnabled = false;
        abr.RegisterHostDrop();
        abr.RegisterTelemetry(new StreamTelemetry { RttMs = 200, DroppedFrames = 5 });
        Assert.AreEqual(16, abr.CurrentBitrateMbps);

        abr.IsEnabled = true;
        abr.MinBitrateMbps = 2;
        abr.Reset(initialBitrateMbps: 12, baseFps: 60);
        Assert.AreEqual("Reinicio", abr.LastAdjustmentReason);

        fakeNow += 3000;
        abr.RegisterTelemetry(new StreamTelemetry { RttMs = 35 }); // spike above baseline 15 + 20 = 35? strictly >, so use 40
        fakeNow += 3000;
        abr.RegisterTelemetry(new StreamTelemetry { RttMs = 40 });
        Assert.AreEqual(AbrNetworkState.Congested, abr.CurrentState);

        fakeNow += 3000;
        abr.RegisterTelemetry(new StreamTelemetry { RttMs = 5 }); // lower baseline
        Assert.AreEqual(AbrNetworkState.Optimal, abr.CurrentState);
    }
}
