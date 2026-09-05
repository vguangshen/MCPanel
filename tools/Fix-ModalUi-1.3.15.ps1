$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

foreach ($relativePath in @('MainWindow.xaml', 'Resources/PanelDialogControls.xaml')) {
    $path = Join-Path $repoRoot $relativePath
    $text = [System.IO.File]::ReadAllText($path)
    $fixed = $text.Replace('Source=\"', 'Source="').Replace('.xaml\"', '.xaml"')
    if ($fixed -eq $text) {
        throw "Expected escaped ResourceDictionary quote markers in $relativePath, but none were found."
    }
    [System.IO.File]::WriteAllText($path, $fixed, $utf8NoBom)
}

Write-Host 'Fixed escaped ResourceDictionary quotes for modal UI migration.'
