using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using LanScreenShare.Core.Security;
using LanScreenShare.Network.Security;
using LanScreenShare.Network.Streaming;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

internal sealed class TrackingInputSimulator : IInputSimulatorService
{
    public int SimulatedCount { get; private set; }
    public int ReleaseCallsCount { get; private set; }
    public bool Disposed { get; private set; }

    public void SimulateInput(RemoteInputMessage input)
    {
        SimulatedCount++;
    }

    public void SetTargetSource(CaptureSource source)
    {
        // No-op test double
    }

    public void ReleaseInjectedInputs() => ReleaseCallsCount++;

    public void Dispose() => Disposed = true;
}

internal sealed class TempIdentityStorage : IIdentityKeyStorage, IDisposable
{
    private readonly string _rootDir;
    private readonly CrossPlatformIdentityKeyStorage _inner;

    public int ResetCount { get; private set; }

    public TempIdentityStorage(string prefix)
    {
        _rootDir = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}");
        _inner = new CrossPlatformIdentityKeyStorage(Path.Combine(_rootDir, "identity.pfx"));
    }

    public Task<X509Certificate2> GetOrCreateIdentityCertificateAsync() =>
        _inner.GetOrCreateIdentityCertificateAsync();

    public async Task ResetIdentityAsync()
    {
        ResetCount++;
        await _inner.ResetIdentityAsync();
    }

    public void Dispose()
    {
        if (Directory.Exists(_rootDir))
        {
            try { Directory.Delete(_rootDir, recursive: true); } catch { /* Best-effort temp cleanup */ }
        }
    }
}

internal sealed class FourChannelMtlsHarness : IAsyncDisposable
{
    private readonly TempIdentityStorage _hostId = new("lss-host-4ch");
    private readonly TempIdentityStorage _viewerId = new("lss-viewer-4ch");

    public InMemoryPairedDeviceStore Store { get; } = new();
    public SessionAuthority Authority { get; }
    public TrackingInputSimulator Simulator { get; } = new();
    public TcpStreamServer StreamServer { get; }
    public TcpAudioServer AudioServer { get; }
    public TcpControlServer ControlServer { get; }
    public TcpFileTransferServer FileServer { get; }
    public int StreamPort { get; }
    public int AudioPort { get; }
    public int ControlPort { get; }
    public int FilePort { get; }
    public X509Certificate2 HostCert { get; private set; } = null!;
    public X509Certificate2 ViewerCert { get; private set; } = null!;
    public string ViewerFingerprint { get; private set; } = string.Empty;
    public System.Net.Security.SslClientAuthenticationOptions ClientSsl { get; private set; } = null!;

    private FourChannelMtlsHarness()
    {
        Authority = new SessionAuthority(Store);
        StreamPort = GetFreeTcpPort();
        AudioPort = GetFreeTcpPort();
        ControlPort = GetFreeTcpPort();
        FilePort = GetFreeTcpPort();
        StreamServer = null!;
        AudioServer = null!;
        ControlServer = null!;
        FileServer = null!;
    }

    private FourChannelMtlsHarness(
        InMemoryPairedDeviceStore store,
        SessionAuthority authority,
        System.Net.Security.SslServerAuthenticationOptions serverSsl,
        (int Stream, int Audio, int Control, int File) ports)
    {
        Store = store;
        Authority = authority;
        StreamPort = ports.Stream;
        AudioPort = ports.Audio;
        ControlPort = ports.Control;
        FilePort = ports.File;
        StreamServer = new TcpStreamServer(authority, serverSsl, store);
        AudioServer = new TcpAudioServer(authority, serverSsl);
        ControlServer = new TcpControlServer(authority, Simulator, serverSsl);
        FileServer = new TcpFileTransferServer(authority, serverSsl);
    }

