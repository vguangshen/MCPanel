using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void AccountAndSettingsReflowAndRecoverAcrossRepeatedResize()
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();
                var account = (AccountApiPage)window.FindName("AccountApiPageControl");
                var settings = (ScrollViewer)window.FindName("SettingsPage");
                var appearance = (Border)window.FindName("AppearanceSettingsCard");
                var updater = (Border)window.FindName("SoftwareUpdateCard");
                var startup = (Border)window.FindName("StartupSettingsCard");
                double? wideHeight = null;
                foreach (var width in new[] { 1440d, 1024d, 1280d, 1024d, 1440d })
                {
                    window.Width = width;
                    window.Height = 720d;
                    account.Visibility = Visibility.Visible;
                    settings.Visibility = Visibility.Visible;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    var metrics = (Grid)account.FindName("MetricsGrid");
                    var connection = (Grid)account.FindName("ConnectionGrid");
                    var diagnostics = (Grid)account.FindName("DiagnosticsGrid");
                    var scroll = (ScrollViewer)account.FindName("PageScroll");
                    var narrow = width == 1024d;
                    Assert.IsTrue(metrics.ColumnDefinitions.Count >= 2 && metrics.ColumnDefinitions.Count <= 4);
                    Assert.AreEqual(narrow ? 1 : 2, connection.ColumnDefinitions.Count);
                    Assert.AreEqual(narrow ? 1 : 2, diagnostics.ColumnDefinitions.Count);
                    Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 1d,
                        "账号 API 不应产生横向溢出。");
                    scroll.ScrollToBottom();
                    window.UpdateLayout();
                    var bottomCard = (FrameworkElement)diagnostics.Children[1];
                    var bottom = bottomCard.TransformToAncestor(scroll)
                        .Transform(new Point(0d, bottomCard.ActualHeight));
                    Assert.IsTrue(bottom.Y <= scroll.ActualHeight + 1d,
                        "日志卡片底部必须可以通过纵向滚动到达。");
                    Assert.AreEqual(narrow ? 1 : 0, Grid.GetRow((Grid)updater.Parent));
                    Assert.AreEqual(narrow ? 0 : 1, Grid.GetColumn((Grid)updater.Parent));
                    Assert.AreEqual(228d, updater.MinHeight, 0.1d,
                        "更新卡片不得累积上次布局的实际高度。");
                    if (narrow)
                    {
                        Assert.IsTrue(double.IsNaN(appearance.Height));
                        Assert.IsTrue(double.IsNaN(startup.Height));
                    }
                    if (width == 1440d)
                    {
                        if (wideHeight.HasValue)
                            Assert.AreEqual(wideHeight.Value, appearance.ActualHeight, 1d,
                                "反复缩放后应恢复原来的卡片高度。");
                        wideHeight = appearance.ActualHeight;
                    }
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); completed.Set(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(30)), "页面重排测试超时。");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    [TestMethod]
    public void MainWindowResponsiveSurfaceUsesNativeScaleAtEverySupportedSize()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();

                var timer = typeof(MainWindow)
                    .GetField("_timer", BindingFlags.Instance | BindingFlags.NonPublic)?
                    .GetValue(window) as DispatcherTimer;
                timer?.Stop();

                var sizes = new[]
                {
                    new Size(1088d, 643d),
                    new Size(1024d, 640d),
                    new Size(1440d, 900d),
                    new Size(1600d, 820d),
                    new Size(1280d, 1000d)
                };

                foreach (var size in sizes)
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    var viewbox = (Viewbox)window.FindName("MainViewbox");
                    var surface = (Grid)window.FindName("DesignSurface");

                    Assert.AreEqual(Stretch.None, viewbox.Stretch,
                        "主窗口不得再整体缩放视觉树，否则小字号会落在非整数像素上。");
                    Assert.AreEqual(HorizontalAlignment.Stretch, viewbox.HorizontalAlignment);
                    Assert.AreEqual(VerticalAlignment.Stretch, viewbox.VerticalAlignment);
                    Assert.AreEqual(viewbox.ActualWidth, surface.ActualWidth, 1.0d,
                        $"{size.Width}x{size.Height} 下设计面必须按 1:1 填满视口宽度。");
                    Assert.AreEqual(viewbox.ActualHeight, surface.ActualHeight, 1.0d,
                        $"{size.Width}x{size.Height} 下设计面必须按 1:1 填满视口高度。");

                    Assert.AreEqual(TextFormattingMode.Display, TextOptions.GetTextFormattingMode(surface));
                    Assert.AreEqual(TextRenderingMode.ClearType, TextOptions.GetTextRenderingMode(surface));
                    Assert.AreEqual(TextHintingMode.Fixed, TextOptions.GetTextHintingMode(surface));
                    Assert.AreEqual(ClearTypeHint.Enabled, RenderOptions.GetClearTypeHint(surface));
                }

                var contentHost = (Grid)((FrameworkElement)window.FindName("PageFocusSentinel")).Parent;
                var accountPage = (FrameworkElement)window.FindName("AccountApiPageControl");
                var aiPage = (FrameworkElement)window.FindName("AiAnalysisPageControl");
                Assert.AreSame(contentHost, accountPage.Parent,
                    "账号 API 页面必须直接参与主内容区布局，不能再套强制滚动宿主。");
                Assert.AreSame(contentHost, aiPage.Parent,
                    "AI 分析页面必须直接参与主内容区布局，不能再套强制滚动宿主。");
                Assert.IsFalse(accountPage.Parent is ScrollViewer);
                Assert.IsFalse(aiPage.Parent is ScrollViewer);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "原生像素响应式主窗口渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"原生像素响应式主窗口布局验证失败：{failure}");
        }
    }

    [TestMethod]
    public void CompactShellUsesIconNavigationAndKeepsPagesInsideViewport()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();
                window.Width = 1024d;
                window.Height = 640d;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var navRail = (Grid)window.FindName("NavRail");
                var navShell = (Grid)((Border)((DockPanel)navRail.Parent).Parent).Parent;
                Assert.AreEqual(72d, navShell.ColumnDefinitions[0].Width.Value, 0.1d,
                    "1024 宽度下应折叠为图标导航，为内容卡片释放横向空间。");

                var navItems = (StackPanel)window.FindName("NavItemsPanel");
                Assert.IsTrue(navItems.Children.OfType<RadioButton>().All(button =>
                        string.IsNullOrEmpty(button.Content as string)),
                    "图标导航模式不应继续占用文字标签宽度。");

                var pageSentinel = (Border)window.FindName("PageFocusSentinel");
                Assert.AreEqual(new Thickness(16d), ((Grid)pageSentinel.Parent).Margin,
                    "低高度或窄窗口应减少内容边距。");

                var homePage = (ScrollViewer)window.FindName("HomePage");
                var homeSummary = (Border)window.FindName("HomeSystemSummaryCard");
                Assert.AreEqual(92d, homeSummary.Height, 0.1d,
                    "紧凑首页仍应保留系统概览的安全高度，避免硬件信息行拥挤。");
                Assert.IsTrue(homePage.ExtentHeight <= homePage.ViewportHeight + 3d,
                    $"1024x640 首页正常状态不应出现整页滚动；Extent={homePage.ExtentHeight}, Viewport={homePage.ViewportHeight}。");

                var environmentNav = navItems.Children
                    .OfType<RadioButton>()
                    .Single(button => string.Equals(button.CommandParameter as string, "Environment", StringComparison.Ordinal));
                environmentNav.IsChecked = true;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var environmentPage = (Grid)window.FindName("EnvironmentPage");
                var environmentItems = environmentPage.Children.OfType<ItemsControl>().Single();
                Assert.IsTrue(environmentPage.ActualWidth >= 880d,
                    $"图标导航后环境页应获得足够横向空间；实际 {environmentPage.ActualWidth:N1}。");
                Assert.AreEqual(6, environmentItems.Items.Count);

                var aiNav = navItems.Children
                    .OfType<RadioButton>()
                    .Single(button => string.Equals(button.CommandParameter as string, "AiAnalysis", StringComparison.Ordinal));
                aiNav.IsChecked = true;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                var aiPage = (FrameworkElement)window.FindName("AiAnalysisPageControl");
                Assert.AreEqual(((Grid)pageSentinel.Parent).ActualHeight, aiPage.ActualHeight, 2d,
                    "AI 分析页应直接填满主内容区高度，而不是使用大于视口的最小高度触发整页滚动。");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "紧凑布局一屏适配测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"紧凑布局一屏适配验证失败：{failure}");
        }
    }

    [TestMethod]
    public void CompactShellRestoresFullNavigationAtWideSize()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();

                var applyResponsiveLayout = typeof(MainWindow).GetMethod(
                    "ApplyResponsiveLayout",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.IsNotNull(applyResponsiveLayout, "必须存在主窗口响应式布局入口。");

                applyResponsiveLayout!.Invoke(window, new object[] { 1024d, 640d });
                window.UpdateLayout();

                var navRail = (Grid)window.FindName("NavRail");
                var navShell = (Grid)((Border)((DockPanel)navRail.Parent).Parent).Parent;
                Assert.AreEqual(72d, navShell.ColumnDefinitions[0].Width.Value, 0.1d);

                applyResponsiveLayout.Invoke(window, new object[] { 1440d, 900d });
                window.UpdateLayout();

                Assert.AreEqual(226d, navShell.ColumnDefinitions[0].Width.Value, 0.1d,
                    "宽窗口请求应恢复标准导航栏宽度。");
                var navItems = (StackPanel)window.FindName("NavItemsPanel");
                Assert.IsTrue(navItems.Children.OfType<RadioButton>().All(button =>
                        !string.IsNullOrWhiteSpace(button.Content?.ToString())),
                    "从紧凑窗口恢复宽窗口后必须恢复导航文字。");

                var appearance = (FrameworkElement)window.FindName("AppearanceSettingsCard");
                var updater = (FrameworkElement)window.FindName("SoftwareUpdateCard");
                var leftColumn = (Grid)appearance.Parent;
                var rightColumn = (Grid)updater.Parent;
                Assert.AreSame(leftColumn.Parent, rightColumn.Parent);
                Assert.AreEqual(0, Grid.GetRow(leftColumn));
                Assert.AreEqual(0, Grid.GetRow(rightColumn));
                Assert.AreEqual(0, Grid.GetColumn(leftColumn));
                Assert.AreEqual(1, Grid.GetColumn(rightColumn),
                    "宽窗口应保持设置页双列布局。");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "宽窄窗口恢复测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"宽窄窗口恢复验证失败：{failure}");
        }
    }

    [TestMethod]
    public void TextScalingAuditKeepsOnlyNonTextVisualScaling()
    {
        var responsiveSource = ReadRepositoryFile("MainWindow.ResponsiveLayout.cs");
        StringAssert.Contains(responsiveSource, "MainViewbox.Stretch = Stretch.None");
        Assert.IsFalse(responsiveSource.Contains("viewportWidth / ResponsiveWindowSizing.MainDesignWidth"),
            "主窗口不应再计算整棵视觉树的缩放比例。");
        Assert.IsFalse(responsiveSource.Contains("WrapEmbeddedPage"),
            "账号 API 与 AI 分析页不能再被额外 ScrollViewer 包裹。");

        var densitySource = ReadRepositoryFile("MainWindow.ResponsivePageDensity.cs");
        StringAssert.Contains(densitySource, "IconOnlyNavigationBreakpoint");
        StringAssert.Contains(densitySource, "ApplyHomePageDensity");
        StringAssert.Contains(densitySource, "ApplyEnvironmentPageDensity");

        var aiPage = ReadRepositoryFile("AiAnalysisPage.xaml");
        Assert.IsFalse(aiPage.Contains("MinHeight=\"620\""),
            "AI 分析页不能再通过固定 620 高度强制整页滚动。");

        var templates = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        var viewboxCount = templates.Split(new[] { "<Viewbox" }, StringSplitOptions.None).Length - 1;
        Assert.AreEqual(1, viewboxCount,
            "共享模板中只允许保留已安装完成图标的矢量 Viewbox，不应使用 Viewbox 缩放文本。");
        StringAssert.Contains(templates, "<Canvas Width=\"1024\" Height=\"1024\">");

        var marquee = ReadRepositoryFile("MarqueeText.cs");
        StringAssert.Contains(marquee, "SnapToDevicePixel");
        StringAssert.Contains(marquee, "TextRenderingMode.ClearType");

        var modal = ReadRepositoryFile("PanelModalWindow.cs");
        StringAssert.Contains(modal, "TextFormattingMode.Display");
        StringAssert.Contains(modal, "ClearTypeHint.Enabled");
    }
}
