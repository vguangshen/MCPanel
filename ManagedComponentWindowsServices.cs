using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.Win32;
using ServiceBase = System.ServiceProcess.ServiceBase;
using ServiceController = System.ServiceProcess.ServiceController;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace MCPanel;

internal static class ManagedWindowsServiceController
{
    internal sealed record ScResult(int ExitCode, string Output, string Error);

    public static bool IsInstalled(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
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

    public static bool IsRunning(string serviceName)
    {
        try
        {
            using var controller = new ServiceController(serviceName);
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

    public static bool IsRegisteredForRoot(string serviceName, string serviceArgument, string componentRoot)
    {
        if (!IsInstalled(serviceName) || string.IsNullOrWhiteSpace(componentRoot))
        {
            return false;
        }

        try
        {
            var expectedRoot = Path.GetFullPath(componentRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var imagePath = key?.GetValue("ImagePath")?.ToString();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return false;
            }

            var expanded = Environment.ExpandEnvironmentVariables(imagePath);
            return expanded.Contains(serviceArgument, StringComparison.OrdinalIgnoreCase) &&
                   ImagePathContainsRoot(expanded, expectedRoot);
        }
        catch
        {
            return false;
        }
    }

    internal static bool ImagePathContainsRoot(string imagePath, string componentRoot)
    {
        if (string.IsNullOrWhiteSpace(imagePath) || string.IsNullOrWhiteSpace(componentRoot))
        {
            return false;
        }

        var expectedRoot = Path.GetFullPath(componentRoot)
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

    public static string BuildServiceImagePath(string executablePath, string serviceArgument, string componentRoot)
    {
        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("MCPanel 主程序路径不能为空。", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(componentRoot))
        {
            throw new ArgumentException("服务组件目录不能为空。", nameof(componentRoot));
        }

        return string.Join(" ",
            Compat.QuoteCommandLineArgument(Path.GetFullPath(executablePath)),
            serviceArgument,
            Compat.QuoteCommandLineArgument(Path.GetFullPath(componentRoot)));
    }

    public static void EnsureRegistered(
        string serviceName,
        string displayName,
        string description,
        string executablePath,
        string serviceArgument,
        string componentRoot,
        bool configureRecovery = true)
    {
        var imagePath = BuildServiceImagePath(executablePath, serviceArgument, componentRoot);
        if (IsInstalled(serviceName) && !IsRegisteredForRoot(serviceName, serviceArgument, componentRoot))
        {
            throw new InvalidOperationException(
                $"Windows 服务 {serviceName} 已存在，但未指向当前 MCPanel 组件目录，已停止操作以保护现有服务。");
        }

        var operation = IsInstalled(serviceName) ? "config" : "create";
        RunScOrThrow(
            BuildScArguments(
                operation,
                serviceName,
                "binPath=",
                imagePath,
                "start=",
                "auto",
                "DisplayName=",
                displayName),
            $"{(operation == "create" ? "注册" : "更新")} {displayName} Windows 服务",
            elevated: true);

        RunScOrThrow(
            BuildScArguments("description", serviceName, description),
            $"写入 {displayName} Windows 服务说明",
            elevated: true);
        if (configureRecovery)
        {
            ConfigureRecovery(serviceName, displayName);
        }
        else
        {
            DisableRecovery(serviceName, displayName);
        }
    }

    public static void ConfigureRecovery(string serviceName, string displayName)
    {
        RunScOrThrow(BuildRecoveryPolicyArguments(serviceName), $"配置 {displayName} Windows 服务自动恢复", elevated: true);
        RunScOrThrow(BuildFailureFlagArguments(serviceName), $"启用 {displayName} Windows 服务失败恢复", elevated: true);
    }

    public static void DisableRecovery(string serviceName, string displayName)
    {
        RunScOrThrow(BuildDisableRecoveryPolicyArguments(serviceName), $"清除 {displayName} Windows 服务自动恢复", elevated: true);
        RunScOrThrow(BuildFailureFlagArguments(serviceName, enabled: false), $"禁用 {displayName} Windows 服务失败恢复", elevated: true);
    }

    internal static string BuildRecoveryPolicyArguments(string serviceName) =>
        BuildScArguments(
            "failure",
            serviceName,
            "reset=",
            "86400",
            "actions=",
            "restart/5000/restart/15000/restart/30000");

    internal static string BuildDisableRecoveryPolicyArguments(string serviceName) =>
        BuildScArguments(
            "failure",
            serviceName,
            "reset=",
            "0",
            "actions=",
            string.Empty);

    internal static string BuildFailureFlagArguments(string serviceName, bool enabled = true) =>
        BuildScArguments("failureflag", serviceName, enabled ? "1" : "0");

    public static void StartWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            throw new InvalidOperationException($"{displayName} Windows 服务尚未注册，请先重新安装该组件。");
        }

        var result = RunSc(BuildScArguments("start", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1056)
        {
            ThrowCommandFailure($"启动 {displayName} Windows 服务", result);
        }
    }

    public static void StopWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("stop", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1062)
        {
            ThrowCommandFailure($"停止 {displayName} Windows 服务", result);
        }
    }

    public static void DeleteWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("delete", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1060)
        {
            ThrowCommandFailure($"删除 {displayName} Windows 服务", result);
        }
    }
    public static void Start(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            throw new InvalidOperationException($"{displayName} Windows 服务尚未注册，请先重新安装该组件。");
        }

        if (!IsRunning(serviceName))
        {
            var result = RunSc(BuildScArguments("start", serviceName), elevated: true);
            if (result.ExitCode != 0 && result.ExitCode != 1056)
            {
                ThrowCommandFailure($"启动 {displayName} Windows 服务", result);
            }
        }

        WaitForStatus(serviceName, displayName, ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
    }

    public static void Stop(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName) || !IsRunning(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("stop", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1062)
        {
            ThrowCommandFailure($"停止 {displayName} Windows 服务", result);
        }

        WaitForStatus(serviceName, displayName, ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
    }

    public static void Delete(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("delete", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1060)
        {
            ThrowCommandFailure($"删除 {displayName} Windows 服务", result);
        }

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(25);
        while (DateTime.UtcNow < deadline)
        {
            if (!IsInstalled(serviceName))
            {
                return;
            }
            Thread.Sleep(250);
        }

        throw new TimeoutException($"{displayName} Windows 服务正在等待系统删除，请稍后重试。");
    }

    private static void WaitForStatus(
        string serviceName,
        string displayName,
        ServiceControllerStatus expected,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                using var controller = new ServiceController(serviceName);
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

        throw new TimeoutException($"{displayName} Windows 服务未能在规定时间内变为“{expected}”状态。");
    }

    private static string BuildScArguments(params string[] arguments) =>
        string.Join(" ", arguments.Select(Compat.QuoteCommandLineArgument));

    private static ScResult RunSc(string arguments, bool elevated)
    {
        var result = ProcessRunner.RunSynchronously(
            "sc.exe",
            arguments,
            ComponentPaths.ApplicationRoot,
            elevated: elevated,
            captureOutput: !elevated,
            timeout: TimeSpan.FromMinutes(2));
        return new ScResult(result.ExitCode, result.StandardOutput, result.StandardError);
    }

    private static void RunScOrThrow(string arguments, string action, bool elevated)
    {
        var result = RunSc(arguments, elevated);
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
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(detail)
                ? $"{action}失败，sc.exe 退出码：{result.ExitCode}"
                : $"{action}失败：{detail}");
    }
}

internal static class TomcatWindowsServiceManager
{
    public const string ServiceName = "MCPanelTomcat";
    public const string ServiceDisplayName = "Tomcat Server (MCPanel)";
    public const string ServiceDescription = "Tomcat 8.5.57 Web 服务器（由 MCPanel 管理；不自动恢复崩溃实例）。";

    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRegisteredForRoot(string tomcatRoot) =>
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    public static string BuildServiceImagePath(string executablePath, string tomcatRoot) =>
        ManagedWindowsServiceController.BuildServiceImagePath(executablePath, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    public static void EnsureRegistered(string executablePath, string tomcatRoot) =>
        ManagedWindowsServiceController.EnsureRegistered(
            ServiceName,
            ServiceDisplayName,
            ServiceDescription,
            executablePath,
            TomcatWindowsServiceHost.ServiceArgument,
            tomcatRoot,
            configureRecovery: false);

    public static void Start()
    {
        // Tomcat is launched through SCM, but MCPanel deliberately does not wait
        // for SCM status transitions. The Java process/ports are the runtime truth.
        ManagedWindowsServiceController.DisableRecovery(ServiceName, ServiceDisplayName);
        ManagedWindowsServiceController.StartWithoutStatusWait(ServiceName, ServiceDisplayName);
    }

    public static void Stop() => ManagedWindowsServiceController.StopWithoutStatusWait(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.DeleteWithoutStatusWait(ServiceName, ServiceDisplayName);
}

internal static class FrpWindowsServiceManager
{
    public const string ServiceName = "MCPanelFrp";
    public const string ServiceDisplayName = "FRP Client (MCPanel)";
    public const string ServiceDescription = "FRP 内网穿透客户端（由 MCPanel 管理）。";

    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRunning() => ManagedWindowsServiceController.IsRunning(ServiceName);
    public static bool IsRunningForRoot(string applicationRoot) =>
        IsRunning() && ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, FrpWindowsServiceHost.ServiceArgument, applicationRoot);
    public static bool IsRegisteredForRoot(string applicationRoot) =>
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, FrpWindowsServiceHost.ServiceArgument, applicationRoot);

