using System;
using System.Runtime.InteropServices;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Desktop
{
    internal static class NativeDisplayHelper
    {
        private const int EnumCurrentSettings = -1;
        private const int DefaultRefreshRateHz = 60;

        public static VirtualDisplayMode? TryGetPrimaryPhysicalDisplayMode()
        {
            if (!OperatingSystem.IsWindows())
                return null;

            var devMode = new DevModeW
            {
                dmSize = (ushort)Marshal.SizeOf<DevModeW>()
            };

            if (!EnumDisplaySettingsW(null, EnumCurrentSettings, ref devMode))
                return null;

            int width = (int)devMode.dmPelsWidth;
            int height = (int)devMode.dmPelsHeight;
            int rawHz = (int)devMode.dmDisplayFrequency;
            int refreshHz = rawHz > 1
                ? Math.Clamp(rawHz, VirtualDisplayModeValidator.MinRefreshRateHz, VirtualDisplayModeValidator.MaxRefreshRateHz)
                : DefaultRefreshRateHz;

            var mode = new VirtualDisplayMode(width, height, refreshHz);
            return VirtualDisplayModeValidator.IsSupported(in mode) ? mode : null;
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool EnumDisplaySettingsW(
            string? lpszDeviceName,
            int iModeNum,
            ref DevModeW lpDevMode);

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DevModeW
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmDeviceName;
            public ushort dmSpecVersion;
            public ushort dmDriverVersion;
            public ushort dmSize;
            public ushort dmDriverExtra;
            public uint dmFields;
            public int dmPositionX;
            public int dmPositionY;
            public uint dmDisplayOrientation;
            public uint dmDisplayFixedOutput;
            public short dmColor;
            public short dmDuplex;
            public short dmYResolution;
            public short dmTTOption;
            public short dmCollate;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string dmFormName;
            public ushort dmLogPixels;
            public uint dmBitsPerPel;
            public uint dmPelsWidth;
            public uint dmPelsHeight;
            public uint dmDisplayFlags;
            public uint dmDisplayFrequency;
            public uint dmICMMethod;
            public uint dmICMIntent;
            public uint dmMediaType;
            public uint dmDitherType;
            public uint dmReserved1;
            public uint dmReserved2;
            public uint dmPanningWidth;
            public uint dmPanningHeight;
        }
    }
}