    private static int GetFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    public static async Task<FourChannelMtlsHarness> CreateAndStartAsync(
        DevicePermissions viewerPermissions,
        DateTime viewerLastSeen)
    {
        using var hostId = new TempIdentityStorage("lss-4ch-h");
        using var viewerId = new TempIdentityStorage("lss-4ch-v");
        var hostCert = await hostId.GetOrCreateIdentityCertificateAsync();
        var viewerCert = await viewerId.GetOrCreateIdentityCertificateAsync();

        string hostFp = TlsConfigurationHelper.ComputeFingerprint(hostCert);
        string viewerFp = TlsConfigurationHelper.ComputeFingerprint(viewerCert);

        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(new PairedDevice
        {
            DeviceId = "h",
            DeviceName = "Host",
            PublicKeyFingerprint = hostFp,
            PairedAt = viewerLastSeen,
            LastSeenAt = viewerLastSeen
        });
        await store.AddPairedDeviceAsync(new PairedDevice
        {
            DeviceId = "v",
            DeviceName = "Viewer",
            PublicKeyFingerprint = viewerFp,
            PairedAt = viewerLastSeen,
            LastSeenAt = viewerLastSeen,
            Permissions = viewerPermissions
        });

        var authority = new SessionAuthority(store);
        var serverSsl = TlsConfigurationHelper.CreateServerOptions(hostCert, store);
        var clientSsl = TlsConfigurationHelper.CreateClientOptions(viewerCert, "localhost", store);

        var ports = (GetFreeTcpPort(), GetFreeTcpPort(), GetFreeTcpPort(), GetFreeTcpPort());
        var harness = new FourChannelMtlsHarness(store, authority, serverSsl, ports)
        {
            HostCert = hostCert,
            ViewerCert = viewerCert,
            ViewerFingerprint = viewerFp,
            ClientSsl = clientSsl
        };

        await harness.StreamServer.StartAsync(harness.StreamPort);
        await harness.AudioServer.StartAsync(harness.AudioPort);
        await harness.ControlServer.StartAsync(harness.ControlPort);
        await harness.FileServer.StartAsync(harness.FilePort);
        return harness;
    }

    public ValueTask DisposeAsync()
    {
        StreamServer.Dispose();
        AudioServer.Dispose();
        ControlServer.Dispose();
        FileServer.Dispose();
        HostCert.Dispose();
        ViewerCert.Dispose();
        _hostId.Dispose();
        _viewerId.Dispose();
        return ValueTask.CompletedTask;
    }
}

[TestClass]
public class TrustedDeviceManagementTests
{
    private const string FingerprintA = "SHA256:AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899";
    private const string FingerprintB = "SHA256:1111222233334444555566667777888899990000AAAABBBBCCCCDDDDEEEEFFFF";

    private static PairedDevice CreateDevice(string name, string fingerprint, bool isRevoked = false) =>
        new()
        {
            DeviceId = Guid.NewGuid().ToString("D"),
            DeviceName = name,
            PublicKeyFingerprint = fingerprint,
            PairedAt = DateTime.UtcNow.AddDays(-2),
            LastSeenAt = DateTime.UtcNow.AddHours(-1),
            IsRevoked = isRevoked
        };

    [TestMethod]
    public async Task JsonPairedDeviceStore_LifecyclePermissionsLastSeenAndLegacyDeserialization_WorkCorrectly()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "lss-trust-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        string storePath = Path.Combine(tempDir, "trusted_devices.json");

