$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot 'Prune-TomcatBackend-1.3.11.ps1'
if (-not (Test-Path $scriptPath)) {
    throw 'Base cleanup script is missing.'
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($scriptPath)

# Do not reject unrelated RestartAsync methods elsewhere in MCPanel. The retired
# capability is specifically TomcatProductInstanceManager.RestartAsync.
$text = [regex]::Replace(
    $text,
    "(?m)^\s*'RestartAsync\(',\r?\n",
    '')

# Make the generated regression assertions equally specific so the retained
# ClearCacheAndRestartAsync API does not create a substring false positive.
$text = $text.Replace(
    'Assert.IsFalse(handler.Contains("RestartAsync(", StringComparison.Ordinal));',
    'Assert.IsFalse(handler.Contains("_tomcatInstanceManager.RestartAsync(", StringComparison.Ordinal));')
$text = $text.Replace(
    'Assert.IsFalse(manager.Contains("RestartAsync(", StringComparison.Ordinal));',
    'Assert.IsFalse(manager.Contains("public async Task<string> RestartAsync(", StringComparison.Ordinal));')

# Use the normal test command; restore is required on a fresh hosted runner.
$text = [regex]::Replace(
    $text,
    '(?m)^dotnet test \.\\MCPanel\.Tests\\MCPanel\.Tests\.csproj -c Release --no-restore:\$false\r?$',
    'dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release')

# Add a targeted source audit immediately before the XAML audit.
$marker = '$finalXaml = Read-RepoText $xamlPath'
if (-not $text.Contains($marker)) {
    throw 'Unable to locate XAML audit insertion point.'
}
$targetedAudit = @'
$finalProducts = Read-RepoText $productsPath
if ($finalProducts.Contains('_tomcatInstanceManager.RestartAsync(')) {
    throw 'Retired Tomcat product RestartAsync route remains in MainWindow.Products.cs.'
}
$finalManager = Read-RepoText $managerPath
if ($finalManager.Contains('public async Task<string> RestartAsync(')) {
    throw 'Retired Tomcat product RestartAsync API remains in TomcatProductInstanceManager.cs.'
}

'@
$text = $text.Replace($marker, $targetedAudit + $marker)

# Remove every temporary cleanup helper from the final source tree.
$cleanupMarker = 'Remove-Item -LiteralPath $PSCommandPath -Force'
if (-not $text.Contains($cleanupMarker)) {
    throw 'Unable to locate temporary-script cleanup point.'
}
$cleanupReplacement = @'
Remove-Item -LiteralPath (Join-Path $repoRoot 'tools/Prune-TomcatBackend-1.3.11-v2.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $repoRoot 'tools/Prune-TomcatBackend-1.3.11-v3.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force
'@
$text = $text.Replace($cleanupMarker, $cleanupReplacement.TrimEnd("`r", "`n"))

[System.IO.File]::WriteAllText($scriptPath, $text, $utf8NoBom)
& $scriptPath
