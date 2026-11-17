using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using LanScreenShare.Core.Display;

namespace LanScreenShare.Media.Display
{
    public interface ICcdPlatformAdapter
    {
        bool TryQueryActiveDisplayPaths(out IReadOnlyList<DisplayPathSnapshot> paths);
        bool ApplyExtendTopology();
        bool ApplySuppliedConfig(IReadOnlyList<DisplayPathSnapshot> paths);
    }

    public sealed partial class WindowsDisplayTopologyManager : IDisplayTopologyManager
    {
        private readonly ICcdPlatformAdapter _adapter;

        public WindowsDisplayTopologyManager()
            : this(CreateDefaultAdapter())
        {
        }

        internal WindowsDisplayTopologyManager(ICcdPlatformAdapter adapter)
        {
            _adapter = adapter ?? throw new ArgumentNullException(nameof(adapter));
        }

        private static ICcdPlatformAdapter CreateDefaultAdapter() =>
            OperatingSystem.IsWindows() ? new Win32CcdAdapter() : new NullCcdAdapter();

        public DisplayTopologySnapshot CaptureSnapshot()
        {
            _adapter.TryQueryActiveDisplayPaths(out var paths);
            return new DisplayTopologySnapshot
            {
                CapturedAtUtc = DateTime.UtcNow,
                Paths = paths
            };
        }

        public bool ApplyExtendTopology(string? preferredDeviceName, VirtualDisplayMode mode)
        {
            if (!_adapter.ApplyExtendTopology())
                return false;

            if (!VirtualDisplayModeValidator.IsSupported(in mode))
                return true;

            if (!TryQueryPathsReadyForTarget(preferredDeviceName, out var activePaths) || activePaths.Count == 0)
                return true;

            var updated = ApplyModeToTargetPath(activePaths, preferredDeviceName, mode);
            if (updated == null)
                return true;

            _adapter.ApplySuppliedConfig(updated);
            return true;
        }

        public bool EnsureExtendedLayout(string? preferredDeviceName)
        {
            if (!_adapter.TryQueryActiveDisplayPaths(out var activePaths))
                return false;

            int targetIdx = FindTargetPathIndexOrNegative(activePaths, preferredDeviceName);
            bool needsExtend = activePaths.Count < 2 || (targetIdx >= 0 && HasOverlappingBounds(activePaths, targetIdx));
            if (!needsExtend)
                return true;

            if (!_adapter.ApplyExtendTopology())
                return false;

            if (!TryQueryPathsReadyForTarget(preferredDeviceName, out var afterExtend) || afterExtend.Count < 2)
                return afterExtend.Count >= 2;

            targetIdx = FindTargetPathIndexOrNegative(afterExtend, preferredDeviceName);
            if (targetIdx >= 0 && HasOverlappingBounds(afterExtend, targetIdx))
            {
                var separated = SeparateOverlappingTargetPath(afterExtend, targetIdx);
                return _adapter.ApplySuppliedConfig(separated);
            }

            return true;
        }

        private bool TryQueryPathsReadyForTarget(string? preferredDeviceName, out IReadOnlyList<DisplayPathSnapshot> activePaths)
        {
            if (!_adapter.TryQueryActiveDisplayPaths(out activePaths))
                return false;

            if (_adapter is not Win32CcdAdapter || string.IsNullOrWhiteSpace(preferredDeviceName))
                return true;

            for (int attempt = 0; attempt < 12; attempt++)
            {
                if (activePaths.Any(p => string.Equals(p.DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase)))
                    return true;

                System.Threading.Thread.Sleep(50);
                if (!_adapter.TryQueryActiveDisplayPaths(out activePaths))
                    return false;
            }

            return true;
        }

        public bool RestoreSnapshot(DisplayTopologySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Paths.Count == 0)
                return true;

            return _adapter.ApplySuppliedConfig(snapshot.Paths);
        }

        internal static List<DisplayPathSnapshot>? ApplyModeToTargetPath(
            IReadOnlyList<DisplayPathSnapshot> activePaths,
            string? preferredDeviceName,
            VirtualDisplayMode mode)
        {
            if (activePaths.Count == 0)
                return null;

            int targetIdx = FindTargetPathIndexOrNegative(activePaths, preferredDeviceName);
            if (targetIdx < 0)
                return null;

            var target = activePaths[targetIdx];
            bool overlapsOther = activePaths.Count > 1 && HasOverlappingBounds(activePaths, targetIdx);
            if (!NeedsTargetPathUpdate(in target, in mode, overlapsOther))
                return null;

            var copy = activePaths.ToList();
            copy[targetIdx] = BuildUpdatedTargetPath(in target, activePaths, targetIdx, in mode, overlapsOther);
            return copy;
        }