        try
        {
            const string legacyJson = """
            [
              {
                "DeviceId": "legacy-1",
                "DeviceName": "PC Antigua",
                "PublicKeyFingerprint": "SHA256:AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
                "PairedAt": "2026-01-01T10:00:00Z",
                "LastSeenAt": "2026-01-01T11:00:00Z",
                "IsRevoked": false
              }
            ]
            """;
            await File.WriteAllTextAsync(storePath, legacyJson);

            var store = new JsonPairedDeviceStore(storePath);
            var loaded = await store.GetPairedDeviceAsync(FingerprintA.ToLowerInvariant());
            Assert.IsNotNull(loaded);
            Assert.AreEqual(("PC Antigua", DeviceAccessMode.Auto, true), (loaded.DisplayName, loaded.EffectivePermissions.AccessMode, loaded.EffectivePermissions.AllowFileTransfer));

            DateTime seenAt = new(2026, 11, 15, 12, 30, 0, DateTimeKind.Utc);
            await store.UpdateLastSeenAsync(FingerprintA, seenAt);
            var updatedPerms = new DevicePermissions
            {
                AccessMode = DeviceAccessMode.AskEveryTime,
                AllowRemoteControl = false,
                AllowClipboardSync = true,
                AllowFileTransfer = false
            };
            await store.UpdateDeviceSettingsAsync(FingerprintA, "  Laptop Oficina  ", updatedPerms);
            await store.RevokeDeviceAsync(FingerprintA);

            var revoked = await store.GetPairedDeviceAsync(FingerprintA);
            Assert.IsTrue(revoked!.IsRevoked);
            Assert.AreEqual(("Laptop Oficina", seenAt, DeviceAccessMode.AskEveryTime), (revoked.DisplayName, revoked.LastSeenAt, revoked.EffectivePermissions.AccessMode));

            await store.UnrevokeDeviceAsync(FingerprintA);
            var restored = await store.GetPairedDeviceAsync(FingerprintA);
            Assert.IsFalse(restored!.IsRevoked);

            await store.ClearAllDevicesAsync();
            var empty = await store.GetAllPairedDevicesAsync();
            Assert.AreEqual(0, empty.Count);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TrustedDeviceManager_FormatShortFingerprint_FormatsLongAndShortValues()
    {
        Assert.AreEqual("Desconocida", TrustedDeviceManager.FormatShortFingerprint(" "));
        Assert.AreEqual("SHA256:1234", TrustedDeviceManager.FormatShortFingerprint("SHA256:1234"));
        Assert.AreEqual("AABBCCDD…66778899", TrustedDeviceManager.FormatShortFingerprint(FingerprintA));
    }

    [TestMethod]
    public async Task TrustedDeviceManager_RevokeRemoveAndControlDrop_EnforceHotKillAndReleaseInputs()
    {
        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(CreateDevice("Equipo A", FingerprintA));
        await store.AddPairedDeviceAsync(CreateDevice("Equipo B", FingerprintB, isRevoked: true));

        var authority = new SessionAuthority(store);
        var simulator = new TrackingInputSimulator();
        using var identity = new TempIdentityStorage("lss-mgr-1");
        var manager = new TrustedDeviceManager(store, authority, identity, () => simulator);

        var (ok, sessionA, _) = await authority.TryCreateSessionAsync(FingerprintA, new DeviceName("Equipo A"));
        Assert.IsTrue(ok);
        authority.TryAcquireControl(sessionA!.SessionId, out _);

        int terminatedCount = 0;
        authority.SessionTerminated += (_, id) =>
        {
            if (id == sessionA.SessionId.Value) terminatedCount++;
        };

        var items = await manager.GetTrustedDevicesAsync();
        Assert.AreEqual(2, await manager.GetTrustedDeviceCountAsync());
        Assert.AreEqual((TrustedDeviceStatus.Connected, true, TrustedDeviceStatus.Revoked), (items[0].Status, items[0].IsActiveController, items[1].Status));

        manager.RevokeRemoteControlForDevice(FingerprintA);
        Assert.IsNull(authority.ActiveControllerSessionId);
        Assert.AreEqual((RemoteControlState.Revoked, 1, SessionState.Active), (sessionA.ControlState, simulator.ReleaseCallsCount, sessionA.State));

        await manager.RevokeDeviceAsync(FingerprintA);
        Assert.AreEqual((1, 2), (terminatedCount, simulator.ReleaseCallsCount));
        Assert.IsTrue((await store.GetPairedDeviceAsync(FingerprintA))!.IsRevoked);

        await manager.UnrevokeDeviceAsync(FingerprintA);
        Assert.IsFalse((await store.GetPairedDeviceAsync(FingerprintA))!.IsRevoked);

        await manager.RemoveDeviceAsync(FingerprintA);
        Assert.IsNull(await store.GetPairedDeviceAsync(FingerprintA));
    }

    [TestMethod]
    public async Task TrustedDeviceManager_LivePermissionsPanicAndIdentityRotation_BehaveAsExpected()
    {
        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(CreateDevice("Equipo A", FingerprintA));
        await store.AddPairedDeviceAsync(CreateDevice("Equipo B", FingerprintB));

        var authority = new SessionAuthority(store);
        var simulator = new TrackingInputSimulator();
        using var identity = new TempIdentityStorage("lss-mgr-2");
        var manager = new TrustedDeviceManager(store, authority, identity, () => simulator);

        using var initialCert = await identity.GetOrCreateIdentityCertificateAsync();
        string initialThumbprint = initialCert.Thumbprint;
        var (_, sessionA, _) = await authority.TryCreateSessionAsync(FingerprintA);
        authority.TryAcquireControl(sessionA!.SessionId, out _);

        await manager.UpdateDeviceSettingsAsync(
            FingerprintA,
            "Mi Alias",
            new DevicePermissions
            {
                AccessMode = DeviceAccessMode.AskEveryTime,
                AllowRemoteControl = false,
                AllowClipboardSync = false,
                AllowFileTransfer = false
            });

        Assert.AreEqual((false, false, false), (sessionA.ControlPermissionAllowed, sessionA.ClipboardAuthorized, sessionA.FileTransferAuthorized));
        Assert.AreEqual((RemoteControlState.Revoked, 1), (authority.RequestRemoteControl(sessionA.SessionId), simulator.ReleaseCallsCount));

        await manager.RevokeAllDevicesAsync();
        Assert.AreEqual((0, 0), ((await store.GetAllPairedDevicesAsync()).Count, authority.GetActiveSessions().Count));

        await store.AddPairedDeviceAsync(CreateDevice("Equipo C", FingerprintA));
        await authority.TryCreateSessionAsync(FingerprintA);

        using var rotatedCert = await manager.RegenerateIdentityAsync();
        Assert.AreNotEqual(initialThumbprint, rotatedCert.Thumbprint);
        Assert.AreEqual((0, 1), (authority.GetActiveSessions().Count, identity.ResetCount));
    }

    [TestMethod]
    public async Task MultiChannelMtlsServers_UpdateLastSeenEnforceAskEveryTimeAndBlockDeniedFileChannel()
    {
        DateTime staleSeenAt = DateTime.UtcNow.AddDays(-5);
        await using var harness = await FourChannelMtlsHarness.CreateAndStartAsync(
            new DevicePermissions
            {
                AccessMode = DeviceAccessMode.AskEveryTime,
                AllowRemoteControl = true,
                AllowClipboardSync = true,
                AllowFileTransfer = false
            },
            staleSeenAt);

        int explicitPromptCount = 0;
        harness.StreamServer.ClientApprovalCallback = _ => Task.FromResult(true);
        harness.StreamServer.ExplicitApprovalCallback = _ =>
        {
            Interlocked.Increment(ref explicitPromptCount);
            return Task.FromResult(true);
        };

        using var streamClient = new TcpStreamClient();
        await streamClient.ConnectAsync("127.0.0.1", harness.StreamPort, harness.ClientSsl);
        var handshake = streamClient.ActiveSessionHandshake!;
        Assert.AreEqual(("SESSION_OK", 1), (handshake.Type, explicitPromptCount));

        var updatedRecord = await harness.Store.GetPairedDeviceAsync(harness.ViewerFingerprint);
        Assert.IsTrue(updatedRecord!.LastSeenAt > staleSeenAt);

        using var fileClient = new TcpFileTransferClient();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            fileClient.ConnectAsync(new FileTransferClientConnectionOptions("127.0.0.1", harness.FilePort, harness.ClientSsl, handshake.SessionId, handshake.FileTransferToken)));
    }

