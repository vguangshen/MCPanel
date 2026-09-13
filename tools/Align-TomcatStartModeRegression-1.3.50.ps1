$ErrorActionPreference = 'Stop'

function Assert-FileContains {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Expected,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = Get-Content -LiteralPath $Path -Raw
    if (-not $text.Contains($Expected, [StringComparison]::Ordinal)) {
        throw "Regression alignment check failed for $Description in $Path. Missing: $Expected"
    }
}

function Assert-FileNotContains {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Unexpected,
        [Parameter(Mandatory = $true)][string]$Description
    )

    $text = Get-Content -LiteralPath $Path -Raw
    if ($text.Contains($Unexpected, [StringComparison]::Ordinal)) {
        throw "Regression alignment check failed for $Description in $Path. Legacy expectation remains: $Unexpected"
    }
}

$path114 = 'MCPanel.Tests/ReliabilityTests.TomcatVisibleProductStart114.cs'
Assert-FileContains -Path $path114 -Expected 'internal static void LaunchTomcatStartConsole(string tomcatRoot)' -Description 'visible shared Tomcat normal launcher'
Assert-FileContains -Path $path114 -Expected 'call startup.bat' -Description 'visible shared Tomcat start command'
Assert-FileContains -Path $path114 -Expected 'call catalina.bat run' -Description 'explicit Catalina command remains covered'
Assert-FileNotContains -Path $path114 -Unexpected '// Shared Tomcat now also opens a visible Catalina CMD console instead of' -Description 'legacy shared Tomcat comment'

$path143 = 'MCPanel.Tests/ReliabilityTests.VisibleSharedTomcat143.cs'
Assert-FileContains -Path $path143 -Expected 'StringAssert.Contains(block, "LaunchTomcatStartConsole(tomcatRoot)");' -Description 'normal shared Tomcat Start launcher'
Assert-FileContains -Path $path143 -Expected 'Assert.IsFalse(block.Contains("LaunchTomcatConsole(tomcatRoot)", StringComparison.Ordinal));' -Description 'normal Start excludes Catalina launcher'
Assert-FileNotContains -Path $path143 -Unexpected 'StringAssert.Contains(block, "LaunchTomcatConsole(tomcatRoot)");' -Description 'legacy normal-start Catalina expectation'

$path137 = 'MCPanel.Tests/ReliabilityTests.TomcatServiceDecoupling137.cs'
Assert-FileContains -Path $path137 -Expected 'Tomcat Server 已按标准 start 模式启动' -Description 'new Tomcat Start status text'
Assert-FileContains -Path $path137 -Expected 'LaunchTomcatStartConsole(tomcatRoot)' -Description 'service-decoupling normal launcher'
Assert-FileContains -Path $path137 -Expected 'LaunchTomcatConsole(tomcatRoot)' -Description 'service-decoupling Catalina launcher'
Assert-FileNotContains -Path $path137 -Unexpected '共享 Tomcat 不再通过 Windows Service 隐藏启动' -Description 'legacy Tomcat status text'

$path113 = 'MCPanel.Tests/ReliabilityTests.TomcatServerProgress113.cs'
Assert-FileContains -Path $path113 -Expected 'LaunchTomcatStartConsole(tomcatRoot)' -Description 'environment progress normal launcher'
Assert-FileContains -Path $path113 -Expected 'Tomcat Server 已按标准 start 模式启动' -Description 'environment progress new status text'
Assert-FileNotContains -Path $path113 -Unexpected '共享 Tomcat 不再通过 Windows Service 隐藏启动' -Description 'environment progress legacy status text'

Write-Host 'Verified legacy Tomcat regressions are permanently aligned with the 1.3.50 normal-start/Catalina split.'
