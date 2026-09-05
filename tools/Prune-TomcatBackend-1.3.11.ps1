$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    return [System.IO.File]::ReadAllText((Join-Path $repoRoot $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $repoRoot $Path), $Text, $utf8NoBom)
}

function Replace-ExactOrFail([string]$Text, [string]$Old, [string]$New, [string]$Description) {
    if (-not $Text.Contains($Old)) {
        throw "Expected text not found while $Description."
    }
    return $Text.Replace($Old, $New)
}

function Replace-RegexOrFail([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Description, [int]$ExpectedCount = 1) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne $ExpectedCount) {
        throw "Expected $ExpectedCount match(es) while $Description, found $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement)
}

Write-Host '1/8 Removing retired Tomcat buttons from XAML source...'
$xamlPath = 'Resources/MainWindowTemplates.xaml'
$xaml = Read-RepoText $xamlPath
foreach ($label in @('重启应用', '实例目录', '清理 work/temp')) {
    $escaped = [regex]::Escape($label)
    $pattern = '(?ms)^[ \t]*<Button Content="' + $escaped + '".*?^[ \t]*Visibility="\{Binding IsTomcatDeployment, Converter=\{StaticResource BooleanToVisibility\}\}" />\r?\n'
    $xaml = Replace-RegexOrFail $xaml $pattern '' "removing the '$label' button"
}
$xaml = Replace-ExactOrFail $xaml 'IsEnabled="{Binding CanRestartTomcatProduct}"' 'IsEnabled="{Binding CanClearCacheAndRestartTomcatProduct}"' 'rebinding cache-clean restart availability'
Write-RepoText $xamlPath $xaml

Write-Host '2/8 Removing the runtime UI-pruner workaround...'
$bridgePath = 'MainWindowTemplates.xaml.cs'
$bridge = Read-RepoText $bridgePath
$bridgePattern = '(?ms)    private void InstalledProductManageToggle_Click\(object sender, RoutedEventArgs e\)\r?\n    \{\r?\n        GetOwner\(sender\)\?\.InstalledProductManageToggle_Click\(sender, e\);\r?\n        if \(sender is DependencyObject element\)\r?\n        \{\r?\n            TomcatManagementUiPruner.RemoveRetiredActions\(element\);\r?\n        \}\r?\n    \}'
$bridgeReplacement = "    private void InstalledProductManageToggle_Click(object sender, RoutedEventArgs e) =>`n        GetOwner(sender)?.InstalledProductManageToggle_Click(sender, e);"
$bridge = Replace-RegexOrFail $bridge $bridgePattern $bridgeReplacement 'removing the runtime pruner bridge'
Write-RepoText $bridgePath $bridge

Write-Host '3/8 Removing retired action routing from MainWindow...'
$productsPath = 'MainWindow.Products.cs'
$products = Read-RepoText $productsPath
$openPattern = '(?ms)            if \(action is "OpenLogs" or "OpenInstance"\)\r?\n            \{.*?                return;\r?\n            \}'
$openReplacement = @'
            if (action == "OpenLogs")
            {
                var instanceRoot = await _tomcatInstanceManager.PrepareProductInstanceAsync(item.ProductId);
                var target = Path.Combine(instanceRoot, "logs");
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                item.RefreshRuntime();
                return;
            }
'@
$products = Replace-RegexOrFail $products $openPattern $openReplacement.TrimEnd("`r", "`n") 'removing the instance-directory action route'
foreach ($pattern in @(
    '(?m)^\s*"Restart" => "正在重启独立实例",\r?\n',
    '(?m)^\s*"ClearCache" => "正在清理 work/temp",\r?\n',
    '(?m)^\s*"Restart" => await _tomcatInstanceManager\.RestartAsync\(item\.ProductId\),\r?\n',
    '(?m)^\s*"ClearCache" => await _tomcatInstanceManager\.ClearCacheAsync\(item\.ProductId, restart: false\),\r?\n'
)) {
    $products = Replace-RegexOrFail $products $pattern '' 'removing a retired Tomcat action switch arm'
}
$products = Replace-ExactOrFail $products '_tomcatInstanceManager.ClearCacheAsync(item.ProductId, restart: true)' '_tomcatInstanceManager.ClearCacheAndRestartAsync(item.ProductId)' 'routing the retained cache-clean restart action to its dedicated backend method'
Write-RepoText $productsPath $products

