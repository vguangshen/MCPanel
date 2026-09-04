using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Media;
using System.Xml.Linq;

namespace MCPanel;

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
