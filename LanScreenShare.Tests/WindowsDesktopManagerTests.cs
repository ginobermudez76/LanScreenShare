using System;
using System.Runtime.Versioning;
using LanScreenShare.Media;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
[SupportedOSPlatform("windows")]
public class WindowsDesktopManagerTests
{
    [TestMethod]
    [DataRow("Winlogon", true)]
    [DataRow("WINLOGON", true)]
    [DataRow("winlogon", true)]
    [DataRow("Screen-saver", true)]
    [DataRow("SCREEN-SAVER", true)]
    [DataRow("Default", false)]
    [DataRow("default", false)]
    [DataRow("WinlogonDesktop", false)]
    [DataRow("", false)]
    public void IsSecureDesktopName_ValidatesCorrectly(string name, bool expectedSecure)
    {
        bool isSecure = WindowsDesktopManager.IsSecureDesktopName(name);
        Assert.AreEqual(expectedSecure, isSecure);
    }

    [TestMethod]
    public void IsActiveDesktopSecure_RunsSafelyWithoutExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Prueba exclusiva para Windows.");
            return;
        }

        bool isSecure = WindowsDesktopManager.IsActiveDesktopSecure(out string desktopName);

        Assert.IsFalse(string.IsNullOrEmpty(desktopName));

        if (string.Equals(desktopName, WindowsDesktopManager.DesktopWinlogon, StringComparison.OrdinalIgnoreCase))
        {
            Assert.IsTrue(isSecure);
        }
        else if (string.Equals(desktopName, WindowsDesktopManager.DesktopDefault, StringComparison.OrdinalIgnoreCase))
        {
            Assert.IsFalse(isSecure);
        }
    }

    [TestMethod]
    public void TryAttachThreadToInputDesktop_RunsSafelyWithoutExceptions()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Inconclusive("Prueba exclusiva para Windows.");
            return;
        }

        _ = WindowsDesktopManager.TryAttachThreadToInputDesktop(out string desktopName, out bool isSecure);

        Assert.IsFalse(string.IsNullOrEmpty(desktopName));
        Assert.AreEqual(WindowsDesktopManager.IsSecureDesktopName(desktopName), isSecure);
    }
}
