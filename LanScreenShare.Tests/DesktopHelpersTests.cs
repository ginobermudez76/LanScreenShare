using System;
using System.IO;
using Avalonia;
using Avalonia.Input;
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
}
