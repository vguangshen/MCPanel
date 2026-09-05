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
    if ($count -ne 1) { throw "Expected one match while $Description, found $count." }
    [regex]::Replace($Text, $Pattern, $Replacement)
}

Write-Host '1/7 Cleaning the Tomcat product management XAML...'
$xamlPath = 'Resources/MainWindowTemplates.xaml'
$xaml = Read-RepoText $xamlPath
foreach ($label in @('重启应用', '实例目录', '清理 work/temp')) {
    if ($xaml.Contains("Content=\"$label\"")) {
        $pattern = '(?ms)^[ \t]*<Button Content="' + [regex]::Escape($label) + '".*?^[ \t]*Visibility="\{Binding IsTomcatDeployment, Converter=\{StaticResource BooleanToVisibility\}\}" />\r?\n'
        $xaml = Replace-RegexOnce $xaml $pattern '' "removing the '$label' button"
    }
}
$cacheRestartPattern = '(?ms)(<Button Content="清理缓存并重启".*?IsEnabled=")\{Binding CanRestartTomcatProduct\}(".*?/>)'
if ([regex]::IsMatch($xaml, $cacheRestartPattern)) {
    $xaml = [regex]::Replace($xaml, $cacheRestartPattern, '$1{Binding CanClearCacheAndRestartTomcatProduct}$2')
}
Write-RepoText $xamlPath $xaml

Write-Host '2/7 Cleaning the Tomcat backend API surface...'
$managerPath = 'TomcatProductInstanceManager.cs'
$manager = Read-RepoText $managerPath
$manager = [regex]::Replace($manager, '(?m)^    public string GetInstanceDirectory\(string productId\) => GetInstanceRoot\(productId\);\r?\n\r?\n', '')
$manager = $manager.Replace(
    'public async Task<string> PrepareProductInstanceAsync(string productId, CancellationToken cancellationToken = default)',
    'internal async Task<string> PrepareProductInstanceAsync(string productId, CancellationToken cancellationToken = default)')
if ($manager.Contains('public async Task<string> RestartAsync(') -or $manager.Contains('public async Task<string> ClearCacheAsync(')) {
    $pattern = '(?ms)\r?\n    public async Task<string> RestartAsync\(string productId, CancellationToken cancellationToken = default\)\r?\n    \{.*?\r?\n    public async Task<string> StartAsync'
    $replacement = @'

    internal async Task<string> ClearCacheAndRestartAsync(string productId, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntimeInfo(productId);
        if (runtime.Mode == TomcatProductRuntimeMode.Shared)
        {
            throw new InvalidOperationException($"{productId} 当前由总 Tomcat Server 运行。为避免影响其他应用，请先切换到独立模式后再执行缓存维护。");
        }
        if (runtime.Mode == TomcatProductRuntimeMode.PortConflict)
        {
            throw new InvalidOperationException($"端口 {runtime.Port} 被其他进程占用，无法安全维护 {productId} 的独立实例。");
        }
        if (runtime.Mode is not (TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina))
        {
            throw new InvalidOperationException($"{productId} 当前未独立运行；“清理缓存并重启”只允许对正在运行的独立实例执行。");
        }

        var catalinaMode = runtime.Mode == TomcatProductRuntimeMode.Catalina;
        await StopAsync(productId, cancellationToken);

        var instanceRoot = await PrepareProductInstanceAsync(productId, cancellationToken);
        foreach (var name in new[] { "work", "temp" })
        {
            var directory = Path.Combine(instanceRoot, name);
            DeleteDirectory(directory);
            Directory.CreateDirectory(directory);
        }
        WriteOperationLog(productId, "已清理独立实例 work/temp 缓存，准备按原运行模式重新启动。");

        var startMessage = await StartAsync(productId, catalinaMode, cancellationToken);
        return $"{productId} 的 work/temp 已清理并按原运行模式重新启动。{Environment.NewLine}{startMessage}";
    }

    public async Task<string> StartAsync
'@
    $manager = Replace-RegexOnce $manager $pattern $replacement.TrimEnd("`r", "`n") 'replacing retired restart/cache APIs'
}
Write-RepoText $managerPath $manager

