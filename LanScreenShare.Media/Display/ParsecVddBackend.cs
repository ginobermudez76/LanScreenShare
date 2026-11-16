using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Display;
using Microsoft.Win32;

namespace LanScreenShare.Media.Display
{
    public interface IParsecDeviceTransport : IDisposable
    {
        bool IsDriverInstalled();
        bool TryOpenDevice();
        int AddDisplay();
        bool RemoveDisplay(int displayIndex);
        bool SendUpdateHeartbeat();
        void CloseDevice();
    }

    [SupportedOSPlatform("windows")]
    internal sealed partial class WindowsParsecDeviceTransport : IParsecDeviceTransport
    {
        public const uint IoctlVddAdd = 0x0022E004;
        public const uint IoctlVddRemove = 0x0022E008;
        public const uint IoctlVddUpdate = 0x0022A00C;

        private const uint GenericRead = 0x80000000;
        private const uint GenericWrite = 0x40000000;
        private const uint FileShareReadWrite = 0x00000003;
        private const uint OpenExisting = 3;
        private const uint FileAttributeNormal = 0x00000080;
        private static readonly IntPtr InvalidHandle = new(-1);

        private IntPtr _deviceHandle = IntPtr.Zero;

        public bool IsDriverInstalled() => FindDevicePath() != null;

        public bool TryOpenDevice()
        {
            if (_deviceHandle != IntPtr.Zero && _deviceHandle != InvalidHandle)
                return true;

            string? devicePath = FindDevicePath();
            if (string.IsNullOrEmpty(devicePath))
                return false;

            IntPtr handle = CreateFileW(
                devicePath,
                GenericRead | GenericWrite,
                FileShareReadWrite,
                IntPtr.Zero,
                OpenExisting,
                FileAttributeNormal,
                IntPtr.Zero);

            if (handle == IntPtr.Zero || handle == InvalidHandle)
                return false;

            _deviceHandle = handle;
            return true;
        }

        public int AddDisplay()
        {
            if (!TryOpenDevice())
                return -1;

            byte[] inBuffer = new byte[32];
            int outIndex = 0;
            bool ok = DeviceIoControl(
                _deviceHandle,
                IoctlVddAdd,
                inBuffer,
                (uint)inBuffer.Length,
                ref outIndex,
                sizeof(int),
                out _,
                IntPtr.Zero);

            return ok ? outIndex : -1;
        }

        public bool RemoveDisplay(int displayIndex)
        {
            if (!TryOpenDevice() || displayIndex < 0)
                return false;

            byte[] inBuffer = new byte[32];
            inBuffer[0] = (byte)(displayIndex & 0xFF);
            int unusedOut = 0;
            return DeviceIoControl(
                _deviceHandle,
                IoctlVddRemove,
                inBuffer,
                (uint)inBuffer.Length,
                ref unusedOut,
                sizeof(int),
                out _,
                IntPtr.Zero);
        }

        public bool SendUpdateHeartbeat()
        {
            if (_deviceHandle == IntPtr.Zero || _deviceHandle == InvalidHandle)
                return false;

            byte[] inBuffer = new byte[32];
            int unusedOut = 0;
            return DeviceIoControl(
                _deviceHandle,
                IoctlVddUpdate,
                inBuffer,
                (uint)inBuffer.Length,
                ref unusedOut,
                sizeof(int),
                out _,
                IntPtr.Zero);
        }

        public void CloseDevice()
        {
            if (_deviceHandle != IntPtr.Zero && _deviceHandle != InvalidHandle)
            {
                CloseHandle(_deviceHandle);
                _deviceHandle = IntPtr.Zero;
            }
        }

        public void Dispose() => CloseDevice();

