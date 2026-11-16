using System;
using System.IO;
using LanScreenShare.Core.Models;
using LanScreenShare.Desktop;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class InstallDefaultsTests
{
    [TestMethod]
    public void SettingsService_Load_AppliesInstallDefaultsOnlyWhenDownloadDirectoryIsEmpty()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"lss-defaults-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string settingsPath = Path.Combine(tempDir, "settings.json");
            string defaultsPath = Path.Combine(tempDir, "install-defaults.json");
            File.WriteAllText(defaultsPath, "{\"DefaultDownloadDirectory\":\"D:\\\\LanDownloads\"}");

            var firstRun = SettingsService.Load(settingsPath, defaultsPath);
            Assert.AreEqual(@"D:\LanDownloads", firstRun.DownloadDirectory);

            var custom = new AppSettings { DownloadDirectory = @"E:\MiCarpeta" };
            SettingsService.Save(custom, settingsPath);
            var preserved = SettingsService.Load(settingsPath, defaultsPath);
            Assert.AreEqual(@"E:\MiCarpeta", preserved.DownloadDirectory);

            File.WriteAllText(defaultsPath, "{invalid-json");
            string emptySettingsPath = Path.Combine(tempDir, "missing-settings.json");
            var safeFallback = SettingsService.Load(emptySettingsPath, defaultsPath);
            Assert.AreEqual(string.Empty, safeFallback.DownloadDirectory);
        }
        finally
        {
            if (Directory.Exists(tempDir))
                Directory.Delete(tempDir, recursive: true);
        }
    }
}
