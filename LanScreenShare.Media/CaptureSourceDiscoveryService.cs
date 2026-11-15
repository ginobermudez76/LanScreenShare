using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Models;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace LanScreenShare.Media
{
    public partial class CaptureSourceDiscoveryService
    {
        private const string DefaultProcessDisplayName = "Aplicación";

        private static readonly HashSet<string> ExcludedProcessNames = new(StringComparer.OrdinalIgnoreCase)
        {
            "SearchHost",
            "StartMenuExperienceHost",
            "TextInputHost",
            "ShellExperienceHost",
            "ApplicationFrameHost"
        };

        private static bool IsSystemWindowClass(string className, string title)
        {
            if (className == "Progman" || className == "WorkerW")
                return true;
            if (className == "Shell_TrayWnd" || className == "Shell_SecondaryTrayWnd")
                return true;
            if (className == "EdgeUiInputTopWndClass")
                return true;
            return className == "Windows.UI.Core.CoreWindow" && title == "Inicio";
        }

        private static CaptureSource CreateFallbackScreenSource(int width, int height, string deviceName, string subtitle) =>
            new()
            {
                Type = CaptureSourceType.Screen,
                Id = "screen:0",
                Name = "Pantalla principal",
                Subtitle = subtitle,
                MonitorIndex = 0,
                DeviceName = deviceName,
                X = 0,
                Y = 0,
                Width = width,
                Height = height,
                IsPrimary = true
            };

        private static (int left, int top, int width, int height) GetVirtualScreenDimensions()
        {
            int vLeft = GetSystemMetrics(SM_XVIRTUALSCREEN);
            int vTop = GetSystemMetrics(SM_YVIRTUALSCREEN);
            int vWidth = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            int vHeight = GetSystemMetrics(SM_CYVIRTUALSCREEN);

            if (vWidth <= 0 || vHeight <= 0)
            {
                vWidth = GetSystemMetrics(SM_CXSCREEN);
                vHeight = GetSystemMetrics(SM_CYSCREEN);
                vLeft = 0;
                vTop = 0;
            }

            return (vLeft, vTop, vWidth, vHeight);
        }

        private static string ReadUtf16NullTerminated(ReadOnlySpan<byte> utf16Bytes)
        {
            ReadOnlySpan<char> chars = MemoryMarshal.Cast<byte, char>(utf16Bytes);
            int nullIdx = chars.IndexOf('\0');
            return (nullIdx >= 0 ? chars[..nullIdx] : chars).ToString();
        }

        private static List<CaptureSource> EnumeratePhysicalMonitors()
        {
            var monitors = new List<CaptureSource>();
            int monitorIndex = 0;

            MonitorEnumProc callback = (IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData) =>
            {
                Span<byte> mi = stackalloc byte[MonitorInfoExByteSize];
                BinaryPrimitives.WriteInt32LittleEndian(mi, MonitorInfoExByteSize);

                if (GetMonitorInfo(hMonitor, mi))
                {
                    int left = BinaryPrimitives.ReadInt32LittleEndian(mi[4..8]);
                    int top = BinaryPrimitives.ReadInt32LittleEndian(mi[8..12]);
                    int right = BinaryPrimitives.ReadInt32LittleEndian(mi[12..16]);
                    int bottom = BinaryPrimitives.ReadInt32LittleEndian(mi[16..20]);
                    uint dwFlags = BinaryPrimitives.ReadUInt32LittleEndian(mi[36..40]);
                    int w = right - left;
                    int h = bottom - top;
                    bool isPrimary = (dwFlags & MONITORINFOF_PRIMARY) != 0;
                    string devName = ReadUtf16NullTerminated(mi[40..]);
                    string cleanDevName = devName.Replace(@"\\.\", "").Trim();

                    monitors.Add(new CaptureSource
                    {
                        Type = CaptureSourceType.Screen,
                        Id = $"screen:{monitorIndex}",
                        Name = $"Pantalla {monitorIndex + 1}",
                        Subtitle = $"{w} × {h} · {cleanDevName}",
                        MonitorIndex = monitorIndex,
                        DeviceName = devName,
                        Handle = hMonitor,
                        X = left,
                        Y = top,
                        Width = w,
                        Height = h,
                        IsPrimary = isPrimary
                    });

                    monitorIndex++;
                }
                return true;
            };

            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);
            GC.KeepAlive(callback);

            return monitors;
        }

        public static List<CaptureSource> GetScreenSources()
        {
            var sources = new List<CaptureSource>();

            if (!OperatingSystem.IsWindows())
            {
                sources.Add(CreateFallbackScreenSource(1920, 1080, "DefaultDisplay", "1920 × 1080 · Multiplataforma"));
                return sources;
            }

            try
            {
                var (vLeft, vTop, vWidth, vHeight) = GetVirtualScreenDimensions();
                var physicalMonitors = EnumeratePhysicalMonitors();
                sources.AddRange(physicalMonitors);

                if (sources.Count > 1)
                {
                    sources.Insert(0, new CaptureSource
                    {
                        Type = CaptureSourceType.AllScreens,
                        Id = "screen:all",
                        Name = "Todas las pantallas",
                        Subtitle = $"{vWidth} × {vHeight} · Escritorio completo",
                        DeviceName = "VirtualDesktop",
                        X = vLeft,
                        Y = vTop,
                        Width = vWidth,
                        Height = vHeight,
                        IsPrimary = false
                    });
                }
                else if (sources.Count == 0)
                {
                    sources.Add(CreateFallbackScreenSource(vWidth, vHeight, @"\\.\DISPLAY1", $"{vWidth} × {vHeight} · DISPLAY1"));
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CaptureDiscovery] Error enumerando pantallas: {ex.Message}");
            }

            return sources;
        }

        private static bool IsWindowCloaked(IntPtr hWnd) =>
            DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;

        private static bool TryGetWindowStrings(IntPtr hWnd, out string title, out string className)
        {
            Span<char> titleBuffer = stackalloc char[512];
            int titleLen = GetWindowText(hWnd, titleBuffer, titleBuffer.Length);
            if (titleLen <= 0)
            {
                title = string.Empty;
                className = string.Empty;
                return false;
            }
            title = titleBuffer[..titleLen].ToString().Trim();

            Span<char> classBuffer = stackalloc char[256];
            int classLen = GetClassName(hWnd, classBuffer, classBuffer.Length);
            className = classLen > 0 ? classBuffer[..classLen].ToString() : string.Empty;

            return !string.IsNullOrEmpty(title);
        }

        private static bool TryGetWindowDimensions(IntPtr hWnd, out NativeRect rect, out int width, out int height)
        {
            if (!GetWindowRect(hWnd, out rect))
            {
                width = 0;
                height = 0;
                return false;
            }

            width = rect.Right - rect.Left;
            height = rect.Bottom - rect.Top;
            return width >= 120 && height >= 120;
        }

        private static bool TryGetWindowProcessId(IntPtr hWnd, out uint processId)
        {
            uint threadId = GetWindowThreadProcessId(hWnd, out processId);
            return threadId != 0 && processId != 0;
        }

        private static bool IsCapturableWindow(IntPtr hWnd, out string title, out NativeRect rect)
        {
            title = string.Empty;
            rect = default;
            if (!IsWindowVisible(hWnd) || IsWindowCloaked(hWnd)) return false;
            if (!TryGetWindowStrings(hWnd, out title, out string className)) return false;
            if (IsSystemWindowClass(className, title)) return false;
            return TryGetWindowDimensions(hWnd, out rect, out _, out _);
        }

        private static bool TryCreateWindowSource(IntPtr hWnd, uint currentProcessId, out CaptureSource? source)
        {
            source = null;
            if (!IsCapturableWindow(hWnd, out string title, out NativeRect rect)) return false;
            if (!TryGetWindowProcessId(hWnd, out uint pid)) return false;

            pid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);
            if (pid == currentProcessId || ExcludedProcessNames.Contains(procName))
                return false;

            int w = rect.Right - rect.Left;
            int h = rect.Bottom - rect.Top;

            source = new CaptureSource
            {
                Type = CaptureSourceType.Window,
                Id = $"window:{hWnd.ToInt64()}",
                Name = title,
                Subtitle = $"{procName} · {w} × {h}",
                Handle = hWnd,
                ProcessName = procName,
                ProcessId = (int)pid,
                X = rect.Left,
                Y = rect.Top,
                Width = w,
                Height = h
            };

            return true;
        }

        public static List<CaptureSource> GetWindowSources()
        {
            var sources = new List<CaptureSource>();
            if (!OperatingSystem.IsWindows()) return sources;

            try
            {
                uint currentProcessId = (uint)Environment.ProcessId;
                EnumWindowsProc callback = (hWnd, _) =>
                {
                    if (TryCreateWindowSource(hWnd, currentProcessId, out var source) && source != null)
                    {
                        sources.Add(source);
                    }
                    return true;
                };
                EnumWindows(Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);
                GC.KeepAlive(callback);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[CaptureDiscovery] Error enumerando ventanas: {ex.Message}");
            }

            return sources;
        }

        public static int ResolveEffectiveProcessId(IntPtr hWnd, int fallbackPid = 0)
        {
            uint pid = (uint)Math.Max(0, fallbackPid);
            if (hWnd != IntPtr.Zero && TryGetWindowProcessId(hWnd, out uint winPid))
            {
                pid = winPid;
            }

            if (pid == 0) return 0;

            uint rootPid = ResolveEffectiveWindowProcess(hWnd, pid, out string procName);
            if (rootPid > 0) return (int)rootPid;

            int audioSessionPid = FindAudioSessionProcessId(procName, (int)pid);
            if (audioSessionPid > 0)
            {
                Console.WriteLine($"[CaptureDiscovery] Sesión de audio identificada para '{procName}': PID {audioSessionPid}");
                return audioSessionPid;
            }

            return (int)pid;
        }

        private static uint FindUwpChildProcess(IntPtr hWnd, uint initialPid)
        {
            uint realChildPid = 0;
            EnumWindowsProc callback = (childHwnd, _) =>
            {
                if (!TryGetWindowProcessId(childHwnd, out uint cPid) || cPid == initialPid)
                    return true;

                realChildPid = cPid;
                return false;
            };
            EnumChildWindows(hWnd, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero);
            GC.KeepAlive(callback);
            return realChildPid;
        }

        private static uint ResolveUwpChildProcess(IntPtr hWnd, uint initialPid, ref string procName)
        {
            if (hWnd == IntPtr.Zero || !procName.Equals("ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
                return initialPid;

            uint childPid = FindUwpChildProcess(hWnd, initialPid);
            if (childPid == 0) return initialPid;

            procName = GetProcessNameSafe(childPid);
            return childPid;
        }

        private static uint ResolveRootProcessIfApplicable(uint pid, string procName)
        {
            if (string.IsNullOrEmpty(procName) || procName.Equals(DefaultProcessDisplayName, StringComparison.OrdinalIgnoreCase))
                return pid;

            uint rootPid = FindApplicationRootProcessId(pid, procName);
            return rootPid > 0 ? rootPid : pid;
        }

        private static uint ResolveEffectiveWindowProcess(IntPtr hWnd, uint initialPid, out string procName)
        {
            procName = GetProcessNameSafe(initialPid);
            uint pid = ResolveUwpChildProcess(hWnd, initialPid, ref procName);
            return ResolveRootProcessIfApplicable(pid, procName);
        }

        public static int FindAudioSessionProcessId(string procName, int fallbackPid)
        {
            if (!OperatingSystem.IsWindows())
                return fallbackPid;

            if (string.IsNullOrEmpty(procName) || procName.Equals(DefaultProcessDisplayName, StringComparison.OrdinalIgnoreCase))
                return fallbackPid;

            try
            {
                using var enumerator = new MMDeviceEnumerator();
                using var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
                var mgr = device.AudioSessionManager;
                var sessions = mgr.Sessions;

                int detectedPid = ScanSessionsForProcess(sessions, procName);
                if (detectedPid > 0)
                    return detectedPid;
            }
            catch
            {
                // Dispositivo o driver no expone IAudioSessionManager2 o sin sesiones activas
            }

            return fallbackPid;
        }

        [SupportedOSPlatform("windows")]
        private static int ScanSessionsForProcess(SessionCollection sessions, string procName)
        {
            int matchingPid = 0;
            for (int i = 0; i < sessions.Count; i++)
            {
                var s = sessions[i];
                uint sPid = s.GetProcessID;
                if (sPid == 0) continue;

                string sName = GetProcessNameSafe(sPid);
                if (!sName.Equals(procName, StringComparison.OrdinalIgnoreCase)) continue;

                if (s.State == AudioSessionState.AudioSessionStateActive)
                {
                    return (int)sPid;
                }
                matchingPid = (int)sPid;
            }
            return matchingPid;
        }

        private static (Dictionary<uint, uint> ParentMap, Dictionary<uint, string> NameMap) ReadProcessHierarchy(IntPtr snapshot)
        {
            var parentMap = new Dictionary<uint, uint>();
            var nameMap = new Dictionary<uint, string>();

            var (entrySize, parentOffset, exeOffset) = IntPtr.Size == 8
                ? (568, 32, 44)
                : (556, 24, 36);

            Span<byte> pe = stackalloc byte[entrySize];
            BinaryPrimitives.WriteUInt32LittleEndian(pe, (uint)entrySize);

            if (Process32First(snapshot, pe))
            {
                do
                {
                    uint pid = BinaryPrimitives.ReadUInt32LittleEndian(pe[8..12]);
                    uint parentPid = BinaryPrimitives.ReadUInt32LittleEndian(pe.Slice(parentOffset, 4));
                    parentMap[pid] = parentPid;
                    string exeName = ReadUtf16NullTerminated(pe[exeOffset..]);
                    int dotIdx = exeName.LastIndexOf('.');
                    if (dotIdx > 0)
                    {
                        exeName = exeName.Substring(0, dotIdx);
                    }
                    nameMap[pid] = exeName;
                } while (Process32Next(snapshot, pe));
            }

            return (parentMap, nameMap);
        }

        private static uint FindRootInProcessHierarchy(
            uint startPid,
            string targetName,
            Dictionary<uint, uint> parentMap,
            Dictionary<uint, string> nameMap)
        {
            uint current = startPid;
            for (int depth = 0; depth < 10; depth++)
            {
                if (!parentMap.TryGetValue(current, out uint parentPid)) break;
                if (parentPid == 0 || parentPid == current) break;

                if (nameMap.TryGetValue(parentPid, out string? parentName) &&
                    parentName.Equals(targetName, StringComparison.OrdinalIgnoreCase))
                {
                    current = parentPid;
                }
                else
                {
                    break;
                }
            }

            return current;
        }

        public static uint FindApplicationRootProcessId(uint pid, string procName)
        {
            if (pid == 0) return 0;

            IntPtr snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1)) return pid;

            try
            {
                var (parentMap, nameMap) = ReadProcessHierarchy(snapshot);
                return FindRootInProcessHierarchy(pid, procName, parentMap, nameMap);
            }
            catch
            {
                return pid;
            }
            finally
            {
                CloseHandle(snapshot);
            }
        }

        private static string GetProcessNameSafe(uint pid)
        {
            if (pid == 0) return DefaultProcessDisplayName;

            try
            {
                using var proc = Process.GetProcessById((int)pid);
                return proc.ProcessName;
            }
            catch
            {
                return DefaultProcessDisplayName;
            }
        }

        #region Win32 P/Invoke

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;
        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int MONITORINFOF_PRIMARY = 1;
        private const int DWMWA_CLOAKED = 14;
        private const uint TH32CS_SNAPPROCESS = 0x00000002;

        private const int MonitorInfoExByteSize = 104;

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32First(IntPtr hSnapshot, Span<byte> lppe);

        [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool Process32Next(IntPtr hSnapshot, Span<byte> lppe);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(IntPtr hObject);

        [StructLayout(LayoutKind.Sequential)]
        public struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref NativeRect lprcMonitor, IntPtr dwData);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, IntPtr lpfnEnum, IntPtr dwData);

        [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetMonitorInfo(IntPtr hMonitor, Span<byte> lpmi);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumWindows(IntPtr enumProc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool EnumChildWindows(IntPtr hWndParent, IntPtr lpEnumFunc, IntPtr lParam);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindowVisible(IntPtr hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetWindowText(IntPtr hWnd, Span<char> lpString, int nMaxCount);

        [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
        private static partial int GetClassName(IntPtr hWnd, Span<char> lpClassName, int nMaxCount);

        [LibraryImport("user32.dll")]
        private static partial uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetWindowRect(IntPtr hWnd, out NativeRect lpRect);

        [LibraryImport("dwmapi.dll")]
        private static partial int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

        #endregion
    }
}
