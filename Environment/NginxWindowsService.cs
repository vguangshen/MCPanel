using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;
using ServiceBase = System.ServiceProcess.ServiceBase;
using ServiceController = System.ServiceProcess.ServiceController;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace MCPanel;

/// <summary>
/// Registers and controls the Nginx Windows service used by MCPanel.
///
/// The current Nginx package contains nginx.exe but not the original
/// nginx-server.exe wrapper. MCPanel.exe therefore hosts an equivalent
/// ServiceBase entry point and supervises nginx.exe from the installed runtime
/// directory when launched by the Service Control Manager.
///
/// Registration and control go through <see cref="ManagedWindowsServiceController"/>
/// like Tomcat and FRP. Nginx differs in three ways: the installer is already
/// elevated, so sc.exe runs directly and its output reaches error messages; an
/// existing "nginx" service counts as ours when it points into the Nginx root,
/// even without the host argument (older wrappers); and the waits are shorter.
/// </summary>
internal static class NginxWindowsServiceManager
{
    public const string ServiceName = "nginx";
    public const string ServiceDisplayName = "nginx";
    public const string ServiceDescription = "Nginx Web 服务器/反向代理服务（由 MCPanel 管理）。";

    private static readonly TimeSpan StatusTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(20);

    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRunning() => ManagedWindowsServiceController.IsRunning(ServiceName);

    /// <summary>
    /// Checks the service state and its registered component root. Reading
    /// Process.MainModule.FileName is not reliable from a non-elevated WPF
    /// process when nginx is hosted by the elevated service account.
    /// </summary>
    public static bool IsRunningForRoot(string nginxRoot) =>
        IsRunning() && IsRegisteredForRoot(nginxRoot);

    public static bool IsRegisteredForRoot(string nginxRoot) =>
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, serviceArgument: null, nginxRoot);

    internal static bool ImagePathContainsRoot(string imagePath, string nginxRoot) =>
        ManagedWindowsServiceController.ImagePathContainsRoot(imagePath, nginxRoot);

    public static string BuildServiceImagePath(string executablePath, string nginxRoot) =>
        ManagedWindowsServiceController.BuildServiceImagePath(executablePath, NginxWindowsServiceHost.ServiceArgument, nginxRoot);

    public static void EnsureRegistered(string executablePath, string nginxRoot)
    {
        if (!ProcessRunner.IsAdministrator())
        {
            throw new InvalidOperationException("注册 Nginx Windows 服务需要管理员权限，请从安装按钮重新授权。");
        }

        ManagedWindowsServiceController.EnsureRegistered(
            ServiceName,
            ServiceDisplayName,
            ServiceDescription,
            executablePath,
            NginxWindowsServiceHost.ServiceArgument,
            nginxRoot,
            configureRecovery: true,
            elevated: false,
            ownershipRequiresServiceArgument: false);
    }

    /// <summary>
    /// Lets SCM recover the wrapper itself. The in-process watchdog handles
    /// nginx.exe failures while this service process is still alive; SCM
    /// recovery is the second layer for wrapper crashes or explicit failures.
    /// </summary>
    internal static void ConfigureRecoveryPolicy() =>
        ManagedWindowsServiceController.ConfigureRecovery(ServiceName, ServiceDisplayName, elevated: false);

    internal static string BuildRecoveryPolicyArguments() => ManagedWindowsServiceController.BuildRecoveryPolicyArguments(ServiceName);
    internal static string BuildFailureFlagArguments() => ManagedWindowsServiceController.BuildFailureFlagArguments(ServiceName);

    public static void Start() =>
        ManagedWindowsServiceController.Start(ServiceName, ServiceDisplayName, elevated: false, StatusTimeout);

    public static void Stop() =>
        ManagedWindowsServiceController.Stop(ServiceName, ServiceDisplayName, elevated: false, StatusTimeout);

    public static void Delete() =>
        ManagedWindowsServiceController.Delete(ServiceName, ServiceDisplayName, elevated: false, DeleteTimeout);

    public static bool IsAccessDenied(Exception exception) => ManagedWindowsServiceController.IsAccessDenied(exception);

    public static void WaitForStatus(ServiceControllerStatus expected, TimeSpan timeout) =>
        ManagedWindowsServiceController.WaitForStatus(ServiceName, ServiceDisplayName, expected, timeout);

    public static void WaitUntilMissing(TimeSpan timeout) =>
        ManagedWindowsServiceController.WaitUntilMissing(ServiceName, ServiceDisplayName, timeout);
}

