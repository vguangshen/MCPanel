using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MCPanel;

internal sealed record ProcessRunResult(int ExitCode, string StandardOutput, string StandardError)
{
    public string CombinedOutput =>
        string.Join(Environment.NewLine, new[] { StandardOutput, StandardError }
            .Where(text => !string.IsNullOrWhiteSpace(text)))
        .Trim();
}

/// <summary>
/// Single entry point for launching local commands and UAC-backed commands.
/// It keeps shell, working-directory, output-capture and cancellation behavior
/// consistent across installers, environment actions and product deployment.
/// </summary>
internal static class ProcessRunner
{
    [DllImport("kernel32.dll")]
    private static extern uint GetOEMCP();

    private static readonly Lazy<Encoding> ConsoleOutputEncodingValue = new(() =>
    {
        try
        {
            return Encoding.GetEncoding((int)GetOEMCP());
        }
        catch
        {
            return Encoding.UTF8;
        }
    });

    /// <summary>
    /// Encoding that Windows console tools (sc.exe, netstat, appcmd, nginx,
    /// mysqld) and Windows PowerShell 5.1 use for redirected output. A hidden
    /// child console starts with the system OEM code page (936/GBK on Chinese
    /// Windows, 437/850 on Western systems, 65001 when the system UTF-8 option
    /// is on), so decoding as UTF-8 garbles localized error messages.
    /// Programs that always emit UTF-8 (Go tools such as frpc) must pass
    /// <see cref="Encoding.UTF8"/> explicitly.
    /// </summary>
    internal static Encoding ConsoleOutputEncoding => ConsoleOutputEncodingValue.Value;

    private static readonly Lazy<string?> WindowsPowerShellVersionValue = new(ReadWindowsPowerShellVersion);

    private static string? ReadWindowsPowerShellVersion()
    {
        try
        {
            using var machine = RegistryKey.OpenBaseKey(
                RegistryHive.LocalMachine,
                Environment.Is64BitOperatingSystem ? RegistryView.Registry64 : RegistryView.Default);
            using var engine = machine.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine");
            return engine?.GetValue("PowerShellVersion") as string;
        }
        catch
        {
            // Unknown is treated as supported so a registry permission problem
            // never blocks a machine that actually has PowerShell 3.0+.
            return "unknown";
        }
    }

    /// <summary>
    /// MCPanel's generated component scripts need Windows PowerShell 3.0+.
    /// Windows 7 SP1 / Server 2008 R2 ship with 2.0, where the scripts fail
    /// with confusing parse errors. Fail early with an actionable message.
    /// </summary>
    internal static void EnsureSupportedWindowsPowerShell()
    {
        if (string.IsNullOrWhiteSpace(WindowsPowerShellVersionValue.Value))
        {
            throw new InvalidOperationException(
                "此系统的 Windows PowerShell 低于 3.0，无法运行 MCPanel 的组件管理脚本。" +
                "请安装 Windows Management Framework 5.1（KB3191566），重启后重试。");
        }
    }

    public static Process Start(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        bool elevated = false,
        bool captureOutput = false,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden,
        Encoding? outputEncoding = null)
    {
        var redirectOutput = !elevated && captureOutput;
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments ?? string.Empty,
            WorkingDirectory = string.IsNullOrWhiteSpace(workingDirectory)
                ? ComponentPaths.ApplicationRoot
                : workingDirectory,
            UseShellExecute = elevated,
            Verb = elevated && !IsAdministrator() ? "runas" : string.Empty,
            CreateNoWindow = !elevated && windowStyle == ProcessWindowStyle.Hidden,
            WindowStyle = windowStyle,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput
        };

        // .NET Framework rejects these properties unless the corresponding
        // standard streams are redirected. Detached and elevated processes
        // intentionally do not redirect their output.
        if (redirectOutput)
        {
            var encoding = outputEncoding ?? ConsoleOutputEncoding;
            startInfo.StandardOutputEncoding = encoding;
            startInfo.StandardErrorEncoding = encoding;
        }

