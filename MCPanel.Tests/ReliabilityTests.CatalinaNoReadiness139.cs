using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void CatalinaConsoleLaunchDoesNotRunAutomaticReadinessDiagnostics()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("public Task<string> StartTomcatInCatalinaConsoleAsync", StringComparison.Ordinal);
        var end = runtime.IndexOf("public NginxRuntimeOptions GetNginxOptions()", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = runtime.Substring(start, end - start);

        StringAssert.Contains(method, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(method, "不隐藏启动，也不等待端口或执行 HTTP 就绪诊断");
        Assert.IsFalse(method.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ReadHttpPorts", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ArePortsListening", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("真正 HTTP 就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("诊断模式", StringComparison.Ordinal));

        var launcherStart = runtime.IndexOf("internal static void LaunchTomcatConsole", StringComparison.Ordinal);
        var launcherEnd = runtime.IndexOf("private static void RetireLegacyTomcatWindowsService", launcherStart, StringComparison.Ordinal);
        Assert.IsTrue(launcherStart >= 0 && launcherEnd > launcherStart);
        var launcher = runtime.Substring(launcherStart, launcherEnd - launcherStart);
        StringAssert.Contains(launcher, "ProcessRunner.StartFile");
        StringAssert.Contains(launcher, "call catalina.bat run");
        StringAssert.Contains(launcher, "ProcessWindowStyle.Normal");
    }
}