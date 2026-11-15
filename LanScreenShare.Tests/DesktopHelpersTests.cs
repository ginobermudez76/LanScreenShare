using System;
using System.IO;
using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using LanScreenShare.Core.Models;
using LanScreenShare.Desktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class DesktopHelpersTests
{
    [DataTestMethod]
    [DataRow(100, 50, true)]
    [DataRow(0, 50, false)]
    [DataRow(100, 0, false)]
    [DataRow(-1, 50, false)]
    public void HasPositiveDimensions_ValidatesSizeAndRect(double w, double h, bool expected)
    {
        Assert.AreEqual(expected, RemoteInputHelper.HasPositiveDimensions(new Size(w, h)));
        Assert.AreEqual(expected, RemoteInputHelper.HasPositiveDimensions(new Rect(0, 0, w, h)));
    }

    [TestMethod]
    public void TryGetNormalizedCoordinates_NullImage_ReturnsFalse()
    {
        bool ok = RemoteInputHelper.TryGetNormalizedCoordinates(null, null!, out float x, out float y);
        Assert.IsFalse(ok);
        Assert.AreEqual(0f, x);
        Assert.AreEqual(0f, y);
    }

    [DataTestMethod]
    [DataRow(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed, RemoteMouseButton.Left)]
    [DataRow(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed, RemoteMouseButton.Right)]
    [DataRow(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed, RemoteMouseButton.Middle)]
    [DataRow(RawInputModifiers.None, PointerUpdateKind.Other, RemoteMouseButton.None)]
    public void GetPressedMouseButton_MapsButtonsAndFallback(
        RawInputModifiers modifiers,
        PointerUpdateKind kind,
        RemoteMouseButton expected)
    {
        var props = new PointerPointProperties(modifiers, kind);
        Assert.AreEqual(expected, RemoteInputHelper.GetPressedMouseButton(props));
    }

    [DataTestMethod]
    [DataRow(PointerUpdateKind.LeftButtonReleased, RemoteMouseButton.Left)]
    [DataRow(PointerUpdateKind.RightButtonReleased, RemoteMouseButton.Right)]
    [DataRow(PointerUpdateKind.MiddleButtonReleased, RemoteMouseButton.Middle)]
    [DataRow(PointerUpdateKind.Other, RemoteMouseButton.Left)]
    public void GetReleasedMouseButton_MapsButtonsAndFallback(PointerUpdateKind kind, RemoteMouseButton expected)
    {
        var props = new PointerPointProperties(RawInputModifiers.None, kind);
        Assert.AreEqual(expected, RemoteInputHelper.GetReleasedMouseButton(props));
    }

    [DataTestMethod]
    [DataRow(Key.A, 0x41)]
    [DataRow(Key.Z, 0x5A)]
    [DataRow(Key.D0, 0x30)]
    [DataRow(Key.D9, 0x39)]
    [DataRow(Key.NumPad0, 0x60)]
    [DataRow(Key.NumPad9, 0x69)]
    [DataRow(Key.F1, 0x70)]
    [DataRow(Key.F12, 0x7B)]
    [DataRow(Key.Return, 0x0D)]
    [DataRow(Key.Escape, 0x1B)]
    [DataRow(Key.Space, 0x20)]
    [DataRow(Key.LeftShift, 0x10)]
    [DataRow(Key.RightCtrl, 0x11)]
    [DataRow(Key.LeftAlt, 0x12)]
    [DataRow(Key.None, 0)]
    public void ConvertAvaloniaKeyToWin32Vk_MapsRangesSpecialsAndUnknown(Key key, int expectedVk)
    {
        Assert.AreEqual(expectedVk, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(key));
    }

    [TestMethod]
    public void SettingsService_LoadAndSave_RoundTripsWithIsolatedPath()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"LanScreenShare_SettingsTest_{Guid.NewGuid():N}");
        string tempFile = Path.Combine(tempDir, "settings.json");

        try
        {
            var initial = SettingsService.Load(tempFile);
            Assert.IsNotNull(initial);

            initial.EnableAdaptiveBitrate = false;
            initial.BitrateMbps = 22;
            SettingsService.Save(initial, tempFile);

            var reloaded = SettingsService.Load(tempFile);
            Assert.IsFalse(reloaded.EnableAdaptiveBitrate);
            Assert.AreEqual(22, reloaded.BitrateMbps);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
        }
    }

    [TestMethod]
    public void TryGetNormalizedCoordinates_FillAndUniformModes_MapAndRejectLetterboxMargins()
    {
        var fillInput = new RemoteInputHelper.NormalizedCoordinateInput(new Point(50, 25), new Size(200, 100), new Rect(0, 0, 100, 50), Stretch.Fill);
        Assert.IsTrue(RemoteInputHelper.TryGetNormalizedCoordinates(fillInput, out float fx, out float fy));
        Assert.AreEqual((0.5f, 0.5f), (fx, fy));

        // Imagen 100x100 dentro de contenedor 200x100 => escala Uniform 1.0 -> offsetX = 50, rendered = 100x100
        var uniformCenter = new RemoteInputHelper.NormalizedCoordinateInput(new Point(100, 50), new Size(100, 100), new Rect(0, 0, 200, 100), Stretch.Uniform);
        Assert.IsTrue(RemoteInputHelper.TryGetNormalizedCoordinates(uniformCenter, out float ux, out float uy));
        Assert.AreEqual((0.5f, 0.5f), (ux, uy));

        var uniformLetterboxMargin = uniformCenter with { Position = new Point(20, 50) };
        Assert.IsFalse(RemoteInputHelper.TryGetNormalizedCoordinates(uniformLetterboxMargin, out _, out _));

        var zeroBounds = uniformCenter with { Bounds = new Rect(0, 0, 0, 100) };
        Assert.IsFalse(RemoteInputHelper.TryGetNormalizedCoordinates(zeroBounds, out _, out _));
    }

    [TestMethod]
    public void ConvertAvaloniaKeyToWin32Vk_CoversNavigationAndModifierKeys()
    {
        var mappings = new[]
        {
            (Key.Back, 0x08), (Key.Tab, 0x09), (Key.Delete, 0x2E),
            (Key.Left, 0x25), (Key.Up, 0x26), (Key.Right, 0x27), (Key.Down, 0x28),
            (Key.Home, 0x24), (Key.End, 0x23), (Key.PageUp, 0x21), (Key.PageDown, 0x22),
            (Key.RightShift, 0x10), (Key.LeftCtrl, 0x11), (Key.RightAlt, 0x12)
        };

        foreach (var (key, expectedVk) in mappings)
        {
            Assert.AreEqual(expectedVk, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(key));
        }
    }
}
