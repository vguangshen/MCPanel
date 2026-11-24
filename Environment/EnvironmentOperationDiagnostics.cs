using System.IO;
using System.Text;

namespace MCPanel;

internal sealed class EnvironmentOperationException : InvalidOperationException
{
    public EnvironmentOperationException(
        string message,
        string? logPath,
        Exception? innerException = null,
        int? exitCode = null)
        : base(message, innerException)
    {
        LogPath = string.IsNullOrWhiteSpace(logPath) ? null : Path.GetFullPath(logPath);
        ExitCode = exitCode;
    }

    public string? LogPath { get; }
    public int? ExitCode { get; }
}

internal static class EnvironmentOperationDiagnostics
{
    private const int MaximumDisplayedLogCharacters = 6000;
    private const int MaximumDisplayedLogLines = 24;
    public static string OperationsLogPath =>
        Path.Combine(ComponentPaths.WorkRoot, "environment-operations.log");

    public static EnvironmentOperationException CreateScriptFailure(
        string operationName,
        int exitCode,
        string? logPath,
        Exception? innerException = null)
    {
        var normalizedLogPath = string.IsNullOrWhiteSpace(logPath) ? null : Path.GetFullPath(logPath);
        var logTail = ReadLogTail(normalizedLogPath);
        var message = $"{operationName}执行失败，退出码：{exitCode}。";
        if (!string.IsNullOrWhiteSpace(logTail))
        {
            message += $"{Environment.NewLine}{Environment.NewLine}最近原生日志：{Environment.NewLine}{logTail}";
        }

        if (!string.IsNullOrWhiteSpace(normalizedLogPath))
        {
            message += $"{Environment.NewLine}{Environment.NewLine}完整日志：{normalizedLogPath}";
        }

        return new EnvironmentOperationException(message, normalizedLogPath, innerException, exitCode);
    }

    public static string RecordFailure(
        string componentName,
        string operationName,
        Exception exception,
        string? preferredLogPath = null)
    {
        var logPath = string.IsNullOrWhiteSpace(preferredLogPath)
            ? OperationsLogPath
            : Path.GetFullPath(preferredLogPath);

        try
        {
            RollingLogWriter.Append(
                logPath,
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}] {componentName} / {operationName}{Environment.NewLine}{exception}{Environment.NewLine}",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

            return logPath;
        }
        catch
        {
            return string.Empty;
        }
    }

    public static string? FindAttachedLogPath(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is EnvironmentOperationException operationException &&
                !string.IsNullOrWhiteSpace(operationException.LogPath))
            {
                return operationException.LogPath;
            }
        }

        return null;
    }

    internal static string ReadLogTail(string? logPath)
    {
        if (string.IsNullOrWhiteSpace(logPath) || !File.Exists(logPath))
        {
            return string.Empty;
        }

        try
        {
            var text = TextFiles.ReadTailBestEffort(logPath!);
            if (string.IsNullOrWhiteSpace(text))
            {
                return string.Empty;
            }

            var lines = text
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .TakeLast(MaximumDisplayedLogLines)
                .ToArray();
            var tail = string.Join(Environment.NewLine, lines).Trim();
            return tail.Length <= MaximumDisplayedLogCharacters
                ? tail
                : tail.Substring(tail.Length - MaximumDisplayedLogCharacters);
        }
        catch
        {
            return string.Empty;
        }
    }

}
