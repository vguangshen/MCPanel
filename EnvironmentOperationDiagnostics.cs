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
        Path.Combine(AppContext.BaseDirectory, "StoreData", "Work", "environment-operations.log");

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
            var text = ReadTextBestEffort(logPath!);
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

    private static string ReadTextBestEffort(string path)
    {
        const int maximumBytes = 512 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = stream.Length > maximumBytes ? stream.Length - maximumBytes : 0;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maximumBytes, stream.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }

        var bytes = read == buffer.Length ? buffer : buffer.Take(read).ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
        {
            return Encoding.Unicode.GetString(bytes);
        }

        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
        {
            return Encoding.BigEndianUnicode.GetString(bytes);
        }

        return Encoding.UTF8.GetString(bytes);
    }
}
