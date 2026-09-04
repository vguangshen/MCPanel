using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MCPanel;

internal sealed record ProductConfigurationResult(bool Changed, string Message)
{
    public static ProductConfigurationResult None { get; } = new(false, string.Empty);
}

internal sealed record ProductConfigurationRefreshResult(int Updated, int Failed)
{
    public string Message => Failed == 0
        ? Updated == 0 ? "未发现需要同步的 Java 产品配置" : $"已同步 {Updated} 个 Java 产品的数据库连接配置"
        : $"已同步 {Updated} 个 Java 产品配置，另有 {Failed} 个同步失败，请查看 product-config-refresh.log";
}

/// <summary>
/// Re-applies the vendor product identity and database connection values after
/// a package/SVN update.  Supplier updates intentionally revert local files,
/// so this step must run after the product files have been prepared.
/// </summary>
internal static class ProductConfigurationService
{
    private static readonly string[] JavaKeys =
    [
        "global.system.VersionName",
        "global.system.VersionID",
        "global.system.MySQLambient",
        "global.system.MySQLPort",
        "global.system.MySQLUserName",
        "global.system.MySQLPassword"
    ];

    public static ProductConfigurationResult Apply(
        ProductItem product,
        string productRoot,
        string preparedPath,
        bool isTomcatProduct)
    {
        var changedFiles = new List<string>();
        if (TryApplyXmlIdentity(productRoot, product.ProductId, out var xmlFile))
        {
            changedFiles.Add(Path.GetFileName(xmlFile));
        }

        if (isTomcatProduct)
        {
            var credentials = MySqlCredentialStore.Load();
            var ambient = DetectMySqlAmbient(product.SqlEnvironment);
            var ymlFile = ApplyJavaConfiguration(
                productRoot,
                preparedPath,
                product.ProductId,
                product.Level,
                ambient,
                credentials);
            changedFiles.Add(Path.GetFileName(ymlFile));
        }

        if (changedFiles.Count == 0)
        {
            return ProductConfigurationResult.None;
        }

        return new ProductConfigurationResult(
            true,
            $"已写入产品版本及数据库配置（{string.Join("、", changedFiles.Distinct(StringComparer.OrdinalIgnoreCase))}）");
    }