    [TestMethod]
    public async Task RevokeDevice_WhenAllFourChannelsConnected_DropsAllFourServersImmediately()
    {
        await using var harness = await FourChannelMtlsHarness.CreateAndStartAsync(
            DevicePermissions.Default,
            DateTime.UtcNow.AddHours(-2));

        using var streamClient = new TcpStreamClient();
        await streamClient.ConnectAsync("127.0.0.1", harness.StreamPort, harness.ClientSsl);
        var handshake = streamClient.ActiveSessionHandshake!;

        using var audioClient = new TcpAudioClient();
        await audioClient.ConnectAsync(new AudioClientConnectionOptions("127.0.0.1", harness.AudioPort, harness.ClientSsl, handshake.SessionId, handshake.AudioToken));

        using var controlClient = new TcpControlClient();
        await controlClient.ConnectAsync(new ControlClientConnectionOptions("127.0.0.1", harness.ControlPort, harness.ClientSsl, handshake.SessionId, handshake.ControlToken));

        using var fileClient = new TcpFileTransferClient();
        await fileClient.ConnectAsync(new FileTransferClientConnectionOptions("127.0.0.1", harness.FilePort, harness.ClientSsl, handshake.SessionId, handshake.FileTransferToken));

        await Task.Delay(150);
        Assert.AreEqual((1, 1, 1), (harness.StreamServer.ConnectedClientsCount, harness.AudioServer.ConnectedClientsCount, harness.FileServer.ConnectedClientsCount));

        using var id = new TempIdentityStorage("lss-revoke-4ch");
        var manager = new TrustedDeviceManager(harness.Store, harness.Authority, id, () => harness.Simulator);
        await manager.RevokeDeviceAsync(harness.ViewerFingerprint);

        await Task.Delay(350);
        Assert.AreEqual((0, 0, 0), (harness.StreamServer.ConnectedClientsCount, harness.AudioServer.ConnectedClientsCount, harness.FileServer.ConnectedClientsCount));
        Assert.IsNull(harness.Authority.ActiveControllerSessionId);
        Assert.IsTrue(harness.Simulator.ReleaseCallsCount >= 1);
    }

