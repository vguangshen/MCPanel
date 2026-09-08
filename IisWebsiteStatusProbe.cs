using System.IO;
using System.Xml.Linq;

namespace MCPanel;

internal sealed record IisWebsiteStatus(string Text, bool Running);

internal sealed class IisWebsiteStatusProbe
{
    private readonly Dictionary<string, string> _sites;
    private readonly Dictionary<string, string> _pools;
    private readonly bool _available;

    private IisWebsiteStatusProbe(Dictionary<string, string> sites, Dictionary<string, string> pools, bool available)
    {
        _sites = sites;
        _pools = pools;
        _available = available;
    }

    internal static IisWebsiteStatusProbe Read()
    {
        try
        {
            var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
            string Query(string kind)
            {
                var result = ProcessRunner.RunSynchronously(appcmd, $"list {kind} /xml", captureOutput: true,
                    timeout: TimeSpan.FromSeconds(5));
                if (result.ExitCode != 0) throw new IOException(result.CombinedOutput);
                return result.StandardOutput;
            }
            return Parse(Query("site"), Query("apppool"));
        }
        catch
        {
            // No elevation prompt for passive UI refresh. Access denied is unknown, never running.
            return new(new(StringComparer.OrdinalIgnoreCase), new(StringComparer.OrdinalIgnoreCase), false);
        }
    }

    internal static IisWebsiteStatusProbe Parse(string sites, string pools) =>
        new(ParseStates(sites, "SITE", "SITE.NAME"), ParseStates(pools, "APPPOOL", "APPPOOL.NAME"), true);

    private static Dictionary<string, string> ParseStates(string xml, string element, string nameAttribute)
    {
        var document = XDocument.Parse(xml);
        if (document.Root?.Name != "appcmd" || document.Descendants("ERROR").Any())
            throw new InvalidDataException("IIS 状态查询未返回有效结果。");
        return document.Descendants(element).ToDictionary(
            node => (string?)node.Attribute(nameAttribute) ?? throw new InvalidDataException("IIS 状态缺少名称。"),
            node => (string?)node.Attribute("state") ?? string.Empty, StringComparer.OrdinalIgnoreCase);
    }

    internal IisWebsiteStatus Get(CustomWebsiteDefinition definition)
    {
        if (!_available) return new("状态未知（无法查询 IIS）", false);
        if (!_sites.TryGetValue(definition.Name, out var site)) return new("IIS 网站不存在", false);
        if (!_pools.TryGetValue(definition.ApplicationPoolName, out var pool)) return new("应用程序池不存在", false);
        if (site.Equals("Stopped", StringComparison.OrdinalIgnoreCase)) return new("网站已停止", false);
        if (pool.Equals("Stopped", StringComparison.OrdinalIgnoreCase)) return new("应用程序池已停止", false);
        return site.Equals("Started", StringComparison.OrdinalIgnoreCase) && pool.Equals("Started", StringComparison.OrdinalIgnoreCase)
            ? new("运行中", true) : new("IIS 状态尚未就绪", false);
    }
}
