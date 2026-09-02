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
/// nginx-server.exe wrapper.  MCPanel.exe therefore hosts an equivalent
/// ServiceBase entry point and starts nginx.exe from the installed runtime
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
    /// Checks the service state and its registered component root.  Reading
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
    }

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
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "sc.exe",
            Arguments = arguments,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法启动 Windows 服务控制程序 sc.exe。");

        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return new ScResult(process.ExitCode, output, error);
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
    private readonly string _nginxRoot;
    private int _stopStarted;

    public NginxWindowsService(string nginxRoot)
    {
        _nginxRoot = nginxRoot;
        ServiceName = NginxWindowsServiceManager.ServiceName;
        CanStop = true;
        CanShutdown = true;
        AutoLog = true;
    }

    protected override void OnStart(string[] args)
    {
        var nginxExe = Path.Combine(_nginxRoot, "nginx.exe");
        if (!File.Exists(nginxExe))
        {
            throw new FileNotFoundException("未找到 Nginx 运行文件。", nginxExe);
        }

        NginxRuntimeManager.KillProcessesUnderRoot(_nginxRoot);
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = nginxExe,
            WorkingDirectory = _nginxRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 Nginx 进程。");
        }

        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (NginxRuntimeManager.IsRunningUnderRoot(_nginxRoot))
            {
                return;
            }

            if (process.HasExited)
            {
                break;
            }

            Thread.Sleep(250);
        }

        var log = NginxRuntimeManager.ReadRecentErrorLog(_nginxRoot);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
            ? "Nginx 服务启动后未检测到 nginx.exe。"
            : $"Nginx 服务启动失败：{log}");
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

    private void StopNginx()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        var nginxExe = Path.Combine(_nginxRoot, "nginx.exe");
        try
        {
            if (File.Exists(nginxExe))
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = nginxExe,
                    Arguments = "-s quit",
                    WorkingDirectory = _nginxRoot,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                });
                process?.WaitForExit(10000);
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
    }
}
