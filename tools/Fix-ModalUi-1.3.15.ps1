$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

foreach ($relativePath in @('MainWindow.xaml', 'Resources/PanelDialogControls.xaml')) {
    $path = Join-Path $repoRoot $relativePath
    $text = [System.IO.File]::ReadAllText($path)
    $fixed = $text

    # PowerShell does not use backslash to escape a double quote. The migration
    # originally generated literal backslashes immediately before XML quotes.
    # Remove them one at a time until no backslash+quote pair remains. This is
    # deterministic whether the generated text contains one, two, or more.
    while ($fixed.Contains('\"')) {
        $fixed = $fixed.Replace('\"', '"')
    }

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
