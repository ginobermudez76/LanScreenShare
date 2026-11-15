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
    [TestMethod]
    public void HasPositiveDimensions_ValidatesSizeAndRect()
    {
        Assert.IsTrue(RemoteInputHelper.HasPositiveDimensions(new Size(100, 50)));
        Assert.IsFalse(RemoteInputHelper.HasPositiveDimensions(new Size(0, 50)));
        Assert.IsFalse(RemoteInputHelper.HasPositiveDimensions(new Size(100, 0)));
        Assert.IsFalse(RemoteInputHelper.HasPositiveDimensions(new Size(-1, 50)));

        Assert.IsTrue(RemoteInputHelper.HasPositiveDimensions(new Rect(0, 0, 10, 20)));
        Assert.IsFalse(RemoteInputHelper.HasPositiveDimensions(new Rect(0, 0, 0, 20)));
        Assert.IsFalse(RemoteInputHelper.HasPositiveDimensions(new Rect(0, 0, 10, -5)));
    }

    [TestMethod]
    public void TryGetNormalizedCoordinates_NullImage_ReturnsFalse()
    {
        bool ok = RemoteInputHelper.TryGetNormalizedCoordinates(null, null!, out float x, out float y);
        Assert.IsFalse(ok);
        Assert.AreEqual(0f, x);
        Assert.AreEqual(0f, y);
    }

    [TestMethod]
    public void GetPressedMouseButton_MapsButtonsAndFallback()
    {
        Assert.AreEqual(
            RemoteMouseButton.Left,
            RemoteInputHelper.GetPressedMouseButton(new PointerPointProperties(RawInputModifiers.LeftMouseButton, PointerUpdateKind.LeftButtonPressed)));
        Assert.AreEqual(
            RemoteMouseButton.Right,
            RemoteInputHelper.GetPressedMouseButton(new PointerPointProperties(RawInputModifiers.RightMouseButton, PointerUpdateKind.RightButtonPressed)));
        Assert.AreEqual(
            RemoteMouseButton.Middle,
            RemoteInputHelper.GetPressedMouseButton(new PointerPointProperties(RawInputModifiers.MiddleMouseButton, PointerUpdateKind.MiddleButtonPressed)));
        Assert.AreEqual(
            RemoteMouseButton.None,
            RemoteInputHelper.GetPressedMouseButton(new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other)));
    }

    [TestMethod]
    public void GetReleasedMouseButton_MapsButtonsAndFallback()
    {
        Assert.AreEqual(
            RemoteMouseButton.Left,
            RemoteInputHelper.GetReleasedMouseButton(new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.LeftButtonReleased)));
        Assert.AreEqual(
            RemoteMouseButton.Right,
            RemoteInputHelper.GetReleasedMouseButton(new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.RightButtonReleased)));
        Assert.AreEqual(
            RemoteMouseButton.Middle,
            RemoteInputHelper.GetReleasedMouseButton(new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.MiddleButtonReleased)));
        Assert.AreEqual(
            RemoteMouseButton.Left,
            RemoteInputHelper.GetReleasedMouseButton(new PointerPointProperties(RawInputModifiers.None, PointerUpdateKind.Other)));
    }

    [TestMethod]
    public void ConvertAvaloniaKeyToWin32Vk_MapsRangesSpecialsAndUnknown()
    {
        Assert.AreEqual(0x41, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.A));
        Assert.AreEqual(0x5A, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.Z));
        Assert.AreEqual(0x30, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.D0));
        Assert.AreEqual(0x39, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.D9));
        Assert.AreEqual(0x60, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.NumPad0));
        Assert.AreEqual(0x69, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.NumPad9));
        Assert.AreEqual(0x70, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.F1));
        Assert.AreEqual(0x7B, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.F12));

        Assert.AreEqual(0x0D, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.Return));
        Assert.AreEqual(0x1B, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.Escape));
        Assert.AreEqual(0x20, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.Space));
        Assert.AreEqual(0x10, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.LeftShift));
        Assert.AreEqual(0x11, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.RightCtrl));
        Assert.AreEqual(0x12, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.LeftAlt));
        Assert.AreEqual(0, RemoteInputHelper.ConvertAvaloniaKeyToWin32Vk(Key.None));
    }

    [TestMethod]
    public void SettingsService_LoadAndSave_RoundTripsCleanly()
    {
        string settingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "LanScreenShare",
            "settings.json");

        string? originalBackup = File.Exists(settingsPath) ? File.ReadAllText(settingsPath) : null;

        try
        {
            var settings = SettingsService.Load();
            Assert.IsNotNull(settings);

            settings.EnableAdaptiveBitrate = !settings.EnableAdaptiveBitrate;
            SettingsService.Save(settings);

            var reloaded = SettingsService.Load();
            Assert.AreEqual(settings.EnableAdaptiveBitrate, reloaded.EnableAdaptiveBitrate);
        }
        finally
        {
            if (originalBackup != null)
            {
                File.WriteAllText(settingsPath, originalBackup);
            }
            else if (File.Exists(settingsPath))
            {
                File.Delete(settingsPath);
            }
        }
    }
}