    public static string BuildServiceImagePath(string executablePath, string applicationRoot) =>
        ManagedWindowsServiceController.BuildServiceImagePath(executablePath, FrpWindowsServiceHost.ServiceArgument, applicationRoot);

    public static void EnsureRegistered(string executablePath, string applicationRoot) =>
        ManagedWindowsServiceController.EnsureRegistered(
            ServiceName,
            ServiceDisplayName,
            ServiceDescription,
            executablePath,
            FrpWindowsServiceHost.ServiceArgument,
            applicationRoot);

    public static void Start() => ManagedWindowsServiceController.Start(ServiceName, ServiceDisplayName);
    public static void Stop() => ManagedWindowsServiceController.Stop(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.Delete(ServiceName, ServiceDisplayName);
    internal static string BuildRecoveryPolicyArguments() => ManagedWindowsServiceController.BuildRecoveryPolicyArguments(ServiceName);
    internal static string BuildFailureFlagArguments() => ManagedWindowsServiceController.BuildFailureFlagArguments(ServiceName);
}

internal static class TomcatWindowsServiceHost
{
    public const string ServiceArgument = "--tomcat-service";

    public static bool IsServiceRequest(string[] args) =>
        Array.Exists(args, argument => string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (!TryGetRoot(args, out var root))
        {
            return 2;
        }

        ServiceBase.Run(new TomcatWindowsService(root));
        return 0;
    }

