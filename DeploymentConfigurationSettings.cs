using System.IO;
using System.Xml.Linq;

namespace MCPanel;

internal static class DeploymentConfigurationSettings
{
    public static IReadOnlyDictionary<string, string> Capture(string configFile)
    {
        var settings = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(configFile)) return settings;
        var allowed = new HashSet<string>(DeploymentLayoutManifest.PreservedAppSettingKeys, StringComparer.OrdinalIgnoreCase);
        foreach (var element in XDocument.Load(configFile).Root?.Element("appSettings")?.Elements("add") ?? [])
        {
            var key = (string?)element.Attribute("key");
            if (key is not null && allowed.Contains(key) && element.Attribute("value") is not null)
                settings[key] = (string)element.Attribute("value")!;
        }
        return settings;
    }

    public static void Apply(string configFile, IReadOnlyDictionary<string, string> settings)
    {
        if (settings.Count == 0 || !File.Exists(configFile)) return;
        var document = XDocument.Load(configFile, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException("更新后的应用配置文件无效。");
        var appSettings = root.Element("appSettings");
        if (appSettings is null)
        {
            appSettings = new XElement("appSettings");
            root.Add(appSettings);
        }
        foreach (var setting in settings)
        {
            var element = appSettings.Elements("add").FirstOrDefault(element =>
                string.Equals((string?)element.Attribute("key"), setting.Key, StringComparison.OrdinalIgnoreCase));
            if (element is null)
            {
                element = new XElement("add", new XAttribute("key", setting.Key));
                appSettings.Add(element);
            }
            element.SetAttributeValue("value", EnvironmentDownloadCatalog.NormalizeUrl(setting.Key, setting.Value));
        }
        AtomicFile.WriteAllText(configFile, document.ToString());
    }
}
