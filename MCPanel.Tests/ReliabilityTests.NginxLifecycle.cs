using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void NginxServiceRecoveryPolicyUsesLayeredRestartDelays()
    {
        var failure = NginxWindowsServiceManager.BuildRecoveryPolicyArguments();
        var failureFlag = NginxWindowsServiceManager.BuildFailureFlagArguments();

        StringAssert.Contains(failure, "failure");
        StringAssert.Contains(failure, "nginx");
        StringAssert.Contains(failure, "reset=");
        StringAssert.Contains(failure, "86400");
        StringAssert.Contains(failure, "restart/5000/restart/15000/restart/30000");
        StringAssert.Contains(failureFlag, "failureflag");
        StringAssert.Contains(failureFlag, "nginx");
        StringAssert.Contains(failureFlag, "1");
    }

    [TestMethod]
    public void NginxWatchdogBackoffAndEscalationAreBounded()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(1), NginxWindowsService.GetRecoveryDelay(0));
        Assert.AreEqual(TimeSpan.FromSeconds(3), NginxWindowsService.GetRecoveryDelay(1));
        Assert.AreEqual(TimeSpan.FromSeconds(10), NginxWindowsService.GetRecoveryDelay(2));
        Assert.AreEqual(TimeSpan.FromSeconds(30), NginxWindowsService.GetRecoveryDelay(3));
        Assert.AreEqual(TimeSpan.FromSeconds(60), NginxWindowsService.GetRecoveryDelay(4));
        Assert.AreEqual(TimeSpan.FromSeconds(60), NginxWindowsService.GetRecoveryDelay(99));

        Assert.IsFalse(NginxWindowsService.ShouldEscalateWatchdog(
            NginxWindowsService.MaxWatchdogRecoveriesPerWindow - 1));
        Assert.IsTrue(NginxWindowsService.ShouldEscalateWatchdog(
            NginxWindowsService.MaxWatchdogRecoveriesPerWindow));
        Assert.AreEqual(TimeSpan.FromMinutes(10), NginxWindowsService.WatchdogRecoveryWindow);
    }

    [TestMethod]
    public void NginxWatchdogLogStaysInsideManagedNginxRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel-Nginx-Test", "nginx-1.14.2");
        var log = NginxWindowsService.GetServiceLogPath(root);

        Assert.AreEqual(
            Path.Combine(root, "logs", "mcpanel-service.log"),
            log);
    }
}
