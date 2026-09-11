$ErrorActionPreference = 'Stop'

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText((Join-Path $PWD $Path), [Text.UTF8Encoding]::new($false))
}

function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText((Join-Path $PWD $Path), $Content, [Text.UTF8Encoding]::new($false))
}

# Remove the ResourceDictionary event proxy left behind after removing the
# manual "检测访问" button. Keeping this proxy caused the first 1.3.42 build to fail.
$path = 'MainWindowTemplates.xaml.cs'
$text = Read-Utf8 $path
$text = [regex]::Replace($text,
    '(?m)^\s*private void ProbeWebsite_Click\(object sender, RoutedEventArgs e\) => GetOwner\(sender\)\?\.ProbeWebsite_Click\(sender, e\);\r?\n',
    '')
if ($text.Contains('ProbeWebsite_Click')) { throw 'ProbeWebsite_Click proxy still exists.' }
Write-Utf8 $path $text

# Remove website-page status controls. The page is now for locating/configuring sites,
# not continuously probing whether every IIS/Tomcat site is currently running.
$path = 'MainWindow.xaml'
$text = Read-Utf8 $path
$text = $text.Replace(
    '<Grid.ColumnDefinitions><ColumnDefinition Width="132" /><ColumnDefinition Width="142" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>',
    '<Grid.ColumnDefinitions><ColumnDefinition Width="132" /><ColumnDefinition Width="*" /></Grid.ColumnDefinitions>')
$text = [regex]::Replace($text,
    '(?s)\s*<ComboBox Grid.Column="1" SelectedIndex="\{Binding WebsiteStateFilter\}".*?</ComboBox>',
    '')
$text = [regex]::Replace($text,
    '(?m)^\s*<Button Grid.Column="3" Content="刷新状态"[^\r\n]*/>\r?\n',
    '')
$text = [regex]::Replace($text,
    '(?m)^\s*<TextBlock Text="\{Binding WebsiteOperationText\}"[^\r\n]*/>\r?\n',
    '')
$text = $text.Replace('管理平台绑定、域名和运行状态，输入关键词快速定位软件。', '管理平台绑定、域名和 SSL，输入关键词快速定位软件。')
if ($text.Contains('WebsiteStateFilter') -or $text.Contains('Content="刷新状态"') -or $text.Contains('Binding WebsiteOperationText')) {
    throw 'Website status controls were not fully removed from MainWindow.xaml.'
}
Write-Utf8 $path $text

# Remove status dots/text from website cards so stale runtime state is not presented
# after background status probing has been disabled.
$path = 'Resources/MainWindowTemplates.xaml'
$text = Read-Utf8 $path
$text = [regex]::Replace($text,
    '(?s)\s*<Border Grid.Column="1" Width="22" Height="22".*?ToolTip="\{Binding RuntimeStatusText\}">\s*<Ellipse Width="9" Height="9" Fill="\{Binding RuntimeStatusBrush\}" />\s*</Border>',
    '')
$text = [regex]::Replace($text,
    '(?s)\s*<Border Width="20" Height="20" CornerRadius="10".*?<Ellipse Width="8" Height="8" Fill="\{Binding StatusBrush\}" />\s*</Border>\s*<TextBlock Text="\{Binding StatusText\}"[^>]*/>',
    '')
if ($text.Contains('ToolTip="{Binding RuntimeStatusText}"')) { throw 'Installed-product runtime status dot still exists.' }
Write-Utf8 $path $text

# Stop the one-second UI timer from launching website runtime snapshots.
$path = 'MainWindow.xaml.cs'
$text = Read-Utf8 $path
$text = [regex]::Replace($text,
    '(?m)^\s*if \(_monitoringStarted\)\r?\n\s*_ = _model\.RefreshWebsiteStatesAsync\(IsVisible && SitesPage\.IsVisible, WindowState == WindowState\.Minimized\);\r?\n',
    '')
if ($text.Contains('RefreshWebsiteStatesAsync(IsVisible && SitesPage.IsVisible')) {
    throw 'Automatic website runtime refresh is still wired to the main timer.'
}
Write-Utf8 $path $text

# Remove the manual Refresh Status handler too. The manual HTTP probe handler is
# removed by the first patch script in this workflow.
$path = 'MainWindow.Websites.cs'
$text = Read-Utf8 $path
$text = [regex]::Replace($text,
    '(?m)^\s*private async void RefreshWebsiteStates_Click\(object sender, RoutedEventArgs e\)\r?\n\s*=> await _model\.RefreshWebsiteStatesAsync\(true, false, force: true\);\r?\n',
    '')
if ($text.Contains('RefreshWebsiteStates_Click')) { throw 'RefreshWebsiteStates_Click still exists.' }
Write-Utf8 $path $text

