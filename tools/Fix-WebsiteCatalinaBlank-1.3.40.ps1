$ErrorActionPreference = 'Stop'

function Replace-Literal {
    param(
        [Parameter(Mandatory=$true)][string]$Path,
        [Parameter(Mandatory=$true)][string]$Old,
        [Parameter(Mandatory=$true)][string]$New,
        [int]$ExpectedCount = 1
    )

    $text = Get-Content -LiteralPath $Path -Raw
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne $ExpectedCount) {
        throw "Expected $ExpectedCount occurrence(s) in $Path but found $count.`nOLD:`n$Old"
    }
    $text = $text.Replace($Old, $New)
    Set-Content -LiteralPath $Path -Value $text -Encoding UTF8
}

# 1. The header count and the ListBox must use the same CollectionView.
# Previously WebsiteSearchCountText counted two legacy views while the ListBox rendered WebsiteView,
# allowing '找到 1 / 44' to coexist with an empty/stale WebsiteView viewport.
Replace-Literal -Path 'ViewModels/MainViewModel.cs' `
    -Old '    private int VisibleWebsiteCount => VisibleInstalledWebsites.Cast<object>().Count() + VisibleCustomWebsites.Cast<object>().Count();' `
    -New '    private int VisibleWebsiteCount => WebsiteView?.Cast<object>().Count() ?? 0;'

# 2. Explicitly refresh WebsiteView after its batch source reset, and expose one small runtime-change refresh hook.
Replace-Literal -Path 'ViewModels/MainViewModel.Websites.cs' `
    -Old @'
        _websiteRows.Replace(rows);
        NotifyWebsiteFilter();
'@ `
    -New @'
        _websiteRows.Replace(rows);
        WebsiteView.Refresh();
        NotifyWebsiteFilter();
'@

Replace-Literal -Path 'ViewModels/MainViewModel.Websites.cs' `
    -Old @'
    private void RefreshWebsiteFilter()
    {
        VisibleInstalledWebsites.Refresh(); VisibleCustomWebsites.Refresh(); WebsiteView.Refresh(); NotifyWebsiteFilter();
    }
'@ `
    -New @'
    private void RefreshWebsiteFilter()
    {
        VisibleInstalledWebsites.Refresh(); VisibleCustomWebsites.Refresh(); WebsiteView.Refresh(); NotifyWebsiteFilter();
    }

    internal void RefreshWebsiteFilterForRuntimeChange() => RefreshWebsiteFilter();
'@

# 3. Centralize applying a targeted Tomcat runtime snapshot so a button action does not reuse
# the stale 30-second WebsiteRuntimeSnapshot.Latest value.
Replace-Literal -Path 'ViewModels/ProductViewModels.cs' `
    -Old @'
    public void SetOperationState(string text)
    {
        RuntimeStatusText = text;
        RuntimeStatusBrush = Brushes.Goldenrod;
    }

    public void RefreshRuntime()
'@ `
    -New @'
    public void SetOperationState(string text)
    {
        RuntimeStatusText = text;
        RuntimeStatusBrush = Brushes.Goldenrod;
    }

    internal void ApplyTomcatRuntime(TomcatProductRuntimeInfo runtime)
    {
        TomcatRuntimeMode = runtime.Mode;
        RuntimeStatusText = TomcatProductInstanceManager.FormatRuntimeStatus(runtime);
        RuntimeStatusBrush = runtime.Mode switch
        {
            TomcatProductRuntimeMode.Shared => Brushes.MediumSeaGreen,
            TomcatProductRuntimeMode.Independent => Brushes.MediumSeaGreen,
            TomcatProductRuntimeMode.Catalina => Brushes.DeepSkyBlue,
            TomcatProductRuntimeMode.PortConflict => Brushes.IndianRed,
            _ => Brushes.Goldenrod
        };
        CanBrowse = runtime.IsRunning && runtime.PortListening;
    }

    public void RefreshRuntime()
'@

Replace-Literal -Path 'ViewModels/ProductViewModels.cs' `
    -Old @'
            TomcatRuntimeMode = runtime.Mode;

            SiteDisplayText = $"Tomcat / {ProductId}";
            PoolDisplayText = $"应用上下文：/{ProductId}";
            Url = $"http://localhost:{tomcatDeployment.Value.Port}/{ProductId}/";

            RuntimeStatusText = TomcatProductInstanceManager.FormatRuntimeStatus(runtime);
            RuntimeStatusBrush = runtime.Mode switch
            {
                TomcatProductRuntimeMode.Shared => Brushes.MediumSeaGreen,
                TomcatProductRuntimeMode.Independent => Brushes.MediumSeaGreen,
                TomcatProductRuntimeMode.Catalina => Brushes.DeepSkyBlue,
                TomcatProductRuntimeMode.PortConflict => Brushes.IndianRed,
                _ => Brushes.Goldenrod
            };
            CanBrowse = runtime.IsRunning && runtime.PortListening;
'@ `
    -New @'
            SiteDisplayText = $"Tomcat / {ProductId}";
            PoolDisplayText = $"应用上下文：/{ProductId}";
            Url = $"http://localhost:{tomcatDeployment.Value.Port}/{ProductId}/";

            ApplyTomcatRuntime(runtime);
