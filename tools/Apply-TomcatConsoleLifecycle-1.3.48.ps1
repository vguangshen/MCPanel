$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = Get-Content -LiteralPath $Path -Raw
    $first = $text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Could not find expected source block: $Description"
    }
    if ($text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected source block is not unique: $Description"
    }

    $updated = $text.Substring(0, $first) + $New + $text.Substring($first + $Old.Length)
    [IO.File]::WriteAllText((Resolve-Path $Path), $updated, [Text.UTF8Encoding]::new($true))
}

$runtime = 'EnvironmentRuntimeService.cs'

$launchOld = @'
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
'@
$launchNew = @'
        // A previous Catalina console can remain at the post-run pause after
        // Tomcat has stopped. Close only MCPanel-owned launcher windows before
        // opening a fresh shared-server console so repeated starts/restarts do
        // not accumulate stale CMD windows.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
'@
Replace-ExactlyOnce -Path $runtime -Old $launchOld -New $launchNew -Description 'close stale Tomcat console before launch'

$stopOld = @'
                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    throw new InvalidOperationException("Tomcat Server 共享进程未能停止；单应用实例未受影响。请检查共享 Tomcat Java 进程。");
                }

                return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
'@
$stopNew = @'
                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    throw new InvalidOperationException("Tomcat Server 共享进程未能停止；单应用实例未受影响。请检查共享 Tomcat Java 进程。");
                }

                // catalina.bat run returns to run-tomcat-server.cmd after the
                // Java process exits; that wrapper deliberately pauses so an
                // unexpected crash remains diagnosable. A deliberate Stop or
                // Restart must close the old managed console as well.
                TomcatConsoleWindowManager.CloseExistingConsoleWindows();
                return "Tomcat Server 已停止，旧 Catalina CMD 控制台已关闭；单应用 Tomcat 实例不受影响。";
'@
Replace-ExactlyOnce -Path $runtime -Old $stopOld -New $stopNew -Description 'close Tomcat console after shared runtime stop'

$manager = @'
using System.Diagnostics;
using System.Management;
using System.IO;

namespace MCPanel;

/// <summary>
/// Owns the visible CMD wrapper used by the shared Tomcat Catalina console.
/// It intentionally targets only MCPanel's run-tomcat-server.cmd launcher (or
/// its exact console title) so unrelated command prompts are never touched.
/// </summary>
internal static class TomcatConsoleWindowManager
{
    internal const string ConsoleTitle = "MCPanel Tomcat Server";

    internal static string LauncherPath =>
        Path.Combine(ComponentPaths.WorkRoot, "run-tomcat-server.cmd");

    internal static bool IsManagedConsole(
        string? commandLine,
        string? windowTitle,
        string launcherPath)
    {
        var fullLauncher = Path.GetFullPath(launcherPath);
        var commandMatches = !string.IsNullOrWhiteSpace(commandLine) &&
                             commandLine.IndexOf(fullLauncher, StringComparison.OrdinalIgnoreCase) >= 0;
        var titleMatches = string.Equals(
            windowTitle?.Trim(),
            ConsoleTitle,
            StringComparison.OrdinalIgnoreCase);
        return commandMatches || titleMatches;
    }

    public static void CloseExistingConsoleWindows()
    {
        var launcher = LauncherPath;
        foreach (var process in Process.GetProcessesByName("cmd"))
        {
            using (process)
            {
                string? title = null;
                try
                {
                    process.Refresh();
                    title = process.MainWindowTitle;
                }
                catch
                {
                }

                var commandLine = TryReadCommandLine(process.Id);
                if (!IsManagedConsole(commandLine, title, launcher))
                {
                    continue;
                }

                CloseProcess(process);
            }
        }
    }

    private static string? TryReadCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    return item["CommandLine"]?.ToString();
                }
            }
        }
        catch
        {
            // WMI command-line access can be restricted. The exact console
            // title remains a compatibility fallback for those machines.
        }

        return null;
    }

    private static void CloseProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (process.CloseMainWindow() && process.WaitForExit(1200))
            {
                return;
            }
        }
        catch
        {
        }

        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1200);
            }
        }
        catch
        {
            // Console cleanup is best effort. The Tomcat runtime has already
            // been stopped before this path is used by Stop/Restart.
        }
    }
}
'@
[IO.File]::WriteAllText((Join-Path (Get-Location) 'TomcatConsoleWindowManager.cs'), $manager, [Text.UTF8Encoding]::new($true))

$tests = @'
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
'@
[IO.File]::WriteAllText((Join-Path (Get-Location) 'MCPanel.Tests/ReliabilityTests.TomcatConsoleLifecycle148.cs'), $tests, [Text.UTF8Encoding]::new($true))

$notesPath = 'RELEASE-NOTES.md'
$notes = Get-Content -LiteralPath $notesPath -Raw
$bullet = '- 手动“停止 / 重启 Tomcat”现在会在共享 Java 进程退出后同步关闭旧的 `MCPanel Tomcat Server` CMD；再次启动前也会清理残留的 MCPanel Tomcat 控制台，避免多次重启后遗留旧窗口。'
if ($notes -notmatch [regex]::Escape($bullet)) {
    $notes = $notes.TrimEnd() + "`r`n" + $bullet + "`r`n"
    [IO.File]::WriteAllText((Resolve-Path $notesPath), $notes, [Text.UTF8Encoding]::new($true))
}

Write-Host 'MCPanel 1.3.48 Tomcat console lifecycle patch applied.'
