$ErrorActionPreference = 'Stop'

function Replace-RegressionBlock {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = Get-Content -LiteralPath $Path -Raw
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "Expected exactly one $Description block in $Path, found $count."
    }

    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText((Resolve-Path $Path), $text, [Text.UTF8Encoding]::new($true))
    Write-Host "Aligned $Path: $Description."
}

$path = 'MCPanel.Tests/ReliabilityTests.TomcatVisibleProductStart114.cs'
$old = @'
        // Shared Tomcat now also opens a visible Catalina CMD console instead of
        // being launched as a hidden Windows service.
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "ProcessWindowStyle.Normal");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
'@
$new = @'
        // Shared Tomcat remains visible, but normal Start/Restart and explicit
        // Catalina diagnostics now intentionally use different launch semantics.
        StringAssert.Contains(runtime, "internal static void LaunchTomcatStartConsole(string tomcatRoot)");
        StringAssert.Contains(runtime, "call startup.bat");
        StringAssert.Contains(runtime, "call catalina.bat run");
        StringAssert.Contains(runtime, "ProcessWindowStyle.Normal");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
'@
Replace-RegressionBlock -Path $path -Old $old -New $new -Description 'legacy visible-shared-Tomcat regression'

$path143 = 'MCPanel.Tests/ReliabilityTests.VisibleSharedTomcat143.cs'
$old143 = @'
        StringAssert.Contains(block, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(block, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(block, "StopAllProductInstancesAsync(cancellationToken)");
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("startup.bat", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("WaitForStartupAsync", StringComparison.Ordinal));
'@
$new143 = @'
        StringAssert.Contains(block, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(block, "LaunchTomcatStartConsole(tomcatRoot)");
        StringAssert.Contains(block, "StopAllProductInstancesAsync(cancellationToken)");
        Assert.IsFalse(block.Contains("LaunchTomcatConsole(tomcatRoot)", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("WaitForStartupAsync", StringComparison.Ordinal));
'@
Replace-RegressionBlock -Path $path143 -Old $old143 -New $new143 -Description 'legacy normal-start regression'

$path137 = 'MCPanel.Tests/ReliabilityTests.TomcatServiceDecoupling137.cs'
$old137 = @'
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "共享 Tomcat 不再通过 Windows Service 隐藏启动");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));
'@
$new137 = @'
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "Tomcat Server 已按标准 start 模式启动");
        StringAssert.Contains(runtime, "LaunchTomcatStartConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));
'@
Replace-RegressionBlock -Path $path137 -Old $old137 -New $new137 -Description 'Tomcat service-decoupling start-mode regression'

$path113 = 'MCPanel.Tests/ReliabilityTests.TomcatServerProgress113.cs'
$old113 = @'
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "共享 Tomcat 不再通过 Windows Service 隐藏启动");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
'@
$new113 = @'
        StringAssert.Contains(runtime, "LaunchTomcatStartConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "Tomcat Server 已按标准 start 模式启动");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
'@
Replace-RegressionBlock -Path $path113 -Old $old113 -New $new113 -Description 'Tomcat environment-start progress regression'

Write-Host 'Aligned legacy Tomcat regressions with the 1.3.50 normal-start/Catalina split.'