        private static bool NeedsTargetPathUpdate(
            in DisplayPathSnapshot target,
            in VirtualDisplayMode mode,
            bool overlapsOther)
        {
            if (overlapsOther || target.Width != mode.Width || target.Height != mode.Height)
                return true;

            if (target.RefreshRateNumerator == 0)
                return false;

            uint currentHz = target.RefreshRateNumerator / Math.Max(1u, target.RefreshRateDenominator);
            return currentHz != (uint)mode.RefreshRateHz;
        }

        private static DisplayPathSnapshot BuildUpdatedTargetPath(
            in DisplayPathSnapshot target,
            IReadOnlyList<DisplayPathSnapshot> activePaths,
            int targetIdx,
            in VirtualDisplayMode mode,
            bool overlapsOther)
        {
            return target with
            {
                Width = mode.Width,
                Height = mode.Height,
                PositionX = overlapsOther ? ComputeNonOverlappingPositionX(activePaths, targetIdx) : target.PositionX,
                PositionY = overlapsOther ? 0 : target.PositionY,
                RefreshRateNumerator = (uint)mode.RefreshRateHz,
                RefreshRateDenominator = 1
            };
        }

        private static bool HasOverlappingBounds(IReadOnlyList<DisplayPathSnapshot> paths, int targetIdx)
        {
            var target = paths[targetIdx];
            int targetRight = target.PositionX + Math.Max(1, target.Width);
            int targetBottom = target.PositionY + Math.Max(1, target.Height);

            for (int i = 0; i < paths.Count; i++)
            {
                if (i == targetIdx)
                    continue;

                var other = paths[i];
                int otherRight = other.PositionX + Math.Max(1, other.Width);
                int otherBottom = other.PositionY + Math.Max(1, other.Height);

                bool overlapX = target.PositionX < otherRight && targetRight > other.PositionX;
                bool overlapY = target.PositionY < otherBottom && targetBottom > other.PositionY;
                if (overlapX && overlapY)
                    return true;
            }

            return false;
        }

        private static int ComputeNonOverlappingPositionX(IReadOnlyList<DisplayPathSnapshot> paths, int targetIdx)
        {
            int maxRight = 0;
            for (int i = 0; i < paths.Count; i++)
            {
                if (i == targetIdx)
                    continue;

                int right = paths[i].PositionX + Math.Max(1, paths[i].Width);
                if (right > maxRight)
                    maxRight = right;
            }

            return maxRight;
        }

        private static List<DisplayPathSnapshot> SeparateOverlappingTargetPath(
            IReadOnlyList<DisplayPathSnapshot> paths,
            int targetIdx)
        {
            var copy = paths.ToList();
            copy[targetIdx] = copy[targetIdx] with
            {
                PositionX = ComputeNonOverlappingPositionX(paths, targetIdx),
                PositionY = 0
            };
            return copy;
        }

        private static int FindTargetPathIndexOrNegative(IReadOnlyList<DisplayPathSnapshot> paths, string? preferredDeviceName)
        {
            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (string.Equals(paths[i].DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }

                // Si se especificó un dispositivo concreto y no aparece entre las rutas activas,
                // evitamos sobrescribir la resolución de otro monitor físico.
                return -1;
            }

            return paths.Count - 1;
        }

        internal const int SourceDeviceNamePacketSize = 84;

