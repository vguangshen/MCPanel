using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatLogonStartup148_OnlyRestoresInstalledStoppedServerFromTrayStartup()
    {
        Assert.IsTrue(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: false, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: false, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: true));
    }

    [TestMethod]
    public void TomcatLogonStartup148_UsesImmediateVisibleManualCatalinaLauncher()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var startup = ReadRepositoryFile("TomcatLogonStartup.cs");

        StringAssert.Contains(app, "if (startInTray)");
        StringAssert.Contains(app, "TomcatLogonStartup.TryRestoreSharedTomcatAsync()");
        StringAssert.Contains(startup, "EnvironmentRuntimeService.LaunchTomcatConsole(root)");
        Assert.IsFalse(startup.Contains("InitialDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("RetryDelay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartupProbeTimeout", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("Task.Delay", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("ProcessWindowStyle.Hidden", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartHiddenCatalina", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("WaitForSharedTomcatAsync", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartProduct", StringComparison.OrdinalIgnoreCase));

        // Manual environment actions remain on the exact same visible launcher.
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "windowStyle: ProcessWindowStyle.Normal");
    }

    [TestMethod]
    public void TomcatLogonStartup148_DoesNotReviveLegacyPerProductRunRegistration()
    {
        var legacy = ReadRepositoryFile("TomcatProductStartupManager.cs");
        StringAssert.Contains(legacy, "RemoveRegistration();");
        StringAssert.Contains(legacy, "已跳过自动启动并清理启动项");
    }
}
