using Microsoft.Win32;

namespace MCPanel;

/// <summary>
/// SQL Server releases supported by the environment installer.
/// Support flags follow Microsoft's SQL Server/Windows compatibility matrix and
/// the archived Microsoft requirements for older Windows versions still
/// reachable by the legacy MCPanel compatibility path.
/// Unsupported entries remain visible in the UI, but cannot be selected.
/// </summary>
public sealed record SqlServerReleaseDefinition(string Id, string DisplayName)
{
    public SqlServerOsSupport OsSupport => SqlServerOsCompatibility.GetCurrentSupport(Id);
    public bool IsSupported => OsSupport.IsSupported;
    public string SupportNote => OsSupport.Message;
    public string DisplayNameWithSupport => IsSupported ? DisplayName : $"{DisplayName}（当前系统不支持）";
    public override string ToString() => DisplayNameWithSupport;
}

internal enum WindowsSqlCompatibilityFamily
{
    Unknown,
    WindowsServer2025,
    WindowsServer2022,
    Windows11,
    WindowsServer2019,
    Windows10,
    WindowsServer2016,
    WindowsServer2012R2,
    Windows81,
    WindowsServer2012,
    Windows8,
    WindowsServer2008R2Sp1,
    WindowsServer2008R2,
    Windows7Sp1,
    Windows7,
    WindowsServer2008Sp2,
    WindowsServer2008Sp1,
    WindowsServer2008,
    WindowsVistaSp2,
    WindowsVistaSp1,
    WindowsVista,
    Legacy
}

public sealed record SqlServerOsSupport(bool IsSupported, string Message);

internal static class SqlServerOsCompatibility
{
    private static readonly Lazy<(WindowsSqlCompatibilityFamily Family, string DisplayName)> Current = new(DetectCurrent);

    public static SqlServerOsSupport GetCurrentSupport(string releaseId)
    {
        var current = Current.Value;
        return GetSupport(releaseId, current.Family, current.DisplayName);
    }

