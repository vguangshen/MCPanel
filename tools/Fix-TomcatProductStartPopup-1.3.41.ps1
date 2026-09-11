$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Old,
        [Parameter(Mandatory = $true)][string]$New,
        [int]$ExpectedCount = 1
    )

    $raw = Get-Content -LiteralPath $Path -Raw
    $count = ([regex]::Matches($raw, [regex]::Escape($Old))).Count
    if ($count -ne $ExpectedCount) {
        throw "Expected $ExpectedCount occurrence(s) in $Path but found $count."
    }

    Set-Content -LiteralPath $Path -Value ($raw.Replace($Old, $New)) -Encoding UTF8
}

# Remove the modal success dialog shared by both product Start and Catalina actions.
# Keep the returned text in the operation log so diagnostics are not lost.
$productsPath = 'MainWindow.Products.cs'
$oldProducts = @'
            var message = action switch
            {
                "Start" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: false),
                "Catalina" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: true),
                _ => throw new NotSupportedException("未知 Tomcat 应用操作。")
            };

            var runtime = await Task.Run(() => TomcatProductInstanceManager.GetRuntimeInfo(item.ProductId));
            item.ApplyTomcatRuntime(runtime);
            _model.RefreshWebsiteFilterForRuntimeChange();
            EnsureWebsiteRowVisible(item.ProductId);
            MessageBox.Show(message, "Tomcat 应用", MessageBoxButton.OK, MessageBoxImage.Information);
'@
$newProducts = @'
            var message = action switch
            {
                "Start" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: false),
                "Catalina" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: true),
                _ => throw new NotSupportedException("未知 Tomcat 应用操作。")
            };
            TomcatProductInstanceManager.WriteOperationLog(item.ProductId, $"界面操作完成：{message}");

            var runtime = await Task.Run(() => TomcatProductInstanceManager.GetRuntimeInfo(item.ProductId));
            item.ApplyTomcatRuntime(runtime);
            _model.RefreshWebsiteFilterForRuntimeChange();
            EnsureWebsiteRowVisible(item.ProductId);
'@
Replace-Exact -Path $productsPath -Old $oldProducts -New $newProducts

# Regression test: successful product Start/Catalina must stay non-modal.
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatProductStartNoPopup141.cs'
$testContent = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ProductTomcatStartAndCatalinaSuccessStayNonModal()
    {
        var source = ReadRepositoryFile("MainWindow.Products.cs");
        var start = source.IndexOf("internal async void InstalledProductTomcatAction_Click", StringComparison.Ordinal);
        var end = source.IndexOf("internal void InstalledProductIis_Click", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var handler = source.Substring(start, end - start);

        StringAssert.Contains(handler, "catalinaMode: false");
        StringAssert.Contains(handler, "catalinaMode: true");
        StringAssert.Contains(handler, "界面操作完成：{message}");
        Assert.IsFalse(handler.Contains("MessageBox.Show(message, \"Tomcat 应用\"", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("MessageBoxImage.Information", StringComparison.Ordinal));
        StringAssert.Contains(handler, "MessageBoxImage.Warning");
    }

    [TestMethod]
    public void EnvironmentRuntimeStartStopRestartAndCatalinaDoNotShowSuccessDialog()
    {
        var source = ReadRepositoryFile("MainWindow.Environment.cs");
        var start = source.IndexOf("internal async void EnvironmentRuntime_Click", StringComparison.Ordinal);
        var end = source.IndexOf("private async Task<string> ExecuteFrpRuntimeActionAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var handler = source.Substring(start, end - start);

        StringAssert.Contains(handler, "\"Start\" => await _runtimeService.StartAsync(item.Kind)");
        StringAssert.Contains(handler, "\"Stop\" => await _runtimeService.StopAsync(item.Kind)");
        StringAssert.Contains(handler, "\"Restart\" => await _runtimeService.RestartAsync(item.Kind)");
        StringAssert.Contains(handler, "\"CatalinaRun\" => await _runtimeService.StartTomcatInCatalinaConsoleAsync()");

        const string successPopup = "MessageBox.Show(message, \"环境\", MessageBoxButton.OK, MessageBoxImage.Information);";
        Assert.AreEqual(1, CountOccurrences(handler, successPopup));
        var uninstallGuard = handler.IndexOf("if (action == \"Uninstall\")", StringComparison.Ordinal);
        var popup = handler.IndexOf(successPopup, StringComparison.Ordinal);
        Assert.IsTrue(uninstallGuard >= 0 && popup > uninstallGuard,
            "环境运行操作的成功弹窗只能保留给卸载结果，启动/停止/重启/Catalina 不应弹窗。");
    }

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        var index = 0;
        while ((index = text.IndexOf(value, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += value.Length;
        }
        return count;
    }
}
'@
Set-Content -LiteralPath $testPath -Value $testContent -Encoding UTF8

# Version bump.
$projectPath = 'MCPanel.csproj'
$project = Get-Content -LiteralPath $projectPath -Raw
foreach ($pair in @(
    @('<Version>1.3.40</Version>', '<Version>1.3.41</Version>'),
    @('<FileVersion>1.3.40.0</FileVersion>', '<FileVersion>1.3.41.0</FileVersion>'),
    @('<AssemblyVersion>1.3.40.0</AssemblyVersion>', '<AssemblyVersion>1.3.41.0</AssemblyVersion>')
)) {
    if (-not $project.Contains($pair[0])) { throw "Missing version marker: $($pair[0])" }
    $project = $project.Replace($pair[0], $pair[1])
}
Set-Content -LiteralPath $projectPath -Value $project -Encoding UTF8

$notes = @'
# MCPanel 1.3.41

- 移除“已安装网站”中单个 Tomcat 产品执行“单独启动 / 以 Catalina 方式启动”成功后的模态提示框；启动完成后直接更新卡片运行状态，不再要求额外点击“确定”。
- 启动返回信息继续写入 Tomcat 产品操作日志，错误弹窗与必要的风险确认仍保留，避免静默吞掉诊断信息。
- 同步检查其他运行入口：环境页的启动、停止、重启和总 Tomcat Catalina 启动本来就只更新页面状态、不弹成功提示；Account API 的启停也只在失败时弹警告，因此未额外删除必要的错误/确认弹窗。
- 新增回归测试，防止产品 Start/Catalina 成功提示框重新出现，并约束环境运行操作只有卸载结果可以保留成功提示。
'@
Set-Content -LiteralPath 'RELEASE-NOTES.md' -Value $notes -Encoding UTF8

# Lightweight source audit for runtime-related modal dialogs.
Write-Host '=== Runtime modal audit ==='
$runtimeFiles = @('MainWindow.Products.cs', 'MainWindow.Environment.cs', 'AccountApiPage.xaml.cs')
foreach ($file in $runtimeFiles) {
    Write-Host "--- $file ---"
    Select-String -LiteralPath $file -Pattern 'MessageBox\.Show|ShowDialog\(' | ForEach-Object {
        Write-Host ("{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim())
    }
}

Write-Host 'Tomcat product start popup fix 1.3.41 applied.'