internal static class NginxWindowsServiceHost
{
    public const string ServiceArgument = "--nginx-service";

    public static bool IsServiceRequest(string[] args) => ServiceHostArguments.IsRequest(args, ServiceArgument);

    public static int Run(string[] args) =>
        ServiceHostArguments.Run(args, ServiceArgument, root => new NginxWindowsService(root), requiredFile: "nginx.exe");
}

internal sealed class NginxWindowsService : ServiceBase
{
    internal const int MaxWatchdogRecoveriesPerWindow = WatchdogRecoveryBudget.MaxRecoveriesPerWindow;
    internal static readonly TimeSpan WatchdogRecoveryWindow = WatchdogRecoveryBudget.Window;

    private readonly string _nginxRoot;
    private CancellationTokenSource? _watchdogCancellation;
    private Task? _watchdogTask;
    private int _stopStarted;

    public NginxWindowsService(string nginxRoot)
    {
        _nginxRoot = nginxRoot;
        ServiceName = NginxWindowsServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    internal static TimeSpan GetRecoveryDelay(int previousAttempts) => WatchdogRecoveryBudget.GetDelay(previousAttempts);

    internal static bool ShouldEscalateWatchdog(int attemptsInWindow) => WatchdogRecoveryBudget.ShouldEscalate(attemptsInWindow);

    internal static string GetServiceLogPath(string nginxRoot) =>
        Path.Combine(nginxRoot, "logs", "mcpanel-service.log");

    protected override void OnStart(string[] args)
    {
        Interlocked.Exchange(ref _stopStarted, 0);
        _watchdogCancellation?.Dispose();
        _watchdogCancellation = new CancellationTokenSource();

        try
        {
            try
            {
                // Existing installations also receive the recovery policy on
                // their next service start. The service account has permission
                // to update its own SCM recovery settings.
                NginxWindowsServiceManager.ConfigureRecoveryPolicy();
            }
            catch (Exception ex)
            {
                WriteServiceLog($"SCM recovery policy update failed and was ignored: {ex.Message}");
            }

            StartNginxAndVerify(CancellationToken.None, "service start");
            WriteServiceLog("Nginx service started and watchdog is active.");

            var token = _watchdogCancellation.Token;
            _watchdogTask = Task.Run(() => WatchdogLoop(token), token);
        }
        catch
        {
            _watchdogCancellation.Cancel();
            _watchdogCancellation.Dispose();
            _watchdogCancellation = null;
            throw;
        }
    }

    protected override void OnStop()
    {
        StopNginx();
    }

    protected override void OnShutdown()
    {
        StopNginx();
        base.OnShutdown();
    }

    private void WatchdogLoop(CancellationToken cancellationToken)
    {
        var budget = new WatchdogRecoveryBudget();
        var unhealthySamples = 0;

        while (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        {
            var processRunning = NginxRuntimeManager.IsRunningUnderRoot(_nginxRoot);
            var portsHealthy = processRunning &&
                NginxRuntimeManager.AreConfiguredPortsListening(
                    _nginxRoot,
                    out _,
                    out _);

            if (processRunning && portsHealthy)
            {
                unhealthySamples = 0;
                continue;
            }

            // A completely missing nginx process should recover immediately.
            // If only a configured port is temporarily missing, wait for three
            // samples so a normal reload cannot be mistaken for a crash.
            unhealthySamples++;
            if (processRunning && unhealthySamples < 3)
            {
                continue;
            }
            unhealthySamples = 0;

            var now = DateTime.UtcNow;
            var attempts = budget.AttemptsInWindow(now);
            if (ShouldEscalateWatchdog(attempts))
            {
                FailServiceProcess(
                    $"Nginx 在 {WatchdogRecoveryWindow.TotalMinutes:0} 分钟内连续异常超过 {MaxWatchdogRecoveriesPerWindow} 次，停止服务并交给 Windows 服务恢复机制处理。");
                return;
            }

            var delay = GetRecoveryDelay(attempts);
            budget.Record(now);
            attempts++;
            var healthReason = processRunning
                ? "nginx.exe 仍存在但配置端口未全部监听"
                : "nginx.exe 已退出";
            WriteServiceLog(
                $"Watchdog detected unhealthy Nginx ({healthReason}). Recovery attempt {attempts}, waiting {delay.TotalSeconds:0}s.");

            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                return;
            }

            try
            {
                StartNginxAndVerify(cancellationToken, "watchdog recovery");
                WriteServiceLog("Watchdog recovery succeeded.");
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                WriteServiceLog($"Watchdog recovery failed: {ex.Message}");
                if (ShouldEscalateWatchdog(attempts))
                {
                    FailServiceProcess(
                        $"Nginx 自动恢复达到上限，最后错误：{ex.Message}");
                    return;
                }
            }
        }
    }

    private void StartNginxAndVerify(CancellationToken cancellationToken, string reason)
    {
        var nginxExe = Path.Combine(_nginxRoot, "nginx.exe");
        if (!File.Exists(nginxExe))
        {
            throw new FileNotFoundException("未找到 Nginx 运行文件。", nginxExe);
        }

        cancellationToken.ThrowIfCancellationRequested();
        NginxRuntimeManager.KillProcessesUnderRoot(_nginxRoot);
        cancellationToken.ThrowIfCancellationRequested();

        WriteServiceLog($"Starting nginx.exe ({reason}).");
        using var process = ProcessRunner.Start(nginxExe, string.Empty, _nginxRoot);

        for (var attempt = 0; attempt < 40; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsNginxHealthy())
            {
                return;
            }

            if (process.HasExited)
            {
                break;
            }

            if (cancellationToken.WaitHandle.WaitOne(250))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        var log = NginxRuntimeManager.ReadRecentErrorLog(_nginxRoot);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
            ? "Nginx 启动后未形成健康监听。"
            : $"Nginx 启动失败：{log}");
    }

