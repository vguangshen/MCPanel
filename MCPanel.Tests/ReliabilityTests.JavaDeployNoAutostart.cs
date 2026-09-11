using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void JavaProductDeploymentDoesNotInspectOrReloadSharedTomcatService()
    {
        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(
            deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal),
            "Java 产品安装不应再根据 Windows Service Running 状态决定是否重载共享 Tomcat。");
        Assert.IsFalse(
            deployment.Contains("sharedServiceRestarted", StringComparison.Ordinal),
            "Java 产品安装不应保留共享服务自动重启分支。");
        StringAssert.Contains(deployment, "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。");
    }
}