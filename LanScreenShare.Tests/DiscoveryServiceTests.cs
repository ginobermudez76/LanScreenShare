using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Network;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class DiscoveryServiceTests
{
    private static UdpReceiveResult BuildDatagram(string payload, string remoteIp = "192.168.1.55", int port = 11000)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(payload);
        return new UdpReceiveResult(bytes, new IPEndPoint(IPAddress.Parse(remoteIp), port));
    }

    private static int GetAvailableUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [DataTestMethod]
    [DataRow("LAN_SCREEN_SHARE_HOST|PC-REMOTA|192.168.1.50|1", "PC-REMOTA", "192.168.1.50", true)]
    [DataRow("LAN_SCREEN_SHARE_HOST|PC-VIEW-ONLY|10.0.0.25|0", "PC-VIEW-ONLY", "10.0.0.25", false)]
    [DataRow("LAN_SCREEN_SHARE_HOST|PC-LEGACY|10.0.0.30", "PC-LEGACY", "10.0.0.30", true)]
    public void ProcessIncomingDatagram_ValidHostFormats_RaisesExpectedDeviceInfo(
        string payload,
        string expectedName,
        string expectedIp,
        bool expectedRemoteControl)
    {
        var service = new DiscoveryService();
        DeviceInfo? discovered = null;
        service.OnDeviceDiscovered += (_, device) => discovered = device;

        service.ProcessIncomingDatagram(BuildDatagram(payload));

        Assert.IsNotNull(discovered);
        Assert.AreEqual(expectedName, discovered.Name);
        Assert.IsTrue(discovered.IPAddress == expectedIp && discovered.IsRemoteControlAllowed == expectedRemoteControl);
    }

    [DataTestMethod]
    [DataRow("WRONG_PREFIX|PC|10.0.0.5|1", "192.168.1.55")]
    [DataRow("LAN_SCREEN_SHARE_HOST|ONLY_TWO", "192.168.1.55")]
    [DataRow("", "192.168.1.55")]
    public void ProcessIncomingDatagram_InvalidPayloads_DoesNotRaiseEvent(string payload, string remoteIp)
    {
        var service = new DiscoveryService();
        int count = 0;
        service.OnDeviceDiscovered += (_, _) => count++;

        service.ProcessIncomingDatagram(BuildDatagram(payload, remoteIp));

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

    [DataTestMethod]
    [DataRow("127.0.0.1", "192.168.1.99", "192.168.1.99")]
    [DataRow("169.254.12.34", "192.168.1.99", "192.168.1.99")]
    [DataRow("   ", "192.168.1.99", "192.168.1.99")]
    [DataRow("10.0.5.20", "192.168.1.99", "10.0.5.20")]
    public void ResolveFinalIp_SelectsValidOrFallbackIp(string reportedIp, string remoteIp, string expectedIp)
    {
        Assert.AreEqual(expectedIp, DiscoveryService.ResolveFinalIp(reportedIp, remoteIp));
    }

    [DataTestMethod]
    [DataRow(false, "OTHER-HOST-XYZ", "127.0.0.1", "127.0.0.1")]
    [DataRow(true, null, "127.0.0.1", "10.0.0.2")]
    [DataRow(true, null, "10.0.0.2", "127.0.0.1")]
    [DataRow(false, null, "198.51.100.250", "198.51.100.251")]
    public void IsSelfMessage_DistinguishesLocalVsRemoteHosts(bool expected, string? senderOverride, string reportedIp, string remoteIp)
    {
        string sender = senderOverride ?? Environment.MachineName;
        Assert.AreEqual(expected, DiscoveryService.IsSelfMessage(sender, reportedIp, remoteIp));
    }

    [DataTestMethod]
    [DataRow("::1", false)]
    [DataRow("127.0.0.1", false)]
    [DataRow("169.254.1.10", false)]
    [DataRow("192.168.10.5", true)]
    public void IsEligibleIpv4_FiltersLoopbackApipaAndIpv6(string ipText, bool expected)
    {
        Assert.AreEqual(expected, DiscoveryService.IsEligibleIpv4(IPAddress.Parse(ipText)));
    }

    [TestMethod]
    public void SubnetMaskHelpers_NormalizeAndCalculateBroadcast()
    {
        Assert.IsFalse(DiscoveryService.IsValidSubnetMask(null));
        Assert.AreEqual(IPAddress.Parse("255.255.255.0"), DiscoveryService.NormalizeMask(IPAddress.Any));
        Assert.AreEqual(
            IPAddress.Parse("192.168.50.255"),
            DiscoveryService.CalculateBroadcastAddress(IPAddress.Parse("192.168.50.42"), IPAddress.Parse("255.255.255.0")));
    }

    [TestMethod]
    public void CalculateBroadcastAddress_MismatchedFamilies_ReturnsGlobalBroadcast()
    {
        Assert.IsFalse(DiscoveryService.IsValidSubnetMask(IPAddress.None));
        Assert.IsTrue(DiscoveryService.IsValidSubnetMask(IPAddress.Parse("255.255.0.0")));
        Assert.AreEqual(
            IPAddress.Broadcast,
            DiscoveryService.CalculateBroadcastAddress(IPAddress.IPv6Any, IPAddress.Parse("255.255.255.0")));
    }

    [TestMethod]
    public async Task StartAndStopHostingAndListening_ReceivesProbeDatagramAndCleansUp()
    {
        int probePort = GetAvailableUdpPort();
        var service = new DiscoveryService(probePort) { IsRemoteControlAllowedProvider = () => false };
        var discoveredTcs = new TaskCompletionSource<DeviceInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
        service.OnDeviceDiscovered += (_, device) => discoveredTcs.TrySetResult(device);

        try
        {
            service.StartHostingBroadcast();
            service.StartHostingBroadcast();
            service.StartListening();
            service.StartListening();

            await SendProbeUntilDiscoveredAsync(probePort, discoveredTcs.Task, service.ListenerTask);
            var discovered = await discoveredTcs.Task;

            Assert.AreEqual("REMOTE-PROBE", discovered.Name);
            Assert.AreEqual("192.0.2.10", discovered.IPAddress);
        }
        finally
        {
            service.StopHosting();
            service.StopHosting();
            service.StopListening();
            service.StopListening();
        }
    }

    private static async Task SendProbeUntilDiscoveredAsync(int port, Task<DeviceInfo> discoveredTask, Task? listenerTask)
    {
        using var sender = new UdpClient();
        byte[] payload = Encoding.UTF8.GetBytes("LAN_SCREEN_SHARE_HOST|REMOTE-PROBE|192.0.2.10|1");
        var endpoint = new IPEndPoint(IPAddress.Loopback, port);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        while (!discoveredTask.IsCompleted)
        {
            if (listenerTask?.IsFaulted == true)
            {
                await listenerTask;
            }

            await sender.SendAsync(payload, payload.Length, endpoint);
            await Task.WhenAny(discoveredTask, Task.Delay(50, timeoutCts.Token));
            timeoutCts.Token.ThrowIfCancellationRequested();
        }
    }
}
