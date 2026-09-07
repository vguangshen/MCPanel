using System;
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
                    .GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
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

                var accountPage = (FrameworkElement)window.FindName("AccountApiPageControl");
                var aiPage = (FrameworkElement)window.FindName("AiAnalysisPageControl");
                Assert.IsInstanceOfType(accountPage.Parent, typeof(ScrollViewer),
                    "账号 API 页面在小高度窗口中应使用滚动而不是整体缩放。");
                Assert.IsInstanceOfType(aiPage.Parent, typeof(ScrollViewer),
                    "AI 分析页面在小高度窗口中应使用滚动而不是整体缩放。");
                Assert.AreEqual(ScrollBarVisibility.Auto,
                    ((ScrollViewer)accountPage.Parent).VerticalScrollBarVisibility);
                Assert.AreEqual(ScrollBarVisibility.Auto,
                    ((ScrollViewer)aiPage.Parent).VerticalScrollBarVisibility);

                var homePage = (ScrollViewer)window.FindName("HomePage");
                Assert.AreEqual(ScrollBarVisibility.Auto, homePage.VerticalScrollBarVisibility,
                    "首页高度不足时必须滚动，不能通过缩放文字来塞进窗口。");
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
    public void CompactShellReflowsInsteadOfScalingText()
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
                Assert.AreEqual(190d, navShell.ColumnDefinitions[0].Width.Value, 0.1d,
                    "窄窗口应收紧导航栏宽度，而不是缩放文字。");

                var pageSentinel = (Border)window.FindName("PageFocusSentinel");
                Assert.AreEqual(new Thickness(16d), ((Grid)pageSentinel.Parent).Margin,
                    "窄窗口应减少内容边距。");

                var appearance = (FrameworkElement)window.FindName("AppearanceSettingsCard");
                var updater = (FrameworkElement)window.FindName("SoftwareUpdateCard");
                var leftColumn = (Grid)appearance.Parent;
                var rightColumn = (Grid)updater.Parent;
                Assert.AreSame(leftColumn.Parent, rightColumn.Parent);
                Assert.AreEqual(0, Grid.GetRow(leftColumn));
                Assert.AreEqual(0, Grid.GetRow(rightColumn));
                Assert.AreEqual(0, Grid.GetColumn(leftColumn));
                Assert.AreEqual(1, Grid.GetColumn(rightColumn),
                    "受支持的最窄宽度下设置页仍应保留双列结构；紧凑化应通过导航、边距和滚动完成。");

                window.Width = 1440d;
                window.Height = 900d;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();

                Assert.AreEqual(226d, navShell.ColumnDefinitions[0].Width.Value, 0.1d,
                    "宽窗口应恢复标准导航栏宽度。");
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "紧凑布局回归测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"紧凑布局回归验证失败：{failure}");
        }
    }

    [TestMethod]
    public void TextScalingAuditKeepsOnlyNonTextVisualScaling()
    {
        var responsiveSource = ReadRepositoryFile("MainWindow.ResponsiveLayout.cs");
        StringAssert.Contains(responsiveSource, "MainViewbox.Stretch = Stretch.None");
        Assert.IsFalse(responsiveSource.Contains("viewportWidth / ResponsiveWindowSizing.MainDesignWidth"),
            "主窗口不应再计算整棵视觉树的缩放比例。");

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