        return Process.Start(startInfo)
            ?? throw new InvalidOperationException($"无法启动进程：{fileName}");
    }

    public static Process StartPowerShellFile(
        string scriptPath,
        bool elevated,
        bool captureOutput = false)
    {
        if (!File.Exists(scriptPath))
        {
            throw new FileNotFoundException("找不到 PowerShell 脚本。", scriptPath);
        }

        EnsureSupportedWindowsPowerShell();
        var fullScriptPath = Path.GetFullPath(scriptPath);
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Compat.QuoteCommandLineArgument(fullScriptPath)}";
        return Start(
            NativeWindowsPowerShellPath(),
            arguments,
            Path.GetDirectoryName(fullScriptPath),
            elevated,
            captureOutput);
    }

    internal static string NativeWindowsPowerShellPath() =>
        ResolveWindowsPowerShellPath(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.Is64BitOperatingSystem,
            Environment.Is64BitProcess);

    internal static string ResolveWindowsPowerShellPath(string windowsDirectory, bool is64BitOs, bool is64BitProcess) =>
        Path.Combine(
            windowsDirectory,
            is64BitOs && !is64BitProcess ? "Sysnative" : "System32",
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");

    public static Process StartFile(
        string fileName,
        string arguments,
        string workingDirectory,
        bool elevated = false,
        bool captureOutput = false,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException("找不到运行文件。", fileName);
        }

        var fullFileName = Path.GetFullPath(fileName);
        var extension = Path.GetExtension(fullFileName);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command = Compat.QuoteCommandLineArgument(fullFileName);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                command += " " + arguments;
            }

            return Start(
                "cmd.exe",
                $"/d /s /c \"{command}\"",
                workingDirectory,
                elevated,
                captureOutput,
                windowStyle);
        }

        return Start(fullFileName, arguments, workingDirectory, elevated, captureOutput, windowStyle);
    }

    public static async Task<ProcessRunResult> RunAsync(
        string fileName,
        string arguments,
        string? workingDirectory,
        bool elevated,
        CancellationToken cancellationToken,
        bool captureOutput = false,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null)
    {
        using var process = Start(fileName, arguments, workingDirectory, elevated, captureOutput, outputEncoding: outputEncoding);
        var outputTask = !elevated && captureOutput
            ? process.StandardOutput.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);
        var errorTask = !elevated && captureOutput
            ? process.StandardError.ReadToEndAsync(cancellationToken)
            : Task.FromResult(string.Empty);

        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken, timeout);
        return new ProcessRunResult(process.ExitCode, await outputTask, await errorTask);
    }

    public static ProcessRunResult RunSynchronously(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        bool elevated = false,
        bool captureOutput = false,
        TimeSpan? timeout = null,
        Encoding? outputEncoding = null)
    {
        using var process = Start(fileName, arguments, workingDirectory, elevated, captureOutput, outputEncoding: outputEncoding);
        var outputTask = !elevated && captureOutput
            ? process.StandardOutput.ReadToEndAsync()
            : Task.FromResult(string.Empty);
        var errorTask = !elevated && captureOutput
            ? process.StandardError.ReadToEndAsync()
            : Task.FromResult(string.Empty);

        var exited = true;
        if (timeout is null)
        {
            process.WaitForExit();
        }
        else
        {
            exited = process.WaitForExit((int)Math.Max(0, timeout.Value.TotalMilliseconds));
        }
        if (!exited)
        {
            ProcessLifecycle.TryKill(process);
            throw new TimeoutException($"进程在 {timeout?.TotalSeconds:0} 秒内没有退出，已停止该进程。");
        }

        return new ProcessRunResult(
            process.ExitCode,
            outputTask.GetAwaiter().GetResult(),
            errorTask.GetAwaiter().GetResult());
    }

    public static Task<ProcessRunResult> RunPowerShellFileAsync(
        string scriptPath,
        bool elevated,
        CancellationToken cancellationToken,
        bool captureOutput = false,
        TimeSpan? timeout = null)
    {
        EnsureSupportedWindowsPowerShell();
        return RunAsync(
            NativeWindowsPowerShellPath(),
            $"-NoProfile -ExecutionPolicy Bypass -File {Compat.QuoteCommandLineArgument(Path.GetFullPath(scriptPath))}",
            Path.GetDirectoryName(Path.GetFullPath(scriptPath)),
            elevated,
            cancellationToken,
            captureOutput,
            timeout);
    }

    public static Task<ProcessRunResult> RunFileAsync(
        string fileName,
        string arguments,
        string workingDirectory,
        bool elevated,
        CancellationToken cancellationToken,
        bool captureOutput = false,
        TimeSpan? timeout = null)
    {
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException("找不到运行文件。", fileName);
        }

        var fullFileName = Path.GetFullPath(fileName);
        var extension = Path.GetExtension(fullFileName);
        if (extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            var command = Compat.QuoteCommandLineArgument(fullFileName);
            if (!string.IsNullOrWhiteSpace(arguments))
            {
                command += " " + arguments;
            }

            return RunAsync(
                "cmd.exe",
                $"/d /s /c \"{command}\"",
                workingDirectory,
                elevated,
                cancellationToken,
                captureOutput,
                timeout);
        }

        return RunAsync(
            fullFileName,
            arguments,
            workingDirectory,
            elevated,
            cancellationToken,
            captureOutput,
            timeout);
    }

    public static void StartDetached(string fileName, string workingDirectory, bool elevated = false)
    {
        using var process = StartFile(fileName, string.Empty, workingDirectory, elevated);
    }

    internal static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }
}
