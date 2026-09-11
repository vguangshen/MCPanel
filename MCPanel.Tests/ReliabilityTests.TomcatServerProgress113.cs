using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatServerStartup_DoesNotPollApplicationReadinessIntoEnvironmentProgress()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "EnvironmentViewModels.cs"));

        Assert.IsFalse(runtime.Contains("progressPorts.Count(TomcatRuntimeProbe.IsPortListening)", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在加载应用：{ready}/{total}", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在验证 Tomcat 端口稳定监听", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatWindowsServiceManager.Start();");
        StringAssert.Contains(runtime, "不再等待 Windows 服务状态或读取启动进度");
    }

    [TestMethod]
    public void TomcatRuntimeState_UsesProcessAndPortsInsteadOfScmStatus()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var methodStart = runtime.IndexOf("private static EnvironmentRuntimeState GetTomcatState", StringComparison.Ordinal);
        var methodEnd = runtime.IndexOf("private static EnvironmentRuntimeState GetSqlServerState", methodStart, StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0 && methodEnd > methodStart);
        var method = runtime.Substring(methodStart, methodEnd - methodStart);

        StringAssert.Contains(method, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(method, "TomcatRuntimeProbe.ArePortsListening");
        Assert.IsFalse(method.Contains("TomcatWindowsServiceManager.IsRunning", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("serviceRunning", StringComparison.Ordinal));
    }
}