using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatRuntimePathsUseProcessesAndPortsInsteadOfScmRunningState()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var managerStart = services.IndexOf("internal static class TomcatWindowsServiceManager", StringComparison.Ordinal);
        var managerEnd = services.IndexOf("internal static class FrpWindowsServiceManager", managerStart, StringComparison.Ordinal);
        Assert.IsTrue(managerStart >= 0 && managerEnd > managerStart);
        var manager = services.Substring(managerStart, managerEnd - managerStart);
        Assert.IsFalse(manager.Contains("IsRunningForRoot", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("public static bool IsRunning()", StringComparison.Ordinal));
        StringAssert.Contains(manager, "StopWithoutStatusWait");
        StringAssert.Contains(manager, "DeleteWithoutStatusWait");

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(instances.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(instances, "IsSharedTomcatRunning()");
        StringAssert.Contains(instances, "ContainsJavaOptionPath(process.CommandLine, \"-Dcatalina.base\", home)");

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "共享 Tomcat 不再通过 Windows Service 隐藏启动");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));

        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(deployment, "TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot)");
        StringAssert.Contains(deployment, "await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);");
    }
}