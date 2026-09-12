using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MCPanel;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    private const string UiMutexName = "Local\\MCPanel.MainWindow.v1";
    private const string ActivationEventName = "Local\\MCPanel.Activate.v1";
    private Mutex? _uiMutex;
    private EventWaitHandle? _activationEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private TrayIconService? _trayIcon;
    private readonly WindowActivationController _windowActivation = new();
    private MainWindow? _mainWindow => _windowActivation.CurrentWindow as MainWindow;
    private bool _isExiting;
    private int _uiGeneration;

    internal bool IsExiting => _isExiting;
    internal bool HasTrayIcon => _trayIcon is not null;

    public App()
    {
        DispatcherUnhandledException += (_, args) =>
        {
            WriteLifecycleError("UI 未处理异常", args.Exception);
            // A rendering/resource exception must not terminate the entire
            // panel. The detailed exception is retained for diagnosis while
            // the current page can be replaced or retried by the user.
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteLifecycleError("后台任务未处理异常", args.Exception);
            args.SetObserved();
        };
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _ = OnStartupAsync(e);
    }

    private async Task OnStartupAsync(StartupEventArgs e)
    {
        try
        {
            ComponentStorageMigration.MigrateLegacyData();

            if (NginxWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = NginxWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (TomcatWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = TomcatWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (FrpWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = FrpWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (EnvironmentInstallWorker.IsWorkerRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await EnvironmentInstallWorker.RunAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (ProductInstallWorker.IsWorkerRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await ProductInstallWorker.RunAsync(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (TomcatProductStartupManager.IsRestoreRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = await TomcatProductStartupManager.RestoreAsync();
                Shutdown(exitCode);
                return;
            }

            var waitUpdatePlanIndex = Array.FindIndex(e.Args, argument =>
                string.Equals(argument, "--wait-update-plan", StringComparison.OrdinalIgnoreCase));
            if (waitUpdatePlanIndex >= 0)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var planFile = waitUpdatePlanIndex + 1 < e.Args.Length ? e.Args[waitUpdatePlanIndex + 1] : string.Empty;
                var cancelFile = waitUpdatePlanIndex + 2 < e.Args.Length ? e.Args[waitUpdatePlanIndex + 2] : string.Empty;
                var installDirectory = waitUpdatePlanIndex + 3 < e.Args.Length ? e.Args[waitUpdatePlanIndex + 3] : string.Empty;
                var processIdText = waitUpdatePlanIndex + 4 < e.Args.Length ? e.Args[waitUpdatePlanIndex + 4] : string.Empty;
                var parentProcessId = int.TryParse(processIdText, out var parsedProcessId) ? parsedProcessId : 0;
                var exitCode = await ApplicationUpdateService.WaitForAuthorizedUpdatePlanAsync(
                    planFile,
                    cancelFile,
                    installDirectory,
                    parentProcessId);
                Shutdown(exitCode);
                return;
            }

            var applyUpdateIndex = Array.FindIndex(e.Args, argument =>
                string.Equals(argument, "--apply-update", StringComparison.OrdinalIgnoreCase));
            if (applyUpdateIndex >= 0)
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var planFile = applyUpdateIndex + 1 < e.Args.Length ? e.Args[applyUpdateIndex + 1] : string.Empty;
                var exitCode = await ApplicationUpdateService.ApplyUpdatePlanAsync(planFile);
                Shutdown(exitCode);
                return;
            }

            var environmentValidation = EnvironmentDownloadSettings.ValidateConfiguration();
            if (!environmentValidation.IsValid)
            {
                var validationMessage = environmentValidation.ToDisplayMessage();
                WriteLifecycleError(
                    "环境下载配置校验失败",
                    new System.Configuration.ConfigurationErrorsException(validationMessage));
            }

            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var startInTray = ApplicationLaunchMode.IsTrayStartupRequest(e.Args);
            _uiMutex = new Mutex(true, UiMutexName, out var isPrimaryUi);
            if (!isPrimaryUi)
            {
                if (!startInTray)
                {
                    SignalPrimaryInstance();
                }

                Shutdown();
                return;
            }

            InitializeActivationListener();
            TryNormalizeStartupRegistration();
            try
            {
                _trayIcon = new TrayIconService(ShowMainWindow, ExitApplication);
            }
            catch (Exception ex)
            {
                WriteLifecycleError("创建系统托盘图标失败", ex);
                if (startInTray)
                {
                    MessageBox.Show(
                        $"无法创建系统托盘图标，MCPanel 将改为打开主窗口。\n\n{ex.Message}",
                        "MCPanel",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }
            }

            if (!startInTray || _trayIcon is null)
            {
                if (!environmentValidation.IsValid)
                {
                    MessageBox.Show(
                        environmentValidation.ToDisplayMessage() +
                        "\n\n请修正 MCPanel.exe.config 后重新启动；环境安装不会使用静默回退地址。",
                        "环境下载配置",
                        MessageBoxButton.OK,
                        MessageBoxImage.Warning);
                }

                ShowMainWindow();
            }
            else
            {
                _ = ScheduleUiMemoryRelease(Volatile.Read(ref _uiGeneration));
            }

            // Match normal manual Tomcat startup at Windows logon: when MCPanel
            // is launched through the --tray Run entry, immediately restore the
            // shared server with the same visible Catalina CMD launcher. No
            // startup delay or hidden-console path is used here.
            if (startInTray)
            {
                _ = TomcatLogonStartup.TryRestoreSharedTomcatAsync();
            }

            // The embedded API follows the MCPanel process. The Windows
            // startup toggle only decides whether MCPanel itself is launched;
            // the Account API page's enable switch decides whether the API is
            // started inside that process.
            _ = EnsureAccountApiAtStartup();
        }
        catch (Exception ex)
        {
            WriteLifecycleError("MCPanel 启动失败", ex);
            if (!Dispatcher.HasShutdownStarted)
            {
                MessageBox.Show(
                    $"MCPanel 启动失败。\n\n{ex.Message}",
                    "MCPanel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
                Shutdown(1);
            }
        }
    }

    internal void ExitApplication()
    {
        if (_isExiting)
        {
            return;
        }

        _isExiting = true;
        _trayIcon?.Hide();
        _mainWindow?.Close();
        Shutdown();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _isExiting = true;
        _activationRegistration?.Unregister(null);
        _activationRegistration = null;
        _activationEvent?.Dispose();
        _activationEvent = null;
        _trayIcon?.Dispose();
        _trayIcon = null;

        if (_uiMutex is not null)
        {
            try { _uiMutex.ReleaseMutex(); } catch { }
            _uiMutex.Dispose();
            _uiMutex = null;
        }

        AccountApiManagerService.StopEmbeddedRuntime();

        base.OnExit(e);
    }

    private void ShowMainWindow()
    {
        if (_isExiting || Dispatcher.HasShutdownStarted)
        {
            return;
        }

        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke((Action)ShowMainWindow);
            return;
        }

        Interlocked.Increment(ref _uiGeneration);
        _windowActivation.Show(
            () =>
            {
                var window = new MainWindow();
                MainWindow = window;
                window.Closed += MainWindow_Closed;
                return window;
            },
            RestoreAndActivate,
            window =>
            {
                // Failed windows must really close, bypassing close-to-tray.
                window.Closed -= MainWindow_Closed;
                if (ReferenceEquals(MainWindow, window)) MainWindow = null;
                ((MainWindow)window).CloseAfterActivationFailure();
            },
            ex =>
            {
                WriteLifecycleError("打开主窗口失败", ex);
                MessageBox.Show(
                    $"无法打开 MCPanel 主窗口。\n\n{ex.Message}",
                    "MCPanel",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
    }

    private void MainWindow_Closed(object? sender, EventArgs e)
    {
        if (sender is not MainWindow window)
        {
            return;
        }

        window.Closed -= MainWindow_Closed;
        if (ReferenceEquals(MainWindow, window))
        {
            MainWindow = null;
        }

        if (_isExiting)
        {
            return;
        }

        if (_trayIcon is null)
        {
            ExitApplication();
            return;
        }

        _trayIcon?.ShowBackgroundTip();
        _ = ScheduleUiMemoryRelease(Volatile.Read(ref _uiGeneration));
    }

    private async Task ScheduleUiMemoryRelease(int closedGeneration)
    {
        try
        {
            await Task.Delay(750);
            if (!CanReleaseUiMemory(closedGeneration))
            {
                return;
            }

            await Task.Run(() => PanelMemoryService.Release(collectManagedObjects: true));

            // Closing the first WPF window can finish a few dispatcher and thread-pool
            // callbacks after Closed. Trim once more after those pages become idle.
            await Task.Delay(5000);
            if (CanReleaseUiMemory(closedGeneration))
            {
                await Task.Run(() => PanelMemoryService.Release(collectManagedObjects: false));
            }
        }
        catch (Exception ex)
        {
            WriteLifecycleError("释放后台 UI 内存失败", ex);
        }
    }

    private bool CanReleaseUiMemory(int generation) =>
        !_isExiting && _mainWindow is null && generation == Volatile.Read(ref _uiGeneration);

    private void InitializeActivationListener()
    {
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName);
        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            (_, timedOut) =>
            {
                if (timedOut || _isExiting || Dispatcher.HasShutdownStarted)
                {
                    return;
                }

                Dispatcher.BeginInvoke((Action)ShowMainWindow, DispatcherPriority.Normal);
            },
            null,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    private static void SignalPrimaryInstance()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                using var activationEvent = EventWaitHandle.OpenExisting(ActivationEventName);
                activationEvent.Set();
                return;
            }
            catch (WaitHandleCannotBeOpenedException)
            {
                Thread.Sleep(40);
            }
        }
    }

    internal static void RestoreAndActivate(Window window)
    {
        if (!window.IsVisible)
        {
            window.Show();
        }

        if (window.WindowState == WindowState.Minimized)
        {
            window.WindowState = WindowState.Normal;
        }

        var handle = new WindowInteropHelper(window).Handle;
        if (handle != IntPtr.Zero)
        {
            SetForegroundWindow(handle);
        }

        window.Activate();
        window.Focus();
    }

    private static void TryNormalizeStartupRegistration()
    {
        try
        {
            new PanelSettingsService().EnsureStartupRegistrationUsesTrayMode();
        }
        catch (Exception ex)
        {
            WriteLifecycleError("更新开机自启动参数失败", ex);
        }
    }

    private static async Task EnsureAccountApiAtStartup()
    {
        try
        {
            using var manager = new AccountApiManagerService();
            if (!manager.IsEnabled)
            {
                return;
            }

            await manager.StartAsync();
        }
        catch (Exception ex)
        {
            WriteLifecycleError("开机启动 Account API 失败", ex);
        }
    }

    private static void WriteLifecycleError(string action, Exception exception)
    {
        try
        {
            var workDirectory = ComponentPaths.WorkRoot;
            Directory.CreateDirectory(workDirectory);
            RollingLogWriter.Append(
                Path.Combine(workDirectory, "application-lifecycle-error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {action}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}");
        }
        catch
        {
            // Lifecycle diagnostics must never prevent tray startup or shutdown.
        }
    }

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr windowHandle);

}

internal static class ApplicationLaunchMode
{
    internal const string TrayArgument = "--tray";

    internal static bool IsTrayStartupRequest(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument, TrayArgument, StringComparison.OrdinalIgnoreCase));
}
