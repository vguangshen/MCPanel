$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $repoRoot $Path), $Text, $utf8NoBom)
}

function Replace-RegexOnce([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Description) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one match while $Description, found $($matches.Count)."
    }
    [regex]::Replace($Text, $Pattern, $Replacement)
}

Write-Host '1/5 Converting independent product startup from hidden output capture to a visible console...'
$managerPath = 'TomcatProductInstanceManager.cs'
$manager = Read-RepoText $managerPath

$oldCall = '            await StartTomcatAsync(tomcatHome, instanceRoot, cancellationToken);'
$newCall = '            await StartTomcatAsync(tomcatHome, instanceRoot, productId, cancellationToken);'
if (-not $manager.Contains($oldCall)) {
    throw 'Unable to locate independent Tomcat StartTomcatAsync call.'
}
$manager = $manager.Replace($oldCall, $newCall)

$startMethodPattern = '(?ms)    private static async Task StartTomcatAsync\(string tomcatHome, string catalinaBase, CancellationToken cancellationToken\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    internal static ProcessStartInfo BuildTomcatJavaStartInfo'
$startMethodReplacement = @'
    private static async Task StartTomcatAsync(
        string tomcatHome,
        string catalinaBase,
        string productId,
        CancellationToken cancellationToken)
    {
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
            catalinaBase,
            useInstanceLocalErrorFile: true);

        // Product-level starts intentionally use a visible console, matching the
        // original Store workflow where operators can watch Tomcat load in real time.
        // We still launch Java directly instead of supplier catalina.bat because some
        // supplier scripts overwrite CATALINA_BASE and accidentally load shared config.
        var startInfo = BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: false);
        using var launcher = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法打开 {SafeName(productId)} 的 Tomcat 单应用启动窗口。");

        await Task.Delay(300, cancellationToken);
        if (launcher.HasExited)
        {
            var exitCode = launcher.ExitCode;
            var log = ReadRecentTomcatLog(catalinaBase);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
                ? $"Tomcat 启动进程提前退出，退出码：{exitCode}。"
                : $"Tomcat 启动进程提前退出，退出码：{exitCode}。最近日志：{Environment.NewLine}{log}");
        }
    }

    internal static ProcessStartInfo BuildTomcatJavaStartInfo
'@
$manager = Replace-RegexOnce $manager $startMethodPattern $startMethodReplacement.TrimEnd("`r", "`n") 'replacing hidden independent startup'

Write-Host '2/5 Removing the now-unused hidden stdout/stderr capture pipeline...'
$capturePattern = '(?ms)    private static async Task CaptureTomcatOutputAsync\(Process process, string consoleLog\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    private static async Task StopInstanceAsync'
$manager = Replace-RegexOnce $manager $capturePattern '    private static async Task StopInstanceAsync' 'removing hidden Tomcat console capture helpers'
Write-RepoText $managerPath $manager

Write-Host '3/5 Adding regression coverage for visible product starts and hidden shared server startup...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatVisibleProductStart114.cs'
$testContent = @'
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatProductStart_IndependentAndCatalinaUseVisibleConsole_WhileSharedServerStaysHidden()
    {
        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var processRunner = ReadRepositoryFile("ProcessRunner.cs");

        StringAssert.Contains(manager,
            "await StartTomcatAsync(tomcatHome, instanceRoot, productId, cancellationToken);");
        StringAssert.Contains(manager,
            "BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: false)");
        StringAssert.Contains(manager,
            "BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false)");
        StringAssert.Contains(manager, "CreateNoWindow = redirectOutput");
        StringAssert.Contains(manager,
            "WindowStyle = redirectOutput ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal");

        Assert.IsFalse(manager.Contains(
            "BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: true)",
            StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("CaptureTomcatOutputAsync(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("PumpTomcatReaderAsync(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("DisposeProcessAfterCaptureAsync(", StringComparison.Ordinal));

        // Shared Tomcat remains the opposite: managed by the background Windows service.
        StringAssert.Contains(runtime, "TomcatWindowsServiceManager.Start");
        StringAssert.Contains(processRunner,
            "ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden");
    }
}
'@
Write-RepoText $testPath ($testContent.TrimStart("`r", "`n"))

Write-Host '4/5 Bumping MCPanel to 1.3.14...'
$projectPath = 'MCPanel.csproj'
$project = Read-RepoText $projectPath
$project = $project.Replace('<Version>1.3.13</Version>', '<Version>1.3.14</Version>')
$project = $project.Replace('<FileVersion>1.3.13.0</FileVersion>', '<FileVersion>1.3.14.0</FileVersion>')
$project = $project.Replace('<AssemblyVersion>1.3.13.0</AssemblyVersion>', '<AssemblyVersion>1.3.14.0</AssemblyVersion>')
if (-not $project.Contains('<Version>1.3.14</Version>')) {
    throw 'Version bump to 1.3.14 failed.'
}
Write-RepoText $projectPath $project

Write-Host '5/5 Auditing final source invariants...'
$manager = Read-RepoText $managerPath
foreach ($required in @(
    'StartTomcatAsync(tomcatHome, instanceRoot, productId, cancellationToken)',
    'BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: false)',
    'BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false)',
    'WindowStyle = redirectOutput ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal'
)) {
    if (-not $manager.Contains($required)) {
        throw "Missing visible-start invariant: $required"
    }
}
foreach ($retired in @(
    'BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: true)',
    'CaptureTomcatOutputAsync(',
    'PumpTomcatReaderAsync(',
    'DisposeProcessAfterCaptureAsync('
)) {
    if ($manager.Contains($retired)) {
        throw "Hidden product-start code still remains: $retired"
    }
}

$runtime = Read-RepoText 'EnvironmentRuntimeService.cs'
if (-not $runtime.Contains('TomcatWindowsServiceManager.Start')) {
    throw 'Shared Tomcat Windows Service startup path was unexpectedly changed.'
}

Write-Host 'Tomcat product visible-start migration completed successfully.'
