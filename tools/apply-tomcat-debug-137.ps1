$ErrorActionPreference = 'Stop'

function Read-Normalized([string]$Path) {
    return ([System.IO.File]::ReadAllText($Path)).Replace("`r`n", "`n")
}

function Write-Normalized([string]$Path, [string]$Content) {
    [System.IO.File]::WriteAllText($Path, $Content.Replace("`r`n", "`n"), [System.Text.UTF8Encoding]::new($false))
}

function Replace-Once([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Normalized $Path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne 1) {
        throw "$Path: expected exactly one match, found $count.`n---OLD---`n$Old"
    }
    Write-Normalized $Path ($text.Replace($Old, $New))
}

function Replace-AllExact([string]$Path, [string]$Old, [string]$New, [int]$ExpectedCount) {
    $text = Read-Normalized $Path
    $count = ([regex]::Matches($text, [regex]::Escape($Old))).Count
    if ($count -ne $ExpectedCount) {
        throw "$Path: expected $ExpectedCount matches, found $count.`n---OLD---`n$Old"
    }
    Write-Normalized $Path ($text.Replace($Old, $New))
}

# -----------------------------------------------------------------------------
# TomcatProductInstanceManager: runtime mode model + safe mode switching + tools.
# -----------------------------------------------------------------------------
$path = 'TomcatProductInstanceManager.cs'
Replace-Once $path @'
namespace MCPanel;

public sealed class TomcatProductInstanceManager
'@ @'
namespace MCPanel;

public enum TomcatProductRuntimeMode
{
    Stopped,
    Shared,
    Independent,
    Catalina,
    PortConflict
}

public sealed class TomcatProductRuntimeInfo
{
    public TomcatProductRuntimeInfo(
        TomcatProductRuntimeMode mode,
        int port,
        bool portListening,
        int? processId = null,
        DateTime? startedAt = null)
    {
        Mode = mode;
        Port = port;
        PortListening = portListening;
        ProcessId = processId;
        StartedAt = startedAt;
    }

    public TomcatProductRuntimeMode Mode { get; }
    public int Port { get; }
    public bool PortListening { get; }
    public int? ProcessId { get; }
    public DateTime? StartedAt { get; }
    public bool IsRunning => Mode is TomcatProductRuntimeMode.Shared or TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina;
}

public sealed class TomcatProductInstanceManager
'@

Replace-Once $path @'
    public string GetLogDirectory(string productId) =>
        Path.Combine(GetInstanceRoot(productId), "logs");

    public async Task<string> StartAsync(string productId, bool catalinaMode, CancellationToken cancellationToken = default)