Write-Host '3/7 Cleaning obsolete view-model capabilities...'
$vmPath = 'ViewModels/ProductViewModels.cs'
$vm = Read-RepoText $vmPath
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanRestartTomcatProduct\)\);\r?\n', '')
$vm = [regex]::Replace($vm, '(?m)^\s*OnPropertyChanged\(nameof\(CanClearTomcatCache\)\);\r?\n', '')
if ($vm.Contains('public bool CanRestartTomcatProduct =>') -or $vm.Contains('public bool CanClearTomcatCache =>')) {
    $vm = [regex]::Replace(
        $vm,
        '(?m)^    public bool CanRestartTomcatProduct => CanStopTomcatProduct;\r?\n    public bool CanClearTomcatCache => .*?;\r?\n',
        '    public bool CanClearCacheAndRestartTomcatProduct => CanStopTomcatProduct;' + "`r`n")
}
if (-not $vm.Contains('public bool CanClearCacheAndRestartTomcatProduct => CanStopTomcatProduct;')) {
    throw 'Expected retained cache-clean restart view-model capability was not produced.'
}
# Keep the retained action live when deployment/runtime state changes.
$vm = $vm.Replace(
    'OnPropertyChanged(nameof(CanStopTomcatProduct));' + "`r`n                OnPropertyChanged(nameof(CanStartTomcatProduct));",
    'OnPropertyChanged(nameof(CanStopTomcatProduct));' + "`r`n                OnPropertyChanged(nameof(CanClearCacheAndRestartTomcatProduct));`r`n                OnPropertyChanged(nameof(CanStartTomcatProduct));")
$vm = $vm.Replace(
    'OnPropertyChanged(nameof(CanStopTomcatProduct));' + "`n                OnPropertyChanged(nameof(CanStartTomcatProduct));",
    'OnPropertyChanged(nameof(CanStopTomcatProduct));' + "`n                OnPropertyChanged(nameof(CanClearCacheAndRestartTomcatProduct));`n                OnPropertyChanged(nameof(CanStartTomcatProduct));")
Write-RepoText $vmPath $vm

