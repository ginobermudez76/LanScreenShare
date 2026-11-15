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
    private sealed record DatagramInput(string Payload, string RemoteIp = "192.168.1.55", int Port = 11000);
    private sealed record ProbeContext(int Port, Task<DeviceInfo> DiscoveredTask, Task? ListenerTask);

    private static UdpReceiveResult BuildDatagram(DatagramInput input)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(input.Payload);
        return new UdpReceiveResult(bytes, new IPEndPoint(IPAddress.Parse(input.RemoteIp), input.Port));
    }

    private static int GetAvailableUdpPort()
    {
        using var probe = new UdpClient(0);
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    [TestMethod]
    public void ProcessIncomingDatagram_ValidHostFormats_RaisesExpectedDeviceInfo()
    {
        var cases = new[]
        {
            new { Payload = "LAN_SCREEN_SHARE_HOST|PC-REMOTA|192.168.1.50|1", Name = "PC-REMOTA", Ip = "192.168.1.50", RemoteControl = true },
            new { Payload = "LAN_SCREEN_SHARE_HOST|PC-VIEW-ONLY|10.0.0.25|0", Name = "PC-VIEW-ONLY", Ip = "10.0.0.25", RemoteControl = false },
            new { Payload = "LAN_SCREEN_SHARE_HOST|PC-LEGACY|10.0.0.30", Name = "PC-LEGACY", Ip = "10.0.0.30", RemoteControl = true }
        };

        foreach (var item in cases)
        {
            var service = new DiscoveryService();
            DeviceInfo? discovered = null;
            service.OnDeviceDiscovered += (_, device) => discovered = device;

            service.ProcessIncomingDatagram(BuildDatagram(new DatagramInput(item.Payload)));

            Assert.IsNotNull(discovered);
            Assert.AreEqual(
                (item.Name, item.Ip, item.RemoteControl),
                (discovered.Name, discovered.IPAddress, discovered.IsRemoteControlAllowed));
        }
    }

    [TestMethod]
    public void ProcessIncomingDatagram_InvalidPayloads_DoesNotRaiseEvent()
    {
        var cases = new[]
        {
            "WRONG_PREFIX|PC|10.0.0.5|1",
            "LAN_SCREEN_SHARE_HOST|ONLY_TWO",
            string.Empty
        };

        foreach (string payload in cases)
        {
            var service = new DiscoveryService();
            int count = 0;
            service.OnDeviceDiscovered += (_, _) => count++;

            service.ProcessIncomingDatagram(BuildDatagram(new DatagramInput(payload)));

            Assert.AreEqual(0, count);
        }
    }

    [TestMethod]
    public void ProcessIncomingDatagram_SelfLoopbackMessage_IsIgnored()
    {
        var service = new DiscoveryService();
        int count = 0;
        service.OnDeviceDiscovered += (_, _) => count++;

        service.ProcessIncomingDatagram(
            BuildDatagram(new DatagramInput($"LAN_SCREEN_SHARE_HOST|{Environment.MachineName}|127.0.0.1|1", "127.0.0.1")));

        Assert.AreEqual(0, count);
    }

    [TestMethod]
    public void ResolveFinalIp_SelectsValidOrFallbackIp()
    {
        var cases = new[]
        {
            new { ReportedIp = "127.0.0.1", RemoteIp = "192.168.1.99", ExpectedIp = "192.168.1.99" },
            new { ReportedIp = "169.254.12.34", RemoteIp = "192.168.1.99", ExpectedIp = "192.168.1.99" },
            new { ReportedIp = "   ", RemoteIp = "192.168.1.99", ExpectedIp = "192.168.1.99" },
            new { ReportedIp = "10.0.5.20", RemoteIp = "192.168.1.99", ExpectedIp = "10.0.5.20" }
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(item.ExpectedIp, DiscoveryService.ResolveFinalIp(item.ReportedIp, item.RemoteIp));
        }
    }

    [TestMethod]
    public void IsSelfMessage_DistinguishesLocalVsRemoteHosts()
    {
        var cases = new[]
        {
            new { Expected = false, Sender = "OTHER-HOST-XYZ", ReportedIp = "127.0.0.1", RemoteIp = "127.0.0.1" },
            new { Expected = true, Sender = Environment.MachineName, ReportedIp = "127.0.0.1", RemoteIp = "10.0.0.2" },
            new { Expected = true, Sender = Environment.MachineName, ReportedIp = "10.0.0.2", RemoteIp = "127.0.0.1" },
            new { Expected = false, Sender = Environment.MachineName, ReportedIp = "198.51.100.250", RemoteIp = "198.51.100.251" }
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(item.Expected, DiscoveryService.IsSelfMessage(item.Sender, item.ReportedIp, item.RemoteIp));
        }
    }

    [TestMethod]
    public void IsEligibleIpv4_FiltersLoopbackApipaAndIpv6()
    {
        var cases = new[]
        {
            new { IpText = "::1", Expected = false },
            new { IpText = "127.0.0.1", Expected = false },
            new { IpText = "169.254.1.10", Expected = false },
            new { IpText = "192.168.10.5", Expected = true }
        };

        foreach (var item in cases)
        {
            Assert.AreEqual(item.Expected, DiscoveryService.IsEligibleIpv4(IPAddress.Parse(item.IpText)));
        }
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

            await SendProbeUntilDiscoveredAsync(new ProbeContext(probePort, discoveredTcs.Task, service.ListenerTask));
            var discovered = await discoveredTcs.Task;

            Assert.AreEqual(("REMOTE-PROBE", "192.0.2.10"), (discovered.Name, discovered.IPAddress));
        }
        finally
        {
            service.StopHosting();
            service.StopHosting();
            service.StopListening();
            service.StopListening();
        }
    }

    private static async Task SendProbeUntilDiscoveredAsync(ProbeContext context)
    {
        using var sender = new UdpClient();
        byte[] payload = Encoding.UTF8.GetBytes("LAN_SCREEN_SHARE_HOST|REMOTE-PROBE|192.0.2.10|1");
        var endpoint = new IPEndPoint(IPAddress.Loopback, context.Port);
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(4));

        while (!context.DiscoveredTask.IsCompleted)
        {
            if (context.ListenerTask?.IsFaulted == true)
            {
                await context.ListenerTask;
            }

            await sender.SendAsync(payload, payload.Length, endpoint);
            await Task.WhenAny(context.DiscoveredTask, Task.Delay(50, timeoutCts.Token));
            timeoutCts.Token.ThrowIfCancellationRequested();
        }
    }
}
