using System.IO;
using System.Text.Json;

namespace MCPanel;

internal static class DeploymentLayoutManifest
{
    private const string ManifestFileName = "deployment-layout.json";
    private static readonly Lazy<IReadOnlyCollection<string>> PreservedNames =
        new(() => LoadPreservedTopLevelNames(ComponentPaths.ApplicationRoot), LazyThreadSafetyMode.ExecutionAndPublication);
    private static readonly Lazy<IReadOnlyCollection<string>> PreservedSettings =
        new(LoadPreservedAppSettingKeys, LazyThreadSafetyMode.ExecutionAndPublication);

    // This fallback keeps older installations safe when the manifest was not
    // shipped by a previous release. New packages always carry the JSON file.
    private static readonly string[] FallbackNames =
    [
        "StoreData", "AccountApi", "Runtime", "Downloads", "Diagnostics", "Tools", "web", "Cache", "Frp",
        "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite",
        "config.ini", "config.ini.previous", "device.identity", "database.config",
        "database.config.previous", "logs"
    ];

    internal static IReadOnlyCollection<string> PreservedTopLevelNames => PreservedNames.Value;
    internal static IReadOnlyCollection<string> PreservedAppSettingKeys => PreservedSettings.Value;

    private static IReadOnlyCollection<string> LoadPreservedAppSettingKeys()
    {
        var fallback = new HashSet<string>(EnvironmentDownloadSettings.ConfigKeys.Concat(AiProviderSettings.ConfigKeys), StringComparer.OrdinalIgnoreCase)
        { "UpdateManifestUrl", ApplicationUpdateService.GitHubUpdateRepositoryConfigKey };
        try
        {
            var path = Path.Combine(ComponentPaths.ApplicationRoot, ManifestFileName);
            if (!File.Exists(path)) return fallback;
            var document = JsonSerializer.Deserialize<DeploymentLayoutDocument>(File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            foreach (var key in document?.PreservedAppSettingKeys ?? [])
                if (!string.IsNullOrWhiteSpace(key)) fallback.Add(key.Trim());
        }
        catch { /* Older manifests still retain the built-in deployment settings. */ }
        return fallback;
    }

    internal static IReadOnlyCollection<string> LoadPreservedTopLevelNames(string applicationRoot)
    {
        try
        {
            var path = Path.Combine(applicationRoot, ManifestFileName);
            if (!File.Exists(path))
            {
                return CreateFallback();
            }

            var document = JsonSerializer.Deserialize<DeploymentLayoutDocument>(
                File.ReadAllText(path),
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            var names = document?.PreservedTopLevelNames?
                .Where(IsValidTopLevelName)
                .Select(name => name.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            // A helper can carry a manifest from an older installation. Known
            // operational data must stay protected even if that manifest has
            // not yet learned a newer preserved directory such as Diagnostics.
            var preserved = new HashSet<string>(FallbackNames, StringComparer.OrdinalIgnoreCase);
            if (names is not null) preserved.UnionWith(names);
            return preserved;
        }
        catch
        {
            return CreateFallback();
        }
    }

    private static bool IsValidTopLevelName(string? value)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return false;
        }

        if (normalized is "." or "..")
        {
            return false;
        }

        return normalized!.IndexOfAny(['\\', '/']) < 0;
    }

    private static IReadOnlyCollection<string> CreateFallback() =>
        new HashSet<string>(FallbackNames, StringComparer.OrdinalIgnoreCase);

    private sealed class DeploymentLayoutDocument
    {
        public List<string> PreservedTopLevelNames { get; set; } = [];
        public List<string> PreservedAppSettingKeys { get; set; } = [];
    }
}
