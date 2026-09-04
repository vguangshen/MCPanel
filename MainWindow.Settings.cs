using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Interop;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Xml.Linq;
using Microsoft.Win32;

namespace MCPanel;

public partial class MainWindow
{
private void StartupToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _settingsService.SetStartupEnabled(_model.IsStartupEnabled);
            _model.SettingsStatus = _model.IsStartupEnabled
                ? "已开启开机自启动；下次登录 Windows 后将仅驻留托盘。"
                : "已关闭开机自启动。";
        }
        catch (Exception ex)
        {
            _model.IsStartupEnabled = _settingsService.IsStartupEnabled();
            _model.SettingsStatus = $"开机自启动设置失败：{ex.Message}";
            MessageBox.Show(_model.SettingsStatus, "面板设置", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private async void SettingsAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case "OpenStoreData":
                    _settingsService.OpenStoreDataDirectory();
                    _model.SettingsStatus = "已打开 StoreData 数据目录。";
                    break;
                case "OpenRuntime":
                    _settingsService.OpenRuntimeDirectory();
                    _model.SettingsStatus = "已打开软件根目录，可查看各组件的独立文件夹。";
                    break;
                case "OpenProducts":
                    _settingsService.OpenProductDirectory();
                    _model.SettingsStatus = "已打开产品安装目录。";
                    break;
                case "OpenLogs":
                    _settingsService.OpenLogDirectory();
                    _model.SettingsStatus = "已打开脚本和日志目录。";
                    break;
                case "ClearProductCache":
                    _settingsService.ClearProductCache();
                    _model.SettingsStatus = "已清理产品列表缓存和图标缓存。";
                    await RefreshCleanupStorageUsageAsync();
                    MessageBox.Show(_model.SettingsStatus, "面板设置", MessageBoxButton.OK, MessageBoxImage.Information);
                    break;
            }
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "面板设置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    internal async void DatabaseToolAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string action)
        {
            return;
        }

        var isNavicat = action.StartsWith("Navicat", StringComparison.Ordinal);
        var toolModel = isNavicat ? _model.NavicatTool : _model.SqlServerTool;
        try
        {
            switch (action)
            {
                case "NavicatInstall":
                    if (_settingsService.GetNavicatInstallation() is not null)
                    {
                        _model.SettingsStatus = "已检测到 Navicat，无需重复安装。";
                        return;
                    }

                    if (!ConfirmNavicatPremiumLiteInstall("安装 MySQL 连接工具"))
                    {
                        _model.SettingsStatus = "已取消安装 Navicat Premium Lite。";
                        return;
                    }

                    BeginDatabaseToolOperation(toolModel, "正在准备从 Navicat 官网下载 Premium Lite...");
                    await InstallNavicatPremiumLiteAsync(progress => UpdateDatabaseToolProgress(toolModel, progress));
                    if (_settingsService.GetNavicatInstallation() is null)
                    {
                        throw new FileNotFoundException(
                            $"Navicat Premium Lite 安装完成，但未在 {_settingsService.NavicatInstallRoot} 检测到 navicat.exe。");
                    }

                    _model.SettingsStatus = "Navicat Premium Lite 安装完成。";
                    break;

                case "SsmsInstall":
                    if (_settingsService.GetSqlManagementStudioInstallation() is not null)
                    {
                        _model.SettingsStatus = "已检测到 SQL Server Management Studio，无需重复安装。";
                        return;
                    }

                    var installSsms = MessageBox.Show(
                        "是否从微软官方下载并安装 SQL Server Management Studio？\n\n" +
                        $"安装目录：{_settingsService.SsmsInstallRoot}\n" +
                        "安装完成前请不要关闭或强制结束安装程序。",
                        "安装 SQL Server 连接工具",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Information);
                    if (installSsms != MessageBoxResult.Yes)
                    {
                        _model.SettingsStatus = "已取消安装 SQL Server Management Studio。";
                        return;
                    }

                    BeginDatabaseToolOperation(toolModel, "正在准备从微软官方下载 SQL Server Management Studio...");
                    await RunDatabaseToolOperationAsync(token =>
                        _settingsService.InstallSqlManagementStudioAsync(
                            progress => UpdateDatabaseToolProgress(toolModel, progress),
                            token));
                    if (_settingsService.GetSqlManagementStudioInstallation() is null)
                    {
                        throw new FileNotFoundException(
                            $"SQL Server Management Studio 安装完成，但未在 {_settingsService.SsmsInstallRoot} 检测到 Ssms.exe。");
                    }

                    _model.SettingsStatus = "SQL Server Management Studio 安装完成。";
                    break;

                case "NavicatUninstall":
                    await UninstallDatabaseToolAsync(DatabaseToolKind.Navicat, toolModel);
                    break;

                case "SsmsUninstall":
                    await UninstallDatabaseToolAsync(DatabaseToolKind.SqlServerManagementStudio, toolModel);
                    break;

                case "NavicatConnect":
                    if (!_settingsService.TryOpenNavicatForMySql())
                    {
                        throw new FileNotFoundException("未安装 MySQL 连接工具，请先点击“安装”或“配置路径”。");
                    }

                    var mysqlClipboard = MySqlCredentialStore.FormatForClipboard();
                    Clipboard.SetText(mysqlClipboard);
                    ClipboardSafety.ScheduleClear(mysqlClipboard);
                    _model.SettingsStatus = "已打开 Navicat，并将 MySQL 连接信息复制到剪贴板。";
                    break;

                case "SsmsConnect":
                    if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
                    {
                        throw new FileNotFoundException("未安装 SQL Server 连接工具，请先点击“安装”或“配置路径”。");
                    }

                    var sqlClipboard = SqlServerCredentialStore.FormatForClipboard();
                    Clipboard.SetText(sqlClipboard);
                    ClipboardSafety.ScheduleClear(sqlClipboard);
                    _model.SettingsStatus = "已打开 SQL Server Management Studio，并将连接信息复制到剪贴板。";
                    break;

                case "NavicatConfigure":
                    SelectDatabaseToolPath(DatabaseToolKind.Navicat);
                    break;

                case "SsmsConfigure":
                    SelectDatabaseToolPath(DatabaseToolKind.SqlServerManagementStudio);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            _model.SettingsStatus = "数据库工具操作已取消。";
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            RefreshDatabaseToolState();
        }
    }

    private async Task UninstallDatabaseToolAsync(DatabaseToolKind kind, DatabaseToolViewModel toolModel)
    {
        var installation = kind == DatabaseToolKind.Navicat
            ? _settingsService.GetNavicatInstallation()
            : _settingsService.GetSqlManagementStudioInstallation();
        if (installation is null)
        {
            throw new FileNotFoundException("当前未检测到可卸载的数据库连接工具。");
        }

        var confirm = MessageBox.Show(
            $"确定卸载 {installation.DisplayName} 吗？\n\n" +
            $"检测来源：{installation.SourceText}\n" +
            $"程序路径：{installation.ExecutablePath}\n\n" +
            "面板会调用该产品自带或 Windows 注册的卸载程序，不会扫描或直接删除其他目录。",
            "卸载数据库工具",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes)
        {
            _model.SettingsStatus = $"已取消卸载 {installation.DisplayName}。";
            return;
        }

        BeginDatabaseToolOperation(toolModel, $"正在卸载 {installation.DisplayName}...");
        var result = string.Empty;
        await RunDatabaseToolOperationAsync(async token =>
        {
            result = kind == DatabaseToolKind.Navicat
                ? await _settingsService.UninstallNavicatAsync(token)
                : await _settingsService.UninstallSqlManagementStudioAsync(token);
        });
        if (File.Exists(installation.ExecutablePath))
        {
            result += $"\n\n卸载程序已经结束，但仍检测到：{installation.ExecutablePath}" +
                      "\n可能需要重启 Windows，或卸载没有完成；请按上面的日志位置检查。";
            _model.SettingsStatus = "卸载程序已结束，但仍检测到数据库工具，请查看日志。";
            MessageBox.Show(result, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _model.SettingsStatus = result;
        MessageBox.Show(result, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    internal void DatabaseToolPath_Click(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string tag)
        {
            return;
        }

        var kind = tag switch
        {
            "NavicatPath" => DatabaseToolKind.Navicat,
            "SsmsPath" => DatabaseToolKind.SqlServerManagementStudio,
            _ => throw new InvalidOperationException("未知的数据库工具路径。")
        };

        try
        {
            _settingsService.OpenDatabaseToolDirectory(kind);
            _model.SettingsStatus = kind == DatabaseToolKind.Navicat
                ? "已打开 Navicat 安装目录。"
                : "已打开 SQL Server Management Studio 安装目录。";
        }
        catch (Exception ex)
        {
            _model.SettingsStatus = ex.Message;
            MessageBox.Show(ex.Message, "数据库工具", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        e.Handled = true;
    }

    private void SelectDatabaseToolPath(DatabaseToolKind kind)
    {
        var isNavicat = kind == DatabaseToolKind.Navicat;
        var dialog = new OpenFileDialog
        {
            Title = isNavicat
                ? "选择 Navicat 可执行文件"
                : "选择 SQL Server Management Studio 可执行文件",
            Filter = isNavicat
                ? "Navicat (navicat.exe)|navicat.exe|可执行文件 (*.exe)|*.exe"
                : "SQL Server Management Studio (Ssms.exe)|Ssms.exe|可执行文件 (*.exe)|*.exe"
        };

        var current = isNavicat
            ? _settingsService.GetNavicatInstallation()
            : _settingsService.GetSqlManagementStudioInstallation();
        if (current is not null)
        {
            dialog.InitialDirectory = Path.GetDirectoryName(current.ExecutablePath);
            dialog.FileName = Path.GetFileName(current.ExecutablePath);
        }

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        if (isNavicat)
        {
            _settingsService.SetNavicatPath(dialog.FileName);
            _model.SettingsStatus = "已保存 Navicat 配置路径。";
        }
        else
        {
            _settingsService.SetSqlManagementStudioPath(dialog.FileName);
            _model.SettingsStatus = "已保存 SQL Server Management Studio 配置路径。";
        }
    }

    private void BeginDatabaseToolOperation(DatabaseToolViewModel activeTool, string message)
    {
        _model.NavicatTool.SetBusy(true);
        _model.SqlServerTool.SetBusy(true);
        activeTool.SetProgressMessage(message);
        _model.SettingsStatus = message;
    }

    private void UpdateDatabaseToolProgress(DatabaseToolViewModel tool, InstallProgress progress)
    {
        Dispatcher.Invoke(() =>
        {
            tool.SetProgressMessage(progress.Message);
            _model.SettingsStatus = progress.Message;
        });
    }

    private void RefreshDatabaseToolState()
    {
        _model.NavicatTool.Apply(
            _settingsService.GetNavicatInstallation(),
            _settingsService.NavicatInstallRoot);
        var ssmsRecommendation = _settingsService.GetSqlManagementStudioRecommendation();
        _model.SqlServerTool.Apply(
            _settingsService.GetSqlManagementStudioInstallation(),
            _settingsService.SsmsInstallRoot,
            ssmsRecommendation.Release.DisplayName);
    }

    private bool ConfirmNavicatPremiumLiteInstall(string title)
    {
        var message =
            "未找到 Navicat。是否从 Navicat 官方下载并安装 Premium Lite？\n\n" +
            $"安装目录：{_settingsService.NavicatInstallRoot}\n" +
            "下载完成后会校验 PremiumSoft 官方数字签名。\n" +
            "Navicat 官网标注 Premium Lite 每家机构最多 5 位用户，请确认符合使用条件。";
        return MessageBox.Show(
                   message,
                   title,
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Information) == MessageBoxResult.Yes;
    }

    private async Task InstallNavicatPremiumLiteAsync(Action<InstallProgress> progress)
    {
        if (_databaseToolCancellation is not null)
        {
            throw new InvalidOperationException("已有数据库工具正在下载或安装，请稍候。");
        }

        var cancellation = new CancellationTokenSource();
        _databaseToolCancellation = cancellation;
        try
        {
            await _settingsService.InstallNavicatPremiumLiteAsync(progress, cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_databaseToolCancellation, cancellation))
            {
                _databaseToolCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task RunDatabaseToolOperationAsync(Func<CancellationToken, Task> operation)
    {
        if (_databaseToolCancellation is not null)
        {
            throw new InvalidOperationException("已有数据库工具正在下载、安装或卸载，请稍候。");
        }

        var cancellation = new CancellationTokenSource();
        _databaseToolCancellation = cancellation;
        try
        {
            await operation(cancellation.Token);
        }
        finally
        {
            if (ReferenceEquals(_databaseToolCancellation, cancellation))
            {
                _databaseToolCancellation = null;
            }

            cancellation.Dispose();
        }
    }

    private async Task RefreshCleanupStorageUsageAsync()
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _cleanupUsageRefreshCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _model.ProductCacheSizeText = "正在计算...";
        try
        {
            var usage = await _settingsService.GetCleanupStorageUsageAsync(cancellation.Token);
            if (cancellation.IsCancellationRequested) return;
            _model.ProductCacheSizeText = $"占用空间  {PanelSettingsService.FormatStorageSize(usage.ProductCacheBytes)}";
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
            _model.ProductCacheSizeText = "暂时无法统计";
        }
        finally
        {
            if (ReferenceEquals(_cleanupUsageRefreshCancellation, cancellation))
            {
                _cleanupUsageRefreshCancellation = null;
            }
            cancellation.Dispose();
        }
    }

    private async void CheckOnlineUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;
        var githubRepository = _applicationUpdateService.LoadGitHubUpdateRepository();
        if (!string.IsNullOrWhiteSpace(githubRepository))
        {
            if (!_applicationUpdateService.HasStoredGitHubUpdateToken())
            {
                MessageBox.Show(
                    $"已配置 GitHub 私有更新仓库 {githubRepository}，但当前 Windows 用户尚未保存访问令牌。\n\n请先点击“配置 GitHub”，保存一个仅限该仓库、Contents: read 权限的固定访问令牌。",
                    "软件更新",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);
                return;
            }

            await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
            {
                status.Report("正在检查 GitHub 私有 Release...");
                var source = _applicationUpdateService.LoadGitHubReleaseUpdateSource();
                var update = await _applicationUpdateService.CheckGitHubReleaseAsync(source, cancellationToken);
                if (!Version.TryParse(update.Manifest.Version, out var targetVersion) || targetVersion <= ApplicationUpdateService.CurrentVersion)
                {
                    _model.UpdateStatus = $"当前已是最新版本（{ApplicationUpdateService.CurrentVersionText}）。";
                    MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                    return null;
                }

                if (!ConfirmOnlineUpdate(update.Manifest)) return null;
                return await _applicationUpdateService.PrepareGitHubReleaseAsync(update, progress, status, cancellationToken);
            });
            return;
        }

        var manifestUrl = _applicationUpdateService.LoadManifestUrl();
        if (string.IsNullOrWhiteSpace(manifestUrl))
        {
            MessageBox.Show("尚未配置在线更新地址。请在 MCPanel.exe.config 的 UpdateManifestUrl 中填写 HTTPS 更新清单地址。", "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
        {
            status.Report("正在检查在线更新...");
            var manifest = await _applicationUpdateService.CheckOnlineAsync(manifestUrl, cancellationToken);
            if (!Version.TryParse(manifest.Version, out var targetVersion) || targetVersion <= ApplicationUpdateService.CurrentVersion)
            {
                _model.UpdateStatus = $"当前已是最新版本（{ApplicationUpdateService.CurrentVersionText}）。";
                MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
                return null;
            }

            if (!ConfirmOnlineUpdate(manifest)) return null;
            return await _applicationUpdateService.PrepareOnlineAsync(manifest, progress, status, cancellationToken);
        });
    }

    private static bool ConfirmOnlineUpdate(OnlineUpdateManifest manifest)
    {
        var notes = string.IsNullOrWhiteSpace(manifest.ReleaseNotes) ? "发布方未提供更新说明。" : manifest.ReleaseNotes.Trim();
        if (manifest.Mandatory)
        {
            MessageBox.Show(
                $"发现必须安装的新版本 {manifest.Version}\n\n{notes}\n\nMCPanel 将下载并安装此更新，完成后软件会自动重启。",
                "重要软件更新",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
            return true;
        }

        return MessageBox.Show(
            $"发现新版本 {manifest.Version}\n\n{notes}\n\n是否下载并安装？更新完成后软件会自动重启。",
            "软件更新",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information) == MessageBoxResult.Yes;
    }

    private void ConfigureGitHubUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;

        string repository;
        try
        {
            repository = ApplicationUpdateService.NormalizeGitHubRepository(
                _applicationUpdateService.LoadGitHubUpdateRepository());
        }
        catch (Exception error)
        {
            MessageBox.Show($"GitHub 更新仓库配置无效：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (repository.Length == 0)
        {
            MessageBox.Show("尚未配置 GitHub 更新仓库。", "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var hasStoredToken = _applicationUpdateService.HasStoredGitHubUpdateToken();
        var dialog = new Window
        {
            Title = "配置 GitHub 私有更新",
            Owner = this,
            Width = 470,
            Height = 338,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
            Background = (Brush)FindResource("SurfaceBrush")
        };
        var token = new PasswordBox
        {
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalContentAlignment = VerticalAlignment.Center,
            MaxLength = 256,
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("TextBrush"),
            CaretBrush = (Brush)FindResource("TextBrush"),
            BorderThickness = new Thickness(0)
        };
        var tokenInput = new Border
        {
            Height = 38,
            Padding = new Thickness(10, 0, 10, 0),
            Background = (Brush)FindResource("InputBrush"),
            BorderBrush = (Brush)FindResource("LineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Child = token
        };
        var savedState = new TextBlock
        {
            Text = hasStoredToken
                ? "当前 Windows 用户已保存固定令牌。输入新令牌后会安全替换旧令牌。"
                : "尚未保存令牌。首次保存后，MCPanel 会自动使用它检查私有 Release。",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush"),
            Margin = new Thickness(0, 6, 0, 0)
        };
        var save = new Button
        {
            Content = "安全保存",
            Width = 96,
            Height = 34,
            Style = (Style)FindResource("PrimaryButton"),
            IsDefault = true
        };
        var clear = new Button
        {
            Content = "移除令牌",
            Width = 88,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsEnabled = hasStoredToken
        };
        var cancel = new Button
        {
            Content = "取消",
            Width = 72,
            Height = 34,
            Margin = new Thickness(8, 0, 0, 0),
            Style = (Style)FindResource("TonalButton"),
            IsCancel = true
        };

        save.Click += (_, _) =>
        {
            try
            {
                _applicationUpdateService.SaveGitHubUpdateToken(token.Password);
                _model.UpdateStatus = $"已为当前 Windows 用户安全保存 GitHub 更新令牌（{repository}）。";
                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法保存 GitHub 更新令牌：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
                token.Focus();
                token.SelectAll();
            }
        };
        clear.Click += (_, _) =>
        {
            if (MessageBox.Show(
                    "移除后，这台云电脑将无法从私有 GitHub Release 检查更新，直到重新保存令牌。是否继续？",
                    "移除 GitHub 更新令牌",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Warning) != MessageBoxResult.Yes)
            {
                return;
            }

            try
            {
                _applicationUpdateService.ClearGitHubUpdateToken();
                _model.UpdateStatus = "已移除当前 Windows 用户保存的 GitHub 更新令牌。";
                dialog.DialogResult = true;
                dialog.Close();
            }
            catch (Exception error)
            {
                MessageBox.Show($"无法移除 GitHub 更新令牌：{error.Message}", "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        };

        var content = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        for (var i = 0; i < 6; i++)
        {
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }
        content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock
        {
            Text = "GitHub 私有更新",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        };
        var hint = new TextBlock
        {
            Text = "更新仓库已固定。只需保存一个固定的细粒度访问令牌；仅授予 Contents: read 权限，令牌不会写入配置文件、日志或更新包。",
            FontSize = 12,
            Margin = new Thickness(0, 5, 0, 12),
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var repositorySummary = new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Background = (Brush)FindResource("SurfaceAltBrush"),
            BorderBrush = (Brush)FindResource("CardLineBrush"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        var repositoryContent = new StackPanel();
        repositoryContent.Children.Add(new TextBlock
        {
            Text = "更新仓库（固定）",
            FontSize = 10,
            Foreground = (Brush)FindResource("MutedBrush")
        });
        repositoryContent.Children.Add(new TextBlock
        {
            Text = repository,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("TextBrush")
        });
        repositorySummary.Child = repositoryContent;
        var tokenLabel = new TextBlock
        {
            Text = "固定访问令牌",
            FontSize = 11,
            Margin = new Thickness(0, 12, 0, 4),
            Foreground = (Brush)FindResource("MutedBrush")
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        buttons.Children.Add(save);
        buttons.Children.Add(clear);
        buttons.Children.Add(cancel);

        Grid.SetRow(title, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(repositorySummary, 2);
        Grid.SetRow(tokenLabel, 3);
        Grid.SetRow(tokenInput, 4);
        Grid.SetRow(savedState, 5);
        Grid.SetRow(buttons, 7);
        content.Children.Add(title);
        content.Children.Add(hint);
        content.Children.Add(repositorySummary);
        content.Children.Add(tokenLabel);
        content.Children.Add(tokenInput);
        content.Children.Add(savedState);
        content.Children.Add(buttons);
        dialog.Content = content;
        dialog.Loaded += (_, _) => token.Focus();
        dialog.ShowDialog();
    }

    private async void LocalUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (!CanStartApplicationUpdate()) return;
        var dialog = new OpenFileDialog
        {
            Title = "选择 MCPanel 更新包",
            Filter = "MCPanel 更新包 (*.zip)|*.zip",
            CheckFileExists = true,
            Multiselect = false
        };
        if (dialog.ShowDialog(this) != true) return;

        await RunUpdatePreparationAsync(async (progress, status, cancellationToken) =>
        {
            var prepared = await _applicationUpdateService.PrepareLocalAsync(dialog.FileName, progress, status, cancellationToken);
            var comparison = Version.TryParse(prepared.Version, out var targetVersion)
                ? targetVersion.CompareTo(ApplicationUpdateService.CurrentVersion)
                : 0;
            var warning = comparison <= 0
                ? $"所选更新包版本为 {prepared.Version}，不高于当前版本 {ApplicationUpdateService.CurrentVersionText}。\n这将执行覆盖安装，是否继续？"
                : $"已验证版本 {prepared.Version} 的本地更新包。\n安装期间软件会关闭，完成后自动重启，是否继续？";
            var confirmed = MessageBox.Show(warning, "本地更新", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
            if (!confirmed)
            {
                _applicationUpdateService.DiscardPreparedUpdate(prepared);
                return null;
            }

            return prepared;
        });
    }

    private bool CanStartApplicationUpdate()
    {
        if (_model.IsUpdateBusy)
        {
            return false;
        }

        if (!_productInstallQueue.HasActiveItems)
        {
            return true;
        }

        const string message = "当前仍有产品正在下载、安装或部署。请先等待安装队列完成，或取消全部任务后再更新 MCPanel。";
        _model.UpdateStatus = message;
        MessageBox.Show(message, "软件更新", MessageBoxButton.OK, MessageBoxImage.Information);
        return false;
    }

    private void CancelApplicationUpdate_Click(object sender, RoutedEventArgs e)
    {
        _applicationUpdateCancellation?.Cancel();
    }

    private async Task RunUpdatePreparationAsync(
        Func<IProgress<double>, IProgress<string>, CancellationToken, Task<PreparedApplicationUpdate?>> prepare)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _applicationUpdateCancellation, cancellation);
        previous?.Cancel();
        previous?.Dispose();
        _model.IsUpdateBusy = true;
        _model.UpdateProgress = 0;
        PreparedApplicationUpdate? preparedForCleanup = null;
        var updaterLaunched = false;
        var progress = new Progress<double>(value => _model.UpdateProgress = Math.Max(0, Math.Min(100, value)));
        var status = new Progress<string>(value => _model.UpdateStatus = value);
        try
        {
            preparedForCleanup = await prepare(progress, status, cancellation.Token);
            if (preparedForCleanup is null)
            {
                if (!_model.UpdateStatus.StartsWith("当前已是", StringComparison.Ordinal))
                    _model.UpdateStatus = "更新操作已取消。";
                return;
            }

            cancellation.Token.ThrowIfCancellationRequested();
            _model.UpdateStatus = "正在启动安全更新器，软件即将重启...";
            _applicationUpdateService.LaunchUpdater(preparedForCleanup);
            updaterLaunched = true;
            if (Application.Current is App app)
            {
                app.ExitApplication();
            }
            else
            {
                Application.Current.Shutdown();
            }
        }
        catch (OperationCanceledException)
        {
            _model.UpdateStatus = "已取消更新，现有版本未被修改。";
        }
        catch (Exception ex)
        {
            var logPath = EnvironmentOperationDiagnostics.RecordFailure(
                "软件更新",
                "准备或启动更新器",
                ex,
                Path.Combine(AppContext.BaseDirectory, "StoreData", "Work", "application-update-error.log"));
            _model.UpdateStatus = $"更新失败：{ex.GetBaseException().Message}。详细日志：{logPath}";
            if (!_isClosed)
            {
                MessageBox.Show(_model.UpdateStatus, "软件更新", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        finally
        {
            if (!updaterLaunched && preparedForCleanup is not null)
            {
                _applicationUpdateService.DiscardPreparedUpdate(preparedForCleanup);
            }

            if (ReferenceEquals(_applicationUpdateCancellation, cancellation))
            {
                Interlocked.CompareExchange(ref _applicationUpdateCancellation, null, cancellation);
            }
            cancellation.Dispose();
            _model.IsUpdateBusy = false;
        }
    }
}
