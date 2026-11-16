using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Network
{
    public readonly record struct BroadcastTarget(IPAddress LocalIp, IPAddress BroadcastIp);

    public class DiscoveryService
    {
        public const int DefaultDisplaySignalingPort = 5005;

        private const int DefaultDiscoveryPort = 5000;
        private const string BroadcastMessagePrefix = "LAN_SCREEN_SHARE_HOST";
        private const string WirelessDisplayPrefix = "LAN_SCREEN_SHARE_DISPLAY_V1";
        private static readonly IPAddress DefaultSubnetMask = new([255, 255, 255, 0]);

        private readonly int _discoveryPort;
        private CancellationTokenSource? _hostCts;
        private CancellationTokenSource? _wirelessDisplayCts;
        private CancellationTokenSource? _listenCts;
        internal Task? ListenerTask { get; private set; }

        public DiscoveryService(int discoveryPort = DefaultDiscoveryPort)
        {
            _discoveryPort = discoveryPort;
        }

        public event EventHandler<DeviceInfo>? OnDeviceDiscovered;
        public Func<bool>? IsRemoteControlAllowedProvider { get; set; }
        public Func<bool>? IsWirelessDisplayBusyProvider { get; set; }

        public void StartHostingBroadcast()
        {
            StartBroadcastLoop(ref _hostCts, BuildHostBroadcastPayload);
        }

        public void StartWirelessDisplayBroadcast(int signalingPort = DefaultDisplaySignalingPort)
        {
            if (signalingPort is < 1024 or > 65535)
                throw new ArgumentOutOfRangeException(nameof(signalingPort), "Signaling port must be between 1024 and 65535.");

            StartBroadcastLoop(ref _wirelessDisplayCts, (target, pcName) => BuildWirelessDisplayPayload(target, pcName, signalingPort));
        }

        private string BuildHostBroadcastPayload(BroadcastTarget target, string pcName)
        {
            bool rcAllowed = IsRemoteControlAllowedProvider?.Invoke() ?? true;
            return $"{BroadcastMessagePrefix}|{pcName}|{target.LocalIp}|{(rcAllowed ? "1" : "0")}";
        }

        private string BuildWirelessDisplayPayload(BroadcastTarget target, string pcName, int signalingPort)
        {
            bool isBusy = IsWirelessDisplayBusyProvider?.Invoke() ?? false;
            return $"{WirelessDisplayPrefix}|{pcName}|{target.LocalIp}|{signalingPort}|{(isBusy ? "1" : "0")}";
        }

        private void StartBroadcastLoop(
            ref CancellationTokenSource? cts,
            Func<BroadcastTarget, string, string> payloadBuilder)
        {
            if (cts != null && !cts.IsCancellationRequested)
                return;

            cts = new CancellationTokenSource();
            var token = cts.Token;

            Task.Run(async () =>
            {
                var pcName = Environment.MachineName;

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        await BroadcastToAllTargetsAsync(pcName, payloadBuilder);
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error en ciclo de broadcast: {ex.Message}");
                    }

                    await Task.Delay(3000, token);
                }
            }, token);
        }

        private async Task BroadcastToAllTargetsAsync(
            string pcName,
            Func<BroadcastTarget, string, string> payloadBuilder)
        {
            var targets = GetActiveBroadcastTargets();
            foreach (var target in targets)
            {
                await TrySendBroadcastAsync(target, payloadBuilder(target, pcName));
            }
        }

        private async Task TrySendBroadcastAsync(BroadcastTarget target, string message)
        {
            try
            {
                using var udpClient = new UdpClient { EnableBroadcast = true };
                var bytes = Encoding.UTF8.GetBytes(message);

                // 1. Broadcast dirigido a la subred de esta interfaz física
                await udpClient.SendAsync(bytes, bytes.Length, new IPEndPoint(target.BroadcastIp, _discoveryPort));

                // 2. Broadcast global (255.255.255.255) como respaldo
                if (!target.BroadcastIp.Equals(IPAddress.Broadcast))
                {
                    await udpClient.SendAsync(bytes, bytes.Length, new IPEndPoint(IPAddress.Broadcast, _discoveryPort));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error enviando broadcast a {target.BroadcastIp}: {ex.Message}");
            }
        }

        public void StopHosting()
        {
            _hostCts?.Cancel();
            _hostCts?.Dispose();
            _hostCts = null;
        }

        public void StopWirelessDisplayBroadcast()
        {
            _wirelessDisplayCts?.Cancel();
            _wirelessDisplayCts?.Dispose();
            _wirelessDisplayCts = null;
        }

        public void StartListening()
        {
            if (_listenCts != null && !_listenCts.IsCancellationRequested)
                return;

            _listenCts = new CancellationTokenSource();
            var token = _listenCts.Token;

            ListenerTask = Task.Run(async () =>
            {
                using var udpClient = new UdpClient();
                udpClient.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udpClient.Client.Bind(new IPEndPoint(IPAddress.Any, _discoveryPort));

                while (!token.IsCancellationRequested)
                {
                    try
                    {
                        var result = await udpClient.ReceiveAsync(token);
                        ProcessIncomingDatagram(result);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (ObjectDisposedException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error listening: {ex.Message}");
                    }
                }
            }, token);
        }

        internal void ProcessIncomingDatagram(UdpReceiveResult result)
        {
            var message = Encoding.UTF8.GetString(result.Buffer);
            var parts = message.Split('|');
            if (parts.Length < 3)
                return;

            var remoteIp = result.RemoteEndPoint.Address.ToString();
            var device = parts[0] switch
            {
                BroadcastMessagePrefix => TryParseHostDevice(parts, remoteIp),
                WirelessDisplayPrefix => TryParseWirelessDisplayDevice(parts, remoteIp),
                _ => null
            };

            if (device != null)
            {
                OnDeviceDiscovered?.Invoke(this, device);
            }
        }

        private static DeviceInfo? TryParseHostDevice(IReadOnlyList<string> parts, string remoteIp)
        {
            var senderName = parts[1];
            var reportedIp = parts[2];
            if (string.IsNullOrWhiteSpace(senderName) || IsSelfMessage(senderName, reportedIp, remoteIp))
                return null;

            bool isRemoteControlAllowed = parts.Count < 4 || parts[3] == "1";
            return new DeviceInfo
            {
                Name = senderName,
                IPAddress = ResolveFinalIp(reportedIp, remoteIp),
                LastSeen = DateTime.Now,
                IsRemoteControlAllowed = isRemoteControlAllowed,
                Capabilities = DeviceCapabilities.ScreenHost
            };
        }

        private static DeviceInfo? TryParseWirelessDisplayDevice(IReadOnlyList<string> parts, string remoteIp)
        {
            if (parts.Count < 4)
                return null;

            var senderName = parts[1];
            var reportedIp = parts[2];
            if (string.IsNullOrWhiteSpace(senderName) || IsSelfMessage(senderName, reportedIp, remoteIp))
                return null;

            if (!int.TryParse(parts[3], out int signalingPort) || signalingPort < 1024 || signalingPort > 65535)
                return null;

            bool isBusy = parts.Count >= 5 && parts[4] == "1";
            return new DeviceInfo
            {
                Name = senderName,
                IPAddress = ResolveFinalIp(reportedIp, remoteIp),
                LastSeen = DateTime.Now,
                IsRemoteControlAllowed = false,
                Capabilities = DeviceCapabilities.WirelessDisplay,
                DisplaySignalingPort = signalingPort,
                IsWirelessDisplayBusy = isBusy
            };
        }

        internal static string ResolveFinalIp(string reportedIp, string remoteIp)
        {
            bool isReportedValid = !string.IsNullOrWhiteSpace(reportedIp) &&
                                   reportedIp != "127.0.0.1" &&
                                   !reportedIp.StartsWith("169.254");

            return isReportedValid ? reportedIp : remoteIp;
        }

        public void StopListening()
        {
            _listenCts?.Cancel();
            _listenCts?.Dispose();
            _listenCts = null;
        }

        internal static bool IsSelfMessage(string senderName, string reportedIp, string remoteIp)
        {
            if (!string.Equals(senderName, Environment.MachineName, StringComparison.OrdinalIgnoreCase))
                return false;

            if (reportedIp == "127.0.0.1" || remoteIp == "127.0.0.1")
                return true;

            var localIps = GetActiveBroadcastTargets().Select(t => t.LocalIp.ToString()).ToHashSet();
            return localIps.Contains(reportedIp) || localIps.Contains(remoteIp);
        }

        private static List<BroadcastTarget> GetActiveBroadcastTargets()
        {
            var targets = new List<BroadcastTarget>();

            try
            {
                foreach (var ni in NetworkInterface.GetAllNetworkInterfaces().Where(IsEligibleInterface))
                {
                    CollectInterfaceTargets(ni, targets);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error obteniendo interfaces de red: {ex.Message}");
            }

            if (targets.Count == 0)
            {
                targets.Add(new BroadcastTarget(IPAddress.Any, IPAddress.Broadcast));
            }

            return targets;
        }

        private static bool IsEligibleInterface(NetworkInterface ni) =>
            ni.OperationalStatus == OperationalStatus.Up &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Loopback &&
            ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel;

        private static void CollectInterfaceTargets(NetworkInterface ni, List<BroadcastTarget> targets)
        {
            foreach (var unicast in ni.GetIPProperties().UnicastAddresses)
            {
                if (TryExtractTarget(unicast, out var target))
                {
                    targets.Add(target);
                }
            }
        }

        private static bool TryExtractTarget(UnicastIPAddressInformation unicast, out BroadcastTarget target)
        {
            target = default;
            var ip = unicast.Address;

            if (!IsEligibleIpv4(ip))
            {
                return false;
            }

            var mask = NormalizeMask(unicast.IPv4Mask);
            var broadcast = CalculateBroadcastAddress(ip, mask);
            target = new BroadcastTarget(ip, broadcast);
            return true;
        }

        internal static bool IsEligibleIpv4(IPAddress ip)
        {
            if (ip.AddressFamily != AddressFamily.InterNetwork) return false;
            if (IPAddress.IsLoopback(ip)) return false;
            return !ip.ToString().StartsWith("169.254.");
        }

        internal static IPAddress NormalizeMask(IPAddress? mask) =>
            IsValidSubnetMask(mask) ? mask! : DefaultSubnetMask;

        internal static bool IsValidSubnetMask(IPAddress? mask)
        {
            if (mask == null) return false;
            return !mask.Equals(IPAddress.Any) && !mask.Equals(IPAddress.None);
        }

        internal static IPAddress CalculateBroadcastAddress(IPAddress address, IPAddress mask)
        {
            byte[] ipBytes = address.GetAddressBytes();
            byte[] maskBytes = mask.GetAddressBytes();

            if (ipBytes.Length != maskBytes.Length)
                return IPAddress.Broadcast;

            byte[] broadcastBytes = new byte[ipBytes.Length];
            for (int i = 0; i < broadcastBytes.Length; i++)
            {
                broadcastBytes[i] = (byte)(ipBytes[i] | (maskBytes[i] ^ 255));
            }
            return new IPAddress(broadcastBytes);
        }
    }
}
