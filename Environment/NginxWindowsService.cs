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
/// </summary>
internal static class NginxWindowsServiceManager
{
    public const string ServiceName = "nginx";
    public const string ServiceDisplayName = "nginx";
    public const string ServiceDescription = "Nginx Web 服务器/反向代理服务（由 MCPanel 管理）。";

    public static bool IsInstalled()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            _ = controller.Status;
            return true;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    public static bool IsRunning()
    {
        try
        {
            using var controller = new ServiceController(ServiceName);
            return controller.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Checks the service state and its registered component root. Reading
    /// Process.MainModule.FileName is not reliable from a non-elevated WPF
    /// process when nginx is hosted by the elevated service account.
    /// </summary>
    public static bool IsRunningForRoot(string nginxRoot)
    {
        if (!IsRunning() || string.IsNullOrWhiteSpace(nginxRoot))
        {
            return false;
        }

        return IsRegisteredForRoot(nginxRoot);
    }

    public static bool IsRegisteredForRoot(string nginxRoot)
    {
        if (!IsInstalled() || string.IsNullOrWhiteSpace(nginxRoot))
        {
            return false;
        }

        try
        {
            var expectedRoot = Path.GetFullPath(nginxRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{ServiceName}");
            var imagePath = key?.GetValue("ImagePath")?.ToString();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            return ImagePathContainsRoot(Environment.ExpandEnvironmentVariables(imagePath), expectedRoot);
        }
        catch
        {
            // A missing registry read must never be treated as ownership.
            return false;
        }
    }

    internal static bool ImagePathContainsRoot(string imagePath, string nginxRoot)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(nginxRoot))
        {
            return false;
        }

        var expectedRoot = Path.GetFullPath(nginxRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var searchStart = 0;
        while (searchStart < imagePath.Length)
        {
            var match = imagePath.IndexOf(expectedRoot, searchStart, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                return false;
            }

            var end = match + expectedRoot.Length;
            if (end == imagePath.Length ||
                imagePath[end] == '"' ||
                imagePath[end] == '\'' ||
                char.IsWhiteSpace(imagePath[end]) ||
                (imagePath[end] == Path.DirectorySeparatorChar &&
                 (end + 1 == imagePath.Length || imagePath[end + 1] == '"' || char.IsWhiteSpace(imagePath[end + 1]))))
            {
                return true;
            }

            searchStart = match + 1;
        }

        return false;
    }

    public static string BuildServiceImagePath(string executablePath, string nginxRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("MCPanel 主程序路径不能为空。", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(nginxRoot))
        {
            throw new ArgumentException("Nginx 安装目录不能为空。", nameof(nginxRoot));
        }

        return string.Join(" ",
            Compat.QuoteCommandLineArgument(Path.GetFullPath(executablePath)),
            NginxWindowsServiceHost.ServiceArgument,
            Compat.QuoteCommandLineArgument(Path.GetFullPath(nginxRoot)));
    }

    public static void EnsureRegistered(string executablePath, string nginxRoot)
    {
        EnsureAdministrator();

        var imagePath = BuildServiceImagePath(executablePath, nginxRoot);
        if (IsInstalled() && !IsRegisteredForRoot(nginxRoot))
        {
            throw new InvalidOperationException(
                $"Windows 服务 {ServiceName} 已存在，但未指向当前 Nginx 目录，已停止操作以保护现有服务。请先处理该同名服务后重试。");
        }

        if (!IsInstalled())
        {
            RunScOrThrow(
                BuildScArguments(
                    "create",
                    ServiceName,
                    "binPath=",
                    imagePath,
                    "start=",
                    "auto",
                    "DisplayName=",
                    ServiceDisplayName),
                "注册 Nginx Windows 服务");
        }
        else
        {
            RunScOrThrow(
                BuildScArguments(
                    "config",
                    ServiceName,
                    "binPath=",
                    imagePath,
                    "start=",
                    "auto",
                    "DisplayName=",
                    ServiceDisplayName),
                "更新 Nginx Windows 服务配置");
        }

        RunScOrThrow(
            BuildScArguments("description", ServiceName, ServiceDescription),
            "写入 Nginx Windows 服务说明");
        ConfigureRecoveryPolicy();
    }

    /// <summary>
    /// Lets SCM recover the wrapper itself. The in-process watchdog handles
    /// nginx.exe failures while this service process is still alive; SCM
    /// recovery is the second layer for wrapper crashes or explicit failures.
    /// </summary>
    internal static void ConfigureRecoveryPolicy()
    {
        RunScOrThrow(BuildRecoveryPolicyArguments(), "配置 Nginx Windows 服务自动恢复");
        RunScOrThrow(BuildFailureFlagArguments(), "启用 Nginx Windows 服务失败恢复");
    }

    internal static string BuildRecoveryPolicyArguments() =>
        BuildScArguments(
            "failure",
            ServiceName,
            "reset=",
            "86400",
            "actions=",
            "restart/5000/restart/15000/restart/30000");

    internal static string BuildFailureFlagArguments() =>
        BuildScArguments("failureflag", ServiceName, "1");

    public static void Start()
    {
        if (!IsInstalled())
        {
            throw new InvalidOperationException("Nginx Windows 服务尚未注册。请先重新安装 Nginx。");
        }

        if (!IsRunning())
        {
            var result = RunSc(BuildScArguments("start", ServiceName));
            if (result.ExitCode != 0 && !ContainsAlreadyRunning(result))
            {
                ThrowCommandFailure("启动 Nginx Windows 服务", result);
            }
        }

        WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
    }

    public static void Stop()
    {
        if (!IsInstalled() || !IsRunning())
        {
            return;
        }

        var result = RunSc(BuildScArguments("stop", ServiceName));
        if (result.ExitCode != 0 && !ContainsAlreadyStopped(result))
        {
            ThrowCommandFailure("停止 Nginx Windows 服务", result);
        }

        WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
    }

    public static void Delete()
    {
        if (!IsInstalled())
        {
            return;
        }

        var result = RunSc(BuildScArguments("delete", ServiceName));
        if (result.ExitCode != 0 && !ContainsServiceMissing(result))
        {
            ThrowCommandFailure("删除 Nginx Windows 服务", result);
        }

        WaitUntilMissing(TimeSpan.FromSeconds(20));
    }

    public static bool IsAccessDenied(Exception exception)
    {
        return exception is NginxWindowsServiceCommandException commandException &&
               commandException.ExitCode == 5 ||
               exception.Message.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
               exception.Message.Contains("拒绝访问", StringComparison.Ordinal);
    }

    public static void WaitForStatus(ServiceControllerStatus expected, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var controller = new ServiceController(ServiceName);
                controller.Refresh();
                if (controller.Status == expected)
                {
                    return;
                }
            }
            catch (InvalidOperationException) when (expected == ServiceControllerStatus.Stopped)
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new System.TimeoutException($"Nginx Windows 服务未能在规定时间内变为“{expected}”状态。");
    }

    public static void WaitUntilMissing(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (!IsInstalled())
            {
                return;
            }

            Thread.Sleep(250);
        }

        throw new System.TimeoutException("Nginx Windows 服务正在等待系统删除，请稍后重试。");
    }