Write-Host '4/7 Updating regression coverage...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatDebug137.cs'
$test = Read-RepoText $testPath
$testPattern = '(?ms)    \[TestMethod\]\r?\n    public void TomcatManagementUi_PrunesRedundantActionsAndKeepsCoreTools\(\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    private static string ReadRepositoryFile'
if ([regex]::IsMatch($test, $testPattern)) {
    $testReplacement = @'
    [TestMethod]
    public void TomcatManagement_RemovesRetiredUiAndBackendCapabilities()
    {
        var xaml = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
        StringAssert.Contains(xaml, "Content=\"单独启动\"");
        StringAssert.Contains(xaml, "Content=\"以 Catalina 方式启动\"");
        StringAssert.Contains(xaml, "Content=\"停止应用\"");
        StringAssert.Contains(xaml, "Content=\"查看日志\"");
        StringAssert.Contains(xaml, "Content=\"清理缓存并重启\"");
        Assert.IsFalse(xaml.Contains("Content=\"重启应用\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"实例目录\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"清理 work/temp\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Tag=\"OpenInstance\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Tag=\"ClearCache\"", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "CanClearCacheAndRestartTomcatProduct");

        var bridge = ReadRepositoryFile("MainWindowTemplates.xaml.cs");
        Assert.IsFalse(bridge.Contains("TomcatManagementUiPruner", StringComparison.Ordinal));

        var handler = ReadRepositoryFile("MainWindow.Products.cs");
        Assert.IsFalse(handler.Contains("\"Restart\" =>", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("\"OpenInstance\"", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("\"ClearCache\" =>", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("_tomcatInstanceManager.RestartAsync(", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("_tomcatInstanceManager.ClearCacheAsync(", StringComparison.Ordinal));
        StringAssert.Contains(handler, "ClearCacheAndRestartAsync(item.ProductId)");

        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(manager.Contains("public async Task<string> RestartAsync(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("GetInstanceDirectory(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("ClearCacheAsync(", StringComparison.Ordinal));
        StringAssert.Contains(manager, "internal async Task<string> ClearCacheAndRestartAsync(");
        StringAssert.Contains(manager, "internal async Task<string> PrepareProductInstanceAsync(");

        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "ProductViewModels.cs"));
        Assert.IsFalse(viewModel.Contains("CanRestartTomcatProduct", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanClearTomcatCache", StringComparison.Ordinal));
        StringAssert.Contains(viewModel, "CanClearCacheAndRestartTomcatProduct");
    }

    private static string ReadRepositoryFile
'@
    $test = [regex]::Replace($test, $testPattern, $testReplacement.TrimEnd("`r", "`n"))
}
Write-RepoText $testPath $test

Write-Host '5/7 Auditing retained and removed capabilities...'
$xaml = Read-RepoText $xamlPath
foreach ($token in @('Content="重启应用"','Content="实例目录"','Content="清理 work/temp"','Tag="OpenInstance"','Tag="ClearCache"')) {
    if ($xaml.Contains($token)) { throw "Retired Tomcat UI token remains: $token" }
}
foreach ($token in @('Content="单独启动"','Content="以 Catalina 方式启动"','Content="停止应用"','Content="查看日志"','Content="清理缓存并重启"')) {
    if (-not $xaml.Contains($token)) { throw "Required Tomcat action is missing: $token" }
}
$handler = Read-RepoText 'MainWindow.Products.cs'
foreach ($token in @('"Restart" =>','"OpenInstance"','"ClearCache" =>','_tomcatInstanceManager.RestartAsync(','_tomcatInstanceManager.ClearCacheAsync(')) {
    if ($handler.Contains($token)) { throw "Retired Tomcat dispatcher token remains: $token" }
}
if (-not $handler.Contains('ClearCacheAndRestartAsync(item.ProductId)')) { throw 'Retained cache-clean restart dispatch is missing.' }
$manager = Read-RepoText $managerPath
foreach ($token in @('public async Task<string> RestartAsync(','GetInstanceDirectory(','ClearCacheAsync(')) {
    if ($manager.Contains($token)) { throw "Retired Tomcat backend token remains: $token" }
}
if (-not $manager.Contains('internal async Task<string> ClearCacheAndRestartAsync(')) { throw 'Dedicated cache-clean restart backend is missing.' }
$bridge = Read-RepoText 'MainWindowTemplates.xaml.cs'
if ($bridge.Contains('TomcatManagementUiPruner')) { throw 'Runtime UI pruning shim reference remains.' }
if (Test-Path (Join-Path $repoRoot 'TomcatManagementUiPruner.cs')) { throw 'Runtime UI pruning shim source still exists.' }

Write-Host '6/7 Running full regression tests and Win-x64 publish validation...'
dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }
$publishDir = Join-Path $env:RUNNER_TEMP 'MCPanelBackendCleanupPublish'
& .\Publish-WinX64.ps1 -OutputDirectory $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish-WinX64.ps1 failed with exit code $LASTEXITCODE." }

Write-Host '7/7 Removing temporary cleanup infrastructure and committing...'
foreach ($path in @(
    '.github/workflows/tomcat-backend-cleanup-1.3.11.yml',
    'tools/Prune-TomcatBackend-1.3.11.ps1',
    'tools/Prune-TomcatBackend-1.3.11-v2.ps1',
    'tools/Prune-TomcatBackend-1.3.11-v3.ps1',
    'tools/Prune-TomcatBackend-1.3.11-v4.ps1',
    'tools/Prune-TomcatBackend-1.3.11-v5.ps1'
)) {
    Remove-Item -LiteralPath (Join-Path $repoRoot $path) -Force -ErrorAction SilentlyContinue
}

git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git add -A
git commit -m 'Complete Tomcat management backend cleanup'
if ($LASTEXITCODE -ne 0) { throw 'Unable to commit the validated cleanup.' }
git pull --rebase origin main
if ($LASTEXITCODE -ne 0) { throw 'Unable to rebase cleanup onto the latest main branch.' }
git push origin HEAD:main
if ($LASTEXITCODE -ne 0) { throw 'Unable to push the validated cleanup.' }
