using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatLogonStartup147_OnlyRestoresInstalledStoppedServerFromTrayStartup()
    {
        Assert.IsTrue(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: false, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: false, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: true));
    }

    [TestMethod]
    public void TomcatLogonStartup147_AppHooksOnlyTrayStartupAndKeepsManualCatalinaPath()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var startup = ReadRepositoryFile("TomcatLogonStartup.cs");

        StringAssert.Contains(app, "if (startInTray)");
        StringAssert.Contains(app, "TomcatLogonStartup.TryRestoreSharedTomcatAsync()");
        StringAssert.Contains(startup, "ProcessWindowStyle.Hidden");
        StringAssert.Contains(startup, "\"run\"");
        Assert.IsFalse(startup.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartProduct", StringComparison.OrdinalIgnoreCase));

        // Manual environment actions must remain visible and must not be silently
        // redirected to the logon-only startup path.
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "windowStyle: ProcessWindowStyle.Normal");
    }

    [TestMethod]
    public void TomcatLogonStartup147_DoesNotReviveLegacyPerProductRunRegistration()
    {
        var legacy = ReadRepositoryFile("TomcatProductStartupManager.cs");
        StringAssert.Contains(legacy, "RemoveRegistration();");
        StringAssert.Contains(legacy, "已跳过自动启动并清理启动项");
    }
}