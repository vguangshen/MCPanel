using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void SharedTomcatNormalStartUsesVisibleCmdConsole()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("public async Task<string> StartAsync", StringComparison.Ordinal);
        var tomcat = runtime.IndexOf("case EnvironmentKind.Tomcat:", start, StringComparison.Ordinal);
        var nginx = runtime.IndexOf("case EnvironmentKind.Nginx:", tomcat, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && tomcat > start && nginx > tomcat);
        var block = runtime.Substring(tomcat, nginx - tomcat);

        StringAssert.Contains(block, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(block, "LaunchTomcatStartConsole(tomcatRoot)");
        StringAssert.Contains(block, "StopAllProductInstancesAsync(cancellationToken)");
        Assert.IsFalse(block.Contains("LaunchTomcatConsole(tomcatRoot)", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("WaitForStartupAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedTomcatConsoleRunsCatalinaInVisibleNormalWindow()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("internal static void LaunchTomcatConsole", StringComparison.Ordinal);
        var end = runtime.IndexOf("private static void RetireLegacyTomcatWindowsService", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var block = runtime.Substring(start, end - start);

        StringAssert.Contains(block, "title MCPanel Tomcat Server");
        StringAssert.Contains(block, "call catalina.bat run");
        StringAssert.Contains(block, "ProcessWindowStyle.Normal");
        StringAssert.Contains(block, "pause >nul");
    }

    [TestMethod]
    public void TomcatInstallerDoesNotRegisterOrStartHiddenWindowsService()
    {
        var installer = ReadRepositoryFile("EnvironmentInstaller.cs");
        var start = installer.IndexOf("private async Task InstallTomcatAsync", StringComparison.Ordinal);
        var end = installer.IndexOf("private async Task InstallNginxAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var block = installer.Substring(start, end - start);

        StringAssert.Contains(block, "EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(block, "TomcatWindowsServiceManager.Delete()");
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EnvironmentCardKeepsDedicatedCatalinaStartButton()
    {
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        StringAssert.Contains(xaml, "Tag=\"CatalinaRun\"");
        StringAssert.Contains(xaml, "以 Catalina 方式启动");
        StringAssert.Contains(xaml, "Tag=\"Catalina\"");
    }
}
