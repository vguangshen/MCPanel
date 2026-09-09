using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Data;

namespace MCPanel;

public sealed partial class MainViewModel
{
    private string[] _websiteSearchTerms = [];
    private int _websiteTypeFilter;
    private int _websiteStateFilter;
    private bool _compactWebsites;
    private bool _updatingWebsiteRows;
    private bool _persistWebsitePreferences;
    private string _websiteRecordError = string.Empty;
    private string _websiteOperationText = "每 30 秒更新服务状态；仅在此页可见时检测";
    private readonly HashSet<string> _pinnedWebsites = new(StringComparer.OrdinalIgnoreCase);
    private readonly WebsiteRefreshSchedule _websiteRefreshSchedule = new();
    private readonly SemaphoreSlim _websitePreferencesLock = new(1, 1);
    private readonly WebsiteRowCollection _websiteRows = new();
    public ObservableCollection<WebsiteRow> WebsiteRows => _websiteRows;
    public ICollectionView WebsiteView { get; private set; } = null!;
    public int WebsiteTypeFilter { get => _websiteTypeFilter; set { if (SetProperty(ref _websiteTypeFilter, value)) RefreshWebsiteFilter(); } }
    public int WebsiteStateFilter { get => _websiteStateFilter; set { if (SetProperty(ref _websiteStateFilter, value)) RefreshWebsiteFilter(); } }
    public bool CompactWebsites { get => _compactWebsites; set { if (SetProperty(ref _compactWebsites, value)) _ = SaveWebsitePreferencesAsync(); } }
    public string WebsiteRecordError { get => _websiteRecordError; private set { SetProperty(ref _websiteRecordError, value); OnPropertyChanged(nameof(WebsiteRecordErrorVisibility)); } }
    public Visibility WebsiteRecordErrorVisibility => string.IsNullOrEmpty(WebsiteRecordError) ? Visibility.Collapsed : Visibility.Visible;
    public string WebsiteOperationText { get => _websiteOperationText; set => SetProperty(ref _websiteOperationText, value); }

