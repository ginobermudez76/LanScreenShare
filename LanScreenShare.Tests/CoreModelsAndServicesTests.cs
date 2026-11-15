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
    private static bool IsJpegFrame(CapturedFrame frame)
    {
        if (frame.Data.Length < 2) return false;
        return frame.Data[0] == 0xFF && frame.Data[1] == 0xD8;
    }

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
        device.LastSeen = DateTime.UtcNow;
        device.IsRemoteControlAllowed = false;
        device.IsViewing = true;
        device.IsViewing = true;

        Assert.AreEqual("Viendo", device.ActionButtonText);
        Assert.AreEqual(6, changed.Count);
    }

    [TestMethod]
    public void FileTransferModels_CalculatePercentAndStoreFields()
    {
        var zeroProgress = new FileTransferProgress { TransferId = "t1", BytesTransferred = 100, TotalBytes = 0 };
        var halfProgress = new FileTransferProgress { TransferId = "t1", BytesTransferred = 500, TotalBytes = 1000, SpeedBytesPerSec = 10000, IsUpload = true };
        var overProgress = new FileTransferProgress { BytesTransferred = 1500, TotalBytes = 1000 };

        Assert.AreEqual(0.0, zeroProgress.Percent);
        Assert.AreEqual(50.0, halfProgress.Percent, 0.001);
        Assert.AreEqual(100.0, overProgress.Percent);
    }

    [TestMethod]
    public void FileTransferMetadataModels_PreserveProperties()
    {
        var offer = new FileTransferOffer { TransferId = "t1", FileName = "doc.pdf", FileSize = 2048 };
        var completed = new FileTransferCompleted { TransferId = "t1", FileName = "doc.pdf", FilePath = "C:\\doc.pdf", FileSize = 2048, IsUpload = false };
        var error = new FileTransferError { TransferId = "t1", FileName = "doc.pdf", ErrorMessage = "Canceled", IsUpload = true };

        Assert.AreEqual(2048, offer.FileSize);
        Assert.AreEqual("C:\\doc.pdf", completed.FilePath);
        Assert.AreEqual("Canceled", error.ErrorMessage);
    }

    [TestMethod]
    public void CaptureSourceAndDtos_ReportExpectedTypeFlags()
    {
        var screen = new CaptureSource { Type = CaptureSourceType.Screen, Id = "screen:1", Name = "Monitor 2" };
        var allScreens = new CaptureSource { Type = CaptureSourceType.AllScreens };
        var catalog = new RemoteMonitorCatalogDto
        {
            ActiveSourceId = "screen:1",
            Sources = { new RemoteSourceItemDto { Id = "screen:1", Name = "Monitor 2", Category = "screen" } }
        };

        Assert.IsTrue(screen.IsSingleScreen && !screen.IsAllScreens);
        Assert.IsTrue(allScreens.IsAllScreens && !allScreens.IsSingleScreen);
        Assert.AreEqual(1, catalog.Sources.Count);
    }

    [TestMethod]
    public void DecodedVideoFrame_PreservesDimensionsAndPixels()
    {
        var decoded = new DecodedVideoFrame(new byte[] { 1, 2, 3, 4 }, 2, 1);

        Assert.AreEqual(2, decoded.Width);
        Assert.AreEqual(1, decoded.Height);
        Assert.AreEqual(4, decoded.Pixels.Length);
    }

    [TestMethod]
    public void StreamTelemetry_EvaluatesAllNetworkQualityTiers()
    {
        var cases = new[]
        {
            new { Rtt = 0, HostFps = 0, ViewerFps = 0, Drops = 0L, Quality = NetworkQuality.Unknown, Text = "Calculando...", Hex = "#94A3B8" },
            new { Rtt = 12, HostFps = 60, ViewerFps = 60, Drops = 0L, Quality = NetworkQuality.Excellent, Text = "Excelente", Hex = "#10B981" },
            new { Rtt = 35, HostFps = 60, ViewerFps = 60, Drops = 1L, Quality = NetworkQuality.Good, Text = "Buena", Hex = "#3B82F6" },
            new { Rtt = 70, HostFps = 60, ViewerFps = 60, Drops = 10L, Quality = NetworkQuality.Fair, Text = "Moderada", Hex = "#F59E0B" },
            new { Rtt = 150, HostFps = 60, ViewerFps = 60, Drops = 25L, Quality = NetworkQuality.Poor, Text = "Inestable", Hex = "#EF4444" }
        };

        foreach (var item in cases)
        {
            var telemetry = new StreamTelemetry
            {
                RttMs = item.Rtt,
                HostFps = item.HostFps,
                ViewerFps = item.ViewerFps,
                DroppedFrames = item.Drops
            };

            Assert.AreEqual(
                (item.Quality, item.Text, item.Hex),
                (telemetry.Quality, telemetry.QualityText, telemetry.QualityColorHex));
        }
    }

    [TestMethod]
    public void SecurityValueObjects_ValidateEmptyAndConversions()
    {
        ClientIpAddress emptyIp = "  ";
        AttachmentTokenId token = "tok-1";
        DeviceFingerprint fp = "SHA256:ABC";
        DeviceName dn = "Laptop";
        SessionId sid = "sess-1";

        Assert.IsTrue(emptyIp.IsEmpty);
        Assert.IsFalse(token.IsEmpty || sid.IsEmpty);
        Assert.AreEqual("SHA256:ABC|Laptop", $"{(string)fp}|{dn}");
    }

    [TestMethod]
    public void CrossPlatformScreenCaptureService_ClampsProperties()
    {
        using var service = new CrossPlatformScreenCaptureService
        {
            TargetFps = 200,
            Quality = 5,
            BitrateMbps = 100,
            ResolutionScale = 5.0,
            TargetWidth = -10,
            TargetHeight = -10
        };

        Assert.AreEqual((120, 10, 50), (service.TargetFps, service.Quality, service.BitrateMbps));
        Assert.AreEqual((2.0, 0, 0), (service.ResolutionScale, service.TargetWidth, service.TargetHeight));
        Assert.IsFalse(service.IsHardwareAccelerated);
    }

    [TestMethod]
    public async Task CrossPlatformScreenCaptureService_EmitsFramesAndUpdatesSettings()
    {
        using var service = new CrossPlatformScreenCaptureService();
        var firstFrameTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.FrameCaptured += (_, frame) =>
        {
            if (IsJpegFrame(frame)) firstFrameTcs.TrySetResult(true);
        };

        service.StartCapture(new CaptureProfile { TargetFps = 60, Quality = 80, BitrateMbps = 10 });
        service.StartCapture();
        Assert.IsTrue(service.IsCapturing);

        bool gotFrame = await firstFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        service.UpdateSettings(new CaptureProfile { TargetFps = 30, Quality = 70, BitrateMbps = 6, TargetWidth = 640, TargetHeight = 360 });
        service.RequestKeyframe();
        service.StopCapture();

        Assert.IsTrue(gotFrame);
        Assert.IsFalse(service.IsCapturing);
    }

    [TestMethod]
    public void NullServices_OperateWithoutThrowing()
    {
        using var audioCapture = new NullAudioCaptureService();
        audioCapture.StartCapture(1234);
        Assert.IsTrue(audioCapture.IsCapturing);
        audioCapture.StopCapture();

        using var audioPlayer = new NullAudioPlayerService { Volume = 0.5f, IsMuted = false };
        audioPlayer.PlayChunk(new CapturedAudioChunk { Data = new byte[] { 1, 2, 3, 4 } });
        Assert.IsTrue(audioPlayer.IsPlaying);
        audioPlayer.ClearBuffer();
        audioPlayer.Stop();

        using var inputSim = new NullInputSimulatorService();
        inputSim.SetTargetSource(new CaptureSource());
        inputSim.SimulateInput(new RemoteInputMessage { Type = InputEventType.KeyDown, KeyCode = 0x41 });
        inputSim.ReleaseInjectedInputs();
        Assert.AreEqual(1, inputSim.SimulatedInputsCount);
    }

    [TestMethod]
    public void PlatformFactories_CreateExpectedImplementations()
    {
        Assert.IsNotNull(IdentityKeyStorageFactory.Create());
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.AllScreens }, "gdi"));
        Assert.IsNotNull(CaptureServiceFactory.Create(new CaptureSource { Type = CaptureSourceType.Screen }, "dxgi"));
    }

    [TestMethod]
    public void SessionAuthority_TokensAndPruning_EnforceExpirationAndBinding()
    {
        var authority = new SessionAuthority();
        Assert.ThrowsException<ArgumentException>(() => authority.CreateSession(new DeviceFingerprint(""), "peer"));

        var expiredSession = authority.CreateSession("fp-1", "Peer1", TimeSpan.FromMilliseconds(-10));
        var activeSession = authority.CreateSession("fp-2", "Peer2", TimeSpan.FromHours(1));

        var expiredToken = authority.IssueChannelToken(activeSession.SessionId, ChannelType.Audio, TimeSpan.FromMilliseconds(-10));
        var validToken = authority.IssueChannelToken(activeSession.SessionId, ChannelType.RemoteControl, TimeSpan.FromMinutes(5));

        Assert.AreEqual(AttachmentStatus.TokenExpired, authority.TryAttachChannel(expiredToken.TokenId, activeSession.SessionId, "fp-2", ChannelType.Audio));
        Assert.AreEqual(AttachmentStatus.TokenBindingMismatch, authority.TryAttachChannel(validToken.TokenId, activeSession.SessionId, "wrong-fp", ChannelType.RemoteControl));
        Assert.IsTrue(authority.TryAttachChannel(new ChannelAttachmentRequest(validToken.TokenId, activeSession.SessionId.Value, "fp-2", ChannelType.RemoteControl), out _));

        authority.PruneExpired();
        Assert.IsNull(authority.GetSession(expiredSession.SessionId));
    }

    [TestMethod]
    public void SessionAuthority_RemoteControlAndRateLimit_EnforceConflictsAndLockout()
    {
        var authority = new SessionAuthority();
        var s1 = authority.CreateSession("fp-1", "Peer1");
        var s2 = authority.CreateSession("fp-2", "Peer2");

        Assert.IsTrue(authority.TryAcquireControl(s1.SessionId, out _));
        Assert.AreEqual(RemoteControlState.DeniedConflict, authority.RequestRemoteControl(s2.SessionId));
        authority.ReleaseRemoteControl(s1.SessionId);
        authority.RevokeControl(s2.SessionId);
        authority.CloseSession(s2.SessionId);

        authority.RecordPairingAttempt("192.168.1.20", false);
        authority.RecordPairingAttempt("192.168.1.20", false);
        authority.RecordPairingAttempt("192.168.1.20", false);
        Assert.IsTrue(authority.IsPairingBlocked("192.168.1.20"));
    }

    [TestMethod]
    public void AdaptiveBitrateController_ClampsLimitsAndRespectsEnabledFlag()
    {
        long fakeNow = 1000;
        var abr = new AdaptiveBitrateController(initialBitrateMbps: 10, baseFps: 60, timeProvider: () => fakeNow);

        abr.MinBitrateMbps = 12;
        abr.MaxBitrateMbps = 10;
        Assert.AreEqual(12, abr.MaxBitrateMbps);

        abr.IsEnabled = false;
        abr.RegisterHostDrop();
        Assert.AreEqual(12, abr.CurrentBitrateMbps);

        abr.IsEnabled = true;
        abr.MinBitrateMbps = 2;
        abr.MaxBitrateMbps = 20;
        abr.Reset(initialBitrateMbps: 12, baseFps: 60);
        fakeNow += 3000;
        abr.RegisterTelemetry(new StreamTelemetry { RttMs = 40 });
        Assert.AreEqual(AbrNetworkState.Congested, abr.CurrentState);
    }
}
