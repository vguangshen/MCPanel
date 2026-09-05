$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot 'Prune-TomcatBackend-1.3.11.ps1'
if (-not (Test-Path $scriptPath)) {
    throw 'Base cleanup script is missing.'
}

$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$text = [System.IO.File]::ReadAllText($scriptPath)

# Audit only the retired Tomcat product restart route, not unrelated RestartAsync
# APIs or Environment-page Restart tags that are still valid features.
$text = [regex]::Replace($text, "(?m)^\s*'RestartAsync\(',\r?\n", '')
$text = $text.Replace(
    'Assert.IsFalse(handler.Contains("RestartAsync(", StringComparison.Ordinal));',
    'Assert.IsFalse(handler.Contains("_tomcatInstanceManager.RestartAsync(", StringComparison.Ordinal));')
$text = $text.Replace(
    'Assert.IsFalse(manager.Contains("RestartAsync(", StringComparison.Ordinal));',
    'Assert.IsFalse(manager.Contains("public async Task<string> RestartAsync(", StringComparison.Ordinal));')
$text = [regex]::Replace(
    $text,
    '(?m)^\s*Assert\.IsFalse\(xaml\.Contains\("Tag=\\"Restart\\"", StringComparison\.Ordinal\)\);\r?\n',
    '')
$text = $text.Replace(
    'foreach ($token in @(''Content="重启应用"'', ''Content="实例目录"'', ''Content="清理 work/temp"'', ''Tag="Restart"'', ''Tag="OpenInstance"'', ''Tag="ClearCache"'')) {',
    'foreach ($token in @(''Content="重启应用"'', ''Content="实例目录"'', ''Content="清理 work/temp"'', ''Tag="OpenInstance"'', ''Tag="ClearCache"'')) {')

$text = [regex]::Replace(
    $text,
    '(?m)^dotnet test \.\\MCPanel\.Tests\\MCPanel\.Tests\.csproj -c Release --no-restore:\$false\r?$',
    'dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release')

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

$cleanupMarker = 'Remove-Item -LiteralPath $PSCommandPath -Force'
if (-not $text.Contains($cleanupMarker)) {
    throw 'Unable to locate temporary-script cleanup point.'
}
$cleanupReplacement = @'
Remove-Item -LiteralPath (Join-Path $repoRoot 'tools/Prune-TomcatBackend-1.3.11-v2.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $repoRoot 'tools/Prune-TomcatBackend-1.3.11-v3.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath (Join-Path $repoRoot 'tools/Prune-TomcatBackend-1.3.11-v4.ps1') -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $PSCommandPath -Force
'@
$text = $text.Replace($cleanupMarker, $cleanupReplacement.TrimEnd("`r", "`n"))

[System.IO.File]::WriteAllText($scriptPath, $text, $utf8NoBom)
& $scriptPath
