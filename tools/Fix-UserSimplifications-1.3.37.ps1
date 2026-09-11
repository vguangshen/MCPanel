$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path (Get-Location) $Path))
}

function Write-Text([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $Path), $Text, $utf8)
}

function Replace-RegexOne([string]$Path, [string]$Pattern, [string]$Replacement) {
    $text = Read-Text $Path
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $matches = $regex.Matches($text)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one match in $Path, found $($matches.Count)."
    }
    Write-Text $Path ($regex.Replace($text, $Replacement, 1))
}

Write-Host 'Remove obsolete website template proxy handlers...'
$path = 'MainWindowTemplates.xaml.cs'
$text = Read-Text $path
$old = @'
    private void PinWebsite_Click(object sender, RoutedEventArgs e) => GetOwner(sender)?.PinWebsite_Click(sender, e);
    private void ExpandWebsite_Click(object sender, RoutedEventArgs e) => GetOwner(sender)?.ExpandWebsite_Click(sender, e);
'@
if (-not $text.Contains($old)) {
    throw 'Unable to locate obsolete website pin/expand proxy methods.'
}
Write-Text $path ($text.Replace($old, ''))

Write-Host 'Update website regression coverage for the restored full-card view...'
$websiteTest = @'
    [TestMethod]
    public void LargeWebsiteListVirtualizesAndSupportsFiltersAndScroll()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show(); var model = (MainViewModel)window.DataContext;
                model.RefreshCustomWebsites(Enumerable.Range(0, 1000).Select(index => new CustomWebsiteDefinition
                    { Name = "测试网站 " + index, PhysicalPath = @"Z:\Missing\site" + index }).ToArray());
                ((Grid)window.FindName("SitesPage")).Visibility = Visibility.Visible;
                window.UpdateLayout(); window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var list = (ListBox)window.FindName("WebsiteListScroll");
                var realized = Enumerable.Range(0, 1000).Count(index => list.ItemContainerGenerator.ContainerFromIndex(index) is not null);
                Assert.IsTrue(realized > 0 && realized < 100, $"1000 个网站仅应生成屏幕附近条目，实际 {realized}。");
                model.WebsiteTypeFilter = 1; Assert.IsTrue(model.WebsiteView.IsEmpty);
                model.WebsiteTypeFilter = 0; model.WebsiteSearchKeyword = "测试网站 998";
                var narrowed = model.WebsiteView.Cast<object>().Cast<WebsiteRow>().ToArray();
                Assert.IsTrue(narrowed.Length > 0 && narrowed.Length < 100, $"关键词筛选应显著缩小列表，实际 {narrowed.Length} 项。");
                Assert.IsTrue(narrowed.Any(row => row.Name == "测试网站 998"));
                model.WebsiteSearchKeyword = ""; list.ScrollIntoView(model.WebsiteRows[900]); window.UpdateLayout();
                Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(model.WebsiteRows[900]));
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void DialogDropdownUsesThemeColorsWhenActuallyExpanded
'@
Replace-RegexOne 'MCPanel.Tests/ReliabilityTests.WebsiteExperience.cs' '(?s)    \[TestMethod\]\r?\n    public void LargeWebsiteListVirtualizesAndSupportsFilterPinAndScroll\(\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    \[TestMethod\]\r?\n    public void DialogDropdownUsesThemeColorsWhenActuallyExpanded' $websiteTest

Write-Host 'Update Tomcat progress regression to the new no-progress behavior...'
$tomcatTest = @'
    [TestMethod]
    public void SharedTomcatRestartAndCatalinaDoNotDriveEnvironmentProgress()
    {
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(window.Contains("\"Restart\" when item.Kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        StringAssert.Contains(window, "\"Restart\" => await _runtimeService.RestartAsync(item.Kind)");
        StringAssert.Contains(window, "\"CatalinaRun\" => await _runtimeService.StartTomcatInCatalinaConsoleAsync()");
        Assert.IsFalse(window.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("tomcatProgress: kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在验证 Tomcat 端口稳定监听", StringComparison.Ordinal));
    }
}
'@
Replace-RegexOne 'MCPanel.Tests/ReliabilityTests.TomcatRestartProgress.cs' '(?s)    \[TestMethod\]\r?\n    public void SharedTomcatRestartAndCatalinaForwardProgressToEnvironmentCard\(\)\r?\n    \{.*?\r?\n    \}\r?\n\}' $tomcatTest

Write-Host 'Validation compatibility fixes applied.'
