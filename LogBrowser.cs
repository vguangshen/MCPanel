using System.IO;

namespace MCPanel;

/// <summary>The components whose logs the log page can show.</summary>
public enum LogModule
{
    Panel,
    Tomcat,
    Iis,
    Nginx,
    MySql,
    SqlServer,
    Frp
}

/// <summary>One log file of a module, newest first in <see cref="LogBrowser.ListFiles"/>.</summary>
public sealed record LogFileEntry(string Path, string Name, string Folder, long Length, DateTime LastWriteTime)
{
    public string Description => $"{Name}  ·  {LastWriteTime:yyyy-MM-dd HH:mm:ss}  ·  {FormatSize(Length)}";

    /// <summary>The file list and its selection box show the description.</summary>
    public override string ToString() => Description;

    private static string FormatSize(long bytes) =>
        bytes >= 1024 * 1024 ? $"{bytes / 1024d / 1024d:0.0} MB"
        : bytes >= 1024 ? $"{bytes / 1024d:0.0} KB"
        : $"{bytes} B";
}

/// <summary>
/// Finds each module's log files and reads the newest part of one. Files can
/// still be open for writing; reading never locks them and never throws for
/// a missing or locked file.
/// </summary>
public sealed class LogBrowser
{
    /// <summary>How much of the end of a log is shown (newest entries).</summary>
    public const int TailBytes = 256 * 1024;
    private const int MaximumFiles = 40;
    private static readonly string[] TextLogPatterns = ["*.log", "*.out", "*.txt"];

    private readonly string _applicationRoot;

    public LogBrowser() : this(ComponentPaths.ApplicationRoot) { }
    internal LogBrowser(string applicationRoot) => _applicationRoot = Path.GetFullPath(applicationRoot);

    public static IReadOnlyList<LogModule> Modules { get; } =
        [LogModule.Panel, LogModule.Tomcat, LogModule.Iis, LogModule.Nginx, LogModule.MySql, LogModule.SqlServer, LogModule.Frp];

    public static string DisplayName(LogModule module) => module switch
    {
        LogModule.Panel => "MCPanel 面板",
        LogModule.Tomcat => "Tomcat",
        LogModule.Iis => "IIS",
        LogModule.Nginx => "Nginx",
        LogModule.MySql => "MySQL",
        LogModule.SqlServer => "SQL Server",
        LogModule.Frp => "FRP",
        _ => module.ToString()
    };

    /// <summary>The module's log files, most recently written first.</summary>
    public IReadOnlyList<LogFileEntry> ListFiles(LogModule module) =>
        FindCandidates(module)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(TryDescribe)
            .Where(entry => entry is not null)
            .Select(entry => entry!)
            .OrderByDescending(entry => entry.LastWriteTime)
            .Take(MaximumFiles)
            .ToArray();

    /// <summary>The newest <see cref="TailBytes"/> of a log, decoded like the installer logs.</summary>
    public static string ReadLatest(string path)
    {
        try
        {
            return File.Exists(path) ? TextFiles.ReadTailBestEffort(path, TailBytes).TrimEnd() : string.Empty;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private IEnumerable<string> FindCandidates(LogModule module)
    {
        var runtimeTomcat = Path.Combine(ComponentPaths.RuntimeRoot, EnvironmentComponentCatalog.TomcatDirectoryName);
        var legacyTomcat = Path.Combine(ComponentPaths.LegacyRuntimeRoot, EnvironmentComponentCatalog.TomcatDirectoryName);
        var runtimeSqlServer = Path.Combine(ComponentPaths.RuntimeRoot, "MSSQL");
        var legacySqlServer = Path.Combine(ComponentPaths.LegacyRuntimeRoot, "MSSQL");
        string[] sqlServerPatterns = ["ERRORLOG", "ERRORLOG.*", "Summary.txt", "*.log"];

        return module switch
        {
            LogModule.Panel => Find(Path.Combine(_applicationRoot, "StoreData", "Work"), ["*.log"], SearchOption.TopDirectoryOnly)
                .Concat(Find(Path.Combine(_applicationRoot, "StoreData", "Logs"), ["*.log"])),
            LogModule.Tomcat => Find(ComponentPaths.TomcatRoot, TextLogPatterns, SearchOption.AllDirectories,
                    path => path.IndexOf("tomcat", StringComparison.OrdinalIgnoreCase) >= 0)
                .Concat(Find(runtimeTomcat, TextLogPatterns))
                .Concat(Find(legacyTomcat, TextLogPatterns)),
            LogModule.Iis => Find(
                    Path.Combine(Environment.GetEnvironmentVariable("SystemDrive") ?? "C:", "inetpub", "logs", "LogFiles"),
                    ["*.log"])
                .Concat(Find(ComponentPaths.WorkRoot, ["*iis*.log", "*iis*.txt"], SearchOption.TopDirectoryOnly)),
            LogModule.Nginx => ComponentPaths.NginxSearchRoots
                .SelectMany(root => Find(root, ["error.log", "access.log", "*nginx*.log", "mcpanel-service.log"], SearchOption.AllDirectories,
                    path => path.IndexOf("nginx", StringComparison.OrdinalIgnoreCase) >= 0)),
            LogModule.MySql => ComponentPaths.MySqlSearchRoots
                .SelectMany(root => Find(root, ["*.err", "*.log"], SearchOption.AllDirectories,
                    path => path.IndexOf("mysql", StringComparison.OrdinalIgnoreCase) >= 0)),
            LogModule.SqlServer => Find(ComponentPaths.SqlServerRoot, sqlServerPatterns)
                .Concat(Find(runtimeSqlServer, sqlServerPatterns))
                .Concat(Find(legacySqlServer, sqlServerPatterns))
                .Concat(Find(ComponentPaths.WorkRoot, ["*sqlserver*.log", "*sqlserver*.txt"], SearchOption.TopDirectoryOnly)),
            LogModule.Frp => EnvironmentComponentCatalog.FrpLogRoots(_applicationRoot)
                .SelectMany(root => Find(root, ["*frp*.log", "*frpc*.log", "*frp*.txt"])),
            _ => []
        };
    }

    private static IEnumerable<string> Find(
        string root,
        IReadOnlyList<string> patterns,
        SearchOption option = SearchOption.AllDirectories,
        Func<string, bool>? predicate = null)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
        {
            return [];
        }

        var results = new List<string>();
        foreach (var pattern in patterns)
        {
            try
            {
                results.AddRange(Directory.EnumerateFiles(root, pattern, option)
                    .Where(path => predicate?.Invoke(path) ?? true));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // A protected folder must not hide the logs found elsewhere.
            }
        }

        return results;
    }

    private static LogFileEntry? TryDescribe(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists
                ? new LogFileEntry(info.FullName, info.Name, info.DirectoryName ?? string.Empty, info.Length, info.LastWriteTime)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
