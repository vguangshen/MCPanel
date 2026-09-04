using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSvn;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void DownloadQueuePopupRightEdgeRemainsAlignedAfterViewboxScaling()
    {
        const double targetWidth = 1060d;
        const double popupWidth = 460d;
        const double scaleX = 0.8d;

        var left = MainWindow.CalculateDownloadQueuePopupLeft(
            targetWidth,
            popupWidth,
            scaleX);

        var popupRightOnScreen = left * scaleX + popupWidth;
        var targetRightOnScreen = targetWidth * scaleX;
        Assert.AreEqual(targetRightOnScreen, popupRightOnScreen, 0.001d);
    }

    [TestMethod]
    public void ThemeServiceUpdatesNestedPaletteResourcesWithoutShadowCopies()
    {
        var root = new ResourceDictionary();
        var theme = new ResourceDictionary
        {
            ["SurfaceBrush"] = new SolidColorBrush(Colors.White),
            ["PageBrush"] = new SolidColorBrush(Colors.White),
            ["TonalTextBrush"] = new SolidColorBrush(Colors.Blue),
            ["DialogTonalTextBrush"] = new SolidColorBrush(Colors.Blue)
        };
        root.MergedDictionaries.Add(theme);

        PanelThemeService.Apply(dark: true, root);

        Assert.AreEqual(Color.FromRgb(0x1B, 0x20, 0x27), ((SolidColorBrush)theme["SurfaceBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0x10, 0x14, 0x19), ((SolidColorBrush)theme["PageBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0xB8, 0xF2, 0xE6), ((SolidColorBrush)theme["TonalTextBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0xB8, 0xF2, 0xE6), ((SolidColorBrush)theme["DialogTonalTextBrush"]).Color);
        Assert.IsFalse(
            root.Keys.Cast<object>().Any(key => Equals(key, "TonalTextBrush")),
            "主题键已存在于合并字典时，不应在外层创建副本遮蔽它。");

        PanelThemeService.Apply(dark: false, root);

        Assert.AreEqual(Color.FromRgb(0xFF, 0xFF, 0xFF), ((SolidColorBrush)theme["SurfaceBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0xF5, 0xF7, 0xFB), ((SolidColorBrush)theme["PageBrush"]).Color);
        Assert.AreEqual(Color.FromRgb(0x17, 0x4E, 0xA6), ((SolidColorBrush)theme["TonalTextBrush"]).Color);
    }

    [TestMethod]
    public void DownloadQueuePopupTemplateRendersReadOnlyQueueRows()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            Popup? popup = null;
            Exception? dispatcherFailure = null;
            try
            {
                window = new MainWindow();
                window.Dispatcher.UnhandledException += (_, args) =>
                {
                    dispatcherFailure ??= args.Exception;
                    args.Handled = true;
                };
                var model = (MainViewModel)window.DataContext;
                var initialQueueCount = model.InstallationProgress.QueueItems.Count;
                for (var index = 1; index <= 4; index++)
                {
                    model.InstallationProgress.QueueItems.Add(
                        new ProductInstallQueueItemViewModel(
                            $"popup-render-test-{index}",
                            index,
                            new ProductInstallWorkerRequest(
                                $"UI{index:000}",
                                $"弹窗绑定测试软件 {index}",
                                "在线",
                                "/Assets/Logo/DS0102.png",
                                ProductSource.Local,
                                null,
                                "Tomcat7",
                                "MySQL5.6",
                                "Java",
                                null,
                                null,
                                false,
                                null,
                                null,
                                false),
                            ProductInstallQueueStatus.Completed,
                            100d,
                            "已完成"));
                }

                window.Show();
                window.UpdateLayout();
                ((FrameworkElement)window.FindName("HomePage")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("ProductsPage")).Visibility = Visibility.Visible;
                window.UpdateLayout();
                var button = (Button)window.FindName("DownloadQueueButton");
                popup = (Popup)window.FindName("DownloadQueuePopup");

                Assert.AreEqual(40d, button.ActualWidth, 0.1d, "下载队列入口的点击区域宽度必须固定为 40。");
                Assert.AreEqual(40d, button.ActualHeight, 0.1d, "下载队列入口的点击区域高度必须固定为 40。");
                Assert.AreEqual(button.ActualWidth, button.ActualHeight, 0.1d, "下载队列入口必须保持正方形比例。");
                button.ApplyTemplate();
                var downloadIcon = FindVisualChildren<TextBlock>(button)
                    .SingleOrDefault(candidate => candidate.Text == "\uE896");
                Assert.IsNotNull(downloadIcon, "下载队列入口必须渲染下载图标。");
                Assert.AreEqual(
                    "Segoe MDL2 Assets",
                    downloadIcon!.FontFamily.Source,
                    "下载队列入口图标必须使用 Segoe MDL2 Assets 字体，避免显示成空白方框。");
                var buttonRoot = button.Template.FindName("Root", button) as Border;
                Assert.IsNotNull(buttonRoot, "下载队列入口模板必须包含可验证的根边框。");
                Assert.AreEqual(20d, buttonRoot!.CornerRadius.TopLeft, 0.1d, "下载队列入口应保持圆形图标按钮外观。");
                Assert.AreEqual(20d, buttonRoot.CornerRadius.TopRight, 0.1d);
                Assert.AreEqual(20d, buttonRoot.CornerRadius.BottomRight, 0.1d);
                Assert.AreEqual(20d, buttonRoot.CornerRadius.BottomLeft, 0.1d);

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.IsTrue(popup.IsOpen, "下载队列按钮必须打开弹窗。");
                var host = (FrameworkElement)window.FindName("DownloadQueuePopupHost");
                host.Measure(new Size(460d, 560d));
                host.Arrange(new Rect(host.DesiredSize));
                host.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var itemsControl = (ItemsControl)window.FindName("DownloadQueueItemsControl");
                Assert.AreEqual(
                    initialQueueCount + 4,
                    itemsControl.Items.Count,
                    "队列任务必须逐行全部呈现。");
                for (var index = initialQueueCount; index < itemsControl.Items.Count; index++)
                {
                    var container = itemsControl.ItemContainerGenerator.ContainerFromIndex(index);
                    Assert.IsNotNull(container, $"第 {index + 1} 条队列任务没有生成可视行。");

                    var productIconFrame = FindVisualChildren<Border>(container!)
                        .SingleOrDefault(candidate => candidate.Width == 42d && candidate.Height == 42d);
                    Assert.IsNotNull(productIconFrame, $"第 {index + 1} 条队列任务必须显示产品图标容器。");
                    var productIcon = FindVisualChildren<Image>(productIconFrame!)
                        .SingleOrDefault();
                    Assert.IsNotNull(productIcon, $"第 {index + 1} 条队列任务必须渲染产品图标。");
                    Assert.AreEqual(42d, productIconFrame!.ActualWidth, 0.1d,
                        "队列产品图标必须保持固定的放大尺寸。");
                    var productDetails = FindVisualChildren<StackPanel>(container!)
                        .SingleOrDefault(candidate => candidate.Margin.Left == 10d && candidate.Margin.Right == 8d);
                    Assert.IsNotNull(productDetails, "产品名称与图标之间必须保留固定间距。");
                    var progressBar = FindVisualChildren<ProgressBar>(container!).Single();
                    Assert.AreEqual(62d, progressBar.Margin.Left, 0.1d,
                        "队列进度条必须与产品文字起始位置对齐。");

                    var visibleButtons = FindVisualChildren<Button>(container!)
                        .Where(candidate => candidate.Visibility == Visibility.Visible)
                        .ToArray();
                    Assert.AreEqual(1, visibleButtons.Length, $"第 {index + 1} 条队列任务的删除按钮数量不正确。");
                    var removeButton = visibleButtons[0];
                    Assert.AreEqual("\uE74D", removeButton.Content, "队列删除操作应使用图标而不是挤压文字按钮。");
                    Assert.AreEqual(32d, removeButton.ActualWidth, 0.1d, "队列操作按钮宽度必须固定为 32。");
                    Assert.AreEqual(32d, removeButton.ActualHeight, 0.1d, "队列操作按钮高度必须固定为 32。");
                    removeButton.ApplyTemplate();
                    var removeButtonRoot = removeButton.Template.FindName("Root", removeButton) as Border;
                    Assert.IsNotNull(removeButtonRoot, "队列操作按钮模板必须包含可验证的根边框。");
                    Assert.AreEqual(8d, removeButtonRoot!.CornerRadius.TopLeft, 0.1d, "队列操作按钮应使用圆角正方形。");
                    Assert.AreEqual(8d, removeButtonRoot.CornerRadius.TopRight, 0.1d);
                    Assert.AreEqual(8d, removeButtonRoot.CornerRadius.BottomRight, 0.1d);
                    Assert.AreEqual(8d, removeButtonRoot.CornerRadius.BottomLeft, 0.1d);
                }
                if (dispatcherFailure is not null)
                {
                    throw dispatcherFailure;
                }

                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.IsFalse(popup.IsOpen, "再次点击下载队列按钮必须关闭弹窗。");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (popup is not null)
                {
                    popup.IsOpen = false;
                }
                window?.Close();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "弹窗渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"下载队列弹窗无法渲染：{failure}");
        }
    }

    [TestMethod]
    public void DownloadQueuePopupDeleteActionReachesMainWindowQueueService()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            Popup? popup = null;
            try
            {
                window = new MainWindow();
                var model = (MainViewModel)window.DataContext;
                var item = new ProductInstallQueueItemViewModel(
                    "popup-delete-route-test",
                    100000,
                    new ProductInstallWorkerRequest(
                        "POPUP-DELETE",
                        "弹窗删除路由测试软件",
                        "在线",
                        string.Empty,
                        ProductSource.Online,
                        null,
                        null,
                        null,
                        null,
                        null,
                        null,
                        false,
                        null,
                        null,
                        false),
                    ProductInstallQueueStatus.Completed,
                    100d,
                    "已完成");
                model.InstallationProgress.QueueItems.Add(item);

                window.Show();
                window.UpdateLayout();
                ((FrameworkElement)window.FindName("HomePage")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("ProductsPage")).Visibility = Visibility.Visible;
                window.UpdateLayout();

                var button = (Button)window.FindName("DownloadQueueButton");
                popup = (Popup)window.FindName("DownloadQueuePopup");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var itemsControl = (ItemsControl)window.FindName("DownloadQueueItemsControl");
                itemsControl.UpdateLayout();
                var container = itemsControl.ItemContainerGenerator.ContainerFromItem(item);
                Assert.IsNotNull(container, "删除路由测试项必须生成弹窗行。");
                var removeButton = FindVisualChildren<Button>(container!)
                    .Single(candidate => candidate.Visibility == Visibility.Visible);
                Assert.AreEqual("删除此记录", AutomationProperties.GetName(removeButton));

                removeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.IsFalse(
                    model.InstallationProgress.QueueItems.Contains(item),
                    "弹窗内的删除按钮必须通过主窗体队列服务删除队列记录。");
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (popup is not null)
                {
                    popup.IsOpen = false;
                }

                window?.Close();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "弹窗删除路由测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"弹窗删除按钮未能到达队列服务：{failure}");
        }
    }

    [TestMethod]
    public void MainWindowCardTemplatesLoadFromSharedResourceDictionary()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();

                foreach (var resourceKey in new[]
                {
                    "ProductCardTemplate",
                    "CustomWebsiteCardTemplate",
                    "InstalledProductCardTemplate",
                    "ProductGridRowTemplate",
                    "ProductCategoryChipTemplate",
                    "DriveStatTemplate",
                    "ServiceStatusTemplate",
                    "InstallationQueueItemTemplate",
                    "InstallationRecentEventTemplate",
                    "EnvironmentCardTemplate",
                    "DatabaseToolCardTemplate",
                    "PathIconButton"
                })
                {
                    Assert.IsNotNull(window.FindResource(resourceKey), resourceKey);
                }

                var pathIconButton = new Button
                {
                    Style = (Style)window.FindResource("PathIconButton"),
                    Content = "\uE8B7"
                };
                pathIconButton.Measure(new Size(48, 48));
                pathIconButton.Arrange(new Rect(0, 0, pathIconButton.DesiredSize.Width, pathIconButton.DesiredSize.Height));
                pathIconButton.ApplyTemplate();
                var pathIconRoot = pathIconButton.Template.FindName("Root", pathIconButton) as Border;
                Assert.IsNotNull(pathIconRoot, "路径图标按钮模板必须包含可验证的根边框。");
                Assert.AreEqual(Colors.Transparent, ((SolidColorBrush)pathIconRoot!.Background).Color,
                    "已安装网站路径图标不应带额外底色。");
                Assert.AreEqual(24d, pathIconButton.ActualWidth, 0.1d);
                Assert.AreEqual(24d, pathIconButton.ActualHeight, 0.1d);

                window.Show();
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "主窗口卡片模板加载测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"主窗口卡片模板无法从共享资源字典加载：{failure}");
        }
    }

    [TestMethod]
    public void ProductCompletionIndicatorUsesProvidedGreenOutlineSvg()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow
                {
                    Width = 520,
                    Height = 260
                };

                var productCardTemplate = (DataTemplate)window.FindResource("ProductCardTemplate");
                var productCard = productCardTemplate.LoadContent() as FrameworkElement;
                Assert.IsNotNull(productCard, "产品卡片模板必须生成可渲染的根元素。");

                productCard!.DataContext = new ProductItem(
                    "UI-COMPLETE",
                    "已安装产品",
                    "测试",
                    string.Empty,
                    ProductSource.Online)
                {
                    IsInstalled = true
                };
                window.Content = productCard;
                window.Show();
                window.UpdateLayout();

                var indicator = FindVisualChildren<Viewbox>(productCard)
                    .Single(viewbox => viewbox.ToolTip as string == "已安装");
                Assert.AreEqual(40d, indicator.Width, 0.01d,
                    "已安装完成图标应保持 40 像素点击区域内的视觉尺寸。");
                Assert.AreEqual(40d, indicator.Height, 0.01d,
                    "已安装完成图标应保持正方形比例。");

                var paths = FindVisualChildren<System.Windows.Shapes.Path>(indicator).ToArray();
                Assert.AreEqual(2, paths.Length, "已安装完成图标必须由 SVG 的勾线和圆环两段路径组成。");
                var expectedGreen = Color.FromRgb(0x1A, 0xFA, 0x29);
                foreach (var path in paths)
                {
                    var fill = path.Fill as SolidColorBrush;
                    Assert.IsNotNull(fill, "SVG 路径必须使用填充颜色渲染。");
                    Assert.AreEqual(expectedGreen, fill!.Color, "完成图标必须使用指定的绿色。");
                    Assert.IsNull(path.Stroke, "SVG 图标不应再叠加旧式描边。");
                }

                var circlePath = paths.Single(path => path.Data.Bounds.Width > 900d);
                Assert.AreEqual(1024d, circlePath.Data.Bounds.Width, 0.5d,
                    "完成图标圆环必须使用指定 SVG 的 1024 视图范围。");
                Assert.AreEqual(1024d, circlePath.Data.Bounds.Height, 0.5d,
                    "完成图标圆环必须使用指定 SVG 的 1024 视图范围。");
                var checkPath = paths.Single(path => path.Data.Bounds.Width < 900d);
                Assert.IsTrue(checkPath.Data.Bounds.Width > 400d,
                    "完成图标勾线必须使用指定 SVG 的宽比例。");
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "已安装完成图标的 UI 渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"已安装完成图标未按指定 SVG 渲染：{failure}");
        }
    }

    [TestMethod]
    public void SettingsPagePlacesThemeAfterDatabaseToolsAndUsesCompactSwitch()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                var navItems = (StackPanel)window.FindName("NavItemsPanel");
                var settingsNav = navItems.Children
                    .OfType<RadioButton>()
                    .Single(button => string.Equals(button.Content as string, "面板设置", StringComparison.Ordinal));
                settingsNav.IsChecked = true;

                window.Show();
                window.UpdateLayout();

                var settingsPage = (ScrollViewer)window.FindName("SettingsPage");
                var databaseHeading = FindVisualChildren<TextBlock>(settingsPage)
                    .Single(textBlock => textBlock.Text == "数据库工具");
                var themeHeading = FindVisualChildren<TextBlock>(settingsPage)
                    .Single(textBlock => textBlock.Text == "外观主题");
                var databasePoint = databaseHeading.TransformToAncestor(settingsPage).Transform(new Point(0, 0));
                var themePoint = themeHeading.TransformToAncestor(settingsPage).Transform(new Point(0, 0));
                Assert.IsTrue(themePoint.Y > databasePoint.Y,
                    "外观主题应排列在数据库工具之后。");

                var appearanceCard = (Border)window.FindName("AppearanceSettingsCard");
                var softwareUpdateCard = (Border)window.FindName("SoftwareUpdateCard");
                var startupCard = (Border)window.FindName("StartupSettingsCard");
                Assert.IsTrue(FindVisualChildren<TextBlock>(settingsPage)
                    .Any(textBlock => textBlock.Text == "面板运行内存"),
                    "设置页应显示面板运行内存，而不是产品缓存清理入口。");
                var clearMemoryButton = FindVisualChildren<Button>(settingsPage)
                    .Single(button => string.Equals(button.Tag as string, "ClearPanelMemory", StringComparison.Ordinal));
                Assert.AreEqual("清理内存", clearMemoryButton.Content as string,
                    "设置页的清理按钮必须执行面板运行内存整理。");
                Assert.IsFalse(FindVisualChildren<Button>(settingsPage)
                    .Any(button => string.Equals(button.Tag as string, "ClearProductCache", StringComparison.Ordinal)),
                    "设置页不能继续暴露会删除产品缓存的按钮。");
                var appearancePoint = appearanceCard.TransformToAncestor(settingsPage).Transform(new Point(0, 0));
                var softwareUpdatePoint = softwareUpdateCard.TransformToAncestor(settingsPage).Transform(new Point(0, 0));
                var startupPoint = startupCard.TransformToAncestor(settingsPage).Transform(new Point(0, 0));
                Assert.IsTrue(startupPoint.Y > softwareUpdatePoint.Y,
                    "开机自启动卡片应排列在软件更新卡片下方。");
                Assert.IsTrue(startupCard.ActualHeight > 82d,
                    "开机自启动卡片应在保留内容空间的基础上适当拉长。");
                var cardBottomDelta = Math.Abs(
                    (appearancePoint.Y + appearanceCard.ActualHeight) -
                    (startupPoint.Y + startupCard.ActualHeight));
                Assert.IsTrue(cardBottomDelta <= 1.01d,
                    $"开机自启动卡片底边应与外观主题卡片底边对齐，允许设备像素舍入误差；实际差值：{cardBottomDelta:0.##}。");

                var startupToggle = FindVisualChildren<ToggleButton>(settingsPage)
                    .Single(toggle => AutomationProperties.GetName(toggle) == "开机自启动");
                Assert.IsFalse(FindVisualChildren<ToggleButton>(appearanceCard)
                    .Any(toggle => AutomationProperties.GetName(toggle) == "开机自启动"),
                    "开机自启动不能继续嵌套在外观主题卡片内。");
                Assert.IsTrue(FindVisualChildren<ToggleButton>(startupCard)
                    .Contains(startupToggle),
                    "开机自启动开关必须属于独立设置卡片。");
                startupToggle.ApplyTemplate();
                var switchRoot = startupToggle.Template.FindName("SwitchRoot", startupToggle) as Border;
                var switchTrack = startupToggle.Template.FindName("SwitchTrack", startupToggle) as Border;
                Assert.IsNotNull(switchRoot, "开机自启动必须使用紧凑滑动开关模板。");
                Assert.IsNotNull(switchTrack, "开机自启动开关必须包含独立滑轨。");
                Assert.AreEqual(52d, startupToggle.Width, 0.01d);
                Assert.AreEqual(32d, startupToggle.Height, 0.01d);
                Assert.AreEqual(42d, switchTrack!.Width, 0.01d);
                Assert.AreEqual(22d, switchTrack.Height, 0.01d);
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "设置页布局与开关渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"设置页布局或开机自启动开关不符合设计：{failure}");
        }
    }

    [TestMethod]
    public void HomeSystemSummaryKeepsHardwareLinesInsideCard()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = new MainWindow();
                window.Show();
                window.UpdateLayout();

                var card = (Border)window.FindName("HomeSystemSummaryCard");
                var hardwareLines = new[]
                {
                    (TextBlock)window.FindName("HomeHardwareCpuText"),
                    (TextBlock)window.FindName("HomeHardwareMemoryText"),
                    (TextBlock)window.FindName("HomeHardwareOsText")
                };
                var contentBottom = card.ActualHeight - card.BorderThickness.Bottom - card.Padding.Bottom;
                var previousBottom = double.MinValue;

                Assert.AreEqual(92d, card.Height, 0.01d);
                foreach (var line in hardwareLines)
                {
                    Assert.IsTrue(line.ActualWidth > 0 && line.ActualHeight > 0,
                        "首页硬件信息行必须实际参与布局。");
                    var top = line.TransformToAncestor(card).Transform(new Point(0, 0)).Y;
                    var bottom = top + line.ActualHeight;
                    Assert.IsTrue(top >= previousBottom - 0.01d,
                        "首页硬件信息行不能互相覆盖。");
                    Assert.IsTrue(bottom <= contentBottom + 1d,
                        "首页系统版本行必须完整位于卡片内容区域内。");
                    previousBottom = bottom;
                }

                Assert.AreEqual(TextWrapping.NoWrap, hardwareLines[2].TextWrapping);
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "首页系统概览渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"首页系统版本文字布局异常：{failure}");
        }
    }

    [TestMethod]
    public void UpdateBusyPanelRendersWhenLocalUpdatePreparationStarts()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            Exception? dispatcherFailure = null;
            try
            {
                window = new MainWindow();
                var navItems = (StackPanel)window.FindName("NavItemsPanel");
                var settingsNav = navItems.Children
                    .OfType<RadioButton>()
                    .Single(button => string.Equals(button.Content as string, "面板设置", StringComparison.Ordinal));
                settingsNav.IsChecked = true;
                window.Dispatcher.UnhandledException += (_, args) =>
                {
                    dispatcherFailure ??= args.Exception;
                    args.Handled = true;
                };

                var model = (MainViewModel)window.DataContext;
                window.Show();
                window.UpdateLayout();

                var updateCard = (Border)window.FindName("SoftwareUpdateCard");
                var updateProgress = (ProgressBar)window.FindName("ApplicationUpdateProgress");
                var updateProgressText = (TextBlock)window.FindName("ApplicationUpdateProgressText");
                var idleCardHeight = updateCard.ActualHeight;
                Assert.AreEqual(Visibility.Visible, updateProgress.Visibility,
                    "软件更新进度条必须在非更新状态下保留固定位置。");
                Assert.AreEqual("0%", updateProgressText.Text,
                    "软件更新进度条在空闲状态应显示 0%。");
                Assert.IsTrue(updateProgress.ActualWidth > 100d,
                    "软件更新进度条应横向填充更新卡片的可用空间。");
                Assert.AreEqual(10d, updateProgress.ActualHeight, 0.01d,
                    "软件更新进度条的轨道高度必须保持固定。");

                // This is the first UI transition performed after a user selects
                // a local update ZIP. Keep the deferred StaticResource bindings
                // covered so a broken update progress panel cannot mask a valid
                // package as an update failure.
                model.IsUpdateBusy = true;
                model.UpdateProgress = 10;
                model.UpdateStatus = "正在校验本地更新包...";
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.AreEqual(idleCardHeight, updateCard.ActualHeight, 0.01d,
                    "更新开始后软件更新卡片高度不能因进度区域显示而变化。");
                Assert.AreEqual("10%", updateProgressText.Text,
                    "软件更新进度条中心必须显示当前百分比。");
                Assert.AreEqual(Visibility.Visible, updateProgress.Visibility,
                    "更新进行中软件更新进度条必须保持可见。");

                if (dispatcherFailure is not null)
                {
                    throw dispatcherFailure;
                }
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

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "更新准备状态的 UI 渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"本地更新准备状态无法渲染：{failure}");
        }
    }

    [TestMethod]
    public void UpdateConfirmationDialogLoadsItsOwnScrollBarResources()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            PanelMessageDialog? dialog = null;
            Exception? dispatcherFailure = null;
            try
            {
                dialog = new PanelMessageDialog(
                    new string('更', 600),
                    "本地更新",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                dialog.Dispatcher.UnhandledException += (_, args) =>
                {
                    dispatcherFailure ??= args.Exception;
                    args.Handled = true;
                };
                dialog.ApplyTheme(dark: true);
                dialog.Show();
                dialog.UpdateLayout();

                var scrollViewer = (ScrollViewer)dialog.FindName("MessageScrollViewer");
                scrollViewer.ApplyTemplate();
                scrollViewer.UpdateLayout();
                dialog.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                if (dispatcherFailure is not null)
                {
                    throw dispatcherFailure;
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                dialog?.Close();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "本地更新确认窗口的渲染测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"本地更新确认窗口无法渲染：{failure}");
        }
    }

    [TestMethod]
    public void SharedDialogResourceDictionaryLoadsConfigurationDialogs()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            Window[]? dialogs = null;
            try
            {
                var custom = new CustomWebsiteDialog();
                var product = new ProductWebsiteDialog(ProductWebsiteSettings.Default);
                var nginx = new NginxProxyDialog(new EnvironmentRuntimeService());
                custom.ApplyTheme(dark: false);
                product.ApplyTheme(dark: false);
                nginx.ApplyTheme(dark: false);
                dialogs = [custom, product, nginx];

                foreach (var dialog in dialogs)
                {
                    Assert.IsNotNull(dialog.FindResource("DialogButton"));
                    Assert.IsNotNull(dialog.FindResource(typeof(TextBox)));
                    dialog.ApplyTemplate();
                    dialog.UpdateLayout();
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                if (dialogs is not null)
                {
                    foreach (var dialog in dialogs)
                    {
                        dialog.Close();
                    }
                }

                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "共享弹窗资源加载测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"配置类弹窗无法加载共享资源：{failure}");
        }
    }

    [TestMethod]
    public void SharedScrollBarResourcesLoadStandalonePages()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            AiAnalysisPage? aiPage = null;
            AccountApiPage? accountPage = null;
            try
            {
                aiPage = new AiAnalysisPage();
                accountPage = new AccountApiPage();
                Assert.IsNotNull(aiPage.FindResource("CompactScrollBar"));
                Assert.IsNotNull(accountPage.FindResource("ModernVerticalScrollBar"));
                aiPage.ApplyTemplate();
                accountPage.ApplyTemplate();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                aiPage?.Dispose();
                accountPage?.Dispose();
                completed.Set();
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "独立页面滚动条资源测试超时。");
        if (failure is not null)
        {
            Assert.Fail($"独立页面无法加载共享滚动条资源：{failure}");
        }
    }
}
