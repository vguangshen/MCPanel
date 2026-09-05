$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scriptPath = Join-Path $PSScriptRoot 'Prune-TomcatBackend-1.3.11.ps1'
if (-not (Test-Path $scriptPath)) {
    throw 'Base cleanup script is missing.'
}

$text = [System.IO.File]::ReadAllText($scriptPath)
$text = $text.Replace("    'RestartAsync(',`n", '')
$text = $text.Replace('dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release --no-restore:$false', 'dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release')
$text = $text.Replace(
    "# The one-time automation must not remain in the released source tree.`nRemove-Item -LiteralPath (Join-Path `$repoRoot '.github/workflows/tomcat-backend-cleanup-1.3.11.yml') -Force",
    "# The one-time automation must not remain in the released source tree.`nRemove-Item -LiteralPath (Join-Path `$repoRoot '.github/workflows/tomcat-backend-cleanup-1.3.11.yml') -Force`nRemove-Item -LiteralPath (Join-Path `$repoRoot 'tools/Prune-TomcatBackend-1.3.11-v2.ps1') -Force")

# Add targeted checks for the retired Tomcat restart API without rejecting unrelated RestartAsync methods.
$marker = '$finalXaml = Read-RepoText $xamlPath'
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
if (-not $text.Contains($marker)) {
    throw 'Unable to locate targeted-audit insertion point.'
}
$text = $text.Replace($marker, $targetedAudit + $marker)

[System.IO.File]::WriteAllText($scriptPath, $text, (New-Object System.Text.UTF8Encoding($false)))
& $scriptPath