        internal static byte[] BuildSourceDeviceNameRequest(uint luidLow, int luidHigh, uint sourceId)
        {
            byte[] packet = new byte[SourceDeviceNamePacketSize];
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(0, 4), 1u);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4, 4), SourceDeviceNamePacketSize);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8, 4), luidLow);
            BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(12, 4), luidHigh);
            BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(16, 4), sourceId);
            return packet;
        }

        internal static string? ParseSourceDeviceNameResponse(ReadOnlySpan<byte> packet)
        {
            if (packet.Length < SourceDeviceNamePacketSize)
                return null;

            string name = VirtualDriversVddBackend.ReadUtf16NullTerminated(packet.Slice(20, 64));
            return string.IsNullOrWhiteSpace(name) ? null : name;
        }

        internal static bool ApplyWithFallbackStrategy(
            Func<bool> applyConfig,
            Action clearTargetModeIndices,
            Func<bool> applyInternalTopology,
            int pathCount)
        {
            if (applyConfig())
                return true;

            clearTargetModeIndices();
            if (applyConfig())
                return true;

            return pathCount == 1 && applyInternalTopology();
        }

        private const uint DisplayConfigModeInfoTypeSource = 1;
        private const uint DisplayConfigModeInfoTypeTarget = 2;
        private const uint DisplayConfigPathActive = 0x00000001;

        internal static (DisplayConfigPathInfo[] Paths, DisplayConfigModeInfo[] Modes) PrepareSuppliedNativeArrays(
            IReadOnlyList<DisplayPathSnapshot> paths)
        {
            var nativePaths = new DisplayConfigPathInfo[paths.Count];
            var nativeModes = new DisplayConfigModeInfo[paths.Count * 2];
            for (int i = 0; i < paths.Count; i++)
            {
                PopulateNativePathAndModes(paths[i], i, nativePaths, nativeModes);
            }

            return (nativePaths, nativeModes);
        }

        internal static void ClearTargetModeIndices(DisplayConfigPathInfo[] nativePaths)
        {
            for (int i = 0; i < nativePaths.Length; i++)
                nativePaths[i].TargetInfo.ModeInfoIdx = uint.MaxValue;
        }

        internal static void PopulateNativePathAndModes(
            DisplayPathSnapshot snap,
            int index,
            DisplayConfigPathInfo[] paths,
            DisplayConfigModeInfo[] modes)
        {
            uint srcModeIdx = (uint)(index * 2);
            uint tgtModeIdx = srcModeIdx + 1;
            var luid = new Luid { LowPart = snap.AdapterLuidLow, HighPart = snap.AdapterLuidHigh };

            modes[srcModeIdx] = CreateSourceMode(luid, snap);
            modes[tgtModeIdx] = CreateTargetMode(luid, snap);

            paths[index] = new DisplayConfigPathInfo
            {
                SourceInfo = new DisplayConfigPathSourceInfo { AdapterId = luid, Id = snap.SourceId, ModeInfoIdx = srcModeIdx },
                TargetInfo = new DisplayConfigPathTargetInfo
                {
                    AdapterId = luid,
                    Id = snap.TargetId,
                    ModeInfoIdx = tgtModeIdx,
                    Rotation = (uint)snap.Rotation,
                    Scaling = (uint)snap.Scaling,
                    RefreshRateNumerator = snap.RefreshRateNumerator > 0 ? snap.RefreshRateNumerator : 60,
                    RefreshRateDenominator = snap.RefreshRateDenominator > 0 ? snap.RefreshRateDenominator : 1,
                    TargetAvailable = 1
                },
                Flags = DisplayConfigPathActive
            };
        }

        private static DisplayConfigModeInfo CreateSourceMode(Luid luid, DisplayPathSnapshot snap)
        {
            var mode = new DisplayConfigModeInfo { InfoType = DisplayConfigModeInfoTypeSource, Id = snap.SourceId, AdapterId = luid };
            mode.SourceWidth = (uint)Math.Max(1, snap.Width);
            mode.SourceHeight = (uint)Math.Max(1, snap.Height);
            mode.SourcePixelFormat = snap.PixelFormat > 0 ? (uint)snap.PixelFormat : 4;
            mode.PositionX = snap.PositionX;
            mode.PositionY = snap.PositionY;
            return mode;
        }

        private static DisplayConfigModeInfo CreateTargetMode(Luid luid, DisplayPathSnapshot snap)
        {
            uint num = snap.RefreshRateNumerator > 0 ? snap.RefreshRateNumerator : 60;
            uint den = snap.RefreshRateDenominator > 0 ? snap.RefreshRateDenominator : 1;
            uint width = (uint)Math.Max(1, snap.Width);
            uint height = (uint)Math.Max(1, snap.Height);
            ulong hz = Math.Max(1UL, num / Math.Max(1u, den));
            var mode = new DisplayConfigModeInfo { InfoType = DisplayConfigModeInfoTypeTarget, Id = snap.TargetId, AdapterId = luid };
            mode.TargetPixelRate = width * height * hz;
            mode.TargetHSyncNumerator = (uint)(height * hz * 11 / 10);
            mode.TargetHSyncDenominator = 1;
            mode.TargetVSyncNumerator = num;
            mode.TargetVSyncDenominator = den;
            mode.TargetActiveWidth = width;
            mode.TargetActiveHeight = height;
            mode.TargetTotalWidth = width;
            mode.TargetTotalHeight = height;
            mode.TargetVideoStandard = 255;
            mode.TargetScanLineOrdering = 1;
            return mode;
        }

        internal static DisplayPathSnapshot ToSnapshot(in DisplayConfigPathInfo path, DisplayConfigModeInfo[] modes)
        {
            var src = GetModeSafe(modes, path.SourceInfo.ModeInfoIdx);
            return new DisplayPathSnapshot
            {
                AdapterLuidLow = path.SourceInfo.AdapterId.LowPart,
                AdapterLuidHigh = path.SourceInfo.AdapterId.HighPart,
                SourceId = path.SourceInfo.Id,
                TargetId = path.TargetInfo.Id,
                PositionX = src.PositionX,
                PositionY = src.PositionY,
                Width = (int)src.SourceWidth,
                Height = (int)src.SourceHeight,
                RefreshRateNumerator = path.TargetInfo.RefreshRateNumerator,
                RefreshRateDenominator = path.TargetInfo.RefreshRateDenominator,
                Rotation = (int)path.TargetInfo.Rotation,
                Scaling = (int)path.TargetInfo.Scaling,
                PixelFormat = (int)src.SourcePixelFormat
            };
        }

        private static DisplayConfigModeInfo GetModeSafe(DisplayConfigModeInfo[] modes, uint index) =>
            index < (uint)modes.Length ? modes[index] : default;

        [StructLayout(LayoutKind.Sequential)]
        internal struct Luid { public uint LowPart; public int HighPart; }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathSourceInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathTargetInfo
        {
            public Luid AdapterId;
            public uint Id;
            public uint ModeInfoIdx;
            public uint OutputTechnology;
            public uint Rotation;
            public uint Scaling;
            public uint RefreshRateNumerator;
            public uint RefreshRateDenominator;
            public uint ScanLineOrdering;
            public int TargetAvailable;
            public uint StatusFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct DisplayConfigPathInfo
        {
            public DisplayConfigPathSourceInfo SourceInfo;
            public DisplayConfigPathTargetInfo TargetInfo;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Explicit, Size = 64)]
        internal struct DisplayConfigModeInfo
        {
            [FieldOffset(0)] public uint InfoType;
            [FieldOffset(4)] public uint Id;
            [FieldOffset(8)] public Luid AdapterId;

            [FieldOffset(16)] public uint SourceWidth;
            [FieldOffset(20)] public uint SourceHeight;
            [FieldOffset(24)] public uint SourcePixelFormat;
            [FieldOffset(28)] public int PositionX;
            [FieldOffset(32)] public int PositionY;

            [FieldOffset(16)] public ulong TargetPixelRate;
            [FieldOffset(24)] public uint TargetHSyncNumerator;
            [FieldOffset(28)] public uint TargetHSyncDenominator;
            [FieldOffset(32)] public uint TargetVSyncNumerator;
            [FieldOffset(36)] public uint TargetVSyncDenominator;
            [FieldOffset(40)] public uint TargetActiveWidth;
            [FieldOffset(44)] public uint TargetActiveHeight;
            [FieldOffset(48)] public uint TargetTotalWidth;
            [FieldOffset(52)] public uint TargetTotalHeight;
            [FieldOffset(56)] public uint TargetVideoStandard;
            [FieldOffset(60)] public uint TargetScanLineOrdering;
        }

        [SupportedOSPlatform("windows")]
        internal sealed partial class Win32CcdAdapter : ICcdPlatformAdapter
        {
            private const uint QdcOnlyActivePaths = 0x00000002;
            private const uint SdcApply = 0x00000080;
            private const uint SdcTopologyExtend = 0x00000004;
            private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
            private const uint SdcAllowChanges = 0x00000400;
            private const uint SdcSaveToDatabase = 0x00000200;

            public bool TryQueryActiveDisplayPaths(out IReadOnlyList<DisplayPathSnapshot> paths)
            {
                paths = Array.Empty<DisplayPathSnapshot>();
                if (GetDisplayConfigBufferSizes(QdcOnlyActivePaths, out uint pathCount, out uint modeCount) != 0)
                    return false;
                if (pathCount == 0)
                    return true;

                var pathInfos = new DisplayConfigPathInfo[pathCount];
                var modeInfos = new DisplayConfigModeInfo[modeCount];
                var pathsHandle = GCHandle.Alloc(pathInfos, GCHandleType.Pinned);
                var modesHandle = GCHandle.Alloc(modeInfos, GCHandleType.Pinned);
                try
                {
                    int err = QueryDisplayConfig(
                        QdcOnlyActivePaths,
                        ref pathCount,
                        pathsHandle.AddrOfPinnedObject(),
                        ref modeCount,
                        modesHandle.AddrOfPinnedObject(),
                        IntPtr.Zero);
                    if (err != 0)
                        return false;
                }
                finally
                {
                    pathsHandle.Free();
                    modesHandle.Free();
                }

                var result = new List<DisplayPathSnapshot>((int)pathCount);
                for (int i = 0; i < (int)pathCount; i++)
                {
                    var snap = ToSnapshot(pathInfos[i], modeInfos);
                    string? gdiName = QuerySourceDeviceName(pathInfos[i].SourceInfo.AdapterId, pathInfos[i].SourceInfo.Id);
                    result.Add(string.IsNullOrWhiteSpace(gdiName) ? snap : snap with { DeviceName = gdiName });
                }

                paths = result;
                return true;
            }

            public bool ApplyExtendTopology() =>
                SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcApply | SdcTopologyExtend) == 0;

            public bool ApplySuppliedConfig(IReadOnlyList<DisplayPathSnapshot> paths)
            {
                if (paths.Count == 0)
                    return false;

                var (nativePaths, nativeModes) = PrepareSuppliedNativeArrays(paths);

                return WindowsDisplayTopologyManager.ApplyWithFallbackStrategy(
                    () => InvokeSetDisplayConfig(nativePaths, nativeModes),
                    () => ClearTargetModeIndices(nativePaths),
                    () => SetDisplayConfig(0, IntPtr.Zero, 0, IntPtr.Zero, SdcApply | 0x00000001u /* SdcTopologyInternal */) == 0,
                    nativePaths.Length);
            }

            private static bool InvokeSetDisplayConfig(DisplayConfigPathInfo[] nativePaths, DisplayConfigModeInfo[] nativeModes)
            {
                var pathsHandle = GCHandle.Alloc(nativePaths, GCHandleType.Pinned);
                var modesHandle = GCHandle.Alloc(nativeModes, GCHandleType.Pinned);
                try
                {
                    uint flags = SdcApply | SdcUseSuppliedDisplayConfig | SdcAllowChanges | SdcSaveToDatabase;
                    return SetDisplayConfig(
                        (uint)nativePaths.Length,
                        pathsHandle.AddrOfPinnedObject(),
                        (uint)nativeModes.Length,
                        modesHandle.AddrOfPinnedObject(),
                        flags) == 0;
                }
                finally
                {
                    pathsHandle.Free();
                    modesHandle.Free();
                }
            }

            private static string? QuerySourceDeviceName(Luid adapterId, uint sourceId)
            {
                byte[] packet = WindowsDisplayTopologyManager.BuildSourceDeviceNameRequest(adapterId.LowPart, adapterId.HighPart, sourceId);
                if (DisplayConfigGetDeviceInfo(packet) != 0)
                    return null;

                return WindowsDisplayTopologyManager.ParseSourceDeviceNameResponse(packet);
            }

            [LibraryImport("user32.dll")]
            private static partial int DisplayConfigGetDeviceInfo([In, Out] byte[] requestPacket);

            [LibraryImport("user32.dll")]
            private static partial int GetDisplayConfigBufferSizes(
                uint flags,
                out uint numPathArrayElements,
                out uint numModeInfoArrayElements);

            [LibraryImport("user32.dll")]
            private static partial int QueryDisplayConfig(
                uint flags,
                ref uint numPathArrayElements,
                IntPtr pathArray,
                ref uint numModeInfoArrayElements,
                IntPtr modeInfoArray,
                IntPtr currentTopologyId);

            [LibraryImport("user32.dll")]
            private static partial int SetDisplayConfig(
                uint numPathArrayElements,
                IntPtr pathArray,
                uint numModeInfoArrayElements,
                IntPtr modeInfoArray,
                uint flags);

            [LibraryImport("user32.dll")]
            private static partial nint MonitorFromWindow(nint hwnd, uint dwFlags);

            [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool GetMonitorInfoW(nint hMonitor, [In, Out] byte[] lpmi);

            [LibraryImport("user32.dll", EntryPoint = "EnumDisplaySettingsW", StringMarshalling = StringMarshalling.Utf16)]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool EnumDisplaySettingsW(string lpszDeviceName, uint iModeNum, [In, Out] byte[] lpDevMode);

            public static bool TryGetWindowMonitorPhysicalModeCore(
                nint hwnd,
                out VirtualDisplayMode mode,
                out double dpiScale)
            {
                mode = default;
                dpiScale = 1.0;

                const uint monitorDefaultToNearest = 2;
                const uint monitorDefaultToPrimary = 1;
                nint hMonitor = hwnd != IntPtr.Zero
                    ? MonitorFromWindow(hwnd, monitorDefaultToNearest)
                    : MonitorFromWindow(IntPtr.Zero, monitorDefaultToPrimary);
                if (hMonitor == IntPtr.Zero)
                    return false;

                byte[] mi = new byte[104];
                BinaryPrimitives.WriteInt32LittleEndian(mi.AsSpan(0, 4), mi.Length);
                if (!GetMonitorInfoW(hMonitor, mi))
                    return false;

                string deviceName = VirtualDriversVddBackend.ReadUtf16NullTerminated(mi.AsSpan(40, 64));
                if (!string.IsNullOrWhiteSpace(deviceName) &&
                    TryReadDevModePhysicalResolution(deviceName, out mode, out dpiScale))
                {
                    return true;
                }

                int monWidth = BinaryPrimitives.ReadInt32LittleEndian(mi.AsSpan(12, 4)) -
                               BinaryPrimitives.ReadInt32LittleEndian(mi.AsSpan(4, 4));
                int monHeight = BinaryPrimitives.ReadInt32LittleEndian(mi.AsSpan(16, 4)) -
                                BinaryPrimitives.ReadInt32LittleEndian(mi.AsSpan(8, 4));
                if (monWidth > 0 && monHeight > 0)
                {
                    mode = new VirtualDisplayMode(monWidth, monHeight, 60);
                    return true;
                }

                return false;
            }

            private static bool TryReadDevModePhysicalResolution(
                string deviceName,
                out VirtualDisplayMode mode,
                out double dpiScale)
            {
                mode = default;
                dpiScale = 1.0;

                byte[] devMode = new byte[220];
                BinaryPrimitives.WriteUInt16LittleEndian(devMode.AsSpan(68, 2), (ushort)devMode.Length);
                const uint enumCurrentSettings = unchecked((uint)-1);
                if (!EnumDisplaySettingsW(deviceName, enumCurrentSettings, devMode))
                    return false;

                int logPixels = BinaryPrimitives.ReadUInt16LittleEndian(devMode.AsSpan(166, 2));
                if (logPixels >= 96)
                {
                    dpiScale = logPixels / 96.0;
                }

                int pelsW = (int)BinaryPrimitives.ReadUInt32LittleEndian(devMode.AsSpan(172, 4));
                int pelsH = (int)BinaryPrimitives.ReadUInt32LittleEndian(devMode.AsSpan(176, 4));
                int freq = (int)BinaryPrimitives.ReadUInt32LittleEndian(devMode.AsSpan(184, 4));
                if (pelsW <= 0 || pelsH <= 0)
                    return false;

                mode = new VirtualDisplayMode(pelsW, pelsH, freq > 0 ? freq : 60);
                return true;
            }
        }

        public static bool TryGetWindowMonitorPhysicalMode(
            nint hwnd,
            out VirtualDisplayMode mode,
            out double dpiScale)
        {
            if (OperatingSystem.IsWindows())
            {
                try
                {
                    return Win32CcdAdapter.TryGetWindowMonitorPhysicalModeCore(hwnd, out mode, out dpiScale);
                }
                catch
                {
                    // Fallback al proveedor multiplataforma
                }
            }

            mode = default;
            dpiScale = 1.0;
            return false;
        }

        internal sealed class NullCcdAdapter : ICcdPlatformAdapter
        {
            public bool TryQueryActiveDisplayPaths(out IReadOnlyList<DisplayPathSnapshot> paths)
            {
                paths = Array.Empty<DisplayPathSnapshot>();
                return false;
            }

            public bool ApplyExtendTopology() => false;
            public bool ApplySuppliedConfig(IReadOnlyList<DisplayPathSnapshot> paths) => false;
        }
    }
}