'@

Replace-Literal -Path 'ViewModels/ProductViewModels.cs' `
    -Old @'
        if (IsTomcatDeployment && snapshot.Tomcat.TryGetValue(ProductId, out var runtime))
        {
            TomcatRuntimeMode = runtime.Mode;
            CanBrowse = runtime.IsRunning && runtime.PortListening;
            RuntimeStatusText = TomcatProductInstanceManager.FormatRuntimeStatus(runtime);
            RuntimeStatusBrush = CanBrowse ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
        }
'@ `
    -New @'
        if (IsTomcatDeployment && snapshot.Tomcat.TryGetValue(ProductId, out var runtime))
        {
            ApplyTomcatRuntime(runtime);
        }
'@

# 4. After Start/Catalina, get the actual product process state immediately, refresh the same view
# used by the ListBox, and bring that row back into the viewport. This is a status snapshot only;
# it does not wait for HTTP readiness and does not reintroduce Catalina readiness polling.
Replace-Literal -Path 'MainWindow.Products.cs' `
    -Old @'
    internal async void InstalledProductTomcatAction_Click(object sender, RoutedEventArgs e)
'@ `
    -New @'
    private void EnsureWebsiteRowVisible(string productId)
    {
        var row = _model.WebsiteView.Cast<object>()
            .OfType<WebsiteRow>()
            .FirstOrDefault(candidate => candidate.Item is InstalledProductItem product &&
                string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase));
        if (row is null)
        {
            return;
        }

        WebsiteListScroll.ScrollIntoView(row);
        WebsiteListScroll.UpdateLayout();
        WebsiteListScroll.Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                if (WebsiteListScroll.Items.Contains(row))
                {
                    WebsiteListScroll.ScrollIntoView(row);
                }
            }));
    }

    internal async void InstalledProductTomcatAction_Click(object sender, RoutedEventArgs e)
'@

Replace-Literal -Path 'MainWindow.Products.cs' `
    -Old @'
            if (action == "Catalina")
            {
                await Task.Delay(1200);
            }

            item.RefreshRuntime();
            MessageBox.Show(message, "Tomcat 应用", MessageBoxButton.OK, MessageBoxImage.Information);
'@ `
    -New @'
            var runtime = await Task.Run(() => TomcatProductInstanceManager.GetRuntimeInfo(item.ProductId));
            item.ApplyTomcatRuntime(runtime);
            _model.RefreshWebsiteFilterForRuntimeChange();
            EnsureWebsiteRowVisible(item.ProductId);
            MessageBox.Show(message, "Tomcat 应用", MessageBoxButton.OK, MessageBoxImage.Information);
'@

# 5. Keep virtualization for large lists, but avoid Recycling containers for the dynamic full-card template.
# Standard virtualization is still covered by the 1000-row UI regression test and avoids stale recycled cards
# after a CollectionView.Refresh() while a management panel is expanded.
Replace-Literal -Path 'MainWindow.xaml' `
    -Old '                        ScrollViewer.CanContentScroll="True" VirtualizingPanel.IsVirtualizing="True" VirtualizingPanel.VirtualizationMode="Recycling"`r`n                        VirtualizingPanel.ScrollUnit="Pixel" VirtualizingPanel.CacheLength="1" Visibility="{Binding WebsiteContentVisibility}">' `
    -New '                        ScrollViewer.CanContentScroll="True" VirtualizingPanel.IsVirtualizing="True" VirtualizingPanel.VirtualizationMode="Standard"`r`n                        VirtualizingPanel.ScrollUnit="Pixel" VirtualizingPanel.CacheLength="1" Visibility="{Binding WebsiteContentVisibility}">'

