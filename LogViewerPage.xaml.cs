using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace MCPanel;

public sealed record LogModuleOption(LogModule Module, string Name);

/// <summary>
/// Shows the newest log of the selected component. Choosing a module opens
/// its most recently written file; other files of the module stay one pick
/// away in the file list. Reading happens off the UI thread.
/// </summary>
public partial class LogViewerPage : UserControl, INotifyPropertyChanged, IDisposable
{
    internal static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(3);
    private const double NarrowWidth = 760d;
    private const double MinimumFillHeight = 440d;

    private readonly LogBrowser _browser;
    private readonly DispatcherTimer _autoRefreshTimer;
    private int _loadVersion;
    private bool _updatingFiles;
    private bool _activated;
    private bool _disposed;
    private bool _followEnd = true;
    private LogFileEntry? _selectedFile;
    private string _status = "请选择要查看的模块。";
    private string _emptyText = "请选择要查看的模块。";

    public LogViewerPage() : this(new LogBrowser()) { }

    internal LogViewerPage(LogBrowser browser)
    {
        _browser = browser;
        InitializeComponent();
        DataContext = this;
        ModuleList.ItemsSource = LogBrowser.Modules
            .Select(module => new LogModuleOption(module, LogBrowser.DisplayName(module)))
            .ToArray();
        _autoRefreshTimer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = AutoRefreshInterval };
        _autoRefreshTimer.Tick += (_, _) => _ = RefreshAsync(keepSelection: true);
        IsVisibleChanged += (_, _) => UpdateAutoRefresh();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LogModuleOption? SelectedModule => ModuleList.SelectedItem as LogModuleOption;

    public LogFileEntry? SelectedFile
    {
        get => _selectedFile;
        private set
        {
            if (SetProperty(ref _selectedFile, value))
            {
                OnPropertyChanged(nameof(HasFile));
            }
        }
    }

    public bool HasFile => SelectedFile is not null;

    public string Status
    {
        get => _status;
        private set => SetProperty(ref _status, value);
    }

    public string EmptyText
    {
        get => _emptyText;
        private set => SetProperty(ref _emptyText, value);
    }

    public Visibility EmptyVisibility => string.IsNullOrEmpty(LogBox.Text) ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Called when the page is shown: selects the first module once, then refreshes.</summary>
    public void Activate()
    {
        if (_disposed)
        {
            return;
        }

        if (!_activated)
        {
            _activated = true;
            ModuleList.SelectedIndex = 0;
            return;
        }

        _ = RefreshAsync(keepSelection: true);
    }

    /// <summary>Selects a module and shows its newest log.</summary>
    internal Task ShowModuleAsync(LogModule module)
    {
        _activated = true;
        var option = ModuleList.Items.OfType<LogModuleOption>().First(item => item.Module == module);
        if (ReferenceEquals(ModuleList.SelectedItem, option))
        {
            return RefreshAsync(keepSelection: false);
        }

        ModuleList.SelectedItem = option;
        return _pendingLoad ?? Task.CompletedTask;
    }

    private Task? _pendingLoad;

    private void ModuleList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        _pendingLoad = RefreshAsync(keepSelection: false);

