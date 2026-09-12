using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatConsoleLifecycle148_RecognizesOnlyManagedLauncherOrExactTitle()
    {
        var launcher = Path.Combine(Path.GetTempPath(), "MCPanel", "run-tomcat-server.cmd");

        Assert.IsTrue(TomcatConsoleWindowManager.IsManagedConsole(
            $"cmd.exe /d /s /c \"{launcher}\"",
            string.Empty,
            launcher));
        Assert.IsTrue(TomcatConsoleWindowManager.IsManagedConsole(
            "cmd.exe /k echo test",
            TomcatConsoleWindowManager.ConsoleTitle,
            launcher));
        Assert.IsFalse(TomcatConsoleWindowManager.IsManagedConsole(
            "cmd.exe /k echo unrelated",
            "Administrator: Command Prompt",
            launcher));
    }

    [TestMethod]
    public void TomcatConsoleLifecycle148_StopClosesOldWindowAndLaunchPrunesStaleWindow()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var manager = ReadRepositoryFile("TomcatConsoleWindowManager.cs");

        var launchIndex = runtime.IndexOf("internal static void LaunchTomcatConsole", StringComparison.Ordinal);
        var retireIndex = runtime.IndexOf("private static void RetireLegacyTomcatWindowsService", StringComparison.Ordinal);
        Assert.IsTrue(launchIndex >= 0 && retireIndex > launchIndex);
        var launchSection = runtime.Substring(launchIndex, retireIndex - launchIndex);
        StringAssert.Contains(launchSection, "TomcatConsoleWindowManager.CloseExistingConsoleWindows();");
        StringAssert.Contains(launchSection, "title MCPanel Tomcat Server");
        StringAssert.Contains(launchSection, "call catalina.bat run");

        var stopIndex = runtime.IndexOf("public async Task<string> StopAsync", StringComparison.Ordinal);
        var restartIndex = runtime.IndexOf("public async Task<string> RestartAsync", StringComparison.Ordinal);
        Assert.IsTrue(stopIndex >= 0 && restartIndex > stopIndex);
        var stopSection = runtime.Substring(stopIndex, restartIndex - stopIndex);
        StringAssert.Contains(stopSection, "TomcatConsoleWindowManager.CloseExistingConsoleWindows();");
        StringAssert.Contains(stopSection, "旧 Catalina CMD 控制台已关闭");

        StringAssert.Contains(manager, "Win32_Process");
        StringAssert.Contains(manager, "run-tomcat-server.cmd");
        StringAssert.Contains(manager, "Process.GetProcessesByName(\"cmd\")");
        StringAssert.Contains(manager, "process.CloseMainWindow()");
        StringAssert.Contains(manager, "process.Kill()");
    }

    [TestMethod]
    public void TomcatConsoleLifecycle148_RestartStillStopsBeforeStartingVisibleConsole()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var restartIndex = runtime.IndexOf("public async Task<string> RestartAsync", StringComparison.Ordinal);
        var uninstallIndex = runtime.IndexOf("public Task<string> UninstallAsync", StringComparison.Ordinal);
        Assert.IsTrue(restartIndex >= 0 && uninstallIndex > restartIndex);
        var restartSection = runtime.Substring(restartIndex, uninstallIndex - restartIndex);

        var stopCall = restartSection.IndexOf("await StopAsync(kind, cancellationToken);", StringComparison.Ordinal);
        var startCall = restartSection.IndexOf("await StartAsync(kind, cancellationToken);", StringComparison.Ordinal);
        Assert.IsTrue(stopCall >= 0 && startCall > stopCall);
    }
}