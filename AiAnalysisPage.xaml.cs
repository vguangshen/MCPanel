using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class AiAnalysisPage : UserControl, INotifyPropertyChanged, IDisposable
{
    private readonly AiAnalysisService _analysisService = new();
    private readonly LogCollectorService _logCollector = new();
    private CancellationTokenSource? _operationCancellation;
    private bool _disposed;
    private AiTargetOption? _selectedTarget;
    private LogCollectionResult? _collectedLogs;
    private bool _hasAnalysisResult;
    private string _collectionStatus = "已选择 Tomcat，点击“重新采集”读取日志。";
    private string _analysisStatus = "等待分析";
    private string _providerBadgeText = "模型未配置";
    private Brush _apiStatusBrush = Brushes.Gray;
    private bool _isBusy;

    public AiAnalysisPage()
    {
        InitializeComponent();
        Targets =
        [
            new(AiLogTarget.Tomcat, "Tomcat"),
            new(AiLogTarget.Iis, "IIS"),
            new(AiLogTarget.Nginx, "Nginx"),
            new(AiLogTarget.MySql, "MySQL"),
            new(AiLogTarget.SqlServer, "SQL Server"),
            new(AiLogTarget.Frp, "FRP")
        ];
        DataContext = this;
        SelectedTarget = Targets[0];
        SelectTargetItem(SelectedTarget.Target);
        LoadProviderSettings();
    }

    public ObservableCollection<AiTargetOption> Targets { get; }

    public AiTargetOption? SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (SetProperty(ref _selectedTarget, value))
            {
                _collectedLogs = null;
                LogPreviewBox.Text = string.Empty;
                AnalysisBox.Text = string.Empty;
                _hasAnalysisResult = false;
                CollectionStatus = value is null
                    ? "请选择需要分析的组件。"
                    : $"已选择 {value.Name}，点击“重新采集”读取日志。";
                OnPropertyChanged(nameof(SelectedTargetText));
                OnPropertyChanged(nameof(LogFileCountText));
                OnPropertyChanged(nameof(LogEmptyVisibility));
                OnPropertyChanged(nameof(AnalysisEmptyVisibility));
            }
        }
    }

    public string SelectedTargetText => SelectedTarget?.Name ?? "选择组件";
    public string CollectionStatus { get => _collectionStatus; set => SetProperty(ref _collectionStatus, value); }
    public string AnalysisStatus { get => _analysisStatus; set => SetProperty(ref _analysisStatus, value); }
    public string ProviderBadgeText { get => _providerBadgeText; set => SetProperty(ref _providerBadgeText, value); }
    public Brush ApiStatusBrush { get => _apiStatusBrush; set => SetProperty(ref _apiStatusBrush, value); }

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetProperty(ref _isBusy, value))
            {
                OnPropertyChanged(nameof(BusyVisibility));
                OnPropertyChanged(nameof(CanInteract));
            }
        }
    }

    public Visibility BusyVisibility => IsBusy ? Visibility.Visible : Visibility.Collapsed;
    public bool CanInteract => !IsBusy;
    public string LogFileCountText => _collectedLogs is null ? "0 个文件" : $"{_collectedLogs.Files.Count} 个文件";
    public Visibility LogEmptyVisibility => _collectedLogs is null || string.IsNullOrWhiteSpace(_collectedLogs.Content)
        ? Visibility.Visible
        : Visibility.Collapsed;
    public Visibility AnalysisEmptyVisibility => _hasAnalysisResult
        ? Visibility.Collapsed
        : Visibility.Visible;

    private void LoadProviderSettings()
    {
        var settings = _analysisService.LoadSettings();
        UpdateProviderStatus(settings);
    }

    private void TargetOption_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string value } ||
            !Enum.TryParse<AiLogTarget>(value, ignoreCase: true, out var target))
        {
            return;
        }

        SelectedTarget = Targets.First(option => option.Target == target);
    }

    private void TargetComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (TargetComboBox.SelectedItem is not ComboBoxItem { Tag: string value } ||
            !Enum.TryParse<AiLogTarget>(value, ignoreCase: true, out var target))
        {
            return;
        }

        SelectedTarget = Targets.First(option => option.Target == target);
    }

    private void SelectTargetItem(AiLogTarget target)
    {
        var value = target.ToString();
        foreach (var item in TargetComboBox.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag as string, value, StringComparison.OrdinalIgnoreCase))
            {
                TargetComboBox.SelectedItem = item;
                return;
            }
        }
    }

    private async void CollectLogs_Click(object sender, RoutedEventArgs e)
    {
        if (IsBusy)
        {
            return;
        }

        await CollectLogsAsync();
    }

    private async Task<bool> CollectLogsAsync()
    {
        if (_disposed || SelectedTarget is null || IsBusy)
        {
            return false;
        }

        var cancellation = BeginOperation();
        try
        {
            IsBusy = true;
            CollectionStatus = $"正在采集 {SelectedTarget.Name} 日志...";
            _collectedLogs = await _logCollector.CollectAsync(SelectedTarget.Target, cancellation.Token);
            LogPreviewBox.Text = _collectedLogs.Content;
            CollectionStatus = _collectedLogs.Summary;
            OnPropertyChanged(nameof(LogFileCountText));
            OnPropertyChanged(nameof(LogEmptyVisibility));
            return !string.IsNullOrWhiteSpace(_collectedLogs.Content);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            CollectionStatus = "日志采集已取消。";
            return false;
        }
        catch (Exception ex)
        {
            CollectionStatus = $"日志采集失败：{ex.Message}";
            return false;
        }
        finally
        {
            IsBusy = false;
            EndOperation(cancellation);
        }
    }

    private async void Analyze_Click(object sender, RoutedEventArgs e)
    {
        if (_disposed || SelectedTarget is null || IsBusy)
        {
            return;
        }

        CancellationTokenSource? cancellation = null;
        try
        {
            if (_collectedLogs is null || string.IsNullOrWhiteSpace(_collectedLogs.Content))
            {
                if (!await CollectLogsAsync())
                {
                    AnalysisStatus = "没有可分析的日志。";
                    return;
                }
            }

            IsBusy = true;
            cancellation = BeginOperation();
            _hasAnalysisResult = false;
            OnPropertyChanged(nameof(AnalysisEmptyVisibility));
            AnalysisStatus = $"模型正在分析 {SelectedTarget.Name} 日志...";
            AnalysisBox.Text = await _analysisService.AnalyzeAsync(
                SelectedTarget.Name,
                _collectedLogs!.Content,
                IssueBox.Text,
                cancellation.Token);
            _hasAnalysisResult = !string.IsNullOrWhiteSpace(AnalysisBox.Text);
            OnPropertyChanged(nameof(AnalysisEmptyVisibility));
            AnalysisStatus = $"分析完成 · {DateTime.Now:HH:mm:ss}";
        }
        catch (OperationCanceledException) when (cancellation?.IsCancellationRequested == true)
        {
            AnalysisStatus = "分析已取消。";
        }
        catch (Exception ex)
        {
            AnalysisStatus = $"分析失败：{ex.Message}";
            MessageBox.Show(ex.Message, "AI 日志分析", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            IsBusy = false;
            if (cancellation is not null)
            {
                EndOperation(cancellation);
            }
        }
    }

    private void CopyResult_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(AnalysisBox.Text))
        {
            Clipboard.SetText(AnalysisBox.Text);
            AnalysisStatus = "分析结果已复制。";
        }
    }

    private CancellationTokenSource BeginOperation()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _operationCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        return cancellation;
    }

    private void EndOperation(CancellationTokenSource cancellation)
    {
        if (ReferenceEquals(Interlocked.CompareExchange(ref _operationCancellation, null, cancellation), cancellation))
        {
            cancellation.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        var cancellation = Interlocked.Exchange(ref _operationCancellation, null);
        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private void UpdateProviderStatus(AiProviderSettings settings)
    {
        var configured = !string.IsNullOrWhiteSpace(settings.ApiKey) &&
                         !string.IsNullOrWhiteSpace(settings.Endpoint) &&
                         !string.IsNullOrWhiteSpace(settings.Model);
        ProviderBadgeText = configured
            ? $"{settings.Provider} - {settings.Model} 已配置"
            : "模型未配置";
        ApiStatusBrush = configured ? Brushes.MediumSeaGreen : Brushes.Gray;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed record AiTargetOption(AiLogTarget Target, string Name);
