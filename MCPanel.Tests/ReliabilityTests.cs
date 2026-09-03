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
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSvn;

namespace MCPanel.Tests;

[TestClass]
public sealed class ReliabilityTests
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

    [TestMethod]
    public void ProductDownloadPauseBlocksUntilExplicitlyResumed()
    {
        using var pauseController = new DownloadPauseController();

        Assert.IsTrue(pauseController.Pause());
        Assert.IsTrue(pauseController.IsPaused);
        var waitTask = pauseController.WaitIfPausedAsync(CancellationToken.None);
        Assert.IsFalse(waitTask.IsCompleted, "暂停后下载门必须保持关闭。");

        Assert.IsTrue(pauseController.Resume());
        Assert.IsTrue(waitTask.Wait(TimeSpan.FromSeconds(2)), "继续下载后等待任务应立即释放。");
        Assert.IsFalse(pauseController.IsPaused);
    }

    [TestMethod]
    public void InstallationPanelShowsPauseOnlyDuringDownload()
    {
        var product = new ProductItem("TEST1", "测试产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.Begin(product, isUpdate: false);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, progress.PauseButtonVisibility);

        progress.SetDownloadStage();
        Assert.IsTrue(progress.CanPause);
        Assert.IsTrue(progress.IsDownloading);
        Assert.AreEqual(0, progress.DownloadProgress, 0.001);
        Assert.AreEqual(System.Windows.Visibility.Visible, progress.PauseButtonVisibility);
        Assert.AreEqual("暂停下载", progress.PauseActionText);

        progress.ApplyWorkerProgress(new ProductInstallWorkerProgress(
            "running",
            45,
            "正在下载 product.zip",
            InstallProgressStage.Downloading,
            50));
        Assert.AreEqual(50, progress.DownloadProgress, 0.001, "顶部环形进度应使用下载阶段进度，而不是安装总进度。");

        progress.MarkDownloadPaused();
        Assert.IsTrue(progress.IsPaused);
        Assert.AreEqual("继续下载", progress.PauseActionText);
        progress.ReportStatus("正在下载：payload.zip");
        StringAssert.Contains(progress.DetailText, "已暂停");

        progress.MarkDownloadResumed();
        Assert.IsFalse(progress.IsPaused);
        Assert.AreEqual("正在下载：payload.zip", progress.DetailText);

        progress.SetDeploymentStage(isUpdate: false);
        Assert.IsFalse(progress.CanPause);
        Assert.IsFalse(progress.IsDownloading);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, progress.PauseButtonVisibility);
    }

    [TestMethod]
    public void InstalledProductUsesCompletionIndicatorInsteadOfZeroProgress()
    {
        var product = new ProductItem("TEST-COMPLETE", "已安装产品", "测试", string.Empty, ProductSource.Online)
        {
            DownloadProgress = 0
        };

        Assert.IsFalse(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.CompletedIndicatorVisibility);

        product.IsInstalled = true;

        Assert.IsTrue(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.CompletedIndicatorVisibility);

        product.IsBusy = true;

        Assert.IsFalse(product.IsInstallationComplete, "更新或卸载期间不能显示安装完成勾号。");
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.CompletedIndicatorVisibility);

        product.IsBusy = false;
        product.SetQueueState(ProductInstallQueueStatus.Pending, 1);

        Assert.IsFalse(product.IsInstallationComplete, "排队期间不能显示安装完成勾号。");
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);

        product.SetQueueState(null, 0);

        Assert.IsTrue(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.CompletedIndicatorVisibility);
    }

    [TestMethod]
    public void HidingInstallationPanelKeepsDownloadControlsAndStateAlive()
    {
        var product = new ProductItem("TEST-HIDE", "后台下载产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.Begin(product, isUpdate: false);
        progress.SetDownloadStage();
        progress.Hide();

        Assert.IsFalse(progress.IsVisible);
        Assert.IsTrue(progress.CanPause, "隐藏进度层不能关闭后台下载的暂停控制。");
        Assert.IsTrue(progress.CanCancel, "隐藏进度层不能关闭后台安装的取消控制。");

        progress.Show();
        Assert.IsTrue(progress.IsVisible);
        Assert.AreEqual("下载产品文件", progress.StageText);
    }

    [TestMethod]
    public void ProductInstallQueuePreservesClickOrderAcrossRecovery()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstProduct = new ProductItem("QUEUE-1", "第一个产品", "测试", string.Empty, ProductSource.Online);
            var secondProduct = new ProductItem("QUEUE-2", "第二个产品", "测试", string.Empty, ProductSource.Online);
            var thirdProduct = new ProductItem("QUEUE-3", "第三个产品", "测试", string.Empty, ProductSource.Online);
            ProductInstallQueueItemViewModel first;
            ProductInstallQueueItemViewModel second;
            ProductInstallQueueItemViewModel third;

            using (var queue = new ProductInstallQueueService(
                       new InstallationProgressViewModel(),
                       root,
                       autoStartWorker: false))
            {
                first = queue.Enqueue(firstProduct, isUpdate: false);
                second = queue.Enqueue(secondProduct, isUpdate: false);
                third = queue.Enqueue(thirdProduct, isUpdate: false);

                Assert.AreEqual(1, first.Sequence);
                Assert.AreEqual(2, second.Sequence);
                Assert.AreEqual(3, third.Sequence);
                Assert.AreEqual(first.QueueId, queue.CurrentItem!.QueueId);
                Assert.AreEqual(1, first.QueuePosition);
                Assert.AreEqual(2, second.QueuePosition);
                Assert.AreEqual(3, third.QueuePosition);
                Assert.IsTrue(queue.Cancel(second.QueueId));
                Assert.AreEqual(ProductInstallQueueStatus.Cancelled, second.State);
                Assert.AreEqual(2, third.QueuePosition);
            }

            using var restored = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            Assert.AreEqual(2, restored.Items.Count, "已完成、失败或取消的旧记录不应在重启后重新出现。");
            Assert.AreEqual(first.QueueId, restored.CurrentItem!.QueueId);
            Assert.AreEqual(ProductInstallQueueStatus.Pending, restored.Items[0].State);
            Assert.AreEqual(ProductInstallQueueStatus.Pending, restored.Items[1].State);
            Assert.AreEqual(1, restored.Items[0].QueuePosition);
            Assert.AreEqual(third.QueueId, restored.Items[1].QueueId);
            Assert.AreEqual(2, restored.Items[1].QueuePosition);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancellingPendingQueueItemPublishesCancelMarkerBeforeClaim()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = new ProductItem("QUEUE-CANCEL", "取消排队产品", "测试", string.Empty, ProductSource.Online);
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(product, isUpdate: false);

            Assert.IsTrue(queue.Cancel(item.QueueId));
            Assert.AreEqual(ProductInstallQueueStatus.Cancelled, item.State);
            Assert.IsFalse(File.Exists(item.RequestPath));
            Assert.IsTrue(
                File.Exists(item.CancelPath),
                "取消排队项时必须先留下取消标记，避免后台进程恰好接管请求后继续执行。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void DownloadQueueRowPausesAndResumesOnlyTheActiveDownload()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(
                progress,
                root,
                autoStartWorker: false);
            var active = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-1", "正在下载的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var waiting = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-2", "排队中的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);

            active.SetState(ProductInstallQueueStatus.Running);
            active.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                20,
                "正在下载软件包...",
                InstallProgressStage.Downloading,
                25));

            Assert.IsTrue(active.CanTogglePause);
            Assert.IsFalse(waiting.CanTogglePause);
            Assert.AreEqual("\uE769", active.PauseActionGlyph);
            Assert.AreEqual("\uE74D", active.RemoveActionGlyph);
            Assert.AreEqual(true, queue.TogglePause(active.QueueId));
            Assert.IsTrue(active.IsPaused);
            Assert.IsTrue(active.CanTogglePause, "暂停后必须保留同一按钮，供用户继续下载。");
            Assert.AreEqual("继续", active.PauseActionText);
            Assert.AreEqual("\uE768", active.PauseActionGlyph);
            Assert.IsTrue(File.Exists(active.PausePath));
            Assert.IsNull(queue.TogglePause(waiting.QueueId), "等待项不能错误地暂停当前下载。");

            Assert.AreEqual(false, queue.TogglePause(active.QueueId));
            Assert.IsFalse(active.IsPaused);
            Assert.IsTrue(active.CanTogglePause);
            Assert.AreEqual("暂停", active.PauseActionText);
            Assert.IsFalse(File.Exists(active.PausePath));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void RemovingPendingQueueRowDeletesItsSessionFilesAndLegacyOverlayStaysClosed()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(
                progress,
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-REMOVE", "待删除的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var requestPrefix = item.RequestPath.Substring(
                0,
                item.RequestPath.Length - ".request.json".Length);
            var activeEnvelope = requestPrefix + ".active.json";
            var finishedEnvelope = requestPrefix + ".finished.json";
            File.WriteAllText(activeEnvelope, "claimed");
            File.WriteAllText(finishedEnvelope, "finished");
            var paths = new[]
            {
                item.RequestPath,
                activeEnvelope,
                finishedEnvelope,
                item.ProgressPath,
                item.CancelPath,
                item.PausePath
            };

            Assert.IsFalse(progress.IsVisible, "加入下载队列不应再弹出旧的全屏安装队列。");
            Assert.IsTrue(queue.Remove(item.QueueId));
            Assert.AreEqual(0, queue.Items.Count);
            Assert.IsTrue(paths.All(path => !File.Exists(path)), "删除排队项后应清理该项的会话文件。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void RemovingRequestAlreadyClaimedByWorkerKeepsCancellationUntilTerminalProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-CLAIM-RACE", "接管竞态产品", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var activeEnvelope = item.RequestPath.Substring(
                0,
                item.RequestPath.Length - ".request.json".Length) + ".active.json";
            File.Move(item.RequestPath, activeEnvelope);

            Assert.IsTrue(queue.Remove(item.QueueId));
            Assert.AreEqual(1, queue.Items.Count, "后台已经接管请求时，行必须保留到后台确认终态。");
            Assert.IsTrue(item.IsRemovalRequested);
            Assert.IsTrue(File.Exists(item.CancelPath), "后台确认取消前不能提前删除取消标记。");
            Assert.IsTrue(File.Exists(activeEnvelope));

            File.WriteAllText(
                item.ProgressPath,
                JsonSerializer.Serialize(new ProductInstallWorkerProgress(
                    "cancelled",
                    0,
                    "已取消。",
                    InstallProgressStage.Preparing,
                    0)));
            queue.Tick();

            Assert.AreEqual(0, queue.Items.Count);
            Assert.IsFalse(File.Exists(item.CancelPath));
            Assert.IsFalse(File.Exists(activeEnvelope));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void PauseMarkerIsClearedWhenDownloadMovesToDeployment()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-STAGE", "暂停阶段产品", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            item.SetState(ProductInstallQueueStatus.Running);
            item.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                40,
                "正在下载...",
                InstallProgressStage.Downloading,
                50));
            Assert.AreEqual(true, queue.TogglePause(item.QueueId));
            Assert.IsTrue(File.Exists(item.PausePath));

            File.WriteAllText(
                item.ProgressPath,
                JsonSerializer.Serialize(new ProductInstallWorkerProgress(
                    "running",
                    70,
                    "正在部署...",
                    InstallProgressStage.Installing,
                    20)));
            queue.Tick();

            Assert.IsFalse(item.IsPaused);
            Assert.IsFalse(File.Exists(item.PausePath));
            Assert.AreEqual("正在安装", item.StateText);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancelledFutureQueueRowDoesNotInflateOverallDownloadProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(progress, root, autoStartWorker: false);
            var active = queue.Enqueue(
                new ProductItem("QUEUE-RING-1", "当前下载", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var cancelled = queue.Enqueue(
                new ProductItem("QUEUE-RING-2", "取消排队", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            active.SetState(ProductInstallQueueStatus.Running);
            active.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                30,
                "正在下载...",
                InstallProgressStage.Downloading,
                40));
            Assert.IsTrue(queue.Cancel(cancelled.QueueId));

            Assert.AreEqual(40, progress.OverallDownloadProgress, 0.001);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ClaimedCancelledWorkerDoesNotStartProductDownload()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = new ProductItem("WORKER-CANCEL", "已取消产品", "测试", string.Empty, ProductSource.Online);
            var request = ProductInstallWorker.CreateRequest(product, isUpdate: false);
            var progressPath = ProductInstallWorker.CreateProgressFile(root, request.ProductId);
            var cancelPath = ProductInstallWorker.CreateControlFile(root, request.ProductId, "cancel");
            var pausePath = ProductInstallWorker.CreateControlFile(root, request.ProductId, "pause");
            File.WriteAllText(cancelPath, "1");

            var exitCode = ProductInstallWorker.RunOneAsync(
                    request,
                    progressPath,
                    cancelPath,
                    pausePath)
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual(2, exitCode);
            Assert.IsTrue(ProductInstallWorker.TryReadProgress(progressPath, out var progress));
            Assert.AreEqual("cancelled", progress.State);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UninstallationUsesTheSameProgressPanelWithMonotonicProgress()
    {
        var product = new ProductItem("TEST-UNINSTALL", "测试卸载产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.BeginUninstall(product);
        Assert.IsTrue(progress.IsVisible);
        Assert.AreEqual("正在卸载产品", progress.OperationTitle);
        Assert.AreEqual("准备卸载", progress.StageText);
        Assert.IsFalse(progress.CanPause);
        Assert.IsFalse(progress.CanCancel);

        progress.ReportUninstallProgress(new ProductUninstallProgress(42, "正在移除 IIS 应用、应用池和共享站点端口..."));
        Assert.AreEqual(42, progress.Progress, 0.001);
        Assert.AreEqual("清理 IIS 绑定", progress.StageText);
        progress.ReportUninstallProgress(new ProductUninstallProgress(20, "旧阶段消息不应倒退进度"));
        Assert.AreEqual(42, progress.Progress, 0.001);
    }

    [TestMethod]
    public void LegacyProductInstallRootAndIisMetadataArePreserved()
    {
        var product = new ProductItem("DS0102", "评价系统", "初级", string.Empty, ProductSource.Online)
        {
            InstallRoot = Path.Combine(Path.GetTempPath(), "MCPanel-Products"),
            SysType = "64",
            RunEnvironment = "Framework4.5"
        };

        var resolved = ProductInstallPathResolver.ResolveProductDirectory(product);
        StringAssert.EndsWith(resolved, Path.Combine("MCPanel-Products", "DS0102"));
        Assert.IsTrue(ProductInstallPathResolver.IsInsideProductRoot(resolved, product));

        var script = ProductDeploymentService.BuildIisBindScript(
            "C:\\Windows\\System32\\inetsrv\\appcmd.exe",
            "MCPanel",
            "C:\\StoreData\\Runtime\\IISRoot",
            "/DS0102",
            resolved,
            "DS0102",
            "v4.0",
            ProductDeploymentService.ResolveIisPipelineMode(product.RunEnvironment, "v4.0"),
            enable32Bit: false,
            port: 8088);

        StringAssert.Contains(script, "/managedPipelineMode:$managedPipelineMode");
        StringAssert.Contains(script, "/enable32BitAppOnWin64:$enable32Bit");
        StringAssert.Contains(script, "$managedPipelineMode='Classic'");
        StringAssert.Contains(script, "$enable32Bit=$false");
    }

    [TestMethod]
    public void ProductCleanupDeletesReadOnlySvnWorkingCopy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var svn = Path.Combine(root, ".svn", "pristine");
            Directory.CreateDirectory(svn);
            var file = Path.Combine(svn, "entry");
            File.WriteAllText(file, "metadata");
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);

            ProductDeploymentService.DeleteDirectoryForTest(root);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void EnvironmentInstallElevationPolicyPromptsOnlyForPrivilegedComponents()
    {
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Iis));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Nginx));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.MySql));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.SqlServer));
        Assert.IsFalse(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Tomcat));
        Assert.IsFalse(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.FrpTunnel));
    }

    [TestMethod]
    public void EnvironmentInstallProgressShowsDownloadSpeedThenResetsForInstall()
    {
        var item = new EnvironmentItem(EnvironmentKind.SqlServer, "SQL Server", "版本", "测试环境组件");
        item.SetBusyState("安装中", "正在准备安装...", 0);

        item.ApplyInstallProgress(new InstallProgress(
            35,
            "正在下载 SQLServer-SSEI-EntDev.exe：350 MB / 1 GB",
            InstallProgressStage.Downloading,
            35,
            "12.5 MB/s"));

        Assert.AreEqual("状态", item.ProgressStageText);
        Assert.AreEqual(35, item.Progress, 0.001);
        Assert.AreEqual("速度 12.5 MB/s", item.DownloadSpeedText);
        Assert.AreEqual(Visibility.Visible, item.DownloadSpeedVisibility);
        Assert.IsFalse(item.IsProgressIndeterminate);

        item.ApplyInstallProgress(new InstallProgress(
            84,
            "正在通过官方安装包安装 SQL Server",
            InstallProgressStage.Installing,
            18));

        Assert.AreEqual("状态", item.ProgressStageText);
        Assert.AreEqual(18, item.Progress, 0.001);
        Assert.AreEqual(string.Empty, item.DownloadSpeedText);
        Assert.AreEqual(Visibility.Collapsed, item.DownloadSpeedVisibility);
        Assert.IsFalse(item.IsProgressIndeterminate);
    }

    [TestMethod]
    public void ProductDownloadProgressCarriesTransferSizeAndSpeed()
    {
        var progress = new ProductDownloadProgress(
            42,
            "正在下载 product.zip",
            1572864,
            10485760,
            "8.5 MB/s");

        Assert.AreEqual(42, progress.Percent, 0.001);
        Assert.AreEqual("已下载 1.5 MB / 10 MB", progress.TransferText);
        Assert.AreEqual("8.5 MB/s", progress.SpeedText);
    }

    [TestMethod]
    public void ProductDownloadProgressCarriesScannedAndDownloadedSize()
    {
        var progress = new ProductDownloadProgress(
            42,
            "正在扫描并下载 product.jar",
            524288,
            10485760,
            "3.2 MB/s",
            7,
            1572864);

        Assert.AreEqual("已扫描 7 项 · 1.5 MB · 已下载 512 KB / 10 MB", progress.TransferText);
    }

    [TestMethod]
    public void AtomicWriteReplacesCompleteFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            AtomicFile.WriteAllText(path, "{\"value\":1}");
            AtomicFile.WriteAllText(path, "{\"value\":2}");
            Assert.AreEqual("{\"value\":2}", File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void LocalSecretRoundTripsForCurrentUser()
    {
        const string secret = "test-secret-123";
        var protectedValue = LocalSecretProtector.Protect(secret);
        Assert.IsTrue(LocalSecretProtector.IsProtected(protectedValue));
        Assert.AreEqual(secret, LocalSecretProtector.Unprotect(protectedValue));
    }

    [TestMethod]
    public void GitHubUpdateTokenIsDpapiProtectedAndCanBeCleared()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var credentialFile = Path.Combine(root, "github-update-credential.json");
            var store = new GitHubUpdateCredentialStore(credentialFile);
            const string token = "mcpanel_test_token_0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

            store.SaveAccessToken(token);

            Assert.IsTrue(store.HasStoredCredential());
            Assert.AreEqual(-1, File.ReadAllText(credentialFile).IndexOf(token, StringComparison.Ordinal));
            Assert.AreEqual(token, store.LoadAccessToken());

            store.ClearAccessToken();
            Assert.IsFalse(store.HasStoredCredential());
            Assert.AreEqual(string.Empty, store.LoadAccessToken());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void GitHubReleaseManifestRequiresBoundVersionAndSha256()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var manifest = ApplicationUpdateService.ValidateGitHubReleaseManifest(new GitHubReleaseManifest
        {
            Format = ApplicationUpdateService.GitHubReleaseManifestFormat,
            Version = "v1.1.20",
            PackageName = "MCPanel-1.1.20.zip",
            Sha256 = "sha256:" + hash,
            ReleaseNotes = "测试更新"
        });

        Assert.AreEqual("1.1.20", manifest.Version);
        Assert.AreEqual(hash, manifest.Sha256);
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.ValidateGitHubReleaseManifest(new GitHubReleaseManifest
            {
                Format = ApplicationUpdateService.GitHubReleaseManifestFormat,
                Version = "1.1.20",
                PackageName = "other.zip",
                Sha256 = hash
            }));
    }

    [TestMethod]
    public void GitHubRepositoryValidationRejectsTraversal()
    {
        Assert.AreEqual("vguangshen/MCPanel", ApplicationUpdateService.NormalizeGitHubRepository(" vguangshen/MCPanel "));
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.NormalizeGitHubRepository("vguangshen/../../other"));
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.NormalizeGitHubRepository("https://github.com/vguangshen/MCPanel"));
    }

    [TestMethod]
    public void StorageSizeUsesReadableUnits()
    {
        Assert.AreEqual("0 B", PanelSettingsService.FormatStorageSize(0));
        Assert.AreEqual("1.5 MB", PanelSettingsService.FormatStorageSize(1572864));
        Assert.AreEqual("2 GB", PanelSettingsService.FormatStorageSize(2147483648));
    }

    [TestMethod]
    public void AccountApiStateLivesBesideStoreData()
    {
        var accountApiRoot = Path.GetFullPath(AccountApiStorage.RootPath).TrimEnd(Path.DirectorySeparatorChar);
        var storeDataAccountApi = Path.Combine(
            Path.GetDirectoryName(accountApiRoot) ?? string.Empty,
            "StoreData",
            "AccountApi");

        StringAssert.EndsWith(accountApiRoot, Path.Combine("AccountApi"));
        Assert.AreNotEqual(
            Path.GetFullPath(storeDataAccountApi).TrimEnd(Path.DirectorySeparatorChar),
            accountApiRoot,
            true,
            "Account API 不应再以内嵌目录形式写入 StoreData。");
    }

    [TestMethod]
    public void SplitComponentRootsStayOutsideStoreData()
    {
        var storeDataRoot = Path.GetFullPath(ComponentPaths.StoreDataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var componentRoot in new[]
        {
            ComponentPaths.RuntimeRoot,
            ComponentPaths.DownloadsRoot,
            ComponentPaths.ToolsRoot,
            ComponentPaths.ProductIconsRoot
        })
        {
            var normalizedRoot = Path.GetFullPath(componentRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            Assert.IsFalse(
                normalizedRoot.StartsWith(storeDataRoot, StringComparison.OrdinalIgnoreCase),
                $"拆分后的组件目录不应位于 StoreData 下: {componentRoot}");
        }
    }

    [TestMethod]
    public void ComponentStorageMigrationMovesLegacyDirectoriesWithoutOverwritingCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var storeData = Path.Combine(root, "StoreData");
            var legacyRuntimeFile = Path.Combine(storeData, "Runtime", "TomcatProductRuns", "demo", "conf", "server.xml");
            var legacyDownloadFile = Path.Combine(storeData, "Downloads", "Environment", "tomcat.zip");
            var legacyToolFile = Path.Combine(storeData, "Tools", "Installers", "SSMS-Setup.exe");
            var legacyIconFile = Path.Combine(storeData, "ProductIcons", "demo.png");
            foreach (var file in new[] { legacyRuntimeFile, legacyDownloadFile, legacyToolFile, legacyIconFile })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, file);
            }

            var existingCacheFile = Path.Combine(root, "Cache", "products-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(existingCacheFile)!);
            File.WriteAllText(existingCacheFile, "catalog");

            ComponentStorageMigration.MigrateForTest(root);
            ComponentStorageMigration.MigrateForTest(root);

            Assert.IsTrue(File.Exists(Path.Combine(root, "Runtime", "TomcatProductRuns", "demo", "conf", "server.xml")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Downloads", "Environment", "tomcat.zip")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Tools", "Installers", "SSMS-Setup.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Cache", "ProductIcons", "demo.png")));
            Assert.AreEqual("catalog", File.ReadAllText(existingCacheFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Runtime")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Downloads")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Tools")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "ProductIcons")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void NavicatOfficialDownloadMetadataIsStrictlyParsed()
    {
        var html = "<script>var product = \"navicat17_premium_lite_cs_x64.exe\";</script>";
        var product = NavicatPremiumLiteInstaller.ExtractProductFileName(html);

        Assert.AreEqual("navicat17_premium_lite_cs_x64.exe", product);
        Assert.AreEqual(
            NavicatPremiumLiteInstaller.DefaultProductFileName,
            NavicatPremiumLiteInstaller.ExtractProductFileName("<html>missing</html>"));

        var uri = NavicatPremiumLiteInstaller.ParseDownloadUri(
            "{\"download_link\":\"dn.navicat.com.cn/download/navicat17_premium_lite_cs_x64.exe\"}",
            product);
        Assert.AreEqual(Uri.UriSchemeHttps, uri.Scheme);
        Assert.AreEqual("dn.navicat.com.cn", uri.Host);
    }

    [TestMethod]
    public void NavicatDownloadRejectsUntrustedHostsAndProductNames()
    {
        const string product = "navicat17_premium_lite_cs_x64.exe";
        Assert.IsTrue(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("https://navicat-installers.oss-cn-shanghai.aliyuncs.com/download/navicat17_premium_lite_cs_x64.exe?signature=test"),
            product));
        Assert.IsFalse(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("https://example.com/download/navicat17_premium_lite_cs_x64.exe"),
            product));
        Assert.IsFalse(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("http://dn.navicat.com.cn/download/navicat17_premium_lite_cs_x64.exe"),
            product));
        Assert.ThrowsException<InvalidDataException>(() =>
            NavicatPremiumLiteInstaller.ParseDownloadUri(
                "{\"download_link\":\"evil.example/navicat17_premium_lite_cs_x64.exe\"}",
                product));
    }

    [TestMethod]
    public void NavicatInstallerTargetsDedicatedFolderBesideApplication()
    {
        var arguments = NavicatPremiumLiteInstaller.BuildInstallerArguments(
            @"D:\MCPanel\Navicat Premium Lite",
            @"D:\MCPanel\StoreData\Work\navicat-install.log");

        StringAssert.Contains(arguments, "/VERYSILENT");
        StringAssert.Contains(arguments, "/SUPPRESSMSGBOXES");
        StringAssert.Contains(arguments, "/NORESTART");
        StringAssert.Contains(arguments, "/DIR=\"D:\\MCPanel\\Navicat Premium Lite\"");
        StringAssert.Contains(arguments, "/LOG=");
        StringAssert.Contains(arguments, @"D:\MCPanel\StoreData\Work\navicat-install.log");
    }

    [TestMethod]
    public void DatabaseToolDefaultDetectionUsesOnlyKnownBoundedLayouts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var programFiles = Path.Combine(root, "Program Files");
            var programFilesX86 = Path.Combine(root, "Program Files (x86)");
            var navicat = Path.Combine(programFiles, "PremiumSoft", "Navicat Premium 12", "navicat.exe");
            var modernSsms = Path.Combine(
                programFiles,
                "Microsoft SQL Server Management Studio 22",
                "Release",
                "Common7",
                "IDE",
                "Ssms.exe");
            var legacySsms = Path.Combine(
                programFilesX86,
                "Microsoft SQL Server",
                "100",
                "Tools",
                "Binn",
                "VSShell",
                "Common7",
                "IDE",
                "Ssms.exe");
            var unrelated = Path.Combine(programFiles, "Unrelated", "Nested", "navicat.exe");
            foreach (var path in new[] { navicat, modernSsms, legacySsms, unrelated })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0]);
            }

            var roots = new[] { programFiles, programFilesX86 };
            var navicatCandidates = DatabaseToolLocator.EnumerateNavicatDefaultCandidates(roots).ToArray();
            var ssmsCandidates = DatabaseToolLocator.EnumerateSsmsDefaultCandidates(roots).ToArray();

            CollectionAssert.Contains(navicatCandidates, navicat);
            CollectionAssert.DoesNotContain(navicatCandidates, unrelated);
            CollectionAssert.Contains(ssmsCandidates, modernSsms);
            CollectionAssert.Contains(ssmsCandidates, legacySsms);
            Assert.IsFalse(navicatCandidates.Any(path => path.IndexOf("Nested", StringComparison.OrdinalIgnoreCase) >= 0));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void DesktopDetectionDoesNotRecurseIntoSubdirectories()
    {
        var desktop = CreateTemporaryDirectory();
        try
        {
            var navicat = Path.Combine(desktop, "navicat.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), navicat);
            var nestedSsms = Path.Combine(desktop, "Nested", "Ssms.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedSsms)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), nestedSsms);

            var detectedNavicat = DatabaseToolLocator.FindDesktop(DatabaseToolKind.Navicat, [desktop]);
            var detectedSsms = DatabaseToolLocator.FindDesktop(DatabaseToolKind.SqlServerManagementStudio, [desktop]);

            Assert.IsNotNull(detectedNavicat);
            Assert.AreEqual(navicat, detectedNavicat.ExecutablePath, true);
            Assert.IsNull(detectedSsms, "桌面检测不得递归进入子目录。");
        }
        finally
        {
            Directory.Delete(desktop, true);
        }
    }

    [TestMethod]
    public void RegisteredDatabaseToolUninstallCommandsAreParsedWithoutCmdShell()
    {
        var quoted = DatabaseToolUninstaller.ParseRegisteredCommand(
            "\"C:\\Program Files\\PremiumSoft\\Navicat Premium 17\\unins000.exe\" /SILENT");
        Assert.IsNotNull(quoted);
        Assert.AreEqual(@"C:\Program Files\PremiumSoft\Navicat Premium 17\unins000.exe", quoted.FileName);
        Assert.AreEqual("/SILENT", quoted.Arguments);

        var msi = DatabaseToolUninstaller.ParseRegisteredCommand("MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}");
        Assert.IsNotNull(msi);
        Assert.AreEqual("MsiExec.exe", msi.FileName);
        Assert.AreEqual(
            "/X{12345678-1234-1234-1234-1234567890AB}",
            DatabaseToolUninstaller.ConvertMsiInstallToUninstall(msi.Arguments));
    }

    [TestMethod]
    public void EnvironmentDownloadSettingsAcceptsOverridesAndRejectsInvalidUrls()
    {
        var settings = new NameValueCollection
        {
            [EnvironmentDownloadSettings.FrpPackageKey] = "https://mirror.example.com/frp.zip",
            [EnvironmentDownloadSettings.NginxPackageKey] = "not-a-url"
        };

        var downloads = EnvironmentDownloadSettings.Load(settings);

        Assert.AreEqual("https://mirror.example.com/frp.zip", downloads.FrpPackageUrl);
        Assert.AreEqual(EnvironmentDownloadSettings.DefaultNginxPackageUrl, downloads.NginxPackageUrl);
        Assert.AreEqual(
            "https://aka.ms/sql2025EnterpriseDev",
            downloads.SqlServer2025EnterpriseDeveloperUrl);
    }

    [TestMethod]
    public void MySqlReleaseCatalogUsesOfficialWindowsArchives()
    {
        Assert.AreEqual(MySqlReleaseCatalog.MySql8411Id, MySqlReleaseCatalog.Default.Id);
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
            MySqlReleaseCatalog.MySql8411.PackageUrl);
        Assert.AreEqual("mysql-8.4.11-winx64.zip", MySqlReleaseCatalog.MySql8411.PackageFileName);
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-9.7/mysql-9.7.2-winx64.zip",
            MySqlReleaseCatalog.MySql972.PackageUrl);
        Assert.AreEqual("mysql-9.7.2-winx64.zip", MySqlReleaseCatalog.MySql972.PackageFileName);
        Assert.AreEqual("MySQL 8.4.11 LTS", MySqlReleaseCatalog.MySql8411.DisplayName);
        Assert.AreEqual("MySQL 5.6.31", MySqlReleaseCatalog.LegacyMySql56.DisplayName);
        Assert.AreEqual(
            MySqlReleaseCatalog.MySql972Id,
            MySqlReleaseCatalog.FindByServerVersion("mysqld Ver 9.7.2 for Win64 on x86_64")?.Id);

        var legacy = MySqlReleaseCatalog.Resolve(
            MySqlReleaseCatalog.LegacyMySql56Id,
            "https://mirror.example.com/mysql.zip");
        Assert.IsTrue(legacy.IsLegacy);
        Assert.AreEqual("https://mirror.example.com/mysql.zip", legacy.PackageUrl);
    }

    [TestMethod]
    public void SupplierDownloadPreservesLegacyHttpButRejectsUntrustedPlainHttp()
    {
        var legacyUri = new Uri("http://regservice.itmc.cn/down/tomcat/nginx-1.14.2.zip");

        Assert.AreEqual(legacyUri, McPanelStoreClient.ValidateDownloadUri(legacyUri));
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
            McPanelStoreClient.ValidateDownloadUri(new Uri(MySqlReleaseCatalog.MySql8411.PackageUrl)).AbsoluteUri);
        Assert.ThrowsException<InvalidOperationException>(() =>
            McPanelStoreClient.ValidateDownloadUri(new Uri("http://mirror.example.com/package.zip")));
    }

    [TestMethod]
    public void SupplierCatalogSvnSourcesMatchOriginalUpdateClientRouting()
    {
        Assert.IsTrue(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://update.itmc.org.cn/svn/Shop/Shop/")));
        Assert.IsTrue(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://update.product.itmc.cn:9443/udp/itmc_baike_kuajing/")));
        Assert.IsFalse(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("http://regservice.itmc.cn/down/tomcat/nginx-1.14.2.zip")));
        Assert.IsFalse(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip")));
    }

    [TestMethod]
    public void MySqlEnvironmentItemDefaultsToLtsAndPreservesSelection()
    {
        var item = new EnvironmentItem(EnvironmentKind.MySql, "MySQL", "", "test");

        Assert.AreEqual(MySqlReleaseCatalog.MySql8411Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual(Visibility.Visible, item.MySqlVersionSelectorVisibility);

        item.SelectedMySqlReleaseId = MySqlReleaseCatalog.MySql972Id;
        Assert.AreEqual(MySqlReleaseCatalog.MySql972Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual("MySQL 9.7.2 LTS", item.SelectedMySqlRelease.DisplayName);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "MySQL 已安装",
            RuntimeStatusKind.Stopped,
            MySqlReleaseCatalog.MySql972Id,
            "9.7.2"));
        Assert.AreEqual(MySqlReleaseCatalog.MySql972Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual("版本  MySQL 9.7.2 LTS", item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "MySQL 已安装",
            RuntimeStatusKind.Stopped,
            DetectedMySqlVersion: "8.0.36"));
        Assert.AreEqual("版本  MySQL 8.0.36", item.OptionLabel);
    }

    [TestMethod]
    public void SqlServerEnvironmentItemDefaultsToRecommendedReleaseAndPreservesSelection()
    {
        var item = new EnvironmentItem(EnvironmentKind.SqlServer, "SQLServer", "", "test");

        Assert.AreEqual(SqlServerReleaseCatalog.Recommended.Id, item.SelectedSqlServerReleaseId);
        Assert.AreEqual(Visibility.Visible, item.SqlServerVersionSelectorVisibility);
        Assert.AreEqual(
            SqlServerReleaseCatalog.Recommended.DisplayName,
            item.SelectedSqlServerRelease.DisplayName);
        Assert.IsTrue(SqlServerReleaseCatalog.Contains(SqlServerReleaseCatalog.SqlServer2012Id));
        Assert.AreEqual("SQL Server 2012 Express SP4", SqlServerReleaseCatalog.SqlServer2012.DisplayName);
        Assert.IsTrue(SqlServerReleaseCatalog.Contains(SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId));
        Assert.AreEqual(
            "SQL Server 2025 Enterprise Developer",
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloper.DisplayName);
        Assert.AreEqual(
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
            SqlServerReleaseCatalog.FindByInstallation(17, "Enterprise Developer Edition")?.Id);
        Assert.AreEqual(
            SqlServerReleaseCatalog.SqlServer2025Id,
            SqlServerReleaseCatalog.FindByInstallation(17, "Express Edition")?.Id);

        item.SelectedSqlServerReleaseId = SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId;
        Assert.AreEqual(
            "版本  SQL Server 2025 Enterprise Developer",
            item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "SQL Server 已安装",
            RuntimeStatusKind.Stopped,
            DetectedSqlServerReleaseId: SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
            DetectedSqlServerDisplayName: "SQL Server 2025 Enterprise Developer"));
        Assert.AreEqual(SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId, item.SelectedSqlServerReleaseId);
        Assert.AreEqual("版本  SQL Server 2025 Enterprise Developer", item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "SQL Server 已安装",
            RuntimeStatusKind.Stopped,
            DetectedSqlServerDisplayName: "SQL Server 2019 Standard Edition"));
        Assert.AreEqual("版本  SQL Server 2019 Standard Edition", item.OptionLabel);

        var developerPlan = EnvironmentInstaller.GetSqlServerInstallPlan(
            EnvironmentDownloadSettings.Load(new NameValueCollection()),
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId);
        Assert.AreEqual("SQL2025-SSEI-EntDev.exe", developerPlan.InstallerFileName);
        Assert.AreEqual(
            EnvironmentDownloadSettings.DefaultSqlServer2025EnterpriseDeveloperUrl,
            developerPlan.DownloadUrl);
        Assert.IsTrue(developerPlan.UsesSseiInstaller);
        Assert.AreEqual("*", developerPlan.MediaPackageSearchPattern);
        Assert.AreEqual("ISO", developerPlan.MediaType);

        var developerScript = EnvironmentInstaller.BuildModernSqlServerScript(
            "D:\\MCPanel\\Downloads\\SQL2025-SSEI-EntDev.exe",
            "D:\\MCPanel\\MSSQL",
            developerPlan,
            "test-password");
        StringAssert.Contains(developerScript, "FileSystem -eq 'CDFS'");
        StringAssert.Contains(developerScript, "$_.Size -eq $image.Size");
        StringAssert.Contains(developerScript, "Get-ChildItem -LiteralPath $isoRoot -Filter 'setup.exe'");
        StringAssert.Contains(developerScript, "Get-CurrentWindowsUser");
        StringAssert.Contains(developerScript, "Get-SqlAdminAccounts");
        StringAssert.Contains(developerScript, "Ensure-CurrentWindowsSqlLogin");
        StringAssert.Contains(developerScript, "SQLSYSADMINACCOUNTS=' + $adminArgument");

        var legacyScript = EnvironmentInstaller.BuildSqlServer2008Script(
            "D:\\MCPanel\\Downloads\\SQLEXPR_2008_x64.exe",
            "D:\\MCPanel\\MSSQL",
            "test-password",
            "SQL Server 2008 Express");
        StringAssert.Contains(legacyScript, "Get-CurrentWindowsUser");
        StringAssert.Contains(legacyScript, "Get-SqlAdminAccounts");
        StringAssert.Contains(legacyScript, "Ensure-CurrentWindowsSqlLogin");
        StringAssert.Contains(legacyScript, "SQLSYSADMINACCOUNTS=' + $adminArgument");

        var expressPlan = EnvironmentInstaller.GetSqlServerInstallPlan(
            EnvironmentDownloadSettings.Load(new NameValueCollection()),
            SqlServerReleaseCatalog.SqlServer2025Id);
        Assert.AreEqual("Core", expressPlan.MediaType);

        item.SelectedSqlServerReleaseId = SqlServerReleaseCatalog.SqlServer2022Id;
        Assert.AreEqual(SqlServerReleaseCatalog.SqlServer2022Id, item.SelectedSqlServerReleaseId);
        Assert.AreEqual("SQL Server 2022 Express", item.SelectedSqlServerRelease.ToString());
    }

    [TestMethod]
    public void FrpEnvironmentCardSeparatesInstallationFromRuntimeControls()
    {
        var item = new EnvironmentItem(
            EnvironmentKind.FrpTunnel,
            "FRP 内网穿透",
            "frp 0.71.0",
            "test");

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            false,
            false,
            "FRP 尚未安装",
            RuntimeStatusKind.NotInstalled));
        Assert.AreEqual(System.Windows.Visibility.Visible, item.InstallButtonVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, item.ServiceControlsVisibility);
        Assert.AreEqual("安装", item.ActionText);
        Assert.IsFalse(item.CanStart);
        Assert.IsFalse(item.CanUninstall);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "FRP 已安装但未运行",
            RuntimeStatusKind.Stopped));
        Assert.AreEqual(System.Windows.Visibility.Collapsed, item.InstallButtonVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, item.ServiceControlsVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, item.FrpManagementVisibility);
        Assert.IsTrue(item.CanStart);
        Assert.IsTrue(item.CanUninstall);
        Assert.IsFalse(item.CanStop);
    }

    [TestMethod]
    public void FrpStartRefusesToInstallOrCreateFilesImplicitly()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var manager = new FrpManager(root);

            var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                manager.StartAsync().GetAwaiter().GetResult());

            StringAssert.Contains(exception.Message, "请先在“环境管理”中点击 FRP 的“安装”按钮");
            Assert.IsFalse(manager.IsInstalled);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Frp")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Downloads", "Frp")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void FrpUninstallRemovesClientConfigurationAndDownloadCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var clientRoot = Path.Combine(root, "Frp");
            var downloadRoot = Path.Combine(root, "Downloads", "Frp");
            Directory.CreateDirectory(clientRoot);
            Directory.CreateDirectory(downloadRoot);
            File.WriteAllText(Path.Combine(clientRoot, "frpc.exe"), "test");
            File.WriteAllText(Path.Combine(clientRoot, "frpc.toml"), "serverAddr = \"127.0.0.1\"");
            File.WriteAllText(Path.Combine(downloadRoot, "frp.zip"), "test");

            using var manager = new FrpManager(root);
            Assert.IsTrue(manager.IsInstalled);

            var message = manager.UninstallAsync().GetAwaiter().GetResult();

            StringAssert.Contains(message, "已卸载");
            Assert.IsFalse(manager.IsInstalled);
            Assert.IsFalse(Directory.Exists(clientRoot));
            Assert.IsFalse(Directory.Exists(downloadRoot));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ElevatedScriptFailureShowsNativeLogTailAndFullPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var logPath = Path.Combine(root, "uninstall-test.log");
            File.WriteAllText(logPath, "step started\r\nAccess is denied by the service manager\r\n", Encoding.UTF8);

            var exception = EnvironmentOperationDiagnostics.CreateScriptFailure(
                "测试卸载 ",
                1,
                logPath);

            Assert.AreEqual(1, exception.ExitCode);
            Assert.AreEqual(Path.GetFullPath(logPath), exception.LogPath);
            StringAssert.Contains(exception.Message, "退出码：1");
            StringAssert.Contains(exception.Message, "Access is denied by the service manager");
            StringAssert.Contains(exception.Message, Path.GetFullPath(logPath));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void EnvironmentUninstallScriptsParseAndIncludeFailurePostconditions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var scripts = new[]
            {
                (Name: "iis", Text: EnvironmentRuntimeService.BuildIisUninstallScript()),
                (Name: "sqlserver", Text: EnvironmentRuntimeService.BuildSqlServerUninstallScript()),
                (Name: "mysql", Text: EnvironmentRuntimeService.BuildMySqlUninstallScript(null)),
                (Name: "mysql-action", Text: EnvironmentRuntimeService.BuildMySqlServiceActionScript(
                    "start",
                    "mysql-service-action.log",
                    "mysql-service-action.result")),
                (Name: "mysql-preparation", Text: EnvironmentInstaller.BuildMySqlInstallPreparationScript()),
                (Name: "mysql-install", Text: EnvironmentInstaller.BuildMySqlScript(
                    @"D:\MCPanel\MySQL",
                    @"D:\MCPanel\MySQL\bin\mysqld.exe",
                    @"D:\MCPanel\MySQL\bin\mysql.exe",
                    "test-password")),
                (Name: "mysql-cleanup", Text: EnvironmentInstaller.BuildMySqlInstallCleanupScript(@"D:\MCPanel\MySQL")),
                (Name: "iis-install", Text: EnvironmentInstaller.BuildIisScript(
                    @"D:\MCPanel\Downloads\URLRewrite.msi")),
                (Name: "action", Text: EnvironmentRuntimeService.BuildElevatedPowerShellScript(
                    "& whoami.exe",
                    Path.Combine(root, "environment-action.log")))
            };
            var parserScript = Path.Combine(root, "parse.ps1");
            File.WriteAllText(parserScript,
                "param([string]$Target)\n$tokens=$null; $errors=$null\n" +
                "[System.Management.Automation.Language.Parser]::ParseFile($Target,[ref]$tokens,[ref]$errors) | Out-Null\n" +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }\nexit 0\n",
                Encoding.UTF8);

            foreach (var script in scripts)
            {
                var scriptPath = Path.Combine(root, "uninstall-" + script.Name + ".ps1");
                File.WriteAllText(scriptPath, script.Text, Encoding.UTF8);
                using var parser = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{parserScript}\" -Target \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                Assert.IsNotNull(parser);
                parser!.WaitForExit();
                Assert.AreEqual(0, parser.ExitCode, script.Name + ": " + parser.StandardError.ReadToEnd());
                StringAssert.Contains(script.Text, "Start-Transcript");
            }

            StringAssert.Contains(scripts[1].Text, "SQL Server 服务仍然存在");
            StringAssert.Contains(scripts[1].Text, "$migratedDataRoot");
            StringAssert.Contains(scripts[1].Text, "exit 1");
            StringAssert.Contains(scripts[2].Text, "MySQL80 服务仍然存在");
            StringAssert.Contains(scripts[2].Text, "exit 1");
            StringAssert.Contains(scripts[2].Text, "$mysqlInstallationRoots");
            StringAssert.Contains(scripts[2].Text, "MySQL 安装目录与共享 Runtime 根目录重叠");
            StringAssert.Contains(scripts[2].Text, "Removing MySQL installation directory");
            StringAssert.Contains(scripts[2].Text, "非 MCPanel 管理的 MySQL80 服务");
            StringAssert.Contains(scripts[2].Text, "Is-ManagedMySqlPath");
            StringAssert.Contains(scripts[2].Text, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
            Assert.IsFalse(scripts[2].Text.Contains("$deleteTargets", StringComparison.Ordinal));
            StringAssert.Contains(scripts[3].Text, "--bind-address=127.0.0.1");
            Assert.IsFalse(scripts[3].Text.Contains("--skip-networking=0", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(scripts[4].Text, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void IisInstallScriptChecksMsiAndIisResetExitCodes()
    {
        var script = EnvironmentInstaller.BuildIisScript(Path.Combine(Path.GetTempPath(), "URLRewrite.msi"));

        StringAssert.Contains(script, "$rewriteProcess = Start-Process");
        StringAssert.Contains(script, "$rewriteProcess.ExitCode -notin @(0, 3010)");
        StringAssert.Contains(script, "if ($LASTEXITCODE -ne 0)");
        StringAssert.Contains(script, "Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -All -NoRestart -ErrorAction Stop");
    }

    [TestMethod]
    public void NginxUninstallRefusesSharedRuntimeRoot()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => NginxRuntimeManager.EnsureSafeDeleteRoot(ComponentPaths.RuntimeRoot));
        Assert.ThrowsException<InvalidOperationException>(
            () => NginxRuntimeManager.EnsureSafeDeleteRoot(ComponentPaths.LegacyRuntimeRoot));

        var dedicatedRoot = CreateTemporaryDirectory();
        try
        {
            NginxRuntimeManager.EnsureSafeDeleteRoot(dedicatedRoot);
        }
        finally
        {
            DeleteTemporaryTree(dedicatedRoot);
        }
    }

    [TestMethod]
    public void AiProviderSettingsReadsConfigurationOverrides()
    {
        var appSettings = new NameValueCollection
        {
            [AiProviderSettings.ProviderConfigKey] = "OpenAI",
            [AiProviderSettings.EndpointConfigKey] = "https://api.example.com/v1/chat/completions",
            [AiProviderSettings.ModelConfigKey] = "ops-model",
            [AiProviderSettings.ApiKeyConfigKey] = "test-api-key"
        };

        var settings = AiProviderSettings.FromAppSettings(appSettings);

        Assert.AreEqual("OpenAI", settings.Provider);
        Assert.AreEqual("https://api.example.com/v1/chat/completions", settings.Endpoint);
        Assert.AreEqual("ops-model", settings.Model);
        Assert.AreEqual("test-api-key", settings.ApiKey);
    }

    [TestMethod]
    public void SqlServerNewPasswordMatchesOriginalStoreFormat()
    {
        var credentials = SqlServerCredentialStore.CreateNew();

        StringAssert.Matches(credentials.Password, new Regex(@"^it[0-9A-Z]{8}8$"));
        Assert.AreEqual(11, credentials.Password.Length);
    }

    [TestMethod]
    public void SsmsArgumentsUseParametersSharedByOldAndCurrentVersions()
    {
        var arguments = PanelSettingsService.BuildSqlServerArguments(
            new SqlServerDefaultCredentials("localhost", 1433, "sa", "secret", "MSSQLSERVER"));

        StringAssert.Contains(arguments, "-nosplash");
        StringAssert.Contains(arguments, "-S");
        Assert.IsFalse(arguments.Contains("-E"));
        Assert.IsFalse(arguments.Contains("-U"));
        Assert.IsFalse(arguments.Contains("-P"));
    }

    [TestMethod]
    public void SsmsUninstallUsesProductAndChannelInsteadOfInstallPathOrWait()
    {
        var arguments = DatabaseToolUninstaller.BuildModernSsmsUninstallArguments(
            "Microsoft.VisualStudio.Product.Ssms",
            "SSMS.22.SSMS.Release");

        StringAssert.StartsWith(arguments, "uninstall");
        StringAssert.Contains(arguments, "--productId");
        StringAssert.Contains(arguments, "Microsoft.VisualStudio.Product.Ssms");
        StringAssert.Contains(arguments, "--channelId");
        StringAssert.Contains(arguments, "SSMS.22.SSMS.Release");
        Assert.IsFalse(arguments.Contains("--installPath"));
        Assert.IsFalse(arguments.Contains("--wait"));
    }

    [TestMethod]
    public void SsmsReleaseMatchesLegacyAndCurrentSqlServerGenerations()
    {
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(10).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(11).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(12).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(13).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(14).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(15).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(16).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(17).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(null).Id);
        Assert.AreEqual(11, SqlServerInstallationDetector.TryParseMajorVersion("11.0.7001.0"));
        Assert.AreEqual(17, SqlServerInstallationDetector.TryParseMajorVersion("MSSQL17.MSSQLSERVER"));

        var sqlServer2008R2 = new SqlServerInstallationInfo(
            "MSSQLSERVER",
            "MSSQL10_50.MSSQLSERVER",
            10,
            "10.50.6000.34",
            "Express Edition");
        var recommendation = SsmsReleaseCatalog.ResolveForInstalledSqlServer(sqlServer2008R2);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, recommendation.Release.Id);
        StringAssert.Contains(recommendation.Summary, "SQL Server 2008 R2");
        Assert.AreEqual("SQL Server 2008 R2 Express Edition", sqlServer2008R2.GenerationDisplayName);
    }

    [TestMethod]
    public void DatabaseToolViewModelSeparatesSsmsMatchFromInstallStatus()
    {
        var tool = new DatabaseToolViewModel("SQL Server 连接工具", "SSMS");

        tool.Apply(null, @"D:\MCPanel\SSMS", "SSMS 22");

        Assert.AreEqual("未安装", tool.StatusText);
        Assert.AreEqual("（SSMS 22）", tool.MatchedVersionText);

        tool.Apply(
            new DatabaseToolInstallation(
                DatabaseToolKind.SqlServerManagementStudio,
                @"D:\MCPanel\SSMS\Common7\IDE\Ssms.exe",
                DatabaseToolSource.ManagedFolder,
                @"D:\MCPanel\SSMS\Common7\IDE\Ssms.exe",
                "Microsoft SQL Server Management Studio",
                "22.0"),
            @"D:\MCPanel\SSMS",
            "SSMS 22");

        Assert.AreEqual("已安装", tool.StatusText);
        Assert.AreEqual("（SSMS 22）", tool.MatchedVersionText);
    }

    [TestMethod]
    public void DatabaseToolViewModelBuildsStableActionTags()
    {
        var tool = new DatabaseToolViewModel("MySQL 连接工具", "Navicat", "Navicat");

        Assert.AreEqual("NavicatPath", tool.PathActionTag);
        Assert.AreEqual("NavicatInstall", tool.InstallActionTag);
        Assert.AreEqual("NavicatUninstall", tool.UninstallActionTag);
        Assert.AreEqual("NavicatConnect", tool.ConnectActionTag);
        Assert.AreEqual("NavicatConfigure", tool.ConfigureActionTag);
    }

    [TestMethod]
    public void SsmsInstallArgumentsKeepLegacyBootstrapperCompatible()
    {
        var legacy = PanelSettingsService.BuildSsmsInstallArguments(SsmsReleaseCatalog.Ssms18121);
        StringAssert.Contains(legacy, "/quiet");
        StringAssert.Contains(legacy, "/norestart");
        StringAssert.Contains(legacy, "SSMSInstallRoot=");
        Assert.IsFalse(legacy.Contains("--installPath"));

        var current = PanelSettingsService.BuildSsmsInstallArguments(SsmsReleaseCatalog.Ssms22);
        StringAssert.Contains(current, "--installPath");
        StringAssert.Contains(current, "--path");
        Assert.IsFalse(current.Contains("SSMSInstallRoot="));
    }

    [TestMethod]
    public void LegacyRuntimeConfigTextStripsUtf8Bom()
    {
        Assert.AreEqual(
            "serverAddr = \"127.0.0.1\"",
            FrpManager.NormalizeTomlForProcess("\uFEFFserverAddr = \"127.0.0.1\""));
        Assert.AreEqual(
            "-Xms128m",
            EnvironmentInstaller.NormalizeTomcatJvmProperties("\uFEFF-Xms128m"));
    }

    [TestMethod]
    public void MySqlNewPasswordMatchesOriginalStoreDefault()
    {
        var credentials = MySqlCredentialStore.CreateNew();

        Assert.AreEqual("mike", credentials.Password);
        Assert.AreEqual(3380, credentials.Port);

        var script = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            credentials.Password);
        StringAssert.Contains(script, "-P3380");
        StringAssert.Contains(script, "port=3380");

        var existingInstallationScript = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            credentials.Password,
            3306);
        StringAssert.Contains(existingInstallationScript, "-P3306");
        StringAssert.Contains(existingInstallationScript, "port=3306");
    }

    [TestMethod]
    public void ProductXmlIdentityIsReappliedAfterSupplierUpdate()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = Path.Combine(root, "config.xml");
            File.WriteAllText(config, "<?xml version=\"1.0\" encoding=\"utf-8\"?><ROOT><SystemSoft><SoftVersionID>OLD</SoftVersionID></SystemSoft><Keep>value</Keep></ROOT>");

            Assert.IsTrue(ProductConfigurationService.TryApplyXmlIdentity(root, "DS3107", out var updatedFile));
            Assert.AreEqual(config, updatedFile);
            var document = XDocument.Load(config);
            Assert.AreEqual("DS3107", document.Descendants("SoftVersionID").Single().Value);
            Assert.AreEqual("value", document.Descendants("Keep").Single().Value);
            Assert.IsFalse(File.ReadAllText(config).Contains("encoding=\"utf-16\"", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void JavaProductConfigurationUpdatesAllOriginalStoreKeysWithoutDuplicates()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var appRoot = Path.Combine(root, "application");
            Directory.CreateDirectory(appRoot);
            var config = Path.Combine(appRoot, "systemConfig.yml");
            File.WriteAllText(config,
                "custom.keep=yes\n" +
                "global.system.VersionID=OLD\n" +
                "global.system.MySQLPassword=OLDPASSWORD\n");
            var credentials = new MySqlDefaultCredentials("127.0.0.1", 3307, "root", "mike");

            ProductConfigurationService.ApplyJavaConfiguration(root, appRoot, "YX030301", "高级", "MySQL5.6", credentials);
            ProductConfigurationService.ApplyJavaConfiguration(root, appRoot, "YX030301", "高级", "MySQL5.6", credentials);

            var text = File.ReadAllText(config);
            StringAssert.Contains(text, "custom.keep=yes");
            StringAssert.Contains(text, "global.system.VersionName=高级");
            StringAssert.Contains(text, "global.system.VersionID=YX030301");
            StringAssert.Contains(text, "global.system.MySQLambient=MySQL5.6");
            StringAssert.Contains(text, "global.system.MySQLPort=3307");
            StringAssert.Contains(text, "global.system.MySQLUserName=root");
            StringAssert.Contains(text, "global.system.MySQLPassword=mike");
            Assert.AreEqual(1, Regex.Matches(text, "global\\.system\\.VersionID=").Count);
            Assert.AreEqual(1, Regex.Matches(text, "global\\.system\\.MySQLPassword=").Count);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ProductPrerequisitesMatchOriginalJavaAndDotNetStacks()
    {
        var java = new ProductItem("JAVA1", "Java 产品", "实训", string.Empty, ProductSource.Online)
        {
            RunEnvironment = "Tomcat 8",
            DevLanguage = "Java"
        };
        var dotnet = new ProductItem("NET1", ".NET 产品", "实训", string.Empty, ProductSource.Online)
        {
            RunEnvironment = "IIS / .NET Framework",
            SqlEnvironment = "SQL Server 2022",
            DevLanguage = "C#"
        };

        CollectionAssert.AreEqual(
            new[] { EnvironmentKind.Tomcat, EnvironmentKind.MySql },
            ProductEnvironmentPreflight.GetRequiredKinds(java).ToArray());
        CollectionAssert.AreEqual(
            new[] { EnvironmentKind.Iis, EnvironmentKind.SqlServer },
            ProductEnvironmentPreflight.GetRequiredKinds(dotnet).ToArray());
    }

    [TestMethod]
    public void TomcatStartupCommandRestoresProductsWithoutOpeningMainWindow()
    {
        var command = TomcatProductStartupManager.BuildStartupCommand(@"D:\MCPanel\MCPanel.exe");

        Assert.AreEqual("\"D:\\MCPanel\\MCPanel.exe\" --restore-tomcat-products", command);
        Assert.IsTrue(TomcatProductStartupManager.IsRestoreRequest(new[] { "--restore-tomcat-products" }));
    }

    [TestMethod]
    public void PanelStartupCommandUsesLazyTrayMode()
    {
        var command = PanelSettingsService.BuildStartupCommand(@"D:\MCPanel\MCPanel.exe");

        Assert.AreEqual("\"D:\\MCPanel\\MCPanel.exe\" --tray", command);
        Assert.IsTrue(ApplicationLaunchMode.IsTrayStartupRequest(new[] { "--TRAY" }));
        Assert.IsFalse(ApplicationLaunchMode.IsTrayStartupRequest(Array.Empty<string>()));
        Assert.IsFalse(ApplicationLaunchMode.IsTrayStartupRequest(new[] { "--tray=1" }));
    }

    [TestMethod]
    public void NginxManagedWebsiteConfigIncludesSslRedirectAndBandwidth()
    {
        var options = new NginxRuntimeOptions
        {
            ListenPort = 80,
            Rules =
            [
                new NginxProxyRule
                {
                    Name = "产品域名 TEST",
                    ListenPort = 80,
                    ServerName = "example.test www.example.test",
                    LocationPath = "/",
                    ProxyTarget = "http://127.0.0.1:9000/TEST/",
                    SslEnabled = true,
                    HttpsPort = 443,
                    SslCertificatePath = @"D:\MCPanel\Nginx\conf\certificate.pem",
                    SslCertificateKeyPath = @"D:\MCPanel\Nginx\conf\private-key.pem",
                    RedirectHttpToHttps = true,
                    MaxRateKbps = 2048
                }
            ]
        };

        var config = NginxRuntimeManager.BuildManagedConfig(options);

        StringAssert.Contains(config, "listen       80;");
        StringAssert.Contains(config, "return 301 https://$host$request_uri;");
        StringAssert.Contains(config, "listen       443 ssl;");
        StringAssert.Contains(config, "ssl_protocols        TLSv1.2;");
        StringAssert.Contains(config, "limit_rate 2048k;");
        StringAssert.Contains(config, "proxy_pass http://127.0.0.1:9000/TEST/;");
        Assert.AreEqual("http://127.0.0.1:9000/TEST/", NginxRuntimeManager.NormalizeProxyTarget("http://127.0.0.1:9000/TEST/"));
        CollectionAssert.AreEqual(new[] { 80, 443 }, NginxRuntimeManager.GetEffectiveListenPorts(options).ToArray());
    }

    [TestMethod]
    public void NginxServiceOwnershipRequiresACompleteRootToken()
    {
        Assert.IsTrue(NginxWindowsServiceManager.ImagePathContainsRoot(
            "\"D:\\MCPanel\\MCPanel.exe\" --mcpanel-nginx-service \"D:\\MCPanel\\Nginx\\nginx-1.14.2\"",
            @"D:\MCPanel\Nginx\nginx-1.14.2"));
        Assert.IsFalse(NginxWindowsServiceManager.ImagePathContainsRoot(
            "\"D:\\MCPanel\\MCPanel.exe\" --mcpanel-nginx-service \"D:\\MCPanel\\Nginx\\nginx-1.14.20\"",
            @"D:\MCPanel\Nginx\nginx-1.14.2"));
    }

    [TestMethod]
    public void NginxDefaultsUseLegacyPort72()
    {
        Assert.AreEqual(72, NginxRuntimeManager.DefaultListenPort);
        Assert.AreEqual(72, new NginxRuntimeOptions().ListenPort);
        Assert.AreEqual(72, new NginxProxyRule().ListenPort);
        Assert.AreEqual(72, NginxRuntimeManager.CreateDefaultRule().ListenPort);

        var config = NginxRuntimeManager.BuildManagedConfig(new NginxRuntimeOptions());
        StringAssert.Contains(config, "listen       72;");
    }

    [TestMethod]
    public void NginxRuntimeProbeReadsManualListenPortChangesAndIncludedFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            var included = Path.Combine(conf, "conf.d");
            Directory.CreateDirectory(included);
            File.WriteAllText(
                Path.Combine(conf, "nginx.conf"),
                "events {}\nhttp { listen 127.0.0.1:8123; include conf.d/*.conf; }",
                Encoding.UTF8);
            File.WriteAllText(
                Path.Combine(included, "manual.conf"),
                "server { listen 9443 ssl; }",
                Encoding.UTF8);

            CollectionAssert.AreEqual(
                new[] { 8123, 9443 },
                NginxRuntimeManager.ReadConfiguredListenPorts(root).ToArray());
            CollectionAssert.AreEqual(
                new[] { 8123, 9443 },
                NginxRuntimeManager.ParseConfiguredListenPorts("# listen 80;\nlisten 8123; listen [::]:9443 ssl;").ToArray());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void NativeComponentsUseAsciiCompatibilityRootsForChineseInstallPaths()
    {
        var chineseRoot = Path.Combine(Path.GetTempPath(), "中文", "MCPanel");
        var asciiRoot = Path.Combine(Path.GetTempPath(), "MCPanel");

        var chineseNginx = PathCompatibility.GetNativeComponentRoot(chineseRoot, "Nginx");
        var chineseMySql = PathCompatibility.GetNativeComponentRoot(chineseRoot, "MySQL");

        Assert.IsFalse(PathCompatibility.ContainsNonAscii(chineseNginx));
        Assert.IsFalse(PathCompatibility.ContainsNonAscii(chineseMySql));
        Assert.AreEqual(Path.Combine(asciiRoot, "Nginx"), PathCompatibility.GetNativeComponentRoot(asciiRoot, "Nginx"));
    }

    [TestMethod]
    public void MySqlInstallScriptCleansFailedServiceAndWritesNativeErrorLog()
    {
        var script = EnvironmentInstaller.BuildMySqlScript(
            @"D:\MCPanel\MySQL",
            @"D:\MCPanel\MySQL\bin\mysqld.exe",
            @"D:\MCPanel\MySQL\bin\mysql.exe",
            "test-password");

        StringAssert.Contains(script, "$registeredService = $false");
        StringAssert.Contains(script, "$serviceWasPresent = [bool](Get-Service -Name 'MySQL80'");
        StringAssert.Contains(script, "if ($serviceWasPresent) { Fail '安装前仍检测到 MySQL80 服务");
        StringAssert.Contains(script, "sc.exe delete MySQL80");
        StringAssert.Contains(script, "log-error=D:/MCPanel/MySQL/data/mysql-itmc.err");
        StringAssert.Contains(script, @"Ver\s+(8|9)\.|mysqld\s+(8|9)\.");
        StringAssert.Contains(script, "ALTER USER 'root'@'localhost' IDENTIFIED BY");
        StringAssert.Contains(script, "SET PASSWORD FOR 'root'@'localhost' = PASSWORD");
        StringAssert.Contains(script, "--init-file=$initFile");
        StringAssert.Contains(script, "--bind-address=127.0.0.1");
        StringAssert.Contains(script, "bind-address=127.0.0.1");
        StringAssert.Contains(script, "Remove-Item -LiteralPath $initFile");
        StringAssert.Contains(script, "SHUTDOWN;");
        Assert.IsFalse(script.Contains("--skip-grant-tables", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("skip-networking=0", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("mysql_native_password", StringComparison.OrdinalIgnoreCase));

        var serviceAction = EnvironmentRuntimeService.BuildMySqlServiceActionScript(
            "start",
            "mysql-action.log",
            "mysql-action.result");
        StringAssert.Contains(serviceAction, "--init-file=$initFile");
        StringAssert.Contains(serviceAction, "SET PASSWORD FOR 'root'@'localhost' = PASSWORD");
        StringAssert.Contains(serviceAction, "SHUTDOWN;");
        StringAssert.Contains(serviceAction, "$mysqlRoots | Where-Object");
        StringAssert.Contains(serviceAction, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
        Assert.IsFalse(serviceAction.Contains("$mysqlSearchRoots |", StringComparison.Ordinal));
        Assert.IsFalse(serviceAction.Contains("--skip-grant-tables", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void CustomHttpsRedirectDoesNotDuplicateQueryString()
    {
        var rule = CustomWebsiteService.BuildHttpsRedirectRule("{HTTP_HOST}:8443");
        var action = rule.Element("action");

        Assert.IsNotNull(action);
        Assert.AreEqual("https://{HTTP_HOST}:8443{REQUEST_URI}", action!.Attribute("url")?.Value);
        Assert.AreEqual("false", action.Attribute("appendQueryString")?.Value);
    }

    [TestMethod]
    public void AccountApiFormatsIpv6ListenerAndEndpointHosts()
    {
        Assert.AreEqual("+", AccountApiHost.FormatListenerHost("::"));
        Assert.AreEqual("[::1]", AccountApiHost.FormatListenerHost("::1"));
        Assert.AreEqual("[::1]", AccountApiConfiguration.FormatUriHost("[::1]"));

        var configuration = new AccountApiConfiguration { BindAddress = "::1", Port = 8088 };
        Assert.AreEqual("http://[::1]:8088", configuration.Endpoint);
    }

    [TestMethod]
    public void TomcatProbeReadsHttpConnectorsAndIgnoresAjp()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            Directory.CreateDirectory(conf);
            File.WriteAllText(Path.Combine(conf, "server.xml"),
                "<Server><Service><Connector port=\"8080\" protocol=\"HTTP/1.1\" /><Connector port=\"8009\" protocol=\"AJP/1.3\" /></Service></Server>");

            CollectionAssert.AreEqual(new[] { 8080 }, TomcatRuntimeProbe.ReadHttpPorts(root).ToArray());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatProductRecoveryNeverTreatsConnectorZeroAsAPersistedPort()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var conf = Path.Combine(root, "conf");
            Directory.CreateDirectory(conf);
            var serverXml = Path.Combine(conf, "server.xml");
            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina0\"><Connector port=\"0\" protocol=\"HTTP/1.1\" /></Service></Server>");

            Assert.IsNull(
                ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml),
                "port=0 会让 Tomcat 随机选端口，不能作为 MCPanel 产品固定端口恢复。" );
            Assert.IsFalse(ProductDeploymentService.IsValidTomcatProductPort(0));
            Assert.IsTrue(ProductDeploymentService.IsValidTomcatProductPort(9000));
            Assert.IsTrue(ProductDeploymentService.IsValidTomcatProductPort(10000));
            Assert.IsTrue(
                TomcatProductInstanceManager.ProductStartupTimeout > TimeSpan.FromSeconds(80),
                "云电脑日志中的 Java 应用启动耗时约 77 秒，部署等待时间必须覆盖慢速首次建库。" );
            Assert.IsTrue(
                TomcatRuntimeProbe.DefaultStartupTimeout > TimeSpan.FromSeconds(80),
                "环境页启动全部 Tomcat 应用时也必须使用适合云电脑的等待时间。" );
            Assert.AreEqual(
                9000,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(9000, 9000, new[] { 9005 }),
                "旧生成实例不能覆盖主配置和持久化端口。" );
            Assert.AreEqual(
                9001,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, 9001, new[] { 9005 }),
                "主 server.xml 应优先修复缺失的持久化端口。" );
            Assert.AreEqual(
                9005,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, null, new[] { 0, 9005 }),
                "只有两层权威状态都无效时才能采用生成实例端口。" );
            Assert.AreEqual(
                0,
                ProductDeploymentService.SelectCanonicalTomcatRuntimePort(8080, 8080, new[] { 8080 }),
                "共享 Tomcat 的 8080 不能被误当成产品独立端口。" );

            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina\"><Connector port=\"8080\" protocol=\"HTTP/1.1\" /></Service></Server>");
            Assert.IsNull(
                ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml),
                "生成实例只能恢复产品专用端口池中的端口。" );

            File.WriteAllText(
                serverXml,
                "<Server><Service name=\"Catalina9000\"><Connector port=\"9000\" protocol=\"HTTP/1.1\" /></Service></Server>");
            Assert.AreEqual(9000, ProductDeploymentService.ReadFirstTomcatHttpPort(serverXml));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatProductLaunchBindsTheGeneratedCatalinaBaseWithoutSupplierBatchScripts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var tomcatHome = Path.Combine(root, "Tomcat Home");
            var instanceRoot = Path.Combine(root, "Runtime", "TomcatProductRuns", "JAVA-1");
            var bin = Path.Combine(tomcatHome, "bin");
            var javaBin = Path.Combine(tomcatHome, "jre", "jre", "bin");
            var conf = Path.Combine(instanceRoot, "conf");
            Directory.CreateDirectory(bin);
            Directory.CreateDirectory(javaBin);
            Directory.CreateDirectory(conf);
            Directory.CreateDirectory(Path.Combine(instanceRoot, "temp"));
            File.WriteAllText(Path.Combine(bin, "bootstrap.jar"), "test");
            File.WriteAllText(Path.Combine(bin, "tomcat-juli.jar"), "test");
            File.WriteAllText(Path.Combine(javaBin, "java.exe"), "test");
            File.WriteAllText(Path.Combine(conf, "logging.properties"), string.Empty);
            var jvmProperties = Path.Combine(conf, "jvm.properties");
            File.WriteAllText(
                jvmProperties,
                "-Xms256m -Xmx512m -XX:ErrorFile=\"D:/shared-tomcat/logs/hs_err_pid%p.log\"\nTest title");
            EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
                instanceRoot,
                useInstanceLocalErrorFile: true);

            var startInfo = TomcatProductInstanceManager.BuildTomcatJavaStartInfo(
                tomcatHome,
                instanceRoot,
                redirectOutput: true);

            Assert.AreEqual(Path.GetFullPath(Path.Combine(javaBin, "java.exe")), startInfo.FileName);
            Assert.IsFalse(startInfo.UseShellExecute);
            Assert.IsTrue(startInfo.RedirectStandardOutput);
            Assert.IsTrue(startInfo.RedirectStandardError);
            StringAssert.Contains(startInfo.Arguments, "-Dcatalina.base=");
            StringAssert.Contains(startInfo.Arguments, Path.GetFullPath(instanceRoot));
            StringAssert.Contains(startInfo.Arguments, "-Dcatalina.home=");
            StringAssert.Contains(startInfo.Arguments, "org.apache.catalina.startup.Bootstrap");
            var normalizedJvm = File.ReadAllText(jvmProperties);
            StringAssert.Contains(
                normalizedJvm,
                Path.Combine(instanceRoot, "logs", "hs_err_pid%p.log").Replace("\\", "/"));
            Assert.IsFalse(
                normalizedJvm.Contains("shared-tomcat", StringComparison.OrdinalIgnoreCase),
                "产品实例的 JVM 崩溃日志不能继续写入共享 Tomcat 目录。" );
            Assert.IsFalse(
                startInfo.Arguments.Contains("catalina.bat", StringComparison.OrdinalIgnoreCase),
                "供应商修改过的 catalina.bat 不能再覆盖产品实例的 CATALINA_BASE。" );
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatStopFallbackParsesListeningJavaProcessIdsFromNetstat()
    {
        const string netstat = """
            TCP    0.0.0.0:9000       0.0.0.0:0       LISTENING       18968
            TCP    [::]:9000          [::]:0          LISTENING       18968
            TCP    0.0.0.0:8080       0.0.0.0:0       ESTABLISHED     1234
            TCP    0.0.0.0:7000       0.0.0.0:0       LISTENING       2222
            """;

        CollectionAssert.AreEqual(
            new[] { 18968 },
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 9000 }).ToArray());
        CollectionAssert.AreEqual(
            new[] { 2222 },
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 7000 }).ToArray());
        Assert.AreEqual(
            0,
            TomcatProductInstanceManager.ParseNetstatListeningProcessIds(netstat, new[] { 8080 }).Count);
    }

    [TestMethod]
    public void TomcatProbeRequiresEveryConfiguredHttpPortToListen()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        var second = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            first.Start();
            second.Start();

            var ports = new[]
            {
                ((IPEndPoint)first.LocalEndpoint).Port,
                ((IPEndPoint)second.LocalEndpoint).Port
            };

            Assert.IsTrue(TomcatRuntimeProbe.ArePortsListening(ports));

            second.Stop();

            Assert.IsFalse(TomcatRuntimeProbe.ArePortsListening(ports));
        }
        finally
        {
            first.Stop();
            second.Stop();
        }
    }

    [TestMethod]
    public void CustomIisWebsiteInputAndSingleElevationScriptAreDeterministic()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var definition = new CustomWebsiteDefinition
            {
                Id = "site1",
                Name = "TrainingSite",
                PhysicalPath = Path.Combine(root, "site"),
                HttpPort = 8081,
                Domains = new System.Collections.Generic.List<string> { "training.example.test" },
                ManagedRuntimeVersion = "v4.0",
                MaxBandwidthKbps = 1024,
                MimeMappings = CustomWebsiteService.ParseMimeMappings(".webp=image/webp\n.json=application/json")
            };

            CustomWebsiteService.Validate(definition, null);
            const string certificatePassword = "DoNotPersist-Secret-123";
            var script = CustomWebsiteService.BuildConfigureScript(definition, null, certificatePassword, Path.Combine(root, "result"));

            Assert.AreEqual(1, Regex.Matches(script, "Import-Module WebAdministration").Count);
            StringAssert.Contains(script, "New-Website");
            StringAssert.Contains(script, "maxBandwidth -Value 1048576");
            StringAssert.Contains(script, "ProtectedData]::Unprotect");
            Assert.IsFalse(script.Contains(certificatePassword, StringComparison.Ordinal));
            Assert.AreEqual("image/webp", definition.MimeMappings[".webp"]);
            Assert.AreEqual("http://training.example.test:8081/", CustomWebsiteService.BuildUrl(definition));

            var configureScript = Path.Combine(root, "configure.ps1");
            var parserScript = Path.Combine(root, "parse.ps1");
            File.WriteAllText(configureScript, script, Encoding.UTF8);
            File.WriteAllText(parserScript,
                "param([string]$Target)\n$tokens=$null; $errors=$null\n" +
                "[System.Management.Automation.Language.Parser]::ParseFile($Target,[ref]$tokens,[ref]$errors) | Out-Null\n" +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }\nexit 0\n",
                Encoding.UTF8);
            using var parser = Process.Start(new ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{parserScript}\" -Target \"{configureScript}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            });
            Assert.IsNotNull(parser);
            parser!.WaitForExit();
            Assert.AreEqual(0, parser.ExitCode, parser.StandardError.ReadToEnd());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void InstalledProductRemainsAvailableForSupplierUpdate()
    {
        var product = new ProductItem("DS0105", "电子商务运营沙盘平台", "实训系统", string.Empty, ProductSource.Online)
        {
            IsInstalled = true
        };

        Assert.AreEqual("卸载", product.UninstallActionText);
        Assert.AreEqual("更新", product.UpdateActionText);
        Assert.IsTrue(product.CanProductAction);
        Assert.IsTrue(product.CanUpdate);

        product.IsBusy = true;
        Assert.AreEqual("卸载中", product.UninstallActionText);
        Assert.AreEqual("更新中", product.UpdateActionText);
        Assert.IsFalse(product.CanProductAction);
        Assert.IsFalse(product.CanUpdate);
    }

    [TestMethod]
    public void RelativePathForSameProductRootIsCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "content");

        Assert.AreEqual(".", PathCompat.GetRelativePath(root, root));
        Assert.AreEqual(".", PathCompat.GetRelativePath(root + Path.DirectorySeparatorChar, root));
    }

    [TestMethod]
    public void SupplierSvnInstallsDirectlyAndUpdatesInPlace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "PT0404");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllText(Path.Combine(seed, "WEB-INF", "version.txt"), "v1");

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "v1" }));
            }

            var transfer = new SvnProductTransferService();
            var reportedProgress = new System.Collections.Generic.List<double>();
            var reportedDetails = new System.Collections.Generic.List<ProductDownloadProgress>();
            var reportedStatus = new System.Collections.Generic.List<string>();
            var firstResult = transfer.SyncWithDetailsAsync(
                    repositoryUri,
                    installed,
                    update =>
                    {
                        reportedProgress.Add(update.Percent);
                        reportedDetails.Add(update);
                    },
                    reportedStatus.Add,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(installed, firstResult);
            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")));
            Assert.AreEqual("v1", File.ReadAllText(Path.Combine(installed, "WEB-INF", "version.txt")));
            Assert.IsTrue(reportedStatus.Exists(status => status.Contains("已扫描", StringComparison.Ordinal)), string.Join(" | ", reportedStatus));
            Assert.IsTrue(reportedDetails.Exists(update => update.ScannedFiles >= 1), "Checkout 必须实时报告已扫描文件。");
            Assert.AreEqual(100d, reportedProgress[reportedProgress.Count - 1]);
            for (var index = 1; index < reportedProgress.Count; index++)
            {
                Assert.IsTrue(reportedProgress[index] >= reportedProgress[index - 1], "下载进度必须单调递增。");
            }

            var editor = Path.Combine(root, "editor");
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.CheckOut(new SvnUriTarget(repositoryUri), editor));
                File.WriteAllText(Path.Combine(editor, "WEB-INF", "version.txt"), "v2");
                Assert.IsTrue(client.Commit(editor, new SvnCommitArgs { LogMessage = "v2" }));
            }

            var updateResult = transfer.SyncAsync(repositoryUri, installed, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(installed, updateResult);
            Assert.AreEqual("v2", File.ReadAllText(Path.Combine(installed, "WEB-INF", "version.txt")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void SupplierSvnResumesAnExistingIncompleteWorkingCopy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "RESUME1");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllText(Path.Combine(seed, "WEB-INF", "payload.bin"), "resume-payload");

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "seed" }));
                var partialCheckout = new SvnCheckOutArgs { Depth = SvnDepth.Empty };
                Assert.IsTrue(client.CheckOut(new SvnUriTarget(repositoryUri), installed, partialCheckout));
            }

            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")));
            Assert.IsFalse(File.Exists(Path.Combine(installed, "WEB-INF", "payload.bin")));

            var reportedStatus = new System.Collections.Generic.List<string>();
            var transfer = new SvnProductTransferService();
            transfer.SyncAsync(repositoryUri, installed, null, reportedStatus.Add, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.AreEqual("resume-payload", File.ReadAllText(Path.Combine(installed, "WEB-INF", "payload.bin")));
            Assert.IsTrue(reportedStatus.Exists(status => status.Contains("上次进度", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancelledSupplierSvnCheckoutKeepsRecoverableProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "RESUME2");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllBytes(Path.Combine(seed, "WEB-INF", "large-payload.bin"), new byte[8 * 1024 * 1024]);

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "seed" }));
            }

            using var cancellation = new CancellationTokenSource();
            var cancellationTriggered = false;
            var transfer = new SvnProductTransferService();
            Assert.ThrowsException<OperationCanceledException>(() =>
                transfer.SyncAsync(
                        repositoryUri,
                        installed,
                        value =>
                        {
                            if (value >= 1 &&
                                Directory.Exists(Path.Combine(installed, ".svn")) &&
                                !cancellationTriggered)
                            {
                                cancellationTriggered = true;
                                cancellation.Cancel();
                            }
                        },
                        null,
                        cancellation.Token)
                    .GetAwaiter().GetResult());

            Assert.IsTrue(cancellationTriggered, "测试必须在 Checkout 传输期间触发取消。");
            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")), "取消后必须保留可恢复的 SVN 元数据。");

            transfer.SyncAsync(repositoryUri, installed, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(
                8L * 1024 * 1024,
                new FileInfo(Path.Combine(installed, "WEB-INF", "large-payload.bin")).Length);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ProductInstallOrderPersistsAndReinstallMovesToEnd()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var storeFile = Path.Combine(root, "install-order.json");
            var firstInstall = new DateTime(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc);
            var secondInstall = firstInstall.AddDays(1);
            var store = new ProductInstallOrderStore(storeFile);

            store.EnsureInstalled([
                new ProductInstallOrderCandidate("SECOND", secondInstall),
                new ProductInstallOrderCandidate("FIRST", firstInstall)
            ]);

            var firstSequence = store.GetSequence("FIRST");
            var secondSequence = store.GetSequence("SECOND");
            Assert.IsTrue(firstSequence < secondSequence);

            store.RecordInstalled("FIRST", secondInstall.AddHours(1));
            Assert.AreEqual(firstSequence, store.GetSequence("FIRST"), "更新已安装产品不能改变首次安装顺序。");

            var reloaded = new ProductInstallOrderStore(storeFile);
            Assert.AreEqual(firstSequence, reloaded.GetSequence("FIRST"));
            Assert.AreEqual(secondSequence, reloaded.GetSequence("SECOND"));

            reloaded.Remove("FIRST");
            reloaded.RecordInstalled("FIRST", secondInstall.AddDays(1));
            Assert.IsTrue(reloaded.GetSequence("FIRST") > secondSequence, "卸载后重新安装应排到已安装队列末尾。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateArchiveRejectsPathTraversal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archiveFile = Path.Combine(root, "malicious.zip");
            using (var archive = ZipFile.Open(archiveFile, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("blocked");
            }

            var destination = Path.Combine(root, "payload");
            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.ExtractArchiveSafely(archiveFile, destination, null, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void LocalUpdatePreparationVerifiesSidecarAndRetainsSourcePackage()
    {
        var root = CreateTemporaryDirectory();
        string? stagingRoot = null;
        try
        {
            var package = Path.Combine(root, "MCPanel-local.zip");
            var executable = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
            var config = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config");
            Assert.IsTrue(File.Exists(executable), "测试发布目录必须包含 MCPanel.exe。");
            Assert.IsTrue(File.Exists(config), "测试发布目录必须包含 MCPanel.exe.config。");

            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(executable, "MCPanel.exe");
                archive.CreateEntryFromFile(config, "MCPanel.exe.config");
            }

            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(package))
            {
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }

            File.WriteAllText(package + ".sha256", $"{hash}  {Path.GetFileName(package)}");
            var service = new ApplicationUpdateService();
            var prepared = service.PrepareLocalAsync(
                    package,
                    progress: null,
                    status: null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            stagingRoot = Directory.GetParent(prepared.PayloadDirectory)?.FullName;

            Assert.AreEqual(Path.GetFullPath(package), prepared.PackageFile);
            Assert.AreEqual(ApplicationUpdateService.CurrentVersion, new Version(prepared.Version));
            Assert.IsFalse(
                prepared.DeletePackageFileAfterApply,
                "本地更新包是用户选择的源文件，更新完成后不能被更新器删除。");
            Assert.IsTrue(File.Exists(package));

            service.DiscardPreparedUpdate(prepared);
            Assert.IsFalse(
                !string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot),
                "用户取消本地更新确认后，暂存目录应被清理。");
        }
        finally
        {
            if (stagingRoot is string resolvedStagingRoot)
            {
                DeleteTemporaryTree(resolvedStagingRoot);
            }

            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task RemoteUpdateReleasesDownloadFileAndReusesVerifiedPackage()
    {
        var sourceRoot = CreateTemporaryDirectory();
        var service = new ApplicationUpdateService();
        const string version = "999.0.0.1";
        var packageFile = Path.Combine(service.UpdatesRoot, "Downloads", $"MCPanel-{version}.zip");
        var temporaryFile = packageFile + ".part";
        PreparedApplicationUpdate? first = null;
        PreparedApplicationUpdate? second = null;
        try
        {
            if (File.Exists(packageFile)) File.Delete(packageFile);
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);

            var packageSource = Path.Combine(sourceRoot, "MCPanel-source.zip");
            var executable = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
            var config = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config");
            Assert.IsTrue(File.Exists(executable), "测试发布目录必须包含 MCPanel.exe。");
            Assert.IsTrue(File.Exists(config), "测试发布目录必须包含 MCPanel.exe.config。");

            using (var archive = ZipFile.Open(packageSource, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(executable, "MCPanel.exe");
                archive.CreateEntryFromFile(config, "MCPanel.exe.config");
            }

            var packageBytes = File.ReadAllBytes(packageSource);
            string hash;
            using (var sha = SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(packageBytes)).Replace("-", string.Empty).ToLowerInvariant();
            }

            var manifest = new OnlineUpdateManifest
            {
                Version = version,
                PackageUrl = "https://updates.example.test/MCPanel.zip",
                Sha256 = hash
            };
            var downloadCount = 0;
            Task<HttpResponseMessage> Download(CancellationToken _)
            {
                downloadCount++;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                });
            }

            first = await service.PrepareRemotePackageAsync(
                manifest,
                Download,
                response => response.EnsureSuccessStatusCode(),
                progress: null,
                status: null,
                CancellationToken.None);
            service.DiscardPreparedUpdate(first);

            Assert.IsTrue(File.Exists(packageFile), "下载完成后应保留可复用的正式更新包。");
            Assert.IsFalse(File.Exists(temporaryFile), "正式化完成后不应残留被占用的 .part 文件。");

            second = await service.PrepareRemotePackageAsync(
                manifest,
                Download,
                response => response.EnsureSuccessStatusCode(),
                progress: null,
                status: null,
                CancellationToken.None);
            Assert.AreEqual(1, downloadCount, "SHA-256 一致的已下载更新包应直接复用，不应重新下载。");
        }
        finally
        {
            service.DiscardPreparedUpdate(first);
            service.DiscardPreparedUpdate(second);
            if (File.Exists(packageFile)) File.Delete(packageFile);
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            DeleteTemporaryTree(sourceRoot);
        }
    }

    [TestMethod]
    public void UpdateTransactionReplacesProgramAndPreservesOperationalData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(install, "MCPanel.exe.config"),
                "<configuration><appSettings><add key=\"UpdateManifestUrl\" value=\"https://updates.example.com/manifest.json\" /><add key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel-override\" /><add key=\"Environment.Frp.PackageUrl\" value=\"https://mirror.example.com/frp.zip\" /><add key=\"Ai.Provider\" value=\"OpenAI\" /><add key=\"Ai.Endpoint\" value=\"https://mirror.example.com/v1/chat/completions\" /><add key=\"Ai.Model\" value=\"ops-model\" /><add key=\"Ai.ApiKey\" value=\"old-ai-key\" /></appSettings></configuration>");
            File.WriteAllText(Path.Combine(install, "obsolete.dll"), "obsolete");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"),
                "<configuration><appSettings><add key=\"UpdateManifestUrl\" value=\"\" /><add key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel\" /><add key=\"Environment.Frp.PackageUrl\" value=\"https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip\" /><add key=\"Ai.Provider\" value=\"GLM\" /><add key=\"Ai.Endpoint\" value=\"https://open.bigmodel.cn/api/paas/v4/chat/completions\" /><add key=\"Ai.Model\" value=\"glm-5.2\" /><add key=\"Ai.ApiKey\" value=\"new-ai-key\" /></appSettings></configuration>");
            File.WriteAllText(Path.Combine(payload, "current.dll"), "current");

            foreach (var name in new[] { "StoreData", "AccountApi", "Runtime", "Downloads", "Tools", "web", "Cache", "Frp", "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite" })
            {
                var directory = Path.Combine(install, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "preserved.txt"), name);
            }

            ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
            {
                InstallDirectory = install,
                PayloadDirectory = payload,
                StagingRoot = stagingRoot,
                MainExecutableName = "MCPanel.exe",
                TargetVersion = "1.0.1"
            });

            Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(install, "current.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, "obsolete.dll")));
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://updates.example.com/manifest.json");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel-override\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://mirror.example.com/frp.zip");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"Ai.Provider\" value=\"OpenAI\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://mirror.example.com/v1/chat/completions");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"Ai.Model\" value=\"ops-model\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "old-ai-key");
            foreach (var name in new[] { "StoreData", "AccountApi", "Runtime", "Downloads", "Tools", "web", "Cache", "Frp", "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite" })
            {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(install, name, "preserved.txt")));
            }

            Assert.IsFalse(
                Directory.Exists(Path.Combine(install, "StoreData", "Updates", "Rollback", "Current")),
                "成功更新后不应在安装目录中保留旧版 MCPanel 回滚副本。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateTransactionRejectsPayloadOutsideControlledInstallStaging()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var payload = Path.Combine(root, "external-payload");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"), "<configuration />");

            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    StagingRoot = stagingRoot,
                    MainExecutableName = "MCPanel.exe",
                    TargetVersion = "1.0.1"
                }));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateBackupFailureLeavesOriginalProgramUntouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(install, "MCPanel.exe.config"), "<configuration />");
            var lockedFile = Path.Combine(install, "locked.dll");
            File.WriteAllText(lockedFile, "do-not-delete");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"), "<configuration />");

            using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsException<IOException>(() =>
                    ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
                    {
                        InstallDirectory = install,
                        PayloadDirectory = payload,
                        StagingRoot = stagingRoot,
                        MainExecutableName = "MCPanel.exe",
                        TargetVersion = "1.0.1"
                    }));
            }

            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
            Assert.AreEqual("do-not-delete", File.ReadAllText(lockedFile));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        if (root is T matchingChild)
        {
            yield return matchingChild;
        }

        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            foreach (var child in FindVisualChildren<T>(VisualTreeHelper.GetChild(root, index)))
            {
                yield return child;
            }
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }
}
