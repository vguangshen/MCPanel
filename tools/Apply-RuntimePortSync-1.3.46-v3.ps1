$ErrorActionPreference = 'Stop'

& (Join-Path $PWD 'tools\Apply-RuntimePortSync-1.3.46-v2.ps1')

$path = Join-Path $PWD 'MCPanel.Tests\ReliabilityTests.RuntimePortSync146.cs'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))
$old = "using Microsoft.VisualStudio.TestTools.UnitTesting;"
$new = "using System;`r`nusing System.Linq;`r`nusing Microsoft.VisualStudio.TestTools.UnitTesting;"
if (-not $text.Contains($old)) {
    throw 'Runtime-port-sync regression test header was not found.'
}
[IO.File]::WriteAllText($path, $text.Replace($old, $new), [Text.UTF8Encoding]::new($false))
