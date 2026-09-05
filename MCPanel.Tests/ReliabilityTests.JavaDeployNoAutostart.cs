using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void JavaProductDeploymentDoesNotReloadSharedTomcatAutomatically()
    {
        Assert.IsFalse(
            TomcatWindowsServiceManager.ShouldTreatAsRunningForRoot("DeployToTomcatAsync"),
            "Tomcat 产品部署只能写入配置，不能因为共享 Tomcat 原本正在运行就自动重启并加载新产品。");
        Assert.IsTrue(
            TomcatWindowsServiceManager.ShouldTreatAsRunningForRoot("UninstallAsync"),
            "仅产品部署路径应抑制共享 Tomcat 自动重载，其他运行状态检测保持原语义。");

        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        StringAssert.Contains(deployment, "if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatRoot))");
        StringAssert.Contains(deployment, "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。");
    }
}
