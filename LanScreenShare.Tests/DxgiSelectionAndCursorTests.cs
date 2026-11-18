using System;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SkiaSharp;

namespace LanScreenShare.Tests;

[TestClass]
public class DxgiSelectionAndCursorTests
{
    private static DxgiOutputDescriptor CreateOutput(
        (int Adapter, int Output) slot,
        string deviceName,
        DxgiOutputRect bounds) => new()
        {
            AdapterIndex = slot.Adapter,
            OutputIndex = slot.Output,
            DeviceName = deviceName,
            Bounds = bounds
        };

    [TestMethod]
    public void SelectBestIndex_TargetDeviceOnSecondaryAdapter_NeverSelectsAdapterZero()
    {
        var candidates = new[]
        {
            CreateOutput((0, 0), @"\\.\DISPLAY1", new DxgiOutputRect { Left = 0, Top = 0, Width = 1920, Height = 1080 }),
            CreateOutput((1, 0), @"\\.\DISPLAY2", new DxgiOutputRect { Left = 1920, Top = 0, Width = 1920, Height = 1080 })
        };

        var target = new DxgiOutputTarget(
            @"\\.\display2",
            MonitorIndex: 0,
            new DxgiOutputRect { Left = 1920, Top = 0, Width = 1920, Height = 1080 });

        int selected = DxgiOutputSelector.SelectBestIndex(candidates, target);

        Assert.AreEqual(1, selected);
        Assert.AreEqual(1, candidates[selected].AdapterIndex);
    }

    [TestMethod]
    public void SelectBestIndex_IdenticalResolutions_PrioritizesDeviceNameThenBoundsThenIndex()
    {
        var candidates = new[]
        {
            CreateOutput((0, 0), @"\\.\DISPLAY1", new DxgiOutputRect { Left = 0, Top = 0, Width = 1920, Height = 1080 }),
            CreateOutput((0, 1), @"\\.\DISPLAY2", new DxgiOutputRect { Left = 1920, Top = 0, Width = 1920, Height = 1080 }),
            CreateOutput((1, 0), @"\\.\DISPLAY3", new DxgiOutputRect { Left = 3840, Top = 0, Width = 1920, Height = 1080 })
        };

        int byName = DxgiOutputSelector.SelectBestIndex(candidates, new DxgiOutputTarget(
            @"\\.\DISPLAY3",
            0,
            new DxgiOutputRect { Left = 0, Top = 0, Width = 1920, Height = 1080 }));

        int byBounds = DxgiOutputSelector.SelectBestIndex(candidates, new DxgiOutputTarget(
            null,
            0,
            new DxgiOutputRect { Left = 1920, Top = 0, Width = 1920, Height = 1080 }));

        int byIndex = DxgiOutputSelector.SelectBestIndex(candidates, new DxgiOutputTarget(
            @"\\.\MISSING",
            2,
            default));

        Assert.AreEqual((2, 1, 2), (byName, byBounds, byIndex));
        Assert.AreEqual(-1, DxgiOutputSelector.SelectBestIndex([], new DxgiOutputTarget(null, 0, default)));
    }

