using System.Configuration;
using System.IO;
using System.Xml.Linq;

namespace MCPanel;

internal static class AiSettingsStore
{
    internal static void Save(AiProviderSettings settings, string? configurationPath = null)
    {
        Validate(settings);
        var file = configurationPath ?? AppDomain.CurrentDomain.SetupInformation.ConfigurationFile;
        var document = File.Exists(file) ? XDocument.Load(file) : new XDocument(new XElement("configuration"));
        var root = document.Root ?? throw new InvalidDataException("配置文件缺少根节点。");
        var appSettings = root.Element("appSettings");
        if (appSettings is null) { appSettings = new XElement("appSettings"); root.Add(appSettings); }
        foreach (var pair in new Dictionary<string, string>
        {
            [AiProviderSettings.ProviderConfigKey] = settings.Provider.Trim(),
            [AiProviderSettings.EndpointConfigKey] = settings.Endpoint.Trim(),
            [AiProviderSettings.ModelConfigKey] = settings.Model.Trim(),
            [AiProviderSettings.ApiKeyConfigKey] = settings.ApiKey.Trim()
        })
        {
            var entry = appSettings.Elements("add").FirstOrDefault(item => (string?)item.Attribute("key") == pair.Key);
            if (entry is null) { entry = new XElement("add", new XAttribute("key", pair.Key)); appSettings.Add(entry); }
            entry.SetAttributeValue("value", pair.Value);
        }
        AtomicFile.WriteAllText(file, document.ToString());
        if (configurationPath is null) ConfigurationManager.RefreshSection("appSettings");
    }

    internal static Uri Validate(AiProviderSettings settings)
    {
        if (!Uri.TryCreate(settings.Endpoint.Trim(), UriKind.Absolute, out var endpoint) ||
            (endpoint.Scheme != Uri.UriSchemeHttps && !(endpoint.Scheme == Uri.UriSchemeHttp && endpoint.IsLoopback)))
            throw new ArgumentException("请输入完整的 HTTPS 接口地址，本机服务也支持 HTTP。");
        if (string.IsNullOrWhiteSpace(settings.Model)) throw new ArgumentException("请输入模型名称。");
        if (string.IsNullOrWhiteSpace(settings.ApiKey)) throw new ArgumentException("请输入 API Key。");
        return endpoint;
    }
}
