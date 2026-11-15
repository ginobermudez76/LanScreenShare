using System;
using System.Collections.Generic;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using LanScreenShare.Core.Models;

namespace LanScreenShare.Desktop
{
    internal static class RemoteInputHelper
    {
        internal readonly record struct NormalizedCoordinateInput(Point Position, Size ImageSize, Rect Bounds, Stretch Stretch);
        private readonly record struct ImageGeometry(Size ImageSize, Rect Bounds);

        internal static bool HasPositiveDimensions(Size s) => s.Width > 0 && s.Height > 0;
        internal static bool HasPositiveDimensions(Rect r) => r.Width > 0 && r.Height > 0;

        internal static bool TryGetNormalizedCoordinates(Image? screenImage, PointerEventArgs e, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;

            if (!TryGetValidImageGeometry(screenImage, out var geom))
                return false;

            var input = new NormalizedCoordinateInput(e.GetPosition(screenImage), geom.ImageSize, geom.Bounds, screenImage!.Stretch);
            return TryGetNormalizedCoordinates(input, out normX, out normY);
        }

        internal static bool TryGetNormalizedCoordinates(in NormalizedCoordinateInput input, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;

            if (!HasPositiveDimensions(input.ImageSize) || !HasPositiveDimensions(input.Bounds))
                return false;

            if (input.Stretch == Stretch.Fill)
            {
                normX = (float)Math.Clamp(input.Position.X / input.Bounds.Width, 0.0, 1.0);
                normY = (float)Math.Clamp(input.Position.Y / input.Bounds.Height, 0.0, 1.0);
                return true;
            }

            var geom = new ImageGeometry(input.ImageSize, input.Bounds);
            return TryCalculateUniformCoordinates(input.Position, geom, out normX, out normY);
        }

        private static bool TryGetValidImageGeometry(Image? image, out ImageGeometry geom)
        {
            geom = default;
            if (image?.Source == null) return false;
            var size = image.Source.Size;
            var bounds = image.Bounds;
            if (!HasPositiveDimensions(size) || !HasPositiveDimensions(bounds))
                return false;

            geom = new ImageGeometry(size, bounds);
            return true;
        }

        private static bool TryCalculateUniformCoordinates(Point pos, in ImageGeometry geom, out float normX, out float normY)
        {
            normX = 0f;
            normY = 0f;
            double scale = Math.Min(geom.Bounds.Width / geom.ImageSize.Width, geom.Bounds.Height / geom.ImageSize.Height);
            double renderedW = geom.ImageSize.Width * scale;
            double renderedH = geom.ImageSize.Height * scale;
            double offsetX = (geom.Bounds.Width - renderedW) / 2.0;
            double offsetY = (geom.Bounds.Height - renderedH) / 2.0;

            if (IsOutsideBounds(pos.X, offsetX, renderedW) || IsOutsideBounds(pos.Y, offsetY, renderedH))
                return false;

            normX = (float)Math.Clamp((pos.X - offsetX) / renderedW, 0.0, 1.0);
            normY = (float)Math.Clamp((pos.Y - offsetY) / renderedH, 0.0, 1.0);
            return true;
        }

        private static bool IsOutsideBounds(double pos, double offset, double size) =>
            pos < offset || pos > offset + size;

        internal static RemoteMouseButton GetPressedMouseButton(PointerPointProperties props)
        {
            if (props.IsLeftButtonPressed) return RemoteMouseButton.Left;
            if (props.IsRightButtonPressed) return RemoteMouseButton.Right;
            if (props.IsMiddleButtonPressed) return RemoteMouseButton.Middle;
            return RemoteMouseButton.None;
        }

        internal static RemoteMouseButton GetReleasedMouseButton(PointerPointProperties props) =>
            props.PointerUpdateKind switch
            {
                PointerUpdateKind.LeftButtonReleased => RemoteMouseButton.Left,
                PointerUpdateKind.RightButtonReleased => RemoteMouseButton.Right,
                PointerUpdateKind.MiddleButtonReleased => RemoteMouseButton.Middle,
                _ => RemoteMouseButton.Left
            };

        private readonly record struct KeyRange(Key Start, Key End, int BaseVk)
        {
            public bool TryConvert(Key key, out int vk)
            {
                if (key >= Start && key <= End)
                {
                    vk = (int)key - (int)Start + BaseVk;
                    return true;
                }
                vk = 0;
                return false;
            }
        }

        private static readonly KeyRange[] SupportedKeyRanges =
        [
            new(Key.A, Key.Z, 0x41),
            new(Key.D0, Key.D9, 0x30),
            new(Key.NumPad0, Key.NumPad9, 0x60),
            new(Key.F1, Key.F12, 0x70)
        ];

        private static readonly Dictionary<Key, int> SpecialKeyMap = new()
        {
            [Key.Back] = 0x08,
            [Key.Tab] = 0x09,
            [Key.Return] = 0x0D,
            [Key.Escape] = 0x1B,
            [Key.Space] = 0x20,
            [Key.Left] = 0x25,
            [Key.Up] = 0x26,
            [Key.Right] = 0x27,
            [Key.Down] = 0x28,
            [Key.Delete] = 0x2E,
            [Key.LeftShift] = 0x10,
            [Key.RightShift] = 0x10,
            [Key.LeftCtrl] = 0x11,
            [Key.RightCtrl] = 0x11,
            [Key.LeftAlt] = 0x12,
            [Key.RightAlt] = 0x12,
            [Key.Home] = 0x24,
            [Key.End] = 0x23,
            [Key.PageUp] = 0x21,
            [Key.PageDown] = 0x22
        };

        internal static int ConvertAvaloniaKeyToWin32Vk(Key key)
        {
            foreach (var range in SupportedKeyRanges)
            {
                if (range.TryConvert(key, out int vk))
                    return vk;
            }

            return SpecialKeyMap.TryGetValue(key, out int mappedVk) ? mappedVk : 0;
        }
    }
}
