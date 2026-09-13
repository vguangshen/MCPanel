using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatStartModeSplit150_NormalStartAndRestartUseStart_CatalinaUsesRun()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var mainWindow = ReadRepositoryFile("MainWindow.Environment.cs");

        var normalLauncherIndex = runtime.IndexOf(
            "internal static void LaunchTomcatStartConsole(string tomcatRoot)",
            StringComparison.Ordinal);
        var catalinaLauncherIndex = runtime.IndexOf(
            "internal static void LaunchTomcatConsole(string tomcatRoot)",
            StringComparison.Ordinal);
        var retireIndex = runtime.IndexOf(
            "private static void RetireLegacyTomcatWindowsService",
            StringComparison.Ordinal);
        Assert.IsTrue(normalLauncherIndex >= 0 && catalinaLauncherIndex > normalLauncherIndex && retireIndex > catalinaLauncherIndex);

        var normalLauncher = runtime.Substring(normalLauncherIndex, catalinaLauncherIndex - normalLauncherIndex);
        StringAssert.Contains(normalLauncher, "call startup.bat");
        StringAssert.Contains(normalLauncher, "set \"TITLE=MCPanel Tomcat Server\"");
        Assert.IsFalse(normalLauncher.Contains("call catalina.bat run", StringComparison.Ordinal));

        var catalinaLauncher = runtime.Substring(catalinaLauncherIndex, retireIndex - catalinaLauncherIndex);
        StringAssert.Contains(catalinaLauncher, "call catalina.bat run");
        StringAssert.Contains(catalinaLauncher, "pause >nul");

        var startIndex = runtime.IndexOf(
            "public async Task<string> StartAsync(EnvironmentKind kind",
            StringComparison.Ordinal);
        var stopIndex = runtime.IndexOf(
            "public async Task<string> StopAsync(EnvironmentKind kind",
            StringComparison.Ordinal);
        Assert.IsTrue(startIndex >= 0 && stopIndex > startIndex);
        var startSection = runtime.Substring(startIndex, stopIndex - startIndex);
        StringAssert.Contains(startSection, "LaunchTomcatStartConsole(tomcatRoot);");
        Assert.IsFalse(startSection.Contains("LaunchTomcatConsole(tomcatRoot);", StringComparison.Ordinal));

        var restartIndex = runtime.IndexOf(
            "public async Task<string> RestartAsync(EnvironmentKind kind",
            StringComparison.Ordinal);
        var uninstallIndex = runtime.IndexOf(
            "public Task<string> UninstallAsync",
            StringComparison.Ordinal);
        Assert.IsTrue(restartIndex >= 0 && uninstallIndex > restartIndex);
        var restartSection = runtime.Substring(restartIndex, uninstallIndex - restartIndex);
        var restartStop = restartSection.IndexOf("await StopAsync(kind, cancellationToken);", StringComparison.Ordinal);
        var restartStart = restartSection.IndexOf("await StartAsync(kind, cancellationToken);", StringComparison.Ordinal);
        Assert.IsTrue(restartStop >= 0 && restartStart > restartStop);
        Assert.IsFalse(restartSection.Contains("StartTomcatInCatalinaConsoleAsync", StringComparison.Ordinal));

        StringAssert.Contains(mainWindow, "\"Start\" => await _runtimeService.StartAsync(item.Kind)");
        StringAssert.Contains(mainWindow, "\"Restart\" => await _runtimeService.RestartAsync(item.Kind)");
        StringAssert.Contains(mainWindow, "\"CatalinaRun\" => await _runtimeService.StartTomcatInCatalinaConsoleAsync()");
    }
}