        private static string? FindDevicePath()
        {
            try
            {
                using var devClasses = Registry.LocalMachine.OpenSubKey(
                    @"SYSTEM\CurrentControlSet\Control\DeviceClasses\{00b41627-04c4-429e-a26e-026d820a8f07}");
                if (devClasses == null)
                    return null;

                foreach (string subKeyName in devClasses.GetSubKeyNames())
                {
                    if (!subKeyName.StartsWith("##?#", StringComparison.Ordinal))
                        continue;

                    return @"\\?\" + subKeyName[4..].Split('#', 4) is var parts && parts.Length >= 3
                        ? string.Join('#', parts)
                        : subKeyName[4..];
                }
            }
            catch
            {
                // Ignore registry probe failures
            }

            return null;
        }

        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial IntPtr CreateFileW(
            string lpFileName,
            uint dwDesiredAccess,
            uint dwShareMode,
            IntPtr lpSecurityAttributes,
            uint dwCreationDisposition,
            uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeviceIoControl(
            IntPtr hDevice,
            uint dwIoControlCode,
            [In] byte[] lpInBuffer,
            uint nInBufferSize,
            ref int lpOutBuffer,
            uint nOutBufferSize,
            out uint lpBytesReturned,
            IntPtr lpOverlapped);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);
    }

    public sealed class ParsecVddBackend : IVirtualDisplayBackend, IDisposable
    {
        public const string Id = "parsec-vdd";

        private readonly IParsecDeviceTransport _transport;

        public VirtualDisplayBackendCapabilities Capabilities { get; } = new()
        {
            BackendId = Id,
            MinimumIddCxVersion = new Version(1, 4),
            MinimumWindowsBuild = new Version(10, 0, 18362),
            HasNativeKernelWatchdog = true,
            NativeWatchdogTimeout = TimeSpan.FromSeconds(1),
            RequiresAdminForHotplug = false,
            SupportsDynamicModeChange = false
        };

        public ParsecVddBackend()
            : this(CreateDefaultTransport())
        {
        }

        internal ParsecVddBackend(IParsecDeviceTransport transport)
        {
            _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        }

        private static IParsecDeviceTransport CreateDefaultTransport() =>
            OperatingSystem.IsWindows() ? new WindowsParsecDeviceTransport() : new NullParsecTransport();

        public bool IsDriverInstalled() => _transport.IsDriverInstalled();

        public Task<VirtualDisplayInstance?> CreateVirtualMonitorAsync(VirtualDisplayMode mode, CancellationToken ct = default)
        {
            if (!IsDriverInstalled() || !VirtualDisplayModeValidator.IsSupported(in mode))
                return Task.FromResult<VirtualDisplayInstance?>(null);

            int index = _transport.AddDisplay();
            if (index < 0)
                return Task.FromResult<VirtualDisplayInstance?>(null);

            var instance = new VirtualDisplayInstance
            {
                BackendId = Id,
                MonitorId = index.ToString(CultureInfo.InvariantCulture),
                MonitorIndex = index,
                Mode = mode
            };
            return Task.FromResult<VirtualDisplayInstance?>(instance);
        }

        public Task<bool> UpdateModeAsync(string monitorId, VirtualDisplayMode mode, CancellationToken ct = default) =>
            Task.FromResult(VirtualDisplayModeValidator.IsSupported(in mode));

        public Task<bool> DestroyVirtualMonitorAsync(string monitorId, CancellationToken ct = default)
        {
            if (!int.TryParse(monitorId, NumberStyles.Integer, CultureInfo.InvariantCulture, out int index))
                index = 0;

            bool removed = _transport.RemoveDisplay(index);
            _transport.CloseDevice();
            return Task.FromResult(removed);
        }

        public Task<bool> SendHeartbeatAsync(string monitorId, CancellationToken ct = default) =>
            Task.FromResult(_transport.SendUpdateHeartbeat());

        public void Dispose() => _transport.Dispose();

        private sealed class NullParsecTransport : IParsecDeviceTransport
        {
            public bool IsDriverInstalled() => false;
            public bool TryOpenDevice() => false;
            public int AddDisplay() => -1;
            public bool RemoveDisplay(int displayIndex) => false;
            public bool SendUpdateHeartbeat() => false;
            public void CloseDevice() { }
            public void Dispose() { }
        }
    }
}
