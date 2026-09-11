$ErrorActionPreference = 'Stop'
$path = Join-Path $PWD 'MCPanel.Tests/ReliabilityTests.RuntimeLifecycle138.cs'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))
$old = @'
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        StringAssert.Contains(services, "OnStop() => StopTomcat(waitForExitAndForce: false)");
        StringAssert.Contains(services, "if (!waitForExitAndForce)");
        StringAssert.Contains(services, "runtime cleanup delegated to MCPanel controller");
'@
$new = @'
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var stopStart = services.IndexOf("protected override void OnStop()", StringComparison.Ordinal);
        var shutdownStart = services.IndexOf("protected override void OnShutdown()", stopStart, StringComparison.Ordinal);
        Assert.IsTrue(stopStart >= 0 && shutdownStart > stopStart);
        var normalStop = services.Substring(stopStart, shutdownStart - stopStart);
        StringAssert.Contains(normalStop, "runtime cleanup delegated to MCPanel controller");
        Assert.IsFalse(normalStop.Contains("StopTomcat", StringComparison.Ordinal));
        Assert.IsFalse(normalStop.Contains("shutdown.bat", StringComparison.Ordinal));
'@
if (-not $text.Contains($old)) { throw 'Stale RuntimeLifecycle138 Tomcat assertions were not found.' }
$text = $text.Replace($old, $new)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
Write-Host 'Updated RuntimeLifecycle138 to the 1.3.42 no-late-service-cleanup contract.'