    private static bool TryGetRoot(string[] args, out string root)
    {
        root = string.Empty;
        var index = Array.FindIndex(args, argument => string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return false;
        }

        root = Path.GetFullPath(args[index + 1]);
        return Directory.Exists(root);
    }
}

internal static class FrpWindowsServiceHost
{
    public const string ServiceArgument = "--frp-service";

    public static bool IsServiceRequest(string[] args) =>
        Array.Exists(args, argument => string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));

    public static int Run(string[] args)
    {
        if (!TryGetRoot(args, out var root))
        {
            return 2;
        }

        ServiceBase.Run(new FrpWindowsService(root));
        return 0;
    }

    private static bool TryGetRoot(string[] args, out string root)
    {
        root = string.Empty;
        var index = Array.FindIndex(args, argument => string.Equals(argument, ServiceArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 1 >= args.Length || string.IsNullOrWhiteSpace(args[index + 1]))
        {
            return false;
        }

        root = Path.GetFullPath(args[index + 1]);
        return Directory.Exists(root);
    }
}

internal sealed class TomcatWindowsService : ServiceBase
{
    private readonly string _tomcatRoot;
    private int _stopStarted;

    public TomcatWindowsService(string tomcatRoot)
    {
        _tomcatRoot = Path.GetFullPath(tomcatRoot);
        ServiceName = TomcatWindowsServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    internal static string GetServiceLogPath(string tomcatRoot) => Path.Combine(tomcatRoot, "logs", "mcpanel-service.log");

    protected override void OnStart(string[] args)
    {
        Interlocked.Exchange(ref _stopStarted, 0);
        RequestAdditionalTime(120000);
        StartTomcatAndVerify(CancellationToken.None, "service start");
        WriteServiceLog("Tomcat Windows service started; automatic recovery disabled.");
    }

    protected override void OnStop()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        WriteServiceLog("Tomcat Windows service wrapper stopped; runtime cleanup delegated to MCPanel controller.");
    }

    protected override void OnShutdown()
    {
        StopTomcat(waitForExitAndForce: true);
        base.OnShutdown();
    }

