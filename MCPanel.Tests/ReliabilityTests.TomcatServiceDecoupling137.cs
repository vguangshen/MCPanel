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
        StringAssert.Contains(manager, "StartWithoutStatusWait");
        StringAssert.Contains(manager, "StopWithoutStatusWait");
        StringAssert.Contains(manager, "DeleteWithoutStatusWait");

        var serviceStart = services.IndexOf("internal sealed class TomcatWindowsService", StringComparison.Ordinal);
        var serviceEnd = services.IndexOf("internal sealed class FrpWindowsService", serviceStart, StringComparison.Ordinal);
        Assert.IsTrue(serviceStart >= 0 && serviceEnd > serviceStart);
        var service = services.Substring(serviceStart, serviceEnd - serviceStart);
        Assert.IsFalse(service.Contains("WaitForStartupAsync", StringComparison.Ordinal));

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(instances.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(instances, "IsSharedTomcatRunning()");
        StringAssert.Contains(instances, "ContainsJavaOptionPath(process.CommandLine, \"-Dcatalina.base\", home)");

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "不再等待 Windows 服务状态或读取启动进度");

        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(deployment, "TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot)");
        StringAssert.Contains(deployment, "await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);");

        StringAssert.Contains(runtime, "var startup = Path.Combine(tomcatRoot, \"bin\", \"startup.bat\")");
        StringAssert.Contains(runtime, "运行状态按实际 Java 进程判断");
        StringAssert.Contains(runtime, "catch (Exception serviceError)");
    }
}