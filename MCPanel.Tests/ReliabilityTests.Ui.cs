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
                                string.Empty,
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
                window.Dispatcher.UnhandledException += (_, args) =>
                {
                    dispatcherFailure ??= args.Exception;
                    args.Handled = true;
                };

                var model = (MainViewModel)window.DataContext;
                window.Show();
                window.UpdateLayout();

                // This is the first UI transition performed after a user selects
                // a local update ZIP. Keep the deferred StaticResource bindings
                // covered so a broken update progress panel cannot mask a valid
                // package as an update failure.
                model.IsUpdateBusy = true;
                model.UpdateProgress = 10;
                model.UpdateStatus = "正在校验本地更新包...";
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

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
