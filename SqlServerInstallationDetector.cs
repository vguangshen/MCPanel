using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace MCPanel;

/// <summary>
/// A SQL Server instance discovered from the machine registry. This is used
/// only to choose a compatible SSMS bootstrapper; it never reads credentials.
/// </summary>
public sealed record SqlServerInstallationInfo(
    string InstanceName,
    string InstanceId,
    int MajorVersion,
    string Version,
    string Edition)
{
    public string DisplayName
    {
        get
        {
            var editionText = string.IsNullOrWhiteSpace(Edition) ? string.Empty : $" {Edition}";
            var versionText = string.IsNullOrWhiteSpace(Version)
                ? MajorVersion.ToString(CultureInfo.InvariantCulture)
                : Version;
            return $"SQL Server {versionText}{editionText}";
        }
    }

    /// <summary>
    /// Returns the SQL Server product generation, rather than only the raw
    /// build number. SQL Server 2008 R2 is identified by the 10.50 build line.
    /// </summary>
    public string GenerationDisplayName
    {
        get
        {
            var generation = MajorVersion switch
            {
                10 when IsSqlServer2008R2 => "SQL Server 2008 R2",
                10 => "SQL Server 2008",
                11 => "SQL Server 2012",
                12 => "SQL Server 2014",
                13 => "SQL Server 2016",
                14 => "SQL Server 2017",
                15 => "SQL Server 2019",
                16 => "SQL Server 2022",
                17 => "SQL Server 2025",
                _ => string.IsNullOrWhiteSpace(Version)
                    ? $"SQL Server {MajorVersion}.x"
                    : $"SQL Server {Version}"
            };

            return string.IsNullOrWhiteSpace(Edition) ? generation : $"{generation} {Edition}";
        }
    }

    private bool IsSqlServer2008R2 =>
        MajorVersion == 10 &&
        (Version.StartsWith("10.50.", StringComparison.OrdinalIgnoreCase) ||
         InstanceId.Contains("10_50", StringComparison.OrdinalIgnoreCase) ||
         InstanceId.Contains("10.50", StringComparison.OrdinalIgnoreCase));
}

internal static class SqlServerInstallationDetector
{
    private const string InstanceNamesPath = @"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL";
    private const string InstanceRootPath = @"SOFTWARE\Microsoft\Microsoft SQL Server";

    public static IReadOnlyList<SqlServerInstallationInfo> FindInstalled()
    {
        var result = new List<SqlServerInstallationInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var view in RegistryViews())
        {
            try
            {
                using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view);
                using var instanceNames = baseKey.OpenSubKey(InstanceNamesPath);
                if (instanceNames is null)
                {
                    continue;
                }

                foreach (var instanceName in instanceNames.GetValueNames())
                {
                    if (string.IsNullOrWhiteSpace(instanceName))
                    {
                        continue;
                    }

                    var instanceId = ReadValue(instanceNames, instanceName);
                    if (string.IsNullOrWhiteSpace(instanceId))
                    {
                        continue;
                    }

                    using var setup = baseKey.OpenSubKey($"{InstanceRootPath}\\{instanceId}\\Setup");
                    var version = ReadFirstValue(setup, "PatchLevel", "CurrentVersion", "ProductVersion", "Version");
                    var major = TryParseMajorVersion(version) ?? TryParseMajorVersion(instanceId);
                    if (!major.HasValue || major.Value <= 0)
                    {
                        continue;
                    }

                    var edition = ReadFirstValue(setup, "Edition", "EditionName");
                    var key = $"{instanceName}|{instanceId}|{major.Value}|{version}";
                    if (seen.Add(key))
                    {
                        result.Add(new SqlServerInstallationInfo(
                            instanceName,
                            instanceId,
                            major.Value,
                            version,
                            edition));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // A protected registry view must not prevent checking the other view.
            }
        }

        return result;
    }

    public static SqlServerInstallationInfo? FindPreferred()
    {
        return FindInstalled()
            .OrderBy(info => string.Equals(info.InstanceName, "MSSQLSERVER", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(info => info.MajorVersion)
            .ThenBy(info => info.InstanceName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    internal static int? TryParseMajorVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = Regex.Match(value, @"(?<!\d)(\d+)(?:\.\d+)?", RegexOptions.CultureInvariant);
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var major)
            ? major
            : null;
    }

    private static string ReadFirstValue(RegistryKey? key, params string[] names)
    {
        if (key is null)
        {
            return string.Empty;
        }

        foreach (var name in names)
        {
            var value = ReadValue(key, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ReadValue(RegistryKey key, string name) =>
        Convert.ToString(key.GetValue(name), CultureInfo.InvariantCulture)?.Trim() ?? string.Empty;

    private static IEnumerable<RegistryView> RegistryViews()
    {
        var seen = new HashSet<RegistryView>();
        if (Environment.Is64BitOperatingSystem && seen.Add(RegistryView.Registry64))
        {
            yield return RegistryView.Registry64;
        }

        if (seen.Add(RegistryView.Registry32))
        {
            yield return RegistryView.Registry32;
        }

        if (seen.Add(RegistryView.Default))
        {
            yield return RegistryView.Default;
        }
    }
}