Write-Host '4/8 Removing retired backend APIs and narrowing instance preparation visibility...'
$managerPath = 'TomcatProductInstanceManager.cs'
$manager = Read-RepoText $managerPath
$manager = Replace-RegexOrFail $manager '(?m)^    public string GetInstanceDirectory\(string productId\) => GetInstanceRoot\(productId\);\r?\n\r?\n' '' 'removing GetInstanceDirectory'
$manager = Replace-ExactOrFail $manager 'public async Task<string> PrepareProductInstanceAsync(string productId, CancellationToken cancellationToken = default)' 'internal async Task<string> PrepareProductInstanceAsync(string productId, CancellationToken cancellationToken = default)' 'narrowing instance preparation visibility'
$backendPattern = '(?ms)\r?\n    public async Task<string> RestartAsync\(string productId, CancellationToken cancellationToken = default\)\r?\n    \{.*?\r?\n    public async Task<string> StartAsync'
$backendReplacement = @'

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
$manager = Replace-RegexOrFail $manager $backendPattern $backendReplacement.TrimEnd("`r", "`n") 'replacing restart and optional cache-clear APIs with the dedicated cache-clean restart operation'
Write-RepoText $managerPath $manager

Write-Host '5/8 Removing obsolete view-model capabilities...'
$viewModelPath = 'ViewModels/ProductViewModels.cs'
$viewModel = Read-RepoText $viewModelPath
$viewModel = [regex]::Replace($viewModel, '(?m)^\s*OnPropertyChanged\(nameof\(CanRestartTomcatProduct\)\);\r?\n', '')
$viewModel = [regex]::Replace($viewModel, '(?m)^\s*OnPropertyChanged\(nameof\(CanClearTomcatCache\)\);\r?\n', '')
$viewModel = $viewModel.Replace(
    'OnPropertyChanged(nameof(CanStopTomcatProduct));',
    "OnPropertyChanged(nameof(CanStopTomcatProduct));`n                OnPropertyChanged(nameof(CanClearCacheAndRestartTomcatProduct));")
$oldProperties = @'
    public bool CanRestartTomcatProduct => CanStopTomcatProduct;
    public bool CanClearTomcatCache => IsTomcatDeployment && TomcatRuntimeMode is TomcatProductRuntimeMode.Stopped or TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina;
'@
$newProperties = @'
    public bool CanClearCacheAndRestartTomcatProduct => CanStopTomcatProduct;
'@
$viewModel = Replace-ExactOrFail $viewModel $oldProperties.TrimStart("`r", "`n") $newProperties.TrimStart("`r", "`n") 'removing obsolete Tomcat view-model capabilities'
Write-RepoText $viewModelPath $viewModel

