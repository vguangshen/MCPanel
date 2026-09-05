$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $repoRoot $Path), $Text, $utf8NoBom)
}

function Replace-RegexOnce([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Description) {
    $count = [regex]::Matches($Text, $Pattern).Count
    if ($count -ne 1) {
        throw "Expected exactly one match while $Description, found $count."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, 1)
}

Write-Host '1/8 Removing Stop and cache-restart buttons...'
$xamlPath = 'Resources/MainWindowTemplates.xaml'
$xaml = Read-RepoText $xamlPath
foreach ($label in @('停止应用', '清理缓存并重启')) {
    $pattern = '(?ms)^\s*<Button Content="' + [regex]::Escape($label) + '".*?^\s*Visibility="\{Binding IsTomcatDeployment, Converter=\{StaticResource BooleanToVisibility\}\}" />\r?\n'
    $xaml = Replace-RegexOnce $xaml $pattern '' "removing '$label' button"
}
Write-RepoText $xamlPath $xaml

Write-Host '2/8 Removing product-level dispatch branches...'
$productsPath = 'MainWindow.Products.cs'
$products = Read-RepoText $productsPath
foreach ($line in @(
    '                "Stop" => "正在停止",',
    '                "ClearCacheRestart" => "正在清理缓存并重启",',
    '                "Stop" => await _tomcatInstanceManager.StopAsync(item.ProductId),',
    '                "ClearCacheRestart" => await _tomcatInstanceManager.ClearCacheAndRestartAsync(item.ProductId),'
)) {
    if (-not $products.Contains($line)) {
        throw "Expected product dispatcher line missing: $line"
    }
    $products = $products.Replace($line + "`r`n", '').Replace($line + "`n", '')
}
Write-RepoText $productsPath $products

Write-Host '3/8 Removing dedicated cache-restart backend...'
$managerPath = 'TomcatProductInstanceManager.cs'
$manager = Read-RepoText $managerPath
$cachePattern = '(?ms)\r?\n    public async Task<string> ClearCacheAndRestartAsync\(string productId, CancellationToken cancellationToken = default\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n(?=    public async Task<string> StartAsync)'
$manager = Replace-RegexOnce $manager $cachePattern "`r`n" 'removing ClearCacheAndRestartAsync backend'
Write-RepoText $managerPath $manager

Write-Host '4/8 Removing view-model state used only by deleted buttons...'
$vmPath = 'ViewModels/ProductViewModels.cs'
$vm = Read-RepoText $vmPath
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanStopTomcatProduct\)\);\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanRestartTomcatProduct\)\);\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanClearTomcatCache\)\);\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanClearCacheAndRestartTomcatProduct\)\);\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^    public bool CanStopTomcatProduct => .*?;\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^    public bool CanRestartTomcatProduct => .*?;\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^    public bool CanClearTomcatCache => .*?;\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^    public bool CanClearCacheAndRestartTomcatProduct => .*?;\r?\n', '')
Write-RepoText $vmPath $vm

