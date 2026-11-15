using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LanScreenShare.Core.Models;
using LanScreenShare.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
[DoNotParallelize]
public class DiscoveryServiceTests
{
    private static UdpReceiveResult BuildDatagram(string payload, string remoteIp = "192.168.1.55", int port = 11000)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        return new UdpReceiveResult(bytes, new IPEndPoint(IPAddress.Parse(remoteIp), port));
    }

    [TestMethod]
    public void ProcessIncomingDatagram_ValidRemoteHost_RaisesOnDeviceDiscovered()
    {
        var service = new DiscoveryService();
        DeviceInfo? discovered = null;
        service.OnDeviceDiscovered += (_, device) => discovered = device;

        service.ProcessIncomingDatagram(BuildDatagram("LAN_SCREEN_SHARE_HOST|PC-REMOTA|192.168.1.50|1"));

        Assert.IsNotNull(discovered);
        Assert.AreEqual("PC-REMOTA", discovered.Name);
        Assert.AreEqual("192.168.1.50", discovered.IPAddress);
        Assert.IsTrue(discovered.IsRemoteControlAllowed);
    }

    [TestMethod]
    public void ProcessIncomingDatagram_RemoteControlDisabledFlag_SetsFlagFalse()
    {
        var service = new DiscoveryService();
        DeviceInfo? discovered = null;
        service.OnDeviceDiscovered += (_, device) => discovered = device;

        service.ProcessIncomingDatagram(BuildDatagram("LAN_SCREEN_SHARE_HOST|PC-VIEW-ONLY|10.0.0.25|0"));

        Assert.IsNotNull(discovered);
        Assert.IsFalse(discovered.IsRemoteControlAllowed);
    }

    [TestMethod]
    public void ProcessIncomingDatagram_LegacyThreePartMessage_DefaultsRemoteControlAllowedTrue()
    {
        var service = new DiscoveryService();
        DeviceInfo? discovered = null;
        service.OnDeviceDiscovered += (_, device) => discovered = device;

        service.ProcessIncomingDatagram(BuildDatagram("LAN_SCREEN_SHARE_HOST|PC-LEGACY|10.0.0.30"));

        Assert.IsNotNull(discovered);
        Assert.AreEqual("10.0.0.30", discovered.IPAddress);
        Assert.IsTrue(discovered.IsRemoteControlAllowed);
    }

    [TestMethod]
    public void ProcessIncomingDatagram_InvalidOrShortPayloads_DoesNotRaiseEvent()
    {
        var service = new DiscoveryService();
        int count = 0;
        service.OnDeviceDiscovered += (_, _) => count++;

        service.ProcessIncomingDatagram(BuildDatagram("WRONG_PREFIX|PC|10.0.0.5|1"));
        service.ProcessIncomingDatagram(BuildDatagram("LAN_SCREEN_SHARE_HOST|ONLY_TWO"));
        service.ProcessIncomingDatagram(BuildDatagram(""));

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void ProcessIncomingDatagram_SelfLoopbackMessage_IsIgnored()
    {
        var service = new DiscoveryService();
        int count = 0;
        service.OnDeviceDiscovered += (_, _) => count++;

        service.ProcessIncomingDatagram(BuildDatagram($"LAN_SCREEN_SHARE_HOST|{Environment.MachineName}|127.0.0.1|1", "127.0.0.1"));

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void ResolveFinalIp_InvalidOrApipaReportedIp_FallsBackToRemoteIp()
    {
        Assert.AreEqual("192.168.1.99", DiscoveryService.ResolveFinalIp("127.0.0.1", "192.168.1.99"));
        Assert.AreEqual("192.168.1.99", DiscoveryService.ResolveFinalIp("169.254.12.34", "192.168.1.99"));
        Assert.AreEqual("192.168.1.99", DiscoveryService.ResolveFinalIp("   ", "192.168.1.99"));
        Assert.AreEqual("10.0.5.20", DiscoveryService.ResolveFinalIp("10.0.5.20", "192.168.1.99"));
    }

    [TestMethod]
    public void IsSelfMessage_DistinguishesLocalVsRemoteHosts()
    {
        Assert.IsFalse(DiscoveryService.IsSelfMessage("OTHER-HOST-XYZ", "127.0.0.1", "127.0.0.1"));
        Assert.IsTrue(DiscoveryService.IsSelfMessage(Environment.MachineName, "127.0.0.1", "10.0.0.2"));
        Assert.IsTrue(DiscoveryService.IsSelfMessage(Environment.MachineName, "10.0.0.2", "127.0.0.1"));
        Assert.IsFalse(DiscoveryService.IsSelfMessage(Environment.MachineName, "198.51.100.250", "198.51.100.251"));
    }

    [TestMethod]
    public void NetworkAddressHelpers_FilterAndCalculateBroadcastCorrectly()
    {
        Assert.IsFalse(DiscoveryService.IsEligibleIpv4(IPAddress.IPv6Loopback));
        Assert.IsFalse(DiscoveryService.IsEligibleIpv4(IPAddress.Loopback));
        Assert.IsFalse(DiscoveryService.IsEligibleIpv4(IPAddress.Parse("169.254.1.10")));
        Assert.IsTrue(DiscoveryService.IsEligibleIpv4(IPAddress.Parse("192.168.10.5")));

        Assert.IsFalse(DiscoveryService.IsValidSubnetMask(null));
        Assert.IsFalse(DiscoveryService.IsValidSubnetMask(IPAddress.Any));
        Assert.IsFalse(DiscoveryService.IsValidSubnetMask(IPAddress.None));
        Assert.IsTrue(DiscoveryService.IsValidSubnetMask(IPAddress.Parse("255.255.0.0")));

        Assert.AreEqual(IPAddress.Parse("255.255.255.0"), DiscoveryService.NormalizeMask(null));
        Assert.AreEqual(IPAddress.Parse("255.255.255.0"), DiscoveryService.NormalizeMask(IPAddress.Any));
        Assert.AreEqual(IPAddress.Parse("255.255.0.0"), DiscoveryService.NormalizeMask(IPAddress.Parse("255.255.0.0")));

        var directed = DiscoveryService.CalculateBroadcastAddress(
            IPAddress.Parse("192.168.50.42"),
            IPAddress.Parse("255.255.255.0"));
        Assert.AreEqual(IPAddress.Parse("192.168.50.255"), directed);

        var mismatched = DiscoveryService.CalculateBroadcastAddress(
            IPAddress.IPv6Any,
            IPAddress.Parse("255.255.255.0"));
        Assert.AreEqual(IPAddress.Broadcast, mismatched);
    }

    [TestMethod]
    public void StartAndStopHostingAndListening_AreIdempotentAndSafe()
    {
        var service = new DiscoveryService
        {
            IsRemoteControlAllowedProvider = () => false
        };

        service.StartHostingBroadcast();
        service.StartHostingBroadcast();
        service.StartListening();
        service.StartListening();

        Thread.Sleep(50);

        service.StopHosting();
        service.StopHosting();
        service.StopListening();
        service.StopListening();
    }
}