    internal static SqlServerOsSupport GetSupport(
        string releaseId,
        WindowsSqlCompatibilityFamily family,
        string? osDisplayName = null)
    {
        var normalized = releaseId ?? string.Empty;
        var supported = family switch
        {
            WindowsSqlCompatibilityFamily.WindowsServer2025 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id),
            WindowsSqlCompatibilityFamily.WindowsServer2022 or WindowsSqlCompatibilityFamily.Windows11 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id),
            WindowsSqlCompatibilityFamily.WindowsServer2019 or WindowsSqlCompatibilityFamily.Windows10 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id),
            WindowsSqlCompatibilityFamily.WindowsServer2016 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id),
            WindowsSqlCompatibilityFamily.WindowsServer2012R2 or
            WindowsSqlCompatibilityFamily.Windows81 or
            WindowsSqlCompatibilityFamily.WindowsServer2012 or
            WindowsSqlCompatibilityFamily.Windows8 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id,
                SqlServerReleaseCatalog.SqlServer2008Id),
            WindowsSqlCompatibilityFamily.WindowsServer2008R2Sp1 or
            WindowsSqlCompatibilityFamily.Windows7Sp1 or
            WindowsSqlCompatibilityFamily.WindowsServer2008Sp2 or
            WindowsSqlCompatibilityFamily.WindowsVistaSp2 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2012Id,
                SqlServerReleaseCatalog.SqlServer2008Id),
            WindowsSqlCompatibilityFamily.WindowsServer2008R2 or
            WindowsSqlCompatibilityFamily.Windows7 or
            WindowsSqlCompatibilityFamily.WindowsServer2008Sp1 or
            WindowsSqlCompatibilityFamily.WindowsVistaSp1 or
            WindowsSqlCompatibilityFamily.Legacy => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2008Id),
            WindowsSqlCompatibilityFamily.WindowsServer2008 or
            WindowsSqlCompatibilityFamily.WindowsVista => false,
            WindowsSqlCompatibilityFamily.Unknown => true,
            _ => false
        };

        if (supported)
        {
            if (normalized == SqlServerReleaseCatalog.SqlServer2008Id)
            {
                return new(true, "Microsoft 官方旧版要求中仅保留 SQL Server 2008 SP4 兼容入口；请确认安装包已达到 SP4。 ");
            }

            if (normalized == SqlServerReleaseCatalog.SqlServer2012Id &&
                family is WindowsSqlCompatibilityFamily.Windows10 or
                    WindowsSqlCompatibilityFamily.WindowsServer2019 or
                    WindowsSqlCompatibilityFamily.WindowsServer2016 or
                    WindowsSqlCompatibilityFamily.Windows81 or
                    WindowsSqlCompatibilityFamily.WindowsServer2012R2 or
                    WindowsSqlCompatibilityFamily.Windows8 or
                    WindowsSqlCompatibilityFamily.WindowsServer2012)
            {
                return new(true, "Microsoft 官方兼容矩阵要求 SQL Server 2012 SP4。 ");
            }

            return new(true, string.Empty);
        }

        var release = SqlServerReleaseCatalog.Options.FirstOrDefault(option =>
            string.Equals(option.Id, normalized, StringComparison.OrdinalIgnoreCase));
        var releaseName = release?.DisplayName ?? normalized;
        var osName = string.IsNullOrWhiteSpace(osDisplayName) ? FamilyDisplayName(family) : osDisplayName!;
        return new(false, $"Microsoft 官方兼容要求不支持 {releaseName} 安装在 {osName}。请选择受支持的 SQL Server 版本。");
    }

    private static bool IsOneOf(string value, params string[] supported) =>
        supported.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private static (WindowsSqlCompatibilityFamily Family, string DisplayName) DetectCurrent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = Convert.ToString(key?.GetValue("ProductName"))?.Trim() ?? string.Empty;
            var buildText = Convert.ToString(key?.GetValue("CurrentBuildNumber"))?.Trim();
            var build = int.TryParse(buildText, out var parsedBuild)
                ? parsedBuild
                : Environment.OSVersion.Version.Build;
            var isServer = productName.Contains("Server", StringComparison.OrdinalIgnoreCase);
            var family = Classify(isServer, build);
            var displayName = string.IsNullOrWhiteSpace(productName)
                ? FamilyDisplayName(family)
                : productName;
            return (family, displayName);
        }
        catch
        {
            var version = Environment.OSVersion.Version;
            return (WindowsSqlCompatibilityFamily.Unknown, $"Windows {version}");
        }
    }

    internal static WindowsSqlCompatibilityFamily Classify(bool isServer, int build)
    {
        if (isServer)
        {
            if (build >= 26100) return WindowsSqlCompatibilityFamily.WindowsServer2025;
            if (build >= 20348) return WindowsSqlCompatibilityFamily.WindowsServer2022;
            if (build >= 17763) return WindowsSqlCompatibilityFamily.WindowsServer2019;
            if (build >= 14393) return WindowsSqlCompatibilityFamily.WindowsServer2016;
            if (build >= 9600) return WindowsSqlCompatibilityFamily.WindowsServer2012R2;
            if (build >= 9200) return WindowsSqlCompatibilityFamily.WindowsServer2012;
            if (build >= 7601) return WindowsSqlCompatibilityFamily.WindowsServer2008R2Sp1;
            if (build >= 7600) return WindowsSqlCompatibilityFamily.WindowsServer2008R2;
            if (build >= 6002) return WindowsSqlCompatibilityFamily.WindowsServer2008Sp2;
            if (build >= 6001) return WindowsSqlCompatibilityFamily.WindowsServer2008Sp1;
            if (build >= 6000) return WindowsSqlCompatibilityFamily.WindowsServer2008;
            return WindowsSqlCompatibilityFamily.Legacy;
        }

        if (build >= 22000) return WindowsSqlCompatibilityFamily.Windows11;
        if (build >= 10240) return WindowsSqlCompatibilityFamily.Windows10;
        if (build >= 9600) return WindowsSqlCompatibilityFamily.Windows81;
        if (build >= 9200) return WindowsSqlCompatibilityFamily.Windows8;
        if (build >= 7601) return WindowsSqlCompatibilityFamily.Windows7Sp1;
        if (build >= 7600) return WindowsSqlCompatibilityFamily.Windows7;
        if (build >= 6002) return WindowsSqlCompatibilityFamily.WindowsVistaSp2;
        if (build >= 6001) return WindowsSqlCompatibilityFamily.WindowsVistaSp1;
        if (build >= 6000) return WindowsSqlCompatibilityFamily.WindowsVista;
        return WindowsSqlCompatibilityFamily.Legacy;
    }

    private static string FamilyDisplayName(WindowsSqlCompatibilityFamily family) => family switch
    {
        WindowsSqlCompatibilityFamily.WindowsServer2025 => "Windows Server 2025",
        WindowsSqlCompatibilityFamily.WindowsServer2022 => "Windows Server 2022",
        WindowsSqlCompatibilityFamily.Windows11 => "Windows 11",
        WindowsSqlCompatibilityFamily.WindowsServer2019 => "Windows Server 2019",
        WindowsSqlCompatibilityFamily.Windows10 => "Windows 10",
        WindowsSqlCompatibilityFamily.WindowsServer2016 => "Windows Server 2016",
        WindowsSqlCompatibilityFamily.WindowsServer2012R2 => "Windows Server 2012 R2",
        WindowsSqlCompatibilityFamily.Windows81 => "Windows 8.1",
        WindowsSqlCompatibilityFamily.WindowsServer2012 => "Windows Server 2012",
        WindowsSqlCompatibilityFamily.Windows8 => "Windows 8",
        WindowsSqlCompatibilityFamily.WindowsServer2008R2Sp1 => "Windows Server 2008 R2 SP1",
        WindowsSqlCompatibilityFamily.WindowsServer2008R2 => "Windows Server 2008 R2",
        WindowsSqlCompatibilityFamily.Windows7Sp1 => "Windows 7 SP1",
        WindowsSqlCompatibilityFamily.Windows7 => "Windows 7",
        WindowsSqlCompatibilityFamily.WindowsServer2008Sp2 => "Windows Server 2008 SP2",
        WindowsSqlCompatibilityFamily.WindowsServer2008Sp1 => "Windows Server 2008 SP1",
        WindowsSqlCompatibilityFamily.WindowsServer2008 => "Windows Server 2008",
        WindowsSqlCompatibilityFamily.WindowsVistaSp2 => "Windows Vista SP2",
        WindowsSqlCompatibilityFamily.WindowsVistaSp1 => "Windows Vista SP1",
        WindowsSqlCompatibilityFamily.WindowsVista => "Windows Vista",
        WindowsSqlCompatibilityFamily.Legacy => "旧版 Windows",
        _ => "当前 Windows"
    };
}