# 6. Add a regression test for the exact blank-list pattern and source guards for the Catalina path.
$testPath = 'MCPanel.Tests/ReliabilityTests.WebsiteCatalinaBlank140.cs'
$test = @'
using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteSearchCountUsesTheSameViewRenderedByTheListBox()
    {
        RunWebsiteUiTest(() =>
        {
            var model = new MainViewModel(false);
            try
            {
                for (var index = 0; index < 44; index++)
                {
                    model.CustomWebsites.Add(new CustomWebsiteItem(new CustomWebsiteDefinition
                    {
                        Name = $"平台 {index:D2}",
                        PhysicalPath = $@"Z:\Missing\site{index:D2}"
                    }));
                }

                model.WebsiteSearchKeyword = "平台 43";
                Assert.AreEqual(1, model.WebsiteView.Cast<object>().Count());
                Assert.AreEqual("找到 1 / 44 个网站", model.WebsiteSearchCountText);
                model.RefreshWebsiteFilterForRuntimeChange();
                Assert.AreEqual(1, model.WebsiteView.Cast<object>().Count());
                Assert.AreEqual("找到 1 / 44 个网站", model.WebsiteSearchCountText);
            }
            finally { model.Dispose(); }
        });
    }

    [TestMethod]
    public void NarrowWebsiteViewRemainsRealizedAfterRuntimeRefresh()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show();
                var model = (MainViewModel)window.DataContext;
                model.RefreshCustomWebsites(Enumerable.Range(0, 44).Select(index => new CustomWebsiteDefinition
                {
                    Name = $"YX030{index:D2}",
                    PhysicalPath = $@"Z:\Missing\YX030{index:D2}"
                }).ToArray());
                ((Grid)window.FindName("SitesPage")).Visibility = Visibility.Visible;
                model.WebsiteSearchKeyword = "YX03043";
                model.RefreshWebsiteFilterForRuntimeChange();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var list = (ListBox)window.FindName("WebsiteListScroll");
                Assert.AreEqual(1, list.Items.Count);
                var row = model.WebsiteView.Cast<object>().Cast<WebsiteRow>().Single();
                list.ScrollIntoView(row);
                window.UpdateLayout();
                Assert.IsNotNull(list.ItemContainerGenerator.ContainerFromItem(row),
                    "运行状态刷新后，唯一匹配的网站卡片必须继续生成，不能只剩标题计数而列表空白。");
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ProductCatalinaUiRefreshUsesFreshRuntimeAndDoesNotWaitForReadiness()
    {
        var source = ReadRepositoryFile("MainWindow.Products.cs");
        var start = source.IndexOf("internal async void InstalledProductTomcatAction_Click", StringComparison.Ordinal);
        Assert.IsTrue(start >= 0);
        var end = source.IndexOf("internal void InstalledProductIis_Click", start, StringComparison.Ordinal);
        Assert.IsTrue(end > start);
        var handler = source.Substring(start, end - start);

        StringAssert.Contains(handler, "TomcatProductInstanceManager.GetRuntimeInfo(item.ProductId)");
        StringAssert.Contains(handler, "item.ApplyTomcatRuntime(runtime)");
        StringAssert.Contains(handler, "RefreshWebsiteFilterForRuntimeChange");
        StringAssert.Contains(handler, "EnsureWebsiteRowVisible(item.ProductId)");
        Assert.IsFalse(handler.Contains("Task.Delay(1200)", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(handler.Contains("WaitForPortAsync", StringComparison.Ordinal));

        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var listStart = xaml.IndexOf("x:Name=\"WebsiteListScroll\"", StringComparison.Ordinal);
        Assert.IsTrue(listStart >= 0);
        var listEnd = xaml.IndexOf("</ListBox>", listStart, StringComparison.Ordinal);
        var list = xaml.Substring(listStart, listEnd - listStart);
        StringAssert.Contains(list, "VirtualizingPanel.IsVirtualizing=\"True\"");
        StringAssert.Contains(list, "VirtualizingPanel.VirtualizationMode=\"Standard\"");
        Assert.IsFalse(list.Contains("VirtualizationMode=\"Recycling\"", StringComparison.Ordinal));
    }
}
'@
Set-Content -LiteralPath $testPath -Value $test -Encoding UTF8

# 7. Bump the validated source to 1.3.40 and prepare release notes.
$project = Get-Content -LiteralPath 'MCPanel.csproj' -Raw
foreach ($pair in @(
    @('<Version>1.3.39</Version>', '<Version>1.3.40</Version>'),
    @('<FileVersion>1.3.39.0</FileVersion>', '<FileVersion>1.3.40.0</FileVersion>'),
    @('<AssemblyVersion>1.3.39.0</AssemblyVersion>', '<AssemblyVersion>1.3.40.0</AssemblyVersion>')
)) {
    if (-not $project.Contains($pair[0])) { throw "Version marker missing: $($pair[0])" }
    $project = $project.Replace($pair[0], $pair[1])
}
Set-Content -LiteralPath 'MCPanel.csproj' -Value $project -Encoding UTF8

$notes = @'
# MCPanel 1.3.40

- 修复“已安装网站”中对单个 Java 产品执行“单独启动 / 以 Catalina 方式启动”后，搜索结果计数仍存在但网站卡片区域可能变成空白的问题。
- 网站标题计数与实际 ListBox 统一使用 WebsiteView，批量重建网站行后强制刷新同一视图，避免两个 CollectionView 状态不同步。
- Tomcat 产品操作完成后直接读取该产品真实 Java/CATALINA_BASE 运行状态并更新卡片，不再拿 30 秒缓存快照覆盖刚启动的状态；Catalina 仍不等待端口或 HTTP 就绪。
- 网站列表继续保持虚拟化，但由 Recycling 改为 Standard，并在运行状态刷新后主动将当前产品滚回可视区域，避免动态卡片与展开管理面板被回收后出现空白视口。
'@
Set-Content -LiteralPath 'RELEASE-NOTES.md' -Value $notes -Encoding UTF8

Write-Host 'Website Catalina blank-state fix 1.3.40 applied.'
