using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace MCPanel;

public sealed partial class EnvironmentRuntimeService
{
    public NginxRuntimeOptions GetNginxOptions()
    {
        return NginxRuntimeManager.LoadOptions() ?? new NginxRuntimeOptions();
    }

    public Task<string> SaveNginxOptionsAsync(NginxRuntimeOptions options, CancellationToken cancellationToken = default,
        string? expectedRevision = null) => NginxConfigurationCoordinator.RunAsync(async () =>
    {
        NginxConfigurationCoordinator.EnsureUnchanged(expectedRevision, GetNginxOptions());
        return await SaveNginxOptionsCoreAsync(options, cancellationToken);
    }, cancellationToken);

    private async Task<string> SaveNginxOptionsCoreAsync(NginxRuntimeOptions options, CancellationToken cancellationToken)
    {
        var normalized = NginxRuntimeManager.NormalizeOptions(options);
        NginxRuntimeManager.ValidateOptions(normalized);

        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;
        var isRunning = IsNginxOperational(nginxRoot);
        var savedOptions = NginxRuntimeManager.LoadOptions();
        var currentPorts = NginxRuntimeManager.ReadConfiguredListenPorts(nginxRoot).ToHashSet();
        if (currentPorts.Count == 0 && savedOptions is not null)
        {
            currentPorts = NginxRuntimeManager.GetEffectiveListenPorts(savedOptions).ToHashSet();
        }
        var portsToCheck = NginxRuntimeManager.GetEffectiveListenPorts(normalized).ToArray();
        if (portsToCheck.Length == 0)
        {
            portsToCheck = [normalized.ListenPort];
        }

        foreach (var port in portsToCheck)
        {
            var ownedByRunningNginx = isRunning && currentPorts.Contains(port);
            if (!ownedByRunningNginx && !NginxRuntimeManager.IsPortAvailable(port))
            {
                throw new InvalidOperationException($"端口 {port} 已被占用，请换一个监听端口。");
            }
        }

        var transaction = CaptureNginxTransaction(nginxRoot);
        try
        {
            NginxRuntimeManager.CleanConfigFiles(nginxRoot);
            NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
            await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            NginxRuntimeManager.SaveOptions(normalized);
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); }
            catch (Exception rollbackError) { throw new AggregateException("Nginx 配置保存失败，且回滚未完成。", ex, rollbackError); }
            throw new InvalidOperationException($"Nginx 配置保存失败，已恢复原配置：{ex.Message}", ex);
        }

        if (isRunning)
        {
            try
            {
                await RunFileAsync(nginxExe, "-s reload", nginxRoot, false, cancellationToken);
                return $"Nginx 配置已保存并热重载。{normalized.Summary}";
            }
            catch (Exception reloadError)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return $"Nginx 配置已保存，但热重载被取消：{reloadError.Message}。请在“环境”页面重启 Nginx。";
                }

                try
                {
                    await RestartAsync(EnvironmentKind.Nginx, cancellationToken);
                    return $"Nginx 配置已保存并重启生效。{normalized.Summary}";
                }
                catch (Exception restartError)
                {
                    return $"Nginx 配置已保存，但热重载和自动重启均未完成：{restartError.Message}。请在“环境”页面重启 Nginx。";
                }
            }
        }

        return $"Nginx 配置已保存。{normalized.Summary}";
    }

    private static ConfigurationFileTransaction CaptureNginxTransaction(string nginxRoot)
    {
        var transaction = new ConfigurationFileTransaction();
        transaction.Capture(NginxRuntimeManager.StateFile);
        var directory = Path.Combine(nginxRoot, "conf");
        transaction.Capture(Path.Combine(directory, "nginx.conf"));
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.conf", SearchOption.AllDirectories))
                transaction.Capture(path);
        return transaction;
    }

    private static async Task<string> StartNginxAsync(CancellationToken cancellationToken)
    {
        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;

        if (IsNginxOperational(nginxRoot))
        {
            var current = NginxRuntimeManager.LoadOptions();
            return current is not null ? $"Nginx 已在运行。{current.Summary}" : "Nginx 已在运行。";
        }

        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxRoot))
            {
                throw new InvalidOperationException(
                    $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已存在，但未指向当前 Nginx 目录，已停止启动以保护现有服务。");
            }

            await RunNginxServiceActionAsync("start", cancellationToken);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(300, cancellationToken);
                if (IsNginxOperational(nginxRoot))
                {
                    var options = NginxRuntimeManager.LoadOptions();
                    return options is not null
                        ? $"Nginx Windows 服务已启动。{options.Summary}"
                        : "Nginx Windows 服务已启动。";
                }
            }

            var serviceLog = NginxRuntimeManager.ReadRecentErrorLog(nginxRoot);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(serviceLog)
                ? "Nginx Windows 服务已启动，但未检测到 nginx.exe。"
                : $"Nginx Windows 服务启动失败。最近日志：{serviceLog}");
        }

        var listenPort = await ConfigureAndTestNginxAsync(nginxExe, nginxRoot, cancellationToken);
        StartDetached(nginxExe, nginxRoot);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(300, cancellationToken);
            if (IsNginxOperational(nginxRoot))
            {
                var options = NginxRuntimeManager.LoadOptions();
                return options is not null ? $"Nginx 已启动。{options.Summary}" : $"Nginx 已启动，监听端口 {listenPort}。";
            }
        }

        var log = NginxRuntimeManager.ReadRecentErrorLog(nginxRoot);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
            ? "Nginx 启动失败，未检测到运行中的 nginx.exe。"
            : $"Nginx 启动失败。最近日志：{log}");
    }

    private static async Task<string> StopNginxAsync(CancellationToken cancellationToken)
    {
        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;

        var nginxIsRunning = IsNginxProcessOrServiceRunning(nginxRoot);
        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxRoot))
            {
                if (NginxWindowsServiceManager.IsRunning())
                {
                    throw new InvalidOperationException(
                        $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已运行，但未指向当前 Nginx 目录，已停止操作以保护现有服务。");
                }

                // A stopped foreign service must not block stopping a
                // separately launched MCPanel Nginx process below.
            }
            else
            {
                if (!nginxIsRunning && !NginxWindowsServiceManager.IsRunning())
                {
                    return "Nginx 当前未运行。";
                }

                await RunNginxServiceActionAsync("stop", cancellationToken);
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(250, cancellationToken);
                    if (!NginxWindowsServiceManager.IsRunningForRoot(nginxRoot))
                    {
                        return "Nginx Windows 服务已停止。";
                    }
                }

                NginxRuntimeManager.KillProcessesUnderRoot(nginxRoot);
                if (NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
                {
                    throw new InvalidOperationException(
                        "Nginx Windows 服务已收到停止请求，但 nginx.exe 仍在运行，已取消后续卸载。请先结束占用进程后重试。");
                }

                return "Nginx Windows 服务已停止。";
            }
        }

        if (!nginxIsRunning)
        {
            return "Nginx 当前未运行。";
        }

        try
        {
            await RunFileAsync(nginxExe, "-s quit", nginxRoot, false, cancellationToken);
        }
        catch
        {
            try
            {
                await RunFileAsync(nginxExe, "-s stop", nginxRoot, false, cancellationToken);
            }
            catch
            {
                // Fall back to killing only the nginx.exe instances under the configured Nginx root.
            }
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (!NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
            {
                return "Nginx 已停止。";
            }
        }

        NginxRuntimeManager.KillProcessesUnderRoot(nginxRoot);
        if (NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
        {
            throw new InvalidOperationException(
                "Nginx 进程未能完全停止，已取消后续卸载。请先结束占用进程后重试。");
        }

        return "Nginx 已停止。";
    }

    private static Task<int> ConfigureAndTestNginxAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken) =>
        NginxConfigurationCoordinator.RunAsync(async () =>
        {
            var transaction = CaptureNginxTransaction(nginxRoot);
            try { return await ConfigureAndTestNginxCoreAsync(nginxExe, nginxRoot, cancellationToken); }
            catch (Exception error)
            {
                try { transaction.Rollback(); }
                catch (Exception rollbackError) { throw new AggregateException("Nginx 启动配置失败，且回滚未完成。", error, rollbackError); }
                throw;
            }
        }, cancellationToken);

    private static async Task<int> ConfigureAndTestNginxCoreAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken)
    {
        var savedOptions = NginxRuntimeManager.LoadOptions();
        if (savedOptions is { Rules.Count: > 0 })
        {
            var normalized = NginxRuntimeManager.NormalizeOptions(savedOptions);

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
                await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
                NginxRuntimeManager.SaveOptions(normalized);
                return normalized.ListenPort;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                var ports = string.Join("、", normalized.EnabledRules.Select(rule => rule.ListenPort).Distinct().OrderBy(port => port));
                throw new InvalidOperationException($"Nginx 监听端口被占用，请在“管理”中调整端口后再启动。当前端口：{ports}。", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        Exception? lastError = null;
        foreach (var port in NginxRuntimeManager.BuildPortCandidates())
        {
            if (!NginxRuntimeManager.IsPortAvailable(port))
            {
                continue;
            }

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteListenPort(nginxRoot, port);
                await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
                NginxRuntimeManager.SaveOptions(port);
                return port;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Nginx 无法找到可用监听端口。请检查 72、80、8088、8080、8090、8099、18080 是否被占用。", lastError);
    }

    private static EnvironmentRuntimeState GetNginxState(ComponentLocator locator)
    {
        var nginxExe = locator.FindNginxExecutable();
        if (nginxExe is null)
        {
            return NotInstalled();
        }

        var nginxRoot = Path.GetDirectoryName(nginxExe)!;
        var runtimePresent = IsNginxProcessOrServiceRunning(nginxRoot);
        var portsHealthy = NginxRuntimeManager.AreConfiguredPortsListening(
            nginxRoot,
            out var configuredPorts,
            out var missingPorts);
        var isRunning = runtimePresent && portsHealthy;
        var options = NginxRuntimeManager.LoadOptions();
        var optionText = options is null ? string.Empty : options.Summary;
        var healthText = !runtimePresent
            ? "当前未检测到 Nginx 进程。"
            : configuredPorts.Count == 0
                ? "未从实际 nginx.conf 读取到有效监听端口，已判定为启动异常。"
                : missingPorts.Count > 0
                    ? $"检测到 Nginx 进程，但实际配置端口 {string.Join("、", missingPorts)} 未监听，已判定为启动异常。"
                    : "实际 nginx.conf 配置端口均已监听。";
        var actualPortText = configuredPorts.Count == 0
            ? string.Empty
            : $"实际监听端口：{string.Join("、", configuredPorts)}。";
        return Installed(isRunning, $"Nginx 已安装到 {nginxRoot}。{healthText}{actualPortText}{optionText}");
    }

    private static string RequireNginxExe() => FindNginxExe() ?? throw new FileNotFoundException("未找到 nginx.exe。");

    private static bool IsNginxProcessOrServiceRunning(string nginxRoot) =>
        NginxRuntimeManager.IsRunningUnderRoot(nginxRoot) ||
        NginxWindowsServiceManager.IsRunningForRoot(nginxRoot);

    private static bool IsNginxOperational(string nginxRoot)
    {
        if (!IsNginxProcessOrServiceRunning(nginxRoot))
        {
            return false;
        }

        return NginxRuntimeManager.AreConfiguredPortsListening(
            nginxRoot,
            out _,
            out _);
    }

    private static string? FindNginxExe() => new ComponentLocator().FindNginxExecutable();
    private static Task RunNginxServiceActionAsync(string action, CancellationToken cancellationToken)
        => Task.Run(() => RunNginxServiceActionCoreAsync(action, cancellationToken), cancellationToken);

    private static async Task RunNginxServiceActionCoreAsync(string action, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case "start":
                    NginxWindowsServiceManager.Start();
                    break;
                case "stop":
                    NginxWindowsServiceManager.Stop();
                    break;
                case "delete":
                    NginxWindowsServiceManager.Delete();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的 Nginx 服务操作。");
            }
        }
        catch (Exception ex) when (NginxWindowsServiceManager.IsAccessDenied(ex))
        {
            await RunElevatedPowerShellAsync($"sc.exe {action} {NginxWindowsServiceManager.ServiceName}", cancellationToken);
            switch (action)
            {
                case "start":
                    NginxWindowsServiceManager.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
                case "stop":
                    NginxWindowsServiceManager.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    break;
                case "delete":
                    NginxWindowsServiceManager.WaitUntilMissing(TimeSpan.FromSeconds(20));
                    break;
            }
        }
    }
}
