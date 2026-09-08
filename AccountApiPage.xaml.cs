using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using Microsoft.Win32;

namespace MCPanel;

public partial class AccountApiPage : UserControl, INotifyPropertyChanged, IDisposable
{
    private readonly AccountApiManagerService _service = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(4) };
    private CancellationTokenSource? _refreshCancellation;
    private AccountApiSnapshot? _snapshot;
    private string _secretValue = string.Empty;
    private string _manualErrorText = string.Empty;
    private bool _secretVisible;
    private bool _isBusy;
    private bool _isRefreshing;
    private bool _disposed;
    private string _runtimeDirectoryText = "正在准备内置 Account API...";
    private string _versionText = "未识别";
    private string _endpointText = "未配置";
    private string _authModeText = "HMAC 请求签名";
    private string _databaseSummaryText = "等待检测";
    private string _healthStatusText = "尚未检测";
    private string _windowsServiceText = "MCPanel 内置";
    private string _runtimeStateText = "等待检查";
    private string _serviceModeText = "MCPanel 内置";
    private string _deviceIdText = "未读取";
    private string _checkedAtText = "尚未检查";
    private string _headerHintText = "Account API 已集成在 MCPanel 内，首次进入页面会自动检查运行状态。";
    private string _serviceStatusKind = "Checking";
    private string _operationStatusText = "服务由顶部开关控制";
    private string _operationHintText = "打开顶部开关后，MCPanel 会启动内置 Account API；关闭后会停止监听。";
    private string _logsText = "正在读取日志...";
    private string _logHintText = "日志内容会自动隐藏密码和签名密钥字段。";
    private bool _accountApiEnabled;

    public AccountApiPage()
    {
        InitializeComponent();
        DataContext = this;
        _timer.Tick += async (_, _) => await RefreshPageAsync();
        Providers = new ObservableCollection<AccountApiProviderViewModel>();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void PageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        // Reserve scrollbar space consistently to avoid oscillating at a breakpoint.
        var width = Math.Max(0d, e.NewSize.Width - SystemParameters.VerticalScrollBarWidth);
        ArrangeCards(MetricsGrid, width < 600d ? 1 : width < 960d ? 2 : 4);
        ArrangeCards(ConnectionGrid, width < 960d ? 1 : 2);
        ArrangeCards(DiagnosticsGrid, width < 960d ? 1 : 2);
    }

    private static void ArrangeCards(Grid grid, int columns)
    {
        var rows = (grid.Children.Count + columns - 1) / columns;
        if (grid.ColumnDefinitions.Count == columns && grid.RowDefinitions.Count == rows)
        {
            return;
        }

        grid.ColumnDefinitions.Clear();
        grid.RowDefinitions.Clear();
        for (var column = 0; column < columns; column++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1d, GridUnitType.Star) });
        for (var row = 0; row < rows; row++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (var index = 0; index < grid.Children.Count; index++)
        {
            var card = (FrameworkElement)grid.Children[index];
            Grid.SetRow(card, index / columns);
            Grid.SetColumn(card, index % columns);
            card.Margin = new Thickness(0d, 0d, index % columns < columns - 1 ? 8d : 0d,
                index / columns < rows - 1 ? 8d : 0d);
        }
    }

    public ObservableCollection<AccountApiProviderViewModel> Providers { get; }

    public string RuntimeDirectoryText { get => _runtimeDirectoryText; private set => SetProperty(ref _runtimeDirectoryText, value); }
    public string VersionText { get => _versionText; private set => SetProperty(ref _versionText, value); }
    public string EndpointText { get => _endpointText; private set => SetProperty(ref _endpointText, value); }
    public string AuthModeText { get => _authModeText; private set => SetProperty(ref _authModeText, value); }
    public string DatabaseSummaryText { get => _databaseSummaryText; private set => SetProperty(ref _databaseSummaryText, value); }
    public string HealthStatusText { get => _healthStatusText; private set => SetProperty(ref _healthStatusText, value); }
    public string WindowsServiceText { get => _windowsServiceText; private set => SetProperty(ref _windowsServiceText, value); }
    public string RuntimeStateText { get => _runtimeStateText; private set => SetProperty(ref _runtimeStateText, value); }
    public string ServiceModeText { get => _serviceModeText; private set => SetProperty(ref _serviceModeText, value); }
    public string DeviceIdText { get => _deviceIdText; private set => SetProperty(ref _deviceIdText, value); }
    public string CheckedAtText { get => _checkedAtText; private set => SetProperty(ref _checkedAtText, value); }
    public string HeaderHintText { get => _headerHintText; private set => SetProperty(ref _headerHintText, value); }
    public string ServiceStatusKind { get => _serviceStatusKind; private set => SetProperty(ref _serviceStatusKind, value); }
    public string OperationStatusText { get => _operationStatusText; private set => SetProperty(ref _operationStatusText, value); }
    public string OperationHintText { get => _operationHintText; private set => SetProperty(ref _operationHintText, value); }
    public string LogsText { get => _logsText; private set => SetProperty(ref _logsText, value); }
    public string LogHintText { get => _logHintText; private set => SetProperty(ref _logHintText, value); }

    public bool AccountApiEnabled
    {
        get => _accountApiEnabled;
        private set => SetProperty(ref _accountApiEnabled, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(string.Empty);
            }
        }
    }

    public bool HasSigningSecret => _snapshot?.Configuration.HasSigningSecret == true;
    public string SecretToggleText => _secretVisible ? "隐藏" : "显示";
    public string SecretDisplayText
    {
        get
        {
            if (!HasSigningSecret)
            {
                return "尚未生成 HMAC 签名密钥";
            }

            return _secretVisible
                ? _secretValue
                : new string('•', Math.Min(32, Math.Max(12, _secretValue.Length)));
        }
    }

    public bool CanInteract => !IsBusy;
    public bool CanToggleEnabled => !IsBusy;
    public bool CanGenerateSecret =>
        !IsBusy &&
        _snapshot?.Configuration.ConfigExists == true;
    public bool CanCopySecret => !IsBusy && HasSigningSecret;
    public bool CanImportSettings => !IsBusy && !AccountApiEnabled;
    public bool CanOpenRuntime => _snapshot?.Configuration.RuntimeExists == true;
    public bool CanEditEndpoint => !IsBusy && _snapshot?.Configuration.ConfigExists == true;
    public bool CanOpenLogs => _snapshot?.Configuration.RuntimeExists == true;
    public string ProviderEmptyText =>
        _snapshot is not null && !_snapshot.Enabled
            ? "账号 API 当前未启用，开启开关后才会检测数据库连接。"
            : _snapshot?.HealthStatus == "not_checked"
            ? "云端连接配置已就绪。点击“检测”查看数据库连接状态。"
            : _snapshot?.HealthStatus == "check_failed"
            ? "数据库检测未完成；本地桥接仍在线，请检查数据库可达性后重试。"
            : _snapshot?.Reachable == true
            ? "数据库连接由 MarchCenter 云端管理；本地桥接在线，可等待云端下发配置。"
            : "内置服务未在线，暂时没有可展示的数据库连接状态。";
    public Visibility ProviderEmptyVisibility => Providers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    public string ErrorText => _snapshot is null
        ? _manualErrorText
        : string.IsNullOrWhiteSpace(_manualErrorText)
            ? _snapshot.ErrorText
            : _manualErrorText;
    public Visibility ErrorVisibility => string.IsNullOrWhiteSpace(ErrorText)
        ? Visibility.Collapsed
        : Visibility.Visible;

    public async Task ActivateAsync()
    {
        if (_disposed)
        {
            return;
        }

        _timer.Start();
        // Opening a management page should be read-only. Starting a local
        // process may show UAC, so the user explicitly chooses Start instead.
        await RefreshPageAsync();
    }

    public void Deactivate()
    {
        _timer.Stop();
        _refreshCancellation?.Cancel();
    }

    private async Task RefreshPageAsync(bool checkDatabases = false)
    {
        if (_disposed || _isRefreshing || IsBusy)
        {
            return;
        }

        _isRefreshing = true;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = new CancellationTokenSource();
        var cancellationToken = _refreshCancellation.Token;

        try
        {
            var snapshot = await _service.RefreshAsync(cancellationToken, checkDatabases);
            if (_disposed || cancellationToken.IsCancellationRequested || IsBusy) return;
            ApplySnapshot(snapshot);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception error)
        {
            _manualErrorText = error.Message;
            OperationStatusText = "读取 Account API 状态失败";
            OperationHintText = "请确认内置数据目录和配置文件可读，然后重试。";
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _isRefreshing = false;
        }
    }

    internal void ApplySnapshot(AccountApiSnapshot snapshot)
    {
        _snapshot = snapshot;
        _secretValue = snapshot.Configuration.SigningSecret;
        AccountApiEnabled = snapshot.Enabled;
        RuntimeDirectoryText = snapshot.Configuration.RuntimeExists
            ? snapshot.Configuration.RuntimeDirectory
            : string.IsNullOrWhiteSpace(snapshot.Configuration.RuntimeDirectory)
                ? "内置 Account API 尚未配置"
                : snapshot.Configuration.RuntimeDirectory;
        VersionText = snapshot.Version;
        var effectiveBindAddress = string.IsNullOrWhiteSpace(snapshot.EffectiveBindAddress)
            ? snapshot.Configuration.BindAddress
            : snapshot.EffectiveBindAddress;
        EndpointText = snapshot.Configuration.RuntimeExists
            ? AccountApiConfiguration.FormatUriHost(effectiveBindAddress) + ":" + snapshot.Configuration.Port
            : "未配置";
        if (!string.Equals(effectiveBindAddress, snapshot.Configuration.BindAddress, StringComparison.OrdinalIgnoreCase))
        {
            EndpointText += "（权限不足，已回退本机）";
        }
        else if (string.Equals(snapshot.Configuration.BindAddress, "0.0.0.0", StringComparison.Ordinal))
        {
            EndpointText += "（允许外部访问）";
        }
        if (!snapshot.Enabled && snapshot.Configuration.RuntimeExists)
        {
            EndpointText += "（未启用）";
        }

        AuthModeText = string.Equals(
            snapshot.Configuration.AuthenticationMode,
            "Hmac",
            StringComparison.OrdinalIgnoreCase)
            ? "HMAC 请求签名"
            : snapshot.Configuration.AuthenticationMode;
        DatabaseSummaryText = BuildDatabaseSummary(snapshot);
        HealthStatusText = BuildHealthStatus(snapshot) + (snapshot.DatabaseCheckedAt.HasValue
            ? " · " + snapshot.DatabaseCheckedAt.Value.ToLocalTime().ToString("HH:mm:ss") : string.Empty);
        WindowsServiceText = snapshot.Enabled ? snapshot.WindowsServiceStatus : "未启用";
        RuntimeStateText = !snapshot.Enabled
            ? "未启用"
            : snapshot.Reachable
            ? "本地桥接在线"
            : !snapshot.Configuration.RuntimeExists
                ? "未部署"
                : snapshot.LocalProcessRunning
                    ? "进程运行中但接口未响应"
                    : "未响应";
        ServiceModeText = !snapshot.Enabled
            ? "手动启用"
            : snapshot.Configuration.RuntimeExists ? "MCPanel 内置" : "未配置";
        DeviceIdText = string.IsNullOrWhiteSpace(snapshot.DeviceId)
            ? "未读取"
            : snapshot.DeviceId;
        CheckedAtText = snapshot.CheckedAt == default
            ? "尚未检查"
            : snapshot.CheckedAt.ToLocalTime().ToString("HH:mm:ss");
        LogsText = snapshot.LogsText;
        LogHintText = snapshot.Configuration.RuntimeExists
            ? snapshot.Configuration.LogDirectory
            : "尚未创建日志目录";
        ServiceStatusKind = GetStatusKind(snapshot);
        HeaderHintText = BuildHeaderHint(snapshot);
        Providers.Clear();
        foreach (var provider in snapshot.Providers)
        {
            Providers.Add(new AccountApiProviderViewModel(provider));
        }

        if (string.IsNullOrWhiteSpace(snapshot.ErrorText))
        {
            _manualErrorText = string.Empty;
        }

        OnPropertyChanged(string.Empty);
    }

    private static string BuildDatabaseSummary(AccountApiSnapshot snapshot)
    {
        if (!snapshot.Enabled)
        {
            return "未启用";
        }

        if (snapshot.Providers.Count == 0)
        {
            return snapshot.Reachable ? "云端管理" : "未检测";
        }

        var healthy = snapshot.Providers.Count(provider => provider.IsHealthy);
        return healthy == snapshot.Providers.Count
            ? healthy + " 个连接正常"
            : healthy + " 正常 / " + (snapshot.Providers.Count - healthy) + " 异常";
    }

    private static string BuildHealthStatus(AccountApiSnapshot snapshot)
    {
        if (!snapshot.Enabled)
        {
            return "手动启用后检测";
        }

        if (!snapshot.Reachable)
        {
            return "等待服务响应";
        }

        return snapshot.HealthStatus switch
        {
            "ok" => "全部连接正常",
            "setup_required" => "尚无云端连接配置",
            "not_checked" => "点击检测数据库连接",
            "check_failed" => "数据库检测未完成",
            "degraded" => "至少一个连接异常",
            _ => snapshot.HealthStatus
        };
    }

    private static string GetStatusKind(AccountApiSnapshot snapshot)
    {
        if (!snapshot.Enabled)
        {
            return "Disabled";
        }

        if (!snapshot.Configuration.RuntimeExists)
        {
            return "Missing";
        }

        if (!snapshot.Reachable)
        {
            return "Offline";
        }

        return snapshot.Healthy || snapshot.HealthStatus == "setup_required"
            ? "Healthy"
            : "Degraded";
    }

    private static string BuildHeaderHint(AccountApiSnapshot snapshot)
    {
        if (!snapshot.Enabled)
        {
            return "账号 API 当前未启用，打开开关后 MCPanel 才会开始监听端口。";
        }

        if (!snapshot.Configuration.RuntimeExists)
        {
            return "Account API 已内置在 MCPanel 中，无需额外安装执行端。";
        }

        if (!snapshot.Configuration.ConfigExists)
        {
            return "内置 Account API 配置文件缺失，MCPanel 会在可写目录中自动创建。";
        }

        if (!snapshot.Configuration.HasSigningSecret)
        {
            return "内置服务已就绪，正在等待生成 HMAC 签名密钥。";
        }

        return snapshot.Reachable
            ? "本地桥接已在线；云端管理连接配置，本机执行请求并展示诊断信息。"
            : "账号 API 已启用，但接口尚未响应；请检查监听端口和运行日志。";
    }

    private async Task RunOperationAsync(string operation, Func<Task> action)
    {
        if (_disposed || IsBusy)
        {
            return;
        }

        IsBusy = true;
        _refreshCancellation?.Cancel();
        _manualErrorText = string.Empty;
        OperationStatusText = operation;
        OperationHintText = "正在执行，请稍候...";
        var operationLabel = operation.TrimEnd('…', '.');
        if (operationLabel.StartsWith("正在", StringComparison.Ordinal))
        {
            operationLabel = operationLabel.Substring(2);
        }
        try
        {
            await action();
            OperationStatusText = BuildOperationResultText(operationLabel, succeeded: true);
            OperationHintText = "状态和日志将在操作完成后自动刷新。";
        }
        catch (Exception error)
        {
            _manualErrorText = error.Message;
            OperationStatusText = BuildOperationResultText(operationLabel, succeeded: false);
            OperationHintText = "请检查权限、端口和 Account API 日志。";
            MessageBox.Show(error.Message, "账号管理 API", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            await RefreshPageAsync();
        }
    }

    private static string BuildOperationResultText(string operationLabel, bool succeeded)
    {
        return operationLabel switch
        {
            "启用账号 API" => succeeded ? "账号 API 已启用" : "账号 API 启用失败",
            "停用账号 API" => succeeded ? "账号 API 已停用" : "账号 API 停用失败",
            "修改监听端口" => succeeded ? "监听端口已更新" : "监听端口修改失败",
            _ => operationLabel + (succeeded ? "已完成" : "失败")
        };
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        _manualErrorText = string.Empty;
        await RefreshPageAsync();
    }

    private async void DetectDatabases_Click(object sender, RoutedEventArgs e)
    {
        await RunOperationAsync("正在检测数据库连接…", async () =>
        {
            var snapshot = await _service.RefreshAsync(checkDatabases: true);
            if (!_disposed) ApplySnapshot(snapshot);
        });
    }


    private async void EditEndpoint_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy || _snapshot?.Configuration.ConfigExists != true)
        {
            return;
        }

        var currentPort = _snapshot.Configuration.Port;
        var dialog = PanelInputDialog.CreateAccountApiPortEditor(currentPort);
        var owner = Window.GetWindow(this);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        if (dialog.ShowDialog() != true ||
            !int.TryParse(dialog.ResultText, out var port) ||
            port == currentPort)
        {
            return;
        }

        await RunOperationAsync(
            "正在修改监听端口…",
            () => _service.SetPortAsync(port));
    }

    private async void Enabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggleButton || IsBusy)
        {
            return;
        }

        // AccountApiEnabled is the authoritative state.  The switch uses a
        // one-way binding so accessibility Invoke (and some touch/keyboard
        // paths) can raise Click without first changing IsChecked.
        var enabled = !AccountApiEnabled;
        toggleButton.IsChecked = enabled;
        // Reflect the user's choice immediately. Health checks can take several
        // seconds when a configured database is unavailable.
        AccountApiEnabled = enabled;
        ServiceStatusKind = enabled ? "Starting" : "Disabled";
        await RunOperationAsync(
            enabled ? "正在启用账号 API…" : "正在停用账号 API…",
            async () =>
            {
                if (!enabled)
                {
                    await _service.SetEnabledAsync(false);
                    return;
                }

                await _service.SetEnabledAsync(true);
                try
                {
                    await _service.StartAsync();
                }
                catch
                {
                    // Do not leave a failed start persisted as "enabled". Otherwise
                    // the next refresh and the next MCPanel startup keep retrying a
                    // service that the user was just told failed to start.
                    try
                    {
                        await _service.SetEnabledAsync(false);
                    }
                    catch
                    {
                        // Preserve the original startup error; RefreshPageAsync will
                        // surface any remaining persisted-state mismatch afterwards.
                    }
                    throw;
                }
            });
    }

    private async void SelectRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (!CanImportSettings) return;
        var dialog = new OpenFileDialog
        {
            Title = "导入 Account API 配置",
            Filter = "Account API 配置|config.ini|配置文件 (*.ini)|*.ini",
            CheckFileExists = true,
            Multiselect = false
        };
        if (Directory.Exists(_service.ConfiguredRuntimeDirectory))
        {
            dialog.InitialDirectory = _service.ConfiguredRuntimeDirectory;
        }

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            _service.SetRuntimeDirectory(dialog.FileName);
            _manualErrorText = string.Empty;
            OperationStatusText = "已导入 Account API 配置";
            OperationHintText = "配置已复制到 MCPanel 数据目录；如需运行请打开顶部开关。";
            await RefreshPageAsync();
        }
        catch (Exception error)
        {
            _manualErrorText = error.Message;
            OnPropertyChanged(string.Empty);
            MessageBox.Show(error.Message, "账号管理 API", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OpenRuntime_Click(object sender, RoutedEventArgs e) => RunUiAction(_service.OpenRuntimeDirectory);
    private void OpenLogs_Click(object sender, RoutedEventArgs e) => RunUiAction(_service.OpenLogDirectory);

    private void ToggleSecret_Click(object sender, RoutedEventArgs e)
    {
        _secretVisible = !_secretVisible;
        OnPropertyChanged(nameof(SecretToggleText));
        OnPropertyChanged(nameof(SecretDisplayText));
    }

    private void CopySecret_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!HasSigningSecret)
            {
                throw new InvalidOperationException("尚未生成 HMAC 签名密钥。");
            }

            Clipboard.SetText(_secretValue);
            ClipboardSafety.ScheduleClear(_secretValue);
            OperationStatusText = "HMAC 密钥已复制";
            OperationHintText = "请把同一枚密钥填入 MarchCenter 网页后台的账号管理 API 配置。";
        }
        catch (Exception error)
        {
            _manualErrorText = error.Message;
            OnPropertyChanged(string.Empty);
        }
    }

    private async void GenerateSecret_Click(object sender, RoutedEventArgs e)
    {
        if (HasSigningSecret &&
            MessageBox.Show(
                "生成新密钥后，网页后台保存的旧密钥会立即失效，是否继续？",
                "更换 HMAC 密钥",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        await RunOperationAsync("正在生成 HMAC 密钥…", async () =>
        {
            var secret = _service.GenerateSigningSecret();
            _secretVisible = true;
            try
            {
                Clipboard.SetText(secret);
                ClipboardSafety.ScheduleClear(secret);
            }
            catch
            {
            }

            var shouldRestart = _service.IsLocalProcessRunning();
            if (shouldRestart)
            {
                await _service.RestartAsync();
            }
        });
    }

    private void RunUiAction(Action action)
    {
        try
        {
            action();
        }
        catch (Exception error)
        {
            _manualErrorText = error.Message;
            OnPropertyChanged(string.Empty);
            MessageBox.Show(error.Message, "账号管理 API", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnPropertyChanged(string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _timer.Stop();
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _service.Dispose();
    }
}

public sealed class AccountApiProviderViewModel
{
    public AccountApiProviderViewModel(AccountApiProviderStatus source)
    {
        Name = string.IsNullOrWhiteSpace(source.Name) ? source.Id : source.Name;
        IsHealthy = source.IsHealthy;
        Error = source.Error;
        LatencyText = source.LatencyMs is int latency ? latency + " ms" : string.Empty;
        StatusText = source.IsHealthy
            ? "连接正常"
            : string.IsNullOrWhiteSpace(source.Error) ? "连接失败" : source.Error;
    }

    public string Name { get; }
    public bool IsHealthy { get; }
    public string Error { get; }
    public string LatencyText { get; }
    public string StatusText { get; }
}