    private void FileComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingFiles || FileComboBox.SelectedItem is not LogFileEntry file)
        {
            return;
        }

        _pendingLoad = LoadFileAsync(file, scrollToEnd: true);
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => _ = RefreshAsync(keepSelection: true);

    private void AutoRefresh_Changed(object sender, RoutedEventArgs e) => UpdateAutoRefresh();

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedFile is not { } file)
        {
            return;
        }

        try
        {
            ShellOpen.Folder(file.Folder);
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException or ArgumentException or System.IO.IOException)
        {
            Status = $"无法打开文件夹：{file.Folder}";
        }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(LogBox.Text))
        {
            return;
        }

        try
        {
            Clipboard.SetText(LogBox.Text);
            Status = "已复制当前显示的日志内容。";
        }
        catch (Exception error) when (error is System.Runtime.InteropServices.ExternalException)
        {
            Status = "剪贴板正被其他程序占用，请稍后再试。";
        }
    }

    private void LogPageScroll_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (LogPageLayout is null)
        {
            return;
        }

        // Fill the viewport so the log box, not the page, scrolls. Only a very
        // short window lets the page scroll, with a fixed-height log box.
        var fill = e.NewSize.Height >= MinimumFillHeight;
        LogPageLayout.Height = fill ? e.NewSize.Height : double.NaN;
        LogBox.Height = fill ? double.NaN : 260d;
        var narrow = e.NewSize.Width < NarrowWidth;
        Grid.SetRow(LogActions, narrow ? 1 : 0);
        Grid.SetColumn(LogActions, narrow ? 0 : 1);
        Grid.SetColumnSpan(LogActions, narrow ? 2 : 1);
        Grid.SetColumnSpan(FileComboBox, narrow ? 2 : 1);
        LogActions.Margin = narrow ? new Thickness(0d, 10d, 0d, 0d) : new Thickness(10d, 0d, 0d, 0d);
    }

    /// <summary>
    /// Re-lists the module's files. With <paramref name="keepSelection"/> the
    /// file the user picked stays shown while it exists; otherwise, and when
    /// the user is following the newest file, the newest file is shown.
    /// </summary>
    internal async Task RefreshAsync(bool keepSelection)
    {
        if (_disposed || SelectedModule is not { } module)
        {
            return;
        }

        var version = ++_loadVersion;
        var followingNewest = !keepSelection || FileComboBox.SelectedIndex <= 0;
        var previousPath = SelectedFile?.Path;
        IReadOnlyList<LogFileEntry> files;
        try
        {
            files = await Task.Run(() => _browser.ListFiles(module.Module));
        }
        catch (Exception error)
        {
            if (version == _loadVersion)
            {
                ShowNothing($"读取 {module.Name} 日志目录失败：{error.Message}");
            }

            return;
        }

        if (_disposed || version != _loadVersion)
        {
            return;
        }

        if (files.Count == 0)
        {
            ShowNothing($"未找到 {module.Name} 的日志文件。组件安装并运行后会在这里显示。");
            return;
        }

        var target = followingNewest
            ? files[0]
            : files.FirstOrDefault(file => string.Equals(file.Path, previousPath, StringComparison.OrdinalIgnoreCase)) ?? files[0];
        _updatingFiles = true;
        try
        {
            FileComboBox.ItemsSource = files;
            FileComboBox.SelectedItem = target;
        }
        finally
        {
            _updatingFiles = false;
        }

        var unchanged = keepSelection && SelectedFile is { } shown &&
            string.Equals(shown.Path, target.Path, StringComparison.OrdinalIgnoreCase) &&
            shown.Length == target.Length && shown.LastWriteTime == target.LastWriteTime;
        if (unchanged)
        {
            SelectedFile = target;
            Status = DescribeStatus(module, files.Count, target);
            return;
        }

        await LoadFileAsync(target, scrollToEnd: !keepSelection || _followEnd, version, files.Count);
    }

    private Task LoadFileAsync(LogFileEntry file, bool scrollToEnd) =>
        LoadFileAsync(file, scrollToEnd, ++_loadVersion, FileComboBox.Items.Count);

    private async Task LoadFileAsync(LogFileEntry file, bool scrollToEnd, int version, int fileCount)
    {
        var text = await Task.Run(() => LogBrowser.ReadLatest(file.Path));
        if (_disposed || version != _loadVersion || SelectedModule is not { } module)
        {
            return;
        }

        SelectedFile = file;
        LogBox.Text = text;
        EmptyText = "该日志文件目前没有内容。";
        OnPropertyChanged(nameof(EmptyVisibility));
        Status = DescribeStatus(module, fileCount, file);
        if (scrollToEnd)
        {
            ScrollLogToEnd();
        }
    }

    /// <summary>
    /// Shows the newest lines. Repeated after layout because a log loaded
    /// while the page is still hidden or being measured has no extent yet.
    /// </summary>
    private void ScrollLogToEnd()
    {
        _followEnd = true;
        LogBox.CaretIndex = LogBox.Text.Length;
        LogBox.ScrollToEnd();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            LogBox.CaretIndex = LogBox.Text.Length;
            LogBox.ScrollToEnd();
        }));
    }

    private static string DescribeStatus(LogModuleOption module, int fileCount, LogFileEntry file)
    {
        var tail = file.Length > LogBrowser.TailBytes ? $"，显示末尾 {LogBrowser.TailBytes / 1024} KB" : string.Empty;
        return $"{module.Name} · 共 {fileCount} 个日志文件 · {file.Path}（{file.LastWriteTime:yyyy-MM-dd HH:mm:ss} 更新{tail}）· 读取于 {DateTime.Now:HH:mm:ss}";
    }

    private void ShowNothing(string message)
    {
        _updatingFiles = true;
        try
        {
            FileComboBox.ItemsSource = null;
        }
        finally
        {
            _updatingFiles = false;
        }

        SelectedFile = null;
        LogBox.Text = string.Empty;
        EmptyText = message;
        Status = message;
        OnPropertyChanged(nameof(EmptyVisibility));
    }

    private bool IsScrolledToEnd() =>
        string.IsNullOrEmpty(LogBox.Text) ||
        LogBox.VerticalOffset + LogBox.ViewportHeight >= LogBox.ExtentHeight - 4d;

    /// <summary>
    /// Keeps the newest line in view while the user follows the end, also
    /// when a resize re-wraps the text; scrolling up stops following.
    /// </summary>
    private void LogBox_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ExtentHeightChange == 0d && e.ViewportHeightChange == 0d)
        {
            _followEnd = IsScrolledToEnd();
        }
        else if (_followEnd)
        {
            LogBox.ScrollToEnd();
        }
    }

    private void UpdateAutoRefresh()
    {
        var run = !_disposed && IsVisible && AutoRefreshToggle.IsChecked == true;
        if (run && !_autoRefreshTimer.IsEnabled)
        {
            _autoRefreshTimer.Start();
        }
        else if (!run && _autoRefreshTimer.IsEnabled)
        {
            _autoRefreshTimer.Stop();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _autoRefreshTimer.Stop();
    }

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