public static class SqlServerReleaseCatalog
{
    public const string SqlServer2025Id = "sqlserver-2025";
    public const string SqlServer2025EnterpriseDeveloperId = "sqlserver-2025-enterprise-developer";
    public const string SqlServer2022Id = "sqlserver-2022";
    public const string SqlServer2017Id = "sqlserver-2017";
    public const string SqlServer2012Id = "sqlserver-2012";
    public const string SqlServer2008Id = "sqlserver-2008";

    public static SqlServerReleaseDefinition SqlServer2025 { get; } = new(SqlServer2025Id, "SQL Server 2025 Express");
    public static SqlServerReleaseDefinition SqlServer2025EnterpriseDeveloper { get; } = new(SqlServer2025EnterpriseDeveloperId, "SQL Server 2025 Enterprise Developer");
    public static SqlServerReleaseDefinition SqlServer2022 { get; } = new(SqlServer2022Id, "SQL Server 2022 Express");
    public static SqlServerReleaseDefinition SqlServer2017 { get; } = new(SqlServer2017Id, "SQL Server 2017 Express");
    public static SqlServerReleaseDefinition SqlServer2012 { get; } = new(SqlServer2012Id, "SQL Server 2012 Express SP4");
    public static SqlServerReleaseDefinition SqlServer2008 { get; } = new(SqlServer2008Id, "SQL Server 2008 Express");

    public static IReadOnlyList<SqlServerReleaseDefinition> Options { get; } =
    [
        SqlServer2025,
        SqlServer2025EnterpriseDeveloper,
        SqlServer2022,
        SqlServer2017,
        SqlServer2012,
        SqlServer2008
    ];

    public static SqlServerReleaseDefinition Recommended
    {
        get
        {
            var supported = Options.FirstOrDefault(option => option.IsSupported);
            if (supported is not null)
            {
                return supported;
            }

            var version = Environment.OSVersion.Version;
            if (version.Major >= 10 && version.Build >= 22000) return SqlServer2025;
            if (version.Major >= 10) return SqlServer2022;
            if (version.Major == 6 && version.Minor >= 3) return SqlServer2017;
            if (version.Major == 6 && version.Minor == 2) return SqlServer2012;
            return SqlServer2008;
        }
    }

    public static bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Options.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsSupported(string? id) => Contains(id) && Resolve(id).IsSupported;

    public static SqlServerReleaseDefinition? FindByInstallation(int majorVersion, string? edition)
    {
        var editionText = edition ?? string.Empty;
        var isDeveloper = editionText.Contains("Developer", StringComparison.OrdinalIgnoreCase);
        var isExpress = editionText.Contains("Express", StringComparison.OrdinalIgnoreCase);

        return majorVersion switch
        {
            17 when isDeveloper => SqlServer2025EnterpriseDeveloper,
            17 when isExpress => SqlServer2025,
            16 when isExpress => SqlServer2022,
            14 when isExpress => SqlServer2017,
            11 when isExpress => SqlServer2012,
            10 when isExpress => SqlServer2008,
            _ => null
        };
    }

    public static SqlServerReleaseDefinition Resolve(string? id) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Recommended;
}
