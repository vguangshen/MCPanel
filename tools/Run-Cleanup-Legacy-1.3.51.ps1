$ErrorActionPreference = 'Stop'

$scriptPath = Join-Path $PSScriptRoot 'Cleanup-Legacy-1.3.51.ps1'
$text = Get-Content -LiteralPath $scriptPath -Raw
$old = 'Write-Host "Updated $Path: $Description."'
$new = 'Write-Host "Updated ${Path}: $Description."'
$count = ([regex]::Matches($text, [regex]::Escape($old))).Count
if ($count -ne 1) {
    throw "Expected exactly one PowerShell interpolation fix target, found $count."
}

$text = $text.Replace($old, $new)
[IO.File]::WriteAllText($scriptPath, $text, [Text.UTF8Encoding]::new($true))
& $scriptPath
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
