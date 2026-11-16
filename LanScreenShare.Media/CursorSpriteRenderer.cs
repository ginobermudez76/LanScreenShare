using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
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

    internal static class CursorPixelExtractor
    {
        private readonly record struct BgrPixel(byte B, byte G, byte R)
        {
            public static BgrPixel Read(ReadOnlySpan<byte> buffer, int offset) =>
                new(buffer[offset], buffer[offset + 1], buffer[offset + 2]);

            public bool Exceeds(in BgrPixel other) =>
                R > other.R || G > other.G || B > other.B;
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

        public static ExtractedCursorBuffers ExtractDualPassSprites(
            ReadOnlySpan<byte> onBlackBgra,
            ReadOnlySpan<byte> onWhiteBgra,
            (int Width, int Height) dimensions)
        {
            int pixelCount = Math.Max(0, dimensions.Width) * Math.Max(0, dimensions.Height);
            int expectedBytes = pixelCount * 4;
            if (expectedBytes == 0 || onBlackBgra.Length < expectedBytes || onWhiteBgra.Length < expectedBytes)
            {
                return new ExtractedCursorBuffers
                {
                    ColorBgra = [],
                    XorMaskBgra = [],
                    HasXorPixels = false,
                    HasVisiblePixels = false
                };
            }

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

    [SupportedOSPlatform("windows")]
    internal sealed partial class CursorSpriteRenderer : IDisposable
    {
        private const int MaxCachedSprites = 64;
        private const int DI_NORMAL = 0x0003;
        private const int SM_CXCURSOR = 13;
        private const int SM_CYCURSOR = 14;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
        private const uint SPI_GETMOUSETRAILS = 0x005E;

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

        public static bool AreMouseTrailsActive()
        {
            uint trails = 0;
            return SystemParametersInfo(SPI_GETMOUSETRAILS, 0, ref trails, 0) && trails > 1;
        }

        public void DrawCursor(
            SKBitmap targetBitmap,
            in CursorSample sample,
            in CursorRenderTarget target)
        {
            if (!sample.IsVisible)
                return;

            float hotspotX = (sample.X - target.ScreenLeft) * target.ScaleX;
            float hotspotY = (sample.Y - target.ScreenTop) * target.ScaleY;
            if (!IsNearBitmap(hotspotX, hotspotY, targetBitmap.Width, targetBitmap.Height))
                return;

            var sprite = GetOrCreateSprite(sample.Handle);
            if (sprite != null)
            {
                DrawSprite(targetBitmap, sprite, ComputeSpriteDestRect(sprite, sample, target));
                return;
            }

            if (IsInsideBitmap(hotspotX, hotspotY, targetBitmap.Width, targetBitmap.Height))
            {
                DrawFallbackArrow(targetBitmap, hotspotX, hotspotY);
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

        private static bool IsNearBitmap(float x, float y, int width, int height)
        {
            const float margin = 128f;
            bool nearX = x >= -margin && x < width + margin;
            bool nearY = y >= -margin && y < height + margin;
            return nearX && nearY;
        }

        private static bool IsInsideBitmap(float x, float y, int width, int height) =>
            x >= 0 && x < width && y >= 0 && y < height;

        private CachedCursorSprite? GetOrCreateSprite(IntPtr hCursor)
        {
            if (hCursor == IntPtr.Zero)
                return null;

            if (_cache.TryGetValue(hCursor, out var cached))
                return cached;

            var extracted = TryExtractNativeCursorSprite(hCursor);
            if (extracted == null)
                return null;

            if (_cache.Count >= MaxCachedSprites)
            {
                ClearCache();
            }

            _cache[hCursor] = extracted;
            return extracted;
        }

        private static CachedCursorSprite? TryExtractNativeCursorSprite(IntPtr hCursor)
        {
            if (!GetIconInfo(hCursor, out var iconInfo))
                return null;

            try
            {
                var (width, height) = ResolveCursorDimensions(iconInfo);
                var (onBlack, onWhite) = RenderCursorDualPass(hCursor, width, height);
                if (onBlack == null || onWhite == null)
                    return null;

                var extracted = CursorPixelExtractor.ExtractDualPassSprites(onBlack, onWhite, (width, height));
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
                    HotspotX = iconInfo.xHotspot,
                    HotspotY = iconInfo.yHotspot,
                    ColorImage = colorImage,
                    XorImage = xorImage
                };
            }
            catch
            {
                return null;
            }
            finally
            {
                if (iconInfo.hbmMask != IntPtr.Zero) DeleteObject(iconInfo.hbmMask);
                if (iconInfo.hbmColor != IntPtr.Zero) DeleteObject(iconInfo.hbmColor);
            }
        }

        private static (int Width, int Height) ResolveCursorDimensions(in IconInfo iconInfo)
        {
            IntPtr hBitmap = iconInfo.hbmColor != IntPtr.Zero ? iconInfo.hbmColor : iconInfo.hbmMask;
            if (hBitmap != IntPtr.Zero && TryGetBitmapSize(hBitmap, out int bmpW, out int bmpH))
            {
                int height = iconInfo.hbmColor != IntPtr.Zero ? bmpH : bmpH / 2;
                if (bmpW > 0 && height > 0)
                    return (Math.Min(bmpW, 256), Math.Min(height, 256));
            }

            int sysW = Math.Clamp(GetSystemMetrics(SM_CXCURSOR), 16, 128);
            int sysH = Math.Clamp(GetSystemMetrics(SM_CYCURSOR), 16, 128);
            return (sysW, sysH);
        }

        private static bool TryGetBitmapSize(IntPtr hBitmap, out int width, out int height)
        {
            width = 0;
            height = 0;
            var bmp = new GdiBitmap();
            int size = Marshal.SizeOf<GdiBitmap>();
            if (GetObjectW(hBitmap, size, ref bmp) == 0)
                return false;

            width = bmp.bmWidth;
            height = Math.Abs(bmp.bmHeight);
            return width > 0 && height > 0;
        }

        private static (byte[]? OnBlack, byte[]? OnWhite) RenderCursorDualPass(IntPtr hCursor, int width, int height)
        {
            IntPtr hdcMem = CreateCompatibleDC(IntPtr.Zero);
            if (hdcMem == IntPtr.Zero)
                return (null, null);

            IntPtr hDib = IntPtr.Zero;
            IntPtr hOld = IntPtr.Zero;
            try
            {
                hDib = CreateTopDownDib32(hdcMem, width, height, out IntPtr pBits);
                if (hDib == IntPtr.Zero || pBits == IntPtr.Zero)
                    return (null, null);

                hOld = SelectObject(hdcMem, hDib);
                var passCtx = new DualPassContext
                {
                    Hdc = hdcMem,
                    BitsPtr = pBits,
                    CursorHandle = hCursor,
                    Width = width,
                    Height = height
                };
                byte[] onBlack = CapturePass(passCtx, fillWhite: false);
                byte[] onWhite = CapturePass(passCtx, fillWhite: true);
                return (onBlack, onWhite);
            }
            finally
            {
                if (hOld != IntPtr.Zero) SelectObject(hdcMem, hOld);
                if (hDib != IntPtr.Zero) DeleteObject(hDib);
                DeleteDC(hdcMem);
            }
        }

        private readonly record struct DualPassContext
        {
            public IntPtr Hdc { get; init; }
            public IntPtr BitsPtr { get; init; }
            public IntPtr CursorHandle { get; init; }
            public int Width { get; init; }
            public int Height { get; init; }
            public int ByteCount => Width * Height * 4;
        }

        private static byte[] CapturePass(in DualPassContext ctx, bool fillWhite)
        {
            int byteCount = ctx.ByteCount;
            byte fill = fillWhite ? (byte)0xFF : (byte)0x00;
            byte[] buffer = new byte[byteCount];
            Array.Fill(buffer, fill);
            if (!fillWhite)
            {
                for (int i = 3; i < byteCount; i += 4)
                    buffer[i] = 0xFF;
            }

            Marshal.Copy(buffer, 0, ctx.BitsPtr, byteCount);
            DrawIconEx(ctx.Hdc, 0, 0, ctx.CursorHandle, ctx.Width, ctx.Height, 0, IntPtr.Zero, DI_NORMAL);
            Marshal.Copy(ctx.BitsPtr, buffer, 0, byteCount);
            return buffer;
        }

        private static IntPtr CreateTopDownDib32(IntPtr hdc, int width, int height, out IntPtr pBits)
        {
            var bmi = new BitmapInfo
            {
                bmiHeader = new BitmapInfoHeader
                {
                    biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
                    biWidth = width,
                    biHeight = -height,
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = BI_RGB
                }
            };
            return CreateDIBSection(hdc, ref bmi, DIB_RGB_COLORS, out pBits, IntPtr.Zero, 0);
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

        private static void DrawFallbackArrow(SKBitmap bitmap, float x, float y)
        {
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

        #region Win32 GDI & Cursor P/Invoke

        [StructLayout(LayoutKind.Sequential)]
        private struct IconInfo
        {
            public int fIcon;
            public int xHotspot;
            public int yHotspot;
            public IntPtr hbmMask;
            public IntPtr hbmColor;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct GdiBitmap
        {
            public int bmType;
            public int bmWidth;
            public int bmHeight;
            public int bmWidthBytes;
            public ushort bmPlanes;
            public ushort bmBitsPixel;
            public IntPtr bmBits;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfoHeader
        {
            public uint biSize;
            public int biWidth;
            public int biHeight;
            public ushort biPlanes;
            public ushort biBitCount;
            public uint biCompression;
            public uint biSizeImage;
            public int biXPelsPerMeter;
            public int biYPelsPerMeter;
            public uint biClrUsed;
            public uint biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BitmapInfo
        {
            public BitmapInfoHeader bmiHeader;
            public uint bmiColors;
        }

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetIconInfo(IntPtr hIcon, out IconInfo piconinfo);

        [LibraryImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DrawIconEx(
            IntPtr hdc, int xLeft, int yTop, IntPtr hIcon,
            int cxWidth, int cyWidth, uint istepIfAniCur,
            IntPtr hbrFlickerFreeDraw, uint diFlags);

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

        [LibraryImport("gdi32.dll")]
        private static partial IntPtr CreateCompatibleDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteDC(IntPtr hdc);

        [LibraryImport("gdi32.dll")]
        private static partial IntPtr SelectObject(IntPtr hdc, IntPtr hgdiobj);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DeleteObject(IntPtr hObject);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial int GetObjectW(IntPtr hgdiobj, int cbBuffer, ref GdiBitmap lpvObject);

        [LibraryImport("gdi32.dll", SetLastError = true)]
        private static partial IntPtr CreateDIBSection(
            IntPtr hdc, ref BitmapInfo pbmi, uint usage,
            out IntPtr ppvBits, IntPtr hSection, uint offset);

        #endregion
    }
}
