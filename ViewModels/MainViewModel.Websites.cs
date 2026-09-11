using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace MCPanel;

public sealed partial class MainViewModel
{
    private string[] _websiteSearchTerms = [];
    private int _websiteTypeFilter;
    private bool _updatingWebsiteRows;
    private string _websiteRecordError = string.Empty;
    private string _websiteOperationText = "每 30 秒更新服务状态；仅在此页可见时检测";
    private readonly WebsiteRowCollection _websiteRows = new();
    public ObservableCollection<WebsiteRow> WebsiteRows => _websiteRows;
    public ICollectionView WebsiteView { get; private set; } = null!;
    public int WebsiteTypeFilter { get => _websiteTypeFilter; set { if (SetProperty(ref _websiteTypeFilter, value)) RefreshWebsiteFilter(); } }
    public string WebsiteRecordError { get => _websiteRecordError; private set { SetProperty(ref _websiteRecordError, value); OnPropertyChanged(nameof(WebsiteRecordErrorVisibility)); } }
    public Visibility WebsiteRecordErrorVisibility => string.IsNullOrEmpty(WebsiteRecordError) ? Visibility.Collapsed : Visibility.Visible;
    public string WebsiteOperationText { get => _websiteOperationText; set => SetProperty(ref _websiteOperationText, value); }

    private void InitializeWebsiteFeatures(bool persist)
    {
        _ = persist;
        WebsiteView = new ListCollectionView(WebsiteRows);
        WebsiteView.Filter = item => ((WebsiteRow)item).Item switch
        {
            InstalledProductItem product => MatchesWebsite(product),
            CustomWebsiteItem custom => MatchesWebsite(custom),
            _ => false
        };
        WebsiteView.SortDescriptions.Add(new SortDescription(nameof(WebsiteRow.Order), ListSortDirection.Ascending));
    }

    private bool MatchesWebsiteOptions(bool java) =>
        _websiteTypeFilter == 0 || (_websiteTypeFilter == 1 ? java : !java);

    private void RefreshWebsiteFilter()
    {
        VisibleInstalledWebsites.Refresh(); VisibleCustomWebsites.Refresh(); WebsiteView.Refresh(); NotifyWebsiteFilter();
    }

    internal void RefreshWebsiteFilterForRuntimeChange() => RefreshWebsiteFilter();

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
            row.Order = rows.Count;
            rows.Add(row);
        }
        _websiteRows.Replace(rows);
        WebsiteView.Refresh();
        NotifyWebsiteFilter();
    }

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
    public string Id { get; } = id;
    public object Item { get; } = item;
    public bool IsCustom => Item is CustomWebsiteItem;
    public string Name => Item is InstalledProductItem product ? product.DisplayName : ((CustomWebsiteItem)Item).Name;
    public string Path => Item is InstalledProductItem product ? product.InstallPath : ((CustomWebsiteItem)Item).PhysicalPath;
    public int Order { get; set; }
}

