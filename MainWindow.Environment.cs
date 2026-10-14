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
internal async void EnvironmentInstall_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        var operation = TryBeginComponentOperation(item.Kind);
        if (operation is null) return;
        try
        {
            if (item.Kind == EnvironmentKind.Iis && EnvironmentRuntimeService.HasIisUninstallContinuation)
            {
                await ContinueIisUninstallAsync(item);
                return;
            }
            item.SetBusyState("安装中", "正在准备安装...", 5);
            if (item.Kind == EnvironmentKind.FrpTunnel)
            {
                    await Task.Run(() => _frpManager.InstallAsync(progress =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            item.ApplyInstallProgress(progress);
                        });
                    }));
            }
            else if (EnvironmentInstallWorker.RequiresImmediateElevation(item.Kind))
            {
                await InstallWithImmediateElevationAsync(item);
            }
            else
            {
                await Task.Run(() => _environmentInstaller.InstallAsync(item, progress =>
                {
                    Dispatcher.Invoke(() =>
                    {
                        item.ApplyInstallProgress(progress);
                    });
                }));
            }

            if (item.Kind == EnvironmentKind.FrpTunnel)
            {
                await RefreshComponentStateAsync(item);
                MessageBox.Show("FRP 客户端已安装并启动，已注册为自动启动的 Windows 服务。可以点击“管理”修改连接和映射配置。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                item.IsInstalled = true;
                item.Progress = 100;
                item.BadgeText = "已安装";
                item.BadgeBrush = RuntimeStatusVisuals.Brush(RuntimeStatusKind.Running);
                MessageBox.Show($"{item.Title} 安装流程已完成。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (InstallRestartRequiredException ex)
        {
            item.StatusText = ex.Message;
            item.BadgeText = "需重启";
            item.BadgeBrush = Brushes.Goldenrod;
            item.MarkInstallRestartRequired();
            MessageBox.Show(ex.Message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, "安装", ex);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
        finally
        {
            operation.Dispose();
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }

    private async Task ContinueIisUninstallAsync(EnvironmentItem item)
    {
        try
        {
            item.SetBusyState("卸载中", "正在检查重启后的 IIS 组件状态...", 80);
            var message = await _runtimeService.UninstallAsync(EnvironmentKind.Iis);
            await RefreshComponentStateAsync(item);
            item.StatusText = message;
            item.Progress = 0;
            await RefreshEnvironmentStatesAsync(preserveBusy: true);
            MessageBox.Show(message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, "卸载", ex);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }

    private async Task InstallWithImmediateElevationAsync(EnvironmentItem item)
    {
        var progressPath = EnvironmentInstallWorker.CreateProgressFile();
        Process? worker = null;
        try
        {
            // Starting the worker is intentionally the first installation action. The runas
            // verb shows UAC before any download, script generation, or service work begins.
            worker = EnvironmentInstallWorker.Start(item.Kind, progressPath, item.SelectedInstallReleaseId);
            while (!worker.HasExited)
            {
                ApplyElevatedInstallProgress(item, progressPath);
                await Task.Delay(300);
            }

            ApplyElevatedInstallProgress(item, progressPath);
            if (worker.ExitCode != 0)
            {
                var hasProgress = EnvironmentInstallWorker.TryReadProgress(progressPath, out var failed);
                var message = hasProgress
                    ? failed.Message
                    : "管理员安装进程异常退出。";
                if (hasProgress && string.Equals(failed.State, "restart-required", StringComparison.OrdinalIgnoreCase))
                {
                    throw new InstallRestartRequiredException(message);
                }

                throw new EnvironmentOperationException(message, hasProgress ? failed.LogPath : null, exitCode: worker.ExitCode);
            }
        }
        finally
        {
            worker?.Dispose();
            EnvironmentInstallWorker.DeleteProgressFile(progressPath);
        }
    }

    private static void ApplyElevatedInstallProgress(EnvironmentItem item, string progressPath)
    {
        if (!EnvironmentInstallWorker.TryReadProgress(progressPath, out var progress))
        {
            return;
        }

        item.ApplyInstallProgress(new InstallProgress(
            progress.Percent,
            progress.Message,
            progress.Stage,
            progress.StagePercent,
            progress.SpeedText));
        item.BadgeText = progress.State switch
        {
            "failed" => "失败",
            "needs-repair" => "需修复",
            "restart-required" => "需重启",
            "completed" => "已完成",
            _ => progress.Stage == InstallProgressStage.Downloading ? "下载中" : "安装中"
        };
        item.BadgeBrush = progress.State switch
        {
            "failed" => Brushes.IndianRed,
            "needs-repair" => Brushes.Goldenrod,
            "restart-required" => Brushes.Goldenrod,
            _ => RuntimeStatusVisuals.Brush(RuntimeStatusKind.Starting)
        };
    }

    internal void EnvironmentOpenDirectory_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item ||
            string.IsNullOrWhiteSpace(item.InstallDirectory))
        {
            return;
        }

        if (!Directory.Exists(item.InstallDirectory))
        {
            item.InstallDirectory = null;
            MessageBox.Show("安装目录已不存在，请刷新环境状态或重新安装该组件。", "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{item.InstallDirectory}\"",
            UseShellExecute = true
        });
    }

    internal async void EnvironmentRuntime_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item ||
            sender is not Button { Tag: string action }) return;
        try
        {
            var message = await RunComponentRuntimeActionAsync(item.Kind, action);
            if (message is null) return;
            await RefreshEnvironmentStatesAsync(preserveBusy: true);
            item.StatusText = message;
            if (action == "Uninstall")
                MessageBox.Show(message, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            ShowEnvironmentOperationFailure(item, RuntimeActionText(action), ex);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }

    private async Task<string> ExecuteFrpRuntimeActionAsync(string action)
    {
        switch (action)
        {
            case "Start":
                await _frpManager.StartAsync();
                return "FRP 客户端已启动。";
            case "Stop":
                await _frpManager.StopAsync();
                return "FRP 客户端已停止，安装与配置仍保留。";
            case "Restart":
                await _frpManager.StopAsync();
                await Task.Delay(800);
                await _frpManager.StartAsync();
                return "FRP 客户端已重启。";
            case "Uninstall":
                return await _frpManager.UninstallAsync();
            default:
                throw new NotSupportedException("未知 FRP 操作。");
        }
    }

    private static string BuildEnvironmentUninstallConfirmation(EnvironmentItem item) => item.Kind switch
    {
        EnvironmentKind.Iis => "确定卸载这台计算机的 IIS 吗？\n会卸载整个 Web Server 角色、相关 WAS 组件和 URL Rewrite；现有 IIS 站点及依赖 WAS 的应用也会受到影响。请先备份 IIS 配置和站点。Windows 可能要求重启后继续。",
        EnvironmentKind.Nginx => "确定卸载 Nginx 吗？\n会停止并删除 MCPanel 的 Nginx Windows 服务，然后删除 Nginx 组件目录和代理配置。",
        EnvironmentKind.MySql => "确定卸载 MySQL 吗？\n会停止并删除 MySQL80 服务，以及 MCPanel 的 MySQL 程序、数据和配置目录。",
        EnvironmentKind.SqlServer => "确定卸载 MCPanel 使用的 SQL Server 默认实例吗？\n会停止并移除 MSSQLSERVER 默认实例，以及 MCPanel 的数据、安装缓存和防火墙规则；其他 SQL Server 实例、共享 ODBC/OLE DB 驱动和全局程序目录会保留。",
        EnvironmentKind.Tomcat => "确定卸载 Tomcat 吗？\n会停止 MCPanel 管理的全部 Tomcat 进程，并删除 Tomcat 组件目录；已部署产品数据不会随此按钮删除。",
        EnvironmentKind.FrpTunnel => "确定卸载 FRP 吗？\n会停止 frpc，并删除 FRP 客户端、frpc.toml 映射配置以及下载缓存。此操作不会影响其他五个环境。",
        _ => $"确定卸载 {item.Title} 吗？"
    };

    internal async void ConnectMySql_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        var operation = TryBeginComponentOperation(item.Kind);
        if (operation is null) return;
        try
        {
            item.SetBusyState("连接中", "正在检查 MySQL 连接工具...");
            if (!_settingsService.TryOpenNavicatForMySql())
            {
                item.StatusText = "未找到 Navicat Premium Lite。";
                if (!ConfirmNavicatPremiumLiteInstall("连接 MySQL"))
                {
                    await RefreshComponentStateAsync(item);
                    item.StatusText = "已取消安装 Navicat Premium Lite。";
                    return;
                }

                item.SetBusyState("安装工具中", "正在准备下载 Navicat Premium Lite...", 0);
                await InstallNavicatPremiumLiteAsync(progress =>
                {
                    item.Progress = progress.Percent;
                    item.StatusText = progress.Message;
                });
                if (!_settingsService.TryOpenNavicatForMySql())
                {
                    throw new FileNotFoundException(
                        $"Navicat Premium Lite 安装完成，但未在 {_settingsService.NavicatInstallRoot} 检测到 navicat.exe。");
                }
            }

            var mysqlClipboard = MySqlCredentialStore.FormatForClipboard();
            Clipboard.SetText(mysqlClipboard);
            ClipboardSafety.ScheduleClear(mysqlClipboard);
            await RefreshComponentStateAsync(item);
            item.StatusText = "已打开 Navicat Premium Lite，MySQL 默认连接信息已复制到剪贴板。";
            RefreshDatabaseToolState();
        }
        catch (Exception ex)
        {
            item.StatusText = $"连接 MySQL 失败：{ex.Message}";
            item.BadgeText = "失败";
            item.BadgeBrush = Brushes.IndianRed;
            MessageBox.Show(item.StatusText, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
        finally
        {
            operation.Dispose();
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }

    internal async void ConnectSqlServer_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        var operation = TryBeginComponentOperation(item.Kind);
        if (operation is null) return;
        try
        {
            item.SetBusyState("连接中", "正在检查 SQL Server 加密连接...");
            await _settingsService.EnsureSqlServerLocalCertificateAsync();
            item.StatusText = "证书检查完成，正在打开 SQL Server Management Studio...";
            var sqlClipboard = SqlServerCredentialStore.FormatForClipboard();
            Clipboard.SetText(sqlClipboard);
            ClipboardSafety.ScheduleClear(sqlClipboard);

            if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
            {
                item.StatusText = "未找到 SQL Server Management Studio。";
                var confirm = MessageBox.Show(
                    "未找到 SQL Server Management Studio，是否从微软官方下载并安装？",
                    "连接 SQL Server",
                    MessageBoxButton.YesNo,
                    MessageBoxImage.Information);
                if (confirm != MessageBoxResult.Yes)
                {
                    await RefreshComponentStateAsync(item);
                    item.StatusText = "已取消打开 SQL Server Management Studio。";
                    return;
                }

                item.SetBusyState("安装工具中", "正在准备下载 SQL Server Management Studio...", 0);
                await RunDatabaseToolOperationAsync(token =>
                    _settingsService.InstallSqlManagementStudioAsync(progress =>
                    {
                        item.Progress = progress.Percent;
                        item.StatusText = progress.Message;
                    }, token));
                if (!_settingsService.TryOpenSqlManagementStudioForSqlServer())
                {
                    throw new FileNotFoundException("SSMS 安装完成，但未检测到 Ssms.exe，请从开始菜单打开或重启面板后重试。");
                }
            }

            await RefreshComponentStateAsync(item);
            item.StatusText = "已打开 SQL Server Management Studio，SQL Server 默认连接信息已复制到剪贴板。";
            RefreshDatabaseToolState();
        }
        catch (Exception ex)
        {
            item.StatusText = $"连接 SQL Server 失败：{ex.Message}";
            item.BadgeText = "失败";
            item.BadgeBrush = Brushes.IndianRed;
            MessageBox.Show(item.StatusText, "环境", MessageBoxButton.OK, MessageBoxImage.Information);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
        finally
        {
            operation.Dispose();
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }

    internal async void EnvironmentCardFlip_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element ||
            element.DataContext is not EnvironmentItem item ||
            item.IsFlipAnimating ||
            !item.CanFlipCard)
        {
            return;
        }

        if (item.IsTomcatModule && !item.IsFlipped)
        {
            item.RefreshTomcatPorts();
        }

        var card = FindAncestorBorder(element, "EnvironmentCard");
        if (card is null)
        {
            item.IsFlipped = !item.IsFlipped;
            return;
        }

        var scale = card.RenderTransform as ScaleTransform ?? new ScaleTransform(1, 1);
        if (scale.IsFrozen)
        {
            scale = scale.CloneCurrentValue();
        }
        card.RenderTransform = scale;

        item.IsFlipAnimating = true;
        try
        {
            await AnimateScaleXAsync(scale, scale.ScaleX, 0, TimeSpan.FromMilliseconds(150));
            item.IsFlipped = !item.IsFlipped;
            await AnimateScaleXAsync(scale, 0, 1, TimeSpan.FromMilliseconds(190));
        }
        catch
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.ScaleX = 1;
        }
        finally
        {
            item.IsFlipAnimating = false;
        }
    }

    internal async void TomcatPortSave_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not TomcatPortItem portItem ||
            portItem.IsBusy)
        {
            return;
        }

        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is not EnvironmentItem environmentItem)
        {
            return;
        }

        if (!int.TryParse(portItem.PortText, NumberStyles.None, CultureInfo.InvariantCulture, out var newPort) ||
            newPort is < 9000 or > 10000)
        {
            environmentItem.TomcatPortNotice = "请输入 9000 到 10000 之间的端口号。";
            return;
        }

        if (newPort == portItem.Port)
        {
            environmentItem.TomcatPortNotice = $"{portItem.ProductId} 当前已使用端口 {newPort}。";
            return;
        }

        using var operation = TryBeginComponentOperation(environmentItem.Kind);
        if (operation is null) return;
        portItem.IsBusy = true;
        environmentItem.TomcatPortNotice = $"正在更新 {portItem.ProductId} 的端口...";
        try
        {
            var updated = await ProductDeploymentService.ChangeTomcatProductPortAsync(portItem.ProductId, newPort);
            await _tomcatInstanceManager.PrepareAsync(updated);
            await NginxProductProxyService.TrySyncAsync();
            portItem.Apply(updated);
            environmentItem.TomcatPortNotice = $"{portItem.ProductId} 已改为端口 {newPort}，下次启动时生效。";
            _model.RefreshInstalledProducts();
        }
        catch (Exception ex)
        {
            portItem.PortText = portItem.Port.ToString(CultureInfo.InvariantCulture);
            environmentItem.TomcatPortNotice = "端口修改失败";
            MessageBox.Show(ex.Message, "修改 Tomcat 产品端口", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            portItem.IsBusy = false;
        }
    }

    internal void CredentialCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not string value)
        {
            return;
        }

        Clipboard.SetText(value);
        ClipboardSafety.ScheduleClear(value);
        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is EnvironmentItem item)
        {
            var label = button.CommandParameter as string ?? "信息";
            item.CredentialNotice = $"{label}已复制";
        }
    }

    internal async void CredentialEdit_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button ||
            button.DataContext is not CredentialField field ||
            !field.CanEdit)
        {
            return;
        }

        var card = FindAncestorBorder(button, "EnvironmentCard");
        if (card?.DataContext is not EnvironmentItem item ||
            item.Kind != EnvironmentKind.MySql)
        {
            return;
        }

        using var operation = TryBeginComponentOperation(item.Kind);
        if (operation is null) return;
        var credentials = MySqlCredentialStore.Load();
        if (field.EditKind == CredentialEditKind.Password)
        {
            var newPassword = ShowPasswordEditor();
            if (newPassword is null)
            {
                return;
            }

            try
            {
                item.SetBusyState("配置中", "正在修改并验证 MySQL root 密码...");
                var message = await Task.Run(() => _runtimeService.ChangeMySqlPasswordAsync(newPassword));
                await RefreshComponentStateAsync(item);
                item.RefreshCredentialFields();
                item.CredentialNotice = message;
            }
            catch (Exception ex)
            {
                await RefreshComponentStateAsync(item);
                item.RefreshCredentialFields();
                item.CredentialNotice = "密码修改失败";
                MessageBox.Show($"修改 MySQL 密码失败：{ex.Message}", "修改 MySQL 密码",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }

            return;
        }

        var portText = ShowPortEditor(credentials.Port);
        if (portText is null)
        {
            return;
        }

        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is < 1 or > 65535)
        {
            MessageBox.Show("请输入 1 到 65535 之间的有效端口号。", "修改 MySQL 端口",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            item.SetBusyState("配置中", $"正在把 MySQL 服务端口修改为 {port}...");
            var message = await Task.Run(() => _runtimeService.ChangeMySqlPortAsync(port));
            await RefreshComponentStateAsync(item);
            item.RefreshCredentialFields();
            item.CredentialNotice = message;
        }
        catch (Exception ex)
        {
            await RefreshComponentStateAsync(item);
            item.RefreshCredentialFields();
            item.CredentialNotice = "端口修改失败";
            MessageBox.Show($"修改 MySQL 端口失败：{ex.Message}", "修改 MySQL 端口",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private string? ShowPortEditor(int currentPort)
    {
        var dialog = PanelInputDialog.CreatePortEditor(currentPort);
        dialog.Owner = this;
        dialog.ApplyTheme(_isDarkThemeActive);
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    private string? ShowPasswordEditor()
    {
        var dialog = PanelInputDialog.CreatePasswordEditor();
        dialog.Owner = this;
        dialog.ApplyTheme(_isDarkThemeActive);
        return dialog.ShowDialog() == true ? dialog.ResultText : null;
    }

    internal void CredentialConnect_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not EnvironmentItem item)
        {
            return;
        }

        if (item.Kind == EnvironmentKind.MySql)
        {
            ConnectMySql_Click(sender, e);
        }
        else if (item.Kind == EnvironmentKind.SqlServer)
        {
            ConnectSqlServer_Click(sender, e);
        }
    }

    private static Border? FindAncestorBorder(DependencyObject source, string name)
    {
        for (DependencyObject? current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is Border border && border.Name == name)
            {
                return border;
            }
        }

        return null;
    }

    private static Task AnimateScaleXAsync(ScaleTransform transform, double from, double to, TimeSpan duration)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var animation = new DoubleAnimation(from, to, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut },
            FillBehavior = FillBehavior.Stop
        };
        animation.Completed += (_, _) =>
        {
            transform.ScaleX = to;
            completion.TrySetResult(true);
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation);
        return completion.Task;
    }

    private string OpenIisManager()
    {
        _runtimeService.OpenIisManager();
        return "已打开 IIS 管理器。";
    }

    internal async void NginxOpenManagement_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not EnvironmentItem item)
        {
            return;
        }

        if (!item.IsInstalled)
        {
            MessageBox.Show("请先安装 Nginx，再配置反向代理。", "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        try
        {
            var dialog = new NginxProxyDialog(_runtimeService)
            {
                Owner = this
            };
            dialog.ApplyTheme(_isDarkThemeActive);

            if (dialog.ShowDialog() == true)
            {
                item.StatusText = dialog.ResultMessage;
                await RefreshEnvironmentStatesAsync(preserveBusy: true);
                MessageBox.Show(dialog.ResultMessage, "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Information);
            }
        }
        catch (Exception ex)
        {
            item.StatusText = $"Nginx 管理失败：{ex.Message}";
            MessageBox.Show(item.StatusText, "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string RuntimeActionText(string action) => action switch
    {
        "Start" => "启动",
        "Stop" => "停止",
        "Restart" => "重启",
        "Uninstall" => "卸载",
        "CatalinaRun" => "打开 Catalina 诊断模式",
        _ => "处理"
    };

    private static string RuntimeBusyBadgeText(string action) => action switch
    {
        "Start" => "启动中",
        "Stop" => "停止中",
        "Restart" => "重启中",
        "Uninstall" => "卸载中",
        "CatalinaRun" => "诊断启动",
        _ => "处理中"
    };

    private void ShowEnvironmentOperationFailure(
        EnvironmentItem item,
        string operation,
        Exception exception,
        string dialogTitle = "环境")
    {
        var fallbackLog = EnvironmentOperationDiagnostics.RecordFailure(item.Title, operation, exception);
        var attachedLog = EnvironmentOperationDiagnostics.FindAttachedLogPath(exception);
        var logPath = !string.IsNullOrWhiteSpace(attachedLog) && File.Exists(attachedLog)
            ? attachedLog
            : fallbackLog;
        var iisCoreInstalledButNeedsRepair = item.Kind == EnvironmentKind.Iis &&
            _runtimeService.GetState(EnvironmentKind.Iis).NeedsConfigurationRepair;
        var fullMessage = iisCoreInstalledButNeedsRepair
            ? "IIS Web 服务已安装，但附加配置尚未完成：" + exception.Message
            : $"{item.Title}{operation}失败：{exception.Message}";
        item.StatusText = CompactEnvironmentStatus(fullMessage);
        item.BadgeText = iisCoreInstalledButNeedsRepair ? "需修复" : "失败";
        item.BadgeBrush = iisCoreInstalledButNeedsRepair ? Brushes.Goldenrod : Brushes.IndianRed;
        ShowOperationFailureDialog(fullMessage, logPath, dialogTitle);
    }

    private void ShowServiceOperationFailure(ServiceItem service, string operation, Exception exception)
    {
        var fallbackLog = EnvironmentOperationDiagnostics.RecordFailure(service.Name, operation, exception);
        var attachedLog = EnvironmentOperationDiagnostics.FindAttachedLogPath(exception);
        var logPath = !string.IsNullOrWhiteSpace(attachedLog) && File.Exists(attachedLog)
            ? attachedLog
            : fallbackLog;
        var fullMessage = $"{service.Name}{operation}失败：{exception.Message}";
        service.StatusText = CompactEnvironmentStatus(fullMessage);
        service.BadgeText = "失败";
        service.BadgeBrush = Brushes.IndianRed;
        ShowOperationFailureDialog(fullMessage, logPath, "套件服务");
    }

    private void ShowOperationFailureDialog(string message, string? logPath, string title)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var result = MessageBox.Show(
            $"{message}{Environment.NewLine}{Environment.NewLine}完整日志：{logPath}{Environment.NewLine}{Environment.NewLine}是否立即打开日志？",
            title,
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = logPath,
                UseShellExecute = true
            });
        }
        catch (Exception openError)
        {
            MessageBox.Show($"无法打开日志：{openError.Message}{Environment.NewLine}{logPath}", title, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private static string CompactEnvironmentStatus(string message)
    {
        var compact = message
            .Replace("\r", " ", StringComparison.Ordinal)
            .Replace("\n", " ", StringComparison.Ordinal)
            .Trim();
        return compact.Length <= 420 ? compact : compact.Substring(0, 417) + "...";
    }

    internal async void FrpOpenManagement_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            using var operation = TryBeginComponentOperation(EnvironmentKind.FrpTunnel);
            if (operation is null) return;
            await Task.Run(_frpManager.EnsureManagementServerAsync);
            Process.Start(new ProcessStartInfo
            {
                FileName = _frpManager.ManagementUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            var item = _model.EnvironmentItems.First(environment => environment.Kind == EnvironmentKind.FrpTunnel);
            ShowEnvironmentOperationFailure(item, "打开管理页面", ex, "FRP");
        }
    }

    internal async void ServiceAction_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is not ServiceItem service ||
            sender is not Button { Tag: string action }) return;
        try
        {
            var message = await RunComponentRuntimeActionAsync(service.Kind, action);
            if (message is null) return;
            await RefreshEnvironmentStatesAsync(preserveBusy: true);
        }
        catch (Exception ex)
        {
            ShowServiceOperationFailure(service, RuntimeActionText(action), ex);
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }
}
