using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteSearchFiltersBothGroupsAndPreservesSourceCollections()
    {
        RunWebsiteUiTest(() =>
        {
            var model = new MainViewModel(false);
            try
            {
                var first = new CustomWebsiteItem(new CustomWebsiteDefinition { Name = "教学平台", PhysicalPath = @"D:\web\school", Domains = ["Learn.Example.com"] });
                var second = new CustomWebsiteItem(new CustomWebsiteDefinition { Name = "考试平台", PhysicalPath = @"D:\web\exam" });
                model.CustomWebsites.Add(first);
                model.CustomWebsites.Add(second);
                var product = new InstalledProductItem(new ProductItem("SEARCH01", "电子商务实训", "", "", ProductSource.Online), @"D:\web\SEARCH01");
                model.InstalledProducts.Add(product);
                model.WebsiteSearchKeyword = "  LEARN  教学 ";
                Assert.AreSame(first, model.VisibleCustomWebsites.Cast<object>().Single());
                Assert.IsTrue(model.VisibleInstalledWebsites.IsEmpty);
                Assert.AreEqual("找到 1 / 3 个网站", model.WebsiteSearchCountText);
                model.WebsiteSearchKeyword = "search01";
                Assert.AreSame(product, model.VisibleInstalledWebsites.Cast<object>().Single());
                Assert.AreEqual(Visibility.Collapsed, model.CustomWebsiteGroupVisibility);
                model.WebsiteSearchKeyword = "不存在";
                Assert.AreEqual(Visibility.Visible, model.WebsiteNoResultsVisibility);
                model.CustomWebsites.Add(new CustomWebsiteItem(new CustomWebsiteDefinition { Name = "不存在关键词示例", PhysicalPath = @"D:\example" }));
                Assert.AreEqual(Visibility.Collapsed, model.WebsiteNoResultsVisibility);
                Assert.AreEqual("找到 1 / 4 个网站", model.WebsiteSearchCountText);
                model.WebsiteSearchKeyword = " ";
                Assert.AreEqual(3, model.VisibleCustomWebsites.Cast<object>().Count());
                Assert.AreEqual(1, model.InstalledProducts.Count);
                Assert.AreSame(first, model.CustomWebsites[0]);
                model.CustomWebsites.Clear();
                model.InstalledProducts.Clear();
                Assert.AreEqual(Visibility.Visible, model.WebsiteEmptyVisibility);
            }
            finally { model.Dispose(); }
        });
    }

    [TestMethod]
    public void WebsiteToolbarAndBindingDialogRenderAcrossThemesAndWindowSizes()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show();
                var model = (MainViewModel)window.DataContext;
                model.InstalledProducts.Add(new InstalledProductItem(new ProductItem("DS0101", "电子商务综合实训与竞赛系统", "", "/Assets/Logo/DS0102.png", ProductSource.Online) { RunEnvironment = "Framework4.5", DevLanguage = "C#" }, @"D:\MCPanel\web\DS0101"));
                var page = (Grid)window.FindName("SitesPage");
                foreach (var dark in new[] { false, true })
                {
                    PanelThemeService.Apply(dark, window.Resources);
                    foreach (var width in new[] { 1024d, 1440d })
                    {
                        window.Width = width;
                        window.Height = 800;
                        page.Visibility = Visibility.Visible;
                        window.UpdateLayout();
                        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                        window.UpdateLayout();
                        var search = (TextBox)window.FindName("WebsiteSearchBox");
                        Assert.IsTrue(search.ActualWidth > 200, "窄窗口仍应保留可输入的搜索区域。");
                        Assert.IsTrue(search.ActualWidth < 360, "搜索框应保持紧凑，不应铺满顶部。");
                        foreach (var keyword in new[] { "电", "电子", "电子商", "电子商务", "电子商", "电", "不存在", "" })
                        {
                            search.Text = keyword;
                            Assert.AreEqual(keyword, model.WebsiteSearchKeyword, "每次输入或删除都应立即更新搜索词，无需回车或失焦。");
                            Assert.AreEqual(keyword == "不存在" ? 0 : 1, model.VisibleInstalledWebsites.Cast<object>().Count());
                        }
                        foreach (var name in new[] { "BindPlatformButton", "WebsiteSearchBox" })
                        {
                            var element = (FrameworkElement)window.FindName(name);
                            var right = element.TransformToAncestor(page).Transform(new Point(element.ActualWidth, 0)).X;
                            Assert.IsTrue(right <= page.ActualWidth + 1, name + " 不应超出页面。");
                        }
                        var bindButton = (Button)window.FindName("BindPlatformButton");
                        var searchCenter = search.TransformToAncestor(page).Transform(new Point(0, search.ActualHeight / 2));
                        var buttonCenter = bindButton.TransformToAncestor(page).Transform(new Point(0, bindButton.ActualHeight / 2));
                        Assert.AreEqual(searchCenter.Y, buttonCenter.Y, 1, "搜索框和绑定按钮必须保持同一行。");
                        Assert.IsTrue(searchCenter.X + search.ActualWidth < buttonCenter.X, "搜索框必须位于绑定按钮左侧。");
                        var menu = (System.Windows.Controls.Primitives.Popup)window.FindName("PlatformBindingMenu");
                        Assert.IsFalse(menu.IsOpen);
                        bindButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                        window.UpdateLayout();
                        Assert.IsTrue(menu.IsOpen, "点击后才展开平台选项。");
                        Assert.AreEqual("绑定 Java 平台", ((Button)window.FindName("BindJavaPlatformOption")).Content);
                        Assert.AreEqual("绑定 .NET 平台", ((Button)window.FindName("BindDotNetPlatformOption")).Content);
                        menu.IsOpen = false;
                        search.Text = "无匹配软件";
                        window.UpdateLayout();
                        Assert.AreEqual(Visibility.Visible, model.WebsiteNoResultsVisibility);
                        search.Text = "";
                        window.UpdateLayout();
                        if (dark && width == 1440d)
                        {
                            var bitmap = new RenderTargetBitmap((int)page.ActualWidth, (int)page.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                            bitmap.Render(page);
                            var encoder = new PngBitmapEncoder();
                            encoder.Frames.Add(BitmapFrame.Create(bitmap));
                            using var output = File.Create(Path.Combine(Path.GetTempPath(), "MCPanel-websites-preview.png"));
                            encoder.Save(output);
                        }
                    }
                    foreach (var java in new[] { false, true })
                    {
                        var dialog = new PlatformBindingDialog(java) { Owner = window };
                        try
                        {
                            PanelThemeService.Apply(dark, dialog.Resources);
                            dialog.Show();
                            dialog.UpdateLayout();
                            Assert.IsFalse(((Button)dialog.FindName("BindButton")).IsEnabled);
                            Assert.IsNull(dialog.FindName("ProductBox"));
                            Assert.IsNotNull(dialog.FindName("BrowseButton"));
                            ((TextBox)dialog.FindName("NameBox")).Text = "我的本地平台";
                            ((TextBox)dialog.FindName("PathBox")).Text = @"D:\LocalSoftware";
                            Assert.IsTrue(((Button)dialog.FindName("BindButton")).IsEnabled);
                        }
                        finally { dialog.Close(); }
                    }
                }
            }
            finally { window.Close(); }
        });
    }

    [TestMethod]
    public void ManualPlatformStorePersistsCustomNameAndKeepsUserDirectoryOnRemoval()
    {
        var root = CreateTemporaryDirectory();
        var storeFile = Path.Combine(root, "state", "manual-platforms.json");
        var software = Path.Combine(root, "My Local Shop");
        Directory.CreateDirectory(Path.Combine(software, "WEB-INF"));
        File.WriteAllText(Path.Combine(software, "index.html"), "local");
        try
        {
            var store = new ManualPlatformStore(storeFile);
            var definition = store.Prepare("我的本地 Java 平台", software, true);
            store.Save(definition);

            var loaded = new ManualPlatformStore(storeFile).Load();
            Assert.AreEqual(1, loaded.Count);
            Assert.AreEqual("我的本地 Java 平台", loaded[0].Name);
            Assert.AreEqual(Path.GetFullPath(software), loaded[0].Path);
            var product = loaded[0].ToProduct();
            Assert.IsTrue(product.IsExternalPlatform);
            Assert.AreEqual(loaded[0].Path, ProductInstallPathResolver.ResolveProductDirectory(product));

            store.Remove(definition.Id);
            Assert.AreEqual(0, new ManualPlatformStore(storeFile).Load().Count);
            Assert.IsTrue(File.Exists(Path.Combine(software, "index.html")), "解除绑定不得删除用户软件目录。");
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void ManualPlatformStoreRejectsEmptyDirectoryAndInvalidJavaDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var empty = Path.Combine(root, "empty");
            Directory.CreateDirectory(empty);
            var store = new ManualPlatformStore(Path.Combine(root, "state.json"));
            Assert.ThrowsException<InvalidDataException>(() => store.Prepare("空目录", empty, false));

            var nonJava = Path.Combine(root, "static");
            Directory.CreateDirectory(nonJava);
            File.WriteAllText(Path.Combine(nonJava, "index.html"), "static");
            Assert.ThrowsException<InvalidDataException>(() => store.Prepare("错误 Java 目录", nonJava, true));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private static void RunWebsiteUiTest(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(40)), "网站 UI 测试超时。");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}

