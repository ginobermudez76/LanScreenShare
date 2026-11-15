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
    public sealed partial class WindowsScreenCaptureService : ScreenCaptureServiceBase
    {
        public readonly record struct ScreenBounds(int X, int Y, int Width, int Height, bool AllScreens = false);

        private volatile int _captureX = 0;
        private volatile int _captureY = 0;
        private volatile int _captureWidth = 0;
        private volatile int _captureHeight = 0;
        private volatile bool _captureAllScreens = false;

        protected override int StopWaitTimeoutMs => 500;
        protected override string LogTag => "GDI";

        public WindowsScreenCaptureService()
            : base(defaultFps: 30, defaultQuality: 70, defaultCodec: "h264")
        {
        }

        protected override Task RunCaptureLoopAsync(CancellationToken token) =>
            CaptureLoopAsync(token);

        public int CaptureX
        {
            get => _captureX;
            set => _captureX = value;
        }

        public int CaptureY
        {
            get => _captureY;
            set => _captureY = value;
        }

        public int CaptureWidth
        {
            get => _captureWidth;
            set => _captureWidth = Math.Max(0, value);
        }

        public int CaptureHeight
        {
            get => _captureHeight;
            set => _captureHeight = Math.Max(0, value);
        }

        public bool CaptureAllScreens
        {
            get => _captureAllScreens;
            set => _captureAllScreens = value;
        }

        public void SetTargetBounds(ScreenBounds bounds)
        {
            _captureX = bounds.X;
            _captureY = bounds.Y;
            _captureWidth = Math.Max(0, bounds.Width);
            _captureHeight = Math.Max(0, bounds.Height);
            _captureAllScreens = bounds.AllScreens;
        }

        private async Task CaptureLoopAsync(CancellationToken token)
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
            var fpsTracker = new HostFpsTracker();

            while (!token.IsCancellationRequested)
            {
                stopwatch.Restart();
                int frameIntervalMs = Math.Max(5, 1000 / Math.Max(1, _targetFps));

                CaptureAndDispatchFrame(fpsTracker);

                int elapsed = (int)stopwatch.ElapsedMilliseconds;
                int delay = frameIntervalMs - elapsed;
                if (delay > 0)
                {
                    try
                    {
                        await Task.Delay(delay, token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }

        private void CaptureAndDispatchFrame(HostFpsTracker fpsTracker)
        {
            try
            {
                var frameProcStopwatch = Stopwatch.StartNew();
                bool forceKeyframe = _forceKeyframeRequested;
                _forceKeyframeRequested = false;

                var options = new ScreenCaptureOptions(_quality, ResolutionScale, _targetWidth, _targetHeight, forceKeyframe);
                var frame = CaptureDesktopFrame(options);
                if (frame != null)
                {
                    frameProcStopwatch.Stop();
                    frame.HostFrameTimeMs = (int)frameProcStopwatch.ElapsedMilliseconds;
                    frame.HostFps = fpsTracker.RecordFrameAndGetFps();

                    OnFrameCaptured(frame);
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[ScreenCapture] Error capturando frame: {ex.Message}");
            }
        }

        private sealed class HostFpsTracker
        {
            private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
            private int _frameCount;
            private int _currentFps;

            public int RecordFrameAndGetFps()
            {
                _frameCount++;
                if (_stopwatch.ElapsedMilliseconds >= 1000)
                {
                    _currentFps = (int)Math.Round(_frameCount * 1000.0 / _stopwatch.ElapsedMilliseconds);
                    _frameCount = 0;
                    _stopwatch.Restart();
                }
                return _currentFps > 0 ? _currentFps : _frameCount;
            }
        }

        private readonly record struct ScreenCaptureOptions(
            int Quality,
            double Scale,
            int CustomTargetWidth = 0,
            int CustomTargetHeight = 0,
            bool ForceKeyframe = false);

        private bool ShouldCaptureVirtualScreen() =>
            _captureAllScreens || _captureWidth <= 0 || _captureHeight <= 0;

        private void GetCaptureBounds(out int left, out int top, out int width, out int height)
        {
            if (ShouldCaptureVirtualScreen())
            {
                left = GetSystemMetrics(SM_XVIRTUALSCREEN);
                top = GetSystemMetrics(SM_YVIRTUALSCREEN);
                width = GetSystemMetrics(SM_CXVIRTUALSCREEN);
                height = GetSystemMetrics(SM_CYVIRTUALSCREEN);

                if (width <= 0 || height <= 0)
                {
                    width = GetSystemMetrics(SM_CXSCREEN);
                    height = GetSystemMetrics(SM_CYSCREEN);
                    left = 0;
                    top = 0;
                }
            }
            else
            {
                left = _captureX;
                top = _captureY;
                width = _captureWidth;
                height = _captureHeight;
            }

            width &= ~1;
            height &= ~1;
        }

        private sealed partial class GdiDesktopCaptureSession : IDisposable
        {
            public IntPtr HdcSrc { get; private set; }
            public IntPtr HdcDest { get; private set; }
            public IntPtr HBitmap { get; private set; }
            public IntPtr PBits { get; private set; }
            public IntPtr HOld { get; private set; }

            public static GdiDesktopCaptureSession? TryCreate(int width, int height)
            {
                IntPtr hdcSrc = GetDC(IntPtr.Zero);
                if (hdcSrc == IntPtr.Zero) return null;

                IntPtr hdcDest = CreateCompatibleDC(hdcSrc);
                if (hdcDest == IntPtr.Zero)
                {
                    SafeReleaseDC(IntPtr.Zero, hdcSrc);
                    return null;
                }

                var bmi = new BitmapInfo();
                bmi.bmiHeader.biSize = (uint)Marshal.SizeOf<BitmapInfoHeader>();
                bmi.bmiHeader.biWidth = width;
                bmi.bmiHeader.biHeight = -height;
                bmi.bmiHeader.biPlanes = 1;
                bmi.bmiHeader.biBitCount = 32;
                bmi.bmiHeader.biCompression = BI_RGB;

                IntPtr hBitmap = CreateDIBSection(hdcDest, ref bmi, DIB_RGB_COLORS, out IntPtr pBits, IntPtr.Zero, 0);
                if (hBitmap == IntPtr.Zero || pBits == IntPtr.Zero)
                {
                    DeleteDC(hdcDest);
                    SafeReleaseDC(IntPtr.Zero, hdcSrc);
                    return null;
                }

                IntPtr hOld = SelectObject(hdcDest, hBitmap);
                return new GdiDesktopCaptureSession
                {
                    HdcSrc = hdcSrc,
                    HdcDest = hdcDest,
                    HBitmap = hBitmap,
                    PBits = pBits,
                    HOld = hOld
                };
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
                if (HOld != IntPtr.Zero) SelectObject(HdcDest, HOld);
                if (HBitmap != IntPtr.Zero) DeleteObject(HBitmap);
                if (HdcDest != IntPtr.Zero) DeleteDC(HdcDest);
                if (HdcSrc != IntPtr.Zero) SafeReleaseDC(IntPtr.Zero, HdcSrc);
            }

            [LibraryImport("user32.dll")]
            private static partial IntPtr GetDC(IntPtr hWnd);

            [LibraryImport("user32.dll")]
            private static partial int ReleaseDC(IntPtr hWnd, IntPtr hDC);

            [LibraryImport("gdi32.dll")]
            private static partial IntPtr CreateCompatibleDC(IntPtr hDC);

            [LibraryImport("gdi32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            private static partial bool DeleteDC(IntPtr hDC);

            [LibraryImport("gdi32.dll")]
            private static partial IntPtr SelectObject(IntPtr hDC, IntPtr hObject);

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
            in ScreenCaptureOptions options) =>
            PrepareResizedBitmap(
                skBitmap,
                new BitmapResizeOptions(width, height, options.Scale, options.CustomTargetWidth, options.CustomTargetHeight));

        private CapturedFrame? EncodeCapturedBitmap(IntPtr pBits, int width, int height, in ScreenCaptureOptions options)
        {
            var imageInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var skBitmap = new SKBitmap();
            skBitmap.InstallPixels(imageInfo, pBits);

            var (bitmapToEncode, resizedBitmap, finalWidth, finalHeight) = PrepareEncodingBitmap(skBitmap, width, height, options);

            try
            {
                string currentCodec = _codec;
                byte[]? encodedBytes = null;

                if (currentCodec == "h264")
                {
                    encodedBytes = EncodeH264(bitmapToEncode, new H264FrameRequest(finalWidth, finalHeight, options.ForceKeyframe));
                }

                if (encodedBytes == null || encodedBytes.Length == 0)
                {
                    currentCodec = "jpeg";
                    encodedBytes = EncodeJpeg(bitmapToEncode, options.Quality);
                }

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

        private CapturedFrame? CaptureDesktopFrame(in ScreenCaptureOptions options)
        {
            GetCaptureBounds(out int left, out int top, out int width, out int height);
            if (width <= 0 || height <= 0) return null;

            using var session = GdiDesktopCaptureSession.TryCreate(width, height);
            if (session == null) return null;

            BitBlt(session.HdcDest, 0, 0, width, height, session.HdcSrc, left, top, SRCCOPY | CAPTUREBLT);
            DrawMouseCursor(session.HdcDest, left, top);

            return EncodeCapturedBitmap(session.PBits, width, height, options);
        }

        private static void DrawMouseCursor(IntPtr hdcDest, int screenLeft, int screenTop)
        {
            try
            {
                CursorInfo cursorInfo = new CursorInfo { cbSize = Marshal.SizeOf<CursorInfo>() };
                if (GetCursorInfo(ref cursorInfo) && cursorInfo.flags == CURSOR_SHOWING)
                {
                    DrawIcon(hdcDest, cursorInfo.ptScreenPos.x - screenLeft, cursorInfo.ptScreenPos.y - screenTop, cursorInfo.hCursor);
                }
            }
            catch
            {
                // Ignorar error al capturar el cursor
            }
        }

        #region P/Invoke GDI & User32

        private const int SRCCOPY = 0x00CC0020;
        private const int CAPTUREBLT = 0x40000000;
        private const int DIB_RGB_COLORS = 0;
        private const int BI_RGB = 0;
        private const int CURSOR_SHOWING = 0x00000001;

        private const int SM_XVIRTUALSCREEN = 76;
        private const int SM_YVIRTUALSCREEN = 77;
        private const int SM_CXVIRTUALSCREEN = 78;
        private const int SM_CYVIRTUALSCREEN = 79;
        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [LibraryImport("gdi32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool BitBlt(IntPtr hObject, int nXDest, int nYDest, int nWidth, int nHeight, IntPtr hObjectSource, int nXSrc, int nYSrc, int dwRop);

        [LibraryImport("user32.dll")]
        private static partial int GetSystemMetrics(int nIndex);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DrawIcon(IntPtr hDC, int X, int Y, IntPtr hIcon);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool GetCursorInfo(ref CursorInfo pci);

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

        #endregion
    }
}
