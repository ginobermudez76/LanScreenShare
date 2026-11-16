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
}