    [TestMethod]
    public async Task DevicePermissions_LegacyJsonDefaultsSourceSwitchTrue_AndBitmaskRoundTrips()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"lss-perms-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string storePath = Path.Combine(tempDir, "paired_devices.json");
            const string legacyJson = """
            [
              {
                "DeviceId": "legacy-src",
                "DeviceName": "Legacy Viewer",
                "PublicKeyFingerprint": "SHA256:AABBCCDDEEFF00112233445566778899AABBCCDDEEFF00112233445566778899",
                "PairedAt": "2026-01-01T10:00:00Z",
                "LastSeenAt": "2026-01-01T11:00:00Z",
                "IsRevoked": false,
                "Permissions": {
                  "AccessMode": 0,
                  "AllowRemoteControl": false,
                  "AllowClipboardSync": true,
                  "AllowFileTransfer": false
                }
              }
            ]
            """;
            await File.WriteAllTextAsync(storePath, legacyJson);

            var store = new JsonPairedDeviceStore(storePath);
            var loaded = await store.GetPairedDeviceAsync(FingerprintA);
            var restrictedExpected = new EffectiveSessionPermissions
            {
                AllowRemoteControl = false,
                AllowSourceSwitch = true,
                AllowFileTransfer = false,
                AllowClipboardSync = true
            };
            var roundTripped = EffectiveSessionPermissions.FromBitmask(restrictedExpected.ToBitmask());
            var intersected = EffectiveSessionPermissions.AllAllowed.IntersectWith(restrictedExpected);