Write-Host '6/8 Updating regression coverage...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatDebug137.cs'
$test = Read-RepoText $testPath
$testPattern = '(?ms)    \[TestMethod\]\r?\n    public void TomcatManagementUi_PrunesRedundantActionsAndKeepsCoreTools\(\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    private static string ReadRepositoryFile'
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
        Assert.IsFalse(xaml.Contains("Tag=\"Restart\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Tag=\"OpenInstance\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Tag=\"ClearCache\"", StringComparison.Ordinal));
        StringAssert.Contains(xaml, "CanClearCacheAndRestartTomcatProduct");

        var bridge = ReadRepositoryFile("MainWindowTemplates.xaml.cs");
        Assert.IsFalse(bridge.Contains("TomcatManagementUiPruner", StringComparison.Ordinal));

        var handler = ReadRepositoryFile("MainWindow.Products.cs");
        Assert.IsFalse(handler.Contains("\"Restart\" =>", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("\"OpenInstance\"", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("\"ClearCache\" =>", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("RestartAsync(", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("ClearCacheAsync(", StringComparison.Ordinal));
        StringAssert.Contains(handler, "ClearCacheAndRestartAsync(item.ProductId)");

        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(manager.Contains("RestartAsync(", StringComparison.Ordinal));
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
$test = Replace-RegexOrFail $test $testPattern $testReplacement.TrimEnd("`r", "`n") 'replacing the Tomcat management regression test'
Write-RepoText $testPath $test

Write-Host '7/8 Deleting the temporary runtime-pruner source and auditing the repository...'
$prunerPath = Join-Path $repoRoot 'TomcatManagementUiPruner.cs'
if (-not (Test-Path $prunerPath)) {
    throw 'TomcatManagementUiPruner.cs was expected to exist before cleanup.'
}
Remove-Item -LiteralPath $prunerPath -Force

$productionCs = Get-ChildItem -Path $repoRoot -Recurse -Filter '*.cs' -File | Where-Object {
    $_.FullName -notmatch '[\\/]MCPanel\.Tests[\\/]' -and
    $_.FullName -notmatch '[\\/](obj|bin)[\\/]'
}
$forbiddenProductionTokens = @(
    'RestartAsync(',
    'GetInstanceDirectory(',
    'ClearCacheAsync(',
    '"OpenInstance"',
    '"ClearCache" =>',
    'TomcatManagementUiPruner'
)
foreach ($token in $forbiddenProductionTokens) {
    $hits = $productionCs | Select-String -SimpleMatch $token
    if ($hits) {
        $locations = ($hits | ForEach-Object { "$($_.Path):$($_.LineNumber)" }) -join ', '
        throw "Forbidden retired capability token '$token' remains in production source: $locations"
    }
}

$finalXaml = Read-RepoText $xamlPath
foreach ($token in @('Content="重启应用"', 'Content="实例目录"', 'Content="清理 work/temp"', 'Tag="Restart"', 'Tag="OpenInstance"', 'Tag="ClearCache"')) {
    if ($finalXaml.Contains($token)) {
        throw "Retired Tomcat UI token remains after cleanup: $token"
    }
}
foreach ($token in @('Content="单独启动"', 'Content="以 Catalina 方式启动"', 'Content="停止应用"', 'Content="查看日志"', 'Content="清理缓存并重启"')) {
    if (-not $finalXaml.Contains($token)) {
        throw "Required Tomcat management action disappeared unexpectedly: $token"
    }
}

Write-Host '8/8 Running full tests and Win-x64 publish validation...'
dotnet test .\MCPanel.Tests\MCPanel.Tests.csproj -c Release --no-restore:$false
if ($LASTEXITCODE -ne 0) { throw "dotnet test failed with exit code $LASTEXITCODE." }

$publishDir = Join-Path $env:RUNNER_TEMP 'MCPanelBackendCleanupPublish'
& .\Publish-WinX64.ps1 -OutputDirectory $publishDir
if ($LASTEXITCODE -ne 0) { throw "Publish-WinX64.ps1 failed with exit code $LASTEXITCODE." }

# The one-time automation must not remain in the released source tree.
Remove-Item -LiteralPath (Join-Path $repoRoot '.github/workflows/tomcat-backend-cleanup-1.3.11.yml') -Force
Remove-Item -LiteralPath $PSCommandPath -Force

git config user.name 'github-actions[bot]'
git config user.email '41898282+github-actions[bot]@users.noreply.github.com'
git add -A
if (-not (git diff --cached --quiet)) {
    git commit -m 'Remove retired Tomcat backend capabilities'
    git push origin HEAD:main
} else {
    throw 'Cleanup produced no repository changes.'
}
