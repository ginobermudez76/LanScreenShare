using System;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LanScreenShare.Media
{
    public enum DesktopSwitchOutcome
    {
        Attached,
        AlreadyAttached,
        AccessDenied,
        ThreadBusyOrHasGui,
        Failed,
        UnsupportedPlatform
    }

    internal enum DxgiDesktopDisposition
    {
        CaptureOnDesktop,
        PauseUnauthorizedSecureDesktop
    }

    internal static class DesktopSwitchClassifier
    {
        internal const int ErrorAccessDenied = 5;
        internal const int ErrorBusy = 170;

        internal static DesktopSwitchOutcome ClassifyAttachFailure(int win32Error) =>
            win32Error switch
            {
                ErrorAccessDenied => DesktopSwitchOutcome.AccessDenied,
                ErrorBusy => DesktopSwitchOutcome.ThreadBusyOrHasGui,
                _ => DesktopSwitchOutcome.Failed
            };

        internal static DxgiDesktopDisposition ResolveDxgiDisposition(bool isSecureDesktop, bool attachedToInputDesktop) =>
            isSecureDesktop && !attachedToInputDesktop
                ? DxgiDesktopDisposition.PauseUnauthorizedSecureDesktop
                : DxgiDesktopDisposition.CaptureOnDesktop;
    }

    /// <summary>
    /// Administra la detección y conmutación de escritorios Win32 (Default vs Winlogon / Escritorio Seguro).
    /// Especificación: ADR-002 / Issue #40.
    /// </summary>
    [SupportedOSPlatform("windows")]
    public static partial class WindowsDesktopManager
    {
        public const string DesktopDefault = "Default";
        public const string DesktopWinlogon = "Winlogon";
        public const string DesktopScreenSaver = "Screen-saver";

        private const uint DESKTOP_READOBJECTS = 0x0001;
        private const uint DESKTOP_SWITCHDESKTOP = 0x0100;
        private const uint DESKTOP_ALL_ACCESS = 0x01FF;

        private const int UOI_NAME = 2;
        private const int ERROR_ACCESS_DENIED = 5;

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr OpenInputDesktop(
            uint dwFlags,
            [MarshalAs(UnmanagedType.Bool)] bool fInherit,
            uint dwDesiredAccess);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseDesktop(IntPtr hDesktop);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SetThreadDesktop(IntPtr hDesktop);

        [LibraryImport("user32.dll", SetLastError = true)]
        private static partial IntPtr GetThreadDesktop(uint dwThreadId);

        [LibraryImport("kernel32.dll")]
        private static partial uint GetCurrentThreadId();

        [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetUserObjectInformation(
            IntPtr hObj,
            int nIndex,
            Span<byte> pvInfo,
            uint nLength,
            out uint lpnLengthNeeded);

        public static string? GetDesktopName(IntPtr hDesktop)
        {
            if (hDesktop == IntPtr.Zero)
                return null;

            GetUserObjectInformation(hDesktop, UOI_NAME, Span<byte>.Empty, 0, out uint needed);
            if (needed == 0)
                return null;

            byte[] buffer = new byte[needed];
            if (!GetUserObjectInformation(hDesktop, UOI_NAME, buffer, needed, out _))
                return null;

            return Encoding.Unicode.GetString(buffer).TrimEnd('\0');
        }

        public static bool IsSecureDesktopName(string name) =>
            string.Equals(name, DesktopWinlogon, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(name, DesktopScreenSaver, StringComparison.OrdinalIgnoreCase);

        public static bool IsActiveDesktopSecure(out string desktopName)
        {
            if (!OperatingSystem.IsWindows())
            {
                desktopName = DesktopDefault;
                return false;
            }

            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS | DESKTOP_SWITCHDESKTOP);
            if (hDesktop == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                {
                    desktopName = DesktopWinlogon;
                    return true;
                }

                desktopName = "Unknown";
                return false;
            }

            try
            {
                string? name = GetDesktopName(hDesktop);
                desktopName = name ?? DesktopDefault;
                return IsSecureDesktopName(desktopName);
            }
            finally
            {
                CloseDesktop(hDesktop);
            }
        }

        public static bool TryAttachThreadToInputDesktop(out string desktopName, out bool isSecureDesktop) =>
            TryAttachThreadToInputDesktopDetailed(out desktopName, out isSecureDesktop, out _);

        public static bool TryAttachThreadToInputDesktopDetailed(
            out string desktopName,
            out bool isSecureDesktop,
            out DesktopSwitchOutcome outcome)
        {
            if (!OperatingSystem.IsWindows())
            {
                desktopName = DesktopDefault;
                isSecureDesktop = false;
                outcome = DesktopSwitchOutcome.UnsupportedPlatform;
                return false;
            }

            IntPtr hDesktop = OpenInputDesktop(0, false, DESKTOP_ALL_ACCESS);
            if (hDesktop == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                if (err == ERROR_ACCESS_DENIED)
                {
                    desktopName = DesktopWinlogon;
                    isSecureDesktop = true;
                    outcome = DesktopSwitchOutcome.AccessDenied;
                    return false;
                }

                desktopName = "Unknown";
                isSecureDesktop = false;
                outcome = DesktopSwitchClassifier.ClassifyAttachFailure(err);
                return false;
            }

            try
            {
                string? name = GetDesktopName(hDesktop);
                desktopName = name ?? DesktopDefault;
                isSecureDesktop = IsSecureDesktopName(desktopName);

                IntPtr hThreadDesktop = GetThreadDesktop(GetCurrentThreadId());
                string? currentThreadDesktopName = GetDesktopName(hThreadDesktop);
                if (string.Equals(desktopName, currentThreadDesktopName, StringComparison.OrdinalIgnoreCase))
                {
                    outcome = DesktopSwitchOutcome.AlreadyAttached;
                    return true;
                }

                if (SetThreadDesktop(hDesktop))
                {
                    outcome = DesktopSwitchOutcome.Attached;
                    return true;
                }

                outcome = DesktopSwitchClassifier.ClassifyAttachFailure(Marshal.GetLastWin32Error());
                return false;
            }
            finally
            {
                CloseDesktop(hDesktop);
            }
        }
    }
}
