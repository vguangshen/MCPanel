namespace MCPanel;

/// <summary>
/// SQL Server releases supported by the environment installer.
/// The recommended entry follows the existing Windows-version mapping, while
/// the other entries remain available for an explicit installation choice.
/// </summary>
public sealed record SqlServerReleaseDefinition(string Id, string DisplayName)
{
    public override string ToString() => DisplayName;
}

public static class SqlServerReleaseCatalog
{
    public const string SqlServer2025Id = "sqlserver-2025";
    public const string SqlServer2025EnterpriseDeveloperId = "sqlserver-2025-enterprise-developer";
    public const string SqlServer2022Id = "sqlserver-2022";
    public const string SqlServer2017Id = "sqlserver-2017";
    public const string SqlServer2012Id = "sqlserver-2012";
    public const string SqlServer2008Id = "sqlserver-2008";

    public static SqlServerReleaseDefinition SqlServer2025 { get; } = new(
        SqlServer2025Id,
        "SQL Server 2025 Express");

    public static SqlServerReleaseDefinition SqlServer2025EnterpriseDeveloper { get; } = new(
        SqlServer2025EnterpriseDeveloperId,
        "SQL Server 2025 Enterprise Developer");

    public static SqlServerReleaseDefinition SqlServer2022 { get; } = new(
        SqlServer2022Id,
        "SQL Server 2022 Express");

    public static SqlServerReleaseDefinition SqlServer2017 { get; } = new(
        SqlServer2017Id,
        "SQL Server 2017 Express");

    public static SqlServerReleaseDefinition SqlServer2012 { get; } = new(
        SqlServer2012Id,
        "SQL Server 2012 Express SP4");

    public static SqlServerReleaseDefinition SqlServer2008 { get; } = new(
        SqlServer2008Id,
        "SQL Server 2008 Express");

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
            var version = Environment.OSVersion.Version;
            if (version.Major >= 10 && version.Build >= 22000)
            {
                return SqlServer2025;
            }

            if (version.Major >= 10)
            {
                return SqlServer2022;
            }

            if (version.Major == 6 && version.Minor >= 3)
            {
                return SqlServer2017;
            }

            if (version.Major == 6 && version.Minor == 2)
            {
                return SqlServer2012;
            }

            return SqlServer2008;
        }
    }

    public static bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Options.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

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