'@ @'
    public string GetLogDirectory(string productId) =>
        Path.Combine(GetInstanceRoot(productId), "logs");

    public string GetInstanceDirectory(string productId) => GetInstanceRoot(productId);

    public static TomcatProductRuntimeInfo GetRuntimeInfo(string productId)
    {
        var deployment = ProductDeploymentService.LoadTomcatDeploymentInfo(productId);
        if (deployment is null || deployment.Port is <= 0 or > 65535)
        {
            return new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Stopped, deployment?.Port ?? 0, false);
        }

        var port = deployment.Port;
        var portListening = IsTcpPortListening(port);
        var tomcatHome = FindTomcatRoot();
        var instanceRoot = GetInstanceRoot(productId);
        var instanceProcessIds = Directory.Exists(instanceRoot)
            ? GetJavaProcessIdsForBase(instanceRoot).ToArray()
            : Array.Empty<int>();

        if (tomcatHome is not null && portListening &&
            (TomcatWindowsServiceManager.IsRunningForRoot(tomcatHome) ||
             (IsSharedTomcatRunning() && instanceProcessIds.Length == 0)))
        {
            return new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Shared, port, true);
        }

        var listeningProcessIds = portListening
            ? GetListeningProcessIds(new[] { port })
            : Array.Empty<int>();
        var processId = instanceProcessIds.FirstOrDefault();
        if (processId <= 0)
        {
            var recorded = ReadInstanceProcessId(instanceRoot);
            if (recorded.HasValue && listeningProcessIds.Contains(recorded.Value) && IsJavaProcessId(recorded.Value))
            {
                processId = recorded.Value;
            }
        }

        if (processId > 0 || instanceProcessIds.Length > 0)
        {
            if (processId <= 0)
            {
                processId = instanceProcessIds[0];
            }

            var mode = string.Equals(ReadInstanceRunMode(instanceRoot), "Catalina", StringComparison.OrdinalIgnoreCase)
                ? TomcatProductRuntimeMode.Catalina
                : TomcatProductRuntimeMode.Independent;
            return new TomcatProductRuntimeInfo(
                mode,
                port,
                portListening,
                processId,
                TryGetProcessStartTime(processId));
        }

        if (portListening)
        {
            return new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.PortConflict, port, true);
        }

        return new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Stopped, port, false);
    }

    public static IReadOnlyList<string> GetRunningProductIds() =>
        ProductDeploymentService.LoadTomcatDeploymentInfos()
            .Select(info => info.ProductId)
            .Where(productId => GetRuntimeInfo(productId).Mode is TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(productId => productId, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    internal static string FormatRuntimeStatus(TomcatProductRuntimeInfo runtime)
    {
        var processText = runtime.ProcessId.HasValue ? $" · PID {runtime.ProcessId.Value}" : string.Empty;
        var uptimeText = string.Empty;
        if (runtime.StartedAt.HasValue)
        {
            var elapsed = DateTime.Now - runtime.StartedAt.Value;
            if (elapsed < TimeSpan.Zero)
            {
                elapsed = TimeSpan.Zero;
            }
            uptimeText = elapsed.TotalMinutes < 1
                ? " · 刚刚启动"
                : elapsed.TotalHours < 1
                    ? $" · 运行 {(int)elapsed.TotalMinutes} 分钟"
                    : $" · 运行 {(int)elapsed.TotalHours} 小时 {elapsed.Minutes} 分钟";
        }

        return runtime.Mode switch
        {
            TomcatProductRuntimeMode.Shared => "总 Tomcat 运行",
            TomcatProductRuntimeMode.Independent => $"独立运行{processText}{uptimeText}",
            TomcatProductRuntimeMode.Catalina => $"Catalina 运行{processText}{uptimeText}",
            TomcatProductRuntimeMode.PortConflict => $"端口 {runtime.Port} 被其他进程占用",
            _ => "已部署，未运行"
        };
    }

    public async Task<string> PrepareProductInstanceAsync(string productId, CancellationToken cancellationToken = default)
    {
        var info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId)
            ?? throw new InvalidOperationException($"未找到 {productId} 的 Tomcat 部署信息，请先修复绑定。");
        var tomcatHome = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");

        await ProductDeploymentService.EnsureTomcatProductServiceAsync(info, cancellationToken);
        info = ProductDeploymentService.LoadTomcatDeploymentInfo(productId) ?? info;
        return await PrepareInstanceAsync(tomcatHome, info, cancellationToken);
    }

    public async Task<string> RestartAsync(string productId, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntimeInfo(productId);
        if (runtime.Mode == TomcatProductRuntimeMode.Shared)
        {
            throw new InvalidOperationException($"{productId} 当前由总 Tomcat Server 运行。请先使用“单独启动”切换到独立模式，再单独重启该应用。");
        }
        if (runtime.Mode == TomcatProductRuntimeMode.PortConflict)
        {
            throw new InvalidOperationException($"端口 {runtime.Port} 已被其他进程占用，无法重启 {productId}。");
        }

        var catalinaMode = runtime.Mode == TomcatProductRuntimeMode.Catalina;
        if (runtime.Mode is TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina)
        {
            await StopAsync(productId, cancellationToken);
        }

        return await StartAsync(productId, catalinaMode, cancellationToken);
    }

    public async Task<string> ClearCacheAsync(string productId, bool restart, CancellationToken cancellationToken = default)
    {
        var runtime = GetRuntimeInfo(productId);
        if (runtime.Mode == TomcatProductRuntimeMode.Shared)
        {
            throw new InvalidOperationException($"{productId} 当前由总 Tomcat Server 运行。为避免影响其他应用，请先切换到独立模式后再清理独立实例缓存。");
        }
        if (runtime.Mode == TomcatProductRuntimeMode.PortConflict)
        {
            throw new InvalidOperationException($"端口 {runtime.Port} 被其他进程占用，无法安全清理 {productId} 的调试实例。");
        }

        var wasRunning = runtime.Mode is TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina;
        var catalinaMode = runtime.Mode == TomcatProductRuntimeMode.Catalina;
        if (wasRunning)
        {
            await StopAsync(productId, cancellationToken);
        }

        var instanceRoot = await PrepareProductInstanceAsync(productId, cancellationToken);
        foreach (var name in new[] { "work", "temp" })
        {
            var directory = Path.Combine(instanceRoot, name);
            DeleteDirectory(directory);
            Directory.CreateDirectory(directory);
        }
        WriteOperationLog(productId, "已清理独立实例 work/temp 缓存。");

        if (restart && wasRunning)
        {
            var startMessage = await StartAsync(productId, catalinaMode, cancellationToken);
            return $"{productId} 的 work/temp 已清理并按原运行模式重新启动。{Environment.NewLine}{startMessage}";
        }

        return restart
            ? $"{productId} 的 work/temp 已清理；该应用原本未运行，因此未自动启动。"
            : $"{productId} 的 work/temp 已清理。";
    }

    public async Task<string> StartAsync(string productId, bool catalinaMode, CancellationToken cancellationToken = default)
'@

Replace-Once $path @'
            await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));