            Assert.AreEqual((false, true, true, false), (
                loaded!.EffectivePermissions.AllowRemoteControl,
                loaded.EffectivePermissions.AllowSourceSwitch,
                loaded.EffectivePermissions.AllowClipboardSync,
                loaded.EffectivePermissions.AllowFileTransfer));
            Assert.AreEqual((roundTripped, roundTripped), (restrictedExpected, intersected));
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }

    [TestMethod]
    public async Task SessionAuthority_RevokeAndUpdatePermissions_EmitChangedEventsAndResetRevokedState()
    {
        var store = new InMemoryPairedDeviceStore();
        await store.AddPairedDeviceAsync(CreateDevice("Equipo Viewer", FingerprintA));
        var authority = new SessionAuthority(store);

        SessionPermissionsChangedEventArgs? lastChanged = null;
        authority.SessionPermissionsChanged += (_, e) => lastChanged = e;

        var (_, session, _) = await authority.TryCreateSessionAsync(FingerprintA);
        authority.TryAcquireControl(session!.SessionId, out _);
        authority.RevokeControl(session.SessionId);

        var revokedSnapshot = (lastChanged!.Permissions, authority.RequestRemoteControl(session.SessionId));

        authority.UpdateDevicePermissions(FingerprintA, new DevicePermissions
        {
            AllowRemoteControl = true,
            AllowSourceSwitch = false,
            AllowFileTransfer = false,
            AllowClipboardSync = false
        });

        var updatedSnapshot = (lastChanged.Permissions, authority.RequestRemoteControl(session.SessionId));

        Assert.AreEqual((new EffectiveSessionPermissions { AllowRemoteControl = false }, RemoteControlState.Revoked), revokedSnapshot);
        Assert.AreEqual((new EffectiveSessionPermissions { AllowSourceSwitch = false, AllowFileTransfer = false, AllowClipboardSync = false }, RemoteControlState.Granted), updatedSnapshot);
    }

    [TestMethod]
    public async Task MtlsHandshakeAndLivePush_SyncEffectivePermissionsWithoutBlockingSourceSwitch()
    {
        await using var harness = await FourChannelMtlsHarness.CreateAndStartAsync(
            new DevicePermissions
            {
                AllowRemoteControl = false,
                AllowSourceSwitch = true,
                AllowFileTransfer = false,
                AllowClipboardSync = true
            },
            DateTime.UtcNow.AddMinutes(-5));

        bool allowGlobalClipboard = false;
        harness.StreamServer.GlobalPermissionsProvider = () =>
            new EffectiveSessionPermissions { AllowClipboardSync = allowGlobalClipboard };

        int switchCount = 0;
        harness.ControlServer.SwitchMonitorRequested += (_, _) => Interlocked.Increment(ref switchCount);

        using var streamClient = new TcpStreamClient();
        var liveUpdateTcs = new TaskCompletionSource<EffectiveSessionPermissions>(TaskCreationOptions.RunContinuationsAsynchronously);
        streamClient.PermissionsUpdated += (_, perms) => liveUpdateTcs.TrySetResult(perms);

        await streamClient.ConnectAsync("127.0.0.1", harness.StreamPort, harness.ClientSsl);
        var handshake = streamClient.ActiveSessionHandshake!;
        var initialEffective = handshake.GetEffectivePermissions();

        using var controlClient = new TcpControlClient();
        await controlClient.ConnectAsync(new ControlClientConnectionOptions(
            "127.0.0.1", harness.ControlPort, harness.ClientSsl, handshake.SessionId, handshake.ControlToken));

        await controlClient.SendSwitchMonitorAsync(-1);
        await controlClient.SendInputAsync(new RemoteInputMessage { Type = InputEventType.MouseMove, NormalizedX = 0.5f, NormalizedY = 0.5f });
        await Task.Delay(150);

        allowGlobalClipboard = true;
        harness.Authority.UpdateDevicePermissions(harness.ViewerFingerprint, new DevicePermissions
        {
            AllowRemoteControl = true,
            AllowSourceSwitch = false,
            AllowFileTransfer = true,
            AllowClipboardSync = true
        });

        var pushed = await liveUpdateTcs.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.AreEqual(new EffectiveSessionPermissions { AllowRemoteControl = false, AllowFileTransfer = false, AllowClipboardSync = false }, initialEffective);
        Assert.AreEqual((1, 0), (switchCount, harness.Simulator.SimulatedCount));
        Assert.AreEqual(new EffectiveSessionPermissions { AllowSourceSwitch = false }, pushed);
    }

    [TestMethod]
    public async Task MtlsControlAndFileServer_EnforceSeparateSourceSwitchAndDropFilesOnHotRevoke()
    {
        await using var harness = await FourChannelMtlsHarness.CreateAndStartAsync(
            new DevicePermissions
            {
                AllowRemoteControl = true,
                AllowSourceSwitch = false,
                AllowFileTransfer = true,
                AllowClipboardSync = true
            },
            DateTime.UtcNow.AddMinutes(-5));

        int selectCount = 0;
        int clipboardCount = 0;
        harness.ControlServer.SelectSourceRequested += (_, _) => Interlocked.Increment(ref selectCount);
        harness.ControlServer.ClipboardTextReceived += (_, _) => Interlocked.Increment(ref clipboardCount);

        using var streamClient = new TcpStreamClient();
        await streamClient.ConnectAsync("127.0.0.1", harness.StreamPort, harness.ClientSsl);
        var handshake = streamClient.ActiveSessionHandshake!;

        using var controlClient = new TcpControlClient();
        await controlClient.ConnectAsync(new ControlClientConnectionOptions(
            "127.0.0.1", harness.ControlPort, harness.ClientSsl, handshake.SessionId, handshake.ControlToken));

        using var fileClient = new TcpFileTransferClient();
        await fileClient.ConnectAsync(new FileTransferClientConnectionOptions(
            "127.0.0.1", harness.FilePort, harness.ClientSsl, handshake.SessionId, handshake.FileTransferToken));

        await controlClient.SendSelectSourceAsync("screen:1");
        await controlClient.SendInputAsync(new RemoteInputMessage { Type = InputEventType.MouseMove, NormalizedX = 0.25f, NormalizedY = 0.25f });
        await Task.Delay(150);
        int simAfterAllowedMove = harness.Simulator.SimulatedCount;

        harness.Authority.UpdateDevicePermissions(harness.ViewerFingerprint, new DevicePermissions
        {
            AllowRemoteControl = false,
            AllowSourceSwitch = false,
            AllowFileTransfer = false,
            AllowClipboardSync = true
        });
        await controlClient.SendClipboardTextAsync("texto-permitido");
        await Task.Delay(250);

        Assert.AreEqual((0, 1, 1), (selectCount, simAfterAllowedMove, clipboardCount));
        Assert.AreEqual(0, harness.FileServer.ConnectedClientsCount);
    }
}
