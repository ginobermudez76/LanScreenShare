using System;
using System.Collections.Generic;
using System.IO;
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
public class VirtualDisplayResilienceTests
{
    private sealed class ResilienceBackendStub : IVirtualDisplayBackend
    {
        public int DestroyCount { get; private set; }
        public VirtualDisplayBackendCapabilities Capabilities { get; } = new() { BackendId = "resilience-vdd" };
        public bool IsDriverInstalled() => true;

        public Task<VirtualDisplayInstance?> CreateVirtualMonitorAsync(VirtualDisplayMode mode, CancellationToken ct = default) =>
            Task.FromResult<VirtualDisplayInstance?>(new VirtualDisplayInstance
            {
                BackendId = "resilience-vdd",
                MonitorId = "m-1",
                DeviceName = @"\\.\DISPLAY9",
                Mode = mode
            });

        public Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default) => Task.FromResult(true);

        public Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default)
        {
            DestroyCount++;
            return Task.FromResult(true);
        }

        public Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default) => Task.FromResult(true);
    }

    private sealed class ResilienceTopologyStub : IDisplayTopologyManager
    {
        public int RestoreCount { get; private set; }
        public int ExtendCount { get; private set; }

        public DisplayTopologySnapshot CaptureSnapshot() => new()
        {
            Paths = new[] { new DisplayPathSnapshot { DeviceName = @"\\.\DISPLAY1", Width = 1920, Height = 1080 } }
        };

        public bool ApplyExtendTopology(string? preferredDeviceName, VirtualDisplayMode mode)
        {
            ExtendCount++;
            return true;
        }

        public bool RestoreSnapshot(DisplayTopologySnapshot snapshot)
        {
            RestoreCount++;
            return true;
        }
    }

    private sealed class StubCapture : IScreenCaptureService
    {
        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;
        public bool IsCapturing { get; private set; }
        public int TargetFps { get; set; } = 60;
        public int Quality { get; set; } = 85;
        public int BitrateMbps { get; set; } = 8;
        public double ResolutionScale { get; set; } = 1.0;
        public int TargetWidth { get; set; } = 1920;
        public int TargetHeight { get; set; } = 1080;
        public string Codec { get; set; } = "h264";

        public void StartCapture(CaptureProfile profile)
        {
            IsCapturing = true;
            SecureDesktopChanged?.Invoke(this, false);
        }

        public void StopCapture() => IsCapturing = false;
        public void UpdateSettings(CaptureProfile profile) { }
        public void RequestKeyframe() { }

        public void EmitFrameWithTimestamp(long timestampMs, int hostFrameTimeMs)
        {
            FrameCaptured?.Invoke(this, new CapturedFrame
            {
                Data = new byte[] { 0x00, 0x00, 0x00, 0x01, 0x65 },
                Width = TargetWidth,
                Height = TargetHeight,
                Timestamp = timestampMs,
                Format = "h264",
                HostFps = TargetFps,
                HostFrameTimeMs = hostFrameTimeMs
            });
        }

        public void Dispose() => StopCapture();
    }

    private sealed class DirectSignalingStub(int streamPort, string targetFingerprint) : IDisplaySignalingClient
    {
        public Task<DisplayNegotiationResult> NegotiateAndInviteAsync(DisplayNegotiationRequest request, CancellationToken ct = default)
        {
            var token = request.InvitationStore.IssueInvitation(new ProjectionInvitationRequest
            {
                TargetFingerprint = targetFingerprint,
                NegotiatedMode = request.PreferredMode ?? VirtualDisplayMode.Default,
                HostStreamPort = streamPort,
                HostControlPort = 0
            });

            return Task.FromResult(new DisplayNegotiationResult
            {
                Accepted = true,
                InvitationToken = token,
                PeerFingerprint = targetFingerprint,
                Capabilities = new DisplayCapabilitiesDto()
            });
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

    private static X509Certificate2 CreatePeerCert(string cn)
    {
        using var ecdsa = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var req = new CertificateRequest($"CN={cn}", ecdsa, HashAlgorithmName.SHA256);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, critical: true));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection
        {
            new Oid(TlsConfigurationHelper.ServerAuthOid),
            new Oid(TlsConfigurationHelper.ClientAuthOid)
        }, critical: true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddYears(1));
        return X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
    }

    [TestMethod]
    public async Task CoordinatorAndHost_ShortDropReconnectsWithoutDestroy_AndTimeoutDestroys()
    {
        string snapshotPath = Path.Combine(Path.GetTempPath(), $"lss-res-{Guid.NewGuid():N}.json");
        var backend = new ResilienceBackendStub();
        var topology = new ResilienceTopologyStub();

        await using var coordinator = new VirtualDisplayCoordinator(
            [backend],
            topology,
            watchdogLauncher: null,
            new VirtualDisplayCoordinatorOptions
            {
                SnapshotFilePath = snapshotPath,
                ReconnectTimeout = TimeSpan.FromMilliseconds(350),
                AutoEnterStreamingState = false
            });

        var start = await coordinator.StartExtendedDisplayAsync(VirtualDisplayMode.Default);
        Assert.IsTrue(start.Success);
        await coordinator.NotifyStreamConnectedAsync();

        // Micro-corte dentro de la ventana: mantiene el monitor virtual
        await coordinator.HandleNetworkDisconnectedAsync();
        Assert.AreEqual((VirtualDisplayState.Reconnecting, 0), (coordinator.State, backend.DestroyCount));

        await coordinator.HandleNetworkReconnectedAsync();
        Assert.AreEqual((VirtualDisplayState.Streaming, 0), (coordinator.State, backend.DestroyCount));

        // Ciclo de suspensión/reanudación conserva sesión y reaplica topología
        await coordinator.HandleSystemSuspendAsync();
        Assert.AreEqual(VirtualDisplayState.Suspended, coordinator.State);
        await coordinator.HandleSystemResumeAsync();
        Assert.AreEqual((VirtualDisplayState.Streaming, 0), (coordinator.State, backend.DestroyCount));

        // Corte prolongado > timeout destruye el monitor y restaura topología
        await coordinator.HandleNetworkDisconnectedAsync();
        await Task.Delay(550);
        Assert.AreEqual(VirtualDisplayState.Inactive, coordinator.State);
        Assert.IsTrue(backend.DestroyCount >= 1 && topology.RestoreCount >= 1);

        DisplayTopologySnapshot.DeleteFileIfExists(snapshotPath);
    }

    [TestMethod]
    public async Task ProjectionHost_StreamTelemetryTimestamps_PropagateOverMtlsLoopback()
    {
        using var senderCert = CreatePeerCert("HostA");
        using var receiverCert = CreatePeerCert("MonitorB");
        var store = new InMemoryPairedDeviceStore();
        string receiverFp = TlsConfigurationHelper.ComputeFingerprint(receiverCert);
        await store.AddPairedDeviceAsync(PairedDevice.Create("b", "MonitorB", receiverFp));
        await store.AddPairedDeviceAsync(PairedDevice.Create("a", "HostA", TlsConfigurationHelper.ComputeFingerprint(senderCert)));

        int streamPort = GetAvailableTcpPort();
        var invitationStore = new ProjectionInvitationStore();
        var backend = new ResilienceBackendStub();
        var topology = new ResilienceTopologyStub();
        string snapshotPath = Path.Combine(Path.GetTempPath(), $"lss-ts-{Guid.NewGuid():N}.json");

        await using var coordinator = new VirtualDisplayCoordinator([backend], topology, null, new VirtualDisplayCoordinatorOptions { SnapshotFilePath = snapshotPath, AutoEnterStreamingState = false });
        var stubCapture = new StubCapture();
        using var streamServer = new TcpStreamServer(new SessionAuthority(store), TlsConfigurationHelper.CreateServerOptions(senderCert, store), store);

        await using var host = new VirtualDisplayProjectionHost(new VirtualDisplayProjectionHostDependencies
        {
            DisplayService = coordinator,
            InvitationStore = invitationStore,
            StreamServer = streamServer,
            TopologyManager = topology,
            CaptureFactory = _ => stubCapture,
            SignalingClient = new DirectSignalingStub(streamPort, receiverFp)
        });

        var result = await host.StartProjectionAsync(new DisplayNegotiationRequest { TargetIp = "127.0.0.1", InvitationStore = invitationStore, HostStreamPort = streamPort });
        Assert.IsTrue(result.Success);

        using var client = new TcpStreamClient();
        var frameTcs = new TaskCompletionSource<CapturedFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.FrameReceived += (_, f) => frameTcs.TrySetResult(f);
        await client.ConnectAsync(new StreamClientConnectionOptions(
            "127.0.0.1", streamPort, TlsConfigurationHelper.CreateClientOptions(receiverCert, "localhost", store), result.InvitationToken!.TokenId));

        long expectedTs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        stubCapture.EmitFrameWithTimestamp(expectedTs, hostFrameTimeMs: 4);
        var received = await frameTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.AreEqual((expectedTs, 4), (received.Timestamp, received.HostFrameTimeMs));
        DisplayTopologySnapshot.DeleteFileIfExists(snapshotPath);
    }
}
