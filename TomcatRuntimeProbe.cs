using System.Globalization;
using System.IO;
using System.Net.NetworkInformation;
using System.Xml.Linq;

namespace MCPanel;

/// <summary>Reads configured Tomcat connectors and checks TCP stability for independent product starts.</summary>
internal static class TomcatRuntimeProbe
{
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

}
