using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network;
using LanScreenShare.Network.Display;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class VirtualDisplaySignalingTests
{
    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class StubVirtualDisplayService : IVirtualDisplayService
    {
        private VirtualDisplayState _state = VirtualDisplayState.Inactive;
        public VirtualDisplayState State => _state;
        public VirtualDisplaySessionInfo? CurrentSession { get; private set; }
        public int DisconnectNotifications { get; private set; }
        public bool FailNextModeChange { get; set; }

        public event EventHandler<VirtualDisplayStateChangedEventArgs>? StateChanged;

        public Task<VirtualDisplayOperationResult> StartExtendedDisplayAsync(VirtualDisplayMode requestedMode, CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in requestedMode))
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.InvalidMode, "Invalid mode"));

            CurrentSession = new VirtualDisplaySessionInfo
            {
                BackendId = "stub-vdd",
                MonitorId = "vdd-1",
                DeviceName = @"\\.\DISPLAY9",
                ActiveMode = requestedMode,
                SnapshotPath = "unused.json"
            };
            SetState(VirtualDisplayState.ConnectingStream, "Waiting for receiver");
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task<VirtualDisplayOperationResult> NotifyStreamConnectedAsync(CancellationToken ct = default)
        {
            if (CurrentSession == null)
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.NoSession, "No session"));

            SetState(VirtualDisplayState.Streaming, "Streaming");
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task<VirtualDisplayOperationResult> ChangeDisplayModeAsync(VirtualDisplayMode newMode, CancellationToken ct = default)
        {
            if (FailNextModeChange || CurrentSession == null)
                return Task.FromResult(VirtualDisplayOperationResult.Fail(VirtualDisplayErrorCode.ModeUpdateFailed, "Rejected by stub"));

            CurrentSession = CurrentSession with { ActiveMode = newMode };
            return Task.FromResult(VirtualDisplayOperationResult.Ok(CurrentSession));
        }

        public Task StopExtendedDisplayAsync(CancellationToken ct = default)
        {
            CurrentSession = null;
            SetState(VirtualDisplayState.Inactive, "Stopped");
            return Task.CompletedTask;
        }

        public Task HandleNetworkDisconnectedAsync(CancellationToken ct = default)
        {
            DisconnectNotifications++;
            if (CurrentSession != null)
            {
                SetState(VirtualDisplayState.Reconnecting, "Reconnecting");
            }
            return Task.CompletedTask;
        }

        public Task HandleNetworkReconnectedAsync(CancellationToken ct = default) =>
            NotifyStreamConnectedAsync(ct);

        public Task HandleSystemSuspendAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task HandleSystemResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task RecoverOrphanedSessionOnStartupAsync(CancellationToken ct = default) => Task.CompletedTask;

        private void SetState(VirtualDisplayState next, string reason)
        {
            var prev = _state;
            _state = next;
            StateChanged?.Invoke(this, new VirtualDisplayStateChangedEventArgs
            {
                PreviousState = prev,
                CurrentState = next,
                Reason = reason
            });
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
        public int KeyframeRequests { get; private set; }

        public void StartCapture(CaptureProfile profile)
        {
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;
            TargetFps = profile.Fps;
            IsCapturing = true;
            SecureDesktopChanged?.Invoke(this, false);
        }

        public void StopCapture() => IsCapturing = false;

        public void UpdateSettings(CaptureProfile profile)
        {
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;
            TargetFps = profile.Fps;
        }

        public void RequestKeyframe() => KeyframeRequests++;

        public void EmitDummyFrame(int width, int height)
        {
            FrameCaptured?.Invoke(this, new CapturedFrame
            {
                Data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x67 },
                Width = width,
                Height = height,
                Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                Format = "h264",
                HostFps = TargetFps,
                HostFrameTimeMs = 2
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

    private static int GetAvailableTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    private static X509Certificate2 CreateBidirectionalPeerCertificate(string commonName)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={commonName}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(
            new X509KeyUsageExtension(
                X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment,
                critical: true));

        var eku = new OidCollection
        {
            new Oid(TlsConfigurationHelper.ServerAuthOid),
            new Oid(TlsConfigurationHelper.ClientAuthOid)
        };
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(eku, critical: true));

        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        byte[] pfx = cert.Export(X509ContentType.Pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null, X509KeyStorageFlags.Exportable);
    }

    private sealed record PairedPeerContext(
        X509Certificate2 SenderCert,
        X509Certificate2 ReceiverCert,
        string SenderFingerprint,
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

    private static async Task<PairedPeerContext> CreatePairedPeersAsync()
    {
        var senderCert = CreateBidirectionalPeerCertificate("SenderPeer");
        var receiverCert = CreateBidirectionalPeerCertificate("ReceiverPeer");
        string senderFp = TlsConfigurationHelper.ComputeFingerprint(senderCert);
        string receiverFp = TlsConfigurationHelper.ComputeFingerprint(receiverCert);

        var senderStore = new InMemoryPairedDeviceStore();
        await senderStore.AddPairedDeviceAsync(PairedDevice.Create("b", "ReceiverPeer", receiverFp));

        var receiverStore = new InMemoryPairedDeviceStore();
        await receiverStore.AddPairedDeviceAsync(PairedDevice.Create("a", "SenderPeer", senderFp));

        return new PairedPeerContext(senderCert, receiverCert, senderFp, receiverFp, senderStore, receiverStore);
    }

    [TestMethod]
    public void DeviceInfo_CapabilitiesAndWirelessDisplayProperties_NotifyChanges()
    {
        var device = new DeviceInfo();
        var changed = new List<string?>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.AreEqual(DeviceCapabilities.None, device.Capabilities);
        Assert.IsFalse(device.IsWirelessDisplay);
        Assert.IsFalse(device.SupportsScreenHost);

        device.Capabilities = DeviceCapabilities.ScreenHost | DeviceCapabilities.WirelessDisplay;
        device.DisplaySignalingPort = 5005;
        device.IsWirelessDisplayBusy = true;

        Assert.IsTrue(device.SupportsScreenHost);
        Assert.IsTrue(device.IsWirelessDisplay);
        Assert.AreEqual(5005, device.DisplaySignalingPort);
        Assert.IsTrue(device.IsWirelessDisplayBusy);
        CollectionAssert.Contains(changed, nameof(DeviceInfo.Capabilities));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IsWirelessDisplay));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.SupportsScreenHost));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.DisplaySignalingPort));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IsWirelessDisplayBusy));
    }

    [TestMethod]
    public void DiscoveryService_WirelessDisplayDatagrams_ParseAndValidateCleanly()
    {
        var validCases = new[]
        {
            new { Payload = "LAN_SCREEN_SHARE_DISPLAY_V1|MONITOR-B|192.168.1.40|5005|0", Port = 5005, Busy = false },
            new { Payload = "LAN_SCREEN_SHARE_DISPLAY_V1|MONITOR-C|192.168.1.41|5006|1", Port = 5006, Busy = true },
            new { Payload = "LAN_SCREEN_SHARE_DISPLAY_V1|MONITOR-D|127.0.0.1|5007", Port = 5007, Busy = false }
        };

        foreach (var item in validCases)
        {
            var service = new DiscoveryService();
            DeviceInfo? found = null;
            service.OnDeviceDiscovered += (_, d) => found = d;

            byte[] bytes = Encoding.UTF8.GetBytes(item.Payload);
            service.ProcessIncomingDatagram(new UdpReceiveResult(bytes, new IPEndPoint(IPAddress.Parse("192.168.1.99"), 15000)));

            Assert.IsNotNull(found);
            Assert.IsTrue(found.IsWirelessDisplay);
            Assert.IsFalse(found.SupportsScreenHost);
            Assert.IsFalse(found.IsRemoteControlAllowed);
            Assert.AreEqual(item.Port, found.DisplaySignalingPort);
            Assert.AreEqual(item.Busy, found.IsWirelessDisplayBusy);
        }

        var invalidPayloads = new[]
        {
            "LAN_SCREEN_SHARE_DISPLAY_V1|NO_PORT|192.168.1.50",
            "LAN_SCREEN_SHARE_DISPLAY_V1|LOW_PORT|192.168.1.50|80|0",
            "LAN_SCREEN_SHARE_DISPLAY_V1|HIGH_PORT|192.168.1.50|70000|0",
            "LAN_SCREEN_SHARE_DISPLAY_V1|BAD_PORT|192.168.1.50|abc|0",
            $"LAN_SCREEN_SHARE_DISPLAY_V1|{Environment.MachineName}|127.0.0.1|5005|0"
        };

        foreach (string invalid in invalidPayloads)
        {
            var service = new DiscoveryService();
            int count = 0;
            service.OnDeviceDiscovered += (_, _) => count++;
            service.ProcessIncomingDatagram(new UdpReceiveResult(Encoding.UTF8.GetBytes(invalid), new IPEndPoint(IPAddress.Loopback, 15000)));
            Assert.AreEqual(0, count, $"Payload debería ser ignorado: {invalid}");
        }
    }

    [TestMethod]
    public void DiscoveryService_StartWirelessDisplayBroadcast_ValidatesPortAndToggles()
    {
        var service = new DiscoveryService(GetAvailableUdpPort())
        {
            IsWirelessDisplayBusyProvider = () => true
        };

        Assert.ThrowsException<ArgumentOutOfRangeException>(() => service.StartWirelessDisplayBroadcast(80));
        service.StartWirelessDisplayBroadcast(5005);
        service.StopWirelessDisplayBroadcast();
    }

    [TestMethod]
    public void ProjectionInvitationStore_Enforces256BitSingleUseFingerprintAndTtl()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero));
        var store = new ProjectionInvitationStore(clock);

        Assert.ThrowsException<ArgumentException>(() => store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = " ",
            NegotiatedMode = VirtualDisplayMode.Default
        }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:AABB",
            NegotiatedMode = new VirtualDisplayMode(100, 100, 60)
        }));
        Assert.ThrowsException<ArgumentOutOfRangeException>(() => store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:AABB",
            NegotiatedMode = VirtualDisplayMode.Default,
            Ttl = TimeSpan.Zero
        }));

        var token = store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:ABCD1234",
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });

        Assert.AreEqual(64, token.TokenId.Length);
        Assert.IsTrue(Guid.TryParse(token.SessionId, out _));

        // Fingerprint mismatch rejects & atomically consumes to block brute force
        var mismatch = store.ConsumeInvitation(token.TokenId, "SHA256:OTHER");
        Assert.IsFalse(mismatch.Accepted);

        var token2 = store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:ABCD1234",
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });

        var ok = store.ConsumeInvitation(token2.TokenId.ToUpperInvariant(), "sha256:abcd1234");
        Assert.IsTrue(ok.Accepted);
        Assert.AreEqual(token2.SessionId, ok.Token!.SessionId);

        // Single-use replay rejected
        var replay = store.ConsumeInvitation(token2.TokenId, "SHA256:ABCD1234");
        Assert.IsFalse(replay.Accepted);

        // Expiration after 15s TTL
        var expiring = store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:ABCD1234",
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });
        clock.Advance(TimeSpan.FromSeconds(16));
        var expiredResult = store.ConsumeInvitation(expiring.TokenId, "SHA256:ABCD1234");
        Assert.IsFalse(expiredResult.Accepted);

        var pruned = store.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = "SHA256:ABCD1234",
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });
        clock.Advance(TimeSpan.FromSeconds(16));
        store.PruneExpired();
        Assert.IsFalse(store.ConsumeInvitation(pruned.TokenId, "SHA256:ABCD1234").Accepted);
    }

    [TestMethod]
    public async Task DisplayControlPacketProtocol_RoundTripAndValidation_WorkCorrectly()
    {
        var messages = new[]
        {
            DisplaySessionControlMessage.CreateModeChangeRequest(new VirtualDisplayMode(2560, 1440, 120)),
            DisplaySessionControlMessage.CreateModeChangeAccepted(new VirtualDisplayMode(2560, 1440, 120)),
            DisplaySessionControlMessage.CreateModeChangeRejected("Unsupported"),
            DisplaySessionControlMessage.CreateModeChanged(new VirtualDisplayMode(1920, 1080, 60))
        };

        foreach (var msg in messages)
        {
            byte[] packet = DisplayControlPacketProtocol.SerializePacket(42, msg);
            Assert.IsTrue(DisplayControlPacketProtocol.IsDisplayControlHeader(packet));
            Assert.IsTrue(DisplayControlPacketProtocol.TryParseHeader(packet, out var header, out string? error));
            Assert.IsNull(error);
            Assert.AreEqual(42UL, header.SequenceNumber);
            Assert.AreEqual(msg.Kind, header.Kind);

            await using var ms = new MemoryStream(packet, DisplayControlPacketProtocol.HeaderSize, packet.Length - DisplayControlPacketProtocol.HeaderSize);
            var parsed = await DisplayControlPacketProtocol.ReadMessagePayloadAsync(ms, header.PayloadLength);
            Assert.AreEqual(msg, parsed);
        }

        byte[] badMagic = new byte[DisplayControlPacketProtocol.HeaderSize];
        Assert.IsFalse(DisplayControlPacketProtocol.TryParseHeader(badMagic, out _, out _));

        byte[] invalidPayload = new byte[DisplayControlPacketProtocol.HeaderSize];
        Array.Copy(DisplayControlPacketProtocol.SerializePacket(1, messages[0]), invalidPayload, DisplayControlPacketProtocol.HeaderSize);
        invalidPayload[27] = 0;
        invalidPayload[26] = 0;
        invalidPayload[25] = 0;
        invalidPayload[24] = 0; // zero payload length
        Assert.IsFalse(DisplayControlPacketProtocol.TryParseHeader(invalidPayload, out _, out _));
    }

    [TestMethod]
    public void VirtualDisplayCaptureSourceFactory_MapsSnapshotBoundsOrDefaults()
    {
        var session = new VirtualDisplaySessionInfo
        {
            BackendId = "vdd",
            MonitorId = "m1",
            DeviceName = @"\\.\DISPLAY8",
            ActiveMode = new VirtualDisplayMode(2560, 1440, 60),
            SnapshotPath = "snap.json"
        };

        var topology = new DisplayTopologySnapshot
        {
            Paths = new[]
            {
                new DisplayPathSnapshot
                {
                    DeviceName = @"\\.\DISPLAY8",
                    PositionX = 1920,
                    PositionY = 0,
                    Width = 2560,
                    Height = 1440
                }
            }
        };

        var boundSource = VirtualDisplayCaptureSourceFactory.CreateCaptureSource(session, topology);
        Assert.AreEqual(@"\\.\DISPLAY8", boundSource.DeviceName);
        Assert.AreEqual((1920, 0, 2560, 1440), (boundSource.X, boundSource.Y, boundSource.Width, boundSource.Height));
        Assert.IsFalse(boundSource.IsPrimary);

        var fallbackSource = VirtualDisplayCaptureSourceFactory.CreateCaptureSource(session);
        Assert.AreEqual((0, 0, 2560, 1440), (fallbackSource.X, fallbackSource.Y, fallbackSource.Width, fallbackSource.Height));
    }

    [TestMethod]
    public async Task DisplaySignaling_MtlsNegotiationAndBusyRejection_WorkEndToEnd()
    {
        using var peers = await CreatePairedPeersAsync();
        bool busy = false;
        int port = GetAvailableTcpPort();

        var serverSsl = TlsConfigurationHelper.CreateServerOptions(peers.ReceiverCert, peers.ReceiverStore);
        var clientSsl = TlsConfigurationHelper.CreateClientOptions(peers.SenderCert, "localhost", peers.SenderStore);

        using var server = new DisplaySignalingServer(new DisplaySignalingServerOptions
        {
            SslServerOptions = serverSsl,
            IsBusyProvider = () => busy,
            CapabilitiesProvider = () => new DisplayCapabilitiesDto
            {
                DeviceName = "WirelessMonitorB",
                SupportedModes = new[]
                {
                    new VirtualDisplayMode(9999, 9999, 240), // fuera de whitelist: debe filtrarse
                    new VirtualDisplayMode(1920, 1080, 60),
                    new VirtualDisplayMode(2560, 1440, 120)
                }
            }
        });
        await server.StartAsync(port);

        var store = new ProjectionInvitationStore();
        var client = new DisplaySignalingClient();

        var ok = await client.NegotiateAndInviteAsync(new DisplayNegotiationRequest
        {
            TargetIp = "127.0.0.1",
            TargetPort = port,
            SslOptions = clientSsl,
            InvitationStore = store,
            PreferredMode = new VirtualDisplayMode(1920, 1080, 60)
        });

        Assert.IsTrue(ok.Accepted);
        Assert.IsNotNull(ok.InvitationToken);
        Assert.AreEqual(new VirtualDisplayMode(1920, 1080, 60), ok.InvitationToken.NegotiatedMode);
        Assert.AreEqual(peers.ReceiverFingerprint, ok.PeerFingerprint);

        // Busy enforces 1:1 rule with ERROR_DISPLAY_BUSY
        busy = true;
        var busyResult = await client.NegotiateAndInviteAsync(new DisplayNegotiationRequest
        {
            TargetIp = "127.0.0.1",
            TargetPort = port,
            SslOptions = clientSsl,
            InvitationStore = store
        });

        Assert.IsFalse(busyResult.Accepted);
        Assert.AreEqual(DisplaySignalingMessage.ErrorDisplayBusy, busyResult.ErrorCode);
    }

    [TestMethod]
    public async Task TcpStreamServer_ProjectionInvitationRequired_EnforcesValidSingleUseToken()
    {
        using var peers = await CreatePairedPeersAsync();
        using var rogueCert = CreateBidirectionalPeerCertificate("RoguePeer");
        await peers.SenderStore.AddPairedDeviceAsync(
            PairedDevice.Create("rogue", "RoguePeer", TlsConfigurationHelper.ComputeFingerprint(rogueCert)));

        var invitationStore = new ProjectionInvitationStore();
        var token = invitationStore.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = peers.ReceiverFingerprint,
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });

        int streamPort = GetAvailableTcpPort();
        var authority = new SessionAuthority(peers.SenderStore);
        using var server = new TcpStreamServer(
            authority,
            TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore),
            peers.SenderStore)
        {
            ProjectionInvitationStore = invitationStore,
            RequireProjectionInvitation = true
        };
        await server.StartAsync(streamPort);

        // 1. Rogue peer con token de otro receptor debe ser rechazado
        using var rogueClient = new TcpStreamClient();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await rogueClient.ConnectAsync(new StreamClientConnectionOptions(
                "127.0.0.1",
                streamPort,
                TlsConfigurationHelper.CreateClientOptions(rogueCert, "localhost", peers.ReceiverStore),
                token.TokenId));
        });

        // 2. El token previo fue consumido/invalidado al intentar usarlo con otro fingerprint; emitimos uno nuevo para B
        var freshToken = invitationStore.IssueInvitation(new ProjectionInvitationRequest
        {
            TargetFingerprint = peers.ReceiverFingerprint,
            NegotiatedMode = new VirtualDisplayMode(1920, 1080, 60)
        });

        using var validClient = new TcpStreamClient();
        await validClient.ConnectAsync(new StreamClientConnectionOptions(
            "127.0.0.1",
            streamPort,
            TlsConfigurationHelper.CreateClientOptions(peers.ReceiverCert, "localhost", peers.ReceiverStore),
            freshToken.TokenId));
        Assert.IsTrue(validClient.IsConnected);

        // 3. Reintentar el mismo token debe fallar (un solo uso)
        using var replayClient = new TcpStreamClient();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () =>
        {
            await replayClient.ConnectAsync(new StreamClientConnectionOptions(
                "127.0.0.1",
                streamPort,
                TlsConfigurationHelper.CreateClientOptions(peers.ReceiverCert, "localhost", peers.ReceiverStore),
                freshToken.TokenId));
        });
    }

    [TestMethod]
    public async Task ProjectionHostAndReceiver_FullMtlsStreamAndDynamicModeChange_Succeed()
    {
        using var peers = await CreatePairedPeersAsync();
        int signalingPort = GetAvailableTcpPort();
        int streamPort = GetAvailableTcpPort();
        int controlPort = GetAvailableTcpPort();

        var displayService = new StubVirtualDisplayService();
        var invitationStore = new ProjectionInvitationStore();
        var captureInstances = new List<StubCaptureService>();

        var senderAuthority = new SessionAuthority(peers.SenderStore);
        using var streamServer = new TcpStreamServer(
            senderAuthority,
            TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore),
            peers.SenderStore);
        using var controlServer = new TcpControlServer(
            senderAuthority,
            null,
            TlsConfigurationHelper.CreateServerOptions(peers.SenderCert, peers.SenderStore));

        var topology = new FakeTopologyManager(new DisplayTopologySnapshot
        {
            Paths = new[]
            {
                new DisplayPathSnapshot
                {
                    DeviceName = @"\\.\DISPLAY9",
                    PositionX = 1920,
                    PositionY = 0,
                    Width = 1920,
                    Height = 1080
                }
            }
        });

        await using var projectionHost = new VirtualDisplayProjectionHost(new VirtualDisplayProjectionHostDependencies
        {
            DisplayService = displayService,
            InvitationStore = invitationStore,
            StreamServer = streamServer,
            ControlServer = controlServer,
            TopologyManager = topology,
            CaptureFactory = _ =>
            {
                var stub = new StubCaptureService();
                captureInstances.Add(stub);
                return stub;
            }
        });

        using var receiverStreamClient = new TcpStreamClient();
        using var receiverControlClient = new TcpControlClient();
        var acceptedTcs = new TaskCompletionSource<DisplayInvitationAcceptanceContext>(TaskCreationOptions.RunContinuationsAsynchronously);

        await using var projectionReceiver = new VirtualDisplayProjectionReceiver(new VirtualDisplayProjectionReceiverOptions
        {
            SignalingServerSslOptions = TlsConfigurationHelper.CreateServerOptions(peers.ReceiverCert, peers.ReceiverStore),
            CapabilitiesProvider = () => new DisplayCapabilitiesDto
            {
                DeviceName = "MonitorInalambricoB",
                SupportedModes = new[] { new VirtualDisplayMode(1920, 1080, 60), new VirtualDisplayMode(1280, 720, 60) }
            },
            ApprovalHandler = ctx =>
            {
                acceptedTcs.TrySetResult(ctx);
                return Task.FromResult(true);
            },
            StreamClient = receiverStreamClient,
            ControlClient = receiverControlClient
        });
        await projectionReceiver.StartListeningAsync(signalingPort);

        var startResult = await projectionHost.StartProjectionAsync(new DisplayNegotiationRequest
        {
            TargetIp = "127.0.0.1",
            TargetPort = signalingPort,
            SslOptions = TlsConfigurationHelper.CreateClientOptions(peers.SenderCert, "localhost", peers.SenderStore),
            InvitationStore = invitationStore,
            PreferredMode = new VirtualDisplayMode(1920, 1080, 60),
            HostStreamPort = streamPort,
            HostControlPort = controlPort
        });

        Assert.IsTrue(startResult.Success);
        Assert.IsTrue(projectionHost.IsProjecting);
        Assert.AreEqual(1, captureInstances.Count);
        Assert.IsTrue(captureInstances[0].IsCapturing);
        Assert.AreEqual(1920, captureInstances[0].TargetWidth);
        Assert.AreEqual(@"\\.\DISPLAY9", projectionHost.ActiveCaptureSource!.DeviceName);

        var receivedFrameTcs = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverStreamClient.FrameReceived += (_, f) => receivedFrameTcs.TrySetResult(f);

        var modeChangedTcs = new TaskCompletionSource<DisplaySessionControlMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        receiverControlClient.DisplayControlMessageReceived += (_, m) =>
        {
            if (m.Kind == DisplayControlMessageKind.ModeChanged)
                modeChangedTcs.TrySetResult(m);
        };

        var acceptedContext = await acceptedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await projectionReceiver.ConnectToHostAsync(
            acceptedContext,
            TlsConfigurationHelper.CreateClientOptions(peers.ReceiverCert, "localhost", peers.ReceiverStore));

        Assert.IsTrue(receiverStreamClient.IsConnected);
        Assert.IsTrue(receiverControlClient.IsConnected);
        Assert.IsTrue(projectionReceiver.IsSessionBusy);

        captureInstances[0].EmitDummyFrame(1920, 1080);
        var frame = await receivedFrameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1920, frame.Width);

        // Hot display mode change separated from InputEventType
        await projectionReceiver.RequestDisplayModeChangeAsync(new VirtualDisplayMode(1280, 720, 60));
        var modeChanged = await modeChangedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1280, modeChanged.Width);
        Assert.AreEqual(720, modeChanged.Height);
        Assert.AreEqual(2, captureInstances.Count);
        Assert.AreEqual(1280, captureInstances[1].TargetWidth);

        await projectionReceiver.DisconnectSessionAsync();
        await Task.Delay(150);
        Assert.IsTrue(displayService.DisconnectNotifications >= 1);

        await projectionHost.StopProjectionAsync();
        Assert.IsFalse(projectionHost.IsProjecting);
        Assert.AreEqual(VirtualDisplayState.Inactive, displayService.State);
    }
}