    private bool IsNginxHealthy()
    {
        if (!NginxRuntimeManager.IsRunningUnderRoot(_nginxRoot))
        {
            return false;
        }

        return NginxRuntimeManager.AreConfiguredPortsListening(
            _nginxRoot,
            out var configuredPorts,
            out _) && configuredPorts.Count > 0;
    }

    private void StopNginx()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        var watchdogCancellation = _watchdogCancellation;
        var watchdogTask = _watchdogTask;
        try
        {
            watchdogCancellation?.Cancel();
            if (watchdogTask is not null)
            {
                try
                {
                    watchdogTask.Wait(TimeSpan.FromSeconds(5));
                }
                catch (AggregateException ex) when (ex.InnerExceptions.All(error => error is TaskCanceledException or OperationCanceledException))
                {
                    // Expected during service shutdown.
                }
            }

            var nginxExe = Path.Combine(_nginxRoot, "nginx.exe");
            try
            {
                if (File.Exists(nginxExe))
                {
                    ProcessRunner.RunSynchronously(
                        nginxExe,
                        "-s quit",
                        _nginxRoot,
                        captureOutput: true,
                        timeout: TimeSpan.FromSeconds(10));
                }
            }
            catch
            {
                // The process-root cleanup below is the final stop fallback.
            }

            for (var attempt = 0; attempt < 40; attempt++)
            {
                if (!NginxRuntimeManager.IsRunningUnderRoot(_nginxRoot))
                {
                    WriteServiceLog("Nginx service stopped cleanly.");
                    return;
                }

                Thread.Sleep(250);
            }

            NginxRuntimeManager.KillProcessesUnderRoot(_nginxRoot);
            if (NginxRuntimeManager.IsRunningUnderRoot(_nginxRoot))
            {
                throw new InvalidOperationException(
                    "Nginx 进程未能完全停止，Windows 服务停止操作失败。");
            }

            WriteServiceLog("Nginx service stopped after process cleanup fallback.");
        }
        finally
        {
            _watchdogTask = null;
            _watchdogCancellation = null;
            watchdogCancellation?.Dispose();
        }
    }

    private void FailServiceProcess(string message)
    {
        WriteServiceLog(message);
        try
        {
            NginxRuntimeManager.KillProcessesUnderRoot(_nginxRoot);
        }
        catch
        {
            // SCM will restart the wrapper and perform another root-scoped cleanup.
        }

        ExitCode = 1;
        Environment.Exit(1);
    }

    private void WriteServiceLog(string message) => PanelLog.Write(GetServiceLogPath(_nginxRoot), message);
}
