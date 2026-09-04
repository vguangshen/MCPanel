using System.IO;
using System.Text.Json;

namespace MCPanel;

internal static class DeploymentLayoutManifest
{
    private const string ManifestFileName = "deployment-layout.json";
    private static readonly Lazy<IReadOnlyCollection<string>> PreservedNames =
        new(LoadPreservedTopLevelNames, LazyThreadSafetyMode.ExecutionAndPublication);

    // This fallback keeps older installations safe when the manifest was not
    // shipped by a previous release. New packages always carry the JSON file.
    private static readonly string[] FallbackNames =
    [
        "StoreData", "AccountApi", "Runtime", "Downloads", "Tools", "web", "Cache", "Frp",
        "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite",
        "config.ini", "config.ini.previous", "device.identity", "database.config",
        "database.config.previous", "logs"
    ];

    internal static IReadOnlyCollection<string> PreservedTopLevelNames => PreservedNames.Value;

    internal static bool IsPreservedTopLevelName(string name) =>
        PreservedNames.Value.Contains(name);

    private static IReadOnlyCollection<string> LoadPreservedTopLevelNames()
    {
        try
        {
            var path = Path.Combine(ComponentPaths.ApplicationRoot, ManifestFileName);
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
            return names is { Length: > 0 }
                ? new HashSet<string>(names, StringComparer.OrdinalIgnoreCase)
                : CreateFallback();
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
    }
}
