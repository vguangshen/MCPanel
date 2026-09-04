using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public enum AiLogTarget
{
    Tomcat,
    Iis,
    Nginx,
    MySql,
    SqlServer,
    Frp
}

public sealed record LogCollectionResult(
    string ComponentName,
    string Summary,
    string Content,
    IReadOnlyList<string> Files);

public sealed class LogCollectorService
{
    private const int MaxFileBytes = 48 * 1024;
    private const int MaxTotalCharacters = 160_000;
    private static readonly Regex PasswordRegex = new(@"(?i)(password|pwd|passwd|token|secret)\s*[:=]\s*[^\s,;]+", RegexOptions.Compiled);
    private static readonly Regex AuthorizationRegex = new(@"(?i)(authorization\s*:\s*bearer\s+)[A-Za-z0-9._-]+", RegexOptions.Compiled);
    private static readonly Regex ApiKeyRegex = new(@"\b[a-f0-9]{32}\.[A-Za-z0-9_-]{12,}\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public Task<LogCollectionResult> CollectAsync(
        AiLogTarget target,
        CancellationToken cancellationToken = default)
    {
        return Task.Run(() => Collect(target, cancellationToken), cancellationToken);
    }

    private static LogCollectionResult Collect(AiLogTarget target, CancellationToken cancellationToken)
    {
        var candidates = FindCandidates(target)
            .Where(File.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileInfo(path))
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .Take(12)
            .ToList();

        var builder = new StringBuilder();
        var files = new List<string>();
        foreach (var file in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var text = ReadTail(file.FullName, MaxFileBytes);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            text = RedactSecrets(text);
            var remaining = MaxTotalCharacters - builder.Length;
            if (remaining <= 0)
            {
                break;
            }

            if (text.Length > remaining)
            {
                text = text.Substring(text.Length - remaining);
            }

            files.Add(file.FullName);
            builder.AppendLine($"===== {file.FullName} | {file.LastWriteTime:yyyy-MM-dd HH:mm:ss} =====");
            builder.AppendLine(text);
            builder.AppendLine();
        }

        var name = DisplayName(target);
        var summary = files.Count == 0
            ? $"未找到 {name} 的可读日志。"
            : $"已采集 {files.Count} 个最近日志文件，共 {builder.Length:N0} 个字符。";
        return new LogCollectionResult(name, summary, builder.ToString(), files);
    }

    private static IEnumerable<string> FindCandidates(AiLogTarget target)
    {
        var storeData = ComponentPaths.StoreDataRoot;
        var work = ComponentPaths.WorkRoot;
        var runtimeTomcat = Path.Combine(ComponentPaths.RuntimeRoot, "apache-tomcat-8.5.57");
        var runtimeNginx = Path.Combine(ComponentPaths.RuntimeRoot, NginxRuntimeManager.VersionDirectoryName);
        var runtimeMySql = ComponentPaths.RuntimeMySqlRoot;
        var runtimeSqlServer = Path.Combine(ComponentPaths.RuntimeRoot, "MSSQL");
        var legacyTomcat = Path.Combine(ComponentPaths.LegacyRuntimeRoot, "apache-tomcat-8.5.57");
        var legacyNginx = Path.Combine(ComponentPaths.LegacyRuntimeRoot, NginxRuntimeManager.VersionDirectoryName);
        var legacyMySql = ComponentPaths.LegacyMySqlRoot;
        var legacySqlServer = Path.Combine(ComponentPaths.LegacyRuntimeRoot, "MSSQL");

        return target switch
        {
            AiLogTarget.Tomcat => SafeFind(ComponentPaths.TomcatRoot, ["*.log", "*.out", "*.txt"],
                    path => path.Contains("tomcat", StringComparison.OrdinalIgnoreCase))
                .Concat(SafeFind(runtimeTomcat, ["*.log", "*.out", "*.txt"]))
                .Concat(SafeFind(legacyTomcat, ["*.log", "*.out", "*.txt"])),
            AiLogTarget.Iis => SafeFind(
                    Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "inetpub", "logs", "LogFiles"),
                    ["*.log"])
                .Concat(SafeFind(work, ["*iis*.log", "*iis*.txt"])),
            AiLogTarget.Nginx => ComponentPaths.NginxSearchRoots
                .SelectMany(root => SafeFind(root, ["error.log", "access.log", "*nginx*.log"],
                    path => path.Contains("nginx", StringComparison.OrdinalIgnoreCase))),
            AiLogTarget.MySql => ComponentPaths.MySqlSearchRoots
                .SelectMany(root => SafeFind(root, ["*.err", "*.log"],
                    path => path.Contains("mysql", StringComparison.OrdinalIgnoreCase))),
            AiLogTarget.SqlServer => SafeFind(ComponentPaths.SqlServerRoot, ["ERRORLOG", "ERRORLOG.*", "Summary.txt", "*.log"],
                    path => path.Contains("mssql", StringComparison.OrdinalIgnoreCase) ||
                            path.Contains("sql", StringComparison.OrdinalIgnoreCase))
                .Concat(SafeFind(runtimeSqlServer, ["ERRORLOG", "ERRORLOG.*", "Summary.txt", "*.log"]))
                .Concat(SafeFind(legacySqlServer, ["ERRORLOG", "ERRORLOG.*", "Summary.txt", "*.log"]))
                .Concat(SafeFind(work, ["*sqlserver*.log", "*sqlserver*.txt", "*sql*.log"])),
            AiLogTarget.Frp => SafeFind(storeData, ["*frp*.log", "*frpc*.log", "*frp*.txt"]),
            _ => []
        };
    }

    private static IEnumerable<string> SafeFind(
        string root,
        IReadOnlyList<string> patterns,
        Func<string, bool>? predicate = null)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var pattern in patterns)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
                    .Where(path => predicate?.Invoke(path) ?? true));
            }
            catch
            {
                // A locked log directory should not block collection from the remaining sources.
            }
        }

        return results;
    }

    private static string ReadTail(string path, int maxBytes)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, maxBytes);
            stream.Seek(-length, SeekOrigin.End);
            var buffer = new byte[length];
            var read = stream.Read(buffer, 0, length);
            return DecodeBestEffort(buffer.AsSpan(0, read).ToArray()).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DecodeBestEffort(byte[] bytes)
    {
        var utf8 = new UTF8Encoding(false, true);
        try
        {
            return utf8.GetString(bytes);
        }
        catch
        {
            try
            {
                return Encoding.GetEncoding(936).GetString(bytes);
            }
            catch
            {
                return Encoding.Default.GetString(bytes);
            }
        }
    }

    private static string RedactSecrets(string value)
    {
        var redacted = PasswordRegex.Replace(value, "$1***");
        redacted = AuthorizationRegex.Replace(redacted, "$1***");
        return ApiKeyRegex.Replace(redacted, "***");
    }

    public static string DisplayName(AiLogTarget target) => target switch
    {
        AiLogTarget.Tomcat => "Tomcat",
        AiLogTarget.Iis => "IIS",
        AiLogTarget.Nginx => "Nginx",
        AiLogTarget.MySql => "MySQL",
        AiLogTarget.SqlServer => "SQL Server",
        AiLogTarget.Frp => "FRP",
        _ => target.ToString()
    };

}
