using System;
using System.Collections.Generic;

namespace LanScreenShare.Media
{
    internal readonly record struct DxgiOutputRect
    {
        public int Left { get; init; }
        public int Top { get; init; }
        public int Width { get; init; }
        public int Height { get; init; }

        public bool IsValid => Width > 0 && Height > 0;

        public bool Contains(int x, int y)
        {
            if (!IsValid) return false;
            bool insideX = x >= Left && x < Left + Width;
            bool insideY = y >= Top && y < Top + Height;
            return insideX && insideY;
        }
    }

    internal readonly record struct DxgiOutputDescriptor
    {
        public int AdapterIndex { get; init; }
        public int OutputIndex { get; init; }
        public string DeviceName { get; init; }
        public DxgiOutputRect Bounds { get; init; }
    }

    internal readonly record struct DxgiOutputTarget(
        string? DeviceName,
        int MonitorIndex,
        DxgiOutputRect Bounds);

    internal static class DxgiOutputSelector
    {
        public static int SelectBestIndex(
            IReadOnlyList<DxgiOutputDescriptor> candidates,
            in DxgiOutputTarget target)
        {
            if (candidates.Count == 0)
                return -1;

            int byDeviceName = FindByDeviceName(candidates, target.DeviceName);
            if (byDeviceName >= 0)
                return byDeviceName;

            int byBounds = FindByBounds(candidates, target.Bounds);
            if (byBounds >= 0)
                return byBounds;

            if (target.MonitorIndex >= 0 && target.MonitorIndex < candidates.Count)
                return target.MonitorIndex;

            return 0;
        }

        private static int FindByDeviceName(IReadOnlyList<DxgiOutputDescriptor> candidates, string? targetDeviceName)
        {
            if (string.IsNullOrEmpty(targetDeviceName))
                return -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (string.Equals(candidates[i].DeviceName, targetDeviceName, StringComparison.OrdinalIgnoreCase))
                    return i;
            }

            return -1;
        }

        private static int FindByBounds(IReadOnlyList<DxgiOutputDescriptor> candidates, DxgiOutputRect targetBounds)
        {
            if (!targetBounds.IsValid)
                return -1;

            for (int i = 0; i < candidates.Count; i++)
            {
                if (candidates[i].Bounds == targetBounds)
                    return i;
            }

            return -1;
        }
    }

    internal readonly record struct CursorSample
    {
        public bool IsVisible { get; init; }
        public int X { get; init; }
        public int Y { get; init; }
        public IntPtr Handle { get; init; }
    }

    internal struct CursorTrackingState
    {
        public int LastX;
        public int LastY;
        public IntPtr LastHandle;
        public bool WasInside;

        public static CursorTrackingState Initial => new()
        {
            LastX = int.MinValue,
            LastY = int.MinValue,
            LastHandle = IntPtr.Zero,
            WasInside = false
        };
    }

    internal static class DxgiCursorTracker
    {
        public static bool ShouldRedrawCursor(
            in CursorSample sample,
            in DxgiOutputRect bounds,
            ref CursorTrackingState state)
        {
            if (!sample.IsVisible)
            {
                return HandleHiddenCursor(ref state);
            }

            bool isInside = bounds.Contains(sample.X, sample.Y);
            if (!isInside)
            {
                return HandleCursorOutsideBounds(sample, ref state);
            }

            bool changed = !state.WasInside || HasSampleChanged(sample, state);
            UpdateState(sample, isInside: true, ref state);
            return changed;
        }

        private static bool HandleHiddenCursor(ref CursorTrackingState state)
        {
            if (!state.WasInside)
                return false;

            state.WasInside = false;
            state.LastHandle = IntPtr.Zero;
            return true;
        }

        private static bool HandleCursorOutsideBounds(in CursorSample sample, ref CursorTrackingState state)
        {
            bool justExited = state.WasInside;
            UpdateState(sample, isInside: false, ref state);
            return justExited;
        }

        private static bool HasSampleChanged(in CursorSample sample, in CursorTrackingState state) =>
            sample.X != state.LastX || sample.Y != state.LastY || sample.Handle != state.LastHandle;

        private static void UpdateState(in CursorSample sample, bool isInside, ref CursorTrackingState state)
        {
            state.WasInside = isInside;
            state.LastX = sample.X;
            state.LastY = sample.Y;
            state.LastHandle = sample.Handle;
        }
    }
}
