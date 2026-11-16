using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using LanScreenShare.Core.Models;
using LanScreenShare.Desktop;
using LanScreenShare.Media.Display;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LanScreenShare.Tests;

[TestClass]
public class VirtualDisplayDriverInstallerTests
{
    private sealed class StubElevatedRunner(ElevatedProcessOutcome outcome) : IElevatedProcessRunner
    {
        public VddInstallPlan? LastPlan { get; private set; }

        public ElevatedProcessOutcome RunElevated(VddInstallPlan plan)
        {
            LastPlan = plan;
            return outcome;
        }
    }

    [TestMethod]
    public void BuildInstallPlan_SelectsLocalInfOrWingetFallback()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "lss-vdd-test");
        string expectedInf = Path.Combine(baseDir, "Drivers", "VDD", "MttVDD.inf");

        var localInstaller = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true)),
            path => string.Equals(path, expectedInf, StringComparison.OrdinalIgnoreCase),
            isWindows: true);

        var localPlan = localInstaller.BuildInstallPlan(baseDir);
        Assert.AreEqual((VddInstallSource.LocalInfPackage, "pnputil.exe", true), (localPlan.Source, localPlan.Executable, localPlan.RequiresElevation));
        Assert.IsTrue(localPlan.Arguments.Contains(expectedInf, StringComparison.OrdinalIgnoreCase));

        var fallbackInstaller = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true)),
            _ => false,
            isWindows: true);

        var fallbackPlan = fallbackInstaller.BuildInstallPlan(baseDir);
        Assert.AreEqual((VddInstallSource.WingetFallback, "winget", true), (fallbackPlan.Source, fallbackPlan.Executable, fallbackPlan.RequiresElevation));
        Assert.IsTrue(fallbackPlan.Arguments.Contains(VirtualDisplayDriverInstallerService.WingetPackageId, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task InstallDriverAsync_HandlesOutcomesAndCancellationGracefully()
    {
        var rebootOk = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true, ExitCode: 3010)),
            _ => false,
            isWindows: true);
        var rebootResult = await rebootOk.InstallDriverAsync();
        Assert.IsTrue(rebootResult.Success);

        var cancelled = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(false, 1223, WasCancelledByUser: true, "Cancelado")),
            _ => false,
            isWindows: true);
        var cancelResult = await cancelled.InstallDriverAsync();
        Assert.IsTrue(!cancelResult.Success && cancelResult.WasCancelledByUser);

        var failed = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true, ExitCode: 5)),
            _ => false,
            isWindows: true);
        var failResult = await failed.InstallDriverAsync();
        Assert.IsFalse(failResult.Success);
        Assert.AreEqual(5, failResult.ExitCode);

        var nonWindows = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true)),
            _ => false,
            isWindows: false);
        Assert.IsFalse((await nonWindows.InstallDriverAsync()).Success);

        var genericFail = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(false, -1, false, "Fallo genérico")),
            _ => false,
            isWindows: true);
        var genericResult = await genericFail.InstallDriverAsync();
        Assert.IsFalse(genericResult.Success || genericResult.WasCancelledByUser);
    }

    [TestMethod]
    public void ElevatedRunner_CreateStartInfoAndDefaultBackends_AreConfiguredSafely()
    {
        var defaultInstaller = new VirtualDisplayDriverInstallerService();
        var defaultPlan = defaultInstaller.BuildInstallPlan();
        var elevatedStartInfo = WindowsElevatedProcessRunner.CreateStartInfo(defaultPlan);
        Assert.AreEqual(("runas", true), (elevatedStartInfo.Verb, elevatedStartInfo.UseShellExecute));

        var missingPlan = new VddInstallPlan
        {
            Source = VddInstallSource.LocalInfPackage,
            Executable = "nonexistent-vdd-installer-tool-xyz.exe",
            Arguments = "/version",
            RequiresElevation = false
        };
        var runner = new WindowsElevatedProcessRunner();
        var outcome = runner.RunElevated(missingPlan);
        Assert.IsFalse(outcome.Started);

        var backends = VirtualDisplayDriverInstallerService.CreateDefaultBackends();
        Assert.AreEqual(2, backends.Count);
        _ = VirtualDisplayDriverInstallerService.IsAnyVirtualDisplayDriverInstalled();
    }

    [TestMethod]
    public void DeviceInfo_ExtendButtonPropertiesAndAvailability_NotifyChanges()
    {
        var device = new DeviceInfo { Capabilities = DeviceCapabilities.WirelessDisplay };
        var changed = new List<string?>();
        device.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        Assert.AreEqual("Extender pantalla", device.ExtendButtonText);
        Assert.AreEqual(OperatingSystem.IsWindows(), device.CanExtendToDevice);

        device.IsWirelessDisplayBusy = true;
        Assert.AreEqual("Ocupado", device.ExtendButtonText);
        Assert.IsFalse(device.CanExtendToDevice);

        device.IsExtending = true;
        Assert.AreEqual("Extendiendo", device.ExtendButtonText);
        CollectionAssert.Contains(changed, nameof(DeviceInfo.ExtendButtonText));
        CollectionAssert.Contains(changed, nameof(DeviceInfo.CanExtendToDevice));
    }

    [TestMethod]
    public void ParsePowerBroadcast_MapsSuspendAndResumeMessagesCorrectly()
    {
        Assert.AreEqual(VirtualDisplayPowerEvent.Suspend, MainWindow.ParsePowerBroadcast(0x0218, 0x0004));
        Assert.AreEqual(VirtualDisplayPowerEvent.Resume, MainWindow.ParsePowerBroadcast(0x0218, 0x0012));
        Assert.AreEqual(VirtualDisplayPowerEvent.Resume, MainWindow.ParsePowerBroadcast(0x0218, 0x0007));
        Assert.AreEqual(VirtualDisplayPowerEvent.None, MainWindow.ParsePowerBroadcast(0x0218, 0x000A));
        Assert.AreEqual(VirtualDisplayPowerEvent.None, MainWindow.ParsePowerBroadcast(0x0010, 0x0004));
    }

    [TestMethod]
    public void BuildInstallPlan_PrefersRepairVddScriptWhenInstallerAssetsExist()
    {
        string baseDir = Path.Combine(Path.GetTempPath(), "lss-vdd-repair-test");
        string expectedInf = Path.Combine(baseDir, "Drivers", "VDD", "MttVDD.inf");
        string setupScript = Path.Combine(baseDir, "Configure-LanScreenShareSetup.ps1");
        string rootInstaller = Path.Combine(baseDir, "VddRootDeviceInstaller.cs.txt");

        var repairInstaller = new VirtualDisplayDriverInstallerService(
            new StubElevatedRunner(new ElevatedProcessOutcome(true)),
            path => string.Equals(path, expectedInf, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, setupScript, StringComparison.OrdinalIgnoreCase)
                || string.Equals(path, rootInstaller, StringComparison.OrdinalIgnoreCase),
            isWindows: true);

        var repairPlan = repairInstaller.BuildInstallPlan(baseDir);
        Assert.AreEqual((VddInstallSource.LocalInfPackage, "powershell.exe", true), (repairPlan.Source, repairPlan.Executable, repairPlan.RequiresElevation));
        Assert.IsTrue(repairPlan.Arguments.Contains("-Action RepairVdd", StringComparison.Ordinal));
    }

    private static readonly string[] UnrelatedDisplayIds = [@"root\wvmd", @"PCI\VEN_1002"];
    private static readonly string[] RootMttVddIds = [@"Root\MttVDD"];
    private static readonly string[] ShortMttVddIds = ["mttvdd"];

    [TestMethod]
    public void VirtualDriversVddBackend_HasMttVddHardwareId_RejectsUnrelatedDrivers()
    {
        Assert.IsFalse(VirtualDriversVddBackend.HasMttVddHardwareId(null));
        Assert.IsFalse(VirtualDriversVddBackend.HasMttVddHardwareId(Array.Empty<string>()));
        Assert.IsFalse(VirtualDriversVddBackend.HasMttVddHardwareId(UnrelatedDisplayIds));
        Assert.IsTrue(VirtualDriversVddBackend.HasMttVddHardwareId(RootMttVddIds));
        Assert.IsTrue(VirtualDriversVddBackend.HasMttVddHardwareId(ShortMttVddIds));
    }
}