    internal static IReadOnlyList<string> GetConfigurationFilesForBackup(
        string productRoot,
        string preparedPath)
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in new[] { "config.xml", "systemConfig.yml" })
        {
            foreach (var file in EnumerateNamedFiles(productRoot, name))
            {
                files.Add(file);
            }
        }

        files.Add(Path.Combine(ResolveJavaConfigurationRoot(productRoot, preparedPath), "systemConfig.yml"));
        return files.ToArray();
    }

    internal static bool TryApplyXmlIdentity(string productRoot, string productId, out string configFile)
    {
        configFile = FindXmlConfiguration(productRoot) ?? string.Empty;
        if (configFile.Length == 0)
        {
            return false;
        }

        var document = XDocument.Load(configFile, LoadOptions.PreserveWhitespace);
        var root = document.Root ?? throw new InvalidDataException($"产品配置缺少根节点：{configFile}");
        var systemSoft = root.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("SystemSoft", StringComparison.OrdinalIgnoreCase));
        if (systemSoft is null)
        {
            systemSoft = new XElement(root.GetDefaultNamespace() + "SystemSoft");
            root.Add(systemSoft);
        }

        var versionId = systemSoft.Elements().FirstOrDefault(element =>
            element.Name.LocalName.Equals("SoftVersionID", StringComparison.OrdinalIgnoreCase));
        if (versionId is null)
        {
            versionId = new XElement(systemSoft.GetDefaultNamespace() + "SoftVersionID");
            systemSoft.Add(versionId);
        }

        versionId.Value = SanitizeValue(productId);
        AtomicFile.WriteAllText(configFile, SerializeXml(document), new UTF8Encoding(false));
        return true;
    }

    internal static string ApplyJavaConfiguration(
        string productRoot,
        string preparedPath,
        string productId,
        string versionName,
        string mysqlAmbient,
        MySqlDefaultCredentials credentials)
    {
        var configurationFile = FindNamedFile(productRoot, "systemConfig.yml")
            ?? Path.Combine(ResolveJavaConfigurationRoot(productRoot, preparedPath), "systemConfig.yml");
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["global.system.VersionName"] = SanitizeValue(versionName),
            ["global.system.VersionID"] = SanitizeValue(productId),
            ["global.system.MySQLambient"] = SanitizeValue(mysqlAmbient),
            ["global.system.MySQLPort"] = credentials.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["global.system.MySQLUserName"] = SanitizeValue(credentials.UserName),
            ["global.system.MySQLPassword"] = SanitizeSecretValue(credentials.Password)
        };

        var lines = File.Exists(configurationFile)
            ? File.ReadAllLines(configurationFile, Encoding.UTF8).ToList()
            : new List<string>();
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        for (var index = 0; index < lines.Count; index++)
        {
            var match = Regex.Match(lines[index], @"^\s*(?<key>global\.system\.[A-Za-z0-9]+)\s*=", RegexOptions.IgnoreCase);
            if (!match.Success || !values.TryGetValue(match.Groups["key"].Value, out var value))
            {
                continue;
            }

            var key = JavaKeys.First(candidate => candidate.Equals(match.Groups["key"].Value, StringComparison.OrdinalIgnoreCase));
            if (updated.Contains(key))
            {
                lines.RemoveAt(index--);
                continue;
            }
            lines[index] = $"{key}={value}";
            updated.Add(key);
        }

        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]))
        {
            lines.Add(string.Empty);
        }

        foreach (var key in JavaKeys.Where(key => !updated.Contains(key)))
        {
            lines.Add($"{key}={values[key]}");
        }

        var contents = JoinConfigurationLines(lines);
        AtomicFile.WriteAllText(configurationFile, contents, new UTF8Encoding(false));
        return configurationFile;
    }

    internal static string DetectMySqlAmbient(string? declaredEnvironment)
    {
        var executable = new ComponentLocator().FindMySqlExecutable();
        if (executable is not null)
        {
            try
            {
                var version = FileVersionInfo.GetVersionInfo(executable);
                if (version.FileMajorPart > 0)
                {
                    return $"MySQL{version.FileMajorPart}.{Math.Max(0, version.FileMinorPart)}";
                }
            }
            catch
            {
                // Fall back to product metadata below.
            }
        }

        var declared = SanitizeValue(declaredEnvironment ?? string.Empty);
        var match = Regex.Match(declared, @"mysql\s*(?<version>\d+(?:\.\d+)?)", RegexOptions.IgnoreCase);
        return match.Success ? $"MySQL{match.Groups["version"].Value}" : "MySQL5.6";
    }

    public static ProductConfigurationRefreshResult RefreshInstalledMySqlConnections()
    {
        var credentials = MySqlCredentialStore.Load();
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["global.system.MySQLambient"] = DetectMySqlAmbient(null),
            ["global.system.MySQLPort"] = credentials.Port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["global.system.MySQLUserName"] = SanitizeValue(credentials.UserName),
            ["global.system.MySQLPassword"] = SanitizeSecretValue(credentials.Password)
        };
        var updated = 0;
        var failed = 0;
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var deployment in ProductDeploymentService.LoadTomcatDeploymentInfos())
        {
            var productRoot = Path.Combine(ComponentPaths.WebRoot, SafeName(deployment.ProductId));
            var file = FindNamedFile(productRoot, "systemConfig.yml");
            if (file is null)
            {
                var appRoot = Directory.Exists(deployment.PhysicalPath)
                    ? deployment.PhysicalPath
                    : Path.GetDirectoryName(deployment.PhysicalPath);
                if (!string.IsNullOrWhiteSpace(appRoot)) file = FindNamedFile(appRoot!, "systemConfig.yml");
            }

            if (file is null || !files.Add(file)) continue;
            try
            {
                UpdateJavaValues(file, values);
                updated++;
            }
            catch (Exception ex)
            {
                failed++;
                WriteRefreshLog(deployment.ProductId, file, ex);
            }
        }

        return new ProductConfigurationRefreshResult(updated, failed);
    }

    private static string? FindXmlConfiguration(string root)
    {
        var direct = Path.Combine(root, "config.xml");
        if (File.Exists(direct))
        {
            return direct;
        }

        foreach (var candidate in EnumerateNamedFiles(root, "config.xml"))
        {
            try
            {
                var document = XDocument.Load(candidate, LoadOptions.None);
                if (document.Descendants().Any(element =>
                        element.Name.LocalName.Equals("SystemSoft", StringComparison.OrdinalIgnoreCase) ||
                        element.Name.LocalName.Equals("SoftVersionID", StringComparison.OrdinalIgnoreCase)))
                {
                    return candidate;
                }
            }
            catch
            {
                // A different module may also own a file named config.xml.
            }
        }

        return null;
    }

    private static string? FindNamedFile(string root, string name) =>
        EnumerateNamedFiles(root, name).FirstOrDefault();

    private static IEnumerable<string> EnumerateNamedFiles(string root, string name)
    {
        if (!Directory.Exists(root))
        {
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories)
                .Where(path => !PathHasDirectory(path, ".svn"))
                .OrderBy(path => path.Count(ch => ch == Path.DirectorySeparatorChar))
                .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveJavaConfigurationRoot(string productRoot, string preparedPath)
    {
        if (Directory.Exists(preparedPath))
        {
            return preparedPath;
        }

        var parent = Path.GetDirectoryName(preparedPath);
        return !string.IsNullOrWhiteSpace(parent) && Directory.Exists(parent) ? parent! : productRoot;
    }

    private static void UpdateJavaValues(string file, IReadOnlyDictionary<string, string> values)
    {
        var lines = File.ReadAllLines(file, Encoding.UTF8).ToList();
        var updated = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < lines.Count; index++)
        {
            var match = Regex.Match(lines[index], @"^\s*(?<key>global\.system\.[A-Za-z0-9]+)\s*=", RegexOptions.IgnoreCase);
            if (!match.Success || !values.TryGetValue(match.Groups["key"].Value, out var value)) continue;
            var key = values.Keys.First(candidate => candidate.Equals(match.Groups["key"].Value, StringComparison.OrdinalIgnoreCase));
            if (updated.Contains(key))
            {
                lines.RemoveAt(index--);
                continue;
            }
            lines[index] = $"{key}={value}";
            updated.Add(key);
        }

        if (lines.Count > 0 && !string.IsNullOrWhiteSpace(lines[lines.Count - 1]) && values.Keys.Any(key => !updated.Contains(key)))
            lines.Add(string.Empty);
        foreach (var key in values.Keys.Where(key => !updated.Contains(key))) lines.Add($"{key}={values[key]}");
        AtomicFile.WriteAllText(file, JoinConfigurationLines(lines), new UTF8Encoding(false));
    }

    private static string JoinConfigurationLines(List<string> lines)
    {
        while (lines.Count > 0 && lines[lines.Count - 1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static void WriteRefreshLog(string productId, string file, Exception exception)
    {
        try
        {
            var directory = ComponentPaths.WorkRoot;
            RollingLogWriter.Append(Path.Combine(directory, "product-config-refresh.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {productId} / {file}{Environment.NewLine}{exception}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch { }
    }

    private static bool PathHasDirectory(string path, string name) =>
        path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(part => part.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string SerializeXml(XDocument document)
    {
        var builder = new StringBuilder();
        var settings = new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(false),
            Indent = false,
            OmitXmlDeclaration = document.Declaration is null,
            NewLineHandling = NewLineHandling.None
        };
        using var textWriter = new Utf8StringWriter(builder);
        using var writer = XmlWriter.Create(textWriter, settings);
        document.Save(writer);
        writer.Flush();
        return builder.ToString();
    }

    private static string SanitizeValue(string value) =>
        (value ?? string.Empty)
        .Replace("\r", " ", StringComparison.Ordinal)
        .Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\t", " ", StringComparison.Ordinal)
        .Trim();

    private static string SanitizeSecretValue(string value)
    {
        if ((value ?? string.Empty).Any(char.IsControl))
        {
            throw new InvalidDataException("数据库密码包含无法写入产品配置的控制字符。");
        }

        return value ?? string.Empty;
    }

    private static string SafeName(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray()).Trim('_');
        return safe.Length == 0 ? "product" : safe;
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public Utf8StringWriter(StringBuilder builder) : base(builder) { }
        public override Encoding Encoding => new UTF8Encoding(false);
    }
}
