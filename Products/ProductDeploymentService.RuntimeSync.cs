using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed partial class ProductDeploymentService
{
    private static readonly object RuntimeBindingReconcileGate = new();
    private static DateTime RuntimeBindingReconcileNextUtc = DateTime.MinValue;
    private static readonly TimeSpan RuntimeBindingReconcileCache = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reconciles MCPanel's deployment-state cache with the real IIS/Tomcat
    /// configuration. The runtime configuration is authoritative; state JSON is
    /// only a cache used by UI and routing code.
    /// </summary>
    public static IReadOnlyCollection<string> ReconcileRuntimeDeploymentState(bool force = false)
    {
        lock (RuntimeBindingReconcileGate)
        {
            var now = DateTime.UtcNow;
            if (!force && now < RuntimeBindingReconcileNextUtc)
            {
                return Array.Empty<string>();
            }

            RuntimeBindingReconcileNextUtc = now + RuntimeBindingReconcileCache;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ReconcileIisDeploymentState(changed);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog("回读 IIS 实际绑定失败，保留现有缓存并等待下次同步。", ex);
            }

            try
            {
                ReconcileTomcatDeploymentState(changed);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog("回读 Tomcat 实际端口失败，保留现有缓存并等待下次同步。", ex);
            }

            return changed.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static void ReconcileIisDeploymentState(HashSet<string> changed)
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory)) return;

        var appcmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appcmd)) return;

        var portsBySite = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".tomcat.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var info = JsonSerializer.Deserialize<IisProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is null || !Directory.Exists(info.PhysicalPath) || string.IsNullOrWhiteSpace(info.SiteName))
                {
                    continue;
                }

                if (!portsBySite.TryGetValue(info.SiteName, out var actualPort))
                {
                    actualPort = TryReadIisSiteHttpPort(appcmd, info.SiteName);
                    portsBySite[info.SiteName] = actualPort;
                }

                if (!actualPort.HasValue || !IsValidObservedRuntimePort(actualPort.Value) || actualPort.Value == info.Port)
                {
                    continue;
                }

                var updated = info with
                {
                    Port = actualPort.Value,
                    Url = $"http://localhost:{actualPort.Value}{info.ApplicationPath.TrimEnd('/')}/"
                };
                AtomicFile.WriteAllText(
                    stateFile,
                    JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                changed.Add(updated.ProductId);
                WriteRuntimeBindingSyncLog(
                    $"检测到 IIS 外部端口变更：{updated.ProductId} {info.Port} -> {updated.Port}，已同步状态缓存。",
                    null);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog($"同步 IIS 状态文件失败：{stateFile}", ex);
            }
        }
    }

    private static void ReconcileTomcatDeploymentState(HashSet<string> changed)
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory)) return;

        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.tomcat.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is null || (!Directory.Exists(info.PhysicalPath) && !File.Exists(info.PhysicalPath)))
                {
                    continue;
                }

                var actual = ResolveTomcatRuntimeInfo(info);
                if (!IsValidObservedRuntimePort(actual.Port) ||
                    (actual.Port == info.Port && string.Equals(actual.Url, info.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AtomicFile.WriteAllText(
                    stateFile,
                    JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                changed.Add(actual.ProductId);
                WriteRuntimeBindingSyncLog(
                    $"检测到 Tomcat 外部端口变更：{actual.ProductId} {info.Port} -> {actual.Port}，已同步状态缓存。",
                    null);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog($"同步 Tomcat 状态文件失败：{stateFile}", ex);
            }
        }
    }

    private static int? TryReadIisSiteHttpPort(string appcmd, string siteName)
    {
        try
        {
            var result = ProcessRunner.RunSynchronously(
                appcmd,
                $"list site {Compat.QuoteCommandLineArgument(siteName)} /text:bindings",
                Path.GetDirectoryName(appcmd),
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3));
            return result.ExitCode == 0 ? ParseIisHttpBindingPort(result.StandardOutput) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static int? ParseIisHttpBindingPort(string? bindings)
    {
        if (string.IsNullOrWhiteSpace(bindings)) return null;
        var matches = Regex.Matches(
            bindings,
            @"(?i)(?:^|,)\s*http/[^,]*?:(?<port>\d+):[^,]*");
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["port"].Value, out var port) && IsValidObservedRuntimePort(port))
            {
                return port;
            }
        }

        return null;
    }

    private static void WriteRuntimeBindingSyncLog(string message, Exception? exception)
    {
        try
        {
            var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            RollingLogWriter.Append(
                Path.Combine(ComponentPaths.WorkRoot, "runtime-binding-sync.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{detail}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Passive reconciliation must never interrupt normal panel work.
        }
    }
}