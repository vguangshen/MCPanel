using System;
using System.Linq;
using System.Threading.Tasks;

namespace MCPanel;

public partial class MainWindow
{
    private readonly QueueTomcatRestartTracker _queueTomcatRestart = new();

    private void LoadQueueTomcatRestartSetting()
    {
        var progress = _model.InstallationProgress;
        try
        {
            progress.LoadRestartTomcatAfterQueue(_settingsService.GetRestartTomcatAfterQueue());
        }
        catch (Exception error)
        {
            SuppressedErrorLog.Record("读取队列完成后重启 Tomcat 设置", error);
        }

        progress.RestartTomcatAfterQueueChanged += enabled =>
        {
            try
            {
                _settingsService.SetRestartTomcatAfterQueue(enabled);
                progress.IsTomcatRestartStatusWarning = false;
                progress.TomcatRestartStatusText = enabled
                    ? _productInstallQueue.HasActiveItems
                        ? "已开启：本批任务完成后将自动重启 Tomcat。"
                        : "已开启：下次队列完成后将自动重启 Tomcat。"
                    : string.Empty;
            }
            catch (Exception error)
            {
                progress.IsTomcatRestartStatusWarning = true;
                progress.TomcatRestartStatusText = $"无法保存设置：{error.Message}";
            }
        };
    }

    /// <summary>
    /// Called for every finished queue item. When the queue has just emptied
    /// after installing Java/Tomcat products and the switch is on, restarts
    /// the shared Tomcat Server so the new applications are loaded.
    /// </summary>
    private void TrackQueueTomcatRestart(ProductInstallQueueItemViewModel item)
    {
        var isTomcatProduct = QueueTomcatRestartTracker.IsTomcatProduct(
            item.Request.RunEnvironment, item.Request.DevLanguage);
        var due = _queueTomcatRestart.ItemFinished(item.State, isTomcatProduct, _productInstallQueue.HasActiveItems);
        if (due > 0 && _model.InstallationProgress.RestartTomcatAfterQueue && !_isClosed)
        {
            _ = RestartTomcatAfterQueueAsync(due);
        }
    }

    private async Task RestartTomcatAfterQueueAsync(int tomcatProducts)
    {
        var progress = _model.InstallationProgress;
        void Report(string text, bool warning = false)
        {
            progress.IsTomcatRestartStatusWarning = warning;
            progress.TomcatRestartStatusText = text;
        }

        var tomcat = _model.EnvironmentItems.First(item => item.Kind == EnvironmentKind.Tomcat);
        try
        {
            Report("队列已完成，正在检查 Tomcat 状态…");
            var state = await Task.Run(() => _runtimeService.GetState(EnvironmentKind.Tomcat));
            if (_isClosed) return;
            if (!state.IsInstalled)
            {
                Report("队列已完成；未检测到 Tomcat，已跳过自动重启。");
                return;
            }

            if (!state.IsRunning)
            {
                Report("队列已完成；Tomcat Server 未运行，未自动启动。");
                return;
            }

            Report($"队列已完成，正在自动重启 Tomcat（{tomcatProducts} 个 Java 产品）…");
            var message = await RunComponentRuntimeActionAsync(EnvironmentKind.Tomcat, "Restart", confirmed: true);
            if (_isClosed) return;
            if (message is null)
            {
                Report("Tomcat 正在执行其他操作，已跳过自动重启，请稍后手动重启。", warning: true);
                return;
            }

            await RefreshEnvironmentStatesAsync(preserveBusy: true);
            tomcat.StatusText = message;
            Report($"{DateTime.Now:HH:mm} 队列完成后已自动重启 Tomcat。");
        }
        catch (Exception error)
        {
            if (_isClosed) return;
            Report($"自动重启 Tomcat 失败：{error.Message}", warning: true);
            ShowEnvironmentOperationFailure(tomcat, "自动重启", error, "下载与安装队列");
            await RefreshRuntimeStatesAfterOperationAsync();
        }
    }
}
