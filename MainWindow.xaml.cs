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

public partial class MainWindow : Window
{
    private const int DwmWindowCornerPreferenceAttribute = 33;
    private const int DwmCornerPreferenceDoNotRound = 1;
    private const int DwmCornerPreferenceRound = 2;
    [DllImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int attribute,
        ref int value,
        int valueSize);

    private readonly MainViewModel _model = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly McPanelStoreClient _storeClient = new();
    private readonly EnvironmentInstaller _environmentInstaller = new();
    private readonly EnvironmentRuntimeService _runtimeService = new();
    private readonly ProductWebsiteService _productWebsiteService;
    private readonly CustomWebsiteService _customWebsiteService = new();
    private readonly FrpManager _frpManager = new();
    private readonly ProductDeploymentService _deploymentService = new();
    private readonly ProductInstallQueueService _productInstallQueue;
    private readonly TomcatProductInstanceManager _tomcatInstanceManager = new();
    private readonly PanelSettingsService _settingsService = new();
    private readonly ApplicationUpdateService _applicationUpdateService = new();
    private bool _isDarkThemeActive;
    private bool _monitoringStarted;
    private bool _runtimeRefreshInFlight;
    private int _runtimeRefreshTick;
    private int _runtimeRefreshGeneration;
    private bool _isClosed;
    private CancellationTokenSource? _cleanupUsageRefreshCancellation;
    private bool _productUninstallInProgress;
    private CancellationTokenSource? _productUninstallCancellation;
    private CancellationTokenSource? _applicationUpdateCancellation;
    private CancellationTokenSource? _databaseToolCancellation;
    private bool _downloadQueuePopupPlacementRefreshPending;

    internal bool IsDarkThemeActive => _isDarkThemeActive;

    public MainWindow()
    {
        InitializeComponent();
        DownloadQueuePopup.CustomPopupPlacementCallback = PlaceDownloadQueuePopup;
        _productWebsiteService = new ProductWebsiteService(_runtimeService);
        _productInstallQueue = new ProductInstallQueueService(_model.InstallationProgress);
        _productInstallQueue.ItemChanged += ProductInstallQueue_ItemChanged;
        _productInstallQueue.ProgressChanged += ProductInstallQueue_ProgressChanged;
        _productInstallQueue.ItemFinished += ProductInstallQueue_ItemFinished;
        _model.ThemeMode = _settingsService.GetThemeMode();
        ApplySelectedTheme();
        SystemEvents.UserPreferenceChanged += SystemEvents_UserPreferenceChanged;
        SystemEvents.DisplaySettingsChanged += SystemEvents_DisplaySettingsChanged;
        SourceInitialized += (_, _) =>
        {
            FitWindowToMonitor();
            ApplyWindowCornerPreference();
        };
        StateChanged += (_, _) =>
        {
            UpdateWindowStateChrome();
            RequestDownloadQueuePopupPlacementRefresh();
        };
        SizeChanged += (_, _) => RequestDownloadQueuePopupPlacementRefresh();
        LocationChanged += (_, _) => RequestDownloadQueuePopupPlacementRefresh();
        Closing += MainWindow_Closing;
        ContentRendered += (_, _) =>
        {
            _applicationUpdateService.CleanupStaleUpdaterFiles();
            HomeNavButton.IsChecked = true;
            Dispatcher.BeginInvoke(
                new Action(FocusActiveNavigationItem),
                DispatcherPriority.ApplicationIdle);
        };
        Activated += (_, _) => Dispatcher.BeginInvoke(
            new Action(FocusActiveNavigationItem),
            DispatcherPriority.ApplicationIdle);
        UpdateWindowStateChrome();
        Closed += (_, _) =>
        {
            _isClosed = true;
            SystemEvents.UserPreferenceChanged -= SystemEvents_UserPreferenceChanged;
            SystemEvents.DisplaySettingsChanged -= SystemEvents_DisplaySettingsChanged;
            _timer.Stop();
            DownloadQueuePopup.IsOpen = false;
            _cleanupUsageRefreshCancellation?.Cancel();
            _cleanupUsageRefreshCancellation?.Dispose();
            _productInstallQueue.Dispose();
            _productUninstallCancellation?.Cancel();
            _productUninstallCancellation?.Dispose();
            _applicationUpdateCancellation?.Cancel();
            _applicationUpdateCancellation?.Dispose();
            _databaseToolCancellation?.Cancel();
            _databaseToolCancellation?.Dispose();
            AccountApiPageControl.Dispose();
            AiAnalysisPageControl.Dispose();
            _model.Dispose();
            _frpManager.Dispose();
            _storeClient.Dispose();
            _environmentInstaller.Dispose();
        };
        DataContext = _model;
        _timer.Tick += async (_, _) =>
        {
            _model.TickSystemState();
            _productInstallQueue.Tick();
            _model.InstallationProgress.Tick();

            if (!_monitoringStarted || _runtimeRefreshInFlight || ++_runtimeRefreshTick % 2 != 0)
            {
                return;
            }

            _runtimeRefreshInFlight = true;
            try
            {
                await RefreshEnvironmentStatesAsync(preserveBusy: true);
            }
            catch (Exception ex)
            {
                WriteRuntimeRefreshError(ex);
            }
            finally
            {
                _runtimeRefreshInFlight = false;
            }
        };
        _model.RefreshSystemState();
        _deploymentService.PruneStaleDeploymentState();
        TomcatProductStartupManager.RefreshRegistration();
        _model.RefreshInstalledProducts();
        _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
        _model.StoreDataRoot = _settingsService.StoreDataRoot;
        _model.IsStartupEnabled = _settingsService.IsStartupEnabled();
        RefreshDatabaseToolState();
        var updateResult = _applicationUpdateService.ConsumeLastUpdateResult();
        if (!string.IsNullOrWhiteSpace(updateResult))
        {
            _model.SettingsStatus = updateResult;
            _model.UpdateStatus = updateResult;
        }
        Loaded += async (_, _) =>
        {
            if (_monitoringStarted)
            {
                return;
            }

            _monitoringStarted = true;
            FitWindowToMonitor();
            _runtimeRefreshInFlight = true;
            _timer.Start();
            try
            {
                _productInstallQueue.ResumePending();
                await NginxProductProxyService.TrySyncAsync();
                await RefreshEnvironmentStatesAsync();
                await RefreshCleanupStorageUsageAsync();
            }
            catch (Exception ex)
            {
                WriteRuntimeRefreshError(ex);
            }
            finally
            {
                _runtimeRefreshInFlight = false;
            }
        };
    }

    private CustomPopupPlacement[] PlaceDownloadQueuePopup(
        Size popupSize,
        Size targetSize,
        Point offset)
    {
        const double popupGap = 32d;
        var (scaleX, scaleY) = GetProductsHeaderScale();
        // The placement point is expressed in the header's pre-Viewbox
        // coordinate space, while popupSize is already in screen DIPs. Convert
        // the popup width back through the horizontal scale before subtracting
        // it. Without this conversion the popup is shifted to the right (and
        // can be entirely off-screen) on compact/RDP window sizes.
        var popupLeft = CalculateDownloadQueuePopupLeft(
            targetSize.Width,
            popupSize.Width,
            scaleX);
        var scaledGapY = popupGap / scaleY;
        var popupTop = targetSize.Height + scaledGapY;
        var buttonTopInHeader = 0d;

        try
        {
            buttonTopInHeader = DownloadQueueButton
                .TranslatePoint(new Point(0, 0), ProductsHeader)
                .Y;
            popupTop = buttonTopInHeader + DownloadQueueButton.ActualHeight + scaledGapY;
        }
        catch (InvalidOperationException)
        {
            // Use the header-bottom fallback until the visual tree is connected.
        }

        return
        [
            new CustomPopupPlacement(
                new Point(popupLeft, popupTop),
                PopupPrimaryAxis.Horizontal),
            new CustomPopupPlacement(
                new Point(popupLeft, buttonTopInHeader - popupSize.Height / scaleY - scaledGapY),
                PopupPrimaryAxis.Horizontal)
        ];
    }

    internal static double CalculateDownloadQueuePopupLeft(
        double targetWidth,
        double popupWidth,
        double scaleX)
    {
        var safeScaleX = !double.IsNaN(scaleX) &&
                         !double.IsInfinity(scaleX) &&
                         scaleX > 0.001d
            ? scaleX
            : 1d;
        return Math.Max(0d, targetWidth - popupWidth / safeScaleX);
    }

    private (double ScaleX, double ScaleY) GetProductsHeaderScale()
    {
        try
        {
            if (ProductsHeader.ActualWidth <= 0 || ProductsHeader.ActualHeight <= 0)
            {
                return (1d, 1d);
            }

            var transform = ProductsHeader.TransformToAncestor(MainViewbox);
            var origin = transform.Transform(new Point(0, 0));
            var right = transform.Transform(new Point(ProductsHeader.ActualWidth, 0));
            var bottom = transform.Transform(new Point(0, ProductsHeader.ActualHeight));
            var scaleX = Math.Abs(right.X - origin.X) / ProductsHeader.ActualWidth;
            var scaleY = Math.Abs(bottom.Y - origin.Y) / ProductsHeader.ActualHeight;
            return (
                scaleX > 0.001d ? scaleX : 1d,
                scaleY > 0.001d ? scaleY : 1d);
        }
        catch (InvalidOperationException)
        {
            return (1d, 1d);
        }
    }

    private static void WriteRuntimeRefreshError(Exception exception)
    {
        try
        {
            var workDirectory = Path.Combine(AppContext.BaseDirectory, "StoreData", "Work");
            RollingLogWriter.Append(
                Path.Combine(workDirectory, "runtime-refresh-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]{Environment.NewLine}{exception}{Environment.NewLine}");
        }
        catch
        {
            // A monitoring failure must never terminate the panel.
        }
    }

    private void SystemEvents_UserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (_model.ThemeMode == PanelThemeMode.System &&
            e.Category is (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle))
        {
            Dispatcher.Invoke(ApplySelectedTheme);
        }
    }

    private void SystemEvents_DisplaySettingsChanged(object? sender, EventArgs e)
    {
        if (!Dispatcher.HasShutdownStarted)
        {
            Dispatcher.BeginInvoke(FitWindowToMonitor, DispatcherPriority.Loaded);
        }
    }

    private void FitWindowToMonitor()
    {
        ResponsiveWindowSizing.FitToCurrentMonitor(
            this,
            ResponsiveWindowSizing.MainDesignWidth,
            ResponsiveWindowSizing.MainDesignHeight,
            workAreaFill: 0.94,
            maximumScale: ResponsiveWindowSizing.MainDefaultScale);
    }

    private void ApplySelectedTheme()
    {
        var dark = _model.ThemeMode switch
        {
            PanelThemeMode.Dark => true,
            PanelThemeMode.Light => false,
            _ => IsSystemDarkTheme()
        };

        ApplyTheme(dark);
    }

    private void ApplyTheme(bool dark)
    {
        _isDarkThemeActive = dark;
        PanelThemeService.Apply(dark, Resources);
    }