Write-Host '5/8 Updating regression coverage...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatDebug137.cs'
$test = Read-RepoText $testPath
$testPattern = '(?ms)    \[TestMethod\]\r?\n    public void TomcatDebug_ProductManagement_RemovesRetiredActionsAndBackends\(\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n(?=    private static string ReadRepositoryFile)'
$testReplacement = @'
    [TestMethod]
    public void TomcatDebug_ProductManagement_KeepsOnlyCoreStartAndLogActions()
    {
        var xaml = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
        var products = ReadRepositoryFile("MainWindow.Products.cs");
        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "ProductViewModels.cs"));

        StringAssert.Contains(xaml, "Content=\"单独启动\"");
        StringAssert.Contains(xaml, "Content=\"以 Catalina 方式启动\"");
        StringAssert.Contains(xaml, "Content=\"查看日志\"");
        Assert.IsFalse(xaml.Contains("Content=\"停止应用\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"清理缓存并重启\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"重启应用\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"实例目录\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"清理 work/temp\"", StringComparison.Ordinal));

        Assert.IsFalse(products.Contains("\"Stop\" =>", StringComparison.Ordinal));
        Assert.IsFalse(products.Contains("ClearCacheRestart", StringComparison.Ordinal));
        Assert.IsFalse(products.Contains("ClearCacheAndRestartAsync", StringComparison.Ordinal));
        StringAssert.Contains(products, "\"Start\" => await _tomcatInstanceManager.StartAsync");
        StringAssert.Contains(products, "\"Catalina\" => await _tomcatInstanceManager.StartAsync");

        Assert.IsFalse(manager.Contains("ClearCacheAndRestartAsync(", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanRestartTomcatProduct", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanClearTomcatCache", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanClearCacheAndRestartTomcatProduct", StringComparison.Ordinal));

        // StopAsync remains an internal lifecycle primitive for uninstall and
        // global Tomcat management; it is no longer exposed as a product button.
        StringAssert.Contains(manager, "public async Task<string> StopAsync(");
    }

'@
$test = Replace-RegexOnce $test $testPattern $testReplacement 'replacing Tomcat management regression test'
Write-RepoText $testPath $test

Write-Host '6/8 Bumping version to 1.3.12...'
$projectPath = 'MCPanel.csproj'
$project = Read-RepoText $projectPath
foreach ($pair in @(
    @('<Version>1.3.11</Version>', '<Version>1.3.12</Version>'),
    @('<FileVersion>1.3.11.0</FileVersion>', '<FileVersion>1.3.12.0</FileVersion>'),
    @('<AssemblyVersion>1.3.11.0</AssemblyVersion>', '<AssemblyVersion>1.3.12.0</AssemblyVersion>')
)) {
    if (-not $project.Contains($pair[0])) {
        throw "Version token missing: $($pair[0])"
    }
    $project = $project.Replace($pair[0], $pair[1])
}
Write-RepoText $projectPath $project

Write-Host '7/8 Auditing removed and retained capabilities...'
$xaml = Read-RepoText $xamlPath
$products = Read-RepoText $productsPath
$manager = Read-RepoText $managerPath
$vm = Read-RepoText $vmPath
foreach ($token in @('Content="停止应用"', 'Content="清理缓存并重启"')) {
    if ($xaml.Contains($token)) { throw "Retired XAML token remains: $token" }
}
foreach ($token in @('"Stop" =>', 'ClearCacheRestart', 'ClearCacheAndRestartAsync')) {
    if ($products.Contains($token)) { throw "Retired product dispatcher token remains: $token" }
}
if ($manager.Contains('ClearCacheAndRestartAsync(')) { throw 'Dedicated cache-restart backend still exists.' }
foreach ($token in @('CanRestartTomcatProduct', 'CanClearTomcatCache', 'CanClearCacheAndRestartTomcatProduct')) {
    if ($vm.Contains($token)) { throw "Retired view-model token remains: $token" }
}
foreach ($token in @('Content="单独启动"', 'Content="以 Catalina 方式启动"', 'Content="查看日志"')) {
    if (-not $xaml.Contains($token)) { throw "Retained product action missing: $token" }
}
if (-not $manager.Contains('public async Task<string> StopAsync(')) {
    throw 'StopAsync lifecycle primitive was accidentally removed.'
}

Write-Host '8/8 Running tests and Win-x64 publish validation...'
dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
$publishDir = Join-Path $env:RUNNER_TEMP 'MCPanelTrimPublish'
& .\Publish-WinX64.ps1 -OutputDirectory $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish-WinX64.ps1 failed with exit code $LASTEXITCODE." }
"MCPANEL_TRIM_PUBLISH_DIR=$publishDir" | Out-File -FilePath $env:GITHUB_ENV -Append -Encoding utf8
