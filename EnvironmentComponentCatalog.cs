using System.IO;

namespace MCPanel;

internal sealed record EnvironmentComponentDefinition(
    EnvironmentKind Kind, string Title, string ServiceTitle, string VersionLabel,
    string InstallDescription, string ServiceDescription, string DirectoryName);

/// <summary>Shared component identity for installers, cards and diagnostics.</summary>
internal static class EnvironmentComponentCatalog
{
    public const string NginxVersion = "1.14.2";
    public const string NginxDirectoryName = "nginx-" + NginxVersion;
    public const string NginxPackageFileName = NginxDirectoryName + ".zip";
    public const string TomcatVersion = "8.5.57";
    public const string TomcatDirectoryName = "apache-tomcat-" + TomcatVersion;
    public const string TomcatPackageFileName = TomcatDirectoryName + ".zip";
    public const string FrpDirectoryName = "Frp";
    public const string FrpVersion = "0.71.0";
    public const string FrpPackageFileName = "frp_" + FrpVersion + "_windows_amd64.zip";

    public static IReadOnlyList<EnvironmentComponentDefinition> All { get; } =
    [
        new(EnvironmentKind.Iis, "Web Server", "IIS Web 服务", "服务器  IIS",
            "根据当前 Windows 版本启用对应 IIS 组件，并安装 URL Rewrite。", "Default Web Site / IIS 应用服务", "IIS"),
        new(EnvironmentKind.Nginx, "Nginx", "Nginx 反向代理", "版本  Nginx " + NginxVersion,
            "从原版服务器下载 Nginx 定制包，解压并启动反向代理服务。", "本地反向代理与端口转发服务", "Nginx"),
        new(EnvironmentKind.MySql, "MySQL", "MySQL 数据库", "版本  " + MySqlReleaseCatalog.Default.DisplayName,
            "选择版本后从 MySQL 官网下载 Windows x64 ZIP，初始化 root 密码并注册服务。", "本机 MySQL 数据库连接", "MySQL"),
        new(EnvironmentKind.SqlServer, "SQL Server", "SQL Server", "版本  " + SqlServerReleaseCatalog.Recommended.DisplayName,
            "选择 Express 或 Enterprise Developer 版本后下载对应官方 SQL Server 安装包；默认按当前 Windows 版本推荐。", "本机 SQL Server 数据库连接", "MSSQL"),
        new(EnvironmentKind.Tomcat, "Tomcat Server", "Tomcat Server", "Tomcat " + TomcatVersion,
            "从原版服务器下载定制 Tomcat，自动解压并配置启动脚本。", "Java Web 应用运行服务", "Tomcat"),
        new(EnvironmentKind.FrpTunnel, "FRP 内网穿透", "FRP 内网穿透", "frp " + FrpVersion + " windows amd64",
            "点击安装后下载 FRP 客户端，并注册、启动 Windows 服务；可配置连接和代理映射。", "客户端映射与网页配置管理", FrpDirectoryName)
    ];

    public static EnvironmentComponentDefinition Get(EnvironmentKind kind) => All.First(item => item.Kind == kind);

    public static IReadOnlyList<EnvironmentKind> HomeOrder { get; } =
        [EnvironmentKind.Iis, EnvironmentKind.Nginx, EnvironmentKind.Tomcat, EnvironmentKind.MySql, EnvironmentKind.SqlServer, EnvironmentKind.FrpTunnel];

    public static IReadOnlyList<string> FrpLogRoots(string applicationRoot) =>
        [Path.Combine(applicationRoot, FrpDirectoryName), Path.Combine(applicationRoot, "StoreData")];
}
