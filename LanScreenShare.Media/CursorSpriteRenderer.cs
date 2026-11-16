using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace LanScreenShare.Media
{
    internal readonly record struct ExtractedCursorBuffers
    {
        public byte[] ColorBgra { get; init; }
        public byte[] XorMaskBgra { get; init; }
        public bool HasXorPixels { get; init; }
        public bool HasVisiblePixels { get; init; }
    }

    internal readonly record struct CursorRenderTarget
    {
        public int ScreenLeft { get; init; }
        public int ScreenTop { get; init; }
        public float ScaleX { get; init; }
        public float ScaleY { get; init; }
    }

    internal readonly record struct NativeCursorCapture(
        (int Width, int Height) Size,
        (int X, int Y) Hotspot,
        (byte[] OnBlack, byte[] OnWhite) DualPass);

    internal interface INativeCursorProvider
    {
        bool TryCaptureCursor(IntPtr cursorHandle, out NativeCursorCapture capture);
    }

    internal static class CursorPixelExtractor
    {
        private readonly record struct BgrPixel(byte B, byte G, byte R)
        {
            public static BgrPixel Read(ReadOnlySpan<byte> buffer, int offset) =>
                new(buffer[offset], buffer[offset + 1], buffer[offset + 2]);

            public bool Exceeds(in BgrPixel other)
            {
                if (R > other.R) return true;
                if (G > other.G) return true;
                return B > other.B;
            }
        }

        private ref struct DualPassBuffers
        {
            public required ReadOnlySpan<byte> OnBlack;
            public required ReadOnlySpan<byte> OnWhite;
            public required byte[] ColorOut;
            public required byte[] XorOut;
            public bool HasXor;
            public bool HasVisible;
        }

        private static ExtractedCursorBuffers EmptyResult() => new()
        {
            ColorBgra = [],
            XorMaskBgra = [],
            HasXorPixels = false,
            HasVisiblePixels = false
        };

        public static ExtractedCursorBuffers ExtractDualPassSprites(
            ReadOnlySpan<byte> onBlackBgra,
            ReadOnlySpan<byte> onWhiteBgra,
            (int Width, int Height) dimensions)
        {
            int pixelCount = Math.Max(0, dimensions.Width) * Math.Max(0, dimensions.Height);
            int expectedBytes = pixelCount * 4;
            if (expectedBytes == 0)
                return EmptyResult();
            if (onBlackBgra.Length < expectedBytes || onWhiteBgra.Length < expectedBytes)
                return EmptyResult();

            var buffers = new DualPassBuffers
            {
                OnBlack = onBlackBgra,
                OnWhite = onWhiteBgra,
                ColorOut = new byte[expectedBytes],
                XorOut = new byte[expectedBytes]
            };

            for (int i = 0; i < expectedBytes; i += 4)
            {
                ClassifyAndWritePixel(ref buffers, i);
            }

            return new ExtractedCursorBuffers
            {
                ColorBgra = buffers.ColorOut,
                XorMaskBgra = buffers.XorOut,
                HasXorPixels = buffers.HasXor,
                HasVisiblePixels = buffers.HasVisible
            };
        }

        private static void ClassifyAndWritePixel(ref DualPassBuffers buffers, int offset)
        {
            var onBlack = BgrPixel.Read(buffers.OnBlack, offset);
            var onWhite = BgrPixel.Read(buffers.OnWhite, offset);

            if (onBlack.Exceeds(onWhite))
            {
                WriteOpaqueWhite(buffers.XorOut, offset);
                buffers.HasXor = true;
                buffers.HasVisible = true;
                return;
            }

            int delta = ((onWhite.B - onBlack.B) + (onWhite.G - onBlack.G) + (onWhite.R - onBlack.R)) / 3;
            int alpha = Math.Clamp(255 - delta, 0, 255);
            if (alpha == 0)
                return;

            buffers.ColorOut[offset] = (byte)Math.Min(onBlack.B, alpha);
            buffers.ColorOut[offset + 1] = (byte)Math.Min(onBlack.G, alpha);
            buffers.ColorOut[offset + 2] = (byte)Math.Min(onBlack.R, alpha);
            buffers.ColorOut[offset + 3] = (byte)alpha;
            buffers.HasVisible = true;
        }

        private static void WriteOpaqueWhite(byte[] dest, int offset)
        {
            dest[offset] = 255;
            dest[offset + 1] = 255;
            dest[offset + 2] = 255;
            dest[offset + 3] = 255;
        }

        public static SKBitmap CreateBgraBitmap(byte[] bgraBytes, int width, int height)
        {
            var bitmap = new SKBitmap(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
            Marshal.Copy(bgraBytes, 0, bitmap.GetPixels(), Math.Min(bgraBytes.Length, bitmap.ByteCount));
            return bitmap;
        }

        public static SKImage CreateBgraImage(byte[] bgraBytes, int width, int height)
        {
            using var bitmap = CreateBgraBitmap(bgraBytes, width, height);
            return SKImage.FromBitmap(bitmap);
        }
    }

    internal sealed class CursorSpriteRenderer : IDisposable
    {
        private const int DefaultMaxCacheEntries = 64;

        private sealed record CachedCursorSprite : IDisposable
        {
            public int Width { get; init; }
            public int Height { get; init; }
            public int HotspotX { get; init; }
            public int HotspotY { get; init; }
            public required SKImage ColorImage { get; init; }
            public SKImage? XorImage { get; init; }

            public void Dispose()
            {
                ColorImage.Dispose();
                XorImage?.Dispose();
            }
        }

        private readonly Dictionary<IntPtr, CachedCursorSprite> _cache = new();
        private readonly INativeCursorProvider? _nativeProvider;
        private readonly int _maxCacheEntries;

        public int CachedCount => _cache.Count;

        public CursorSpriteRenderer(
            INativeCursorProvider? nativeProvider = null,
            int maxCacheEntries = DefaultMaxCacheEntries)
        {
            _nativeProvider = nativeProvider;
            _maxCacheEntries = Math.Max(1, maxCacheEntries);
        }

        public void DrawCursor(
            SKBitmap targetBitmap,
            in CursorSample sample,
            in CursorRenderTarget target)
        {
            if (!sample.IsVisible)
                return;

            var hotspotPos = new SKPoint(
                (sample.X - target.ScreenLeft) * target.ScaleX,
                (sample.Y - target.ScreenTop) * target.ScaleY);
            if (!IsNearBitmap(hotspotPos, targetBitmap.Info))
                return;

            var sprite = GetOrCreateSprite(sample.Handle);
            if (sprite != null)
            {
                DrawSprite(targetBitmap, sprite, ComputeSpriteDestRect(sprite, sample, target));
                return;
            }

            if (IsInsideBitmap(hotspotPos, targetBitmap.Info))
            {
                DrawFallbackArrow(targetBitmap, hotspotPos);
            }
        }

        private static SKRect ComputeSpriteDestRect(
            CachedCursorSprite sprite,
            in CursorSample sample,
            in CursorRenderTarget target)
        {
            float left = (sample.X - target.ScreenLeft - sprite.HotspotX) * target.ScaleX;
            float top = (sample.Y - target.ScreenTop - sprite.HotspotY) * target.ScaleY;
            return SKRect.Create(left, top, sprite.Width * target.ScaleX, sprite.Height * target.ScaleY);
        }

        private static bool IsNearBitmap(SKPoint point, SKImageInfo info)
        {
            const float margin = 128f;
            bool nearX = point.X >= -margin && point.X < info.Width + margin;
            bool nearY = point.Y >= -margin && point.Y < info.Height + margin;
            return nearX && nearY;
        }

        private static bool IsInsideBitmap(SKPoint point, SKImageInfo info) =>
            point.X >= 0 && point.X < info.Width && point.Y >= 0 && point.Y < info.Height;

        private CachedCursorSprite? GetOrCreateSprite(IntPtr hCursor)
        {
            if (hCursor == IntPtr.Zero || _nativeProvider == null)
                return null;

            if (_cache.TryGetValue(hCursor, out var cached))
                return cached;

            if (!_nativeProvider.TryCaptureCursor(hCursor, out var capture))
                return null;

            var sprite = BuildSpriteFromCapture(capture);
            if (sprite == null)
                return null;

            if (_cache.Count >= _maxCacheEntries)
            {
                ClearCache();
            }

            _cache[hCursor] = sprite;
            return sprite;
        }

        private static CachedCursorSprite? BuildSpriteFromCapture(in NativeCursorCapture capture)
        {
            var (width, height) = capture.Size;
            var extracted = CursorPixelExtractor.ExtractDualPassSprites(
                capture.DualPass.OnBlack,
                capture.DualPass.OnWhite,
                capture.Size);
            if (!extracted.HasVisiblePixels)
                return null;

            var colorImage = CursorPixelExtractor.CreateBgraImage(extracted.ColorBgra, width, height);
            SKImage? xorImage = extracted.HasXorPixels
                ? CursorPixelExtractor.CreateBgraImage(extracted.XorMaskBgra, width, height)
                : null;

            return new CachedCursorSprite
            {
                Width = width,
                Height = height,
                HotspotX = capture.Hotspot.X,
                HotspotY = capture.Hotspot.Y,
                ColorImage = colorImage,
                XorImage = xorImage
            };
        }

        private static void DrawSprite(SKBitmap targetBitmap, CachedCursorSprite sprite, SKRect destRect)
        {
            if (!destRect.IntersectsWith(SKRect.Create(0, 0, targetBitmap.Width, targetBitmap.Height)))
                return;

            using var canvas = new SKCanvas(targetBitmap);
            var linearSampling = new SKSamplingOptions(SKFilterMode.Linear);
            canvas.DrawImage(sprite.ColorImage, destRect, linearSampling);

            if (sprite.XorImage != null)
            {
                using var xorPaint = new SKPaint { BlendMode = SKBlendMode.Difference, IsAntialias = false };
                canvas.DrawImage(sprite.XorImage, destRect, SKSamplingOptions.Default, xorPaint);
            }
        }

        private static void DrawFallbackArrow(SKBitmap bitmap, SKPoint origin)
        {
            float x = origin.X;
            float y = origin.Y;
            using var canvas = new SKCanvas(bitmap);
            using var fillPaint = new SKPaint { Color = SKColors.White, Style = SKPaintStyle.Fill, IsAntialias = true };
            using var strokePaint = new SKPaint { Color = SKColors.Black, Style = SKPaintStyle.Stroke, StrokeWidth = 1.5f, IsAntialias = true };

            using var builder = new SKPathBuilder();
            builder.MoveTo(x, y);
            builder.LineTo(x, y + 17);
            builder.LineTo(x + 4.5f, y + 13);
            builder.LineTo(x + 8.5f, y + 21);
            builder.LineTo(x + 11.5f, y + 19.5f);
            builder.LineTo(x + 7.5f, y + 11.5f);
            builder.LineTo(x + 13, y + 11.5f);
            builder.Close();

            using var path = builder.Detach();
            canvas.DrawPath(path, fillPaint);
            canvas.DrawPath(path, strokePaint);
        }

        public void ClearCache()
        {
            foreach (var sprite in _cache.Values)
            {
                sprite.Dispose();
            }
            _cache.Clear();
        }

        public void Dispose() => ClearCache();
    }
}
