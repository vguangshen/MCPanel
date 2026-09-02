using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Xml.Linq;

namespace MCPanel;

/// <summary>
/// Shared Tomcat startup validation.  A successful batch-file launch is not
/// enough: Java may exit a moment later because of a bad JVM option, a broken
/// deployment, or a port conflict.
/// </summary>
internal static class TomcatRuntimeProbe
{
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromMinutes(3);

    public static IReadOnlyList<int> ReadHttpPorts(string tomcatRoot)
    {
        var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
        try
        {
            if (!File.Exists(serverXml))
            {
                return Array.Empty<int>();
            }

            var document = XDocument.Load(serverXml);
            return document.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Connector", StringComparison.OrdinalIgnoreCase))
                .Where(element => !string.Equals(element.Attribute("protocol")?.Value, "AJP/1.3", StringComparison.OrdinalIgnoreCase))
                .Select(element => int.TryParse(
                    element.Attribute("port")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var port) ? port : -1)
                .Where(port => port > 0 && port <= 65535)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public static async Task WaitForStartupAsync(
        string tomcatRoot,
        IReadOnlyCollection<int> ports,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null)
    {
        if (ports.Count == 0)
        {
            throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。");
        }

        var deadline = DateTime.UtcNow + (timeout ?? DefaultStartupTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (ArePortsListening(ports))
            {
                await EnsureStableAsync(ports, TimeSpan.FromSeconds(1), cancellationToken);
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        var recentLog = ReadRecentLog(tomcatRoot);
        var message = $"Tomcat 启动失败，端口 {string.Join(", ", ports)} 未在限定时间内稳定监听。请检查 {Path.Combine(tomcatRoot, "logs")}。";
        if (!string.IsNullOrWhiteSpace(recentLog))
        {
            message += Environment.NewLine + Environment.NewLine + "最近的 Tomcat 日志：" + Environment.NewLine + recentLog;
        }

        throw new TimeoutException(message);
    }

    public static async Task EnsureStableAsync(
        IReadOnlyCollection<int> ports,
        TimeSpan stabilityDelay,
        CancellationToken cancellationToken)
    {
        if (!ArePortsListening(ports))
        {
            throw new InvalidOperationException($"Tomcat 端口 {string.Join(", ", ports)} 未监听，Java 进程可能已退出。");
        }

        await Task.Delay(stabilityDelay, cancellationToken);
        if (!ArePortsListening(ports))
        {
            throw new InvalidOperationException($"Tomcat 启动后很快退出，端口 {string.Join(", ", ports)} 已停止监听。");
        }
    }

    public static bool ArePortsListening(IEnumerable<int> ports)
    {
        var expected = ports
            .Where(port => port > 0 && port <= 65535)
            .Distinct()
            .ToArray();
        if (expected.Length == 0)
        {
            return false;
        }

        var active = GetActiveTcpPorts();
        return expected.All(active.Contains);
    }

    public static bool IsPortListening(int port)
    {
        return GetActiveTcpPorts().Contains(port);
    }

    private static HashSet<int> GetActiveTcpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    private static string ReadRecentLog(string tomcatRoot)
    {
        try
        {
            var logs = Path.Combine(tomcatRoot, "logs");
            var file = Directory.EnumerateFiles(logs, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".out", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null)
            {
                return string.Empty;
            }

            var lines = new Queue<string>();
            foreach (var line in File.ReadLines(file))
            {
                lines.Enqueue(line);
                if (lines.Count > 60)
                {
                    lines.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }
}
