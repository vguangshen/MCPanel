using System.Configuration;
using System.Collections.Specialized;

namespace MCPanel;

public sealed record EnvironmentDownloadSettings(
    string IisUrlRewriteUrl,
    string NginxPackageUrl,
    string MySqlPackageUrl,
    string SqlServer2025ExpressUrl,
    string SqlServer2025EnterpriseDeveloperUrl,
    string SqlServer2022ExpressUrl,
    string SqlServer2017ExpressUrl,
    string SqlServer2012ExpressX64Url,
    string SqlServer2012ExpressX86Url,
    string SqlServer2008ExpressX64Url,
    string SqlServer2008ExpressX86Url,
    string TomcatPackageUrl,
    string FrpPackageUrl)
{
    // Keep these aliases for callers and older integrations. The catalog above
    // is the only place where keys and built-in URLs are defined.
    public const string IisUrlRewriteKey = EnvironmentDownloadCatalog.IisUrlRewriteKey;
    public const string NginxPackageKey = EnvironmentDownloadCatalog.NginxPackageKey;
    public const string MySqlPackageKey = EnvironmentDownloadCatalog.MySqlPackageKey;
    public const string SqlServer2025ExpressKey = EnvironmentDownloadCatalog.SqlServer2025ExpressKey;
    public const string SqlServer2025EnterpriseDeveloperKey = EnvironmentDownloadCatalog.SqlServer2025EnterpriseDeveloperKey;
    public const string SqlServer2022ExpressKey = EnvironmentDownloadCatalog.SqlServer2022ExpressKey;
    public const string SqlServer2017ExpressKey = EnvironmentDownloadCatalog.SqlServer2017ExpressKey;
    public const string SqlServer2012ExpressX64Key = EnvironmentDownloadCatalog.SqlServer2012ExpressX64Key;
    public const string SqlServer2012ExpressX86Key = EnvironmentDownloadCatalog.SqlServer2012ExpressX86Key;
    public const string SqlServer2008ExpressX64Key = EnvironmentDownloadCatalog.SqlServer2008ExpressX64Key;
    public const string SqlServer2008ExpressX86Key = EnvironmentDownloadCatalog.SqlServer2008ExpressX86Key;
    public const string TomcatPackageKey = EnvironmentDownloadCatalog.TomcatPackageKey;
    public const string FrpPackageKey = EnvironmentDownloadCatalog.FrpPackageKey;

    public const string DefaultIisUrlRewriteUrl = EnvironmentDownloadCatalog.DefaultIisUrlRewriteUrl;
    public const string DefaultNginxPackageUrl = EnvironmentDownloadCatalog.DefaultNginxPackageUrl;
    public const string DefaultMySqlPackageUrl = EnvironmentDownloadCatalog.DefaultMySqlPackageUrl;
    public const string DefaultSqlServer2025ExpressUrl = EnvironmentDownloadCatalog.DefaultSqlServer2025ExpressUrl;
    public const string DefaultSqlServer2025EnterpriseDeveloperUrl = EnvironmentDownloadCatalog.DefaultSqlServer2025EnterpriseDeveloperUrl;
    public const string DefaultSqlServer2022ExpressUrl = EnvironmentDownloadCatalog.DefaultSqlServer2022ExpressUrl;
    public const string DefaultSqlServer2017ExpressUrl = EnvironmentDownloadCatalog.DefaultSqlServer2017ExpressUrl;
    public const string DefaultSqlServer2012ExpressX64Url = EnvironmentDownloadCatalog.DefaultSqlServer2012ExpressX64Url;
    public const string DefaultSqlServer2012ExpressX86Url = EnvironmentDownloadCatalog.DefaultSqlServer2012ExpressX86Url;
    public const string DefaultSqlServer2008ExpressX64Url = EnvironmentDownloadCatalog.DefaultSqlServer2008ExpressX64Url;
    public const string DefaultSqlServer2008ExpressX86Url = EnvironmentDownloadCatalog.DefaultSqlServer2008ExpressX86Url;
    public const string DefaultTomcatPackageUrl = EnvironmentDownloadCatalog.DefaultTomcatPackageUrl;
    public const string DefaultFrpPackageUrl = EnvironmentDownloadCatalog.DefaultFrpPackageUrl;

    public static IReadOnlyList<string> ConfigKeys { get; } =
        EnvironmentDownloadCatalog.All.Select(definition => definition.Key).ToArray();

    public static EnvironmentDownloadSettings Load()
    {
        NameValueCollection settings;
        try
        {
            settings = ConfigurationManager.AppSettings;
        }
        catch (Exception exception)
        {
            throw new ConfigurationErrorsException(
                "无法读取 MCPanel.exe.config 中的环境下载配置。",
                exception);
        }

        var validation = Validate(settings);
        if (!validation.IsValid)
        {
            throw new ConfigurationErrorsException(validation.ToDisplayMessage());
        }

        return Load(settings);
    }

    public static EnvironmentDownloadSettings Load(NameValueCollection settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var urls = EnvironmentDownloadCatalog.All.ToDictionary(
            definition => definition.Key,
            definition => ReadUrl(settings, definition));

        return new(
            urls[IisUrlRewriteKey],
            urls[NginxPackageKey],
            urls[MySqlPackageKey],
            urls[SqlServer2025ExpressKey],
            urls[SqlServer2025EnterpriseDeveloperKey],
            urls[SqlServer2022ExpressKey],
            urls[SqlServer2017ExpressKey],
            urls[SqlServer2012ExpressX64Key],
            urls[SqlServer2012ExpressX86Key],
            urls[SqlServer2008ExpressX64Key],
            urls[SqlServer2008ExpressX86Key],
            urls[TomcatPackageKey],
            urls[FrpPackageKey]);
    }

    public static EnvironmentDownloadConfigurationValidation ValidateConfiguration()
    {
        try
        {
            return Validate(ConfigurationManager.AppSettings);
        }
        catch (Exception exception)
        {
            return new(
            [
                new EnvironmentDownloadConfigurationIssue(
                    "App.config",
                    "环境下载配置",
                    $"无法读取配置文件：{exception.Message}")
            ]);
        }
    }

    public static EnvironmentDownloadConfigurationValidation Validate(NameValueCollection settings)
    {
        if (settings is null)
        {
            throw new ArgumentNullException(nameof(settings));
        }

        var issues = new List<EnvironmentDownloadConfigurationIssue>();
        foreach (var definition in EnvironmentDownloadCatalog.All)
        {
            var value = settings[definition.Key]?.Trim();
            if (string.IsNullOrWhiteSpace(value))
            {
                issues.Add(new(
                    definition.Key,
                    definition.DisplayName,
                    "缺少配置值"));
                continue;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                issues.Add(new(
                    definition.Key,
                    definition.DisplayName,
                    "必须是完整的 http:// 或 https:// URL"));
            }
        }

        return new(issues);
    }

    private static string ReadUrl(
        NameValueCollection settings,
        EnvironmentDownloadDefinition definition)
    {
        try
        {
            var value = settings[definition.Key]?.Trim();
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value!
                : definition.DefaultUrl;
        }
        catch
        {
            return definition.DefaultUrl;
        }
    }
}

public sealed record EnvironmentDownloadConfigurationIssue(
    string Key,
    string DisplayName,
    string Reason);

public sealed record EnvironmentDownloadConfigurationValidation(
    IReadOnlyList<EnvironmentDownloadConfigurationIssue> Issues)
{
    public bool IsValid => Issues.Count == 0;

    public string ToDisplayMessage() => IsValid
        ? "环境下载配置完整。"
        : "MCPanel.exe.config 中存在缺失或无效的环境下载配置：\n" +
          string.Join(
              Environment.NewLine,
              Issues.Select(issue => $"- {issue.DisplayName} ({issue.Key})：{issue.Reason}"));
}
