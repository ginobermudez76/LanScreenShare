using System;
using System.Threading;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace LanScreenShare.Tests;

[TestClass]
public class ScreenCaptureServiceBaseTests
{
    private sealed class TestableCaptureService : ScreenCaptureServiceBase
    {
        public TestableCaptureService(int fps = 30, int quality = 75, string codec = "jpeg")
            : base(fps, quality, codec)
        {
        }

        public bool ConsumeKeyframe() => ConsumeKeyframeRequest();
        public void EmitFrame(CapturedFrame frame) => OnFrameCaptured(frame);
        public byte[]? TryEncodeH264(SKBitmap bitmap, in H264FrameRequest request) => EncodeH264(bitmap, request);

        protected override async Task RunCaptureLoopAsync(CancellationToken token)
        {
            try
            {
                await Task.Delay(Timeout.Infinite, token);
            }
            catch (OperationCanceledException)
            {
                // Expected cancellation
            }
        }
    }

    [TestMethod]
    public void ConstructorAndProperties_ClampValuesToValidRanges()
    {
        using var service = new TestableCaptureService(fps: 500, quality: 1, codec: "invalid");
        Assert.AreEqual((120, 10, "jpeg"), (service.TargetFps, service.Quality, service.Codec));

        service.UpdateSettings(new CaptureProfile
        {
            TargetFps = 0,
            Quality = 200,
            BitrateMbps = 100,
            ResolutionScale = 0.05,
            Codec = "H264",
            TargetWidth = -10,
            TargetHeight = 720
        });

        Assert.AreEqual((1, 100, 50, 0.25), (service.TargetFps, service.Quality, service.BitrateMbps, service.ResolutionScale));
        Assert.AreEqual(("h264", 0, 720), (service.Codec, service.TargetWidth, service.TargetHeight));
    }

    [TestMethod]
    public void LifecycleMethods_StartStopKeyframeAndDisposeWorkCleanly()
    {
        using var service = new TestableCaptureService();
        bool? secureDesktopState = null;
        CapturedFrame? capturedFrame = null;
        service.SecureDesktopChanged += (_, s) => secureDesktopState = s;
        service.FrameCaptured += (_, f) => capturedFrame = f;

        Assert.IsFalse(service.IsCapturing);
        Assert.IsTrue(service.ConsumeKeyframe());
        Assert.IsFalse(service.ConsumeKeyframe());

        service.RequestKeyframe();
        Assert.IsTrue(service.ConsumeKeyframe());

        service.StartCapture(new CaptureProfile { TargetFps = 60, Quality = 80, BitrateMbps = 10, ResolutionScale = 1.0, Codec = "jpeg" });
        service.StartCapture(new CaptureProfile());
        Assert.IsTrue(service.IsCapturing);

        var testFrame = new CapturedFrame { Width = 4, Height = 4, Data = [1, 2], Format = "jpeg" };
        service.EmitFrame(testFrame);
        Assert.AreSame(testFrame, capturedFrame);

        service.StopCapture();
        Assert.IsFalse(service.IsCapturing);
        Assert.IsFalse(secureDesktopState!.Value);

        service.Dispose();
        service.Dispose();
    }

    [TestMethod]
    public void PrepareResizedBitmap_HandlesCustomTargetsScalesAndNoOp()
    {
        Assert.AreEqual(1280, ScreenCaptureServiceBase.ComputeTargetDimension(1281, 1920, 1.0));
        Assert.AreEqual(960, ScreenCaptureServiceBase.ComputeTargetDimension(0, 1920, 0.5));
        Assert.AreEqual(0, ScreenCaptureServiceBase.ComputeTargetDimension(0, 1920, 1.0));

        using var source = new SKBitmap(100, 80);
        var (sameBmp, sameDisp, sameW, sameH) = ScreenCaptureServiceBase.PrepareResizedBitmap(source, new BitmapResizeOptions(100, 80, 1.0));
        Assert.AreSame(source, sameBmp);
        Assert.IsNull(sameDisp);
        Assert.AreEqual((100, 80), (sameW, sameH));

        var (resizedBmp, resizedDisp, targetW, targetH) = ScreenCaptureServiceBase.PrepareResizedBitmap(source, new BitmapResizeOptions(100, 80, 0.5));
        try
        {
            Assert.IsNotNull(resizedDisp);
            Assert.AreEqual((50, 40), (targetW, targetH));
            Assert.AreEqual((50, 40), (resizedBmp.Width, resizedBmp.Height));
        }
        finally
        {
            resizedDisp?.Dispose();
        }
    }

    [TestMethod]
    public void EncodeJpegAndEncoderMetadata_ProduceValidOutputAndSafeFallback()
    {
        using var service = new TestableCaptureService(codec: "jpeg");
        Assert.AreEqual(("JPEG", false), (service.ActiveEncoderDescription, service.IsHardwareAccelerated));

        using var bitmap = new SKBitmap(16, 16, SKColorType.Bgra8888, SKAlphaType.Premul);
        bitmap.Erase(SKColors.CornflowerBlue);

        byte[]? jpegBytes = ScreenCaptureServiceBase.EncodeJpeg(bitmap, 80);
        Assert.IsNotNull(jpegBytes);
        Assert.AreEqual((true, 0xFF, 0xD8), (jpegBytes.Length > 2, jpegBytes[0], jpegBytes[1]));

        byte[]? h264OrFallback = service.TryEncodeH264(bitmap, new H264FrameRequest(16, 16, true));
        Assert.IsTrue(h264OrFallback == null || h264OrFallback.Length > 0);
    }
}
