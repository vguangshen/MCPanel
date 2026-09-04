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
    public bool IsInstallationComplete => IsInstalled && !IsBusy && !IsQueued;
    public ProductInstallQueueStatus? QueueState => _queueState;
    public int QueuePosition => _queuePosition;
    public Visibility InstallButtonVisibility => IsInstalled ? Visibility.Collapsed : Visibility.Visible;
    public Visibility UninstallButtonVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility UpdateButtonVisibility => IsInstalled ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CompletedIndicatorVisibility => IsInstallationComplete ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ProgressIndicatorVisibility => IsInstallationComplete ? Visibility.Collapsed : Visibility.Visible;
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
                NotifyInstallationVisualStateChanged();
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
            NotifyInstallationVisualStateChanged();
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
                NotifyInstallationVisualStateChanged();
            }
        }
    }

    private void NotifyInstallationVisualStateChanged()
    {
        OnPropertyChanged(nameof(IsInstallationComplete));
        OnPropertyChanged(nameof(CompletedIndicatorVisibility));
        OnPropertyChanged(nameof(ProgressIndicatorVisibility));
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
