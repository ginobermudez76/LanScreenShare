using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Interfaces;
using LanScreenShare.Core.Models;
using SkiaSharp;

namespace LanScreenShare.Media
{
    public readonly record struct BitmapResizeOptions(
        int Width,
        int Height,
        double Scale,
        int CustomTargetWidth = 0,
        int CustomTargetHeight = 0);

    public readonly record struct H264FrameRequest(
        int Width,
        int Height,
        bool ForceKeyframe);

    public abstract class ScreenCaptureServiceBase : IScreenCaptureService
    {
        private CancellationTokenSource? _cts;
        private Task? _captureTask;
        private bool _disposed;

        protected volatile int _targetFps;
        protected volatile int _quality;
        protected volatile int _scalePercent = 100;
        protected volatile int _targetWidth;
        protected volatile int _targetHeight;
        protected volatile string _codec;
        protected volatile int _bitrateMbps = 8;
        protected volatile bool _forceKeyframeRequested = true;

        private byte[]? _reusableBgraBytes;
        private MediaFoundationH264EncoderService? _h264Encoder;

        protected ScreenCaptureServiceBase(int defaultFps, int defaultQuality, string defaultCodec)
        {
            _targetFps = Math.Clamp(defaultFps, 1, 120);
            _quality = Math.Clamp(defaultQuality, 10, 100);
            _codec = string.Equals(defaultCodec, "h264", StringComparison.OrdinalIgnoreCase) ? "h264" : "jpeg";
        }

        public event EventHandler<CapturedFrame>? FrameCaptured;
        public event EventHandler<bool>? SecureDesktopChanged;

        public bool IsCapturing => _cts != null && !_cts.IsCancellationRequested;
        public string? ActiveEncoderDescription => _codec == "h264" && OperatingSystem.IsWindows() ? _h264Encoder?.EncoderDescription : "JPEG";
        public bool IsHardwareAccelerated => _codec == "h264" && OperatingSystem.IsWindows() && (_h264Encoder?.IsHardwareAccelerated ?? false);

        protected virtual int StopWaitTimeoutMs => 800;
        protected virtual bool ClearSecureDesktopOnStop => true;
        protected virtual string LogTag => "Capture";

        public int TargetFps
        {
            get => _targetFps;
            set
            {
                _targetFps = Math.Clamp(value, 1, 120);
                if (OperatingSystem.IsWindows())
                {
                    _h264Encoder?.UpdateFps(_targetFps);
                }
            }
        }

        public int Quality
        {
            get => _quality;
            set => _quality = Math.Clamp(value, 10, 100);
        }

        public int BitrateMbps
        {
            get => _bitrateMbps;
            set
            {
                _bitrateMbps = Math.Clamp(value, 1, 50);
                if (OperatingSystem.IsWindows())
                {
                    _h264Encoder?.UpdateBitrate(_bitrateMbps * 1_000_000);
                }
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

        public void RequestKeyframe() => _forceKeyframeRequested = true;

        protected bool ConsumeKeyframeRequest()
        {
            bool requested = _forceKeyframeRequested;
            _forceKeyframeRequested = false;
            return requested;
        }

        protected void OnFrameCaptured(CapturedFrame frame) => FrameCaptured?.Invoke(this, frame);

        protected void OnSecureDesktopChanged(bool isSecure) => SecureDesktopChanged?.Invoke(this, isSecure);

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
            _captureTask = Task.Run(() => RunCaptureLoopAsync(token), token);
        }

        public void StopCapture()
        {
            _cts?.Cancel();
            try
            {
                _captureTask?.Wait(StopWaitTimeoutMs, CancellationToken.None);
            }
            catch
            {
                // Ignorar excepciones al esperar cancelación
            }
            _cts?.Dispose();
            _cts = null;
            _captureTask = null;
            if (ClearSecureDesktopOnStop)
            {
                OnSecureDesktopChanged(false);
            }
        }

        protected abstract Task RunCaptureLoopAsync(CancellationToken token);

        protected static Task RunDedicatedThreadLoopAsync(
            string threadName,
            Action<CancellationToken> loopBody,
            CancellationToken token)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    loopBody(token);
                    tcs.TrySetResult();
                }
                catch (OperationCanceledException)
                {
                    tcs.TrySetCanceled(token);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            })
            {
                IsBackground = true,
                Name = threadName
            };
            thread.Start();
            return tcs.Task;
        }

        internal static bool NeedsResize(int targetW, int targetH, int width, int height) =>
            targetW > 0 && targetH > 0 && (targetW != width || targetH != height);

        internal static int ComputeTargetDimension(int customTarget, int original, double scale)
        {
            if (customTarget > 0) return customTarget & ~1;
            if (scale > 0.2 && scale < 0.99) return ((int)(original * scale)) & ~1;
            return 0;
        }

        internal static (SKBitmap bitmapToEncode, SKBitmap? disposableBitmap, int finalWidth, int finalHeight) PrepareResizedBitmap(
            SKBitmap skBitmap,
            in BitmapResizeOptions options)
        {
            int targetW = ComputeTargetDimension(options.CustomTargetWidth, options.Width, options.Scale);
            int targetH = ComputeTargetDimension(options.CustomTargetHeight, options.Height, options.Scale);

            if (NeedsResize(targetW, targetH, options.Width, options.Height))
            {
                var resized = skBitmap.Resize(
                    new SKImageInfo(targetW, targetH),
                    new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (resized != null)
                {
                    return (resized, resized, targetW, targetH);
                }
            }

            return (skBitmap, null, options.Width, options.Height);
        }

        protected byte[]? EncodeH264(SKBitmap bitmap, in H264FrameRequest request)
        {
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                _h264Encoder ??= new MediaFoundationH264EncoderService(_targetFps, _bitrateMbps * 1_000_000);
                int requiredBytes = request.Width * request.Height * 4;
                if (_reusableBgraBytes == null || _reusableBgraBytes.Length != requiredBytes)
                {
                    _reusableBgraBytes = new byte[requiredBytes];
                }

                Marshal.Copy(bitmap.GetPixels(), _reusableBgraBytes, 0, requiredBytes);
                return _h264Encoder.EncodeFrame(_reusableBgraBytes, request.Width, request.Height, request.ForceKeyframe);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{LogTag}] Error codificando H.264, fallback a JPEG: {ex.Message}");
                return null;
            }
        }

        internal static byte[]? EncodeJpeg(SKBitmap bitmap, int quality)
        {
            using var image = SKImage.FromBitmap(bitmap);
            if (image != null)
            {
                using var data = image.Encode(SKEncodedImageFormat.Jpeg, quality);
                return data?.ToArray();
            }
            return null;
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_disposed)
                return;

            if (disposing)
            {
                StopCapture();
                if (OperatingSystem.IsWindows())
                {
                    _h264Encoder?.Dispose();
                }
                _h264Encoder = null;
            }

            _disposed = true;
        }

        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }
    }
}
