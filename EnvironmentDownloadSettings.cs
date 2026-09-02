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
    public const string IisUrlRewriteKey = "Environment.Iis.UrlRewriteUrl";
    public const string NginxPackageKey = "Environment.Nginx.PackageUrl";
    public const string MySqlPackageKey = "Environment.MySql.PackageUrl";
    public const string SqlServer2025ExpressKey = "Environment.SqlServer.Sql2025ExpressUrl";
    public const string SqlServer2025EnterpriseDeveloperKey = "Environment.SqlServer.Sql2025EnterpriseDeveloperUrl";
    public const string SqlServer2022ExpressKey = "Environment.SqlServer.Sql2022ExpressUrl";
    public const string SqlServer2017ExpressKey = "Environment.SqlServer.Sql2017ExpressUrl";
    public const string SqlServer2012ExpressX64Key = "Environment.SqlServer.Sql2012ExpressX64Url";
    public const string SqlServer2012ExpressX86Key = "Environment.SqlServer.Sql2012ExpressX86Url";
    public const string SqlServer2008ExpressX64Key = "Environment.SqlServer.Sql2008ExpressX64Url";
    public const string SqlServer2008ExpressX86Key = "Environment.SqlServer.Sql2008ExpressX86Url";
    public const string TomcatPackageKey = "Environment.Tomcat.PackageUrl";
    public const string FrpPackageKey = "Environment.Frp.PackageUrl";

    public const string DefaultIisUrlRewriteUrl = "http://regservice.itmc.cn/down/tomcat/URLRewrite.msi";
    public const string DefaultNginxPackageUrl = "http://regservice.itmc.cn/down/tomcat/nginx-1.14.2.zip";
    public const string DefaultMySqlPackageUrl = "http://regservice.itmc.cn/down/mysql/mysql.zip";
    public const string DefaultSqlServer2025ExpressUrl = "https://download.microsoft.com/download/7ab8f535-7eb8-4b16-82eb-eca0fa2d38f3/SQL2025-SSEI-Expr.exe";
    public const string DefaultSqlServer2025EnterpriseDeveloperUrl = "https://aka.ms/sql2025EnterpriseDev";
    public const string DefaultSqlServer2022ExpressUrl = "https://download.microsoft.com/download/5/1/4/5145fe04-4d30-4b85-b0d1-39533663a2f1/SQL2022-SSEI-Expr.exe";
    public const string DefaultSqlServer2017ExpressUrl = "https://download.microsoft.com/download/5/E/9/5E9B18CC-8FD5-467E-B5BF-BADE39C51F73/SQLServer2017-SSEI-Expr.exe";
    public const string DefaultSqlServer2012ExpressX64Url = "https://download.microsoft.com/download/b/d/e/bde8fad6-33e5-44f6-b714-348f73e602b6/SQLEXPR_x64_ENU.exe";
    public const string DefaultSqlServer2012ExpressX86Url = "https://download.microsoft.com/download/b/d/e/bde8fad6-33e5-44f6-b714-348f73e602b6/SQLEXPR_x86_ENU.exe";
    public const string DefaultSqlServer2008ExpressX64Url = "http://regservice.itmc.cn/down/SQLServer/SQLEXPR_2008_x64.exe";
    public const string DefaultSqlServer2008ExpressX86Url = "http://regservice.itmc.cn/down/SQLServer/SQLEXPR_2008_x86.exe";
    public const string DefaultTomcatPackageUrl = "http://regservice.itmc.cn/down/tomcat/apache-tomcat-8.5.57.zip";
    public const string DefaultFrpPackageUrl = "https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip";

    public static IReadOnlyList<string> ConfigKeys { get; } =
    [
        IisUrlRewriteKey,
        NginxPackageKey,
        MySqlPackageKey,
        SqlServer2025ExpressKey,
        SqlServer2025EnterpriseDeveloperKey,
        SqlServer2022ExpressKey,
        SqlServer2017ExpressKey,
        SqlServer2012ExpressX64Key,
        SqlServer2012ExpressX86Key,
        SqlServer2008ExpressX64Key,
        SqlServer2008ExpressX86Key,
        TomcatPackageKey,
        FrpPackageKey
    ];

    public static EnvironmentDownloadSettings Load()
    {
        try
        {
            return Load(ConfigurationManager.AppSettings);
        }
        catch
        {
            return Load(new NameValueCollection());
        }
    }

    public static EnvironmentDownloadSettings Load(NameValueCollection settings)
    {
        return new(
            ReadUrl(settings, IisUrlRewriteKey, DefaultIisUrlRewriteUrl),
            ReadUrl(settings, NginxPackageKey, DefaultNginxPackageUrl),
            ReadUrl(settings, MySqlPackageKey, DefaultMySqlPackageUrl),
            ReadUrl(settings, SqlServer2025ExpressKey, DefaultSqlServer2025ExpressUrl),
            ReadUrl(settings, SqlServer2025EnterpriseDeveloperKey, DefaultSqlServer2025EnterpriseDeveloperUrl),
            ReadUrl(settings, SqlServer2022ExpressKey, DefaultSqlServer2022ExpressUrl),
            ReadUrl(settings, SqlServer2017ExpressKey, DefaultSqlServer2017ExpressUrl),
            ReadUrl(settings, SqlServer2012ExpressX64Key, DefaultSqlServer2012ExpressX64Url),
            ReadUrl(settings, SqlServer2012ExpressX86Key, DefaultSqlServer2012ExpressX86Url),
            ReadUrl(settings, SqlServer2008ExpressX64Key, DefaultSqlServer2008ExpressX64Url),
            ReadUrl(settings, SqlServer2008ExpressX86Key, DefaultSqlServer2008ExpressX86Url),
            ReadUrl(settings, TomcatPackageKey, DefaultTomcatPackageUrl),
            ReadUrl(settings, FrpPackageKey, DefaultFrpPackageUrl));
    }

    private static string ReadUrl(NameValueCollection settings, string key, string fallback)
    {
        try
        {
            var value = settings[key]?.Trim();
            return Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                   (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
                ? value!
                : fallback;
        }
        catch
        {
            return fallback;
        }
    }
}
