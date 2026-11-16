using System;
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

            if (!_adapter.TryQueryActiveDisplayPaths(out var activePaths) || activePaths.Count == 0)
                return true;

            var updated = ApplyModeToTargetPath(activePaths, preferredDeviceName, mode);
            if (updated == null)
                return true;

            return _adapter.ApplySuppliedConfig(updated);
        }

        public bool RestoreSnapshot(DisplayTopologySnapshot snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (snapshot.Paths.Count == 0)
                return true;

            return _adapter.ApplySuppliedConfig(snapshot.Paths);
        }

        internal static IReadOnlyList<DisplayPathSnapshot>? ApplyModeToTargetPath(
            IReadOnlyList<DisplayPathSnapshot> activePaths,
            string? preferredDeviceName,
            VirtualDisplayMode mode)
        {
            if (activePaths.Count == 0)
                return null;

            int targetIdx = FindTargetPathIndex(activePaths, preferredDeviceName);
            var target = activePaths[targetIdx];
            if (target.Width == mode.Width && target.Height == mode.Height)
                return null;

            var copy = activePaths.ToList();
            copy[targetIdx] = target with
            {
                Width = mode.Width,
                Height = mode.Height,
                RefreshRateNumerator = (uint)mode.RefreshRateHz,
                RefreshRateDenominator = 1
            };
            return copy;
        }

        private static int FindTargetPathIndex(IReadOnlyList<DisplayPathSnapshot> paths, string? preferredDeviceName)
        {
            if (!string.IsNullOrWhiteSpace(preferredDeviceName))
            {
                for (int i = 0; i < paths.Count; i++)
                {
                    if (string.Equals(paths[i].DeviceName, preferredDeviceName, StringComparison.OrdinalIgnoreCase))
                        return i;
                }
            }

            return paths.Count - 1;
        }

        [SupportedOSPlatform("windows")]
        private sealed partial class Win32CcdAdapter : ICcdPlatformAdapter
        {
            private const uint QdcOnlyActivePaths = 0x00000002;
            private const uint SdcApply = 0x00000080;
            private const uint SdcTopologyExtend = 0x00000004;
            private const uint SdcUseSuppliedDisplayConfig = 0x00000020;
            private const uint SdcAllowChanges = 0x00000400;
            private const uint SdcSaveToDatabase = 0x00000200;
            private const uint DisplayConfigModeInfoTypeSource = 1;
            private const uint DisplayConfigModeInfoTypeTarget = 2;
            private const uint DisplayConfigPathActive = 0x00000001;

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
                    result.Add(ToSnapshot(pathInfos[i], modeInfos));
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

                var nativePaths = new DisplayConfigPathInfo[paths.Count];
                var nativeModes = new DisplayConfigModeInfo[paths.Count * 2];

                for (int i = 0; i < paths.Count; i++)
                {
                    PopulateNativePathAndModes(paths[i], i, nativePaths, nativeModes);
                }

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

            private static void PopulateNativePathAndModes(
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
                var mode = new DisplayConfigModeInfo { InfoType = DisplayConfigModeInfoTypeTarget, Id = snap.TargetId, AdapterId = luid };
                mode.TargetActiveWidth = (uint)Math.Max(1, snap.Width);
                mode.TargetActiveHeight = (uint)Math.Max(1, snap.Height);
                mode.TargetVSyncNumerator = num;
                mode.TargetVSyncDenominator = den;
                return mode;
            }

            private static DisplayPathSnapshot ToSnapshot(in DisplayConfigPathInfo path, DisplayConfigModeInfo[] modes)
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
            private struct Luid { public uint LowPart; public int HighPart; }

            [StructLayout(LayoutKind.Sequential)]
            private struct DisplayConfigPathSourceInfo
            {
                public Luid AdapterId;
                public uint Id;
                public uint ModeInfoIdx;
                public uint StatusFlags;
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct DisplayConfigPathTargetInfo
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
            private struct DisplayConfigPathInfo
            {
                public DisplayConfigPathSourceInfo SourceInfo;
                public DisplayConfigPathTargetInfo TargetInfo;
                public uint Flags;
            }

            [StructLayout(LayoutKind.Explicit, Size = 64)]
            private struct DisplayConfigModeInfo
            {
                [FieldOffset(0)] public uint InfoType;
                [FieldOffset(4)] public uint Id;
                [FieldOffset(8)] public Luid AdapterId;

                [FieldOffset(16)] public uint SourceWidth;
                [FieldOffset(20)] public uint SourceHeight;
                [FieldOffset(24)] public uint SourcePixelFormat;
                [FieldOffset(28)] public int PositionX;
                [FieldOffset(32)] public int PositionY;

                [FieldOffset(32)] public uint TargetVSyncNumerator;
                [FieldOffset(36)] public uint TargetVSyncDenominator;
                [FieldOffset(40)] public uint TargetActiveWidth;
                [FieldOffset(44)] public uint TargetActiveHeight;
            }

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
        }

        private sealed class NullCcdAdapter : ICcdPlatformAdapter
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
