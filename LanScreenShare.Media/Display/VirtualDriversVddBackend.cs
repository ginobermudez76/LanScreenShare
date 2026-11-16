using System;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using Microsoft.Win32;

namespace LanScreenShare.Media.Display
{
    public interface IVddPipeTransport
    {
        Task<bool> SendCommandAsync(VddCommandRequest command, CancellationToken ct = default);
    }

    public interface IVddEnvironment
    {
        bool IsDriverPresent();
        string? ReadSettingsXml();
        void WriteSettingsXml(string xmlContent);
        Task<bool> EnsureDriverEnabledAsync(CancellationToken ct = default) => Task.FromResult(true);
        Task DisableDriverAsync(CancellationToken ct = default) => Task.CompletedTask;
        string? FindVirtualDisplayDeviceName() => null;
    }

    [SupportedOSPlatform("windows")]
    internal sealed class NamedPipeVddTransport : IVddPipeTransport
    {
        private readonly string _pipeName;
        private readonly int _connectTimeoutMs;

        public NamedPipeVddTransport(string pipeName = "MTTVirtualDisplayPipe", int connectTimeoutMs = 2000)
        {
            _pipeName = pipeName;
            _connectTimeoutMs = Math.Max(200, connectTimeoutMs);
        }

        public async Task<bool> SendCommandAsync(VddCommandRequest command, CancellationToken ct = default)
        {
            string wireCommand = command.FormatWireCommand();
            if (string.IsNullOrWhiteSpace(wireCommand))
                return false;

            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                await pipe.ConnectAsync(_connectTimeoutMs, ct);
                byte[] payload = new UnicodeEncoding(bigEndian: false, byteOrderMark: false).GetBytes(wireCommand);
                await pipe.WriteAsync(payload, ct);
                await pipe.FlushAsync(ct);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed record VddEnvironmentPaths
    {
        public string? SettingsPath { get; init; }
        public string? InfPath { get; init; }
    }

    [SupportedOSPlatform("windows")]
    internal sealed partial class WindowsVddEnvironment : IVddEnvironment
    {
        private const string EnableTaskName = "LanScreenShare-EnableVdd";
        private const string DisableTaskName = "LanScreenShare-DisableVdd";
        private const string VddPipeName = "MTTVirtualDisplayPipe";
        private const int DisplayDeviceAttachedToDesktop = 0x00000001;

        private readonly string _settingsPath;
        private readonly string _infPath;

        public WindowsVddEnvironment(VddEnvironmentPaths? paths = null)
        {
            string baseDir = GetDefaultInstallDirectory();
            _settingsPath = paths?.SettingsPath ?? Path.Combine(baseDir, "vdd_settings.xml");
            _infPath = paths?.InfPath ?? Path.Combine(baseDir, "MttVDD.inf");
        }

        public static string GetDefaultInstallDirectory()
        {
            string? root = Path.GetPathRoot(Environment.SystemDirectory);
            string baseRoot = string.IsNullOrEmpty(root)
                ? Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles)
                : root;
            return Path.Combine(baseRoot, "VirtualDisplayDriver");
        }

        public bool IsDriverPresent() =>
            VirtualDriversVddBackend.ResolveDriverPresence(
                IsNamedPipeAvailable(VddPipeName),
                File.Exists(_infPath) || File.Exists(_settingsPath),
                HasRegisteredMttVddDevice);

        public async Task<bool> EnsureDriverEnabledAsync(CancellationToken ct = default)
        {
            if (IsNamedPipeAvailable(VddPipeName))
                return true;

            if (!RunScheduledTask(EnableTaskName))
                return IsNamedPipeAvailable(VddPipeName);

            return await WaitForPipeStateAsync(expectedAvailable: true, TimeSpan.FromSeconds(5), ct);
        }

        public async Task DisableDriverAsync(CancellationToken ct = default)
        {
            if (!IsNamedPipeAvailable(VddPipeName))
                return;

            if (RunScheduledTask(DisableTaskName))
            {
                await WaitForPipeStateAsync(expectedAvailable: false, TimeSpan.FromSeconds(3), ct);
            }
        }

        private readonly record struct DisplayAdapterEntry(
            string DeviceName,
            string DeviceString,
            string DeviceId,
            int StateFlags);

        public string? FindVirtualDisplayDeviceName()
        {
            string? fallback = null;
            for (uint index = 0; index < 64; index++)
            {
                if (!TryReadDisplayAdapter(index, out var entry))
                    break;

                if (!IsMatchingVddAdapter(entry.DeviceId, entry.DeviceString))
                    continue;

                if ((entry.StateFlags & DisplayDeviceAttachedToDesktop) != 0)
                    return entry.DeviceName;

                fallback ??= entry.DeviceName;
            }

            return fallback;
        }

        private static bool IsMatchingVddAdapter(string deviceId, string deviceString)
        {
            if (VirtualDriversVddBackend.HasMttVddHardwareId([deviceId]))
                return true;

            return deviceString.Contains("Virtual Display Driver", StringComparison.OrdinalIgnoreCase);
        }

        private static bool RunScheduledTask(string taskName)
        {
            try
            {
                using var proc = Process.Start(new ProcessStartInfo
                {
                    FileName = "schtasks.exe",
                    Arguments = $"/Run /TN \"{taskName}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                });
                if (proc == null)
                    return false;

                proc.WaitForExit(3000);
                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private static async Task<bool> WaitForPipeStateAsync(bool expectedAvailable, TimeSpan timeout, CancellationToken ct)
        {
            DateTime deadline = DateTime.UtcNow.Add(timeout);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                if (IsNamedPipeAvailable(VddPipeName) == expectedAvailable)
                    return true;

                await Task.Delay(150, ct);
            }

            return IsNamedPipeAvailable(VddPipeName) == expectedAvailable;
        }

        private static bool IsNamedPipeAvailable(string pipeName)
        {
            try
            {
                return Directory.GetFiles(@"\\.\pipe\", pipeName).Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static bool HasRegisteredMttVddDevice() =>
            EnumKeyContainsMttVdd(@"SYSTEM\CurrentControlSet\Enum\ROOT\MTTVDD") ||
            EnumKeyContainsMttVdd(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");

        private static bool EnumKeyContainsMttVdd(string registryPath)
        {
            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey(registryPath);
                if (rootKey == null)
                    return false;

                foreach (string subKeyName in rootKey.GetSubKeyNames())
                {
                    using var devKey = rootKey.OpenSubKey(subKeyName);
                    var hwIds = devKey?.GetValue("HardwareID") as string[];
                    if (VirtualDriversVddBackend.HasMttVddHardwareId(hwIds))
                        return true;
                }

                return false;
            }
            catch
            {
                return false;
            }
        }

        private static unsafe bool TryReadDisplayAdapter(uint index, out DisplayAdapterEntry entry)
        {
            var dd = new NativeDisplayDevice { cb = sizeof(NativeDisplayDevice) };
            if (!EnumDisplayDevicesW(null, index, &dd, 0))
            {
                entry = default;
                return false;
            }

            entry = new DisplayAdapterEntry(
                ReadFixedString(dd.DeviceName, 32),
                ReadFixedString(dd.DeviceString, 128),
                ReadFixedString(dd.DeviceID, 128),
                dd.StateFlags);
            return true;
        }

        private static unsafe string ReadFixedString(char* buffer, int maxChars)
        {
            var span = new ReadOnlySpan<char>(buffer, maxChars);
            int nullIdx = span.IndexOf('\0');
            return (nullIdx >= 0 ? span[..nullIdx] : span).ToString();
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private unsafe struct NativeDisplayDevice
        {
            public int cb;
            public fixed char DeviceName[32];
            public fixed char DeviceString[128];
            public int StateFlags;
            public fixed char DeviceID[128];
            public fixed char DeviceKey[128];
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static unsafe partial bool EnumDisplayDevicesW(
            string? lpDevice,
            uint iDevNum,
            NativeDisplayDevice* lpDisplayDevice,
            uint dwFlags);

        public string? ReadSettingsXml() =>
            File.Exists(_settingsPath) ? File.ReadAllText(_settingsPath, Encoding.UTF8) : null;

        public void WriteSettingsXml(string xmlContent)
        {
            string? dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(_settingsPath, xmlContent, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    public sealed class VirtualDriversVddBackend : IVirtualDisplayBackend
    {
        public const string Id = "virtual-drivers-vdd";

        private readonly IVddPipeTransport _pipeTransport;
        private readonly IVddEnvironment _environment;
        private int _previousCount;

        public VirtualDisplayBackendCapabilities Capabilities { get; } = new()
        {
            BackendId = Id,
            MinimumIddCxVersion = new Version(1, 4),
            MinimumWindowsBuild = new Version(10, 0, 19041),
            HasNativeKernelWatchdog = false,
            NativeWatchdogTimeout = TimeSpan.Zero,
            RequiresAdminForHotplug = false,
            SupportsDynamicModeChange = true
        };

        public VirtualDriversVddBackend()
            : this(CreateDefaultTransport(), CreateDefaultEnvironment())
        {
        }

        internal VirtualDriversVddBackend(IVddPipeTransport pipeTransport, IVddEnvironment environment)
        {
            _pipeTransport = pipeTransport ?? throw new ArgumentNullException(nameof(pipeTransport));
            _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        }

        private static IVddPipeTransport CreateDefaultTransport() =>
            OperatingSystem.IsWindows() ? new NamedPipeVddTransport() : new NullVddPipeTransport();

        private static IVddEnvironment CreateDefaultEnvironment() =>
            OperatingSystem.IsWindows() ? new WindowsVddEnvironment() : new NullVddEnvironment();

        public bool IsDriverInstalled() => _environment.IsDriverPresent();

        internal static bool ResolveDriverPresence(bool pipeAvailable, bool filesExist, Func<bool> hasRegisteredDevice)
        {
            if (pipeAvailable)
                return true;
            if (!filesExist)
                return false;
            return hasRegisteredDevice();
        }

        internal static bool HasMttVddHardwareId(string[]? hardwareIds) =>
            hardwareIds != null && hardwareIds.Any(IsMttVddId);

        private static bool IsMttVddId(string id) =>
            string.Equals(id, @"Root\MttVDD", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(id, "MttVDD", StringComparison.OrdinalIgnoreCase);

        public async Task<VirtualDisplayInstance?> CreateVirtualMonitorAsync(
            VirtualDisplayMode mode,
            CancellationToken ct = default)
        {
            if (!IsDriverInstalled() || !VirtualDisplayModeValidator.IsSupported(in mode))
                return null;

            string? existingXml = _environment.ReadSettingsXml();
            _previousCount = VddSettingsXmlHelper.ReadMonitorCount(existingXml ?? string.Empty);
            int targetCount = Math.Max(1, _previousCount + 1);

            string updatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode(existingXml, targetCount, mode);
            _environment.WriteSettingsXml(updatedXml);

            if (!await _environment.EnsureDriverEnabledAsync(ct))
            {
                RestorePreviousCountInSettings(mode);
                return null;
            }

            bool sent = await _pipeTransport.SendCommandAsync(new VddCommandRequest(VddCommandKind.SetDisplayCount, targetCount), ct);
            if (!sent)
            {
                RestorePreviousCountInSettings(mode);
                await _environment.DisableDriverAsync(CancellationToken.None);
                return null;
            }

            string? deviceName = await WaitForVirtualDeviceNameAsync(ct);

            return new VirtualDisplayInstance
            {
                BackendId = Id,
                MonitorId = targetCount.ToString(),
                MonitorIndex = targetCount - 1,
                DeviceName = deviceName,
                Mode = mode
            };
        }

        private async Task<string?> WaitForVirtualDeviceNameAsync(CancellationToken ct)
        {
            string? found = _environment.FindVirtualDisplayDeviceName();
            if (!string.IsNullOrWhiteSpace(found) || !(_environment is WindowsVddEnvironment))
                return found;

            DateTime deadline = DateTime.UtcNow.AddSeconds(2.5);
            while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct);
                found = _environment.FindVirtualDisplayDeviceName();
                if (!string.IsNullOrWhiteSpace(found))
                    return found;
            }

            return found;
        }

        public async Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in mode))
                return false;

            string? existingXml = _environment.ReadSettingsXml();
            int currentCount = Math.Max(1, VddSettingsXmlHelper.ReadMonitorCount(existingXml ?? string.Empty));
            string updatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode(existingXml, currentCount, mode);
            _environment.WriteSettingsXml(updatedXml);

            return await _pipeTransport.SendCommandAsync(new VddCommandRequest(VddCommandKind.ReloadDriver), ct);
        }

        public async Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default)
        {
            string? existingXml = _environment.ReadSettingsXml();
            int restoreCount = Math.Max(0, _previousCount);
            string updatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode(
                existingXml,
                restoreCount,
                VirtualDisplayMode.Default);
            _environment.WriteSettingsXml(updatedXml);

            bool sent = await _pipeTransport.SendCommandAsync(new VddCommandRequest(VddCommandKind.SetDisplayCount, restoreCount), ct);
            if (restoreCount == 0)
            {
                await _environment.DisableDriverAsync(ct);
            }
            return sent;
        }

        public Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default) =>
            Task.FromResult(true);

        private void RestorePreviousCountInSettings(VirtualDisplayMode mode)
        {
            try
            {
                string? currentXml = _environment.ReadSettingsXml();
                string rollback = VddSettingsXmlHelper.UpdateMonitorCountAndMode(currentXml, _previousCount, mode);
                _environment.WriteSettingsXml(rollback);
            }
            catch
            {
                // Best-effort rollback of settings XML
            }
        }

        internal sealed class NullVddPipeTransport : IVddPipeTransport
        {
            public Task<bool> SendCommandAsync(VddCommandRequest command, CancellationToken ct = default) =>
                Task.FromResult(false);
        }

        internal sealed class NullVddEnvironment : IVddEnvironment
        {
            public bool IsDriverPresent() => false;
            public string? ReadSettingsXml() => null;
            public void WriteSettingsXml(string xmlContent) { }
        }
    }
}
