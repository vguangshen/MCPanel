namespace MCPanel;

public sealed record SsmsReleaseDefinition(
    string Id,
    string DisplayName,
    string DownloadUrl,
    int MajorVersion,
    string CompatibilityText)
{
    public string CacheFileName => $"SSMS-Setup-{Id}.exe";

    public string ChannelId => $"SSMS.{MajorVersion}.SSMS.Release";

    public string ProductId => "Microsoft.VisualStudio.Product.Ssms";
}

public sealed record SsmsReleaseRecommendation(
    SsmsReleaseDefinition Release,
    SqlServerInstallationInfo? SqlServer)
{
    public string Summary => SqlServer is null
        ? $"匹配下载：{Release.DisplayName}（未检测到本机 SQL Server，使用通用兼容版本）"
        : $"匹配下载：{Release.DisplayName}（对应 {SqlServer.GenerationDisplayName}；{Release.CompatibilityText}）";
}

/// <summary>
/// SSMS is a separate client product and is not numerically locked to the SQL
/// Server engine. For ordinary Database Engine connections, Microsoft documents
/// SSMS 22 for SQL Server 2014 and later. SQL Server 2008/2008 R2/2012 use the
/// last legacy-compatible bootstrapper kept by this application. The explicit
/// switch below makes every supported engine generation auditable instead of
/// silently treating all versions below a threshold as the same product.
/// </summary>
public static class SsmsReleaseCatalog
{
    public const string Ssms22Id = "22";
    public const string Ssms18121Id = "18.12.1";

    public static SsmsReleaseDefinition Ssms22 { get; } = new(
        Ssms22Id,
        "SSMS 22",
        "https://aka.ms/ssmsfullsetup",
        22,
        "适用于 SQL Server 2014 及更高版本");

    public static SsmsReleaseDefinition Ssms18121 { get; } = new(
        Ssms18121Id,
        "SSMS 18.12.1",
        "https://download.microsoft.com/download/8/a/8/8a8073d2-2e00-472b-9a18-88361d105915/SSMS-Setup-ENU.exe",
        18,
        "兼容 SQL Server 2008 / 2012");

    public static SsmsReleaseRecommendation ResolveForInstalledSqlServer()
    {
        var sqlServer = SqlServerInstallationDetector.FindPreferred();
        return ResolveForInstalledSqlServer(sqlServer);
    }

    public static SsmsReleaseRecommendation ResolveForInstalledSqlServer(SqlServerInstallationInfo? sqlServer)
    {
        return new(ResolveForSqlServerMajor(sqlServer?.MajorVersion), sqlServer);
    }

    public static SsmsReleaseDefinition ResolveForSqlServerMajor(int? majorVersion)
    {
        // SQL Server 2008 and 2008 R2 are 10.x; SQL Server 2012 is 11.x.
        // SSMS 22's supported Database Engine list starts at SQL Server 2014
        // (12.x). Keep the cases explicit so a newly added engine generation
        // cannot accidentally inherit the legacy rule.
        return majorVersion switch
        {
            10 => Ssms18121,
            11 => Ssms18121,
            12 => Ssms22,
            13 => Ssms22,
            14 => Ssms22,
            15 => Ssms22,
            16 => Ssms22,
            17 => Ssms22,
            _ => Ssms22
        };
    }
}