    private void StartTomcatAndVerify(CancellationToken cancellationToken, string reason)
    {
        var startup = Path.Combine(_tomcatRoot, "bin", "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("未找到 Tomcat startup.bat。", startup);
        }

        var ports = TomcatRuntimeProbe.ReadHttpPorts(_tomcatRoot).ToArray();
        if (ports.Length == 0)
        {
            throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。");
        }

        TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken).GetAwaiter().GetResult();
        if (TomcatProductInstanceManager.IsSharedTomcatRunning())
        {
            if (TomcatRuntimeProbe.ArePortsListening(ports))
            {
                return;
            }
            StopSharedTomcatBestEffort();
        }

        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(_tomcatRoot);
        WriteServiceLog($"Starting shared Tomcat ({reason}).");
        var result = ProcessRunner.RunFileAsync(
                startup,
                string.Empty,
                Path.Combine(_tomcatRoot, "bin"),
                elevated: false,
                cancellationToken,
                captureOutput: false,
                timeout: TimeSpan.FromSeconds(30))
            .GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"Tomcat startup.bat 退出码：{result.ExitCode}");
        }

        // Do not block the Windows service on connector/application readiness; startup.bat returning successfully is sufficient here.
    }

    private void StopTomcat(bool waitForExitAndForce)
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        StopSharedTomcatBestEffort(waitForExitAndForce);
        WriteServiceLog(waitForExitAndForce
            ? "Tomcat Windows service stopped."
            : "Tomcat Windows service stop request sent; runtime cleanup delegated to MCPanel controller.");
    }

    private void StopSharedTomcatBestEffort(bool waitForExitAndForce = true)
    {
        try
        {
            var shutdown = Path.Combine(_tomcatRoot, "bin", "shutdown.bat");
            if (File.Exists(shutdown))
            {
                try
                {
                    ProcessRunner.RunFileAsync(
                            shutdown,
                            string.Empty,
                            Path.Combine(_tomcatRoot, "bin"),
                            elevated: false,
                            CancellationToken.None,
                            captureOutput: false,
                            timeout: TimeSpan.FromSeconds(30))
                        .GetAwaiter().GetResult();
                }
                catch (Exception ex)
                {
                    WriteServiceLog("Tomcat shutdown.bat failed: " + ex.Message);
                }
            }

            if (!waitForExitAndForce)
            {
                return;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && TomcatProductInstanceManager.IsSharedTomcatRunning())
            {
                Thread.Sleep(250);
            }

            if (TomcatProductInstanceManager.IsSharedTomcatRunning())
            {
                TomcatProductInstanceManager.StopAllTomcatProcessesAsync(CancellationToken.None, throwOnFailure: false)
                    .GetAwaiter().GetResult();
            }
        }
        catch (Exception ex)
        {
            WriteServiceLog("Tomcat forced stop failed: " + ex.Message);
        }
    }

    private void WriteServiceLog(string message)
    {
        try
        {
            var path = GetServiceLogPath(_tomcatRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RollingLogWriter.Append(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }
}

internal sealed class FrpWindowsService : ServiceBase
{
    internal const int MaxRecoveriesPerWindow = 5;
    internal static readonly TimeSpan RecoveryWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan[] RecoveryDelays =
    [
        TimeSpan.FromSeconds(1),
        TimeSpan.FromSeconds(3),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
        TimeSpan.FromSeconds(60)
    ];

    private readonly string _applicationRoot;
    private readonly object _processGate = new();
    private Process? _process;
    private CancellationTokenSource? _watchdogCancellation;
    private Task? _watchdogTask;
    private int _stopStarted;

    public FrpWindowsService(string applicationRoot)
    {
        _applicationRoot = Path.GetFullPath(applicationRoot);
        ServiceName = FrpWindowsServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    private string WorkRoot => Path.Combine(_applicationRoot, "Frp");
    private string FrpcPath => Path.Combine(WorkRoot, "frpc.exe");
    private string ConfigPath => Path.Combine(WorkRoot, "frpc.toml");
    internal static string GetServiceLogPath(string applicationRoot) => Path.Combine(applicationRoot, "Frp", "frp-service.log");
    internal static TimeSpan GetRecoveryDelay(int previousAttempts) =>
        RecoveryDelays[Math.Min(Math.Max(previousAttempts, 0), RecoveryDelays.Length - 1)];
    internal static bool ShouldEscalate(int attemptsInWindow) => attemptsInWindow >= MaxRecoveriesPerWindow;

    protected override void OnStart(string[] args)
    {
        Interlocked.Exchange(ref _stopStarted, 0);
        RequestAdditionalTime(60000);
        _watchdogCancellation?.Dispose();
        _watchdogCancellation = new CancellationTokenSource();
        try
        {
            StartFrpAndVerify("service start");
            WriteServiceLog("FRP Windows service started; watchdog active.");
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

    protected override void OnStop() => StopFrpService();

    protected override void OnShutdown()
    {
        StopFrpService();
        base.OnShutdown();
    }

    private void WatchdogLoop(CancellationToken cancellationToken)
    {
        var recoveryHistory = new Queue<DateTime>();
        while (!cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(2)))
        {
            if (IsChildRunning())
            {
                continue;
            }

            var now = DateTime.UtcNow;
            while (recoveryHistory.Count > 0 && now - recoveryHistory.Peek() > RecoveryWindow)
            {
                recoveryHistory.Dequeue();
            }

            if (ShouldEscalate(recoveryHistory.Count))
            {
                WriteServiceLog("FRP repeatedly exited; escalating to SCM recovery.");
                ExitCode = 1;
                try { Stop(); } catch { }
                return;
            }

            var delay = GetRecoveryDelay(recoveryHistory.Count);
            recoveryHistory.Enqueue(now);
            WriteServiceLog($"FRP child exited; recovery in {delay.TotalSeconds:0}s.");
            if (cancellationToken.WaitHandle.WaitOne(delay))
            {
                return;
            }

            try
            {
                StartFrpAndVerify("watchdog recovery");
                WriteServiceLog("FRP watchdog recovery succeeded.");
            }
            catch (Exception ex)
            {
                WriteServiceLog("FRP watchdog recovery failed: " + ex.Message);
            }
        }
    }

    private bool IsChildRunning()
    {
        lock (_processGate)
        {
            try
            {
                return _process is { HasExited: false };
            }
            catch
            {
                return false;
            }
        }
    }

    private void StartFrpAndVerify(string reason)
    {
        if (!File.Exists(FrpcPath))
        {
            throw new FileNotFoundException("未找到 frpc.exe。", FrpcPath);
        }
        if (!File.Exists(ConfigPath))
        {
            throw new FileNotFoundException("未找到 frpc.toml。", ConfigPath);
        }

        VerifyConfiguration();
        StopOwnedChild();
        KillLegacyFrpProcesses();

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = FrpcPath,
                Arguments = $"-c {Compat.QuoteCommandLineArgument(ConfigPath)}",
                WorkingDirectory = WorkRoot,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            },
            EnableRaisingEvents = true
        };
        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) WriteServiceLog("frpc: " + eventArgs.Data);
        };
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data)) WriteServiceLog("frpc stderr: " + eventArgs.Data);
        };

        WriteServiceLog($"Starting frpc ({reason}).");
        if (!process.Start())
        {
            process.Dispose();
            throw new InvalidOperationException("无法启动 frpc.exe。");
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        lock (_processGate)
        {
            _process = process;
        }

        Thread.Sleep(1000);
        if (process.HasExited)
        {
            var exitCode = process.ExitCode;
            StopOwnedChild();
            throw new InvalidOperationException($"frpc 启动后立即退出，退出码：{exitCode}");
        }
    }

    private void VerifyConfiguration()
    {
        var result = ProcessRunner.RunSynchronously(
            FrpcPath,
            $"verify -c {Compat.QuoteCommandLineArgument(ConfigPath)}",
            WorkRoot,
            elevated: false,
            captureOutput: true,
            timeout: TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"frpc 配置验证失败，退出码：{result.ExitCode}"
                : result.CombinedOutput);
        }
    }

    private void StopFrpService()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        try { _watchdogCancellation?.Cancel(); } catch { }
        try { _watchdogTask?.Wait(TimeSpan.FromSeconds(10)); } catch { }
        _watchdogTask = null;
        _watchdogCancellation?.Dispose();
        _watchdogCancellation = null;
        StopOwnedChild();
        WriteServiceLog("FRP Windows service stopped.");
    }

    private void StopOwnedChild()
    {
        Process? process;
        lock (_processGate)
        {
            process = _process;
            _process = null;
        }

        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(10000);
            }
        }
        catch
        {
        }
        finally
        {
            process.Dispose();
        }
    }

    private void KillLegacyFrpProcesses()
    {
        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable) &&
                    Path.GetFullPath(executable).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private void WriteServiceLog(string message)
    {
        try
        {
            var path = GetServiceLogPath(_applicationRoot);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            RollingLogWriter.Append(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", Encoding.UTF8);
        }
        catch
        {
        }
    }
}
