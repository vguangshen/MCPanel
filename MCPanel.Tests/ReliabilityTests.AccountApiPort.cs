using System.Collections.Generic;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void IisPoolNeverTakesTheAccountApiPortEvenWhenItIsFree()
    {
        // After a full reinstall the Account API starts switched off, so 8088 is
        // free when the first IIS product is deployed. It must still be skipped.
        Assert.AreEqual(8089, ProductDeploymentService.SelectFreeIisPort(new HashSet<int>(), reservedPort: 8088));
        Assert.AreEqual(8088, ProductDeploymentService.SelectFreeIisPort(new HashSet<int>(), reservedPort: 9100),
            "With the API moved elsewhere, 8088 is an ordinary IIS port again.");
        Assert.AreEqual(8090, ProductDeploymentService.SelectFreeIisPort(new HashSet<int> { 8089 }, reservedPort: 8088));
    }

    [TestMethod]
    public void NginxCandidatesSkipTheAccountApiPort()
    {
        var reserved = AccountApiManagerService.ReservedPort();
        var candidates = NginxRuntimeManager.BuildPortCandidates();
        Assert.IsFalse(candidates.Contains(reserved), $"Nginx must not try the Account API port {reserved}.");
        Assert.IsTrue(candidates.Contains(NginxRuntimeManager.DefaultListenPort));
    }

    [TestMethod]
    public void AccountApiPageExplainsWhoElseListensOnItsPort()
    {
        Assert.AreEqual(string.Empty, AccountApiManagerService.DescribePortConflict(8088, new[] { 8089, 8090 }, 72));

        var iis = AccountApiManagerService.DescribePortConflict(8088, new[] { 8088 }, 72);
        StringAssert.Contains(iis, "端口 8088");
        StringAssert.Contains(iis, "IIS 网站 MCPanel");
        StringAssert.Contains(iis, "同时被 IIS 网站 MCPanel 使用", "Only IIS is named as the other owner.");

        var both = AccountApiManagerService.DescribePortConflict(8088, new[] { 8088 }, 8088);
        StringAssert.Contains(both, "IIS 网站 MCPanel、Nginx");
        StringAssert.Contains(both, "自动改用其他端口");
    }

    [TestMethod]
    public void AccountApiReservesItsDefaultPortWithoutAConfigFile() =>
        Assert.IsTrue(AccountApiManagerService.ReservedPort() is > 0 and <= 65535);
}
