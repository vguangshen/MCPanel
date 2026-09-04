using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Text;

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
    public static Process Start(
        string fileName,
        string arguments,
        string? workingDirectory = null,
        bool elevated = false,
        bool captureOutput = false,
        ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden)
    {
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
            RedirectStandardOutput = !elevated && captureOutput,
            RedirectStandardError = !elevated && captureOutput,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

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

        var fullScriptPath = Path.GetFullPath(scriptPath);
        var arguments = $"-NoProfile -ExecutionPolicy Bypass -File {Compat.QuoteCommandLineArgument(fullScriptPath)}";
        return Start(
            "powershell.exe",
            arguments,
            Path.GetDirectoryName(fullScriptPath),
            elevated,
            captureOutput);
    }

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
        TimeSpan? timeout = null)
    {
        using var process = Start(fileName, arguments, workingDirectory, elevated, captureOutput);
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
        TimeSpan? timeout = null)
    {
        using var process = Start(fileName, arguments, workingDirectory, elevated, captureOutput);
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
        TimeSpan? timeout = null) =>
        RunAsync(
            "powershell.exe",
            $"-NoProfile -ExecutionPolicy Bypass -File {Compat.QuoteCommandLineArgument(Path.GetFullPath(scriptPath))}",
            Path.GetDirectoryName(Path.GetFullPath(scriptPath)),
            elevated,
            cancellationToken,
            captureOutput,
            timeout);

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
