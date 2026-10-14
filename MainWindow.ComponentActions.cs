using System.Windows;

namespace MCPanel;

public partial class MainWindow
{
    private IDisposable? TryBeginComponentOperation(EnvironmentKind kind) =>
        _model.ComponentOperations.TryBegin(kind);

    private async Task<bool> ConfirmComponentRuntimeActionAsync(EnvironmentKind kind, string action)
    {
        if (action == "Uninstall")
        {
            var item = _model.EnvironmentItems.First(environment => environment.Kind == kind);
            return MessageBox.Show(BuildEnvironmentUninstallConfirmation(item), "环境",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
        }

        if (kind != EnvironmentKind.Tomcat || action is not ("Start" or "Restart")) return true;
        var runningProducts = await Task.Run(TomcatProductInstanceManager.GetRunningProductIds);
        if (_isClosed) return false;
        if (runningProducts.Count == 0) return true;
        var preview = string.Join("、", runningProducts.Take(8));
        if (runningProducts.Count > 8) preview += $" 等 {runningProducts.Count} 个应用";
        return MessageBox.Show(
            $"当前有独立 Tomcat 调试实例正在运行：{preview}。{Environment.NewLine}{Environment.NewLine}" +
            "启动总 Tomcat Server 会先停止这些独立实例，以避免端口冲突。是否继续？",
            "切换到总 Tomcat Server", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes;
    }

    private Task<string?> RunComponentRuntimeActionAsync(EnvironmentKind kind, string action) =>
        _model.ComponentOperations.RunAsync(kind,
            () => ConfirmComponentRuntimeActionAsync(kind, action),
            () =>
            {
                var badge = RuntimeBusyBadgeText(action);
                var status = $"{_model.EnvironmentItems.First(item => item.Kind == kind).Title} 正在{RuntimeActionText(action)}...";
                _model.EnvironmentItems.First(item => item.Kind == kind).SetBusyState(badge, status);
                _model.Services.First(service => service.Kind == kind).SetBusyState(badge, status);
                return Task.Run(() => ExecuteComponentRuntimeActionAsync(kind, action));
            });

    private Task<string> ExecuteComponentRuntimeActionAsync(EnvironmentKind kind, string action) =>
        kind == EnvironmentKind.FrpTunnel
            ? ExecuteFrpRuntimeActionAsync(action)
            : action switch
            {
                "Start" => _runtimeService.StartAsync(kind),
                "Stop" => _runtimeService.StopAsync(kind),
                "Restart" => _runtimeService.RestartAsync(kind),
                "Uninstall" => _runtimeService.UninstallAsync(kind),
                "CatalinaRun" => _runtimeService.StartTomcatInCatalinaConsoleAsync(),
                "OpenIis" => Task.FromResult(OpenIisManager()),
                _ => throw new NotSupportedException("未知环境操作。")
            };

    private async Task RefreshComponentStateAsync(EnvironmentItem item)
    {
        var probe = await Task.Run(() => CaptureComponentState(() => item.Kind == EnvironmentKind.FrpTunnel
            ? _frpManager.GetState() : _runtimeService.GetState(item.Kind)));
        if (_isClosed) return;
        var service = _model.Services.First(service => service.Kind == item.Kind);
        if (probe.Key is null)
        {
            item.ApplyProbeFailure(probe.Value!);
            service.ApplyProbeFailure(probe.Value!);
        }
        else
        {
            item.ApplyRuntimeState(probe.Key);
            service.ApplyRuntimeState(probe.Key);
        }
    }

    private static KeyValuePair<EnvironmentRuntimeState?, string?> CaptureComponentState(Func<EnvironmentRuntimeState> probe)
    {
        try { return new(probe(), null); }
        catch (Exception error) { return new(null, error.Message); }
    }
}
