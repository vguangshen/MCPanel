using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatServiceImagePathTargetsSharedServerRoot()
    {
        var exe = Path.Combine(Path.GetTempPath(), "MCPanel", "MCPanel.exe");
        var root = Path.Combine(Path.GetTempPath(), "MCPanel", "Tomcat", "apache-tomcat-8.5.57");
        var imagePath = TomcatWindowsServiceManager.BuildServiceImagePath(exe, root);

        StringAssert.Contains(imagePath, TomcatWindowsServiceHost.ServiceArgument);
        StringAssert.Contains(imagePath, Path.GetFullPath(root));
        Assert.AreEqual("MCPanelTomcat", TomcatWindowsServiceManager.ServiceName);
    }

    [TestMethod]
    public void FrpServiceImagePathTargetsManagedApplicationRoot()
    {
        var exe = Path.Combine(Path.GetTempPath(), "MCPanel", "MCPanel.exe");
        var root = Path.Combine(Path.GetTempPath(), "MCPanel");
        var imagePath = FrpWindowsServiceManager.BuildServiceImagePath(exe, root);

        StringAssert.Contains(imagePath, FrpWindowsServiceHost.ServiceArgument);
        StringAssert.Contains(imagePath, Path.GetFullPath(root));
        Assert.AreEqual("MCPanelFrp", FrpWindowsServiceManager.ServiceName);
    }

    [TestMethod]
    public void TomcatDisablesScmRecoveryWhileFrpKeepsIt()
    {
        var tomcatRecovery = ManagedWindowsServiceController.BuildDisableRecoveryPolicyArguments(
            TomcatWindowsServiceManager.ServiceName);
        var tomcatFlag = ManagedWindowsServiceController.BuildFailureFlagArguments(
            TomcatWindowsServiceManager.ServiceName,
            enabled: false);
        var frpRecovery = FrpWindowsServiceManager.BuildRecoveryPolicyArguments();
        var frpFlag = FrpWindowsServiceManager.BuildFailureFlagArguments();

        StringAssert.Contains(tomcatRecovery, "reset=");
        StringAssert.Contains(tomcatRecovery, "actions=");
        Assert.IsFalse(tomcatRecovery.Contains("restart/", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(tomcatFlag, "failureflag");
        StringAssert.Contains(tomcatFlag, "MCPanelTomcat");
        Assert.IsTrue(tomcatFlag.TrimEnd().EndsWith("0", StringComparison.Ordinal));

        StringAssert.Contains(frpRecovery, "reset=");
        StringAssert.Contains(frpRecovery, "86400");
        StringAssert.Contains(frpRecovery, "restart/5000/restart/15000/restart/30000");
        StringAssert.Contains(frpFlag, "failureflag");
        StringAssert.Contains(frpFlag, "MCPanelFrp");
    }

    [TestMethod]
    public void TomcatServiceContainsNoWatchdogOrAutomaticRestartLoop()
    {
        var source = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var tomcatStart = source.IndexOf("internal sealed class TomcatWindowsService", StringComparison.Ordinal);
        var frpStart = source.IndexOf("internal sealed class FrpWindowsService", StringComparison.Ordinal);
        Assert.IsTrue(tomcatStart >= 0 && frpStart > tomcatStart);

        var tomcatSection = source.Substring(tomcatStart, frpStart - tomcatStart);
        Assert.IsFalse(tomcatSection.Contains("WatchdogLoop", StringComparison.Ordinal));
        Assert.IsFalse(tomcatSection.Contains("_watchdog", StringComparison.Ordinal));
        Assert.IsFalse(tomcatSection.Contains("watchdog recovery", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(tomcatSection.Contains("RecoveryDelays", StringComparison.Ordinal));
        Assert.IsFalse(tomcatSection.Contains("MaxRecoveriesPerWindow", StringComparison.Ordinal));
        StringAssert.Contains(tomcatSection, "automatic recovery disabled");
    }

    [TestMethod]
    public void FrpServiceWatchdogStillUsesBoundedBackoffAndEscalation()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), FrpWindowsService.GetRecoveryDelay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(60), FrpWindowsService.GetRecoveryDelay(99));
        Assert.IsFalse(FrpWindowsService.ShouldEscalate(FrpWindowsService.MaxRecoveriesPerWindow - 1));
        Assert.IsTrue(FrpWindowsService.ShouldEscalate(FrpWindowsService.MaxRecoveriesPerWindow));
        Assert.AreEqual(TimeSpan.FromMinutes(10), FrpWindowsService.RecoveryWindow);
    }

    [TestMethod]
    public void ServiceDiagnosticsStayInsideManagedRoots()
    {
        var appRoot = Path.Combine(Path.GetTempPath(), "MCPanel-Service136");
        var tomcatRoot = Path.Combine(appRoot, "Tomcat", "apache-tomcat-8.5.57");

        Assert.AreEqual(
            Path.Combine(tomcatRoot, "logs", "mcpanel-service.log"),
            TomcatWindowsService.GetServiceLogPath(tomcatRoot));
        Assert.AreEqual(
            Path.Combine(appRoot, "Frp", "frp-service.log"),
            FrpWindowsService.GetServiceLogPath(appRoot));
    }

    [TestMethod]
    public void LegacyTomcatProductRestoreCommandIsCleanupOnly()
    {
        Assert.IsTrue(TomcatProductStartupManager.IsRestoreRequest(new[] { "--restore-tomcat-products" }));
        Assert.IsFalse(TomcatProductStartupManager.IsRestoreRequest(Array.Empty<string>()));
    }
}
