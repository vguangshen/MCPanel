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
    public void TomcatAndFrpServicesUseScmRecovery()
    {
        var tomcatRecovery = TomcatWindowsServiceManager.BuildRecoveryPolicyArguments();
        var tomcatFlag = TomcatWindowsServiceManager.BuildFailureFlagArguments();
        var frpRecovery = FrpWindowsServiceManager.BuildRecoveryPolicyArguments();
        var frpFlag = FrpWindowsServiceManager.BuildFailureFlagArguments();

        foreach (var command in new[] { tomcatRecovery, frpRecovery })
        {
            StringAssert.Contains(command, "reset=");
            StringAssert.Contains(command, "86400");
            StringAssert.Contains(command, "restart/5000/restart/15000/restart/30000");
        }
        StringAssert.Contains(tomcatFlag, "failureflag");
        StringAssert.Contains(tomcatFlag, "MCPanelTomcat");
        StringAssert.Contains(frpFlag, "failureflag");
        StringAssert.Contains(frpFlag, "MCPanelFrp");
    }

    [TestMethod]
    public void ServiceWatchdogsUseBoundedBackoffAndEscalation()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), TomcatWindowsService.GetRecoveryDelay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(3), TomcatWindowsService.GetRecoveryDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(10), TomcatWindowsService.GetRecoveryDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(30), TomcatWindowsService.GetRecoveryDelay(3));
        Assert.AreEqual(TimeSpan.FromSeconds(60), TomcatWindowsService.GetRecoveryDelay(4));
        Assert.AreEqual(TimeSpan.FromSeconds(60), TomcatWindowsService.GetRecoveryDelay(99));
        Assert.IsFalse(TomcatWindowsService.ShouldEscalate(TomcatWindowsService.MaxRecoveriesPerWindow - 1));
        Assert.IsTrue(TomcatWindowsService.ShouldEscalate(TomcatWindowsService.MaxRecoveriesPerWindow));
        Assert.AreEqual(TimeSpan.FromMinutes(10), TomcatWindowsService.RecoveryWindow);

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
