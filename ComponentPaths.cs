using System.IO;

namespace MCPanel;

/// <summary>
/// Canonical locations for external runtime components.
///
/// StoreData is the panel's application state (settings, operational state,
/// update metadata and working logs).  Large component-owned runtime data,
/// download caches and tools live beside it so a deployment can be inspected
/// and backed up one category at a time.
/// Legacy paths remain in the search lists so existing installations and
/// Windows services continue to work while the layout is migrated.
/// </summary>
internal static class ComponentPaths
{
    public static string ApplicationRoot => Path.GetFullPath(AppContext.BaseDirectory);
    public static string StoreDataRoot => Path.Combine(ApplicationRoot, "StoreData");
    public static string RuntimeRoot => Path.Combine(ApplicationRoot, "Runtime");
    public static string LegacyRuntimeRoot => Path.Combine(StoreDataRoot, "Runtime");
    public static string DownloadsRoot => Path.Combine(ApplicationRoot, "Downloads");
    public static string LegacyDownloadsRoot => Path.Combine(StoreDataRoot, "Downloads");
    public static string ToolsRoot => Path.Combine(ApplicationRoot, "Tools");
    public static string LegacyToolsRoot => Path.Combine(StoreDataRoot, "Tools");
    public static string ProductIconsRoot => Path.Combine(ApplicationRoot, "Cache", "ProductIcons");
    public static string LegacyProductIconsRoot => Path.Combine(StoreDataRoot, "ProductIcons");
    public static string AccountApiRoot => Path.Combine(ApplicationRoot, "AccountApi");

    public static string ApplicationNginxRoot => Path.Combine(ApplicationRoot, "Nginx");
    public static string ApplicationMySqlRoot => Path.Combine(ApplicationRoot, "MySQL");
    public static string NginxRoot => PathCompatibility.GetNativeComponentRoot(ApplicationRoot, "Nginx");
    public static string MySqlRoot => PathCompatibility.GetNativeComponentRoot(ApplicationRoot, "MySQL");
    public static string SqlServerRoot => Path.Combine(ApplicationRoot, "MSSQL");
    public static string TomcatRoot => Path.Combine(ApplicationRoot, "Tomcat");
    public static string FrpRoot => Path.Combine(ApplicationRoot, "Frp");
    public static string SsmsRoot => Path.Combine(ApplicationRoot, "SSMS");
    public static string NavicatLiteRoot => Path.Combine(ApplicationRoot, "Navicat Premium Lite");

    public static string EnvironmentDownloadRoot =>
        Path.Combine(DownloadsRoot, "Environment");

    public static string SsmsCacheRoot =>
        Path.Combine(ToolsRoot, "SSMSCache");

    public static IReadOnlyList<string> NginxSearchRoots =>
        new[] { NginxRoot, ApplicationNginxRoot, RuntimeRoot, LegacyRuntimeRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> MySqlSearchRoots =>
        new[] { MySqlRoot, ApplicationMySqlRoot, RuntimeRoot, LegacyRuntimeRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> MySqlInstallationRoots =>
        new[] { MySqlRoot, ApplicationMySqlRoot, RuntimeMySqlRoot, LegacyMySqlRoot }
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static IReadOnlyList<string> TomcatSearchRoots =>
        new[] { TomcatRoot, RuntimeRoot, LegacyRuntimeRoot };

    public static IReadOnlyList<string> SqlServerDataRoots =>
        new[]
        {
            SqlServerRoot,
            Path.Combine(RuntimeRoot, "MSSQL"),
            Path.Combine(LegacyRuntimeRoot, "MSSQL")
        };

    public static string RuntimeMySqlRoot => Path.Combine(RuntimeRoot, "mysql");
    public static string LegacyMySqlRoot => Path.Combine(LegacyRuntimeRoot, "mysql");

    public static string SelectNginxInstallRoot()
    {
        if (NginxRuntimeManager.FindNginxExe(NginxRoot) is not null)
        {
            return NginxRoot;
        }

        if (NginxRuntimeManager.FindNginxExe(ApplicationNginxRoot) is not null)
        {
            return NginxRoot;
        }

        if (NginxRuntimeManager.FindNginxExe(RuntimeRoot) is not null)
        {
            // Runtime is shared with Tomcat and other compatibility data. Do not
            // reuse it as a new Nginx installation root.
            return NginxRoot;
        }

        if (NginxRuntimeManager.FindNginxExe(LegacyRuntimeRoot) is not null)
        {
            return NginxRoot;
        }

        return NginxRoot;
    }

    public static string SelectMySqlInstallRoot()
    {
        if (FindMySqlExecutable(MySqlRoot) is not null)
        {
            return MySqlRoot;
        }

        if (FindMySqlExecutable(ApplicationMySqlRoot) is not null)
        {
            return MySqlRoot;
        }

        if (FindMySqlExecutable(RuntimeMySqlRoot) is not null)
        {
            return RuntimeMySqlRoot;
        }

        if (FindMySqlExecutable(LegacyMySqlRoot) is not null)
        {
            return LegacyMySqlRoot;
        }

        // Runtime and StoreData\Runtime are shared compatibility roots. A legacy
        // MySQL package may still be detected there, but new installs must never
        // reuse either shared root as the MySQL installation directory.

        return MySqlRoot;
    }

    public static string SelectSqlServerDataRoot(bool serviceExists)
    {
        var migrated = Path.Combine(RuntimeRoot, "MSSQL");
        if (serviceExists && Directory.Exists(migrated) && !Directory.Exists(SqlServerRoot))
        {
            return migrated;
        }

        var legacy = Path.Combine(LegacyRuntimeRoot, "MSSQL");
        if (serviceExists && Directory.Exists(legacy) && !Directory.Exists(SqlServerRoot))
        {
            return legacy;
        }

        return SqlServerRoot;
    }

    private static string? FindMySqlExecutable(string root)
    {
        var direct = Path.Combine(root, "bin", "mysql.exe");
        if (File.Exists(direct))
        {
            return direct;
        }

        var knownNested = Path.Combine(root, "mysql", "bin", "mysql.exe");
        if (File.Exists(knownNested))
        {
            return knownNested;
        }

        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            foreach (var directory in Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(directory, "bin", "mysql.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A protected legacy sibling must not hide a valid new install.
        }
        catch (IOException)
        {
            // Treat transient filesystem failures as "not found".
        }

        return null;
    }
}
