$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

foreach ($relativePath in @('MainWindow.xaml', 'Resources/PanelDialogControls.xaml')) {
    $path = Join-Path $repoRoot $relativePath
    $text = [System.IO.File]::ReadAllText($path)

    # PowerShell does not use backslash to escape quotes. The migration's two
    # interpolated replacement strings can therefore emit one or more literal
    # backslashes immediately before XML attribute quotes. Remove only those
    # quote-adjacent backslashes and leave all other path/backslash content intact.
    $fixed = [regex]::Replace($text, '\\+(?=")', '')

    try {
        [xml]$null = $fixed
    }
    catch {
        throw "Generated XML is still invalid after quote normalization: $relativePath. $($_.Exception.Message)"
    }

    [System.IO.File]::WriteAllText($path, $fixed, $utf8NoBom)
    Write-Host "Validated generated XML: $relativePath"
}

Write-Host 'Normalized and validated generated ResourceDictionary quotes for modal UI migration.'
