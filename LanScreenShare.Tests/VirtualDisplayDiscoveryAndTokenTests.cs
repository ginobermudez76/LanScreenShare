using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using LanScreenShare.Core.Models;
using LanScreenShare.Network;
using LanScreenShare.Network.Security;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class VirtualDisplayDiscoveryAndTokenTests
{
    private sealed class MutableTimeProvider(DateTimeOffset initialUtc) : TimeProvider
    {
        private DateTimeOffset _utcNow = initialUtc;
        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);
        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private static UdpReceiveResult BuildDatagram(string payload, string remoteIp = "192.168.1.99") =>
        new(Encoding.UTF8.GetBytes(payload), new IPEndPoint(IPAddress.Parse(remoteIp), 15000));

    private static int GetAvailableUdpPort()
    {
        using var udp = new UdpClient(0);
        return ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
    }

    [TestMethod]
    public void DeviceInfo_CapabilitiesAndSignalingProperties_NotifyChanges()
    {
        var device = new DeviceInfo();
        var changed = new List<string?>();
        device.PropertyChanged += (_, args) => changed.Add(args.PropertyName);

        Assert.AreEqual(DeviceCapabilities.None, device.Capabilities);

        device.Capabilities = DeviceCapabilities.ScreenHost | DeviceCapabilities.WirelessDisplay;
        device.DisplaySignalingPort = 5005;
        device.IsWirelessDisplayBusy = true;

        Assert.IsTrue((device.SupportsScreenHost, device.IsWirelessDisplay, device.IsWirelessDisplayBusy) == (true, true, true));
        Assert.AreEqual(5005, device.DisplaySignalingPort);
        CollectionAssert.Contains(changed, nameof(DeviceInfo.IsWirelessDisplay));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.DisplaySignalingPort));
    }

    [TestMethod]
    public void DiscoveryService_ValidWirelessDisplayDatagrams_PopulateDeviceCapabilities()
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
            service.ProcessIncomingDatagram(BuildDatagram(item.Payload));

            Assert.IsNotNull(found);
            Assert.AreEqual(
                (true, false, false, item.Port, item.Busy),
                (found.IsWirelessDisplay, found.SupportsScreenHost, found.IsRemoteControlAllowed, found.DisplaySignalingPort, found.IsWirelessDisplayBusy));
        }
    }

    [TestMethod]
    public void DiscoveryService_InvalidWirelessDisplayDatagrams_AreIgnored()
    {
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
            service.ProcessIncomingDatagram(BuildDatagram(invalid, "127.0.0.1"));
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
    public void ProjectionInvitationStore_IssueInvitation_ValidatesInputsAndGenerates256BitToken()
    {
        var store = new ProjectionInvitationStore();

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
    }

    [TestMethod]
    public void ProjectionInvitationStore_ConsumeInvitation_EnforcesSingleUseFingerprintAndTtl()
    {
        var clock = new MutableTimeProvider(new DateTimeOffset(2026, 11, 16, 5, 0, 0, TimeSpan.Zero));
        var store = new ProjectionInvitationStore(clock);

        var mismatchedToken = store.IssueInvitation(new ProjectionInvitationRequest { TargetFingerprint = "SHA256:ABCD1234" });
        Assert.IsFalse(store.ConsumeInvitation(mismatchedToken.TokenId, "SHA256:OTHER").Accepted);

        var validToken = store.IssueInvitation(new ProjectionInvitationRequest { TargetFingerprint = "SHA256:ABCD1234" });
        var firstUse = store.ConsumeInvitation(validToken.TokenId.ToUpperInvariant(), "sha256:abcd1234");
        Assert.IsTrue(firstUse.Accepted);
        Assert.IsFalse(store.ConsumeInvitation(validToken.TokenId, "SHA256:ABCD1234").Accepted);

        var expiring = store.IssueInvitation(new ProjectionInvitationRequest { TargetFingerprint = "SHA256:ABCD1234" });
        clock.Advance(TimeSpan.FromSeconds(16));
        Assert.IsFalse(store.ConsumeInvitation(expiring.TokenId, "SHA256:ABCD1234").Accepted);

        store.PruneExpired();
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
            Assert.IsTrue(DisplayControlPacketProtocol.TryParseHeader(packet, out var header, out _));

            await using var ms = new MemoryStream(packet, DisplayControlPacketProtocol.HeaderSize, packet.Length - DisplayControlPacketProtocol.HeaderSize);
            var parsed = await DisplayControlPacketProtocol.ReadMessagePayloadAsync(ms, header.PayloadLength);
            Assert.AreEqual((42UL, msg), (header.SequenceNumber, parsed));
        }

        Assert.IsFalse(DisplayControlPacketProtocol.TryParseHeader(new byte[DisplayControlPacketProtocol.HeaderSize], out _, out _));
        byte[] zeroLength = DisplayControlPacketProtocol.SerializePacket(1, messages[0]);
        zeroLength[24] = zeroLength[25] = zeroLength[26] = zeroLength[27] = 0;
        Assert.IsFalse(DisplayControlPacketProtocol.TryParseHeader(zeroLength, out _, out _));
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
                new DisplayPathSnapshot { DeviceName = @"\\.\DISPLAY8", PositionX = 1920, PositionY = 0, Width = 2560, Height = 1440 }
            }
        };

        var bound = VirtualDisplayCaptureSourceFactory.CreateCaptureSource(session, topology);
        Assert.AreEqual((@"\\.\DISPLAY8", 1920, 0, 2560, 1440, false), (bound.DeviceName, bound.X, bound.Y, bound.Width, bound.Height, bound.IsPrimary));

        var fallback = VirtualDisplayCaptureSourceFactory.CreateCaptureSource(session);
        Assert.AreEqual((0, 0, 2560, 1440), (fallback.X, fallback.Y, fallback.Width, fallback.Height));
    }
}
