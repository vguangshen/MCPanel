$ErrorActionPreference = 'Stop'

$path = 'MCPanel.Tests/ReliabilityTests.TomcatVisibleProductStart114.cs'
$text = Get-Content -LiteralPath $path -Raw

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

$count = ([regex]::Matches($text, [regex]::Escape($old))).Count
if ($count -ne 1) {
    throw "Expected exactly one legacy visible-shared-Tomcat regression block, found $count."
}

$text = $text.Replace($old, $new)
[IO.File]::WriteAllText((Resolve-Path $path), $text, [Text.UTF8Encoding]::new($true))
Write-Host 'Aligned the legacy visible Tomcat regression with the 1.3.50 start/run split.'

$path143 = 'MCPanel.Tests/ReliabilityTests.VisibleSharedTomcat143.cs'
$text143 = Get-Content -LiteralPath $path143 -Raw

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

$count143 = ([regex]::Matches($text143, [regex]::Escape($old143))).Count
if ($count143 -ne 1) {
    throw "Expected exactly one legacy normal-start block in VisibleSharedTomcat143, found $count143."
}

$text143 = $text143.Replace($old143, $new143)
[IO.File]::WriteAllText((Resolve-Path $path143), $text143, [Text.UTF8Encoding]::new($true))
Write-Host 'Aligned VisibleSharedTomcat143 normal Start with the 1.3.50 startup.bat launcher.'
