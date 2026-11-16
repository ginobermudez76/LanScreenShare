using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Display;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class VirtualDisplayProjectionMtlsTests
{
    private sealed class StubVirtualDisplayService : IVirtualDisplayService
    {
        private VirtualDisplayState _state = VirtualDisplayState.Inactive;
        public VirtualDisplayState State => _state;
        public VirtualDisplaySessionInfo? CurrentSession { get; private set; }
        public int DisconnectNotifications { get; private set; }
        public bool ShouldFailStart { get; set; }

        public event EventHandler<VirtualDisplayStateChangedEventArgs>? StateChanged;

        public Task<VirtualDisplayOperationResult> StartExtendedDisplayAsync(VirtualDisplayMode requestedMode, CancellationToken ct = default)
        {
            if (ShouldFailStart)
            {
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.DriverNotInstalled, "MttVDD no disponible"));
            }

            CurrentSession = new VirtualDisplaySessionInfo
            {
                BackendId = "stub-vdd",
                MonitorId = "vdd-1",
                DeviceName = @"\\.\DISPLAY9",
                ActiveMode = requestedMode,
                SnapshotPath = "unused.json"
            };
            SetState(VirtualDisplayState.ConnectingStream, "Waiting");
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task<VirtualDisplayOperationResult> NotifyStreamConnectedAsync(CancellationToken ct = default)
        {
            SetState(VirtualDisplayState.Streaming, "Streaming");
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task<VirtualDisplayOperationResult> ChangeDisplayModeAsync(VirtualDisplayMode newMode, CancellationToken ct = default)
        {
            if (CurrentSession == null)
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.NoSession, "No session"));

            CurrentSession = CurrentSession with { ActiveMode = newMode };
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task HandleNetworkDisconnectedAsync(CancellationToken ct = default)
        {
            DisconnectNotifications++;
            SetState(VirtualDisplayState.Reconnecting, "Reconnecting");
            return Task.CompletedTask;
        }

        public Task HandleNetworkReconnectedAsync(CancellationToken ct = default) => NotifyStreamConnectedAsync(ct);
        public Task HandleSystemSuspendAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleSystemResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecoverOrphanedSessionOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

        public Task StopExtendedDisplayAsync(CancellationToken ct = default)
        {
            CurrentSession = null;
            SetState(VirtualDisplayState.Inactive, "Stopped");
            return Task.CompletedTask;
        }

        private void SetState(VirtualDisplayState next, string reason)
        {
            var prev = _state;
            _state = next;
            StateChanged?.Invoke(this, new VirtualDisplayStateChangedEventArgs { PreviousState = prev, CurrentState = next, Reason = reason });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class StubCaptureService : IScreenCaptureService
    {
        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;
        public bool IsCapturing { get; private set; }
        public int TargetFps { get; set; } = 60;
        public int Quality { get; set; } = 85;
        public int BitrateMbps { get; set; } = 8;
        public double ResolutionScale { get; set; } = 1.0;
        public int TargetWidth { get; set; }
        public int TargetHeight { get; set; }
        public string Codec { get; set; } = "h264";

        public void StartCapture(CaptureProfile profile)
        {
            (TargetWidth, TargetHeight, TargetFps, IsCapturing) = (profile.TargetWidth, profile.TargetHeight, profile.Fps, true);
            SecureDesktopChanged?.Invoke(this, false);
        }

        public void StopCapture() => IsCapturing = false;
        public void UpdateSettings(CaptureProfile profile) => (TargetWidth, TargetHeight, TargetFps) = (profile.TargetWidth, profile.TargetHeight, profile.Fps);
        public void RequestKeyframe() { /* No-op for test stub */ }

        public void EmitDummyFrame(int width, int height)
        {
            FrameCaptured?.Invoke(this, new CapturedFrame
            {
                Data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 },
                Width = width,
                Height = height,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Format = "h264"
            });
        }

        public void Dispose() => StopCapture();
    }

    private sealed class FakeTopologyManager(DisplayTopologySnapshot snapshot) : IDisplayTopologyManager
    {
        public DisplayTopologySnapshot CaptureSnapshot() => snapshot;
        public bool ApplyExtendTopology(string? preferredDeviceName, VirtualDisplayMode mode) => true;
        public bool RestoreSnapshot(DisplayTopologySnapshot snapshotToRestore) => true;
    }

    private sealed record PairedPeerContext(
        X509Certificate2 SenderCert,
        X509Certificate2 ReceiverCert,
        string ReceiverFingerprint,
        InMemoryPairedDeviceStore SenderStore,
        InMemoryPairedDeviceStore ReceiverStore) : IDisposable
    {
        public void Dispose()
        {
            SenderCert.Dispose();
            ReceiverCert.Dispose();
        }
    }

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static X509Certificate2 CreateBidirectionalPeerCertificate(string commonName)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TlsConfigurationHelper.ServerAuthOid),
            new Oid(TlsConfigurationHelper.ClientAuthOid)
        }, critical: true));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    private static async Task<PairedPeerContext> CreatePairedPeersAsync()
    {
        var senderCert = CreateBidirectionalPeerCertificate("SenderPeer");
        var receiverCert = CreateBidirectionalPeerCertificate("ReceiverPeer");
        var senderStore = new InMemoryPairedDeviceStore();
        await senderStore.AddPairedDeviceAsync(PairedDevice.Create("b", "ReceiverPeer", TlsConfigurationHelper.ComputeFingerprint(receiverCert)));
        var receiverStore = new InMemoryPairedDeviceStore();
        await receiverStore.AddPairedDeviceAsync(PairedDevice.Create("a", "SenderPeer", TlsConfigurationHelper.ComputeFingerprint(senderCert)));
        return new PairedPeerContext(senderCert, receiverCert, TlsConfigurationHelper.ComputeFingerprint(receiverCert), senderStore, receiverStore);
    }

    [TestMethod]
    public async Task DisplaySignaling_MtlsNegotiationAndBusyRejection_WorkEndToEnd()
    {
        using var peers = await CreatePairedPeersAsync();
        bool busy = false;
        int port = GetAvailableTcpPort();

        using var server = new DisplaySignalingServer(new DisplaySignalingServerOptions
        {
            SslServerOptions = TlsConfigurationHelper.CreateServerOptions(peers.ReceiverCert, peers.ReceiverStore),
            IsBusyProvider = () => busy,
            CapabilitiesProvider = () => new DisplayCapabilitiesDto
            {
                DeviceName = "WirelessMonitorB",
                SupportedModes = new[] { new VirtualDisplayMode(9999, 9999, 240), new VirtualDisplayMode(1920, 1080, 60) }
            }
        });
        await server.StartAsync(port);

        var store = new ProjectionInvitationStore();
        var client = new DisplaySignalingClient();
        var clientSsl = TlsConfigurationHelper.CreateClientOptions(peers.SenderCert, "localhost", peers.SenderStore);

        var ok = await client.NegotiateAndInviteAsync(new DisplayNegotiationRequest { TargetIp = "127.0.0.1", TargetPort = port, SslOptions = clientSsl, InvitationStore = store });
        Assert.IsTrue(ok.Accepted);
        Assert.AreEqual((new VirtualDisplayMode(1920, 1080, 60), peers.ReceiverFingerprint), (ok.InvitationToken!.NegotiatedMode, ok.PeerFingerprint));

        busy = true;
        var busyResult = await client.NegotiateAndInviteAsync(new DisplayNegotiationRequest { TargetIp = "127.0.0.1", TargetPort = port, SslOptions = clientSsl, InvitationStore = store });
        Assert.IsFalse(busyResult.Accepted);
        Assert.AreEqual(DisplaySignalingMessage.ErrorDisplayBusy, busyResult.ErrorCode);
    }

    [TestMethod]
    public async Task TcpStreamServer_ProjectionInvitationRequired_EnforcesValidSingleUseToken()
    {
        using var peers = await CreatePairedPeersAsync();
        using var rogueCert = CreateBidirectionalPeerCertificate("RoguePeer");
        await peers.SenderStore.AddPairedDeviceAsync(PairedDevice.Create("rogue", "RoguePeer", TlsConfigurationHelper.ComputeFingerprint(rogueCert)));

        var invitationStore = new ProjectionInvitationStore();
        int streamPort = GetAvailableTcpPort();
        using var server = new TcpStreamServer(
            new SessionAuthority(peers.SenderStore),
            TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore),
            peers.SenderStore)
        {
            ProjectionInvitationStore = invitationStore,
            RequireProjectionInvitation = true
        };
        await server.StartAsync(streamPort);

        var rogueToken = invitationStore.IssueInvitation(new ProjectionInvitationRequest { TargetFingerprint = peers.ReceiverFingerprint });
        using var rogueClient = new TcpStreamClient();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => rogueClient.ConnectAsync(new StreamClientConnectionOptions(
            "127.0.0.1", streamPort, TlsConfigurationHelper.CreateClientOptions(rogueCert, "localhost", peers.ReceiverStore), rogueToken.TokenId)));

        var validToken = invitationStore.IssueInvitation(new ProjectionInvitationRequest { TargetFingerprint = peers.ReceiverFingerprint });
        var receiverSsl = TlsConfigurationHelper.CreateClientOptions(peers.ReceiverCert, "localhost", peers.ReceiverStore);

        using var validClient = new TcpStreamClient();
        await validClient.ConnectAsync(new StreamClientConnectionOptions("127.0.0.1", streamPort, receiverSsl, validToken.TokenId));
        Assert.IsTrue(validClient.IsConnected);

        using var replayClient = new TcpStreamClient();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => replayClient.ConnectAsync(
            new StreamClientConnectionOptions("127.0.0.1", streamPort, receiverSsl, validToken.TokenId)));
    }

    private sealed record StreamVerificationContext(
        DisplayInvitationAcceptanceContext AcceptedContext,
        PairedPeerContext Peers,
        VirtualDisplayProjectionReceiver Receiver,
        TcpStreamClient StreamClient,
        TcpControlClient ControlClient,
        List<StubCaptureService> CaptureInstances);

    private sealed record ReceiverCreationArgs(
        PairedPeerContext Peers,
        TaskCompletionSource<DisplayInvitationAcceptanceContext> AcceptedTcs,
        TcpStreamClient StreamClient,
        TcpControlClient ControlClient);

    private sealed record HostCreationArgs(
        IVirtualDisplayService DisplayService,
        IProjectionInvitationStore InvitationStore,
        TcpStreamServer StreamServer,
        TcpControlServer ControlServer,
        List<StubCaptureService> CaptureInstances);

    [TestMethod]
    public async Task ProjectionHostAndReceiver_FullMtlsStreamAndDynamicModeChange_Succeed()
    {
        using var peers = await CreatePairedPeersAsync();
        var displayService = new StubVirtualDisplayService();
        var invitationStore = new ProjectionInvitationStore();
        var captureInstances = new List<StubCaptureService>();

        var authority = new SessionAuthority(peers.SenderStore);
        using var streamServer = new TcpStreamServer(authority, TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore), peers.SenderStore);
        using var controlServer = new TcpControlServer(authority, null, TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore));

        await using var host = CreateProjectionHost(new HostCreationArgs(displayService, invitationStore, streamServer, controlServer, captureInstances));
        using var streamClient = new TcpStreamClient();
        using var controlClient = new TcpControlClient();
        var acceptedTcs = new TaskCompletionSource<DisplayInvitationAcceptanceContext>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var receiver = CreateProjectionReceiver(new ReceiverCreationArgs(peers, acceptedTcs, streamClient, controlClient));
        int signalingPort = GetAvailableTcpPort();
        await receiver.StartListeningAsync(signalingPort);

        var start = await host.StartProjectionAsync(new DisplayNegotiationRequest
        {
            TargetIp = "127.0.0.1",
            TargetPort = signalingPort,
            SslOptions = TlsConfigurationHelper.CreateClientOptions(peers.SenderCert, "localhost", peers.SenderStore),
            InvitationStore = invitationStore,
            HostStreamPort = GetAvailableTcpPort(),
            HostControlPort = GetAvailableTcpPort()
        });

        Assert.IsTrue(start.Success && host.IsProjecting);
        Assert.AreEqual((1920, @"\\.\DISPLAY9"), (captureInstances[0].TargetWidth, host.ActiveCaptureSource!.DeviceName));

        var acceptedContext = await acceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await VerifyStreamAndModeSwitchAsync(new StreamVerificationContext(acceptedContext, peers, receiver, streamClient, controlClient, captureInstances));
        await VerifyTeardownAsync(displayService, receiver, host);
    }

    private static VirtualDisplayProjectionHost CreateProjectionHost(HostCreationArgs args)
    {
        var topology = new FakeTopologyManager(new DisplayTopologySnapshot
        {
            Paths = new[] { new DisplayPathSnapshot { DeviceName = @"\\.\DISPLAY9", PositionX = 1920, Width = 1920, Height = 1080 } }
        });

        return new VirtualDisplayProjectionHost(new VirtualDisplayProjectionHostDependencies
        {
            DisplayService = args.DisplayService,
            InvitationStore = args.InvitationStore,
            StreamServer = args.StreamServer,
            ControlServer = args.ControlServer,
            TopologyManager = topology,
            CaptureFactory = _ =>
            {
                var stub = new StubCaptureService();
                args.CaptureInstances.Add(stub);
                return stub;
            }
        });
    }

    private static VirtualDisplayProjectionReceiver CreateProjectionReceiver(ReceiverCreationArgs args) =>
        new(new VirtualDisplayProjectionReceiverOptions
        {
            SignalingServerSslOptions = TlsConfigurationHelper.CreateServerOptions(args.Peers.ReceiverCert, args.Peers.ReceiverStore),
            CapabilitiesProvider = () => new DisplayCapabilitiesDto
            {
                DeviceName = "MonitorInalambricoB",
                SupportedModes = new[] { new VirtualDisplayMode(1920, 1080, 60), new VirtualDisplayMode(1280, 720, 60) }
            },
            ApprovalHandler = ctx =>
            {
                args.AcceptedTcs.TrySetResult(ctx);
                return Task.FromResult(true);
            },
            StreamClient = args.StreamClient,
            ControlClient = args.ControlClient
        });

    private static async Task VerifyStreamAndModeSwitchAsync(StreamVerificationContext ctx)
    {
        var receivedFrameTcs = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.StreamClient.FrameReceived += (_, f) => receivedFrameTcs.TrySetResult(f);

        var modeChangedTcs = new TaskCompletionSource<DisplaySessionControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        ctx.ControlClient.DisplayControlMessageReceived += (_, m) =>
        {
            if (m.Kind == DisplayControlMessageKind.ModeChanged)
                modeChangedTcs.TrySetResult(m);
        };

        var receiverSsl = TlsConfigurationHelper.CreateClientOptions(ctx.Peers.ReceiverCert, "localhost", ctx.Peers.ReceiverStore);
        await ctx.Receiver.ConnectToHostAsync(ctx.AcceptedContext, receiverSsl);
        ctx.CaptureInstances[0].EmitDummyFrame(1920, 1080);

        var frame = await receivedFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1920, frame.Width);

        await ctx.Receiver.RequestDisplayModeChangeAsync(new VirtualDisplayMode(1280, 720, 60));
        var modeChanged = await modeChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual((1280, 720, 2, 1280), (modeChanged.Width, modeChanged.Height, ctx.CaptureInstances.Count, ctx.CaptureInstances[1].TargetWidth));
    }

    private static async Task VerifyTeardownAsync(
        StubVirtualDisplayService displayService,
        VirtualDisplayProjectionReceiver receiver,
        VirtualDisplayProjectionHost host)
    {
        await receiver.DisconnectSessionAsync();
        await Task.Delay(150);
        Assert.IsTrue(displayService.DisconnectNotifications >= 1);

        await host.StopProjectionAsync();
        Assert.IsFalse(host.IsProjecting);
        Assert.AreEqual(VirtualDisplayState.Inactive, displayService.State);
    }

    [TestMethod]
    public async Task ProjectionHost_CleansUpServersWhenVirtualDisplayStartFails()
    {
        using var peers = await CreatePairedPeersAsync();
        var displayService = new StubVirtualDisplayService { ShouldFailStart = true };
        var invitationStore = new ProjectionInvitationStore();
        var captureInstances = new List<StubCaptureService>();

        var authority = new SessionAuthority(peers.SenderStore);
        using var streamServer = new TcpStreamServer(authority, TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore), peers.SenderStore);
        using var controlServer = new TcpControlServer(authority, null, TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore));

        await using var host = CreateProjectionHost(new HostCreationArgs(displayService, invitationStore, streamServer, controlServer, captureInstances));
        using var streamClient = new TcpStreamClient();
        using var controlClient = new TcpControlClient();
        var acceptedTcs = new TaskCompletionSource<DisplayInvitationAcceptanceContext>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var receiver = CreateProjectionReceiver(new ReceiverCreationArgs(peers, acceptedTcs, streamClient, controlClient));
        int signalingPort = GetAvailableTcpPort();
        await receiver.StartListeningAsync(signalingPort);

        var start = await host.StartProjectionAsync(new DisplayNegotiationRequest
        {
            TargetIp = "127.0.0.1",
            TargetPort = signalingPort,
            SslOptions = TlsConfigurationHelper.CreateClientOptions(peers.SenderCert, "localhost", peers.SenderStore),
            InvitationStore = invitationStore,
            HostStreamPort = GetAvailableTcpPort(),
            HostControlPort = GetAvailableTcpPort()
        });

        Assert.IsFalse(start.Success || host.IsProjecting);
        Assert.IsFalse(streamServer.IsRunning);
        Assert.AreEqual(VirtualDisplayErrorCode.DriverNotInstalled.ToString(), start.ErrorCode);
    }
}