    private static string BuildScArguments(params string[] arguments) =>
        string.Join(" ", arguments.Select(Compat.QuoteCommandLineArgument));

    private static ScResult RunSc(string arguments)
    {
        var result = ProcessRunner.RunSynchronously(
            "sc.exe",
            arguments,
            ComponentPaths.ApplicationRoot,
            captureOutput: true);
        return new ScResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static void RunScOrThrow(string arguments, string action)
    {
        var result = RunSc(arguments);
        if (result.ExitCode != 0)
        {
            ThrowCommandFailure(action, result);
        }
    }

    private static void ThrowCommandFailure(string action, ScResult result)
    {
        var detail = string.Join(Environment.NewLine, new[] { result.Output, result.Error }
            .Where(text => !string.IsNullOrWhiteSpace(text)))
            .Trim();
        throw new NginxWindowsServiceCommandException(
            result.ExitCode,
            string.IsNullOrWhiteSpace(detail)
                ? $"{action}失败，sc.exe 退出码：{result.ExitCode}"
                : $"{action}失败：{detail}");
    }

    private static bool ContainsAlreadyRunning(ScResult result) =>
        result.ExitCode == 1056 ||
        result.Output.Contains("already been started", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("already been started", StringComparison.OrdinalIgnoreCase) ||
        result.Output.Contains("正在运行", StringComparison.Ordinal) ||
        result.Error.Contains("正在运行", StringComparison.Ordinal);

    private static bool ContainsAlreadyStopped(ScResult result) =>
        result.ExitCode == 1062 ||
        result.Output.Contains("1062", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("1062", StringComparison.OrdinalIgnoreCase) ||
        result.Output.Contains("not started", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("not started", StringComparison.OrdinalIgnoreCase) ||
        result.Output.Contains("已停止", StringComparison.Ordinal);

    private static bool ContainsServiceMissing(ScResult result) =>
        result.ExitCode == 1060 ||
        result.Output.Contains("1060", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("1060", StringComparison.OrdinalIgnoreCase) ||
        result.Output.Contains("does not exist", StringComparison.OrdinalIgnoreCase) ||
        result.Error.Contains("does not exist", StringComparison.OrdinalIgnoreCase);

    private static void EnsureAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        if (!new System.Security.Principal.WindowsPrincipal(identity)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator))
        {
            throw new InvalidOperationException("注册 Nginx Windows 服务需要管理员权限，请从安装按钮重新授权。");
        }
    }

    private sealed record ScResult(int ExitCode, string Output, string Error);
}

internal sealed class NginxWindowsServiceCommandException : InvalidOperationException
{
    public NginxWindowsServiceCommandException(int exitCode, string message)
        : base(message)
    {
        ExitCode = exitCode;
    }

    public int ExitCode { get; }
}

internal static class NginxWindowsServiceHost
{
    public const string ServiceArgument = "--nginx-service";

    public static bool IsServiceRequest(string[] args) =>
        Array.Exists(args, argument =>
            string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (!TryGetNginxRoot(args, out var nginxRoot))
        {
            return 2;
        }

        ServiceBase.Run(new NginxWindowsService(nginxRoot));
        return 0;
    }

    private static bool TryGetNginxRoot(string[] args, out string nginxRoot)
    {
        nginxRoot = string.Empty;
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return false;
        }

        try
        {
            nginxRoot = Path.GetFullPath(args[index + 1])
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.Exists(nginxRoot) &&
                   File.Exists(Path.Combine(nginxRoot, "nginx.exe"));
        }
        catch
        {
            nginxRoot = string.Empty;
            return false;
        }
    }
}

internal sealed class NginxWindowsService : ServiceBase
{
    internal const int MaxWatchdogRecoveriesPerWindow = 5;
    internal static readonly TimeSpan WatchdogRecoveryWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private readonly string _nginxRoot;
    private readonly object _logGate = new();
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

    internal static TimeSpan GetRecoveryDelay(int previousAttempts)
    {
        if (previousAttempts < 0)
        {
            previousAttempts = 0;
        }

        return RecoveryDelays[Math.Min(previousAttempts, RecoveryDelays.Length - 1)];
    }

    internal static bool ShouldEscalateWatchdog(int attemptsInWindow) =>
        attemptsInWindow >= MaxWatchdogRecoveriesPerWindow;

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
        var recoveryHistory = new Queue<DateTime>();
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
            while (recoveryHistory.Count > 0 &&
                   now - recoveryHistory.Peek() > WatchdogRecoveryWindow)
            {
                recoveryHistory.Dequeue();
            }

            if (ShouldEscalateWatchdog(recoveryHistory.Count))
            {
                FailServiceProcess(
                    $"Nginx 在 {WatchdogRecoveryWindow.TotalMinutes:0} 分钟内连续异常超过 {MaxWatchdogRecoveriesPerWindow} 次，停止服务并交给 Windows 服务恢复机制处理。");
                return;
            }

            var delay = GetRecoveryDelay(recoveryHistory.Count);
            recoveryHistory.Enqueue(now);
            var healthReason = processRunning
                ? "nginx.exe 仍存在但配置端口未全部监听"
                : "nginx.exe 已退出";
            WriteServiceLog(
                $"Watchdog detected unhealthy Nginx ({healthReason}). Recovery attempt {recoveryHistory.Count}, waiting {delay.TotalSeconds:0}s.");

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
                if (ShouldEscalateWatchdog(recoveryHistory.Count))
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

    private void WriteServiceLog(string message)
    {
        try
        {
            lock (_logGate)
            {
                var logPath = GetServiceLogPath(_nginxRoot);
                Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
                File.AppendAllText(
                    logPath,
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} {message}{Environment.NewLine}",
                    new System.Text.UTF8Encoding(false));
            }
        }
        catch
        {
            // Diagnostics must never take down the service.
        }
    }
}
