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
                        Name = index == 43 ? "ONLY_TARGET_43" : $"平台 {index:D2}",
                        PhysicalPath = $@"Z:\Missing\site{index:D2}"
                    }));
                }

                model.WebsiteSearchKeyword = "ONLY_TARGET_43";
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

