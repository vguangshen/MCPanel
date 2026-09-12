$ErrorActionPreference = 'Stop'

function Replace-ExactlyOnce {
    param(
        [Parameter(Mandatory = $true)][string]$Text,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $first = $Text.IndexOf($Old, [StringComparison]::Ordinal)
    if ($first -lt 0) {
        throw "Could not find expected source block: $Description"
    }
    if ($Text.IndexOf($Old, $first + $Old.Length, [StringComparison]::Ordinal) -ge 0) {
        throw "Expected source block is not unique: $Description"
    }

    return $Text.Substring(0, $first) + $New + $Text.Substring($first + $Old.Length)
}

$runtimePath = 'EnvironmentRuntimeService.cs'
$runtime = Get-Content -LiteralPath $runtimePath -Raw

$launchMarker = '    internal static void LaunchTomcatConsole(string tomcatRoot)'
if ($runtime.Contains('internal static void LaunchTomcatStartConsole(string tomcatRoot)', [StringComparison]::Ordinal)) {
    throw 'LaunchTomcatStartConsole already exists; aborting one-time migration.'
}

$ordinaryLauncher = @'
    internal static void LaunchTomcatStartConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var startup = Path.Combine(binDirectory, "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("未找到 Tomcat 普通启动脚本。", startup);
        }

        // Normal Start/Restart uses Tomcat's standard `start` semantics. Keep
        // the explicit Catalina entry point separate so only that operation is
        // tied to `catalina.bat run` and its diagnostic console lifecycle.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "start-tomcat-server.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Startup
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            set "TITLE=MCPanel Tomcat Server"
            cd /d "{binDirectory}"
            call startup.bat
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var tomcatWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);
    }

'@
$runtime = Replace-ExactlyOnce -Text $runtime -Old $launchMarker -New ($ordinaryLauncher + $launchMarker) -Description 'Tomcat console launcher insertion point'

$startIndex = $runtime.IndexOf('    public async Task<string> StartAsync(EnvironmentKind kind', [StringComparison]::Ordinal)
$stopIndex = $runtime.IndexOf('    public async Task<string> StopAsync(EnvironmentKind kind', [StringComparison]::Ordinal)
if ($startIndex -lt 0 -or $stopIndex -le $startIndex) {
    throw 'Unable to isolate EnvironmentRuntimeService.StartAsync.'
}

$startSection = $runtime.Substring($startIndex, $stopIndex - $startIndex)
$startSection = Replace-ExactlyOnce -Text $startSection -Old '                LaunchTomcatConsole(tomcatRoot);' -New '                LaunchTomcatStartConsole(tomcatRoot);' -Description 'normal Tomcat Start launcher'
$startSection = Replace-ExactlyOnce -Text $startSection -Old '                return $"Tomcat Server CMD 控制台已打开；共享 Tomcat 不再通过 Windows Service 隐藏启动。请在控制台观察 Catalina 输出，日志目录：{Path.Combine(tomcatRoot, "logs")}";' -New '                return $"Tomcat Server 已按标准 start 模式启动，普通 Tomcat CMD 窗口已打开。需要持续查看 Catalina 前台输出时请使用“以 Catalina 方式启动”。日志目录：{Path.Combine(tomcatRoot, "logs")}";' -Description 'normal Tomcat Start status text'
$runtime = $runtime.Substring(0, $startIndex) + $startSection + $runtime.Substring($stopIndex)

[IO.File]::WriteAllText((Resolve-Path $runtimePath), $runtime, [Text.UTF8Encoding]::new($true))

$projectPath = 'MCPanel.csproj'
$project = Get-Content -LiteralPath $projectPath -Raw
$project = Replace-ExactlyOnce -Text $project -Old '<Version>1.3.49</Version>' -New '<Version>1.3.50</Version>' -Description 'project Version'
$project = Replace-ExactlyOnce -Text $project -Old '<FileVersion>1.3.49.0</FileVersion>' -New '<FileVersion>1.3.50.0</FileVersion>' -Description 'project FileVersion'
$project = Replace-ExactlyOnce -Text $project -Old '<AssemblyVersion>1.3.49.0</AssemblyVersion>' -New '<AssemblyVersion>1.3.50.0</AssemblyVersion>' -Description 'project AssemblyVersion'
[IO.File]::WriteAllText((Resolve-Path $projectPath), $project, [Text.UTF8Encoding]::new($true))

$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatStartModeSplit150.cs'
$test = @'
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
        Assert.IsFalse(normalLauncher.Contains("catalina.bat run", StringComparison.Ordinal));

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
'@
[IO.File]::WriteAllText((Join-Path (Get-Location) $testPath), $test, [Text.UTF8Encoding]::new($true))

$notes = @'
# MCPanel 1.3.50

- 修正环境页 Tomcat Server 的普通“启动”语义：不再复用 `catalina.bat run`，改为通过标准 `startup.bat` / `start` 链路打开普通 Tomcat CMD 窗口。
- “以 Catalina 方式启动”继续独立使用 `catalina.bat run`，保留前台持续输出以及退出后暂停查看错误的诊断行为。
- “重启”保持先停止再调用普通 `StartAsync`，因此重启后同样回到标准 `start` 模式，不会再误入 Catalina `run`。
- 普通启动与 Catalina 启动继续共享同一个 Tomcat 安装目录与配置，不改变产品端口、绑定或现有停止逻辑。
- 新增回归测试锁定 Start / Restart / Catalina 三条入口，防止后续再次合并。
'@
[IO.File]::WriteAllText((Resolve-Path 'RELEASE-NOTES.md'), $notes, [Text.UTF8Encoding]::new($true))

Write-Host 'Tomcat start/Catalina split migration prepared for MCPanel 1.3.50.'
