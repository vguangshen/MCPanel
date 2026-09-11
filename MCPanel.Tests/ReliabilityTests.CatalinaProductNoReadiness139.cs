using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ProductCatalinaLaunchDoesNotRunAutomaticReadinessDiagnostics()
    {
        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var start = instances.IndexOf("if (catalinaMode)", StringComparison.Ordinal);
        var end = instances.IndexOf("await StartTomcatAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var branch = instances.Substring(start, end - start);

        StringAssert.Contains(branch, "StartCatalinaConsole");
        Assert.IsFalse(branch.Contains("WaitForPortAsync", StringComparison.Ordinal));
        Assert.IsFalse(branch.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(branch.Contains("EnsureStableAsync", StringComparison.Ordinal));
        Assert.IsFalse(branch.Contains("ReadHttpPorts", StringComparison.Ordinal));
        Assert.IsFalse(instances.Contains("Catalina 诊断窗口", StringComparison.Ordinal));
    }
}