    private void ThemeMode_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button || button.Tag is not string value ||
            !Enum.TryParse<PanelThemeMode>(value, ignoreCase: true, out var mode))
        {
            return;
        }

        var previousMode = _model.ThemeMode;
        _model.ThemeMode = mode;
        ApplySelectedTheme();
        try
        {
            _settingsService.SetThemeMode(mode);
            _model.SettingsStatus = mode switch
            {
                PanelThemeMode.Light => "已切换为浅色模式。",
                PanelThemeMode.Dark => "已切换为夜间模式。",
                _ => "已切换为跟随系统主题。"
            };
        }
        catch (Exception ex)
        {
            _model.ThemeMode = previousMode;
            ApplySelectedTheme();
            _model.SettingsStatus = $"主题配置保存失败：{ex.Message}";
            MessageBox.Show(_model.SettingsStatus, "面板设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static bool IsSystemDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }

    private void RefreshRuntimeStates(bool preserveBusy)
    {
        Interlocked.Increment(ref _runtimeRefreshGeneration);
        var frpState = _frpManager.GetState();
        _model.RefreshEnvironmentStates(_runtimeService, preserveBusy);
        if (!preserveBusy || !_model.EnvironmentItems.Any(item => item.Kind == EnvironmentKind.FrpTunnel && item.IsBusy))
        {
            _model.UpdateFrpState(frpState);
        }

        _model.RefreshSuiteServices(_runtimeService, frpState, preserveBusy);
    }

    private async Task RefreshEnvironmentStatesAsync(bool preserveBusy = false)
    {
        var generation = Volatile.Read(ref _runtimeRefreshGeneration);
        var statesTask = Task.Run(_runtimeService.GetStates);
        var frpTask = Task.Run(_frpManager.GetState);
        var directoriesTask = Task.Run(_runtimeService.GetInstallDirectories);
        await Task.WhenAll(statesTask, frpTask, directoriesTask);

        if (_isClosed || generation != Volatile.Read(ref _runtimeRefreshGeneration))
        {
            return;
        }

        _model.ApplyRuntimeStates(statesTask.Result, frpTask.Result, directoriesTask.Result, preserveBusy);
    }

    private async Task RefreshRuntimeStatesAfterFailureAsync()
    {
        // Invalidate a refresh that may have started before the failed operation
        // finished. Otherwise an older snapshot can overwrite the real state.
        Interlocked.Increment(ref _runtimeRefreshGeneration);
        try
        {
            await RefreshEnvironmentStatesAsync();
        }
        catch (Exception refreshError)
        {
            WriteRuntimeRefreshError(refreshError);
            try
            {
                RefreshRuntimeStates(preserveBusy: false);
            }
            catch (Exception fallbackError)
            {
                WriteRuntimeRefreshError(fallbackError);
            }
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleWindowState();
            return;
        }

        if (WindowState != WindowState.Maximized)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) => ToggleWindowState();

    private void ToggleWindowState() => WindowState = WindowState == WindowState.Maximized
        ? WindowState.Normal
        : WindowState.Maximized;

    private void UpdateWindowStateChrome()
    {
        if (MaximizeButton is null)
        {
            return;
        }

        var maximized = WindowState == WindowState.Maximized;
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, maximized ? "还原" : "最大化");
        var chrome = WindowChrome.GetWindowChrome(this);
        if (chrome is not null)
        {
            chrome.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
        }
        ApplyWindowCornerPreference();
    }

    private void ApplyWindowCornerPreference()
    {
        if (Environment.OSVersion.Platform != PlatformID.Win32NT ||
            Environment.OSVersion.Version.Build < 22000)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        try
        {
            var preference = WindowState == WindowState.Maximized
                ? DwmCornerPreferenceDoNotRound
                : DwmCornerPreferenceRound;
            _ = DwmSetWindowAttribute(
                hwnd,
                DwmWindowCornerPreferenceAttribute,
                ref preference,
                sizeof(int));
        }
        catch (DllNotFoundException)
        {
        }
        catch (EntryPointNotFoundException)
        {
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        if (Application.Current is App app && !app.HasTrayIcon)
        {
            Close();
            return;
        }

        // Closing the main window means "go to tray" in MCPanel. Keep the
        // queue worker and durable state alive; the tray icon can restore the
        // window and the queue button can reopen the progress panel later.
        _model.InstallationProgress.Hide();
        DownloadQueuePopup.IsOpen = false;
        Hide();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (Dispatcher.HasShutdownStarted ||
            Application.Current is not App app ||
            app.IsExiting ||
            !app.HasTrayIcon)
        {
            return;
        }

        // Alt+F4 and the shell close gesture follow the same close-to-tray
        // contract as the custom title-bar button. Only the tray's explicit
        // "退出 MCPanel" path is allowed to close this window.
        e.Cancel = true;
        _model.InstallationProgress.Hide();
        DownloadQueuePopup.IsOpen = false;
        Hide();
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton button || button.CommandParameter is not string page)
        {
            return;
        }

        FrameworkElement activePage = page switch
        {
            "Home" => HomePage,
            "Sites" => SitesPage,
            "Products" => ProductsPage,
            "Environment" => EnvironmentPage,
            "AccountApi" => AccountApiPageControl,
            "AiAnalysis" => AiAnalysisPageControl,
            "Settings" => SettingsPage,
            _ => HomePage
        };

        SetPageVisibility(HomePage, activePage == HomePage);
        SetPageVisibility(SitesPage, activePage == SitesPage);
        SetPageVisibility(ProductsPage, activePage == ProductsPage);
        SetPageVisibility(EnvironmentPage, activePage == EnvironmentPage);
        SetPageVisibility(AccountApiPageControl, activePage == AccountApiPageControl);
        SetPageVisibility(AiAnalysisPageControl, activePage == AiAnalysisPageControl);
        SetPageVisibility(SettingsPage, activePage == SettingsPage);
        SearchBox.Focusable = page == "Products";
        SearchBox.IsTabStop = page == "Products";
        SearchBox.IsEnabled = page == "Products";
        SearchBox.Visibility = page == "Products" ? Visibility.Visible : Visibility.Collapsed;
        if (page != "Products")
        {
            DownloadQueuePopup.IsOpen = false;
        }
        FocusNavigationItem(button);
        Dispatcher.BeginInvoke(
            new Action(() => FocusNavigationItem(button)),
            DispatcherPriority.ApplicationIdle);
        if (page == "AccountApi")
        {
            _ = AccountApiPageControl.ActivateAsync();
        }
        else
        {
            AccountApiPageControl.Deactivate();
        }

        if (page == "Sites")
        {
            _model.RefreshInstalledProducts();
            _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
        }
        else if (page == "Settings")
        {
            RefreshDatabaseToolState();
            _ = RefreshCleanupStorageUsageAsync();
        }

    }

    private static void SetPageVisibility(UIElement element, bool visible)
    {
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FocusNavigationItem(RadioButton button)
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(DesignSurface, button);
        Keyboard.Focus(PageFocusSentinel);
    }

    private void FocusActiveNavigationItem()
    {
        foreach (var child in NavItemsPanel.Children)
        {
            if (child is RadioButton { IsChecked: true } button)
            {
                FocusNavigationItem(button);
                return;
            }
        }
    }

    private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.SetStartupEnabled(_model.IsStartupEnabled);
            _model.SettingsStatus = _model.IsStartupEnabled
                ? "已开启开机自启动；下次登录 Windows 后将仅驻留托盘。"
                : "已关闭开机自启动。";
        }
        catch (Exception ex)
        {
            _model.IsStartupEnabled = _settingsService.IsStartupEnabled();
            _model.SettingsStatus = $"开机自启动设置失败：{ex.Message}";
            MessageBox.Show(_model.SettingsStatus, "面板设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SettingsAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case "OpenStoreData":
                    _settingsService.OpenStoreDataDirectory();
                    _model.SettingsStatus = "已打开 StoreData 数据目录。";
                    break;
                case "OpenRuntime":
                    _settingsService.OpenRuntimeDirectory();
                    _model.SettingsStatus = "已打开软件根目录，可查看各组件的独立文件夹。";
                    break;
                case "OpenProducts":
                    _settingsService.OpenProductDirectory();
                    _model.SettingsStatus = "已打开产品安装目录。";
                    break;
                case "OpenLogs":
                    _settingsService.OpenLogDirectory();
                    _model.SettingsStatus = "已打开脚本和日志目录。";
                    break;
                case "ClearProductCache":
                    _settingsService.ClearProductCache();
                    _model.SettingsStatus = "已清理产品列表缓存和图标缓存。";
                    await RefreshCleanupStorageUsageAsync();
                    MessageBox.Show(_model.SettingsStatus, "面板设置", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "面板设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal async void DatabaseToolAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        var isNavicat = action.StartsWith("Navicat", StringComparison.Ordinal);
        var toolModel = isNavicat ? _model.NavicatTool : _model.SqlServerTool;
        try
        {
            switch (action)
            {
                case "NavicatInstall":
                    if (_settingsService.GetNavicatInstallation() is not null)
                    {
                        _model.SettingsStatus = "已检测到 Navicat，无需重复安装。";
                        return;
                    }

                    if (!ConfirmNavicatPremiumLiteInstall("安装 MySQL 连接工具"))
                    {
                        _model.SettingsStatus = "已取消安装 Navicat Premium Lite。";
                        return;
                    }

                    BeginDatabaseToolOperation(toolModel, "正在准备从 Navicat 官网下载 Premium Lite...");
                    await InstallNavicatPremiumLiteAsync(progress => UpdateDatabaseToolProgress(toolModel, progress));
                    if (_settingsService.GetNavicatInstallation() is null)
                    {
                        throw new FileNotFoundException(
                            $"Navicat Premium Lite 安装完成，但未在 {_settingsService.NavicatInstallRoot} 检测到 navicat.exe。");
                    }

                    _model.SettingsStatus = "Navicat Premium Lite 安装完成。";
                    break;

                case "SsmsInstall":
                    if (_settingsService.GetSqlManagementStudioInstallation() is not null)
                    {
                        _model.SettingsStatus = "已检测到 SQL Server Management Studio，无需重复安装。";
                        return;
                    }

                    var installSsms = MessageBox.Show(
                        "是否从微软官方下载并安装 SQL Server Management Studio？\n\n" +
                        $"安装目录：{_settingsService.SsmsInstallRoot}\n" +
                        "安装完成前请不要关闭或强制结束安装程序。",
                        "安装 SQL Server 连接工具",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (installSsms != MessageBoxResult.Yes)
                    {
                        _model.SettingsStatus = "已取消安装 SQL Server Management Studio。";
                        return;
                    }

                    BeginDatabaseToolOperation(toolModel, "正在准备从微软官方下载 SQL Server Management Studio...");
                    await RunDatabaseToolOperationAsync(token =>
                        _settingsService.InstallSqlManagementStudioAsync(
                            progress => UpdateDatabaseToolProgress(toolModel, progress),
                            token));
                    if (_settingsService.GetSqlManagementStudioInstallation() is null)
                    {
                        throw new FileNotFoundException(
                            $"SQL Server Management Studio 安装完成，但未在 {_settingsService.SsmsInstallRoot} 检测到 Ssms.exe。");
                    }

                    _model.SettingsStatus = "SQL Server Management Studio 安装完成。";
                    break;

                case "NavicatUninstall":
                    await UninstallDatabaseToolAsync(DatabaseToolKind.Navicat, toolModel);
                    break;

                case "SsmsUninstall":
                    await UninstallDatabaseToolAsync(DatabaseToolKind.SqlServerManagementStudio, toolModel);
                    break;

                case "NavicatConnect":
                    if (!_settingsService.TryOpenNavicatForMySql())
                    {
                        throw new FileNotFoundException("未安装 MySQL 连接工具，请先点击“安装”或“配置路径”。");
                    }

                    var mysqlClipboard = MySqlCredentialStore.FormatForClipboard();
                    Clipboard.SetText(mysqlClipboard);
                    ClipboardSafety.ScheduleClear(mysqlClipboard);
                    _model.SettingsStatus = "已打开 Navicat，并将 MySQL 连接信息复制到剪贴板。";
                    break;

                case "SsmsConnect":
                    if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
                    {
                        throw new FileNotFoundException("未安装 SQL Server 连接工具，请先点击“安装”或“配置路径”。");
                    }

                    var sqlClipboard = SqlServerCredentialStore.FormatForClipboard();
                    Clipboard.SetText(sqlClipboard);
                    ClipboardSafety.ScheduleClear(sqlClipboard);
                    _model.SettingsStatus = "已打开 SQL Server Management Studio，并将连接信息复制到剪贴板。";
                    break;

                case "NavicatConfigure":
                    SelectDatabaseToolPath(DatabaseToolKind.Navicat);
                    break;

                case "SsmsConfigure":
                    SelectDatabaseToolPath(DatabaseToolKind.SqlServerManagementStudio);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _model.SettingsStatus = "数据库工具操作已取消。";
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshDatabaseToolState();
        }
    }

    private async Task UninstallDatabaseToolAsync(DatabaseToolKind kind, DatabaseToolViewModel toolModel)
    {
        var installation = kind == DatabaseToolKind.Navicat
            ? _settingsService.GetNavicatInstallation()
            : _settingsService.GetSqlManagementStudioInstallation();
        if (installation is null)
        {
            throw new FileNotFoundException("当前未检测到可卸载的数据库连接工具。");
        }

        var confirm = MessageBox.Show(
            $"确定卸载 {installation.DisplayName} 吗？\n\n" +
            $"检测来源：{installation.SourceText}\n" +
            $"程序路径：{installation.ExecutablePath}\n\n" +
            "面板会调用该产品自带或 Windows 注册的卸载程序，不会扫描或直接删除其他目录。",
            "卸载数据库工具",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            _model.SettingsStatus = $"已取消卸载 {installation.DisplayName}。";
            return;
        }

        BeginDatabaseToolOperation(toolModel, $"正在卸载 {installation.DisplayName}...");
        var result = string.Empty;
        await RunDatabaseToolOperationAsync(async token =>
        {
            result = kind == DatabaseToolKind.Navicat
                ? await _settingsService.UninstallNavicatAsync(token)
                : await _settingsService.UninstallSqlManagementStudioAsync(token);
        });
        if (File.Exists(installation.ExecutablePath))
        {
            result += $"\n\n卸载程序已经结束，但仍检测到：{installation.ExecutablePath}" +
                      "\n可能需要重启 Windows，或卸载没有完成；请按上面的日志位置检查。";
            _model.SettingsStatus = "卸载程序已结束，但仍检测到数据库工具，请查看日志。";
            MessageBox.Show(result, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _model.SettingsStatus = result;
        MessageBox.Show(result, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal void DatabaseToolPath_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        var kind = tag switch
        {
            "NavicatPath" => DatabaseToolKind.Navicat,
            "SsmsPath" => DatabaseToolKind.SqlServerManagementStudio,
            _ => throw new InvalidOperationException("未知的数据库工具路径。")
        };

        try
        {
            _settingsService.OpenDatabaseToolDirectory(kind);
            _model.SettingsStatus = kind == DatabaseToolKind.Navicat
                ? "已打开 Navicat 安装目录。"
                : "已打开 SQL Server Management Studio 安装目录。";
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void SelectDatabaseToolPath(DatabaseToolKind kind)
    {
        var isNavicat = kind == DatabaseToolKind.Navicat;
        var dialog = new OpenFileDialog
        {
            Title = isNavicat
                ? "选择 Navicat 可执行文件"
                : "选择 SQL Server Management Studio 可执行文件",
            Filter = isNavicat
                ? "Navicat (navicat.exe)|navicat.exe|可执行文件 (*.exe)|*.exe"
                : "SQL Server Management Studio (Ssms.exe)|Ssms.exe|可执行文件 (*.exe)|*.exe"
        };

        var current = isNavicat
            ? _settingsService.GetNavicatInstallation()
            : _settingsService.GetSqlManagementStudioInstallation();
        if (current is not null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current.ExecutablePath);
            dialog.FileName = Path.GetFileName(current.ExecutablePath);
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (isNavicat)
        {
            _settingsService.SetNavicatPath(dialog.FileName);
            _model.SettingsStatus = "已保存 Navicat 配置路径。";
        }
        else
        {
            _settingsService.SetSqlManagementStudioPath(dialog.FileName);
            _model.SettingsStatus = "已保存 SQL Server Management Studio 配置路径。";
        }
    }

    private void BeginDatabaseToolOperation(DatabaseToolViewModel activeTool, string message)
    {
        _model.NavicatTool.SetBusy(true);
        _model.SqlServerTool.SetBusy(true);
        activeTool.SetProgressMessage(message);
        _model.SettingsStatus = message;
    }

    private void UpdateDatabaseToolProgress(DatabaseToolViewModel tool, InstallProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            tool.SetProgressMessage(progress.Message);
            _model.SettingsStatus = progress.Message;
        });
    }

    private void RefreshDatabaseToolState()
    {
        _model.NavicatTool.Apply(
            _settingsService.GetNavicatInstallation(),
            _settingsService.NavicatInstallRoot);
        var ssmsRecommendation = _settingsService.GetSqlManagementStudioRecommendation();
        _model.SqlServerTool.Apply(
            _settingsService.GetSqlManagementStudioInstallation(),
            _settingsService.SsmsInstallRoot,
            ssmsRecommendation.Release.DisplayName);
    }

    private bool ConfirmNavicatPremiumLiteInstall(string title)
    {
        var message =
            "未找到 Navicat。是否从 Navicat 官方下载并安装 Premium Lite？\n\n" +
            $"安装目录：{_settingsService.NavicatInstallRoot}\n" +
            "下载完成后会校验 PremiumSoft 官方数字签名。\n" +
            "Navicat 官网标注 Premium Lite 每家机构最多 5 位用户，请确认符合使用条件。";
        return MessageBox.Show(
                   message,
                   title,
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Information) == MessageBoxResult.Yes;
    }

    private async Task InstallNavicatPremiumLiteAsync(Action<InstallProgress> progress)
    {
        if (_databaseToolCancellation is not null)
        {
            throw new InvalidOperationException("已有数据库工具正在下载或安装，请稍候。");
        }

        var cancellation = new CancellationTokenSource();
        _databaseToolCancellation = cancellation;
        try
        {
            await _settingsService.InstallNavicatPremiumLiteAsync(progress, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_databaseToolCancellation, cancellation))
            {
                _databaseToolCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task RunDatabaseToolOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_databaseToolCancellation is not null)
        {
            throw new InvalidOperationException("已有数据库工具正在下载、安装或卸载，请稍候。");
        }

        var cancellation = new CancellationTokenSource();
        _databaseToolCancellation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_databaseToolCancellation, cancellation))
            {
                _databaseToolCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task RefreshCleanupStorageUsageAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cleanupUsageRefreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _model.ProductCacheSizeText = "正在计算...";
        try
        {
            var usage = await _settingsService.GetCleanupStorageUsageAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _model.ProductCacheSizeText = $"占用空间  {PanelSettingsService.FormatStorageSize(usage.ProductCacheBytes)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _model.ProductCacheSizeText = "暂时无法统计";
        }
        finally
        {
            if (ReferenceEquals(_cleanupUsageRefreshCancellation, cancellation))
            {
                _cleanupUsageRefreshCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async void CheckOnlineUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;
        var githubRepository = _applicationUpdateService.LoadGitHubUpdateRepository();
        if (!string.IsNullOrWhiteSpace(githubRepository))
        {
            if (!_applicationUpdateService.HasStoredGitHubUpdateToken())
            {
                MessageBox.Show(
                    $"已配置 GitHub 私有更新仓库 {githubRepository}，但当前 Windows 用户尚未保存访问令牌。\n\n请先点击“配置 GitHub”，保存一个仅限该仓库、Contents: read 权限的固定访问令牌。",
                    "软件更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
            {
                status.Report("正在检查 GitHub 私有 Release...");
                var source = _applicationUpdateService.LoadGitHubReleaseUpdateSource();
                var update = await _applicationUpdateService.CheckGitHubReleaseAsync(source, cancellationToken);
                if (!Version.TryParse(update.Manifest.Version, out var targetVersion) || targetVersion <= ApplicationUpdateService.CurrentVersion)
                {
                    _model.UpdateStatus = $"当前已是最新版本（{ApplicationUpdateService.CurrentVersionText}）。";
                    MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return null;
                }

                if (!ConfirmOnlineUpdate(update.Manifest)) return null;
                return await _applicationUpdateService.PrepareGitHubReleaseAsync(update, progress, status, cancellationToken);
            });
            return;
        }

        var manifestUrl = _applicationUpdateService.LoadManifestUrl();
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            MessageBox.Show("尚未配置在线更新地址。请在 MCPanel.exe.config 的 UpdateManifestUrl 中填写 HTTPS 更新清单地址。", "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
        {
            status.Report("正在检查在线更新...");
            var manifest = await _applicationUpdateService.CheckOnlineAsync(manifestUrl, cancellationToken);
            if (!Version.TryParse(manifest.Version, out var targetVersion) || targetVersion <= ApplicationUpdateService.CurrentVersion)
            {
                _model.UpdateStatus = $"当前已是最新版本（{ApplicationUpdateService.CurrentVersionText}）。";
                MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            if (!ConfirmOnlineUpdate(manifest)) return null;
            return await _applicationUpdateService.PrepareOnlineAsync(manifest, progress, status, cancellationToken);
        });
    }

    private static bool ConfirmOnlineUpdate(OnlineUpdateManifest manifest)
    {
        var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? "发布方未提供更新说明。" : manifest.ReleaseNotes.Trim();
        if (manifest.Mandatory)
        {
            MessageBox.Show(
                $"发现必须安装的新版本 {manifest.Version}\n\n{notes}\n\nMCPanel 将下载并安装此更新，完成后软件会自动重启。",
                "重要软件更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return true;
        }

        return MessageBox.Show(
            $"发现新版本 {manifest.Version}\n\n{notes}\n\n是否下载并安装？更新完成后软件会自动重启。",
            "软件更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information) == MessageBoxResult.Yes;
    }

    private void ConfigureGitHubUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;

        string repository;
        try
        {
            repository = ApplicationUpdateService.NormalizeGitHubRepository(
                _applicationUpdateService.LoadGitHubUpdateRepository());
        }
        catch (Exception error)
        {
            MessageBox.Show($"GitHub 更新仓库配置无效：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (repository.Length == 0)
        {
            MessageBox.Show("尚未配置 GitHub 更新仓库。", "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hasStoredToken = _applicationUpdateService.HasStoredGitHubUpdateToken();
        var dialog = new Window
        {
            Title = "配置 GitHub 私有更新",
            Owner = this,
            Width = 470,
            Height = 338,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("SurfaceBrush")
        };
        var token = new PasswordBox
        {
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 256,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("TextBrush"),
            CaretBrush = (Brush)FindResource("TextBrush"),
            BorderThickness = new Thickness(0)
        };
        var tokenInput = new Border
        {
            Height = 38,
            Padding = new Thickness(10, 0, 10, 0),
            Background = (Brush)FindResource("InputBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = token
        };
        var savedState = new TextBlock
        {
            Text = hasStoredToken
                ? "当前 Windows 用户已保存固定令牌。输入新令牌后会安全替换旧令牌。"
                : "尚未保存令牌。首次保存后，MCPanel 会自动使用它检查私有 Release。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var save = new Button
        {
            Content = "安全保存",
            Width = 96,
            Height = 34,
            Style = (Style)FindResource("PrimaryButton"),
            IsDefault = true
        };
        var clear = new Button
        {
            Content = "移除令牌",
            Width = 88,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsEnabled = hasStoredToken
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 72,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsCancel = true
        };

        save.Click += (_, _) =>
        {
            try
            {
                _applicationUpdateService.SaveGitHubUpdateToken(token.Password);
                _model.UpdateStatus = $"已为当前 Windows 用户安全保存 GitHub 更新令牌（{repository}）。";
                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法保存 GitHub 更新令牌：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                token.Focus();
                token.SelectAll();
            }
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    "移除后，这台云电脑将无法从私有 GitHub Release 检查更新，直到重新保存令牌。是否继续？",
                    "移除 GitHub 更新令牌",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _applicationUpdateService.ClearGitHubUpdateToken();
                _model.UpdateStatus = "已移除当前 Windows 用户保存的 GitHub 更新令牌。";
                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法移除 GitHub 更新令牌：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var content = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        for (var i = 0; i < 6; i++)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "GitHub 私有更新",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        };
        var hint = new TextBlock
        {
            Text = "更新仓库已固定。只需保存一个固定的细粒度访问令牌；仅授予 Contents: read 权限，令牌不会写入配置文件、日志或更新包。",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var repositorySummary = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("CardLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        var repositoryContent = new StackPanel();
        repositoryContent.Children.Add(new TextBlock
        {
            Text = "更新仓库（固定）",
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush")
        });
        repositoryContent.Children.Add(new TextBlock
        {
            Text = repository,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        });
        repositorySummary.Child = repositoryContent;
        var tokenLabel = new TextBlock
        {
            Text = "固定访问令牌",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(save);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);

        Grid.SetRow(title, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(repositorySummary, 2);
        Grid.SetRow(tokenLabel, 3);
        Grid.SetRow(tokenInput, 4);
        Grid.SetRow(savedState, 5);
        Grid.SetRow(buttons, 7);
        content.Children.Add(title);
        content.Children.Add(hint);
        content.Children.Add(repositorySummary);
        content.Children.Add(tokenLabel);
        content.Children.Add(tokenInput);
        content.Children.Add(savedState);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => token.Focus();
        dialog.ShowDialog();
    }

    private async void LocalUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 MCPanel 更新包",
            Filter = "MCPanel 更新包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
        {
            var prepared = await _applicationUpdateService.PrepareLocalAsync(dialog.FileName, progress, status, cancellationToken);
            var comparison = Version.TryParse(prepared.Version, out var targetVersion)
                ? targetVersion.CompareTo(ApplicationUpdateService.CurrentVersion)
                : 0;
            var warning = comparison <= 0
                ? $"所选更新包版本为 {prepared.Version}，不高于当前版本 {ApplicationUpdateService.CurrentVersionText}。\n这将执行覆盖安装，是否继续？"
                : $"已验证版本 {prepared.Version} 的本地更新包。\n安装期间软件会关闭，完成后自动重启，是否继续？";
            var confirmed = MessageBox.Show(warning, "本地更新", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                _applicationUpdateService.DiscardPreparedUpdate(prepared);
                return null;
            }

            return prepared;
        });
    }

    private bool CanStartApplicationUpdate()
    {
        if (_model.IsUpdateBusy)
        {
            return false;
        }

        if (!_productInstallQueue.HasActiveItems)
        {
            return true;
        }

        const string message = "当前仍有产品正在下载、安装或部署。请先等待安装队列完成，或取消全部任务后再更新 MCPanel。";
        _model.UpdateStatus = message;
        MessageBox.Show(message, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void CancelApplicationUpdate_Click(object sender, RoutedEventArgs e)
    {
        _applicationUpdateCancellation?.Cancel();
    }

    private async Task RunUpdatePreparationAsync(
        Func<IProgress<double>, IProgress<string>, CancellationToken, Task<PreparedApplicationUpdate?>> prepare)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _applicationUpdateCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _model.IsUpdateBusy = true;
        _model.UpdateProgress = 0;
        PreparedApplicationUpdate? preparedForCleanup = null;
        var updaterLaunched = false;
        var progress = new Progress<double>(value => _model.UpdateProgress = Math.Max(0, Math.Min(100, value)));
        var status = new Progress<string>(value => _model.UpdateStatus = value);
        try
        {
            preparedForCleanup = await prepare(progress, status, cancellation.Token);
            if (preparedForCleanup is null)
            {
                if (!_model.UpdateStatus.StartsWith("当前已是", StringComparison.Ordinal))
                    _model.UpdateStatus = "更新操作已取消。";
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            _model.UpdateStatus = "正在启动安全更新器，软件即将重启...";
            _applicationUpdateService.LaunchUpdater(preparedForCleanup);
            updaterLaunched = true;
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            _model.UpdateStatus = "已取消更新，现有版本未被修改。";
        }
        catch (Exception ex)
        {
            var logPath = EnvironmentOperationDiagnostics.RecordFailure(
                "软件更新",
                "准备或启动更新器",
                ex,
                Path.Combine(AppContext.BaseDirectory, "StoreData", "Work", "application-update-error.log"));
            _model.UpdateStatus = $"更新失败：{ex.GetBaseException().Message}。详细日志：{logPath}";
            if (!_isClosed)
            {
                MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!updaterLaunched && preparedForCleanup is not null)
            {
                _applicationUpdateService.DiscardPreparedUpdate(preparedForCleanup);
            }

            if (ReferenceEquals(_applicationUpdateCancellation, cancellation))
            {
                Interlocked.CompareExchange(ref _applicationUpdateCancellation, null, cancellation);
            }
            cancellation.Dispose();
            _model.IsUpdateBusy = false;
        }
    }

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

    private void InstalledProductOpen_Click(object sender, RoutedEventArgs e)
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
            item.SetOperationState(action switch
            {
                "Start" => "正在独立启动",
                "Catalina" => "正在打开诊断模式",
                "Stop" => "正在停止",
                _ => "正在处理"
            });

            var message = action switch
            {
                "Start" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: false),
                "Catalina" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: true),
                "Stop" => await _tomcatInstanceManager.StopAsync(item.ProductId),
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

    internal async void EnvironmentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        if (item.Kind == EnvironmentKind.Iis && EnvironmentRuntimeService.HasIisUninstallContinuation)
        {
            await ContinueIisUninstallAsync(item);
            return;
        }

        try
        {
            item.SetBusyState("安装中", "正在准备安装...", 5);
            if (item.Kind == EnvironmentKind.FrpTunnel)
            {
                    await _frpManager.InstallAsync(progress =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            item.ApplyInstallProgress(progress);
                        });
                    });
            }
            else if (EnvironmentInstallWorker.RequiresImmediateElevation(item.Kind))
            {
                await InstallWithImmediateElevationAsync(item);
            }
            else
            {
                await _environmentInstaller.InstallAsync(item, progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        item.ApplyInstallProgress(progress);
                    });
                });
            }

            if (item.Kind == EnvironmentKind.FrpTunnel)
            {
                item.ApplyRuntimeState(_frpManager.GetState());
                MessageBox.Show("FRP 客户端已安装，但不会自动启动。现在可以点击“管理”配置，或点击“启动”运行。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                item.IsInstalled = true;
                item.Progress = 100;
                item.BadgeText = "已安装";
                item.BadgeBrush = RuntimeStatusVisuals.Brush(RuntimeStatusKind.Running);
                MessageBox.Show($"{item.Title} 安装流程已完成。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            RefreshRuntimeStates(preserveBusy: false);
        }
        catch (InstallRestartRequiredException ex)
        {
            item.StatusText = ex.Message;
            item.BadgeText = "需重启";
            item.BadgeBrush = Brushes.Goldenrod;
            item.MarkInstallRestartRequired();
            MessageBox.Show(ex.Message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, "安装", ex);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task ContinueIisUninstallAsync(EnvironmentItem item)
    {
        try
        {
            item.SetBusyState("卸载中", "正在检查重启后的 IIS 组件状态...", 80);
            var message = await _runtimeService.UninstallAsync(EnvironmentKind.Iis);
            item.ApplyRuntimeState(_runtimeService.GetState(EnvironmentKind.Iis));
            item.StatusText = message;
            item.Progress = 0;
            _model.RefreshEnvironmentStates(_runtimeService);
            _model.RefreshSuiteServices(_runtimeService, _frpManager.GetState());
            MessageBox.Show(message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, "卸载", ex);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task InstallWithImmediateElevationAsync(EnvironmentItem item)
    {
        var progressPath = EnvironmentInstallWorker.CreateProgressFile();
        Process? worker = null;
        try
        {
            // Starting the worker is intentionally the first installation action. The runas
            // verb shows UAC before any download, script generation, or service work begins.
            worker = EnvironmentInstallWorker.Start(item.Kind, progressPath, item.SelectedInstallReleaseId);
            while (!worker.HasExited)
            {
                ApplyElevatedInstallProgress(item, progressPath);
                await Task.Delay(300);
            }

            ApplyElevatedInstallProgress(item, progressPath);
            if (worker.ExitCode != 0)
            {
                var hasProgress = EnvironmentInstallWorker.TryReadProgress(progressPath, out var failed);
                var message = hasProgress
                    ? failed.Message
                    : "管理员安装进程异常退出。";
                if (hasProgress && string.Equals(failed.State, "restart-required", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallRestartRequiredException(message);
                }

                throw new EnvironmentOperationException(message, hasProgress ? failed.LogPath : null, exitCode: worker.ExitCode);
            }
        }
        finally
        {
            worker?.Dispose();
            EnvironmentInstallWorker.DeleteProgressFile(progressPath);
        }
    }

    private static void ApplyElevatedInstallProgress(EnvironmentItem item, string progressPath)
    {
        if (!EnvironmentInstallWorker.TryReadProgress(progressPath, out var progress))
        {
            return;
        }

        item.ApplyInstallProgress(new InstallProgress(
            progress.Percent,
            progress.Message,
            progress.Stage,
            progress.StagePercent,
            progress.SpeedText));
        item.BadgeText = progress.State switch
        {
            "failed" => "失败",
            "restart-required" => "需重启",
            "completed" => "已完成",
            _ => progress.Stage == InstallProgressStage.Downloading ? "下载中" : "安装中"
        };
        item.BadgeBrush = progress.State switch
        {
            "failed" => Brushes.IndianRed,
            "restart-required" => Brushes.Goldenrod,
            _ => RuntimeStatusVisuals.Brush(RuntimeStatusKind.Starting)
        };
    }

    internal void EnvironmentOpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item ||
            string.IsNullOrWhiteSpace(item.InstallDirectory))
        {
            return;
        }

        if (!Directory.Exists(item.InstallDirectory))
        {
            item.InstallDirectory = null;
            MessageBox.Show("安装目录已不存在，请刷新环境状态或重新安装该组件。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{item.InstallDirectory}\"",
            UseShellExecute = true
        });
    }

    internal async void EnvironmentRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item ||
            sender is not Button button ||
            button.Tag is not string action)
        {
            return;
        }

        if (action == "Uninstall")
        {
            var message = BuildEnvironmentUninstallConfirmation(item);
            var confirm = MessageBox.Show(message, "环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        try
        {
            item.SetBusyState(RuntimeBusyBadgeText(action), $"{item.Title} 正在{RuntimeActionText(action)}...");
            var message = item.Kind == EnvironmentKind.FrpTunnel
                ? await ExecuteFrpRuntimeActionAsync(action)
                : action switch
                {
                    "Start" => await _runtimeService.StartAsync(item.Kind),
                    "Stop" => await _runtimeService.StopAsync(item.Kind),
                    "Restart" => await _runtimeService.RestartAsync(item.Kind),
                    "Uninstall" => await _runtimeService.UninstallAsync(item.Kind),
                    "OpenIis" => OpenIisManager(),
                    "CatalinaRun" => _runtimeService.StartTomcatInCatalinaConsole(),
                    _ => throw new NotSupportedException("未知环境操作。")
                };

            _model.RefreshEnvironmentStates(_runtimeService);
            var frpState = _frpManager.GetState();
            _model.UpdateFrpState(frpState);
            _model.RefreshSuiteServices(_runtimeService, frpState);
            item.StatusText = message;
            if (action == "Uninstall")
            {
                MessageBox.Show(message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, RuntimeActionText(action), ex);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private async Task<string> ExecuteFrpRuntimeActionAsync(string action)
    {
        switch (action)
        {
            case "Start":
                await _frpManager.StartAsync();
                return "FRP 客户端已启动。";
            case "Stop":
                await _frpManager.StopAsync();
                return "FRP 客户端已停止，安装与配置仍保留。";
            case "Restart":
                await _frpManager.StopAsync();
                await Task.Delay(800);
                await _frpManager.StartAsync();
                return "FRP 客户端已重启。";
            case "Uninstall":
                return await _frpManager.UninstallAsync();
            default:
                throw new NotSupportedException("未知 FRP 操作。");
        }
    }

    private static string BuildEnvironmentUninstallConfirmation(EnvironmentItem item) => item.Kind switch
    {
        EnvironmentKind.Iis => "确定卸载 IIS 吗？\n会禁用 MCPanel 使用的 IIS Windows 功能并卸载 URL Rewrite；Windows 可能要求重启后继续。",
        EnvironmentKind.Nginx => "确定卸载 Nginx 吗？\n会停止并删除 MCPanel 的 Nginx Windows 服务，然后删除 Nginx 组件目录和代理配置。",
        EnvironmentKind.MySql => "确定卸载 MySQL 吗？\n会停止并删除 MySQL80 服务，以及 MCPanel 的 MySQL 程序、数据和配置目录。",
        EnvironmentKind.SqlServer => "确定完整卸载 SQL Server 吗？\n会停止并移除 SQL Server 服务、相关系统组件、注册表项、Program Files/ProgramData 残留目录，以及 MCPanel 的 MSSQL 数据目录。\n仅在确认本机没有其他需要保留的 SQL Server 实例时执行。",
        EnvironmentKind.Tomcat => "确定卸载 Tomcat 吗？\n会停止 MCPanel 管理的全部 Tomcat 进程，并删除 Tomcat 组件目录；已部署产品数据不会随此按钮删除。",
        EnvironmentKind.FrpTunnel => "确定卸载 FRP 吗？\n会停止 frpc，并删除 FRP 客户端、frpc.toml 映射配置以及下载缓存。此操作不会影响其他五个环境。",
        _ => $"确定卸载 {item.Title} 吗？"
    };

    internal async void ConnectMySql_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        try
        {
            item.SetBusyState("连接中", "正在检查 MySQL 连接工具...");
            if (!_settingsService.TryOpenNavicatForMySql())
            {
                item.StatusText = "未找到 Navicat Premium Lite。";
                if (!ConfirmNavicatPremiumLiteInstall("连接 MySQL"))
                {
                    item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
                    item.StatusText = "已取消安装 Navicat Premium Lite。";
                    return;
                }

                item.SetBusyState("安装工具中", "正在准备下载 Navicat Premium Lite...", 0);
                await InstallNavicatPremiumLiteAsync(progress =>
                {
                    item.Progress = progress.Percent;
                    item.StatusText = progress.Message;
                });
                if (!_settingsService.TryOpenNavicatForMySql())
                {
                    throw new FileNotFoundException(
                        $"Navicat Premium Lite 安装完成，但未在 {_settingsService.NavicatInstallRoot} 检测到 navicat.exe。");
                }
            }

            var mysqlClipboard = MySqlCredentialStore.FormatForClipboard();
            Clipboard.SetText(mysqlClipboard);
            ClipboardSafety.ScheduleClear(mysqlClipboard);
            item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
            item.StatusText = "已打开 Navicat Premium Lite，MySQL 默认连接信息已复制到剪贴板。";
            RefreshDatabaseToolState();
        }
        catch (Exception ex)
        {
            item.StatusText = $"连接 MySQL 失败：{ex.Message}";
            item.BadgeText = "失败";
            item.BadgeBrush = Brushes.IndianRed;
            MessageBox.Show(item.StatusText, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    internal async void ConnectSqlServer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        try
        {
            item.SetBusyState("连接中", "正在检查 SQL Server 加密连接...");
            await _settingsService.EnsureSqlServerLocalCertificateAsync();
            item.StatusText = "证书检查完成，正在打开 SQL Server Management Studio...";
            var sqlClipboard = SqlServerCredentialStore.FormatForClipboard();
            Clipboard.SetText(sqlClipboard);
            ClipboardSafety.ScheduleClear(sqlClipboard);

            if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
            {
                item.StatusText = "未找到 SQL Server Management Studio。";
                var confirm = MessageBox.Show(
                    "未找到 SQL Server Management Studio，是否从微软官方下载并安装？",
                    "连接 SQL Server",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (confirm != MessageBoxResult.Yes)
                {
                    item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
                    item.StatusText = "已取消打开 SQL Server Management Studio。";
                    return;
                }

                item.SetBusyState("安装工具中", "正在准备下载 SQL Server Management Studio...", 0);
                await RunDatabaseToolOperationAsync(token =>
                    _settingsService.InstallSqlManagementStudioAsync(progress =>
                    {
                        item.Progress = progress.Percent;
                        item.StatusText = progress.Message;
                    }, token));
                if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
                {
                    throw new FileNotFoundException("SSMS 安装完成，但未检测到 Ssms.exe，请从开始菜单打开或重启面板后重试。");
                }
            }

            item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
            item.StatusText = "已打开 SQL Server Management Studio，SQL Server 默认连接信息已复制到剪贴板。";
            RefreshDatabaseToolState();
        }
        catch (Exception ex)
        {
            item.StatusText = $"连接 SQL Server 失败：{ex.Message}";
            item.BadgeText = "失败";
            item.BadgeBrush = Brushes.IndianRed;
            MessageBox.Show(item.StatusText, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    internal async void EnvironmentCardFlip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not EnvironmentItem item ||
            item.IsFlipAnimating ||
            !item.CanFlipCard)
        {
            return;
        }

        if (item.IsTomcatModule && !item.IsFlipped)
        {
            item.RefreshTomcatPorts();
        }

        var card = FindAncestorBorder(element, "EnvironmentCard");
        if (card is null)
        {
            item.IsFlipped = !item.IsFlipped;
            return;
        }

        var scale = card.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        if (scale.IsFrozen)
        {
            scale = scale.CloneCurrentValue();
        }
        card.RenderTransform = scale;

        item.IsFlipAnimating = true;
        try
        {
            await AnimateScaleXAsync(scale, scale.ScaleX, 0, TimeSpan.FromMilliseconds(150));
            item.IsFlipped = !item.IsFlipped;
            await AnimateScaleXAsync(scale, 0, 1, TimeSpan.FromMilliseconds(190));
        }
        catch
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = 1;
        }
        finally
        {
            item.IsFlipAnimating = false;
        }
    }

    internal async void TomcatPortSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TomcatPortItem portItem ||
            portItem.IsBusy)
        {
            return;
        }

        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is not EnvironmentItem environmentItem)
        {
            return;
        }

        if (!int.TryParse(portItem.PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var newPort) ||
            newPort is < 9000 or > 10000)
        {
            environmentItem.TomcatPortNotice = "请输入 9000 到 10000 之间的端口号。";
            return;
        }

        if (newPort == portItem.Port)
        {
            environmentItem.TomcatPortNotice = $"{portItem.ProductId} 当前已使用端口 {newPort}。";
            return;
        }

        portItem.IsBusy = true;
        environmentItem.TomcatPortNotice = $"正在更新 {portItem.ProductId} 的端口...";
        try
        {
            var updated = await ProductDeploymentService.ChangeTomcatProductPortAsync(portItem.ProductId, newPort);
            await _tomcatInstanceManager.PrepareAsync(updated);
            await NginxProductProxyService.TrySyncAsync();
            portItem.Apply(updated);
            environmentItem.TomcatPortNotice = $"{portItem.ProductId} 已改为端口 {newPort}，下次启动时生效。";
            _model.RefreshInstalledProducts();
        }
        catch (Exception ex)
        {
            portItem.PortText = portItem.Port.ToString(CultureInfo.InvariantCulture);
            environmentItem.TomcatPortNotice = "端口修改失败";
            MessageBox.Show(ex.Message, "修改 Tomcat 产品端口", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            portItem.IsBusy = false;
        }
    }

    internal void CredentialCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string value)
        {
            return;
        }

        Clipboard.SetText(value);
        ClipboardSafety.ScheduleClear(value);
        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is EnvironmentItem item)
        {
            var label = button.CommandParameter as string ?? "信息";
            item.CredentialNotice = $"{label}已复制";
        }
    }

    internal async void CredentialEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not CredentialField field ||
            !field.CanEdit)
        {
            return;
        }

        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is not EnvironmentItem item ||
            item.Kind != EnvironmentKind.MySql)
        {
            return;
        }

        var credentials = MySqlCredentialStore.Load();
        if (field.EditKind == CredentialEditKind.Password)
        {
            var newPassword = ShowPasswordEditor();
            if (newPassword is null)
            {
                return;
            }

            try
            {
                item.SetBusyState("配置中", "正在修改并验证 MySQL root 密码...");
                var message = await _runtimeService.ChangeMySqlPasswordAsync(newPassword);
                item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
                item.RefreshCredentialFields();
                item.CredentialNotice = message;
            }
            catch (Exception ex)
            {
                item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
                item.RefreshCredentialFields();
                item.CredentialNotice = "密码修改失败";
                MessageBox.Show($"修改 MySQL 密码失败：{ex.Message}", "修改 MySQL 密码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                item.IsBusy = false;
            }

            return;
        }

        var portText = ShowPortEditor(credentials.Port);
        if (portText is null)
        {
            return;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            MessageBox.Show("请输入 1 到 65535 之间的有效端口号。", "修改 MySQL 端口",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            item.SetBusyState("配置中", $"正在把 MySQL 服务端口修改为 {port}...");
            var message = await _runtimeService.ChangeMySqlPortAsync(port);
            item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
            item.RefreshCredentialFields();
            item.CredentialNotice = message;
        }
        catch (Exception ex)
        {
            item.ApplyRuntimeState(_runtimeService.GetState(item.Kind));
            item.RefreshCredentialFields();
            item.CredentialNotice = "端口修改失败";
            MessageBox.Show($"修改 MySQL 端口失败：{ex.Message}", "修改 MySQL 端口",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private string? ShowPortEditor(int currentPort)
    {
        var dialog = new Window
        {
            Title = "修改 MySQL 端口",
            Owner = this,
            Width = 360,
            Height = 210,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("SurfaceBrush")
        };

        var input = new TextBox
        {
            Text = currentPort.ToString(CultureInfo.InvariantCulture),
            FontSize = 16,
            Height = 38,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 5
        };

        var confirm = new Button
        {
            Content = "保存",
            Width = 88,
            Height = 34,
            Style = (Style)FindResource("PrimaryButton"),
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsCancel = true
        };
        confirm.Click += (_, _) =>
        {
            dialog.DialogResult = true;
            dialog.Close();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        var content = new Grid { Margin = new Thickness(24, 20, 24, 20) };
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "MySQL 默认端口",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        };
        var hint = new TextBlock
        {
            Text = "请输入面板连接信息中使用的端口号。",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        Grid.SetRow(title, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(input, 2);
        Grid.SetRow(buttons, 3);
        content.Children.Add(title);
        content.Children.Add(hint);
        content.Children.Add(input);
        content.Children.Add(buttons);
        dialog.Content = content;

        dialog.Loaded += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        return dialog.ShowDialog() == true ? input.Text.Trim() : null;
    }

    private string? ShowPasswordEditor()
    {
        var dialog = new Window
        {
            Title = "修改 MySQL 密码",
            Owner = this,
            Width = 390,
            Height = 286,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("SurfaceBrush")
        };

        var password = new PasswordBox
        {
            FontSize = 15,
            Height = 38,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 64
        };
        var confirmation = new PasswordBox
        {
            FontSize = 15,
            Height = 38,
            Padding = new Thickness(10, 5, 10, 5),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 64
        };

        var confirm = new Button
        {
            Content = "保存并应用",
            Width = 108,
            Height = 34,
            Style = (Style)FindResource("PrimaryButton"),
            IsDefault = true
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 88,
            Height = 34,
            Margin = new Thickness(10, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsCancel = true
        };
        confirm.Click += (_, _) =>
        {
            if (password.Password.Length is < 1 or > 64)
            {
                MessageBox.Show("密码不能为空，最多 64 个字符；纯数字密码也可以。", "修改 MySQL 密码",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                password.Focus();
                password.SelectAll();
                return;
            }

            if (password.Password.Any(char.IsControl))
            {
                MessageBox.Show("密码不能包含换行、制表符等控制字符。", "修改 MySQL 密码",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                password.Focus();
                password.SelectAll();
                return;
            }

            if (!string.Equals(password.Password, confirmation.Password, StringComparison.Ordinal))
            {
                MessageBox.Show("两次输入的密码不一致。", "修改 MySQL 密码",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                confirmation.Focus();
                confirmation.SelectAll();
                return;
            }

            dialog.DialogResult = true;
            dialog.Close();
        };

        var content = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        for (var i = 0; i < 6; i++)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "MySQL root 密码",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        };
        var hint = new TextBlock
        {
            Text = "新密码会立即应用到 MySQL，验证成功后才会保存到面板。",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var passwordLabel = new TextBlock
        {
            Text = "新密码",
            FontSize = 11,
            Margin = new Thickness(0, 0, 0, 4),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var confirmationLabel = new TextBlock
        {
            Text = "确认新密码",
            FontSize = 11,
            Margin = new Thickness(0, 10, 0, 4),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(confirm);
        buttons.Children.Add(cancel);

        Grid.SetRow(title, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(passwordLabel, 2);
        Grid.SetRow(password, 3);
        Grid.SetRow(confirmationLabel, 4);
        Grid.SetRow(confirmation, 5);
        Grid.SetRow(buttons, 7);
        content.Children.Add(title);
        content.Children.Add(hint);
        content.Children.Add(passwordLabel);
        content.Children.Add(password);
        content.Children.Add(confirmationLabel);
        content.Children.Add(confirmation);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => password.Focus();

        return dialog.ShowDialog() == true ? password.Password : null;
    }

    internal void CredentialConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentItem item)
        {
            return;
        }

        if (item.Kind == EnvironmentKind.MySql)
        {
            ConnectMySql_Click(sender, e);
        }
        else if (item.Kind == EnvironmentKind.SqlServer)
        {
            ConnectSqlServer_Click(sender, e);
        }
    }

    private static Border? FindAncestorBorder(DependencyObject source, string name)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border border && border.Name == name)
            {
                return border;
            }
        }

        return null;
    }

    private static Task AnimateScaleXAsync(ScaleTransform transform, double from, double to, TimeSpan duration)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            transform.ScaleX = to;
            completion.TrySetResult(true);
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        return completion.Task;
    }

    private string OpenIisManager()
    {
        _runtimeService.OpenIisManager();
        return "已打开 IIS 管理器。";
    }

    internal void NginxOpenManagement_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        if (!item.IsInstalled)
        {
            MessageBox.Show("请先安装 Nginx，再配置反向代理。", "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var dialog = new NginxProxyDialog(_runtimeService)
            {
                Owner = this
            };
            dialog.ApplyTheme(_isDarkThemeActive);

            if (dialog.ShowDialog() == true)
            {
                item.StatusText = dialog.ResultMessage;
                _model.RefreshEnvironmentStates(_runtimeService);
                _model.RefreshSuiteServices(_runtimeService, _frpManager.GetState());
                MessageBox.Show(dialog.ResultMessage, "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            item.StatusText = $"Nginx 管理失败：{ex.Message}";
            MessageBox.Show(item.StatusText, "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string RuntimeActionText(string action) => action switch
    {
        "Start" => "启动",
        "Stop" => "停止",
        "Restart" => "重启",
        "Uninstall" => "卸载",
        "CatalinaRun" => "打开 Catalina 诊断模式",
        _ => "处理"
    };

    private static string RuntimeBusyBadgeText(string action) => action switch
    {
        "Start" => "启动中",
        "Stop" => "停止中",
        "Restart" => "重启中",
        "Uninstall" => "卸载中",
        "CatalinaRun" => "诊断启动",
        _ => "处理中"
    };

    private void ShowEnvironmentOperationFailure(
        EnvironmentItem item,
        string operation,
        Exception exception,
        string dialogTitle = "环境")
    {
        var fallbackLog = EnvironmentOperationDiagnostics.RecordFailure(item.Title, operation, exception);
        var attachedLog = EnvironmentOperationDiagnostics.FindAttachedLogPath(exception);
        var logPath = !string.IsNullOrWhiteSpace(attachedLog) && File.Exists(attachedLog)
            ? attachedLog
            : fallbackLog;
        var fullMessage = $"{item.Title}{operation}失败：{exception.Message}";
        item.StatusText = CompactEnvironmentStatus(fullMessage);
        item.BadgeText = "失败";
        item.BadgeBrush = Brushes.IndianRed;
        ShowOperationFailureDialog(fullMessage, logPath, dialogTitle);
    }

    private void ShowServiceOperationFailure(ServiceItem service, string operation, Exception exception)
    {
        var fallbackLog = EnvironmentOperationDiagnostics.RecordFailure(service.Name, operation, exception);
        var attachedLog = EnvironmentOperationDiagnostics.FindAttachedLogPath(exception);
        var logPath = !string.IsNullOrWhiteSpace(attachedLog) && File.Exists(attachedLog)
            ? attachedLog
            : fallbackLog;
        var fullMessage = $"{service.Name}{operation}失败：{exception.Message}";
        service.StatusText = CompactEnvironmentStatus(fullMessage);
        service.BadgeText = "失败";
        service.BadgeBrush = Brushes.IndianRed;
        ShowOperationFailureDialog(fullMessage, logPath, "套件服务");
    }

    private void ShowOperationFailureDialog(string message, string? logPath, string title)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}完整日志：{logPath}{Environment.NewLine}{Environment.NewLine}是否立即打开日志？",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception openError)
        {
            MessageBox.Show($"无法打开日志：{openError.Message}{Environment.NewLine}{logPath}", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static string CompactEnvironmentStatus(string message)
    {
        var compact = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return compact.Length <= 420 ? compact : compact.Substring(0, 417) + "...";
    }

    internal async void FrpOpenManagement_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await _frpManager.EnsureManagementServerAsync();
            Process.Start(new ProcessStartInfo
            {
                FileName = _frpManager.ManagementUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var item = _model.EnvironmentItems.First(environment => environment.Kind == EnvironmentKind.FrpTunnel);
            ShowEnvironmentOperationFailure(item, "打开管理页面", ex, "FRP");
        }
    }

    internal async void ServiceAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ServiceItem service || sender is not Button button)
        {
            return;
        }

        var action = (string?)button.Tag;
        try
        {
            service.SetBusyState(RuntimeBusyBadgeText(action ?? string.Empty), $"{service.Name} 正在{RuntimeActionText(action ?? string.Empty)}...");
            if (service.Kind == EnvironmentKind.FrpTunnel)
            {
                if (action == "Start")
                {
                    await _frpManager.StartAsync();
                }
                else if (action == "Stop")
                {
                    await _frpManager.StopAsync();
                }
                else if (action == "Restart")
                {
                    await _frpManager.StopAsync();
                    await Task.Delay(800);
                    await _frpManager.StartAsync();
                }
            }
            else
            {
                _ = action switch
                {
                    "Start" => await _runtimeService.StartAsync(service.Kind),
                    "Stop" => await _runtimeService.StopAsync(service.Kind),
                    "Restart" => await _runtimeService.RestartAsync(service.Kind),
                    _ => string.Empty
                };
            }

            var frpState = _frpManager.GetState();
            _model.RefreshSuiteServices(_runtimeService, frpState);
            _model.UpdateFrpState(frpState);
        }
        catch (Exception ex)
        {
            ShowServiceOperationFailure(service, RuntimeActionText(action ?? string.Empty), ex);
            await RefreshRuntimeStatesAfterFailureAsync();
        }
        finally
        {
            service.IsBusy = false;
        }
    }
}

public sealed class DatabaseToolViewModel : ObservableObject
{
    private string _statusText = "未安装";
    private string _pathText = string.Empty;
    private string _matchedVersionText = string.Empty;
    private bool _isInstalled;
    private bool _isBusy;

    public DatabaseToolViewModel(string title, string description, string actionPrefix = "")
    {
        Title = title;
        Description = description;
        ActionPrefix = actionPrefix;
    }

    public string Title { get; }
    public string Description { get; }
    public string ActionPrefix { get; }
    public string PathActionTag => $"{ActionPrefix}Path";
    public string InstallActionTag => $"{ActionPrefix}Install";
    public string UninstallActionTag => $"{ActionPrefix}Uninstall";
    public string ConnectActionTag => $"{ActionPrefix}Connect";
    public string ConfigureActionTag => $"{ActionPrefix}Configure";
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }
    public string PathText { get => _pathText; private set => SetProperty(ref _pathText, value); }
    public string MatchedVersionText { get => _matchedVersionText; private set => SetProperty(ref _matchedVersionText, value); }

    public bool IsInstalled
    {
        get => _isInstalled;
        private set
        {
            if (!SetProperty(ref _isInstalled, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            OnPropertyChanged(nameof(CanConnect));
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetProperty(ref _isBusy, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanUninstall));
            OnPropertyChanged(nameof(CanConnect));
            OnPropertyChanged(nameof(CanConfigure));
            OnPropertyChanged(nameof(CanOpenPath));
        }
    }

    public bool CanInstall => !IsBusy && !IsInstalled;
    public bool CanUninstall => !IsBusy && IsInstalled;
    public bool CanConnect => !IsBusy && IsInstalled;
    public bool CanConfigure => !IsBusy;
    public bool CanOpenPath => !IsBusy;

    internal void Apply(DatabaseToolInstallation? installation, string managedInstallRoot, string? matchedVersion = null)
    {
        IsInstalled = installation is not null;
        MatchedVersionText = string.IsNullOrWhiteSpace(matchedVersion)
            ? string.Empty
            : $"（{matchedVersion}）";
        StatusText = installation is null
            ? "未安装"
            : $"已安装 · {installation.DisplayName}";
        if (installation is null)
        {
            PathText = $"未在桌面或默认安装位置检测到；面板安装目录：{managedInstallRoot}";
        }
        else if (installation.Source == DatabaseToolSource.Desktop &&
                 !string.Equals(installation.DiscoveryPath, installation.ExecutablePath, StringComparison.OrdinalIgnoreCase))
        {
            PathText = $"桌面快捷方式：{installation.DiscoveryPath}  →  {installation.ExecutablePath}";
        }
        else
        {
            PathText = $"{installation.SourceText}：{installation.ExecutablePath}";
        }

        IsBusy = false;
    }

    internal void SetBusy(bool busy)
    {
        IsBusy = busy;
    }

    internal void SetProgressMessage(string message)
    {
        StatusText = "处理中";
        PathText = message;
    }
}

public sealed class MainViewModel : ObservableObject
{
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly CpuSampler _cpuSampler = new();
    private readonly ProductCacheStore _productCacheStore = new();
    private readonly ProductIconCache _productIconCache = new();
    private readonly ProductInstallOrderStore _productInstallOrderStore = new();
    private double _cpuUsage;
    private double _memoryUsage;
    private double _targetCpuUsage;
    private double _targetMemoryUsage;
    private string _uptimeText = string.Empty;
    private string _productStoreStatus = "当前显示旧包内置产品清单；点击“刷新在线列表”可从 regservice.itmc.cn 获取客户可下载产品。";
    private string _settingsStatus = "设置会保存到当前软件目录，便于迁移和维护。";
    private string _storeDataRoot = string.Empty;
    private string _hardwareCpuText = "CPU：检测中";
    private string _hardwareMemoryText = "内存：检测中";
    private string _hardwareOsText = "系统：检测中";
    private bool _isStartupEnabled;
    private PanelThemeMode _themeMode = PanelThemeMode.System;
    private string _productCacheSizeText = "正在计算...";
    private string _updateStatus = "支持 HTTPS 在线更新与经过校验的本地更新包。";
    private double _updateProgress;
    private bool _isUpdateBusy;
    private string _productSearchKeyword = string.Empty;
    private string _selectedProductCategory = "全部";
    private int _visibleProductCount;
    private CancellationTokenSource? _productIconCacheCancellation;
    private CancellationTokenSource? _installedProductsRefreshCancellation;
    private bool _disposed;

    public MainViewModel()
    {
        SummaryCounters =
        [
            new SummaryCounter("网站", 0),
            new SummaryCounter("产品", 0),
            new SummaryCounter("数据库", 0)
        ];

        Services =
        [
            new ServiceItem("IIS Web 服务", EnvironmentKind.Iis, "Default Web Site / IIS 应用服务"),
            new ServiceItem("Nginx 反向代理", EnvironmentKind.Nginx, "本地反向代理与端口转发服务"),
            new ServiceItem("Tomcat Server", EnvironmentKind.Tomcat, "Java Web 应用运行服务"),
            new ServiceItem("MySQL 数据库", EnvironmentKind.MySql, $"127.0.0.1:{MySqlCredentialStore.DefaultPort} / root"),
            new ServiceItem("SQL Server", EnvironmentKind.SqlServer, "127.0.0.1,1433 / sa"),
            new ServiceItem("FRP 内网穿透", EnvironmentKind.FrpTunnel, "客户端映射与网页配置管理")
        ];

        EnvironmentItems =
        [
            new EnvironmentItem(EnvironmentKind.Iis, "Web Server", "服务器  IIS", "根据当前 Windows 版本启用对应 IIS 组件，并安装 URL Rewrite。"),
            new EnvironmentItem(EnvironmentKind.Nginx, "Nginx", "版本  Nginx 1.14.2", "从原版服务器下载 Nginx 定制包，解压并启动反向代理服务。"),
            new EnvironmentItem(
                EnvironmentKind.MySql,
                "MySql",
                $"版本  {MySqlReleaseCatalog.Default.DisplayName}",
                "选择版本后从 MySQL 官网下载 Windows x64 ZIP，初始化 root 密码并注册服务。"),
            new EnvironmentItem(
                EnvironmentKind.SqlServer,
                "SQLServer",
                $"版本  {SqlServerReleaseCatalog.Recommended.DisplayName}",
                "选择 Express 或 Enterprise Developer 版本后下载对应官方 SQL Server 安装包；默认按当前 Windows 版本推荐。"),
            new EnvironmentItem(EnvironmentKind.Tomcat, "Tomcat Server", "Tomcat 8.5.57", "从原版服务器下载定制 Tomcat，自动解压并配置启动脚本。"),
            new EnvironmentItem(EnvironmentKind.FrpTunnel, "FRP 内网穿透", "frp 0.71.0 windows amd64", "点击安装后才会联网下载 FRP 客户端；安装完成后可配置服务器连接、代理映射并单独启动或停止 frpc。")
            {
                StatusText = "FRP 尚未安装。点击“安装”后才会联网下载客户端。"
            }
        ];

        Products =
        [
            new ProductItem("DS0102", "网店运营推广-初级评价系统", "初级", "/Assets/Logo/DS0102.png", ProductSource.Local),
            new ProductItem("YX030104", "网店运营推广-初级实训系统", "初级", "/Assets/Logo/YX030104.png", ProductSource.Local),
            new ProductItem("DS0103", "网店运营推广-高级评价系统", "高级", "/Assets/Logo/DS0103.png", ProductSource.Local),
            new ProductItem("YX030106", "网店运营推广-高级实训系统", "高级", "/Assets/Logo/YX030106.png", ProductSource.Local),
            new ProductItem("QT0501", "网店运营推广-考务系统", "考务", "/Assets/Logo/QT0501.png", ProductSource.Local),
            new ProductItem("DS3102", "网店运营推广-中级评价系统", "中级", "/Assets/Logo/DS3102.png", ProductSource.Local),
            new ProductItem("DS3107", "网店运营推广-中级实训系统", "中级", "/Assets/Logo/DS3107.png", ProductSource.Local)
        ];

        RefreshProductCategories();
        ApplyProductFilter(string.Empty);
        LoadCachedProducts();
        LoadHardwareSummary();
    }

    public string WelcomeText => "欢迎：admin";
    public string VersionText => ApplicationUpdateService.CurrentVersionText;
    public ObservableCollection<SummaryCounter> SummaryCounters { get; }
    public ObservableCollection<ServiceItem> Services { get; }
    public ObservableCollection<EnvironmentItem> EnvironmentItems { get; }
    public ObservableCollection<ProductItem> Products { get; }
    public ObservableCollection<InstalledProductItem> InstalledProducts { get; } = [];
    public ObservableCollection<CustomWebsiteItem> CustomWebsites { get; } = [];
    public ObservableCollection<ProductRow> VisibleProductRows { get; } = [];
    public ObservableCollection<ProductCategoryFilter> ProductCategories { get; } = [];
    public ObservableCollection<DriveItem> Drives { get; } = [];
    public InstallationProgressViewModel InstallationProgress { get; } = new();
    public DatabaseToolViewModel NavicatTool { get; } = new(
        "MySQL 连接工具",
        "Navicat Premium Lite / Premium / for MySQL",
        "Navicat");
    public DatabaseToolViewModel SqlServerTool { get; } = new(
        "SQL Server 连接工具",
        "SQL Server Management Studio（SSMS）",
        "Ssms");
    public string ProductCountText => _visibleProductCount == Products.Count
        ? $"{Products.Count} 个产品"
        : $"{_visibleProductCount} / {Products.Count} 个产品";
    public string InstalledProductCountText => $"{InstalledProducts.Count + CustomWebsites.Count} 个网站";
    public string InstalledProductStatus => InstalledProducts.Count + CustomWebsites.Count == 0
        ? "当前没有网站。可以新建 IIS 网站，或先在“产品管理”中安装产品。"
        : "集中管理产品网站与自定义 IIS 网站，包括域名、SSL、绑定和运行状态。";
    public Visibility WebsiteEmptyVisibility => InstalledProducts.Count + CustomWebsites.Count == 0
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility WebsiteContentVisibility => InstalledProducts.Count + CustomWebsites.Count == 0
        ? Visibility.Collapsed
        : Visibility.Visible;

    public string HardwareCpuText { get => _hardwareCpuText; private set => SetProperty(ref _hardwareCpuText, value); }
    public string HardwareMemoryText { get => _hardwareMemoryText; private set => SetProperty(ref _hardwareMemoryText, value); }
    public string HardwareOsText { get => _hardwareOsText; private set => SetProperty(ref _hardwareOsText, value); }
    public string UptimeText { get => _uptimeText; private set => SetProperty(ref _uptimeText, value); }
    public double CpuUsage { get => _cpuUsage; private set => SetProperty(ref _cpuUsage, value); }
    public double MemoryUsage { get => _memoryUsage; private set => SetProperty(ref _memoryUsage, value); }
    public string ProductStoreStatus { get => _productStoreStatus; set => SetProperty(ref _productStoreStatus, value); }
    public string SettingsStatus { get => _settingsStatus; set => SetProperty(ref _settingsStatus, value); }
    public string StoreDataRoot { get => _storeDataRoot; set => SetProperty(ref _storeDataRoot, value); }
    public bool IsStartupEnabled { get => _isStartupEnabled; set => SetProperty(ref _isStartupEnabled, value); }
    public string ProductCacheSizeText { get => _productCacheSizeText; set => SetProperty(ref _productCacheSizeText, value); }
    public string UpdateStatus { get => _updateStatus; set => SetProperty(ref _updateStatus, value); }
    public double UpdateProgress { get => _updateProgress; set => SetProperty(ref _updateProgress, value); }
    public bool IsUpdateBusy
    {
        get => _isUpdateBusy;
        set
        {
            if (!SetProperty(ref _isUpdateBusy, value)) return;
            OnPropertyChanged(nameof(CanStartUpdate));
            OnPropertyChanged(nameof(CanCancelUpdate));
        }
    }
    public bool CanStartUpdate => !IsUpdateBusy;
    public bool CanCancelUpdate => IsUpdateBusy;
    public PanelThemeMode ThemeMode
    {
        get => _themeMode;
        set
        {
            if (!SetProperty(ref _themeMode, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsThemeSystemSelected));
            OnPropertyChanged(nameof(IsThemeLightSelected));
            OnPropertyChanged(nameof(IsThemeDarkSelected));
        }
    }

    public bool IsThemeSystemSelected => ThemeMode == PanelThemeMode.System;
    public bool IsThemeLightSelected => ThemeMode == PanelThemeMode.Light;
    public bool IsThemeDarkSelected => ThemeMode == PanelThemeMode.Dark;

    public void ApplyProductFilter(string? keyword)
    {
        _productSearchKeyword = (keyword ?? string.Empty).Trim();
        RebuildVisibleProductRows();
    }

    public void SelectProductCategory(string category)
    {
        _selectedProductCategory = string.IsNullOrWhiteSpace(category) ? "全部" : category;
        foreach (var item in ProductCategories)
        {
            item.IsSelected = item.Key.Equals(_selectedProductCategory, StringComparison.Ordinal);
        }

        RebuildVisibleProductRows();
    }

    private void RebuildVisibleProductRows()
    {
        var matches = Products
            .Select((product, catalogIndex) => new { Product = product, CatalogIndex = catalogIndex })
            .Where(item =>
                (_selectedProductCategory == "全部" || item.Product.Category.Equals(_selectedProductCategory, StringComparison.Ordinal)) &&
                (_productSearchKeyword.Length == 0 ||
                 item.Product.Name.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                 item.Product.DisplayName.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                 item.Product.Level.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                 item.Product.ProductId.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                 item.Product.Category.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase) ||
                 item.Product.EnvironmentSummary.Contains(_productSearchKeyword, StringComparison.OrdinalIgnoreCase)))
            .OrderBy(item => item.Product.IsInstalled ? 0 : 1)
            .ThenBy(item => item.Product.IsInstalled ? item.Product.InstallSequence : int.MaxValue)
            .ThenBy(item => item.CatalogIndex)
            .Select(item => item.Product)
            .ToList();

        VisibleProductRows.Clear();
        for (var index = 0; index < matches.Count; index += 3)
        {
            VisibleProductRows.Add(new ProductRow(matches.Skip(index).Take(3).ToArray()));
        }

        _visibleProductCount = matches.Count;
        OnPropertyChanged(nameof(ProductCountText));
    }

    private void RefreshProductCategories()
    {
        var counts = Products
            .GroupBy(product => product.Category, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);

        var orderedCategories = ProductItem.CategoryOrder
            .Where(category => counts.ContainsKey(category))
            .Concat(counts.Keys.Where(category => !ProductItem.CategoryOrder.Contains(category, StringComparer.Ordinal))
                .OrderBy(category => category, StringComparer.CurrentCulture))
            .ToList();

        if (_selectedProductCategory != "全部" && !counts.ContainsKey(_selectedProductCategory))
        {
            _selectedProductCategory = "全部";
        }

        ProductCategories.Clear();
        ProductCategories.Add(new ProductCategoryFilter("全部", "全部", Products.Count, _selectedProductCategory == "全部"));
        foreach (var category in orderedCategories)
        {
            ProductCategories.Add(new ProductCategoryFilter(category, category, counts[category], category == _selectedProductCategory));
        }
    }

    private void LoadHardwareSummary()
    {
        HardwareCpuText = $"CPU：{SystemHardware.GetCpuName()} / {Environment.ProcessorCount} 逻辑处理器";
        HardwareMemoryText = $"内存：{SystemMemory.GetTotalMemoryText()}";
        HardwareOsText = $"系统：{SystemHardware.GetOsText()}";
    }

    public async Task ReplaceProductsAsync(IReadOnlyCollection<ProductItem> products)
    {
        var snapshot = products.ToArray();
        ProductStoreStatus = $"已获取 {snapshot.Length} 个产品，正在加载产品列表...";
        Products.Clear();
        foreach (var product in snapshot)
        {
            Products.Add(product);
        }

        RefreshProductCategories();
        ApplyProductFilter(_productSearchKeyword);
        _productCacheStore.Save(snapshot);
        await RefreshInstalledProductsAsync();
        StartProductIconCaching(snapshot, $"已获取 {snapshot.Length} 个产品");
    }

    public void UpdateFrpState(EnvironmentRuntimeState state)
    {
        var frp = EnvironmentItems.FirstOrDefault(item => item.Kind == EnvironmentKind.FrpTunnel);
        if (frp is null)
        {
            return;
        }

        frp.ApplyRuntimeState(state);
    }

    public void RefreshEnvironmentStates(EnvironmentRuntimeService runtimeService, bool preserveBusy = false)
    {
        foreach (var item in EnvironmentItems.Where(item => item.Kind != EnvironmentKind.FrpTunnel))
        {
            if (preserveBusy && item.IsBusy)
            {
                continue;
            }

            item.ApplyRuntimeState(runtimeService.GetState(item.Kind));
        }

        foreach (var item in EnvironmentItems)
        {
            item.InstallDirectory = runtimeService.GetInstallDirectory(item.Kind);
        }
    }

    public void RefreshSuiteServices(
        EnvironmentRuntimeService runtimeService,
        EnvironmentRuntimeState frpState,
        bool preserveBusy = false)
    {
        foreach (var service in Services)
        {
            if (preserveBusy && service.IsBusy)
            {
                continue;
            }

            if (service.Kind == EnvironmentKind.FrpTunnel)
            {
                service.ApplyRuntimeState(frpState);
                continue;
            }

            service.ApplyRuntimeState(runtimeService.GetState(service.Kind));
        }
    }

    public void ApplyRuntimeStates(
        IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> states,
        EnvironmentRuntimeState frpState,
        IReadOnlyDictionary<EnvironmentKind, string?> installDirectories,
        bool preserveBusy = false)
    {
        foreach (var item in EnvironmentItems)
        {
            if (preserveBusy && item.IsBusy)
            {
                continue;
            }

            if (item.Kind == EnvironmentKind.FrpTunnel)
            {
                item.ApplyRuntimeState(frpState);
            }
            else if (states.TryGetValue(item.Kind, out var state))
            {
                item.ApplyRuntimeState(state);
            }

            item.InstallDirectory = installDirectories.TryGetValue(item.Kind, out var directory) ? directory : null;
        }

        foreach (var service in Services)
        {
            if (preserveBusy && service.IsBusy)
            {
                continue;
            }

            if (service.Kind == EnvironmentKind.FrpTunnel)
            {
                service.ApplyRuntimeState(frpState);
            }
            else if (states.TryGetValue(service.Kind, out var state))
            {
                service.ApplyRuntimeState(state);
            }
        }
    }

    private void LoadCachedProducts()
    {
        var cached = _productCacheStore.Load();
        if (cached.Count == 0)
        {
            return;
        }

        Products.Clear();
        foreach (var product in cached)
        {
            Products.Add(product);
        }

        RefreshProductCategories();
        ApplyProductFilter(_productSearchKeyword);
        _productCacheStore.Save(Products);
        var cacheMessage = $"已加载本地缓存的在线产品库，共 {cached.Count} 个产品；点击“刷新在线列表”可更新";
        ProductStoreStatus = cacheMessage + "。";
        RefreshInstalledProducts();
        StartProductIconCaching(cached.ToArray(), cacheMessage);
    }

    public void RefreshInstalledProducts()
    {
        _ = RefreshInstalledProductsAsync();
    }

    public async Task RefreshInstalledProductsAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed)
        {
            return;
        }

        var refreshCancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _installedProductsRefreshCancellation, refreshCancellation);
        previous?.Cancel();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            refreshCancellation.Token);
        var products = Products.ToArray();
        try
        {
            var installedProducts = await Task.Run(
                () => DetectInstalledProducts(products, linkedCancellation.Token),
                linkedCancellation.Token);
            linkedCancellation.Token.ThrowIfCancellationRequested();
            if (_disposed || !ReferenceEquals(_installedProductsRefreshCancellation, refreshCancellation))
            {
                return;
            }

            var installedById = installedProducts.ToDictionary(
                item => item.Product.ProductId,
                StringComparer.OrdinalIgnoreCase);
            foreach (var product in Products)
            {
                product.IsInstalled = installedById.ContainsKey(product.ProductId);
            }

            ApplyInstalledProducts(installedProducts);
        }
        catch (OperationCanceledException) when (linkedCancellation.IsCancellationRequested)
        {
            // A newer refresh owns the visible product list.
        }
        finally
        {
            if (ReferenceEquals(_installedProductsRefreshCancellation, refreshCancellation))
            {
                _installedProductsRefreshCancellation = null;
            }

            refreshCancellation.Dispose();
        }
    }

    private static List<(ProductItem Product, string InstallPath, DateTime InstalledAtUtc)> DetectInstalledProducts(
        IReadOnlyCollection<ProductItem> products,
        CancellationToken cancellationToken)
    {
        var installedProducts = new List<(ProductItem Product, string InstallPath, DateTime InstalledAtUtc)>();
        foreach (var product in products)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? installPath = null;
            try
            {
                var candidate = ProductInstallPathResolver.ResolveProductDirectory(product);
                if (Directory.Exists(candidate) && Directory.EnumerateFiles(candidate, "*", SearchOption.AllDirectories).Any())
                {
                    installPath = candidate;
                }
            }
            catch
            {
                // Ignore a product directory that is being installed, removed, or temporarily inaccessible.
            }

            if (installPath is not null && HasProductDeploymentBinding(product.ProductId))
            {
                installedProducts.Add((product, installPath, GetDirectoryCreationTimeUtc(installPath)));
            }
        }

        return installedProducts;
    }

    private void ApplyInstalledProducts(
        IReadOnlyCollection<(ProductItem Product, string InstallPath, DateTime InstalledAtUtc)> installedProducts)
    {
        _productInstallOrderStore.EnsureInstalled(installedProducts.Select(item =>
            new ProductInstallOrderCandidate(item.Product.ProductId, item.InstalledAtUtc)));
        foreach (var product in Products)
        {
            product.InstallSequence = product.IsInstalled
                ? _productInstallOrderStore.GetSequence(product.ProductId)
                : int.MaxValue;
        }

        InstalledProducts.Clear();
        foreach (var item in installedProducts.OrderBy(item => item.Product.InstallSequence))
        {
            InstalledProducts.Add(new InstalledProductItem(item.Product, item.InstallPath));
        }

        OnPropertyChanged(nameof(InstalledProductCountText));
        OnPropertyChanged(nameof(InstalledProductStatus));
        OnPropertyChanged(nameof(WebsiteEmptyVisibility));
        OnPropertyChanged(nameof(WebsiteContentVisibility));
        RebuildVisibleProductRows();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _installedProductsRefreshCancellation?.Cancel();
        _productIconCacheCancellation?.Cancel();
        _productIconCache.Dispose();
    }

    public void RefreshCustomWebsites(IReadOnlyList<CustomWebsiteDefinition> definitions)
    {
        CustomWebsites.Clear();
        foreach (var definition in definitions)
        {
            CustomWebsites.Add(new CustomWebsiteItem(definition));
        }

        OnPropertyChanged(nameof(InstalledProductCountText));
        OnPropertyChanged(nameof(InstalledProductStatus));
        OnPropertyChanged(nameof(WebsiteEmptyVisibility));
        OnPropertyChanged(nameof(WebsiteContentVisibility));
    }

    public void RecordProductInstalled(string productId)
    {
        _productInstallOrderStore.RecordInstalled(productId);
    }

    public void RecordProductUninstalled(string productId)
    {
        _productInstallOrderStore.Remove(productId);
    }

    private static DateTime GetDirectoryCreationTimeUtc(string directory)
    {
        try
        {
            var creationTime = Directory.GetCreationTimeUtc(directory);
            return creationTime.Year > 1970 ? creationTime : Directory.GetLastWriteTimeUtc(directory);
        }
        catch
        {
            return DateTime.UtcNow;
        }
    }

    private static bool HasProductDeploymentBinding(string productId) =>
        ProductDeploymentService.LoadTomcatDeploymentInfo(productId) is not null ||
        ProductDeploymentService.LoadIisDeploymentInfo(productId) is not null;

    private void StartProductIconCaching(IReadOnlyCollection<ProductItem> products, string loadedMessage)
    {
        if (_disposed)
        {
            return;
        }

        _productIconCacheCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _productIconCacheCancellation = cancellation;
        _ = CacheProductIconsAsync(products, loadedMessage, cancellation);
    }

    private async Task CacheProductIconsAsync(
        IReadOnlyCollection<ProductItem> products,
        string loadedMessage,
        CancellationTokenSource cancellation)
    {
        try
        {
            var remoteIconCount = products.Count(product => ProductIconCache.IsCacheableRemoteIcon(product.RemoteIconUrl ?? product.IconPath));
            if (remoteIconCount == 0)
            {
                if (!_disposed && ReferenceEquals(_productIconCacheCancellation, cancellation))
                {
                    ProductStoreStatus = loadedMessage + "。";
                }

                return;
            }

            ProductStoreStatus = $"{loadedMessage}，正在后台缓存 {remoteIconCount} 张产品图标...";
            var cachedCount = await _productIconCache.CacheIconsAsync(products, cancellation.Token);
            if (_disposed || !ReferenceEquals(_productIconCacheCancellation, cancellation))
            {
                return;
            }

            _productCacheStore.Save(products);
            ProductStoreStatus = cachedCount > 0
                ? $"{loadedMessage}；已缓存 {cachedCount} 张产品图标。"
                : $"{loadedMessage}；产品图标缓存未成功，产品列表仍可正常使用。";
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // A newer product refresh owns the visible list and its icon cache task.
        }
        catch (Exception ex)
        {
            if (!_disposed && ReferenceEquals(_productIconCacheCancellation, cancellation))
            {
                ProductStoreStatus = $"{loadedMessage}；产品图标缓存失败：{ex.Message}，产品列表仍可正常使用。";
            }
        }
        finally
        {
            if (ReferenceEquals(_productIconCacheCancellation, cancellation))
            {
                _productIconCacheCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    public void RefreshSystemState()
    {
        var span = DateTime.Now - _startedAt;
        UptimeText = $"已不间断运行：{span.Days}天{span.Hours:D2}时{span.Minutes:D2}分{span.Seconds:D2}秒";
        _targetCpuUsage = Compat.Clamp(_cpuSampler.NextValue(), 0, 100);
        _targetMemoryUsage = Compat.Clamp(SystemMemory.GetMemoryUsagePercent(), 0, 100);

        try
        {
            var snapshots = new List<(string Name, long Used, long Total)>();
            var hadReadFailure = false;
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                    {
                        continue;
                    }

                    var total = drive.TotalSize;
                    var used = total - drive.AvailableFreeSpace;
                    snapshots.Add((drive.Name, used, total));
                    if (snapshots.Count == 6)
                    {
                        break;
                    }
                }
                catch (IOException)
                {
                    hadReadFailure = true;
                    // A removable or temporarily unavailable drive should not
                    // terminate the DispatcherTimer callback.
                }
                catch (UnauthorizedAccessException)
                {
                    hadReadFailure = true;
                }
            }

            foreach (var snapshot in snapshots)
            {
                var existing = Drives.FirstOrDefault(item => item.Name.Equals(snapshot.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null)
                {
                    Drives.Add(new DriveItem(snapshot.Name, snapshot.Used, snapshot.Total));
                }
                else
                {
                    existing.UpdateTarget(snapshot.Used, snapshot.Total);
                }
            }

            if (!hadReadFailure)
            {
                var activeNames = snapshots
                    .Select(snapshot => snapshot.Name)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var index = Drives.Count - 1; index >= 0; index--)
                {
                    if (!activeNames.Contains(Drives[index].Name))
                    {
                        Drives.RemoveAt(index);
                    }
                }
            }
        }
        catch (IOException)
        {
            // Keep the previous drive snapshot when the system changes while
            // the timer is reading it.
        }
        catch (UnauthorizedAccessException)
        {
        }

        CpuUsage = _targetCpuUsage;
        MemoryUsage = _targetMemoryUsage;
        foreach (var drive in Drives)
        {
            drive.SnapToTarget();
        }

    }

    public void TickSystemState()
    {
        RefreshSystemState();
    }
}

public sealed class SummaryCounter(string name, int count)
{
    public string Name { get; } = name;
    public int Count { get; } = count;
    public string CountText => $"{Count}个";
}

public sealed class ServiceItem(string name, EnvironmentKind kind, string description) : ObservableObject
{
    private bool _isInstalled;
    private bool _isRunning;
    private bool _isBusy;
    private RuntimeStatusKind _statusKind = RuntimeStatusKind.NotInstalled;
    private string _statusText = "检测中";
    private string _badgeText = "检测中";
    private Brush _badgeBrush = Brushes.Gray;

    public string Name { get; } = name;
    public EnvironmentKind Kind { get; } = kind;
    public string Description { get; } = description;

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanRestart));
                OnPropertyChanged(nameof(CanUninstall));
                OnPropertyChanged(nameof(ServiceControlsVisibility));
                OnPropertyChanged(nameof(InstallHintVisibility));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanRestart));
                OnPropertyChanged(nameof(CanUninstall));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanRestart));
                OnPropertyChanged(nameof(CanUninstall));
            }
        }
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string BadgeText { get => _badgeText; set => SetProperty(ref _badgeText, value); }
    public Brush BadgeBrush { get => _badgeBrush; set => SetProperty(ref _badgeBrush, value); }

    public bool CanStart => IsInstalled && !IsRunning && !IsBusy && _statusKind != RuntimeStatusKind.Unknown;
    public bool CanStop => IsInstalled && IsRunning && !IsBusy;
    public bool CanRestart => IsInstalled && !IsBusy && _statusKind != RuntimeStatusKind.Unknown;
    public bool CanUninstall => IsInstalled && !IsBusy;
    public Visibility ServiceControlsVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility InstallHintVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;

    public void ApplyRuntimeState(EnvironmentRuntimeState state)
    {
        _statusKind = state.StatusKind;
        IsInstalled = state.IsInstalled;
        IsRunning = state.IsRunning;
        StatusText = state.IsInstalled
            ? state.IsRunning ? "运行中" : state.StatusKind is RuntimeStatusKind.Starting or RuntimeStatusKind.Stopping ? RuntimeStatusVisuals.Text(state.StatusKind) : "已安装，未运行"
            : "未安装";
        BadgeText = RuntimeStatusVisuals.Text(state.StatusKind);
        BadgeBrush = RuntimeStatusVisuals.Brush(state.StatusKind);
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(ServiceControlsVisibility));
        OnPropertyChanged(nameof(InstallHintVisibility));
    }

    public void SetBusyState(string badgeText, string statusText)
    {
        _statusKind = RuntimeStatusKind.Starting;
        IsBusy = true;
        StatusText = statusText;
        BadgeText = badgeText;
        BadgeBrush = RuntimeStatusVisuals.Brush(RuntimeStatusKind.Starting);
    }
}

public enum EnvironmentKind
{
    Iis,
    Nginx,
    MySql,
    SqlServer,
    Tomcat,
    FrpTunnel
}

public sealed class EnvironmentItem(EnvironmentKind kind, string title, string optionLabel, string description) : ObservableObject
{
    private bool _isInstalled;
    private bool _isRunning;
    private bool _isBusy;
    private double _progress;
    private RuntimeStatusKind _statusKind = RuntimeStatusKind.NotInstalled;
    private string _statusText = "等待安装";
    private string _badgeText = "未安装";
    private Brush _badgeBrush = Brushes.Gray;
    private bool _isFlipped;
    private bool _isFlipAnimating;
    private bool _installRestartRequired;
    private bool _iisUninstallRestartRequired;
    private string _credentialNotice = string.Empty;
    private string _tomcatPortNotice = string.Empty;
    private string? _installDirectory;
    private string _selectedMySqlReleaseId = MySqlReleaseCatalog.Default.Id;
    private string? _detectedMySqlReleaseId;
    private string? _detectedMySqlVersion;
    private string _selectedSqlServerReleaseId = SqlServerReleaseCatalog.Recommended.Id;
    private string? _detectedSqlServerReleaseId;
    private string? _detectedSqlServerDisplayName;
    private InstallProgressStage _progressStage = InstallProgressStage.Preparing;
    private double? _stagePercent;
    private double _lastDownloadOverallPercent;
    private string _progressStageText = "状态";
    private string _downloadSpeedText = string.Empty;
    private readonly string _optionLabel = optionLabel;

    public EnvironmentKind Kind { get; } = kind;
    public string Title { get; } = title;
    public string OptionLabel => IsMySqlModule
        ? $"版本  {MySqlDisplayName}"
        : IsSqlServerModule
            ? $"版本  {SqlServerDisplayName}"
            : _optionLabel;
    public string Description { get; } = description;
    public string? InstallDirectory
    {
        get => _installDirectory;
        set
        {
            if (SetProperty(ref _installDirectory, value))
            {
                OnPropertyChanged(nameof(InstallDirectoryVisibility));
            }
        }
    }
    public Visibility InstallDirectoryVisibility => IsInstalled && !string.IsNullOrWhiteSpace(InstallDirectory)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public IReadOnlyList<MySqlReleaseDefinition> MySqlReleaseOptions => MySqlReleaseCatalog.Options;
    public string SelectedMySqlReleaseId
    {
        get => _selectedMySqlReleaseId;
        set
        {
            var normalized = MySqlReleaseCatalog.Contains(value)
                ? value
                : MySqlReleaseCatalog.Default.Id;
            if (SetProperty(ref _selectedMySqlReleaseId, normalized))
            {
                OnPropertyChanged(nameof(SelectedMySqlRelease));
                OnPropertyChanged(nameof(SelectedInstallReleaseId));
                OnPropertyChanged(nameof(OptionLabel));
            }
        }
    }
    public MySqlReleaseDefinition SelectedMySqlRelease =>
        MySqlReleaseCatalog.Resolve(SelectedMySqlReleaseId, EnvironmentDownloadSettings.DefaultMySqlPackageUrl);
    private string MySqlDisplayName =>
        MySqlReleaseCatalog.Contains(_detectedMySqlReleaseId)
            ? SelectedMySqlRelease.DisplayName
            : !string.IsNullOrWhiteSpace(_detectedMySqlVersion)
                ? $"MySQL {_detectedMySqlVersion}"
                : SelectedMySqlRelease.DisplayName;
    public IReadOnlyList<SqlServerReleaseDefinition> SqlServerReleaseOptions => SqlServerReleaseCatalog.Options;
    public string SelectedSqlServerReleaseId
    {
        get => _selectedSqlServerReleaseId;
        set
        {
            var normalized = SqlServerReleaseCatalog.Contains(value)
                ? value
                : SqlServerReleaseCatalog.Recommended.Id;
            if (SetProperty(ref _selectedSqlServerReleaseId, normalized))
            {
                OnPropertyChanged(nameof(SelectedSqlServerRelease));
                OnPropertyChanged(nameof(SelectedInstallReleaseId));
                OnPropertyChanged(nameof(OptionLabel));
            }
        }
    }
    public SqlServerReleaseDefinition SelectedSqlServerRelease =>
        SqlServerReleaseCatalog.Resolve(SelectedSqlServerReleaseId);
    private string SqlServerDisplayName
    {
        get
        {
            if (SqlServerReleaseCatalog.Contains(_detectedSqlServerReleaseId))
            {
                return SelectedSqlServerRelease.DisplayName;
            }

            return string.IsNullOrWhiteSpace(_detectedSqlServerDisplayName)
                ? SelectedSqlServerRelease.DisplayName
                : _detectedSqlServerDisplayName!;
        }
    }
    public string? SelectedInstallReleaseId => IsMySqlModule
        ? SelectedMySqlReleaseId
        : IsSqlServerModule ? SelectedSqlServerReleaseId : null;
    public string ActionText => IsInstalled
        ? "已安装"
        : IsBusy ? "安装中" : _iisUninstallRestartRequired ? "继续卸载" : _installRestartRequired ? "继续安装" : "安装";
    public bool CanInstall => !IsBusy;
    public bool CanSelectMySqlVersion => IsMySqlModule && !IsInstalled && !IsBusy;
    public bool CanSelectSqlServerVersion => IsSqlServerModule && !IsInstalled && !IsBusy;
    public bool IsFrpModule => Kind == EnvironmentKind.FrpTunnel;
    public bool IsIisModule => Kind == EnvironmentKind.Iis;
    public bool IsNginxModule => Kind == EnvironmentKind.Nginx;
    public bool IsTomcatModule => Kind == EnvironmentKind.Tomcat;
    public bool IsMySqlModule => Kind == EnvironmentKind.MySql;
    public bool IsSqlServerModule => Kind == EnvironmentKind.SqlServer;
    public bool IsCredentialModule => Kind is EnvironmentKind.MySql or EnvironmentKind.SqlServer;
    public bool CanFlipCard => IsCredentialModule || IsTomcatModule;
    public Visibility InstallButtonVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility MySqlVersionSelectorVisibility => CanSelectMySqlVersion ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SqlServerVersionSelectorVisibility => CanSelectSqlServerVersion ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ServiceControlsVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility MySqlConnectButtonVisibility => IsInstalled && Kind == EnvironmentKind.MySql ? Visibility.Visible : Visibility.Collapsed;
    public Visibility SqlServerConnectButtonVisibility => IsInstalled && Kind == EnvironmentKind.SqlServer ? Visibility.Visible : Visibility.Collapsed;
    public Visibility EnvironmentFlipVisibility => IsInstalled && CanFlipCard ? Visibility.Visible : Visibility.Collapsed;
    public Visibility IisManagementVisibility => IsInstalled && IsIisModule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NginxManagementVisibility => IsInstalled && IsNginxModule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FrpManagementVisibility => IsInstalled && IsFrpModule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility TomcatCatalinaVisibility => IsInstalled && IsTomcatModule ? Visibility.Visible : Visibility.Collapsed;
    public Visibility FrontFaceVisibility => IsFlipped ? Visibility.Hidden : Visibility.Visible;
    public Visibility CredentialBackFaceVisibility => IsFlipped && IsCredentialModule ? Visibility.Visible : Visibility.Hidden;
    public Visibility TomcatBackFaceVisibility => IsFlipped && IsTomcatModule ? Visibility.Visible : Visibility.Hidden;
    public string CredentialTitle => Kind switch
    {
        EnvironmentKind.MySql => "MySQL 连接信息",
        EnvironmentKind.SqlServer => "SQL Server 连接信息",
        _ => "连接信息"
    };
    public string CredentialSubtitle => Kind == EnvironmentKind.SqlServer
        ? $"本机默认连接参数 · 实例：{SqlServerCredentialStore.Load().InstanceName}"
        : "本机默认连接参数";
    public IReadOnlyList<CredentialField> CredentialFields => LoadCredentialFields();
    public string CredentialNotice { get => _credentialNotice; set => SetProperty(ref _credentialNotice, value); }
    public ObservableCollection<TomcatPortItem> TomcatPorts { get; } = [];
    public string TomcatPortNotice { get => _tomcatPortNotice; set => SetProperty(ref _tomcatPortNotice, value); }
    public string TomcatPortSubtitle => TomcatPorts.Count == 0
        ? "当前没有已部署的 Tomcat 产品"
        : $"{TomcatPorts.Count} 个产品 · 修改前请先停止对应应用";

    public void RefreshTomcatPorts()
    {
        TomcatPorts.Clear();
        foreach (var deployment in ProductDeploymentService.LoadTomcatDeploymentInfos())
        {
            TomcatPorts.Add(new TomcatPortItem(deployment));
        }
        TomcatPortNotice = string.Empty;
        OnPropertyChanged(nameof(TomcatPortSubtitle));
    }

    public void RefreshCredentialFields()
    {
        OnPropertyChanged(nameof(CredentialFields));
        OnPropertyChanged(nameof(CredentialSubtitle));
    }

    public bool IsFlipped
    {
        get => _isFlipped;
        set
        {
            if (SetProperty(ref _isFlipped, value))
            {
                CredentialNotice = string.Empty;
                TomcatPortNotice = string.Empty;
                OnPropertyChanged(nameof(FrontFaceVisibility));
                OnPropertyChanged(nameof(CredentialBackFaceVisibility));
                OnPropertyChanged(nameof(TomcatBackFaceVisibility));
                OnPropertyChanged(nameof(CredentialFields));
            }
        }
    }

    public bool IsFlipAnimating
    {
        get => _isFlipAnimating;
        set => SetProperty(ref _isFlipAnimating, value);
    }

    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }
    public string BadgeText { get => _badgeText; set => SetProperty(ref _badgeText, value); }
    public Brush BadgeBrush { get => _badgeBrush; set => SetProperty(ref _badgeBrush, value); }
    public double Progress { get => _progress; set => SetProperty(ref _progress, value); }
    public string ProgressStageText { get => _progressStageText; private set => SetProperty(ref _progressStageText, value); }
    public string DownloadSpeedText
    {
        get => _downloadSpeedText;
        private set
        {
            if (SetProperty(ref _downloadSpeedText, value))
            {
                OnPropertyChanged(nameof(DownloadSpeedVisibility));
            }
        }
    }
    public Visibility DownloadSpeedVisibility => string.IsNullOrWhiteSpace(DownloadSpeedText)
        ? Visibility.Collapsed
        : Visibility.Visible;
    public bool IsProgressIndeterminate => IsBusy &&
        _progressStage == InstallProgressStage.Downloading &&
        !_stagePercent.HasValue;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseStateProperties();
            }
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                if (!value)
                {
                    IsFlipped = false;
                }
                RaiseStateProperties();
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStart));
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(CanRestart));
                OnPropertyChanged(nameof(CanStartCatalina));
            }
        }
    }

    public bool CanStart => IsInstalled && !IsRunning && !IsBusy && _statusKind != RuntimeStatusKind.Unknown;
    public bool CanStop => IsInstalled && IsRunning && !IsBusy;
    public bool CanRestart => IsInstalled && !IsBusy && _statusKind != RuntimeStatusKind.Unknown;
    public bool CanUninstall => IsInstalled && !IsBusy;
    public bool CanManage => IsInstalled && !IsBusy;
    public bool CanStartCatalina => IsTomcatModule && IsInstalled && !IsRunning && !IsBusy;

    public void ApplyInstallProgress(InstallProgress update)
    {
        if (update is null)
        {
            return;
        }

        _progressStage = update.Stage;
        _stagePercent = update.StagePercent;
        if (update.Stage == InstallProgressStage.Downloading)
        {
            _lastDownloadOverallPercent = Compat.Clamp(update.Percent, 0, 100);
        }

        var stagePercent = update.StagePercent;
        if (!stagePercent.HasValue && update.Stage == InstallProgressStage.Installing &&
            _lastDownloadOverallPercent > 0 && _lastDownloadOverallPercent < 100 &&
            update.Percent >= _lastDownloadOverallPercent)
        {
            stagePercent = (update.Percent - _lastDownloadOverallPercent) * 100d /
                           (100d - _lastDownloadOverallPercent);
            _stagePercent = stagePercent;
        }

        Progress = Compat.Clamp(stagePercent ?? update.Percent, 0, 100);
        ProgressStageText = "状态";
        DownloadSpeedText = update.Stage == InstallProgressStage.Downloading &&
                            !string.IsNullOrWhiteSpace(update.SpeedText)
            ? $"速度 {update.SpeedText}"
            : string.Empty;
        StatusText = update.Message;
        BadgeText = update.Stage switch
        {
            InstallProgressStage.Downloading => "下载中",
            InstallProgressStage.Completed => "已安装",
            _ => "安装中"
        };
        BadgeBrush = RuntimeStatusVisuals.Brush(RuntimeStatusKind.Starting);
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    public void ApplyRuntimeState(EnvironmentRuntimeState state)
    {
        if (IsMySqlModule)
        {
            var detectedReleaseId = state.IsInstalled ? state.DetectedMySqlReleaseId : null;
            var detectedVersion = state.IsInstalled ? state.DetectedMySqlVersion : null;
            var releaseChanged = !string.Equals(_detectedMySqlReleaseId, detectedReleaseId, StringComparison.OrdinalIgnoreCase);
            var versionChanged = !string.Equals(_detectedMySqlVersion, detectedVersion, StringComparison.OrdinalIgnoreCase);

            if (MySqlReleaseCatalog.Contains(detectedReleaseId))
            {
                SelectedMySqlReleaseId = detectedReleaseId!;
            }

            _detectedMySqlReleaseId = detectedReleaseId;
            _detectedMySqlVersion = detectedVersion;
            if (releaseChanged || versionChanged)
            {
                OnPropertyChanged(nameof(OptionLabel));
            }
        }
        else if (IsSqlServerModule)
        {
            var detectedReleaseId = state.IsInstalled ? state.DetectedSqlServerReleaseId : null;
            var detectedDisplayName = state.IsInstalled ? state.DetectedSqlServerDisplayName : null;
            var releaseChanged = !string.Equals(_detectedSqlServerReleaseId, detectedReleaseId, StringComparison.OrdinalIgnoreCase);
            var displayNameChanged = !string.Equals(_detectedSqlServerDisplayName, detectedDisplayName, StringComparison.OrdinalIgnoreCase);

            if (SqlServerReleaseCatalog.Contains(detectedReleaseId))
            {
                SelectedSqlServerReleaseId = detectedReleaseId!;
            }

            _detectedSqlServerReleaseId = detectedReleaseId;
            _detectedSqlServerDisplayName = detectedDisplayName;
            if (releaseChanged || displayNameChanged)
            {
                OnPropertyChanged(nameof(OptionLabel));
            }
        }

        _statusKind = state.StatusKind;
        _iisUninstallRestartRequired = Kind == EnvironmentKind.Iis &&
            !state.IsInstalled && EnvironmentRuntimeService.HasIisUninstallContinuation;
        _installRestartRequired = Kind == EnvironmentKind.SqlServer &&
            !state.IsInstalled && EnvironmentInstaller.HasSqlServerInstallContinuation;
        IsInstalled = state.IsInstalled;
        IsRunning = state.IsRunning;
        Progress = state.IsInstalled ? 100 : 0;
        _progressStage = state.IsInstalled ? InstallProgressStage.Completed : InstallProgressStage.Preparing;
        _stagePercent = state.IsInstalled ? 100 : 0;
        ProgressStageText = "状态";
        DownloadSpeedText = string.Empty;
        StatusText = state.StatusText;
        BadgeText = _iisUninstallRestartRequired ? "需重启" : RuntimeStatusVisuals.Text(state.StatusKind);
        BadgeBrush = _iisUninstallRestartRequired ? Brushes.Goldenrod : RuntimeStatusVisuals.Brush(state.StatusKind);
        RaiseStateProperties();
    }

    public void MarkInstallRestartRequired()
    {
        _installRestartRequired = true;
        RaiseStateProperties();
    }

    public void SetBusyState(string badgeText, string statusText, double? progress = null)
    {
        _statusKind = RuntimeStatusKind.Starting;
        _installRestartRequired = false;
        _iisUninstallRestartRequired = false;
        IsBusy = true;
        StatusText = statusText;
        BadgeText = badgeText;
        BadgeBrush = RuntimeStatusVisuals.Brush(RuntimeStatusKind.Starting);
        _progressStage = InstallProgressStage.Preparing;
        _stagePercent = progress ?? 0;
        _lastDownloadOverallPercent = 0;
        ProgressStageText = "状态";
        DownloadSpeedText = string.Empty;
        if (progress.HasValue)
        {
            Progress = progress.Value;
        }

        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    private void RaiseStateProperties()
    {
        OnPropertyChanged(nameof(OptionLabel));
        OnPropertyChanged(nameof(ActionText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(CanSelectMySqlVersion));
        OnPropertyChanged(nameof(CanSelectSqlServerVersion));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanStop));
        OnPropertyChanged(nameof(CanRestart));
        OnPropertyChanged(nameof(CanUninstall));
        OnPropertyChanged(nameof(CanManage));
        OnPropertyChanged(nameof(CanStartCatalina));
        OnPropertyChanged(nameof(InstallButtonVisibility));
        OnPropertyChanged(nameof(MySqlVersionSelectorVisibility));
        OnPropertyChanged(nameof(SqlServerVersionSelectorVisibility));
        OnPropertyChanged(nameof(ServiceControlsVisibility));
        OnPropertyChanged(nameof(MySqlConnectButtonVisibility));
        OnPropertyChanged(nameof(SqlServerConnectButtonVisibility));
        OnPropertyChanged(nameof(EnvironmentFlipVisibility));
        OnPropertyChanged(nameof(IisManagementVisibility));
        OnPropertyChanged(nameof(NginxManagementVisibility));
        OnPropertyChanged(nameof(FrpManagementVisibility));
        OnPropertyChanged(nameof(TomcatCatalinaVisibility));
        OnPropertyChanged(nameof(InstallDirectoryVisibility));
        OnPropertyChanged(nameof(CredentialSubtitle));
        OnPropertyChanged(nameof(CredentialFields));
        OnPropertyChanged(nameof(IsNginxModule));
        OnPropertyChanged(nameof(IsTomcatModule));
    }

    private IReadOnlyList<CredentialField> LoadCredentialFields()
    {
        if (Kind == EnvironmentKind.MySql)
        {
            var credentials = MySqlCredentialStore.Load();
            return
            [
                new CredentialField("地址", credentials.Host),
                new CredentialField("端口", credentials.Port.ToString(CultureInfo.InvariantCulture), CredentialEditKind.Port),
                new CredentialField("账号", credentials.UserName),
                new CredentialField("密码", credentials.Password, CredentialEditKind.Password)
            ];
        }

        if (Kind == EnvironmentKind.SqlServer)
        {
            var credentials = SqlServerCredentialStore.Load();
            return
            [
                new CredentialField("地址", credentials.Host),
                new CredentialField("端口", credentials.Port.ToString(CultureInfo.InvariantCulture)),
                new CredentialField("账号", credentials.UserName),
                new CredentialField("密码", credentials.Password)
            ];
        }

        return [];
    }
}

public sealed class TomcatPortItem : ObservableObject
{
    private int _port;
    private string _portText;
    private string _contextPath;
    private string _url;
    private bool _isBusy;

    public TomcatPortItem(TomcatProductDeploymentInfo deployment)
    {
        ProductId = deployment.ProductId;
        _port = deployment.Port;
        _portText = deployment.Port.ToString(CultureInfo.InvariantCulture);
        _contextPath = deployment.ContextPath;
        _url = deployment.Url;
    }

    public string ProductId { get; }
    public int Port { get => _port; private set => SetProperty(ref _port, value); }
    public string PortText { get => _portText; set => SetProperty(ref _portText, value); }
    public string ContextPath { get => _contextPath; private set => SetProperty(ref _contextPath, value); }
    public string Url { get => _url; private set => SetProperty(ref _url, value); }
    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(CanSave));
            }
        }
    }
    public bool CanSave => !IsBusy;

    public void Apply(TomcatProductDeploymentInfo deployment)
    {
        Port = deployment.Port;
        PortText = deployment.Port.ToString(CultureInfo.InvariantCulture);
        ContextPath = deployment.ContextPath;
        Url = deployment.Url;
    }
}

public enum CredentialEditKind
{
    None,
    Port,
    Password
}

public sealed record CredentialField(string Label, string Value, CredentialEditKind EditKind = CredentialEditKind.None)
{
    public bool CanEdit => EditKind != CredentialEditKind.None;
    public Visibility EditVisibility => CanEdit ? Visibility.Visible : Visibility.Collapsed;
    public string EditToolTip => EditKind == CredentialEditKind.Password ? "修改密码" : "修改端口";
}

public static class RuntimeStatusVisuals
{
    public static string Text(RuntimeStatusKind kind) => kind switch
    {
        RuntimeStatusKind.Running => "运行中",
        RuntimeStatusKind.Starting => "启动中",
        RuntimeStatusKind.Stopping => "停止中",
        RuntimeStatusKind.Stopped => "未运行",
        RuntimeStatusKind.NotInstalled => "未安装",
        _ => "检测中"
    };

    public static Brush Brush(RuntimeStatusKind kind) => kind switch
    {
        RuntimeStatusKind.Running => Brushes.SeaGreen,
        RuntimeStatusKind.Starting => Brushes.DodgerBlue,
        RuntimeStatusKind.Stopping => Brushes.DarkOrange,
        RuntimeStatusKind.Stopped => Brushes.IndianRed,
        RuntimeStatusKind.NotInstalled => Brushes.Gray,
        _ => Brushes.SlateGray
    };
}

public enum ProductSource
{
    Local,
    Online
}

public sealed class CustomWebsiteItem
{
    public CustomWebsiteItem(CustomWebsiteDefinition definition)
    {
        Definition = definition;
        Url = CustomWebsiteService.BuildUrl(definition);
        var ports = IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Select(endpoint => endpoint.Port).ToHashSet();
        var expectedPort = definition.SslEnabled ? definition.HttpsPort : definition.HttpPort;
        if (!Directory.Exists(definition.PhysicalPath))
        {
            StatusText = "网站目录缺失";
            StatusBrush = Brushes.IndianRed;
        }
        else if (ports.Contains(expectedPort))
        {
            StatusText = "运行中";
            StatusBrush = Brushes.MediumSeaGreen;
        }
        else
        {
            StatusText = "已配置，未监听";
            StatusBrush = Brushes.Goldenrod;
        }
    }

    public CustomWebsiteDefinition Definition { get; }
    public string Name => Definition.Name;
    public string PhysicalPath => Definition.PhysicalPath;
    public string Url { get; }
    public string StatusText { get; }
    public Brush StatusBrush { get; }
    public string DomainSummary => Definition.Domains.Count == 0
        ? $"所有主机 · HTTP :{Definition.HttpPort}"
        : $"{string.Join("、", Definition.Domains)} · HTTP :{Definition.HttpPort}";
    public string SecuritySummary => Definition.SslEnabled
        ? $"HTTPS :{Definition.HttpsPort}{(Definition.RedirectHttpToHttps ? " · 强制跳转" : string.Empty)}"
        : "未启用 HTTPS";
    public string PoolSummary => $"应用程序池：{Definition.ApplicationPoolName} · {(Definition.ManagedRuntimeVersion.Length == 0 ? "无托管代码" : Definition.ManagedRuntimeVersion)}";
}

public sealed class InstalledProductItem : ObservableObject
{
    private string _runtimeStatusText = "待检测";
    private Brush _runtimeStatusBrush = Brushes.Gray;
    private string _siteDisplayText = "尚未绑定 IIS";
    private string _poolDisplayText = "应用程序池：未创建";
    private string _url = string.Empty;
    private string _domainDisplayText = "独立域名：未配置";
    private bool _canBrowse;
    private bool _isTomcatDeployment;
    private bool _isManagementExpanded;

    public InstalledProductItem(ProductItem product, string installPath)
    {
        Product = product;
        InstallPath = installPath;
        RefreshRuntime();
    }

    public ProductItem Product { get; }
    public string ProductId => Product.ProductId;
    public string DisplayName => Product.DisplayName;
    public string IconPath => Product.IconPath;
    public string EnvironmentSummary => Product.EnvironmentSummary;
    public string InstallPath { get; }
    public string RuntimeStatusText { get => _runtimeStatusText; private set => SetProperty(ref _runtimeStatusText, value); }
    public Brush RuntimeStatusBrush { get => _runtimeStatusBrush; private set => SetProperty(ref _runtimeStatusBrush, value); }
    public string SiteDisplayText { get => _siteDisplayText; private set => SetProperty(ref _siteDisplayText, value); }
    public string PoolDisplayText { get => _poolDisplayText; private set => SetProperty(ref _poolDisplayText, value); }
    public string Url { get => _url; private set => SetProperty(ref _url, value); }
    public string DomainDisplayText { get => _domainDisplayText; private set => SetProperty(ref _domainDisplayText, value); }
    public bool CanBrowse
    {
        get => _canBrowse;
        private set
        {
            if (SetProperty(ref _canBrowse, value))
            {
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
            }
        }
    }
    public bool IsManagementExpanded
    {
        get => _isManagementExpanded;
        set => SetProperty(ref _isManagementExpanded, value);
    }
    public bool IsTomcatDeployment
    {
        get => _isTomcatDeployment;
        private set
        {
            if (SetProperty(ref _isTomcatDeployment, value))
            {
                OnPropertyChanged(nameof(IsIisDeployment));
                OnPropertyChanged(nameof(DeploymentLabel));
                OnPropertyChanged(nameof(ManagementButtonText));
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
            }
        }
    }
    public bool IsIisDeployment => !IsTomcatDeployment;
    public bool CanStartTomcatProduct => IsTomcatDeployment && !CanBrowse;
    public bool CanStopTomcatProduct => IsTomcatDeployment && CanBrowse;
    public string DeploymentLabel
    {
        get
        {
            if (!IsTomcatDeployment)
            {
                return "IIS 部署";
            }

            var port = GetTomcatDeploymentPort();
            return port is > 0 and <= 65535
                ? $"Tomcat 独立端口 :{port}"
                : "Tomcat 端口待自动修复";
        }
    }
    public string ManagementButtonText => IsTomcatDeployment ? "Tomcat 目录" : "IIS 管理";
    public void SetOperationState(string text)
    {
        RuntimeStatusText = text;
        RuntimeStatusBrush = Brushes.Goldenrod;
    }

    public void RefreshRuntime()
    {
        try
        {
            var domain = new ProductWebsiteService(new EnvironmentRuntimeService()).Load(ProductId);
            DomainDisplayText = domain.Enabled
                ? $"独立域名：{domain.Domains}{(domain.SslEnabled ? $" · HTTPS :{domain.HttpsPort}" : $" · HTTP :{domain.HttpPort}")}" 
                : "独立域名：未配置";
        }
        catch
        {
            DomainDisplayText = "独立域名：配置状态不可读";
        }

        var tomcatDeployment = FindTomcatDeployment();
        if (tomcatDeployment is not null)
        {
            IsTomcatDeployment = true;
            if (tomcatDeployment.Value.Port is <= 0 or > 65535)
            {
                SiteDisplayText = $"Tomcat / {ProductId}";
                PoolDisplayText = $"应用上下文：/{ProductId}";
                Url = string.Empty;
                RuntimeStatusText = "端口配置异常，点击启动可自动修复";
                RuntimeStatusBrush = Brushes.IndianRed;
                CanBrowse = false;
                return;
            }

            var tomcatNetwork = IPGlobalProperties.GetIPGlobalProperties();
            var tomcatListening = tomcatNetwork.GetActiveTcpListeners().Any(endpoint => endpoint.Port == tomcatDeployment.Value.Port);

            SiteDisplayText = $"Tomcat / {ProductId}";
            PoolDisplayText = $"应用上下文：/{ProductId}";
            Url = $"http://localhost:{tomcatDeployment.Value.Port}/{ProductId}/";

            RuntimeStatusText = tomcatListening ? "运行中" : "已部署，未运行";
            RuntimeStatusBrush = tomcatListening ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
            CanBrowse = tomcatListening;
            return;
        }

        IsTomcatDeployment = false;
        var info = ProductDeploymentService.LoadIisDeploymentInfo(ProductId);
        if (info is null)
        {
            RuntimeStatusText = "未绑定";
            RuntimeStatusBrush = Brushes.IndianRed;
            SiteDisplayText = $"MCPanel / {ProductId}";
            PoolDisplayText = $"应用程序池：{ProductId}（未创建）";
            Url = $"http://localhost:8088/{ProductId}/";
            CanBrowse = false;
            return;
        }

        var properties = IPGlobalProperties.GetIPGlobalProperties();
        var listening = properties.GetActiveTcpListeners().Any(endpoint => endpoint.Port == info.Port);

        SiteDisplayText = $"{info.SiteName} {info.ApplicationPath}";
        PoolDisplayText = $"应用程序池：{info.ApplicationPool}";
        Url = info.Url;

        RuntimeStatusText = listening ? "运行中" : "已绑定，未运行";
        RuntimeStatusBrush = listening ? Brushes.MediumSeaGreen : Brushes.Goldenrod;

        CanBrowse = listening;
    }

    public string? GetTomcatDeploymentPath() => FindTomcatDeployment()?.Path;

    private int GetTomcatDeploymentPort() => FindTomcatDeployment()?.Port ?? 0;

    private (string Path, int Port)? FindTomcatDeployment()
    {
        var managed = ProductDeploymentService.LoadTomcatDeploymentInfo(ProductId);
        if (managed is not null &&
            (Directory.Exists(managed.PhysicalPath) || File.Exists(managed.PhysicalPath)))
        {
            return (managed.PhysicalPath, managed.Port);
        }

        foreach (var runtimeRoot in ComponentPaths.TomcatSearchRoots)
        {
            if (!Directory.Exists(runtimeRoot))
            {
                continue;
            }

            try
            {
                foreach (var tomcatRoot in Directory.EnumerateDirectories(runtimeRoot, "apache-tomcat-*", SearchOption.TopDirectoryOnly))
                {
                    var contextFile = Path.Combine(tomcatRoot, "conf", "Catalina", "localhost", $"{ProductId}.xml");
                    if (File.Exists(contextFile))
                    {
                        try
                        {
                            var context = XDocument.Load(contextFile).Root;
                            var docBase = context?.Attribute("docBase")?.Value;
                            if (!string.IsNullOrWhiteSpace(docBase) &&
                                (Directory.Exists(docBase) || File.Exists(docBase)))
                            {
                                return (docBase!, ReadTomcatPort(tomcatRoot));
                            }
                        }
                        catch
                        {
                        }
                    }

                    try
                    {
                        var serverXml = XDocument.Load(Path.Combine(tomcatRoot, "conf", "server.xml"));
                        foreach (var service in serverXml.Root?.Elements("Service") ?? [])
                        {
                            var context = service.Descendants("Context").FirstOrDefault(element =>
                                string.Equals(element.Attribute("path")?.Value?.Trim('/'), ProductId, StringComparison.OrdinalIgnoreCase));
                            if (context is null)
                            {
                                continue;
                            }

                            var connector = service.Elements("Connector").FirstOrDefault();
                            var docBase = context.Attribute("docBase")?.Value;
                            if (int.TryParse(connector?.Attribute("port")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var servicePort) &&
                                servicePort is > 0 and <= 65535 &&
                                !string.IsNullOrWhiteSpace(docBase) &&
                                (Directory.Exists(docBase) || File.Exists(docBase)))
                            {
                                return (docBase!, servicePort);
                            }
                        }
                    }
                    catch
                    {
                    }

                    // Compatibility with products deployed by earlier builds.
                    var deployedPath = Path.Combine(tomcatRoot, "webapps", ProductId);
                    var deployedWar = Path.Combine(tomcatRoot, "webapps", $"{ProductId}.war");
                    if (!Directory.Exists(deployedPath) && !File.Exists(deployedWar))
                    {
                        continue;
                    }

                    return (Directory.Exists(deployedPath) ? deployedPath : deployedWar, ReadTomcatPort(tomcatRoot));
                }
            }
            catch
            {
            }
        }

        return null;
    }

    private static int ReadTomcatPort(string tomcatRoot)
    {
        try
        {
            var serverXml = XDocument.Load(Path.Combine(tomcatRoot, "conf", "server.xml"));
            var connector = serverXml.Descendants("Connector")
                .FirstOrDefault(element =>
                    int.TryParse(element.Attribute("port")?.Value, out _) &&
                    !string.Equals(element.Attribute("protocol")?.Value, "AJP/1.3", StringComparison.OrdinalIgnoreCase));
            if (int.TryParse(connector?.Attribute("port")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) &&
                port is > 0 and <= 65535)
            {
                return port;
            }
        }
        catch
        {
        }

        return 8080;
    }
}

public sealed class InstallationProgressViewModel : ObservableObject
{
    private bool _isVisible;
    private bool _canCancel;
    private bool _canPause;
    private bool _isPaused;
    private string _operationTitle = "正在安装产品";
    private string _productName = string.Empty;
    private string _productId = string.Empty;
    private string _iconPath = "/Assets/defaultimg.png";
    private string _stageText = "准备安装";
    private string _detailText = "正在准备产品信息...";
    private string _elapsedText = "已用时 00:00";
    private double _progress;
    private DateTime _startedAt;
    private string _lastDownloadDetail = "正在连接产品下载服务...";
    private string _downloadSpeedText = string.Empty;
    private string _transferText = string.Empty;
    private double _downloadProgress;
    private bool _downloadTotalKnown;

    public InstallationProgressViewModel()
    {
        QueueItems.CollectionChanged += (_, args) =>
        {
            if (args.OldItems is not null)
            {
                foreach (ProductInstallQueueItemViewModel item in args.OldItems)
                {
                    item.PropertyChanged -= QueueItem_PropertyChanged;
                }
            }

            if (args.NewItems is not null)
            {
                foreach (ProductInstallQueueItemViewModel item in args.NewItems)
                {
                    item.PropertyChanged += QueueItem_PropertyChanged;
                }
            }

            NotifyQueueProperties();
        };
    }

    public ObservableCollection<string> RecentEvents { get; } = [];
    public ObservableCollection<ProductInstallQueueItemViewModel> QueueItems { get; } = [];
    public bool IsVisible { get => _isVisible; private set => SetProperty(ref _isVisible, value); }
    public bool HasQueueItems => QueueItems.Count > 0;
    public bool HasActiveQueue => QueueItems.Any(item => !item.IsTerminal);
    public int ActiveQueueCount => QueueItems.Count(item => !item.IsTerminal);
    public Visibility QueueEmptyVisibility => HasQueueItems ? Visibility.Collapsed : Visibility.Visible;
    public Visibility QueueItemsVisibility => HasQueueItems ? Visibility.Visible : Visibility.Collapsed;
    public string QueueSummaryText
    {
        get
        {
            var active = QueueItems
                .Where(item => !item.IsTerminal)
                .OrderBy(item => item.Sequence)
                .ToArray();
            if (active.Length == 0)
            {
                return "安装队列已完成";
            }

            var running = active.FirstOrDefault(item => item.State == ProductInstallQueueStatus.Running);
            return running is null
                ? $"安装队列：等待 {active.Length} 个产品"
                : $"安装队列：正在处理第 {running.QueuePosition} 项，共 {active.Length} 项";
        }
    }
    public bool CanCancel { get => _canCancel; private set => SetProperty(ref _canCancel, value); }
    public bool CanPause
    {
        get => _canPause;
        private set
        {
            if (SetProperty(ref _canPause, value))
            {
                OnPropertyChanged(nameof(PauseButtonVisibility));
                OnPropertyChanged(nameof(IsDownloading));
            }
        }
    }
    public bool IsDownloading => CanPause || QueueItems.Any(item => item.IsDownloading);
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (SetProperty(ref _isPaused, value))
            {
                OnPropertyChanged(nameof(PauseActionText));
            }
        }
    }
    public string PauseActionText => IsPaused ? "继续下载" : "暂停下载";
    public Visibility PauseButtonVisibility => CanPause ? Visibility.Visible : Visibility.Collapsed;
    public string OperationTitle { get => _operationTitle; private set => SetProperty(ref _operationTitle, value); }
    public string ProductName { get => _productName; private set => SetProperty(ref _productName, value); }
    public string ProductId { get => _productId; private set => SetProperty(ref _productId, value); }
    public string IconPath { get => _iconPath; private set => SetProperty(ref _iconPath, value); }
    public string StageText { get => _stageText; private set => SetProperty(ref _stageText, value); }
    public string DetailText { get => _detailText; private set => SetProperty(ref _detailText, value); }
    public string ElapsedText { get => _elapsedText; private set => SetProperty(ref _elapsedText, value); }
    public string DownloadSpeedText { get => _downloadSpeedText; private set => SetProperty(ref _downloadSpeedText, value); }
    public string TransferText { get => _transferText; private set => SetProperty(ref _transferText, value); }
    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
                OnPropertyChanged(nameof(IsProgressIndeterminate));
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, Compat.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(OverallDownloadProgress));
            }
        }
    }

    public double OverallDownloadProgress
    {
        get
        {
            var firstActive = QueueItems
                .Where(item => !item.IsTerminal)
                .OrderBy(item => item.Sequence)
                .FirstOrDefault();
            if (firstActive is null)
            {
                return DownloadProgress;
            }

            // Queue history is retained for the progress panel. Only include
            // terminal items from the current batch (those at or after the
            // first active item), otherwise an old completed install would
            // dilute the progress of a newly started batch.
            var currentBatch = QueueItems
                .Where(item =>
                    item.Sequence >= firstActive.Sequence &&
                    item.State is not ProductInstallQueueStatus.Failed and
                        not ProductInstallQueueStatus.Cancelled)
                .ToArray();
            if (currentBatch.Length == 0)
            {
                return DownloadProgress;
            }

            var completed = currentBatch
                .Where(item => item.IsTerminal)
                .Sum(_ => 100d);
            var active = currentBatch
                .Where(item => !item.IsTerminal)
                .Sum(item => item.IsDownloading ? item.DownloadProgress : 0d);
            return Compat.Clamp((completed + active) / currentBatch.Length, 0, 100);
        }
    }

    public string ProgressText => $"{Progress:0.0}%";
    public bool IsProgressIndeterminate => IsVisible &&
        (StageText == "准备下载" || (StageText == "下载产品文件" && !_downloadTotalKnown));

    public void Begin(ProductItem product, bool isUpdate, bool show = true)
    {
        _startedAt = DateTime.Now;
        OperationTitle = isUpdate ? "正在更新产品" : "正在安装产品";
        ProductName = product.DisplayName;
        ProductId = product.ProductId;
        IconPath = product.IconPath;
        StageText = "准备安装";
        DetailText = "正在准备产品安装...";
        Progress = 0;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        RecentEvents.Clear();
        AddEvent(DetailText);
        CanPause = false;
        IsPaused = false;
        CanCancel = true;
        IsVisible = show;
        Tick();
    }

    public void BeginQueued(ProductItem product, bool isUpdate, int queuePosition, bool show = true)
    {
        _startedAt = DateTime.Now;
        OperationTitle = isUpdate ? "等待更新产品" : "等待安装产品";
        ProductName = product.DisplayName;
        ProductId = product.ProductId;
        IconPath = product.IconPath;
        StageText = "等待队列";
        DetailText = $"已加入安装队列，当前排在第 {Math.Max(1, queuePosition)} 项。";
        Progress = 0;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        RecentEvents.Clear();
        AddEvent(DetailText);
        CanPause = false;
        IsPaused = false;
        CanCancel = true;
        IsVisible = show;
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        Tick();
    }

    public void BeginUninstall(ProductItem product)
    {
        _startedAt = DateTime.Now;
        OperationTitle = "正在卸载产品";
        ProductName = product.DisplayName;
        ProductId = product.ProductId;
        IconPath = product.IconPath;
        StageText = "准备卸载";
        DetailText = "正在读取产品绑定和运行状态...";
        Progress = 0;
        DownloadProgress = 0;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        RecentEvents.Clear();
        AddEvent(DetailText);
        // Uninstall changes IIS/Tomcat/Nginx state and is intentionally not
        // interruptible from this panel.  The button remains hidden instead of
        // exposing a cancellation path that could leave a half-removed binding.
        CanPause = false;
        IsPaused = false;
        CanCancel = false;
        IsVisible = true;
        Tick();
    }

    public void SetDownloadStage()
    {
        _lastDownloadDetail = "正在连接产品下载服务并准备接收文件...";
        StageText = "下载产品文件";
        DetailText = _lastDownloadDetail;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        DownloadProgress = 0;
        IsPaused = false;
        CanPause = true;
        AddEvent(DetailText);
        Tick();
    }

    public void ReportProgress(double value)
    {
        var normalized = Compat.Clamp(value, 0, 100);
        if (normalized > Progress)
        {
            Progress = normalized;
        }

        Tick();
    }

    public void ReportStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status))
        {
            return;
        }

        var normalized = status.Trim();
        if (CanPause)
        {
            _lastDownloadDetail = normalized;
        }

        if (IsPaused)
        {
            Tick();
            return;
        }

        DetailText = normalized;
        StageText = ResolveStage(DetailText);
        AddEvent(DetailText);
        Tick();
    }

    internal void ApplyWorkerProgress(ProductInstallWorkerProgress update)
    {
        if (update is null)
        {
            return;
        }

        var normalizedProgress = Compat.Clamp(update.Percent, 0, 100);
        if (normalizedProgress >= Progress || string.Equals(update.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            Progress = normalizedProgress;
        }

        DetailText = update.Message;
        StageText = update.Stage switch
        {
            InstallProgressStage.Preparing => "准备下载",
            InstallProgressStage.Downloading => "下载产品文件",
            InstallProgressStage.Installing => "配置运行服务",
            InstallProgressStage.Completed => "处理完成",
            _ => ResolveStage(update.Message)
        };
        var isDownloading = update.Stage == InstallProgressStage.Downloading &&
                            !string.Equals(update.State, "completed", StringComparison.OrdinalIgnoreCase);
        DownloadProgress = isDownloading
            ? update.StagePercent ?? update.Percent
            : update.Stage == InstallProgressStage.Downloading ? 100 : DownloadProgress;
        CanPause = isDownloading;
        CanCancel = !string.Equals(update.State, "completed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(update.State, "failed", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(update.State, "cancelled", StringComparison.OrdinalIgnoreCase);
        DownloadSpeedText = isDownloading
            ? $"速度 {(!string.IsNullOrWhiteSpace(update.SpeedText) ? update.SpeedText : "—")}"
            : string.Empty;
        _downloadTotalKnown = update.TotalBytes is > 0;
        TransferText = isDownloading &&
                       (update.BytesReceived > 0 || update.TotalBytes is not null ||
                        update.ScannedFiles > 0 || update.ScannedBytes > 0)
            ? new ProductDownloadProgress(
                update.StagePercent ?? update.Percent,
                update.Message,
                update.BytesReceived,
                update.TotalBytes,
                update.SpeedText,
                update.ScannedFiles,
                update.ScannedBytes).TransferText
            : string.Empty;
        if (isDownloading)
        {
            _lastDownloadDetail = update.Message;
        }

        AddEvent(update.Message);
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        Tick();
    }

    public void ReportUninstallProgress(ProductUninstallProgress update)
    {
        if (update is null || string.IsNullOrWhiteSpace(update.Status))
        {
            return;
        }

        var status = update.Status.Trim();
        Progress = Math.Max(Progress, Compat.Clamp(update.Percent, 0, 100));
        DetailText = status;
        StageText = ResolveUninstallStage(status);
        AddEvent(status);
        Tick();
    }

    public void SetDeploymentStage(bool isUpdate)
    {
        CanPause = false;
        IsPaused = false;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        DownloadProgress = 100;
        StageText = "配置运行服务";
        DetailText = isUpdate ? "产品文件已更新，正在检查并恢复运行服务..." : "产品文件已下载，正在配置运行服务...";
        ReportProgress(92);
        AddEvent(DetailText);
    }

    public void MarkDownloadPaused()
    {
        if (!CanPause || IsPaused)
        {
            return;
        }

        _lastDownloadDetail = DetailText;
        IsPaused = true;
        StageText = "下载已暂停";
        DetailText = "下载连接已暂停，临时文件会保留；点击“继续下载”即可恢复。";
        AddEvent("用户已暂停产品下载。");
        Tick();
    }

    public void MarkDownloadResumed()
    {
        if (!CanPause || !IsPaused)
        {
            return;
        }

        IsPaused = false;
        DetailText = string.IsNullOrWhiteSpace(_lastDownloadDetail)
            ? "正在继续下载产品文件..."
            : _lastDownloadDetail;
        StageText = ResolveStage(DetailText);
        AddEvent("产品下载已继续。");
        Tick();
    }

    public void MarkCancelling()
    {
        CanPause = false;
        IsPaused = false;
        CanCancel = false;
        StageText = "正在取消";
        DetailText = "正在安全停止当前操作，请稍候...";
        AddEvent(DetailText);
    }

    public void Complete()
    {
        StageText = "处理完成";
        DetailText = "产品文件与运行服务已处理完成。";
        Progress = 100;
        CanPause = false;
        IsPaused = false;
        CanCancel = false;
        DownloadSpeedText = string.Empty;
        TransferText = string.Empty;
        _downloadTotalKnown = false;
        DownloadProgress = 0;
        AddEvent(DetailText);
        Tick();
    }

    public void Hide()
    {
        IsVisible = false;
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

    public void Show()
    {
        IsVisible = true;
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        Tick();
    }

    public void Tick()
    {
        if (!IsVisible)
        {
            return;
        }

        var elapsed = DateTime.Now - _startedAt;
        ElapsedText = elapsed.TotalHours >= 1
            ? $"已用时 {(int)elapsed.TotalHours:00}:{elapsed.Minutes:00}:{elapsed.Seconds:00}"
            : $"已用时 {elapsed.Minutes:00}:{elapsed.Seconds:00}";
    }

    private void AddEvent(string message)
    {
        if (RecentEvents.FirstOrDefault()?.Equals(message, StringComparison.Ordinal) == true)
        {
            return;
        }

        RecentEvents.Insert(0, message);
        while (RecentEvents.Count > 6)
        {
            RecentEvents.RemoveAt(RecentEvents.Count - 1);
        }
    }

    private void QueueItem_PropertyChanged(object? sender, PropertyChangedEventArgs e) =>
        NotifyQueueProperties();

    private void NotifyQueueProperties()
    {
        OnPropertyChanged(nameof(HasQueueItems));
        OnPropertyChanged(nameof(HasActiveQueue));
        OnPropertyChanged(nameof(ActiveQueueCount));
        OnPropertyChanged(nameof(QueueEmptyVisibility));
        OnPropertyChanged(nameof(QueueItemsVisibility));
        OnPropertyChanged(nameof(QueueSummaryText));
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(OverallDownloadProgress));
    }

    private static string ResolveStage(string status)
    {
        if (status.Contains("停止", StringComparison.OrdinalIgnoreCase)) return "停止运行服务";
        if (status.Contains("准备", StringComparison.OrdinalIgnoreCase)) return "准备下载";
        if (status.Contains("更新", StringComparison.OrdinalIgnoreCase) || status.Contains("Revert", StringComparison.OrdinalIgnoreCase)) return "同步产品更新";
        if (status.Contains("下载", StringComparison.OrdinalIgnoreCase) || status.Contains("Checkout", StringComparison.OrdinalIgnoreCase)) return "下载产品文件";
        if (status.Contains("部署", StringComparison.OrdinalIgnoreCase) || status.Contains("配置", StringComparison.OrdinalIgnoreCase)) return "配置运行服务";
        return "处理产品文件";
    }

    private static string ResolveUninstallStage(string status)
    {
        if (status.Contains("准备", StringComparison.OrdinalIgnoreCase)) return "准备卸载";
        if (status.Contains("Nginx", StringComparison.OrdinalIgnoreCase)) return "清理代理配置";
        if (status.Contains("IIS", StringComparison.OrdinalIgnoreCase)) return "清理 IIS 绑定";
        if (status.Contains("Tomcat", StringComparison.OrdinalIgnoreCase)) return "清理 Tomcat 服务";
        if (status.Contains("目录", StringComparison.OrdinalIgnoreCase) || status.Contains("缓存", StringComparison.OrdinalIgnoreCase)) return "清理产品文件";
        if (status.Contains("同步", StringComparison.OrdinalIgnoreCase)) return "同步运行状态";
        if (status.Contains("完成", StringComparison.OrdinalIgnoreCase)) return "卸载完成";
        return "清理产品资源";
    }
}

public sealed class ProductRow(IReadOnlyList<ProductItem> items)
{
    public IReadOnlyList<ProductItem> Items { get; } = items;
    public ProductItem? Item1 => Items.ElementAtOrDefault(0);
    public ProductItem? Item2 => Items.ElementAtOrDefault(1);
    public ProductItem? Item3 => Items.ElementAtOrDefault(2);
}

public sealed class ProductCategoryFilter(string key, string name, int count, bool isSelected) : ObservableObject
{
    private bool _isSelected = isSelected;

    public string Key { get; } = key;
    public string DisplayText { get; } = $"{name}  {count}";
    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ProductItem(string productId, string name, string level, string iconPath, ProductSource source) : ObservableObject
{
    public static readonly IReadOnlyList<string> CategoryOrder =
    [
        "评价系统",
        "实训系统",
        "考试考务",
        "教学平台",
        "电商运营",
        "财会管理",
        "物流供应链",
        "数字技术",
        "其他"
    ];

    private bool _isInstalled;
    private bool _isBusy;
    private ProductInstallQueueStatus? _queueState;
    private int _queuePosition;
    private double _downloadProgress;
    private int _installSequence = int.MaxValue;
    private string _statusText = source == ProductSource.Local
        ? "旧包内置产品。在线刷新后如服务端返回下载地址，可自动安装。"
        : "在线产品库产品。";

    public string ProductId { get; } = productId;
    public string Name { get; } = name;
    public string Level { get; } = level;
    private string _iconPath = iconPath;
    public ProductSource Source { get; } = source;
    public string? RemoteIconUrl { get; init; }
    public string? RunEnvironment { get; init; }
    public string? SqlEnvironment { get; init; }
    public string? DevLanguage { get; init; }
    /// <summary>Original catalog Url: an optional product installation root, not a package URL.</summary>
    public string? InstallRoot { get; init; }
    /// <summary>Original catalog SysType, normally 32 or 64.</summary>
    public string? SysType { get; init; }
    /// <summary>Whether the catalog source came from the original SVN field.</summary>
    public bool UsesSvn { get; init; }
    public string? DownloadUrl { get; init; }
    public string? FileName { get; init; }
    public string Category { get; } = Classify(name, level);
    public string DisplayName { get; } = ResolveDisplayName(name, level, source);
    public string FamilyAndIdText { get; } = ResolveDisplayName(name, level, source).Equals(name, StringComparison.OrdinalIgnoreCase)
        ? productId
        : $"{name} · {productId}";
    public string SourceText => Source == ProductSource.Online ? "来源：在线产品库" : "来源：旧包内置清单";
    public string InstallActionText => IsBusy
        ? "安装中"
        : QueueState == ProductInstallQueueStatus.Pending ? $"排队中 #{QueuePosition}" : "安装";
    public string UninstallActionText => IsBusy ? "卸载中" : "卸载";
    public string UpdateActionText => IsBusy
        ? "更新中"
        : QueueState == ProductInstallQueueStatus.Pending ? $"排队中 #{QueuePosition}" : "更新";
    public bool IsQueued => QueueState is ProductInstallQueueStatus.Pending or ProductInstallQueueStatus.Running;
    public bool CanProductAction => !IsBusy && !IsQueued;
    public bool CanUpdate => IsInstalled && !IsBusy && !IsQueued;
    public ProductInstallQueueStatus? QueueState => _queueState;
    public int QueuePosition => _queuePosition;
    public Visibility InstallButtonVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UninstallButtonVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateButtonVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public int InstallSequence
    {
        get => _installSequence;
        set => SetProperty(ref _installSequence, value);
    }
    public string EnvironmentSummary
    {
        get
        {
            var parts = new[] { RunEnvironment, SqlEnvironment, DevLanguage }
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Select(value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return parts.Length == 0 ? $"版本：{Level}" : string.Join("  ·  ", parts);
        }
    }

    public string CompactStatusText
    {
        get
        {
            var status = (StatusText ?? string.Empty)
                .Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal)
                .Trim();

            if (QueueState == ProductInstallQueueStatus.Pending)
            {
                return $"等待安装队列第 {QueuePosition} 项处理";
            }

            if (IsBusy || IsInstalled || status.StartsWith("安装失败", StringComparison.Ordinal) ||
                status.StartsWith("在线接口", StringComparison.Ordinal) ||
                status.StartsWith("下载", StringComparison.Ordinal))
            {
                return status;
            }

            return string.IsNullOrWhiteSpace(DownloadUrl)
                ? "等待在线下载地址"
                : "可下载并自动部署";
        }
    }

    public string IconPath
    {
        get => _iconPath;
        set => SetProperty(ref _iconPath, string.IsNullOrWhiteSpace(value) ? "/Assets/defaultimg.png" : value);
    }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetProperty(ref _statusText, value))
            {
                OnPropertyChanged(nameof(CompactStatusText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(InstallActionText));
                OnPropertyChanged(nameof(UninstallActionText));
                OnPropertyChanged(nameof(UpdateActionText));
                OnPropertyChanged(nameof(CanProductAction));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(CompactStatusText));
            }
        }
    }

    public double DownloadProgress { get => _downloadProgress; set => SetProperty(ref _downloadProgress, value); }

    internal void SetQueueState(ProductInstallQueueStatus? state, int queuePosition)
    {
        if (SetProperty(ref _queueState, state))
        {
            OnPropertyChanged(nameof(IsQueued));
            OnPropertyChanged(nameof(InstallActionText));
            OnPropertyChanged(nameof(UninstallActionText));
            OnPropertyChanged(nameof(UpdateActionText));
            OnPropertyChanged(nameof(CanProductAction));
            OnPropertyChanged(nameof(CanUpdate));
            OnPropertyChanged(nameof(CompactStatusText));
        }

        if (SetProperty(ref _queuePosition, Math.Max(0, queuePosition)))
        {
            OnPropertyChanged(nameof(InstallActionText));
            OnPropertyChanged(nameof(UpdateActionText));
            OnPropertyChanged(nameof(CompactStatusText));
        }
    }

    public bool IsInstalled
    {
        get => _isInstalled;
        set
        {
            if (SetProperty(ref _isInstalled, value))
            {
                OnPropertyChanged(nameof(InstallActionText));
                OnPropertyChanged(nameof(UninstallActionText));
                OnPropertyChanged(nameof(UpdateActionText));
                OnPropertyChanged(nameof(CanProductAction));
                OnPropertyChanged(nameof(CanUpdate));
                OnPropertyChanged(nameof(InstallButtonVisibility));
                OnPropertyChanged(nameof(UninstallButtonVisibility));
                OnPropertyChanged(nameof(UpdateButtonVisibility));
                OnPropertyChanged(nameof(CompactStatusText));
            }
        }
    }

    private static string Classify(string productName, string productLevel)
    {
        var text = $"{productName} {productLevel}";
        if (ContainsAny(text, "考务", "考试", "考核", "认证"))
        {
            return "考试考务";
        }

        if (ContainsAny(text, "评价", "评测", "测评"))
        {
            return "评价系统";
        }

        if (ContainsAny(text, "实训", "训练", "实操", "练习"))
        {
            return "实训系统";
        }

        if (ContainsAny(text, "课程", "教学", "课堂", "云课", "资源平台"))
        {
            return "教学平台";
        }

        if (ContainsAny(text, "网店", "电商", "跨境", "商品", "营销", "运营"))
        {
            return "电商运营";
        }

        if (ContainsAny(text, "财务", "会计", "财会", "税务"))
        {
            return "财会管理";
        }

        if (ContainsAny(text, "物流", "供应链", "仓储"))
        {
            return "物流供应链";
        }

        if (ContainsAny(text, "大数据", "人工智能", "云计算", "软件开发", "程序设计", "网络技术"))
        {
            return "数字技术";
        }

        return "其他";
    }

    private static string ResolveDisplayName(string productName, string productLevel, ProductSource productSource)
    {
        if (productSource == ProductSource.Online &&
            !string.IsNullOrWhiteSpace(productLevel) &&
            !productLevel.Equals("在线", StringComparison.OrdinalIgnoreCase) &&
            !productLevel.Equals(productName, StringComparison.OrdinalIgnoreCase))
        {
            return productLevel.Trim();
        }

        return productName;
    }

    private static bool ContainsAny(string text, params string[] keywords) =>
        keywords.Any(keyword => text.Contains(keyword, StringComparison.OrdinalIgnoreCase));
}
public sealed class DriveItem : ObservableObject
{
    private double _percentUsed;
    private double _targetPercentUsed;
    private string _sizeText;