    private void InitializeWebsiteFeatures(bool persist)
    {
        _persistWebsitePreferences = persist;
        if (persist)
        {
            try
            {
                var file = Path.Combine(ComponentPaths.ProductStateRoot, "website-view.json");
                if (File.Exists(file))
                {
                    var settings = JsonSerializer.Deserialize<WebsiteViewPreferences>(File.ReadAllText(file));
                    _compactWebsites = settings?.Compact ?? false;
                    foreach (var id in settings?.Pinned ?? []) _pinnedWebsites.Add(id);
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException) { }
        }
        WebsiteView = new ListCollectionView(WebsiteRows);
        WebsiteView.Filter = item => ((WebsiteRow)item).Item switch
        {
            InstalledProductItem product => MatchesWebsite(product),
            CustomWebsiteItem custom => MatchesWebsite(custom),
            _ => false
        };
        WebsiteView.SortDescriptions.Add(new SortDescription(nameof(WebsiteRow.IsPinned), ListSortDirection.Descending));
        WebsiteView.SortDescriptions.Add(new SortDescription(nameof(WebsiteRow.Order), ListSortDirection.Ascending));
    }

    private bool MatchesWebsiteOptions(bool java, bool running) =>
        (_websiteTypeFilter == 0 || (_websiteTypeFilter == 1 ? java : !java)) &&
        (_websiteStateFilter == 0 || (_websiteStateFilter == 1 ? running : !running));

    private void RefreshWebsiteFilter()
    {
        VisibleInstalledWebsites.Refresh(); VisibleCustomWebsites.Refresh(); WebsiteView.Refresh(); NotifyWebsiteFilter();
    }

    private void SyncWebsiteRows()
    {
        if (_updatingWebsiteRows) return;
        var old = WebsiteRows.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<WebsiteRow>();
            foreach (var item in CustomWebsites.Cast<object>().Concat(InstalledProducts))
            {
                var id = item is InstalledProductItem product ? "product:" + product.ProductId : "iis:" + ((CustomWebsiteItem)item).Definition.Id;
                var row = old.TryGetValue(id, out var existing) && ReferenceEquals(existing.Item, item)
                    ? existing : new WebsiteRow(id, item);
                row.IsPinned = _pinnedWebsites.Contains(id);
                row.Order = rows.Count;
                rows.Add(row);
            }
        _websiteRows.Replace(rows);
        NotifyWebsiteFilter();
    }

    internal async Task ToggleWebsitePinAsync(WebsiteRow row)
    {
        if (!_pinnedWebsites.Add(row.Id)) _pinnedWebsites.Remove(row.Id);
        row.IsPinned = _pinnedWebsites.Contains(row.Id);
        WebsiteView.Refresh();
        await SaveWebsitePreferencesAsync();
    }

    private async Task SaveWebsitePreferencesAsync()
    {
        if (!_persistWebsitePreferences) return;
        var settings = new WebsiteViewPreferences(CompactWebsites, _pinnedWebsites.ToArray());
        await _websitePreferencesLock.WaitAsync();
        try { await Task.Run(() => AtomicFile.WriteAllText(Path.Combine(ComponentPaths.ProductStateRoot, "website-view.json"), JsonSerializer.Serialize(settings))); }
        catch (Exception ex) { WebsiteOperationText = "视图设置未保存：" + ex.Message; }
        finally { _websitePreferencesLock.Release(); }
    }

    internal async Task RefreshWebsiteStatesAsync(bool visible, bool minimized, bool force = false)
    {
        if (_disposed) return;
        if (force) _websiteRefreshSchedule.RequestRefresh();
        if (!_websiteRefreshSchedule.TryStart(DateTime.UtcNow, visible, minimized, WebsiteRows.Count > 0)) return;
        var products = InstalledProducts.Where(item => !item.Product.IsBusy).ToArray();
        var custom = CustomWebsites.ToArray();
        var java = products.Where(item => item.IsTomcatDeployment && item.RuntimePort > 0).Select(item => (item.ProductId, item.RuntimePort)).ToArray();
        var hasIis = custom.Length > 0 || products.Any(item => item.IisInfo is not null);
        var directories = products.Select(item => item.InstallPath).Concat(custom.Select(item => item.PhysicalPath)).ToArray();
        var token = _driveCancellation.Token;
        try
        {
            var snapshot = await Task.Run(() => WebsiteRuntimeSnapshot.Capture(hasIis, java, directories, token), token);
            if (_disposed) return;
            WebsiteRuntimeSnapshot.Latest = snapshot;
            var oldStates = products.Select(item => item.CanBrowse).ToArray();
            var oldCustom = custom.Select(item => item.IsRunning).ToArray();
            foreach (var item in products) if (InstalledProducts.Contains(item)) item.ApplyRuntimeSnapshot(snapshot);
            foreach (var item in custom) if (CustomWebsites.Contains(item)) item.RefreshStatus(snapshot.Iis, snapshot.Directories[item.PhysicalPath]);
            if (!oldStates.SequenceEqual(products.Select(item => item.CanBrowse)) || !oldCustom.SequenceEqual(custom.Select(item => item.IsRunning))) RefreshWebsiteFilter();
            WebsiteOperationText = "服务状态更新于 " + DateTime.Now.ToString("HH:mm:ss") + " · 自动间隔 30 秒";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) WebsiteOperationText = "状态暂未更新：" + ex.Message; }
        finally { _websiteRefreshSchedule.Complete(DateTime.UtcNow); }
    }

    private sealed record WebsiteViewPreferences(bool Compact, string[] Pinned);

    private sealed class WebsiteRowCollection : ObservableCollection<WebsiteRow>
    {
        internal void Replace(IEnumerable<WebsiteRow> rows)
        {
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }
}

public sealed class WebsiteRow(string id, object item) : ObservableObject
{
    private bool _pinned;
    private bool _expanded;
    public string Id { get; } = id;
    public object Item { get; } = item;
    public bool IsCustom => Item is CustomWebsiteItem;
    public string Name => Item is InstalledProductItem product ? product.DisplayName : ((CustomWebsiteItem)Item).Name;
    public string Path => Item is InstalledProductItem product ? product.InstallPath : ((CustomWebsiteItem)Item).PhysicalPath;
    public int Order { get; set; }
    public bool IsPinned { get => _pinned; set { if (SetProperty(ref _pinned, value)) OnPropertyChanged(nameof(PinText)); } }
    public string PinText => IsPinned ? "★" : "☆";
    public bool IsExpanded { get => _expanded; set => SetProperty(ref _expanded, value); }
}
