using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;

namespace LanScreenShare.Media
{
    [SupportedOSPlatform("windows")]
    public sealed partial class WindowCaptureService : IScreenCaptureService
    {
        private readonly IntPtr _hwnd;
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        private volatile int _targetFps = 30;
        private volatile int _quality = 75;
        private volatile int _scalePercent = 100;
        private volatile int _targetWidth = 0;
        private volatile int _targetHeight = 0;
        private volatile string _codec = "jpeg";
        private volatile bool _forceKeyframeRequested = true;
        private byte[]? _reusableBgraBytes;
        private MediaFoundationH264EncoderService? _h264Encoder;

        public void RequestKeyframe()
        {
            _forceKeyframeRequested = true;
        }

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;

        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;
        public string? ActiveEncoderDescription => _codec == "h264" ? _h264Encoder?.EncoderDescription : "JPEG";
        public bool IsHardwareAccelerated => _codec == "h264" && (_h264Encoder?.IsHardwareAccelerated ?? false);

        public IntPtr WindowHandle => _hwnd;

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = Math.Clamp(value, 1, 120);
                _h264Encoder?.UpdateFps(_targetFps);
            }
        }

        public int Quality
        {
            get => _quality;
            set => _quality = Math.Clamp(value, 10, 100);
        }

        private volatile int _bitrateMbps = 8;
        public int BitrateMbps
        {
            get => _bitrateMbps;
            set
            {
                _bitrateMbps = Math.Clamp(value, 1, 50);
                _h264Encoder?.UpdateBitrate(_bitrateMbps * 1_000_000);
            }
        }

        public double ResolutionScale
        {
            get => _scalePercent / 100.0;
            set => _scalePercent = (int)Math.Round(Math.Clamp(value, 0.25, 1.0) * 100);
        }

        public int TargetWidth
        {
            get => _targetWidth;
            set => _targetWidth = Math.Max(0, value);
        }

        public int TargetHeight
        {
            get => _targetHeight;
            set => _targetHeight = Math.Max(0, value);
        }

        public string Codec
        {
            get => _codec;
            set => _codec = string.Equals(value, "h264", StringComparison.OrdinalIgnoreCase) ? "h264" : "jpeg";
        }

        public WindowCaptureService(IntPtr hwnd)
        {
            _hwnd = hwnd;
        }

        public void UpdateSettings(CaptureProfile profile)
        {
            TargetFps = profile.TargetFps;
            Quality = profile.Quality;
            BitrateMbps = profile.BitrateMbps;
            ResolutionScale = profile.ResolutionScale;
            Codec = profile.Codec;
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;
        }

        public void StartCapture(CaptureProfile profile)
        {
            if (IsCapturing)
                return;

            _targetFps = Math.Clamp(profile.TargetFps, 1, 120);
            _quality = Math.Clamp(profile.Quality, 10, 100);
            _bitrateMbps = Math.Clamp(profile.BitrateMbps, 1, 50);
            ResolutionScale = profile.ResolutionScale;
            Codec = profile.Codec;
            TargetWidth = profile.TargetWidth;
            TargetHeight = profile.TargetHeight;

            _cts = new CancellationTokenSource();
            var token = _cts.Token;

            _captureTask = Task.Run(() => CaptureLoop(token), token);
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            try
            {
                _captureTask?.Wait(800);
            }
            catch
            {
                // Ignorar excepciones al esperar cancelaciÃ³n
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
            SecureDesktopChanged?.Invoke(this, false);
        }

        private sealed class WindowFpsTracker
        {
            private readonly Stopwatch _fpsStopwatch = Stopwatch.StartNew();
            private int _capturedFramesCount;
            private int _currentHostFps;

            public void UpdateMetrics(CapturedFrame frame, int frameTimeMs)
            {
                _capturedFramesCount++;
                if (_fpsStopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentHostFps = (int)Math.Round(_capturedFramesCount * 1000.0 / _fpsStopwatch.ElapsedMilliseconds);
                    _capturedFramesCount = 0;
                    _fpsStopwatch.Restart();
                }

                frame.HostFps = _currentHostFps > 0 ? _currentHostFps : _capturedFramesCount;
                frame.HostFrameTimeMs = frameTimeMs;
            }
        }

        private void CaptureLoop(CancellationToken token)
        {
            try
            {
                Thread.CurrentThread.Priority = ThreadPriority.Highest;
            }
            catch
            {
                // Ignore if thread priority elevation is not permitted
            }

            var stopwatch = new Stopwatch();
            var tracker = new WindowFpsTracker();

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();

                if (!IsWindow(_hwnd))
                {
                    Console.WriteLine("[WindowCapture] La ventana objetivo fue cerrada.");
                    break;
                }

                if (IsIconic(_hwnd))
                {
                    Thread.Sleep(100);
                    continue;
                }

                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, _targetFps));
                try
                {
                    CaptureAndDispatchSingleFrame(tracker);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[WindowCapture] Error capturando ventana: {ex.Message}");
                }

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int delay = frameIntervalMs - elapsed;
                if (delay > 0)
                {
                    Thread.Sleep(delay);
                }
            }
        }

        private void CaptureAndDispatchSingleFrame(WindowFpsTracker tracker)
        {
            var procStopwatch = Stopwatch.StartNew();
            bool forceKeyframe = _forceKeyframeRequested;
            _forceKeyframeRequested = false;

            var frame = CaptureWindowFrame(_hwnd, new WindowCaptureOptions(_quality, ResolutionScale, _targetWidth, _targetHeight, forceKeyframe));
            if (frame != null)
            {
                procStopwatch.Stop();
                tracker.UpdateMetrics(frame, (int)procStopwatch.ElapsedMilliseconds);
                FrameCaptured?.Invoke(this, frame);
            }
        }

        private readonly record struct WindowCaptureOptions(
            int Quality,
            double Scale,
            int CustomTargetWidth = 0,
            int CustomTargetHeight = 0,
            bool ForceKeyframe = false);

        private static bool NeedsResize(int targetW, int targetH, int width, int height) =>
            targetW > 0 && targetH > 0 && (targetW != width || targetH != height);

        private static int ComputeTargetDimension(int customTarget, int original, double scale)
        {
            if (customTarget > 0) return customTarget & ~1;
            if (scale > 0.2 && scale < 0.99) return ((int)(original * scale)) & ~1;
            return 0;
        }

        private static bool IsCursorShowing(in CursorInfo pci) =>
            pci.flags == CURSOR_SHOWING && pci.hCursor != IntPtr.Zero;

        private static bool IsInsideBounds(int x, int y, int width, int height) =>
            x >= 0 && x < width && y >= 0 && y < height;

        private static bool TryGetEvenClientDimensions(IntPtr hwnd, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (!GetClientRect(hwnd, out NativeRect clientRect))
                return false;

            width = (clientRect.Right - clientRect.Left) & ~1;
            height = (clientRect.Bottom - clientRect.Top) & ~1;
            return width >= 2 && height >= 2;
        }

        private sealed partial class GdiWindowCaptureSession : IDisposable
        {
            public IntPtr Hwnd { get; private init; }
            public IntPtr HdcWindow { get; private init; }
            public IntPtr HdcMem { get; private init; }
            public IntPtr HBitmap { get; private init; }
            public IntPtr PBits { get; private init; }
            public IntPtr HOld { get; private init; }

            public static GdiWindowCaptureSession? TryCreate(IntPtr hwnd, int width, int height)
            {
                IntPtr hdcWindow = GetDC(hwnd);
                if (hdcWindow == IntPtr.Zero)
                    return null;

                IntPtr hdcMem = CreateCompatibleDC(hdcWindow);
                if (hdcMem == IntPtr.Zero)
                {
                    SafeReleaseDC(hwnd, hdcWindow);
                    return null;
                }

                var bmi = CreateBitmapInfo(width, height);
                IntPtr hBitmap = CreateDIBSection(hdcMem, ref bmi, DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (!IsValidBitmapSection(hBitmap, pBits))
                {
                    DeleteDC(hdcMem);
                    SafeReleaseDC(hwnd, hdcWindow);
                    return null;
                }

                IntPtr hOld = SelectObject(hdcMem, hBitmap);
                return new GdiWindowCaptureSession
                {
                    Hwnd = hwnd,
                    HdcWindow = hdcWindow,
                    HdcMem = hdcMem,
                    HBitmap = hBitmap,
                    PBits = pBits,
                    HOld = hOld
                };
            }

            private static bool IsValidBitmapSection(IntPtr hBitmap, IntPtr pBits) =>
                hBitmap != IntPtr.Zero && pBits != IntPtr.Zero;

            private static BitmapInfo CreateBitmapInfo(int width, int height)
            {
                var bmi = new BitmapInfo();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;
                return bmi;
            }

            private static void SafeReleaseDC(IntPtr hwnd, IntPtr hdc)
            {
                if (ReleaseDC(hwnd, hdc) == 0)
                {
                    // El DC ya había sido liberado o es inválido
                }
            }

            public void Dispose()
            {
                if (HOld != IntPtr.Zero) SelectObject(HdcMem, HOld);
                if (HBitmap != IntPtr.Zero) DeleteObject(HBitmap);
                if (HdcMem != IntPtr.Zero) DeleteDC(HdcMem);
                if (HdcWindow != IntPtr.Zero) SafeReleaseDC(Hwnd, HdcWindow);
            }

            [LibraryImport("user32.dll")]
            private static partial IntPtr GetDC(IntPtr hWnd);

            [LibraryImport("user32.dll")]
            private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

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

            [LibraryImport("gdi32.dll")]
            private static partial IntPtr CreateDIBSection(IntPtr hdc, ref BitmapInfo pbmi, uint iUsage, out IntPtr ppvBits, IntPtr hSection, uint dwOffset);
        }

        private static (SKBitmap bitmapToEncode, SKBitmap? disposableBitmap, int finalWidth, int finalHeight) PrepareEncodingBitmap(
            SKBitmap skBitmap,
            int width,
            int height,
            in WindowCaptureOptions options)
        {
            int targetW = ComputeTargetDimension(options.CustomTargetWidth, width, options.Scale);
            int targetH = ComputeTargetDimension(options.CustomTargetHeight, height, options.Scale);

            if (NeedsResize(targetW, targetH, width, height))
            {
                var resized = skBitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (resized != null)
                {
                    return (resized, resized, targetW, targetH);
                }
            }

            return (skBitmap, null, width, height);
        }

        private (byte[]? EncodedBytes, string Codec) EncodeBitmapWithFallback(
            SKBitmap bitmapToEncode,
            int finalWidth,
            int finalHeight,
            in WindowCaptureOptions options)
        {
            string currentCodec = _codec;
            byte[]? encodedBytes = currentCodec == "h264"
                ? EncodeH264(bitmapToEncode, finalWidth, finalHeight, options.ForceKeyframe)
                : null;

            if (encodedBytes == null || encodedBytes.Length == 0)
            {
                currentCodec = "jpeg";
                encodedBytes = EncodeJpeg(bitmapToEncode, options.Quality);
            }

            return (encodedBytes, currentCodec);
        }

        private CapturedFrame? EncodeCapturedWindowBitmap(IntPtr pBits, int width, int height, in WindowCaptureOptions options)
        {
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBitmap = new SKBitmap();
            skBitmap.InstallPixels(imageInfo, pBits, width * 4);

            var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareEncodingBitmap(skBitmap, width, height, options);
            try
            {
                var (encodedBytes, currentCodec) = EncodeBitmapWithFallback(bitmapToEncode, finalWidth, finalHeight, options);
                if (encodedBytes == null || encodedBytes.Length == 0)
                    return null;

                return new CapturedFrame
                {
                    Data = encodedBytes,
                    Width = finalWidth,
                    Height = finalHeight,
                    Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                    Format = currentCodec
                };
            }
            finally
            {
                resizedBitmap?.Dispose();
            }
        }

        private CapturedFrame? CaptureWindowFrame(IntPtr hwnd, in WindowCaptureOptions options)
        {
            if (!TryGetEvenClientDimensions(hwnd, out int width, out int height))
                return null;

            using var session = GdiWindowCaptureSession.TryCreate(hwnd, width, height);
            if (session == null)
                return null;

            if (!PrintWindow(hwnd, session.HdcMem, PW_RENDERFULLCONTENT))
            {
                BitBlt(session.HdcMem, 0, 0, width, height, session.HdcWindow, 0, 0, SRCCOPY);
            }

            DrawCursorOnDC(hwnd, session.HdcMem, width, height);
            return EncodeCapturedWindowBitmap(session.PBits, width, height, options);
        }

        private byte[]? EncodeH264(SKBitmap bitmap, int width, int height, bool forceKeyframe)
        {
            try
            {
                _h264Encoder ??= new MediaFoundationH264EncoderService(_targetFps, _bitrateMbps * 1_000_000);
                int requiredBytes = width * height * 4;
                if (_reusableBgraBytes == null || _reusableBgraBytes.Length != requiredBytes)
                {
                    _reusableBgraBytes = new byte[requiredBytes];
                }
                Marshal.Copy(bitmap.GetPixels(), _reusableBgraBytes, 0, requiredBytes);
                return _h264Encoder.EncodeFrame(_reusableBgraBytes, width, height, forceKeyframe);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[WindowCapture] Error en H.264, fallback a JPEG: {ex.Message}");
                return null;
            }
        }

        private static byte[]? EncodeJpeg(SKBitmap bitmap, int quality)
        {
            using var image = SKImage.FromBitmap(bitmap);
            if (image != null)
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                return data?.ToArray();
            }
            return null;
        }

        private static void DrawCursorOnDC(IntPtr hwnd, IntPtr hdc, int clientWidth, int clientHeight)
        {
            try
            {
                CursorInfo pci = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref pci) && IsCursorShowing(pci))
                {
                    NativePoint pt = pci.ptScreenPos;
                    if (ScreenToClient(hwnd, ref pt) && IsInsideBounds(pt.x, pt.y, clientWidth, clientHeight))
                    {
                        DrawIconEx(hdc, pt.x, pt.y, pci.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
                    }
                }
            }
            catch
            {
                // Ignorar error de dibujado de cursor
            }
        }
        #region Win32 P/Invoke

        private const int PW_RENDERFULLCONTENT = 0x00000002;
        private const int SRCCOPY = 0x00CC0020;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
        private const int CURSOR_SHOWING = 0x00000001;
        private const int DI_NORMAL = 0x0003;

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeRect
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativePoint
        {
            public int x;
            public int y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct CursorInfo
        {
            public int cbSize;
            public int flags;
            public IntPtr hCursor;
            public NativePoint ptScreenPos;
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

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsWindow(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool IsIconic(IntPtr hWnd);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetClientRect(IntPtr hWnd, out NativeRect lpRect);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BitBlt(IntPtr hdcDest, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hdcSrc, int nXSrc, int nYSrc, int dwRop);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorInfo(ref CursorInfo pci);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool ScreenToClient(IntPtr hWnd, ref NativePoint lpPoint);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyWidth, uint istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

        #endregion

        public void Dispose()
        {
            if (_disposed)
                return;

            StopCapture();
            _h264Encoder?.Dispose();
            _h264Encoder = null;
            _disposed = true;
            GC.SuppressFinalize(this);
        }
    }
}