'@ @'
            // Stop only the shared CATALINA_BASE. Do not use every product HTTP port as
            // a fallback here: those ports may belong to other independently running
            // debug instances and must remain untouched.
            await StopCatalinaBaseAsync(tomcatHome, cancellationToken, Array.Empty<int>());
'@

Replace-Once $path @'
            if (catalinaMode)
            {
                StartCatalinaConsole(tomcatHome, instanceRoot, productId);
                return $"已打开 {productId} 的 Catalina 诊断窗口，仅加载该应用，端口 {info.Port}。";
            }
'@ @'
            if (catalinaMode)
            {
                var process = StartCatalinaConsole(tomcatHome, instanceRoot, productId);
                SaveInstanceProcessId(instanceRoot, info.Port, process.Id);
                WriteInstanceRunMode(instanceRoot, "Catalina");
                WriteOperationLog(productId, $"Catalina 方式启动，端口 {info.Port}，PID {process.Id}。");
                return $"已打开 {productId} 的 Catalina 诊断窗口，仅加载该应用，端口 {info.Port}。";
            }
'@

Replace-Once $path @'
                SaveInstanceProcessId(instanceRoot, info.Port);
                WriteOperationLog(productId, $"单应用启动成功，端口 {info.Port}，PID {ReadInstanceProcessId(instanceRoot)?.ToString() ?? "未识别"}。");
'@ @'
                SaveInstanceProcessId(instanceRoot, info.Port);
                WriteInstanceRunMode(instanceRoot, "Independent");
                WriteOperationLog(productId, $"单应用启动成功，端口 {info.Port}，PID {ReadInstanceProcessId(instanceRoot)?.ToString() ?? "未识别"}。");
'@

Replace-Once $path @'
                ClearInstanceProcessId(instanceRoot);
                WriteOperationLog(productId, "单应用停止成功。");
'@ @'
                ClearInstanceProcessId(instanceRoot);
                ClearInstanceRunMode(instanceRoot);
                WriteOperationLog(productId, "单应用停止成功。");
'@

Replace-Once $path @'
                ClearInstanceProcessId(instanceRoot);
                return $"{productId} 当前未运行。";
'@ @'
                ClearInstanceProcessId(instanceRoot);
                ClearInstanceRunMode(instanceRoot);
                return $"{productId} 当前未运行。";
'@

Replace-Once $path @'
    private static void StartCatalinaConsole(string tomcatHome, string instanceRoot, string productId)
    {
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
            instanceRoot,
            useInstanceLocalErrorFile: true);
        var startInfo = BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false);
        _ = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法打开 {SafeName(productId)} 的 Tomcat Catalina 诊断窗口。");
    }