# Remove the actual website runtime refresh path and the status filter. Keep the
# generic WebsiteOperationText property because other configuration actions still
# write human-readable operation feedback, although it is no longer shown as a
# permanent status-monitoring line on the page.
$path = 'ViewModels/MainViewModel.Websites.cs'
$text = Read-Utf8 $path
$text = [regex]::Replace($text, '(?m)^\s*private int _websiteStateFilter;\r?\n', '')
$text = [regex]::Replace($text, '(?m)^\s*private readonly WebsiteRefreshSchedule _websiteRefreshSchedule = new\(\);\r?\n', '')
$text = [regex]::Replace($text, '(?m)^\s*public int WebsiteStateFilter \{[^\r\n]*\}\r?\n', '')
$text = [regex]::Replace($text,
    '(?s)    private bool MatchesWebsiteOptions\(bool java, bool running\) =>\s*\(_websiteTypeFilter == 0 \|\| \(_websiteTypeFilter == 1 \? java : !java\)\) &&\s*\(_websiteStateFilter == 0 \|\| \(_websiteStateFilter == 1 \? running : !running\)\);',
    '    private bool MatchesWebsiteOptions(bool java) =>`r`n        _websiteTypeFilter == 0 || (_websiteTypeFilter == 1 ? java : !java);')
$text = [regex]::Replace($text,
    '(?s)\r?\n    internal async Task RefreshWebsiteStatesAsync\(bool visible, bool minimized, bool force = false\).*?\r?\n    private sealed class WebsiteRowCollection',
    "`r`n    private sealed class WebsiteRowCollection")
if ($text.Contains('WebsiteStateFilter') -or $text.Contains('RefreshWebsiteStatesAsync') -or $text.Contains('_websiteRefreshSchedule')) {
    throw 'Website status refresh implementation was not fully removed.'
}
Write-Utf8 $path $text

$path = 'ViewModels/MainViewModel.cs'
$text = Read-Utf8 $path
$text = $text.Replace('MatchesWebsiteOptions(item.IsTomcatDeployment, item.CanBrowse)', 'MatchesWebsiteOptions(item.IsTomcatDeployment)')
$text = $text.Replace('MatchesWebsiteOptions(false, item.IsRunning)', 'MatchesWebsiteOptions(false)')
$text = [regex]::Replace($text, '(?m)^\s*_websiteRefreshSchedule\.RequestRefresh\(\);\r?\n', '')
$text = $text.Replace(
    '集中管理产品网站与自定义 IIS 网站，包括域名、SSL、绑定和运行状态。',
    '集中管理产品网站与自定义 IIS 网站，包括域名、SSL 和平台绑定。')
if ($text.Contains('MatchesWebsiteOptions(item.IsTomcatDeployment, item.CanBrowse)') -or
    $text.Contains('MatchesWebsiteOptions(false, item.IsRunning)') -or
    $text.Contains('_websiteRefreshSchedule.RequestRefresh')) {
    throw 'MainViewModel still depends on website runtime-status filtering.'
}
Write-Utf8 $path $text

# Extend release notes produced by the first script.
$path = 'RELEASE-NOTES.md'
$text = Read-Utf8 $path
$text = $text.Replace(
    '- 删除“已安装网站”里的“检测访问”按钮以及对应的临时 HTTP GET 探测代码；网站页继续使用运行时快照刷新服务/进程状态。',
    '- 删除“已安装网站”里的“检测访问”按钮以及对应的临时 HTTP GET 探测代码。`r`n- 删除“已安装网站”的服务状态检测功能：移除状态筛选、刷新状态按钮、30 秒自动状态刷新、状态提示行以及卡片运行状态点；网站页只负责搜索、平台绑定、域名/SSL 和显式运行操作。')
Write-Utf8 $path $text

# Regression guards for the UI and background timer.
$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteStatusMonitoringUiAndTimerAreRemoved()
    {
        var window = ReadRepositoryFile("MainWindow.xaml");
        var shell = ReadRepositoryFile("MainWindow.xaml.cs");
        var sites = ReadRepositoryFile("MainWindow.Websites.cs");
        var viewModel = ReadRepositoryFile("ViewModels/MainViewModel.Websites.cs");
        var templates = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");

        Assert.IsFalse(window.Contains("WebsiteStateFilter", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("刷新状态", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("Binding WebsiteOperationText", StringComparison.Ordinal));
        Assert.IsFalse(shell.Contains("RefreshWebsiteStatesAsync(IsVisible && SitesPage.IsVisible", StringComparison.Ordinal));
        Assert.IsFalse(sites.Contains("RefreshWebsiteStates_Click", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("RefreshWebsiteStatesAsync", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("WebsiteStateFilter", StringComparison.Ordinal));
        Assert.IsFalse(templates.Contains("RuntimeStatusText", StringComparison.Ordinal));
    }

    [TestMethod]
    public void WebsiteFilteringNoLongerDependsOnRuntimeState()
    {
        var code = ReadRepositoryFile("ViewModels/MainViewModel.cs");
        StringAssert.Contains(code, "MatchesWebsiteOptions(item.IsTomcatDeployment)");
        StringAssert.Contains(code, "MatchesWebsiteOptions(false)");
        Assert.IsFalse(code.Contains("MatchesWebsiteOptions(item.IsTomcatDeployment, item.CanBrowse)", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("MatchesWebsiteOptions(false, item.IsRunning)", StringComparison.Ordinal));
    }

    [TestMethod]
    public void RemovedWebsiteProbeHasNoTemplateProxy()
    {
        var proxy = ReadRepositoryFile("MainWindowTemplates.xaml.cs");
        Assert.IsFalse(proxy.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.WebsiteStatusRemoved142.cs' $test

Write-Host 'Website status monitoring and stale probe proxy removed for 1.3.42.'
