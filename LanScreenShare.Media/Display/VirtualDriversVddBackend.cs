using System;
using System.IO;
using System.IO.Pipes;
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
        Task<bool> SendCommandAsync(string command, CancellationToken ct = default);
    }

    public interface IVddEnvironment
    {
        bool IsDriverPresent();
        string? ReadSettingsXml();
        void WriteSettingsXml(string xmlContent);
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

        public async Task<bool> SendCommandAsync(string command, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(command))
                return false;

            try
            {
                using var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
                await pipe.ConnectAsync(_connectTimeoutMs, ct);
                await using var writer = new StreamWriter(pipe, new UnicodeEncoding(bigEndian: false, byteOrderMark: false), leaveOpen: true)
                {
                    AutoFlush = true
                };
                await writer.WriteLineAsync(command.AsMemory(), ct);
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    internal sealed class WindowsVddEnvironment : IVddEnvironment
    {
        public const string DefaultInstallDir = @"C:\VirtualDisplayDriver";
        public const string DefaultSettingsFile = @"C:\VirtualDisplayDriver\vdd_settings.xml";
        public const string DefaultInfFile = @"C:\VirtualDisplayDriver\MttVDD.inf";

        private readonly string _settingsPath;
        private readonly string _infPath;

        public WindowsVddEnvironment(string? settingsPath = null, string? infPath = null)
        {
            _settingsPath = settingsPath ?? DefaultSettingsFile;
            _infPath = infPath ?? DefaultInfFile;
        }

        public bool IsDriverPresent()
        {
            if (File.Exists(_infPath) || File.Exists(_settingsPath))
                return true;

            try
            {
                using var rootKey = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\ROOT\DISPLAY");
                return rootKey != null && rootKey.SubKeyCount > 0;
            }
            catch
            {
                return false;
            }
        }

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

            bool sent = await _pipeTransport.SendCommandAsync(
                VddSettingsXmlHelper.BuildSetDisplayCountCommand(targetCount),
                ct);
            if (!sent)
            {
                RestorePreviousSettingsXml(existingXml, mode);
                return null;
            }

            return new VirtualDisplayInstance
            {
                BackendId = Id,
                MonitorId = targetCount.ToString(),
                MonitorIndex = targetCount - 1,
                Mode = mode
            };
        }

        public async Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default)
        {
            if (!VirtualDisplayModeValidator.IsSupported(in mode))
                return false;

            string? existingXml = _environment.ReadSettingsXml();
            int currentCount = Math.Max(1, VddSettingsXmlHelper.ReadMonitorCount(existingXml ?? string.Empty));
            string updatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode(existingXml, currentCount, mode);
            _environment.WriteSettingsXml(updatedXml);

            return await _pipeTransport.SendCommandAsync(
                VddSettingsXmlHelper.BuildReloadDriverCommand(),
                ct);
        }

        public async Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default)
        {
            string? existingXml = _environment.ReadSettingsXml();
            int restoreCount = _previousCount;
            if (restoreCount < 0)
            {
                restoreCount = 0;
            }

            string updatedXml = VddSettingsXmlHelper.UpdateMonitorCountAndMode(
                existingXml,
                restoreCount,
                VirtualDisplayMode.Default);
            _environment.WriteSettingsXml(updatedXml);

            return await _pipeTransport.SendCommandAsync(
                VddSettingsXmlHelper.BuildSetDisplayCountCommand(restoreCount),
                ct);
        }

        public Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default) =>
            Task.FromResult(true);

        private void RestorePreviousSettingsXml(string? existingXml, VirtualDisplayMode mode)
        {
            try
            {
                string rollback = existingXml ?? VddSettingsXmlHelper.UpdateMonitorCountAndMode(null, _previousCount, mode);
                _environment.WriteSettingsXml(rollback);
            }
            catch
            {
                // Best-effort rollback of settings XML
            }
        }

        private sealed class NullVddPipeTransport : IVddPipeTransport
        {
            public Task<bool> SendCommandAsync(string command, CancellationToken ct = default) =>
                Task.FromResult(false);
        }

        private sealed class NullVddEnvironment : IVddEnvironment
        {
            public bool IsDriverPresent() => false;
            public string? ReadSettingsXml() => null;
            public void WriteSettingsXml(string xmlContent) { }
        }
    }
}