'@ @'
    private static Process StartCatalinaConsole(string tomcatHome, string instanceRoot, string productId)
    {
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(
            instanceRoot,
            useInstanceLocalErrorFile: true);
        var startInfo = BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false);
        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法打开 {SafeName(productId)} 的 Tomcat Catalina 诊断窗口。");
    }
'@

Replace-Once $path @'
    private static void SaveInstanceProcessId(string instanceRoot, int expectedHttpPort)
    {
        var processId = GetJavaProcessIdsForBase(instanceRoot).FirstOrDefault();
'@ @'
    private static void SaveInstanceProcessId(string instanceRoot, int expectedHttpPort, int? knownProcessId = null)
    {
        var processId = knownProcessId.GetValueOrDefault();
        if (processId <= 0)
        {
            processId = GetJavaProcessIdsForBase(instanceRoot).FirstOrDefault();
        }
'@

Replace-Once $path @'
    private static string GetInstancePidFile(string instanceRoot) => Path.Combine(instanceRoot, "tomcat.pid");

    public static void WriteOperationLog(string productId, string message)
'@ @'
    private static string GetInstancePidFile(string instanceRoot) => Path.Combine(instanceRoot, "tomcat.pid");
    private static string GetInstanceRunModeFile(string instanceRoot) => Path.Combine(instanceRoot, "tomcat.mode");

    private static void WriteInstanceRunMode(string instanceRoot, string mode)
    {
        try
        {
            Directory.CreateDirectory(instanceRoot);
            File.WriteAllText(GetInstanceRunModeFile(instanceRoot), mode, new UTF8Encoding(false));
        }
        catch
        {
            // Runtime mode is diagnostic metadata only; process ownership remains authoritative.
        }
    }

    private static string? ReadInstanceRunMode(string instanceRoot)
    {
        try
        {
            var file = GetInstanceRunModeFile(instanceRoot);
            return File.Exists(file) ? File.ReadAllText(file).Trim() : null;
        }
        catch
        {
            return null;
        }
    }

    private static void ClearInstanceRunMode(string instanceRoot)
    {
        try
        {
            var file = GetInstanceRunModeFile(instanceRoot);
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
        catch
        {
        }
    }

    private static DateTime? TryGetProcessStartTime(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.StartTime;
        }
        catch
        {
            return null;
        }
    }

    public static void WriteOperationLog(string productId, string message)
'@

# -----------------------------------------------------------------------------
# Installed product runtime UI model.
# -----------------------------------------------------------------------------
$path = 'ViewModels/ProductViewModels.cs'
Replace-Once $path @'
    private bool _isTomcatDeployment;
    private bool _isManagementExpanded;
'@ @'
    private bool _isTomcatDeployment;
    private bool _isManagementExpanded;
    private TomcatProductRuntimeMode _tomcatRuntimeMode = TomcatProductRuntimeMode.Stopped;
'@

Replace-Once $path @'
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
'@ @'
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
                OnPropertyChanged(nameof(CanRestartTomcatProduct));
                OnPropertyChanged(nameof(CanClearTomcatCache));
'@

Replace-Once $path @'
                OnPropertyChanged(nameof(IsIisDeployment));
                OnPropertyChanged(nameof(DeploymentLabel));
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
'@ @'
                OnPropertyChanged(nameof(IsIisDeployment));
                OnPropertyChanged(nameof(DeploymentLabel));
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
                OnPropertyChanged(nameof(CanRestartTomcatProduct));
                OnPropertyChanged(nameof(CanClearTomcatCache));
'@

Replace-Once $path @'
    public bool IsIisDeployment => !IsTomcatDeployment;
    public bool CanStartTomcatProduct => IsTomcatDeployment && !CanBrowse;
    public bool CanStopTomcatProduct => IsTomcatDeployment && CanBrowse;
'@ @'
    public bool IsIisDeployment => !IsTomcatDeployment;
    public TomcatProductRuntimeMode TomcatRuntimeMode
    {
        get => _tomcatRuntimeMode;
        private set
        {
            if (SetProperty(ref _tomcatRuntimeMode, value))
            {
                OnPropertyChanged(nameof(CanStartTomcatProduct));
                OnPropertyChanged(nameof(CanStopTomcatProduct));
                OnPropertyChanged(nameof(CanRestartTomcatProduct));
                OnPropertyChanged(nameof(CanClearTomcatCache));
            }
        }
    }
    public bool CanStartTomcatProduct => IsTomcatDeployment && TomcatRuntimeMode is TomcatProductRuntimeMode.Stopped or TomcatProductRuntimeMode.Shared;
    public bool CanStopTomcatProduct => IsTomcatDeployment && TomcatRuntimeMode is TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina;
    public bool CanRestartTomcatProduct => CanStopTomcatProduct;
    public bool CanClearTomcatCache => IsTomcatDeployment && TomcatRuntimeMode is TomcatProductRuntimeMode.Stopped or TomcatProductRuntimeMode.Independent or TomcatProductRuntimeMode.Catalina;
'@

Replace-Once $path @'
            var tomcatNetwork = IPGlobalProperties.GetIPGlobalProperties();
            var tomcatListening = tomcatNetwork.GetActiveTcpListeners().Any(endpoint => endpoint.Port == tomcatDeployment.Value.Port);

            SiteDisplayText = $"Tomcat / {ProductId}";
            PoolDisplayText = $"应用上下文：/{ProductId}";
            Url = $"http://localhost:{tomcatDeployment.Value.Port}/{ProductId}/";

            RuntimeStatusText = tomcatListening ? "运行中" : "已部署，未运行";
            RuntimeStatusBrush = tomcatListening ? Brushes.MediumSeaGreen : Brushes.Goldenrod;
            CanBrowse = tomcatListening;
            return;
'@ @'
            var runtime = TomcatProductInstanceManager.GetRuntimeInfo(ProductId);
            TomcatRuntimeMode = runtime.Mode;

            SiteDisplayText = $"Tomcat / {ProductId}";
            PoolDisplayText = $"应用上下文：/{ProductId}";
            Url = $"http://localhost:{tomcatDeployment.Value.Port}/{ProductId}/";

            RuntimeStatusText = TomcatProductInstanceManager.FormatRuntimeStatus(runtime);
            RuntimeStatusBrush = runtime.Mode switch
            {
                TomcatProductRuntimeMode.Shared => Brushes.MediumSeaGreen,
                TomcatProductRuntimeMode.Independent => Brushes.MediumSeaGreen,
                TomcatProductRuntimeMode.Catalina => Brushes.DeepSkyBlue,
                TomcatProductRuntimeMode.PortConflict => Brushes.IndianRed,
                _ => Brushes.Goldenrod
            };
            CanBrowse = runtime.IsRunning && runtime.PortListening;
            return;
'@

Replace-Once $path @'
        IsTomcatDeployment = false;
        var info = ProductDeploymentService.LoadIisDeploymentInfo(ProductId);
'@ @'
        IsTomcatDeployment = false;
        TomcatRuntimeMode = TomcatProductRuntimeMode.Stopped;
        var info = ProductDeploymentService.LoadIisDeploymentInfo(ProductId);
'@

# -----------------------------------------------------------------------------
# Product card actions.
# -----------------------------------------------------------------------------
$path = 'MainWindow.Products.cs'
Replace-Once $path @'
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
'@ @'
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

            if (action is "OpenLogs" or "OpenInstance")
            {
                var instanceRoot = await _tomcatInstanceManager.PrepareProductInstanceAsync(item.ProductId);
                var target = action == "OpenLogs"
                    ? Path.Combine(instanceRoot, "logs")
                    : instanceRoot;
                Directory.CreateDirectory(target);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                item.RefreshRuntime();
                return;
            }

            item.SetOperationState(action switch
            {
                "Start" => "正在独立启动",
                "Catalina" => "正在以 Catalina 方式启动",
                "Stop" => "正在停止",
                "Restart" => "正在重启独立实例",
                "ClearCache" => "正在清理 work/temp",
                "ClearCacheRestart" => "正在清理缓存并重启",
                _ => "正在处理"
            });

            var message = action switch
            {
                "Start" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: false),
                "Catalina" => await _tomcatInstanceManager.StartAsync(item.ProductId, catalinaMode: true),
                "Stop" => await _tomcatInstanceManager.StopAsync(item.ProductId),
                "Restart" => await _tomcatInstanceManager.RestartAsync(item.ProductId),
                "ClearCache" => await _tomcatInstanceManager.ClearCacheAsync(item.ProductId, restart: false),
                "ClearCacheRestart" => await _tomcatInstanceManager.ClearCacheAsync(item.ProductId, restart: true),
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
'@

# -----------------------------------------------------------------------------
# Shared Tomcat start confirmation: independent debug sessions are user-owned.
# -----------------------------------------------------------------------------
$path = 'MainWindow.Environment.cs'
Replace-Once $path @'
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
'@ @'
        if (action == "Uninstall")
        {
            var message = BuildEnvironmentUninstallConfirmation(item);
            var confirm = MessageBox.Show(message, "环境", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes)
            {
                return;
            }
        }

        if (item.Kind == EnvironmentKind.Tomcat && action is "Start" or "Restart")
        {
            var runningProducts = TomcatProductInstanceManager.GetRunningProductIds();
            if (runningProducts.Count > 0)
            {
                var preview = string.Join("、", runningProducts.Take(8));
                if (runningProducts.Count > 8)
                {
                    preview += $" 等 {runningProducts.Count} 个应用";
                }
                var confirm = MessageBox.Show(
                    $"当前有独立 Tomcat 调试实例正在运行：{preview}。{Environment.NewLine}{Environment.NewLine}" +
                    "启动总 Tomcat Server 会先停止这些独立实例，以避免端口冲突。是否继续？",
                    "切换到总 Tomcat Server",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning);
                if (confirm != MessageBoxResult.Yes)
                {
                    return;
                }
            }
        }

        try
'@

# -----------------------------------------------------------------------------
# Installed product management template.
# -----------------------------------------------------------------------------
$path = 'Resources/MainWindowTemplates.xaml'
Replace-AllExact $path 'To="180"' 'To="250"' 1
Replace-AllExact $path '<Setter Property="MaxHeight" Value="180" />' '<Setter Property="MaxHeight" Value="250" />' 1

Replace-Once $path @'
                            <Button Content="启动应用" Style="{DynamicResource WebsitePrimaryActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Start" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="Catalina 诊断" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Catalina" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="停止应用" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Stop" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStopTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
'@ @'
                            <Button Content="单独启动" Style="{DynamicResource WebsitePrimaryActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Start" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="以 Catalina 方式启动" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Catalina" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="停止应用" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Stop" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanStopTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="重启应用" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="Restart" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanRestartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="查看日志" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="OpenLogs" Click="InstalledProductTomcatAction_Click"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="实例目录" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="OpenInstance" Click="InstalledProductTomcatAction_Click"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="清理 work/temp" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="ClearCache" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanClearTomcatCache}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
                            <Button Content="清理缓存并重启" Style="{DynamicResource WebsiteActionButton}" Margin="0,0,8,0"
                                    VerticalAlignment="Center"
                                    Tag="ClearCacheRestart" Click="InstalledProductTomcatAction_Click"
                                    IsEnabled="{Binding CanRestartTomcatProduct}"
                                    Visibility="{Binding IsTomcatDeployment, Converter={StaticResource BooleanToVisibility}}" />
'@

# -----------------------------------------------------------------------------
# Version and release notes.
# -----------------------------------------------------------------------------
$path = 'MCPanel.csproj'
Replace-Once $path '<Version>1.3.6</Version>' '<Version>1.3.7</Version>'
Replace-Once $path '<FileVersion>1.3.6.0</FileVersion>' '<FileVersion>1.3.7.0</FileVersion>'
Replace-Once $path '<AssemblyVersion>1.3.6.0</AssemblyVersion>' '<AssemblyVersion>1.3.7.0</AssemblyVersion>'

$path = '.github/workflows/release.yml'
Replace-Once $path "-ReleaseNotes '将共享 Tomcat Server 与 FRP 客户端改为自动启动的 Windows 服务，并取消单应用 Tomcat 的登录后自动恢复。'" "-ReleaseNotes '优化 Tomcat 总服务与单应用调试模式切换，支持多个独立实例并行调试、运行模式识别、日志与缓存维护。'"
$releaseOld = @'
          本版本完善服务器重启后的无人值守恢复：共享 Tomcat Server 与 FRP 改为 Windows 服务，单应用 Tomcat 保持用户按需启动；不改变原有产品下载链路。

          - 环境页“Tomcat Server”注册为 Automatic Windows 服务，服务器重启后无需用户登录即可启动共享 Tomcat。
          - Tomcat 服务带进程/端口健康监控和 SCM recovery；用户手动启动单应用实例时会停止共享服务，单应用实例本身不自动启动。
          - 移除旧版 MCPanelTomcatProducts 当前用户 Run 恢复项，升级后会自动清理旧启动项。
          - FRP 客户端注册为 Automatic Windows 服务，关闭 MCPanel UI 不再终止 frpc.exe，服务器重启后无需登录即可恢复。
          - FRP 服务带子进程 watchdog 与 SCM recovery，配置仍由原有 FRP 管理页面维护。
          - Nginx、MySQL、SQL Server、IIS 和原有产品下载链路保持不变。
          - 发布前执行完整可靠性测试。
'@
$releaseNew = @'
          本版本优化 Tomcat 调试工作流：总 Tomcat Server 继续承担服务器运行模式，单应用 Tomcat 保持用户按需启动并强化独立调试能力；不改变原有产品下载链路。

          - 修复启动第二个单应用 Tomcat 时可能误停止其他独立调试实例的问题，允许多个产品在各自端口并行独立运行。
          - 启动总 Tomcat Server 前会明确列出并确认当前独立实例，再统一切回共享服务器模式。
          - 产品卡片可区分“总 Tomcat 运行 / 独立运行 / Catalina 运行 / 端口冲突”，并显示独立实例 PID 与运行时长。
          - 保留“以 Catalina 方式启动”专业调试入口，并记录 Catalina/普通独立运行模式。
          - 新增单应用重启、查看日志、打开实例目录、清理 work/temp、清理缓存并按原模式重启。
          - 单应用实例仍不注册 Windows 服务、不自动启动；共享 Tomcat Server 与 FRP 的 1.3.6 服务化行为保持不变。
          - Nginx、MySQL、SQL Server、IIS、Account API 和原有产品下载链路保持不变。
          - 发布前执行完整可靠性测试。
'@
Replace-Once $path $releaseOld $releaseNew

# -----------------------------------------------------------------------------
# Regression tests for the 1.3.7 Tomcat debug contract.
# -----------------------------------------------------------------------------
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatDebug137.cs'
$testContent = @'
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatDebug_RuntimeStatus_UsesDistinctProfessionalModes()
    {
        Assert.AreEqual(
            "总 Tomcat 运行",
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Shared, 10081, true)));
        StringAssert.StartsWith(
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Independent, 10081, true, 1234)),
            "独立运行 · PID 1234");
        StringAssert.StartsWith(
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Catalina, 10081, true, 5678)),
            "Catalina 运行 · PID 5678");
        Assert.AreEqual(
            "端口 10081 被其他进程占用",
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.PortConflict, 10081, true)));
    }

    [TestMethod]
    public void TomcatDebug_IndependentSwitch_DoesNotUseAllProductPortsAsStopFallback()
    {
        var source = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        StringAssert.Contains(source, "await StopCatalinaBaseAsync(tomcatHome, cancellationToken, Array.Empty<int>());");
        Assert.IsFalse(source.Contains(
            "await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));"),
            "Independent mode must never kill other product Java processes merely because their ports are part of the shared server.xml.");
    }

    [TestMethod]
    public void TomcatDebug_ProductUi_KeepsCatalinaNameAndMaintenanceTools()
    {
        var xaml = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
        StringAssert.Contains(xaml, "Content=\"以 Catalina 方式启动\"");
        StringAssert.Contains(xaml, "Content=\"查看日志\"");
        StringAssert.Contains(xaml, "Content=\"实例目录\"");
        StringAssert.Contains(xaml, "Content=\"清理 work/temp\"");
        StringAssert.Contains(xaml, "Content=\"清理缓存并重启\"");
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }

        Assert.Fail($"Unable to locate repository file: {relativePath}");
        return string.Empty;
    }
}
'@
Write-Normalized $testPath $testContent

Write-Host 'MCPanel 1.3.7 Tomcat debug patch applied successfully.'
