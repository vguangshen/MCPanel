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
    private int _panelMemoryCleanupInProgress;
    private bool _productUninstallInProgress;
    private CancellationTokenSource? _productUninstallCancellation;
    private CancellationTokenSource? _applicationUpdateCancellation;
    private ApplicationUpdateAuthorization? _applicationUpdateAuthorization;
    private CancellationTokenSource? _databaseToolCancellation;

    internal bool IsDarkThemeActive => _isDarkThemeActive;

    public MainWindow()
    {
        InitializeComponent();
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
        StateChanged += (_, _) => UpdateWindowStateChrome();
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
            CloseDownloadQueueFlyout();
            _productInstallQueue.Dispose();
            _productUninstallCancellation?.Cancel();
            _productUninstallCancellation?.Dispose();
            _applicationUpdateCancellation?.Cancel();
            _applicationUpdateCancellation?.Dispose();
            var updateAuthorization = Interlocked.Exchange(ref _applicationUpdateAuthorization, null);
            _applicationUpdateService.CancelUpdateAuthorization(updateAuthorization);
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
        RefreshPanelMemoryUsage();
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
                RefreshPanelMemoryUsage();
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

    private static void WriteRuntimeRefreshError(Exception exception)
    {
        try
        {
            var workDirectory = ComponentPaths.WorkRoot;
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
        var runtimeSnapshot = _runtimeService.GetSnapshot();
        _model.ApplyRuntimeStates(
            runtimeSnapshot.States,
            frpState,
            runtimeSnapshot.InstallDirectories,
            preserveBusy);
    }

    private async Task RefreshEnvironmentStatesAsync(bool preserveBusy = false)
    {
        var generation = Volatile.Read(ref _runtimeRefreshGeneration);
        var runtimeSnapshotTask = Task.Run(_runtimeService.GetSnapshot);
        var frpTask = Task.Run(_frpManager.GetState);
        await Task.WhenAll(runtimeSnapshotTask, frpTask);

        if (_isClosed || generation != Volatile.Read(ref _runtimeRefreshGeneration))
        {
            return;
        }

        var runtimeSnapshot = runtimeSnapshotTask.Result;
        _model.ApplyRuntimeStates(
            runtimeSnapshot.States,
            frpTask.Result,
            runtimeSnapshot.InstallDirectories,
            preserveBusy);
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
        CloseDownloadQueueFlyout();
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
        CloseDownloadQueueFlyout();
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
            CloseDownloadQueueFlyout();
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
            RefreshPanelMemoryUsage();
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
}
