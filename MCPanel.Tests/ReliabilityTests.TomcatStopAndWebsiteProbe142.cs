using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteManualHttpProbeIsRemoved()
    {
        var code = ReadRepositoryFile("MainWindow.Websites.cs");
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        Assert.IsFalse(code.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WebsiteCheckClient", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_websiteChecks", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("检测访问", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnknownPortOwnerIsNotTreatedAsJava()
    {
        var code = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var start = code.IndexOf("private static bool IsJavaPortListening", StringComparison.Ordinal);
        var end = code.IndexOf("private static async Task<string> PrepareInstanceAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "processIds.Count > 0 && processIds.Any(IsJavaProcessId)");
        Assert.IsFalse(method.Contains("processIds.Count == 0 ||", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EnvironmentTomcatStateTracksSharedServerNotIndependentInstances()
    {
        var code = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = code.IndexOf("private static EnvironmentRuntimeState GetTomcatState", StringComparison.Ordinal);
        var end = code.IndexOf("private static EnvironmentRuntimeState GetSqlServerState", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "var anyRunning = sharedProcessRunning;");
        StringAssert.Contains(method, "一个或多个应用正在单独运行，请在“网站”页面管理");
        Assert.IsFalse(method.Contains("var anyRunning = sharedRunning || managedRunning;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatStopDoesNotWaitTwentySecondsOnScm()
    {
        var code = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = code.IndexOf("public async Task<string> StopAsync", StringComparison.Ordinal);
        var end = code.IndexOf("public async Task<string> RestartAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "TomcatWindowsServiceManager.Stop()");
        StringAssert.Contains(method, "StopSharedTomcatAsync(cancellationToken)");
        Assert.IsFalse(method.Contains("attempt < 80", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("Task.Delay(250", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatServiceNormalStopDoesNotRunShutdownBat()
    {
        var code = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var start = code.IndexOf("protected override void OnStop()", StringComparison.Ordinal);
        var end = code.IndexOf("protected override void OnShutdown()", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "runtime cleanup delegated to MCPanel controller");
        Assert.IsFalse(method.Contains("StopTomcat", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("shutdown.bat", StringComparison.Ordinal));
    }
}