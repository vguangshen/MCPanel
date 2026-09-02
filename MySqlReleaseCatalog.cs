using System.Text.RegularExpressions;

namespace MCPanel;

/// <summary>
/// MySQL packages that can be selected before the first installation.
/// Official packages use Oracle's MySQL CDN directly. The dev.mysql.com/get
/// redirect endpoint currently returns 403 to the desktop downloader, while
/// the corresponding CDN objects return the expected ZIP archive.
/// </summary>
public sealed record MySqlReleaseDefinition(
    string Id,
    string DisplayName,
    string PackageUrl,
    string PackageFileName,
    bool IsLegacy = false)
{
    public string ServerVersion => IsLegacy && Id.StartsWith("legacy-", StringComparison.OrdinalIgnoreCase)
        ? Id.Substring("legacy-".Length)
        : Id;

    public override string ToString() => DisplayName;
}

public static class MySqlReleaseCatalog
{
    public const string MySql8411Id = "8.4.11";
    public const string MySql972Id = "9.7.2";
    public const string LegacyMySql56Id = "legacy-5.6.31";

    public static MySqlReleaseDefinition MySql8411 { get; } = new(
        MySql8411Id,
        "MySQL 8.4.11 LTS",
        "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
        "mysql-8.4.11-winx64.zip");

    public static MySqlReleaseDefinition MySql972 { get; } = new(
        MySql972Id,
        "MySQL 9.7.2 LTS",
        "https://cdn.mysql.com/Downloads/MySQL-9.7/mysql-9.7.2-winx64.zip",
        "mysql-9.7.2-winx64.zip");

    public static MySqlReleaseDefinition LegacyMySql56 { get; } = new(
        LegacyMySql56Id,
        "MySQL 5.6.31",
        string.Empty,
        "mysql.zip",
        IsLegacy: true);

    public static IReadOnlyList<MySqlReleaseDefinition> Options { get; } =
    [
        MySql8411,
        MySql972,
        LegacyMySql56
    ];

    public static MySqlReleaseDefinition Default => MySql8411;

    public static bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Options.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    public static MySqlReleaseDefinition? FindByServerVersion(string? output)
    {
        var version = ExtractServerVersion(output);
        if (version is null)
        {
            return null;
        }

        return Options.FirstOrDefault(option =>
            string.Equals(option.ServerVersion, version, StringComparison.OrdinalIgnoreCase) ||
            version.StartsWith(option.ServerVersion + ".", StringComparison.OrdinalIgnoreCase));
    }

    public static string? ExtractServerVersion(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var match = Regex.Match(output, @"(?<!\d)\d+\.\d+\.\d+(?!\d)", RegexOptions.CultureInvariant);
        return match.Success ? match.Value : null;
    }

    public static MySqlReleaseDefinition Resolve(string? id, string legacyPackageUrl)
    {
        var selected = Options.FirstOrDefault(option =>
            string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

        if (selected is null)
        {
            selected = Default;
        }

        return selected.IsLegacy
            ? selected with { PackageUrl = legacyPackageUrl }
            : selected;
    }
}
