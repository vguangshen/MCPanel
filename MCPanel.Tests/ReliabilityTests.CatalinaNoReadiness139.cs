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

        StringAssert.Contains(method, "ProcessRunner.StartFile");
        StringAssert.Contains(method, "call catalina.bat run");
        StringAssert.Contains(method, "不再等待端口或执行 HTTP 就绪诊断");
        Assert.IsFalse(method.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ReadHttpPorts", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ArePortsListening", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("Catalina 端口就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("真正 HTTP 就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("诊断模式", StringComparison.Ordinal));
    }
}