    [TestMethod]
    public void ShouldRedrawCursor_WhenCursorMovesOutsideCapturedMonitor_EmitsSingleExitFrameThenSkips()
    {
        var monitor2Bounds = new DxgiOutputRect { Left = 1920, Top = 0, Width = 1920, Height = 1080 };
        var state = CursorTrackingState.Initial;
        var arrow = new IntPtr(101);
        var iBeam = new IntPtr(202);

        bool enterInside = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 2100, Y = 400, Handle = arrow }, monitor2Bounds, ref state);
        bool unchangedInside = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 2100, Y = 400, Handle = arrow }, monitor2Bounds, ref state);
        bool shapeChangedInside = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 2100, Y = 400, Handle = iBeam }, monitor2Bounds, ref state);
        bool firstExit = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 500, Y = 400, Handle = iBeam }, monitor2Bounds, ref state);
        bool movingInMonitor1 = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 650, Y = 420, Handle = arrow }, monitor2Bounds, ref state);

        Assert.AreEqual((true, true, true, false, false), (enterInside, shapeChangedInside, firstExit, unchangedInside, movingInMonitor1));
    }

    [TestMethod]
    public void ShouldRedrawCursor_WhenCursorHidesInsideMonitor_EmitsSingleHideFrame()
    {
        var bounds = new DxgiOutputRect { Left = 0, Top = 0, Width = 1920, Height = 1080 };
        var state = CursorTrackingState.Initial;
        var arrow = new IntPtr(101);

        DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = true, X = 100, Y = 100, Handle = arrow }, bounds, ref state);
        bool firstHide = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = false }, bounds, ref state);
        bool stillHidden = DxgiCursorTracker.ShouldRedrawCursor(
            new CursorSample { IsVisible = false }, bounds, ref state);

        Assert.IsTrue(firstHide);
        Assert.IsFalse(stillHidden);
    }

    [TestMethod]
    public void ExtractDualPassSprites_SeparatesTransparentColorAndXorIBeamCorrectly()
    {
        // 3 pixels: [0] transparent, [1] opaque green, [2] monochrome XOR inversion (I-Beam)
        byte[] onBlack =
        [
            0, 0, 0, 255,
            0, 200, 0, 255,
            255, 255, 255, 255
        ];
        byte[] onWhite =
        [
            255, 255, 255, 255,
            0, 200, 0, 255,
            0, 0, 0, 255
        ];

        var extracted = CursorPixelExtractor.ExtractDualPassSprites(onBlack, onWhite, (3, 1));

        using var colorImg = CursorPixelExtractor.CreateBgraImage(extracted.ColorBgra, 3, 1);
        using var xorImg = CursorPixelExtractor.CreateBgraImage(extracted.XorMaskBgra, 3, 1);
        using var darkBg = CompositeOverColor(SKColors.Black, colorImg, xorImg);
        using var lightBg = CompositeOverColor(SKColors.White, colorImg, xorImg);

        Assert.AreEqual((true, true), (extracted.HasVisiblePixels, extracted.HasXorPixels));
        Assert.AreEqual((SKColors.Black, new SKColor(0, 200, 0, 255), SKColors.White), (darkBg.GetPixel(0, 0), darkBg.GetPixel(1, 0), darkBg.GetPixel(2, 0)));
        Assert.AreEqual((SKColors.White, new SKColor(0, 200, 0, 255), SKColors.Black), (lightBg.GetPixel(0, 0), lightBg.GetPixel(1, 0), lightBg.GetPixel(2, 0)));
    }

    private static SKBitmap CompositeOverColor(SKColor bg, SKImage colorImg, SKImage xorImg)
    {
        var target = new SKBitmap(colorImg.Width, colorImg.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var c = new SKCanvas(target);
        c.Clear(bg);
        c.DrawImage(colorImg, 0, 0, SKSamplingOptions.Default);
        using var xorPaint = new SKPaint { BlendMode = SKBlendMode.Difference };
        c.DrawImage(xorImg, 0, 0, SKSamplingOptions.Default, xorPaint);
        return target;
    }

    private sealed class FakeCursorProvider(Func<IntPtr, (bool Ok, NativeCursorCapture Capture)> factory) : INativeCursorProvider
    {
        public int CaptureCalls { get; private set; }

        public bool TryCaptureCursor(IntPtr cursorHandle, out NativeCursorCapture capture)
        {
            CaptureCalls++;
            var result = factory(cursorHandle);
            capture = result.Capture;
            return result.Ok;
        }
    }

    [TestMethod]
    public void CursorSpriteRenderer_CachesByHandleEvictsWhenFullAndDrawsAlignedSprite()
    {
        byte[] onBlack =
        [
            0, 0, 0, 255,       255, 255, 255, 255,
            0, 0, 255, 255,     0, 0, 0, 255
        ];
        byte[] onWhite =
        [
            255, 255, 255, 255, 0, 0, 0, 255,
            0, 0, 255, 255,     255, 255, 255, 255
        ];

        var provider = new FakeCursorProvider(_ => (true, new NativeCursorCapture((2, 2), (1, 0), (onBlack, onWhite))));
        using var renderer = new CursorSpriteRenderer(provider, maxCacheEntries: 2);
        using var canvasBmp = new SKBitmap(20, 20, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(canvasBmp)) { c.Clear(SKColors.Black); }

        var target = new CursorRenderTarget { ScreenLeft = 100, ScreenTop = 100, ScaleX = 1f, ScaleY = 1f };
        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 105, Y = 105, Handle = new IntPtr(10) }, target);
        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 115, Y = 105, Handle = new IntPtr(10) }, target);
        int callsAfterCacheHit = provider.CaptureCalls;

        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 110, Y = 110, Handle = new IntPtr(20) }, target);
        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 112, Y = 112, Handle = new IntPtr(30) }, target);

        Assert.AreEqual((1, 1), (callsAfterCacheHit, renderer.CachedCount));
        Assert.AreEqual((SKColors.White, new SKColor(255, 0, 0, 255)), (canvasBmp.GetPixel(5, 5), canvasBmp.GetPixel(4, 6)));
    }

    [TestMethod]
    public void CursorSpriteRenderer_FallsBackToArrowAndIgnoresInvisibleOrOffscreen()
    {
        var provider = new FakeCursorProvider(_ => (false, default));
        using var renderer = new CursorSpriteRenderer(provider);
        using var canvasBmp = new SKBitmap(40, 40, SKColorType.Bgra8888, SKAlphaType.Premul);
        using (var c = new SKCanvas(canvasBmp)) { c.Clear(SKColors.Navy); }

        var target = new CursorRenderTarget { ScreenLeft = 0, ScreenTop = 0, ScaleX = 1f, ScaleY = 1f };
        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = false, X = 10, Y = 10, Handle = new IntPtr(5) }, target);
        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 500, Y = 500, Handle = new IntPtr(5) }, target);
        var untouchedPixel = canvasBmp.GetPixel(10, 10);

        renderer.DrawCursor(canvasBmp, new CursorSample { IsVisible = true, X = 5, Y = 5, Handle = new IntPtr(5) }, target);
        var arrowTipPixel = canvasBmp.GetPixel(6, 10);

        var emptyResult = CursorPixelExtractor.ExtractDualPassSprites([], [], (0, 0));
        var shortResult = CursorPixelExtractor.ExtractDualPassSprites([1, 2], [1, 2], (2, 2));

        Assert.AreEqual(SKColors.Navy, untouchedPixel);
        Assert.AreNotEqual(SKColors.Navy, arrowTipPixel);
        Assert.AreEqual((false, false), (emptyResult.HasVisiblePixels, shortResult.HasVisiblePixels));
    }

    [TestMethod]
    public void SelectBestIndex_ExactTargetMatch_NeverFallsBackToPrimaryMonitor()
    {
        var candidates = new[]
        {
            CreateOutput((0, 0), @"\\.\DISPLAY1", new DxgiOutputRect { Left = 0, Top = 0, Width = 1920, Height = 1080 }),
            CreateOutput((1, 0), @"\\.\DISPLAY3", new DxgiOutputRect { Left = 1920, Top = 0, Width = 1280, Height = 720 })
        };

        int missingNamedDevice = DxgiOutputSelector.SelectBestIndex(
            candidates,
            new DxgiOutputTarget(@"\\.\DISPLAY2", 0, new DxgiOutputRect { Left = 1920, Top = 0, Width = 1366, Height = 768 }),
            requireExactMatch: true);

        int unmatchedBounds = DxgiOutputSelector.SelectBestIndex(
            candidates,
            new DxgiOutputTarget(null, 0, new DxgiOutputRect { Left = 1920, Top = 0, Width = 1366, Height = 768 }),
            requireExactMatch: true);

        int matchedBounds = DxgiOutputSelector.SelectBestIndex(
            candidates,
            new DxgiOutputTarget(null, 0, new DxgiOutputRect { Left = 1920, Top = 0, Width = 1280, Height = 720 }),
            requireExactMatch: true);

        Assert.AreEqual((-1, -1, 1), (missingNamedDevice, unmatchedBounds, matchedBounds));
    }

    [TestMethod]
    public void DxgiOutputSelector_PacingCursorBoundsAndExternalResolution_WorkAsExpected()
    {
        Assert.AreEqual(0, DxgiOutputSelector.ComputeFramePaceSleepMs(16.3, 60, isNoFrame: false));
        Assert.AreEqual(2, DxgiOutputSelector.ComputeFramePaceSleepMs(15.5, 60, isNoFrame: true));
        Assert.AreEqual(2, DxgiOutputSelector.ComputeFramePaceSleepMs(18.0, 60, isNoFrame: true));
        Assert.AreEqual(12, DxgiOutputSelector.ComputeFramePaceSleepMs(4.2, 60, isNoFrame: false));
        Assert.AreEqual(12, DxgiOutputSelector.ComputeFramePaceSleepMs(4.2, 60, isNoFrame: true));

        var rect = new DxgiOutputRect { Left = 1920, Top = 0, Width = 2560, Height = 1440 };
        Assert.IsTrue(DxgiOutputSelector.IsCursorNearCaptureBounds(1900, 100, in rect));
        Assert.IsFalse(DxgiOutputSelector.IsCursorNearCaptureBounds(500, 500, in rect));
        var invalidRect = new DxgiOutputRect { Left = 0, Top = 0, Width = 0, Height = 0 };
        Assert.IsFalse(DxgiOutputSelector.IsCursorNearCaptureBounds(0, 0, in invalidRect));

        Assert.IsTrue(DxgiOutputSelector.ShouldTrackExternalSourceResolution(true, 100, 1920, 1080, 1280, 720));
        Assert.IsFalse(DxgiOutputSelector.ShouldTrackExternalSourceResolution(true, 75, 1920, 1080, 1280, 720));
        Assert.IsFalse(DxgiOutputSelector.ShouldTrackExternalSourceResolution(false, 100, 1920, 1080, 1280, 720));
        Assert.IsFalse(DxgiOutputSelector.ShouldTrackExternalSourceResolution(true, 100, 1920, 1080, 1920, 1080));
    }
}
