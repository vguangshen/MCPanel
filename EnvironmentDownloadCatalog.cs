namespace MCPanel;

/// <summary>
/// The single catalog of downloadable environment components. The executable
/// config, validation, and installers all use these keys and defaults so a new
/// environment option cannot silently drift out of one of those surfaces.
/// </summary>
public sealed record EnvironmentDownloadDefinition(
    string Key,
    string DisplayName,
    string DefaultUrl,
    string? Platform = null);

public static class EnvironmentDownloadCatalog
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
    public const string DefaultFrpPackageUrl = "https://mirrors.nju.edu.cn/github-release/fatedier/frp/LatestRelease/frp_0.71.0_windows_amd64.zip";

    public static IReadOnlyList<EnvironmentDownloadDefinition> All { get; } =
    [
        new(IisUrlRewriteKey, "IIS URL Rewrite", DefaultIisUrlRewriteUrl),
        new(NginxPackageKey, "Nginx", DefaultNginxPackageUrl),
        new(MySqlPackageKey, "MySQL legacy package", DefaultMySqlPackageUrl),
        new(SqlServer2025ExpressKey, "SQL Server 2025 Express", DefaultSqlServer2025ExpressUrl),
        new(SqlServer2025EnterpriseDeveloperKey, "SQL Server 2025 Enterprise Developer", DefaultSqlServer2025EnterpriseDeveloperUrl),
        new(SqlServer2022ExpressKey, "SQL Server 2022 Express", DefaultSqlServer2022ExpressUrl),
        new(SqlServer2017ExpressKey, "SQL Server 2017 Express", DefaultSqlServer2017ExpressUrl),
        new(SqlServer2012ExpressX64Key, "SQL Server 2012 Express (x64)", DefaultSqlServer2012ExpressX64Url, "x64"),
        new(SqlServer2012ExpressX86Key, "SQL Server 2012 Express (x86)", DefaultSqlServer2012ExpressX86Url, "x86"),
        new(SqlServer2008ExpressX64Key, "SQL Server 2008 Express (x64)", DefaultSqlServer2008ExpressX64Url, "x64"),
        new(SqlServer2008ExpressX86Key, "SQL Server 2008 Express (x86)", DefaultSqlServer2008ExpressX86Url, "x86"),
        new(TomcatPackageKey, "Tomcat", DefaultTomcatPackageUrl),
        new(FrpPackageKey, "FRP", DefaultFrpPackageUrl, "x64")
    ];

    public static EnvironmentDownloadDefinition? Find(string? key) =>
        All.FirstOrDefault(definition =>
            string.Equals(definition.Key, key, StringComparison.OrdinalIgnoreCase));
}
