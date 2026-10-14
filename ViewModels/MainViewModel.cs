using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Xml.Linq;

namespace MCPanel;

public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    internal ComponentOperationCoordinator ComponentOperations { get; } = new();
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
    private string _panelMemoryUsageText = "正在读取...";
    private string _updateStatus = "支持 HTTPS 在线更新与经过校验的本地更新包。";
    private double _updateProgress;
    private bool _isUpdateBusy;
    private string _productSearchKeyword = string.Empty;
    private string _websiteSearchKeyword = string.Empty;
    private string _selectedProductCategory = "全部";
    private int _visibleProductCount;
    private CancellationTokenSource? _productIconCacheCancellation;
    private CancellationTokenSource? _installedProductsRefreshCancellation;
    private bool _disposed;
    private readonly CancellationTokenSource _driveCancellation = new();
    private bool _driveRefreshInFlight;
    private DateTime _nextDriveRefreshUtc;
    private readonly Func<CancellationToken, DriveSnapshotResult> _captureDrives;

    internal async Task RefreshDrivesAsync()
    {
        if (_disposed || _driveRefreshInFlight || DateTime.UtcNow < _nextDriveRefreshUtc) return;
        _driveRefreshInFlight = true;
        var token = _driveCancellation.Token;
        try
        {
            var result = await Task.Run(() => _captureDrives(token), token);
            if (_disposed || token.IsCancellationRequested) return;
            foreach (var snapshot in result.Drives)
            {
                var existing = Drives.FirstOrDefault(item => item.Name.Equals(snapshot.Name, StringComparison.OrdinalIgnoreCase));
                if (existing is null) Drives.Add(new DriveItem(snapshot.Name, snapshot.Used, snapshot.Total));
                else { existing.UpdateTarget(snapshot.Used, snapshot.Total); existing.SnapToTarget(); }
            }
            if (result.IsComplete)
            {
                var names = result.Drives.Select(drive => drive.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                for (var index = Drives.Count - 1; index >= 0; index--)
                    if (!names.Contains(Drives[index].Name)) Drives.RemoveAt(index);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally
        {
            _nextDriveRefreshUtc = DateTime.UtcNow.AddSeconds(10);
            _driveRefreshInFlight = false;
        }
    }

    public MainViewModel() : this(true) { }

    internal MainViewModel(bool initializeData, Func<CancellationToken, DriveSnapshotResult>? captureDrives = null)
    {
        _captureDrives = captureDrives ?? DriveSnapshotService.Capture;
        VisibleInstalledWebsites = new ListCollectionView(InstalledProducts);
        VisibleCustomWebsites = new ListCollectionView(CustomWebsites);
        VisibleInstalledWebsites.Filter = item => MatchesWebsite((InstalledProductItem)item);
        VisibleCustomWebsites.Filter = item => MatchesWebsite((CustomWebsiteItem)item);
        InitializeWebsiteFeatures(initializeData);
        InstalledProducts.CollectionChanged += (_, _) => SyncWebsiteRows();
        CustomWebsites.CollectionChanged += (_, _) => SyncWebsiteRows();
        Services = new ObservableCollection<ServiceItem>(EnvironmentComponentCatalog.HomeOrder.Select(kind =>
        {
            var definition = EnvironmentComponentCatalog.Get(kind);
            return new ServiceItem(definition.ServiceTitle, kind, definition.ServiceDescription);
        }));
        EnvironmentItems = new ObservableCollection<EnvironmentItem>(EnvironmentComponentCatalog.All.Select(definition =>
            new EnvironmentItem(definition.Kind, definition.Title, definition.VersionLabel, definition.InstallDescription)));
        EnvironmentItems.First(item => item.Kind == EnvironmentKind.FrpTunnel).StatusText =
            "FRP 尚未安装。点击“安装”后才会联网下载客户端。";

        ComponentOperations.BusyChanged += (kind, busy) =>
        {
            foreach (var item in EnvironmentItems.Where(item => item.Kind == kind)) item.IsBusy = busy;
            foreach (var service in Services.Where(service => service.Kind == kind)) service.IsBusy = busy;
        };

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
        if (initializeData)
        {
            LoadCachedProducts();
            LoadHardwareSummary();
        }
    }

    public string VersionText => ApplicationUpdateService.CurrentVersionText;
    public ObservableCollection<ServiceItem> Services { get; }
    public ObservableCollection<EnvironmentItem> EnvironmentItems { get; }
    public ObservableCollection<ProductItem> Products { get; }
    public ObservableCollection<InstalledProductItem> InstalledProducts { get; } = [];
    public ObservableCollection<CustomWebsiteItem> CustomWebsites { get; } = [];
    public ICollectionView VisibleInstalledWebsites { get; }
    public ICollectionView VisibleCustomWebsites { get; }
    public string WebsiteSearchKeyword
    {
        get => _websiteSearchKeyword;
        set
        {
            if (!SetProperty(ref _websiteSearchKeyword, value ?? string.Empty)) return;
            _websiteSearchTerms = _websiteSearchKeyword.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            RefreshWebsiteFilter();
        }
    }
    private int VisibleWebsiteCount => WebsiteView?.Cast<object>().Count() ?? 0;
    public string WebsiteSearchCountText => string.IsNullOrWhiteSpace(WebsiteSearchKeyword) && WebsiteTypeFilter == 0
        ? InstalledProductCountText : $"找到 {VisibleWebsiteCount} / {InstalledProducts.Count + CustomWebsites.Count} 个网站";
    public void ClearWebsiteFilters()
    {
        WebsiteSearchKeyword = string.Empty;
        WebsiteTypeFilter = 0;
    }
    public Visibility InstalledWebsiteGroupVisibility => VisibleInstalledWebsites.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility CustomWebsiteGroupVisibility => VisibleCustomWebsites.IsEmpty ? Visibility.Collapsed : Visibility.Visible;
    public Visibility WebsiteNoResultsVisibility => VisibleWebsiteCount == 0 && InstalledProducts.Count + CustomWebsites.Count > 0
        ? Visibility.Visible : Visibility.Collapsed;
    private bool MatchesWebsite(InstalledProductItem item) => MatchesWebsiteOptions(item.IsTomcatDeployment) && MatchesWebsiteText(item.DisplayName, item.ProductId,
        item.InstallPath, item.EnvironmentSummary, item.Url, item.DomainDisplayText, item.SiteDisplayText);
    private bool MatchesWebsite(CustomWebsiteItem item) => MatchesWebsiteOptions(false) && MatchesWebsiteText(item.Name, item.PhysicalPath,
        item.Url, item.DomainSummary, item.PoolSummary, "IIS");
    internal bool MatchesWebsiteText(params string[] fields)
    {
        return _websiteSearchTerms.All(term => fields.Any(field => (field ?? string.Empty).IndexOf(term, StringComparison.OrdinalIgnoreCase) >= 0));
    }
    private void NotifyWebsiteFilter()
    {
        OnPropertyChanged(nameof(WebsiteSearchCountText));
        OnPropertyChanged(nameof(InstalledWebsiteGroupVisibility));
        OnPropertyChanged(nameof(CustomWebsiteGroupVisibility));
        OnPropertyChanged(nameof(WebsiteNoResultsVisibility));
        OnPropertyChanged(nameof(WebsiteEmptyVisibility));
        OnPropertyChanged(nameof(WebsiteContentVisibility));
    }
    public ObservableCollection<ProductRow> VisibleProductRows { get; } = [];
    private int _productColumnCount = 3;
    public void SetProductColumnCount(int count)
    {
        count = Math.Max(1, Math.Min(3, count));
        if (_productColumnCount == count) return;
        _productColumnCount = count;
        RebuildVisibleProductRows();
    }
    public int EnvironmentColumnCount { get => _environmentColumnCount; private set => SetProperty(ref _environmentColumnCount, value); }
    private int _environmentColumnCount = 3;
    public void SetEnvironmentColumnCount(int count) => EnvironmentColumnCount = Math.Max(1, Math.Min(3, count));
    public int HomeServiceColumnCount { get => _homeServiceColumnCount; private set => SetProperty(ref _homeServiceColumnCount, value); }
    private int _homeServiceColumnCount = 3;
    public void SetHomeServiceColumnCount(int count) => HomeServiceColumnCount = Math.Max(1, Math.Min(3, count));
    public Visibility ProductNoResultsVisibility => _visibleProductCount == 0 && Products.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
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
        : "集中管理产品网站与自定义 IIS 网站，包括域名、SSL 和平台绑定。";
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
    public string PanelMemoryUsageText { get => _panelMemoryUsageText; set => SetProperty(ref _panelMemoryUsageText, value); }
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
        for (var index = 0; index < matches.Count; index += _productColumnCount)
        {
            VisibleProductRows.Add(new ProductRow(matches.Skip(index).Take(_productColumnCount).ToArray(), _productColumnCount));
        }

        _visibleProductCount = matches.Count;
        OnPropertyChanged(nameof(ProductCountText));
        OnPropertyChanged(nameof(ProductNoResultsVisibility));
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

    public void ApplyRuntimeStates(
        IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> states,
        EnvironmentRuntimeState? frpState,
        IReadOnlyDictionary<EnvironmentKind, string?> installDirectories,
        bool preserveBusy = false,
        IReadOnlyDictionary<EnvironmentKind, string>? probeErrors = null)
    {
        foreach (var item in EnvironmentItems)
        {
            if (preserveBusy && item.IsBusy)
            {
                continue;
            }

            if (probeErrors is not null && probeErrors.TryGetValue(item.Kind, out var error))
            {
                item.ApplyProbeFailure(error);
                continue;
            }
            if (item.Kind == EnvironmentKind.FrpTunnel && frpState is not null)
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

            if (probeErrors is not null && probeErrors.TryGetValue(service.Kind, out var error))
            {
                service.ApplyProbeFailure(error);
                continue;
            }
            if (service.Kind == EnvironmentKind.FrpTunnel && frpState is not null)
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

            WebsiteRecordError = installedProducts.Error;
            var installedById = installedProducts.Items.ToDictionary(
                item => item.Product.ProductId,
                StringComparer.OrdinalIgnoreCase);
            foreach (var product in Products)
            {
                product.IsInstalled = installedById.ContainsKey(product.ProductId);
            }

            ApplyInstalledProducts(installedProducts.Items);
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

    private static (List<(ProductItem Product, string InstallPath, DateTime InstalledAtUtc)> Items, string Error) DetectInstalledProducts(
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

        try
        {
            foreach (var definition in new ManualPlatformStore().Load())
            {
                cancellationToken.ThrowIfCancellationRequested();
                installedProducts.Add((definition.ToProduct(), definition.Path, GetDirectoryCreationTimeUtc(definition.Path)));
            }
        }
        catch (IOException ex) { return (installedProducts, ex.Message); }
        return (installedProducts, string.Empty);
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

        _updatingWebsiteRows = true;
        InstalledProducts.Clear();
        foreach (var item in installedProducts.OrderBy(item => item.Product.InstallSequence))
        {
            InstalledProducts.Add(new InstalledProductItem(item.Product, item.InstallPath));
        }
        _updatingWebsiteRows = false;
        SyncWebsiteRows();

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
        _driveCancellation.Cancel();
        _driveCancellation.Dispose();
        _installedProductsRefreshCancellation?.Cancel();
        _productIconCacheCancellation?.Cancel();
        _productIconCache.Dispose();
    }

    public void RefreshCustomWebsites(IReadOnlyList<CustomWebsiteDefinition> definitions)
    {
        _updatingWebsiteRows = true;
        CustomWebsites.Clear();
        foreach (var definition in definitions)
        {
            CustomWebsites.Add(new CustomWebsiteItem(definition));
        }
        _updatingWebsiteRows = false;
        SyncWebsiteRows();

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

        _ = RefreshDrivesAsync();

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
