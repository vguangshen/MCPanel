using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MCPanel;

public partial class MainWindow
{
private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) =>
        _model.ApplyProductFilter(SearchBox.Text);

    internal void ProductCategory_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProductCategoryFilter category)
        {
            _model.SelectProductCategory(category.Key);
        }
    }

    internal void ProductInstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ProductItem product)
        {
            return;
        }

        if (product.IsBusy ||
            _productInstallQueue.HasActiveOrQueued(product.ProductId) ||
            _productUninstallInProgress)
        {
            return;
        }

        var preflight = ProductEnvironmentPreflight.Check(product, _runtimeService);
        if (!preflight.CanInstall)
        {
            product.DownloadProgress = 0;
            product.StatusText = preflight.BuildMessage();
            MessageBox.Show(product.StatusText, "产品管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var item = _productInstallQueue.Enqueue(product, product.IsInstalled);
            product.SetQueueState(item.State, item.QueuePosition);
            product.IsBusy = false;
            product.StatusText = $"已加入安装队列，等待第 {item.QueuePosition} 项处理。";
        }
        catch (Exception ex)
        {
            product.SetQueueState(null, 0);
            product.IsBusy = false;
            product.DownloadProgress = 0;
            product.StatusText = $"加入安装队列失败：{ex.GetBaseException().Message}";
            _model.InstallationProgress.Hide();
            if (!_isClosed)
            {
                MessageBox.Show(product.StatusText, "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void ProductInstallQueue_ItemChanged(ProductInstallQueueItemViewModel item)
    {
        var product = FindProduct(item.ProductId);
        if (product is null)
        {
            return;
        }

        if (item.IsTerminal)
        {
            product.SetQueueState(null, 0);
            product.IsBusy = false;
            return;
        }

        product.SetQueueState(item.State, item.QueuePosition);
        product.IsBusy = item.State == ProductInstallQueueStatus.Running;
        if (item.State == ProductInstallQueueStatus.Pending)
        {
            product.DownloadProgress = 0;
            product.StatusText = $"等待安装队列第 {item.QueuePosition} 项处理。";
        }
        else if (item.State == ProductInstallQueueStatus.Running)
        {
            product.StatusText = item.Message;
        }
    }

    private void ProductInstallQueue_ProgressChanged(
        ProductInstallQueueItemViewModel item,
        ProductInstallWorkerProgress progress)
    {
        var product = FindProduct(item.ProductId);
        if (product is null)
        {
            return;
        }

        product.SetQueueState(ProductInstallQueueStatus.Running, item.QueuePosition);
        product.IsBusy = true;
        product.DownloadProgress = progress.Percent;
        product.StatusText = progress.Message;
    }

    private void ProductInstallQueue_ItemFinished(
        ProductInstallQueueItemViewModel item,
        ProductInstallWorkerProgress progress)
    {
        var product = FindProduct(item.ProductId);
        if (product is not null)
        {
            product.SetQueueState(null, 0);
            product.IsBusy = false;
            if (item.State == ProductInstallQueueStatus.Completed)
            {
                product.IsInstalled = true;
                product.DownloadProgress = 100;
                product.StatusText = progress.ResultMessage ?? progress.Message;
                _model.RecordProductInstalled(product.ProductId);
                _model.RefreshInstalledProducts();
            }
            else
            {
                product.DownloadProgress = 0;
                product.StatusText = item.State == ProductInstallQueueStatus.Cancelled
                    ? "产品安装或更新已取消；现有产品缓存会保留，重试时将重新校验并下载缺失文件。"
                    : progress.Message;
            }
        }

    }

    private ProductItem? FindProduct(string productId) =>
        _model.Products.FirstOrDefault(product =>
            string.Equals(product.ProductId, productId, StringComparison.OrdinalIgnoreCase));

    private void DownloadQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (DownloadQueuePopup.IsOpen)
        {
            DownloadQueuePopup.IsOpen = false;
            return;
        }

        ProductsHeader.UpdateLayout();
        UpdateDownloadQueuePopupWidth();
        UpdateDownloadQueuePopupTransformOrigin();
        DownloadQueuePopup.IsOpen = true;
        RequestDownloadQueuePopupPlacementRefresh();
    }

    private void DownloadQueueButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        // Treat a physical double-click as one toggle. This avoids WPF raising
        // two Click events (open, then immediately close), which is especially
        // easy to perceive as "no response" over a remote desktop connection.
        if (e.ClickCount > 1)
        {
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (DownloadQueuePopup.IsOpen && !DownloadQueueButton.IsMouseOver)
        {
            DownloadQueuePopup.IsOpen = false;
        }
    }

    private void RefreshDownloadQueuePopupPlacement()
    {
        if (!DownloadQueuePopup.IsOpen)
        {
            return;
        }

        ProductsHeader.UpdateLayout();
        UpdateDownloadQueuePopupWidth();
        UpdateDownloadQueuePopupTransformOrigin();

        // Popup has no public reposition method. A tiny offset round trip
        // invalidates the initial placement after the first layout pass.
        var horizontalOffset = DownloadQueuePopup.HorizontalOffset;
        DownloadQueuePopup.HorizontalOffset = horizontalOffset + 0.01d;
        DownloadQueuePopup.HorizontalOffset = horizontalOffset;
    }

    private void RequestDownloadQueuePopupPlacementRefresh()
    {
        if (_isClosed || !DownloadQueuePopup.IsOpen || _downloadQueuePopupPlacementRefreshPending)
        {
            return;
        }

        _downloadQueuePopupPlacementRefreshPending = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _downloadQueuePopupPlacementRefreshPending = false;
                RefreshDownloadQueuePopupPlacement();
            }),
            DispatcherPriority.Render);
    }

    private void ProductsHeader_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (DownloadQueuePopup.IsOpen)
        {
            UpdateDownloadQueuePopupWidth();
            UpdateDownloadQueuePopupTransformOrigin();
            RequestDownloadQueuePopupPlacementRefresh();
        }
    }

    private void UpdateDownloadQueuePopupWidth()
    {
        const double maximumWidth = 460d;
        const double minimumWidth = 340d;

        if (ProductsHeader.ActualWidth <= 0 || DownloadQueueButton.ActualWidth <= 0)
        {
            return;
        }

        try
        {
            var availableWidth = Math.Max(0, ProductsHeader.ActualWidth);
            var width = Math.Min(maximumWidth, availableWidth);
            if (width < minimumWidth && availableWidth >= minimumWidth)
            {
                width = minimumWidth;
            }

            if (availableWidth > 0)
            {
                width = Math.Min(width, availableWidth);
                if (Math.Abs(DownloadQueuePopupHost.Width - width) > 0.5)
                {
                    DownloadQueuePopupHost.Width = width;
                }
                if (Math.Abs(DownloadQueuePopupCard.Width - width) > 0.5)
                {
                    DownloadQueuePopupCard.Width = width;
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The popup can be opened while WPF is still connecting the visual tree.
        }
    }

    private void UpdateDownloadQueuePopupTransformOrigin()
    {
        if (ProductsHeader.ActualWidth <= 0 ||
            DownloadQueueButton.ActualWidth <= 0 ||
            DownloadQueuePopupCard.Width <= 0)
        {
            return;
        }

        try
        {
            var buttonLeftInHeader = DownloadQueueButton
                .TranslatePoint(new Point(0, 0), ProductsHeader)
                .X;
            var buttonCenterInHeader = buttonLeftInHeader + DownloadQueueButton.ActualWidth / 2d;
            var (scaleX, _) = GetProductsHeaderScale();
            var popupLeftInHeader = Math.Max(
                0d,
                ProductsHeader.ActualWidth - DownloadQueuePopupCard.Width / scaleX);
            var originX = (buttonCenterInHeader - popupLeftInHeader) * scaleX /
                          DownloadQueuePopupCard.Width;
            DownloadQueuePopupCard.RenderTransformOrigin = new Point(
                Math.Min(1d, Math.Max(0d, originX)),
                0d);
        }
        catch (InvalidOperationException)
        {
            // The visual tree may not be connected during the first layout pass.
        }
    }

    private void DownloadQueuePopup_Opened(object? sender, EventArgs e)
    {
        UpdateDownloadQueuePopupWidth();
        UpdateDownloadQueuePopupTransformOrigin();
        if (DownloadQueuePopupCard.RenderTransform is not ScaleTransform scale)
        {
            return;
        }

        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        scale.ScaleX = 0.92;
        scale.ScaleY = 0.92;

        var duration = TimeSpan.FromMilliseconds(180);
        scale.BeginAnimation(
            ScaleTransform.ScaleXProperty,
            new DoubleAnimation(0.92, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
        scale.BeginAnimation(
            ScaleTransform.ScaleYProperty,
            new DoubleAnimation(0.92, 1, duration)
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
                FillBehavior = FillBehavior.HoldEnd
            });
    }

    private void HideInstallationProgress_Click(object sender, RoutedEventArgs e) =>
        _model.InstallationProgress.Hide();

    internal void ToggleQueuedProductPause_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is ProductInstallQueueItemViewModel item)
        {
            try
            {
                _productInstallQueue.TogglePause(item.QueueId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "下载队列", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    internal void RemoveQueuedProduct_Click(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (((FrameworkElement)sender).DataContext is ProductInstallQueueItemViewModel item)
        {
            try
            {
                _productInstallQueue.Remove(item.QueueId);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "下载队列", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    internal async void ProductUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is ProductItem product)
        {
            await UninstallProductAsync(product, product.DisplayName);
        }
    }

    private async Task UninstallProductAsync(ProductItem product, string displayName)
    {
        if (!product.IsInstalled ||
            product.IsBusy ||
            _productInstallQueue.HasActiveOrQueued(product.ProductId) ||
            _productUninstallInProgress)
        {
            return;
        }

        if (MessageBox.Show(
                $"确定卸载“{displayName}”吗？\n将移除 IIS/Tomcat/Nginx 绑定、端口、安装记录、SVN 工作副本和下载缓存，不保留该产品目录。",
                "卸载产品",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _productUninstallInProgress = true;
        var cancellation = new CancellationTokenSource();
        _productUninstallCancellation = cancellation;
        try
        {
            product.IsBusy = true;
            _model.InstallationProgress.BeginUninstall(product);
            product.StatusText = "正在卸载产品...";
            var uninstallProgress = new Progress<ProductUninstallProgress>(update =>
            {
                product.DownloadProgress = Compat.Clamp(update.Percent, 0, 100);
                product.StatusText = update.Status;
                _model.InstallationProgress.ReportUninstallProgress(update);
            });
            await _deploymentService.UninstallAsync(product, cancellation.Token, uninstallProgress);
            var uninstallDetail = _model.InstallationProgress.DetailText;
            var hasRouteWarning = uninstallDetail.Contains("Nginx", StringComparison.OrdinalIgnoreCase) &&
                                  uninstallDetail.Contains("未自动同步", StringComparison.OrdinalIgnoreCase);
            product.IsInstalled = false;
            product.DownloadProgress = 100;
            product.StatusText = "产品已卸载，可重新安装。";
            _model.RecordProductUninstalled(product.ProductId);
            _model.RefreshInstalledProducts();
            _model.InstallationProgress.Complete();
            await Task.Delay(350);
            _model.InstallationProgress.Hide();
            if (!_isClosed)
            {
                MessageBox.Show(
                    hasRouteWarning
                        ? $"{product.Name} 已卸载完成，但 {uninstallDetail}"
                        : $"{product.Name} 卸载完成。",
                    "产品管理",
                    MessageBoxButton.OK,
                    hasRouteWarning ? MessageBoxImage.Warning : MessageBoxImage.Information);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            product.StatusText = "卸载已取消；已保留产品文件，请刷新状态后重试。";
            _model.InstallationProgress.ReportStatus(product.StatusText);
            if (!_isClosed)
            {
                MessageBox.Show(product.StatusText, "产品管理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            var logPath = EnvironmentOperationDiagnostics.RecordFailure("产品管理", $"卸载 {product.ProductId}", ex);
            var attachedLog = EnvironmentOperationDiagnostics.FindAttachedLogPath(ex);
            var effectiveLog = attachedLog ?? logPath;
            product.StatusText = string.IsNullOrWhiteSpace(effectiveLog)
                ? $"产品卸载失败：{ex.GetBaseException().Message}"
                : $"产品卸载失败：{ex.GetBaseException().Message}。详细日志：{effectiveLog}";
            _model.InstallationProgress.ReportStatus(product.StatusText);
            _model.InstallationProgress.Hide();
            if (!_isClosed)
            {
                MessageBox.Show(product.StatusText, "产品管理", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            product.IsBusy = false;
            _model.InstallationProgress.Hide();
            _productUninstallInProgress = false;
            if (ReferenceEquals(_productUninstallCancellation, cancellation))
            {
                _productUninstallCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    internal void InstalledProductOpen_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item && Directory.Exists(item.InstallPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.InstallPath}\"") { UseShellExecute = true });
        }
    }

    private void InstalledProductBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item && item.CanBrowse)
        {
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
    }

    internal void InstalledProductUrl_Click(object sender, MouseButtonEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item && item.CanBrowse)
        {
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
    }

    internal void InstalledProductManageToggle_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item)
        {
            item.IsManagementExpanded = !item.IsManagementExpanded;
        }
    }

    internal async void InstalledProductTomcatAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not InstalledProductItem item ||
            sender is not Button button ||
            button.Tag is not string action ||
            !item.IsTomcatDeployment)
        {
            return;
        }

        try
        {
            TomcatProductInstanceManager.WriteOperationLog(item.ProductId, $"界面按钮：{action}。");

            if (action == "OpenLogs")
            {
                var logDirectory = _tomcatInstanceManager.GetLogDirectory(item.ProductId);
                Directory.CreateDirectory(logDirectory);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{logDirectory}\"") { UseShellExecute = true });
                item.RefreshRuntime();
                return;
            }

            item.SetOperationState(action switch
            {
                "Start" => "正在独立启动",
                "Catalina" => "正在以 Catalina 方式启动",
                "Stop" => "正在停止",
                "ClearCacheRestart" => "正在清理缓存并重启",
                _ => "正在处理"
            });

            var message = action switch
            {
                "Start" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: false),
                "Catalina" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: true),
                "Stop" => await _tomcatInstanceManager.StopAsync(item.ProductId),
                "ClearCacheRestart" => await _tomcatInstanceManager.ClearCacheAndRestartAsync(item.ProductId),
                _ => throw new NotSupportedException("未知 Tomcat 应用操作。")
            };

            if (action == "Catalina")
            {
                await Task.Delay(1200);
            }

            item.RefreshRuntime();
            MessageBox.Show(message, "Tomcat 应用", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            TomcatProductInstanceManager.WriteOperationLog(item.ProductId, $"界面操作失败：{ex.GetType().Name}: {ex.Message}");
            item.SetOperationState("操作失败");
            MessageBox.Show($"Tomcat 应用操作失败：{ex.Message}", "网站", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void InstalledProductIis_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item && item.IsTomcatDeployment)
        {
            var deploymentPath = item.GetTomcatDeploymentPath();
            if (!string.IsNullOrWhiteSpace(deploymentPath))
            {
                var directory = Directory.Exists(deploymentPath) ? deploymentPath : Path.GetDirectoryName(deploymentPath);
                if (!string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{directory}\"") { UseShellExecute = true });
                }
            }

            return;
        }

        var inetMgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "inetsrv", "InetMgr.exe");
        if (!File.Exists(inetMgr))
        {
            MessageBox.Show("未找到 IIS 管理器，请先安装 IIS 管理控制台。", "网站", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo(inetMgr) { UseShellExecute = true });
    }

    internal async void InstalledProductRepair_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not InstalledProductItem item)
        {
            return;
        }

        try
        {
            item.SetOperationState(item.IsTomcatDeployment ? "正在修复 Tomcat 独立端口绑定..." : "正在修复 IIS 绑定...");
            var result = item.IsTomcatDeployment
                ? await _deploymentService.RepairTomcatBindingAsync(item.Product, item.InstallPath)
                : await _deploymentService.RepairIisBindingAsync(item.Product, item.InstallPath);
            item.RefreshRuntime();
            MessageBox.Show(result.Message, "网站", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            item.SetOperationState("绑定失败");
            MessageBox.Show($"修复{(item.IsTomcatDeployment ? " Tomcat" : " IIS")}绑定失败：{ex.Message}", "网站", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal async void InstalledProductDomain_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not InstalledProductItem item)
        {
            return;
        }

        if (!_productWebsiteService.IsNginxInstalled())
        {
            MessageBox.Show(
                "配置产品域名和 SSL 前，请先进入左侧“环境”页面安装 Nginx。",
                "网站", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var dialog = new ProductWebsiteDialog(_productWebsiteService.Load(item.ProductId))
            {
                Owner = this
            };
            dialog.ApplyTheme(_isDarkThemeActive);
            if (dialog.ShowDialog() != true || dialog.ResultSettings is null)
            {
                return;
            }

            item.SetOperationState("正在保存域名和 SSL 配置...");
            var message = await _productWebsiteService.SaveAsync(item.ProductId, dialog.ResultSettings);
            item.RefreshRuntime();
            MessageBox.Show(message, "网站", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            item.SetOperationState("域名配置失败");
            MessageBox.Show($"域名和 SSL 配置失败：{ex.Message}", "网站", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void CustomWebsiteCreate_Click(object sender, RoutedEventArgs e)
    {
        if (!_runtimeService.GetState(EnvironmentKind.Iis).IsInstalled)
        {
            MessageBox.Show(
                "创建网站前，请先进入左侧“环境”页面安装 Web Server / IIS。",
                "网站", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await ShowCustomWebsiteDialogAsync(null);
    }

    internal async void CustomWebsiteEdit_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CustomWebsiteItem item)
        {
            await ShowCustomWebsiteDialogAsync(item.Definition);
        }
    }

    private async Task ShowCustomWebsiteDialogAsync(CustomWebsiteDefinition? definition)
    {
        var dialog = new CustomWebsiteDialog(definition) { Owner = this };
        dialog.ApplyTheme(_isDarkThemeActive);
        if (dialog.ShowDialog() != true || dialog.ResultDefinition is null)
        {
            return;
        }

        try
        {
            var message = await _customWebsiteService.SaveAsync(
                dialog.ResultDefinition,
                dialog.CertificatePath,
                dialog.CertificatePassword);
            _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
            MessageBox.Show(message, "网站", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"IIS 网站保存失败：{ex.Message}", "网站", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal void CustomWebsiteBrowse_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CustomWebsiteItem item)
        {
            Process.Start(new ProcessStartInfo(item.Url) { UseShellExecute = true });
        }
    }

    internal void CustomWebsiteOpenRoot_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is CustomWebsiteItem item && Directory.Exists(item.PhysicalPath))
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.PhysicalPath}\"") { UseShellExecute = true });
        }
    }

    internal async void CustomWebsiteDelete_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not CustomWebsiteItem item ||
            MessageBox.Show(
                $"确定从 IIS 移除网站“{item.Name}”吗？\n网站根目录和所有业务文件都会保留。",
                "删除网站", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            var message = await _customWebsiteService.DeleteAsync(item.Definition);
            _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
            MessageBox.Show(message, "网站", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"删除 IIS 网站失败：{ex.Message}", "网站", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    internal async void InstalledProductUninstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is InstalledProductItem item)
        {
            await UninstallProductAsync(item.Product, item.DisplayName);
        }
    }

    private async void RefreshProducts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _model.ProductStoreStatus = "正在连接 regservice.itmc.cn 获取产品列表...";
            var products = await _storeClient.GetProductsAsync();
            if (products.Count == 0)
            {
                _model.ProductStoreStatus = "在线接口已响应，但当前账号没有返回可下载产品，已继续使用旧包内置产品清单。";
                return;
            }

            await _model.ReplaceProductsAsync(products);
            _productInstallQueue.SyncActiveItems();
        }
        catch (Exception ex)
        {
            _model.ProductStoreStatus = $"在线刷新失败：{ex.Message}。当前显示旧包内置产品清单。";
        }
    }
}