    public DriveItem(string name, long usedBytes, long totalBytes)
    {
        Name = name;
        _targetPercentUsed = CalculatePercent(usedBytes, totalBytes);
        _percentUsed = _targetPercentUsed;
        _sizeText = FormatSize(usedBytes, totalBytes);
    }

    public string Name { get; }

    public double PercentUsed
    {
        get => _percentUsed;
        private set => SetProperty(ref _percentUsed, value);
    }

    public string SizeText
    {
        get => _sizeText;
        private set => SetProperty(ref _sizeText, value);
    }

    public void UpdateTarget(long usedBytes, long totalBytes)
    {
        _targetPercentUsed = CalculatePercent(usedBytes, totalBytes);
        SizeText = FormatSize(usedBytes, totalBytes);
    }

    public void TickAnimation()
    {
        var next = PercentUsed + (_targetPercentUsed - PercentUsed) * 0.16;
        PercentUsed = Math.Abs(next - _targetPercentUsed) < 0.05 ? _targetPercentUsed : next;
    }

    public void SnapToTarget()
    {
        PercentUsed = _targetPercentUsed;
    }

    private static double CalculatePercent(long usedBytes, long totalBytes) => totalBytes <= 0 ? 0 : usedBytes * 100d / totalBytes;
    private static string FormatSize(long usedBytes, long totalBytes) => $"{ToGb(usedBytes):N2} GB/{ToGb(totalBytes):N0} GB";
    private static double ToGb(long bytes) => bytes / 1024d / 1024d / 1024d;
}

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged(string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public sealed class VerticalMeter : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(VerticalMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(VerticalMeter), new PropertyMetadata(0d));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static VerticalMeter()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VerticalMeter), new FrameworkPropertyMetadata(typeof(VerticalMeter)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Template = BuildTemplate();
    }

    private static ControlTemplate BuildTemplate()
    {
        const string template = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Control}">
                <Border BorderBrush="#D7DEE8" BorderThickness="1" CornerRadius="6" Width="74" Padding="8">
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="28" />
                            <RowDefinition Height="*" />
                            <RowDefinition Height="28" />
                        </Grid.RowDefinitions>
                        <TextBlock Text="{Binding Title, RelativeSource={RelativeSource TemplatedParent}}" FontWeight="SemiBold" FontSize="15" HorizontalAlignment="Center" Background="White" Padding="8,0" />
                        <Grid Grid.Row="1" Margin="10,4" ClipToBounds="True">
                            <Border Background="#E2E8F0" />
                            <Border Background="#1296DB" VerticalAlignment="Bottom">
                                <Border.Height>
                                    <MultiBinding Converter="{x:Static local:MeterHeightConverter.Instance}" xmlns:local="clr-namespace:MCPanel">
                                        <Binding Path="ActualHeight" RelativeSource="{RelativeSource AncestorType=Grid}" />
                                        <Binding Path="Value" RelativeSource="{RelativeSource TemplatedParent}" />
                                    </MultiBinding>
                                </Border.Height>
                            </Border>
                        </Grid>
                        <TextBlock Grid.Row="2" Text="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, StringFormat={}{0:N1}%}" FontSize="13" HorizontalAlignment="Center" VerticalAlignment="Bottom" />
                    </Grid>
                </Border>
            </ControlTemplate>
            """;

        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template);
    }
}

public sealed class MeterHeightConverter : IMultiValueConverter
{
    public static MeterHeightConverter Instance { get; } = new();

    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values.Length < 2 || values[0] is not double height || values[1] is not double value)
        {
            return 0d;
        }

        return Math.Max(0, height * Compat.Clamp(value, 0, 100) / 100d);
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CircularProgress : Control
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnValueChanged));

    public static readonly DependencyProperty AnimatedValueProperty =
        DependencyProperty.Register(
            nameof(AnimatedValue),
            typeof(double),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration ValueAnimationDuration =
        new(TimeSpan.FromMilliseconds(700));

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(nameof(ProgressBrush), typeof(Brush), typeof(CircularProgress), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(CircularProgress), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(232, 234, 237)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CircularProgress), new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double AnimatedValue => (double)GetValue(AnimatedValueProperty);

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = Math.Max(1, StrokeThickness);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, size / 2 - thickness / 2 - 2);
        var trackPen = CreatePen(TrackBrush, thickness);
        var progressPen = CreatePen(ProgressBrush, thickness);

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        var percent = Compat.Clamp(AnimatedValue, 0, 100);
        if (percent <= 0)
        {
            return;
        }

        if (percent >= 99.95)
        {
            drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        var startAngle = -90d;
        var endAngle = startAngle + percent * 3.6d;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, percent >= 50, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, progressPen, geometry);
    }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (CircularProgress)dependencyObject;
        var target = NormalizeValue((double)args.NewValue);

        // Values are normally sampled once per second. Keep the first value
        // immediate, then interpolate subsequent samples at the compositor's
        // frame rate so the ring does not jump between samples.
        if (!control.IsLoaded || control.ActualWidth <= 0 || control.ActualHeight <= 0)
        {
            control.BeginAnimation(AnimatedValueProperty, null);
            control.SetValue(AnimatedValueProperty, target);
            return;
        }

        var current = control.AnimatedValue;
        if (Math.Abs(current - target) < 0.01)
        {
            control.BeginAnimation(AnimatedValueProperty, null);
            control.SetValue(AnimatedValueProperty, target);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = ValueAnimationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        control.BeginAnimation(AnimatedValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static double NormalizeValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Compat.Clamp(value, 0, 100);
    }

    private static Pen CreatePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}

public sealed class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = Brushes.Green;
    public Brush FalseBrush { get; set; } = Brushes.Red;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class BoolToTextConverter : IValueConverter
{
    public string TrueText { get; set; } = "是";
    public string FalseText { get; set; } = "否";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) =>
        value is true ? TrueText : FalseText;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class SubtractConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var width = value is double number ? number : 0d;
        var subtraction = double.TryParse(parameter?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : 0d;
        return Math.Max(0, width - subtraction);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentToScaleConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IConvertible convertible)
        {
            return Compat.Clamp(convertible.ToDouble(CultureInfo.InvariantCulture) / 100d, 0, 1);
        }

        return 0d;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class PercentToArcPointConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var percent = ReadPercent(value);
        var (center, radius) = ReadArc(parameter);
        var angle = (-90d + percent * 3.599d) * Math.PI / 180d;
        return new Point(center + radius * Math.Cos(angle), center + radius * Math.Sin(angle));
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();

    private static double ReadPercent(object value)
    {
        if (value is IConvertible convertible)
        {
            return Compat.Clamp(convertible.ToDouble(CultureInfo.InvariantCulture), 0, 100);
        }

        return 0;
    }

    private static (double Center, double Radius) ReadArc(object parameter)
    {
        if (parameter is string text)
        {
            var parts = text.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length == 2 &&
                double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var center) &&
                double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var radius))
            {
                return (center, radius);
            }

            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out radius))
            {
                return (radius + 8, radius);
            }
        }

        return (59, 51);
    }
}

public sealed class PercentToLargeArcConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is IConvertible convertible)
        {
            return convertible.ToDouble(CultureInfo.InvariantCulture) >= 50;
        }

        return false;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class ServiceActionEnabledConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

public sealed class CpuSampler
{
    private long _lastIdle;
    private long _lastKernel;
    private long _lastUser;

    public double NextValue()
    {
        if (!GetSystemTimes(out var idle, out var kernel, out var user))
        {
            return 0;
        }

        var idleTicks = idle.ToInt64();
        var kernelTicks = kernel.ToInt64();
        var userTicks = user.ToInt64();

        var total = (kernelTicks - _lastKernel) + (userTicks - _lastUser);
        var idleDelta = idleTicks - _lastIdle;

        _lastIdle = idleTicks;
        _lastKernel = kernelTicks;
        _lastUser = userTicks;

        if (total <= 0)
        {
            return 0;
        }

        return (total - idleDelta) * 100d / total;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetSystemTimes(out FileTime idleTime, out FileTime kernelTime, out FileTime userTime);

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct FileTime
    {
        private readonly uint _low;
        private readonly uint _high;
        public long ToInt64() => ((long)_high << 32) + _low;
    }
}

public static class SystemHardware
{
    public static string GetCpuName()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            if (key?.GetValue("ProcessorNameString") is string name && !string.IsNullOrWhiteSpace(name))
            {
                return Normalize(name);
            }
        }
        catch
        {
            // Ignore registry failures and fall back to a generic label.
        }

        return Environment.Is64BitProcess ? "X64" : "X86";
    }

    public static string GetOsText()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            if (key is not null)
            {
                var productName = ReadString(key, "ProductName");
                var editionId = ReadString(key, "EditionID");
                var displayVersion = ReadString(key, "DisplayVersion");
                var releaseId = ReadString(key, "ReleaseId");
                var build = ReadInt(key, "CurrentBuildNumber");
                var ubr = ReadInt(key, "UBR");

                var name = NormalizeWindowsName(productName, editionId, build);
                var version = !string.IsNullOrWhiteSpace(displayVersion) ? displayVersion : releaseId;
                var buildText = build > 0
                    ? ubr >= 0 ? $"{build}.{ubr}" : build.ToString(CultureInfo.InvariantCulture)
                    : string.Empty;

                return string.Join(" ",
                    new[] { name, version, string.IsNullOrWhiteSpace(buildText) ? string.Empty : $"({buildText})" }
                        .Where(part => !string.IsNullOrWhiteSpace(part)));
            }
        }
        catch
        {
            // Fall back below. Some locked-down systems block registry reads.
        }

        return Environment.OSVersion.VersionString;
    }

    private static string Normalize(string value)
    {
        var text = string.Join(" ", value.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries));
        return text
            .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string NormalizeWindowsName(string productName, string editionId, int build)
    {
        var name = string.IsNullOrWhiteSpace(productName) ? string.Empty : productName.Trim();
        if (build >= 22000 && name.StartsWith("Windows 10", StringComparison.OrdinalIgnoreCase))
        {
            name = "Windows 11" + name.Substring("Windows 10".Length);
        }

        if (!string.IsNullOrWhiteSpace(name))
        {
            return name;
        }

        if (build >= 22000)
        {
            return string.IsNullOrWhiteSpace(editionId) ? "Windows 11" : $"Windows 11 {editionId}";
        }

        if (build >= 10240)
        {
            return string.IsNullOrWhiteSpace(editionId) ? "Windows 10" : $"Windows 10 {editionId}";
        }

        return "Windows";
    }

    private static string ReadString(RegistryKey key, string name) =>
        key.GetValue(name)?.ToString() ?? string.Empty;

    private static int ReadInt(RegistryKey key, string name)
    {
        var value = key.GetValue(name);
        return value switch
        {
            int number => number,
            string text when int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) => number,
            _ => -1
        };
    }
}

public static class SystemMemory
{
    public static double GetMemoryUsagePercent()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status) || status.TotalPhys == 0)
        {
            return 0;
        }

        return (status.TotalPhys - status.AvailPhys) * 100d / status.TotalPhys;
    }

    public static string GetTotalMemoryText()
    {
        var status = new MemoryStatusEx();
        if (!GlobalMemoryStatusEx(status) || status.TotalPhys == 0)
        {
            return "未知";
        }

        return $"{status.TotalPhys / 1024d / 1024d / 1024d:N1} GB";
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatusEx lpBuffer);

    [StructLayout(LayoutKind.Sequential)]
    private sealed class MemoryStatusEx
    {
        public uint Length = (uint)Marshal.SizeOf<MemoryStatusEx>();
        public uint MemoryLoad;
        public ulong TotalPhys;
        public ulong AvailPhys;
        public ulong TotalPageFile;
        public ulong AvailPageFile;
        public ulong TotalVirtual;
        public ulong AvailVirtual;
        public ulong AvailExtendedVirtual;
    }
}



