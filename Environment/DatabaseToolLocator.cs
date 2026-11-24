using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;

namespace MCPanel;

internal enum DatabaseToolKind
{
    Navicat,
    SqlServerManagementStudio
}

internal enum DatabaseToolSource
{
    ConfiguredPath,
    ManagedFolder,
    DefaultInstallLocation,
    Desktop
}

internal sealed class DatabaseToolInstallation
{
    public DatabaseToolInstallation(
        DatabaseToolKind kind,
        string executablePath,
        DatabaseToolSource source,
        string discoveryPath,
        string productName,
        string version)
    {
        Kind = kind;
        ExecutablePath = executablePath;
        Source = source;
        DiscoveryPath = discoveryPath;
        ProductName = productName;
        Version = version;
    }

    public DatabaseToolKind Kind { get; }
    public string ExecutablePath { get; }
    public DatabaseToolSource Source { get; }
    public string DiscoveryPath { get; }
    public string ProductName { get; }
    public string Version { get; }

    public string DisplayName => string.IsNullOrWhiteSpace(Version)
        ? ProductName
        : $"{ProductName} {Version}";

    public string SourceText => Source switch
    {
        DatabaseToolSource.ConfiguredPath => "配置路径",
        DatabaseToolSource.ManagedFolder => "软件目录",
        DatabaseToolSource.DefaultInstallLocation => "默认安装位置",
        DatabaseToolSource.Desktop => "桌面",
        _ => "已检测"
    };
}

/// <summary>
/// Finds database clients only in explicitly configured paths, MCPanel's own
/// component folders, well-known vendor install roots, and the two Desktop
/// folders. It intentionally never performs a drive-wide recursive search.
/// </summary>
internal static class DatabaseToolLocator
{
    private static readonly string[] NavicatDirectoryPrefixes =
    [
        "Navicat Premium Lite",
        "Navicat Premium",
        "Navicat for MySQL"
    ];

    private static readonly string[] SsmsStandaloneDirectoryPrefixes =
    [
        "Microsoft SQL Server Management Studio"
    ];

    public static DatabaseToolInstallation? FindNavicat(string configuredPath)
    {
        var configured = TryCreate(
            DatabaseToolKind.Navicat,
            configuredPath,
            DatabaseToolSource.ConfiguredPath,
            configuredPath);
        if (configured is not null)
        {
            return configured;
        }

        var managed = FindFirst(
            DatabaseToolKind.Navicat,
            EnumerateManagedNavicatCandidates(ComponentPaths.NavicatLiteRoot),
            DatabaseToolSource.ManagedFolder);
        if (managed is not null)
        {
            return managed;
        }

        var installed = FindFirst(
            DatabaseToolKind.Navicat,
            EnumerateNavicatDefaultCandidates(GetProgramFileRoots()),
            DatabaseToolSource.DefaultInstallLocation);
        if (installed is not null)
        {
            return installed;
        }

        return FindDesktop(DatabaseToolKind.Navicat, GetDesktopRoots());
    }

    public static DatabaseToolInstallation? FindSqlServerManagementStudio(string configuredPath)
    {
        var configured = TryCreate(
            DatabaseToolKind.SqlServerManagementStudio,
            configuredPath,
            DatabaseToolSource.ConfiguredPath,
            configuredPath);
        if (configured is not null)
        {
            return configured;
        }

        var managed = FindFirst(
            DatabaseToolKind.SqlServerManagementStudio,
            EnumerateManagedSsmsCandidates(ComponentPaths.SsmsRoot),
            DatabaseToolSource.ManagedFolder);
        if (managed is not null)
        {
            return managed;
        }

        var installed = FindFirst(
            DatabaseToolKind.SqlServerManagementStudio,
            EnumerateSsmsDefaultCandidates(GetProgramFileRoots()),
            DatabaseToolSource.DefaultInstallLocation);
        if (installed is not null)
        {
            return installed;
        }

        return FindDesktop(DatabaseToolKind.SqlServerManagementStudio, GetDesktopRoots());
    }

    internal static IEnumerable<string> EnumerateNavicatDefaultCandidates(IEnumerable<string> programFileRoots)
    {
        foreach (var programFiles in DistinctExistingRoots(programFileRoots))
        {
            var vendorRoot = Path.Combine(programFiles, "PremiumSoft");
            foreach (var productDirectory in EnumerateTopDirectories(vendorRoot))
            {
                var name = Path.GetFileName(productDirectory);
                if (!NavicatDirectoryPrefixes.Any(prefix =>
                        name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return Path.Combine(productDirectory, "navicat.exe");
            }
        }
    }

    internal static IEnumerable<string> EnumerateSsmsDefaultCandidates(IEnumerable<string> programFileRoots)
    {
        foreach (var programFiles in DistinctExistingRoots(programFileRoots))
        {
            foreach (var productDirectory in EnumerateTopDirectories(programFiles))
            {
                var name = Path.GetFileName(productDirectory);
                if (!SsmsStandaloneDirectoryPrefixes.Any(prefix =>
                        name.Equals(prefix, StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith(prefix + " ", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                yield return Path.Combine(productDirectory, "Release", "Common7", "IDE", "Ssms.exe");
                yield return Path.Combine(productDirectory, "Common7", "IDE", "Ssms.exe");
            }

            var sqlServerRoot = Path.Combine(programFiles, "Microsoft SQL Server");
            foreach (var versionDirectory in EnumerateTopDirectories(sqlServerRoot))
            {
                yield return Path.Combine(versionDirectory, "Tools", "Binn", "ManagementStudio", "Ssms.exe");
                yield return Path.Combine(versionDirectory, "Tools", "Binn", "VSShell", "Common7", "IDE", "Ssms.exe");
                yield return Path.Combine(versionDirectory, "Tools", "Binn", "Ssms.exe");
            }
        }
    }

    internal static DatabaseToolInstallation? FindDesktop(
        DatabaseToolKind kind,
        IEnumerable<string> desktopRoots)
    {
        foreach (var desktop in DistinctExistingRoots(desktopRoots))
        {
            var executable = Path.Combine(
                desktop,
                kind == DatabaseToolKind.Navicat ? "navicat.exe" : "Ssms.exe");
            var direct = TryCreate(kind, executable, DatabaseToolSource.Desktop, executable);
            if (direct is not null)
            {
                return direct;
            }

            foreach (var shortcut in EnumerateTopFiles(desktop, "*.lnk"))
            {
                if (!LooksLikeToolShortcut(kind, Path.GetFileNameWithoutExtension(shortcut)))
                {
                    continue;
                }

                var target = TryResolveShortcutTarget(shortcut);
                var installation = TryCreate(kind, target, DatabaseToolSource.Desktop, shortcut);
                if (installation is not null)
                {
                    return installation;
                }
            }
        }

        return null;
    }

    internal static bool IsExpectedExecutable(DatabaseToolKind kind, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var expectedName = kind == DatabaseToolKind.Navicat ? "navicat.exe" : "ssms.exe";
        return string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase);
    }

    private static DatabaseToolInstallation? FindFirst(
        DatabaseToolKind kind,
        IEnumerable<string> candidates,
        DatabaseToolSource source)
    {
        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var installation = TryCreate(kind, candidate, source, candidate);
            if (installation is not null)
            {
                return installation;
            }
        }

        return null;
    }

    private static DatabaseToolInstallation? TryCreate(
        DatabaseToolKind kind,
        string? path,
        DatabaseToolSource source,
        string? discoveryPath)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var candidatePath = path!;
        if (!IsExpectedExecutable(kind, candidatePath))
        {
            return null;
        }

        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(candidatePath));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }

        if (!File.Exists(fullPath))
        {
            return null;
        }

        var fallbackName = kind == DatabaseToolKind.Navicat
            ? "Navicat"
            : "SQL Server Management Studio";
        var productName = fallbackName;
        var version = string.Empty;
        try
        {
            var versionInfo = FileVersionInfo.GetVersionInfo(fullPath);
            if (!string.IsNullOrWhiteSpace(versionInfo.ProductName))
            {
                productName = versionInfo.ProductName.Trim();
            }

            version = (versionInfo.ProductVersion ?? versionInfo.FileVersion ?? string.Empty).Trim();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // The executable name and location are enough to launch it. Version is optional.
        }

        var actualDiscoveryPath = string.IsNullOrWhiteSpace(discoveryPath) ? fullPath : discoveryPath!;
        return new DatabaseToolInstallation(
            kind,
            fullPath,
            source,
            actualDiscoveryPath,
            productName,
            version);
    }

    private static IEnumerable<string> EnumerateManagedNavicatCandidates(string root)
    {
        yield return Path.Combine(root, "navicat.exe");
        foreach (var directory in EnumerateTopDirectories(root))
        {
            yield return Path.Combine(directory, "navicat.exe");
        }
    }

    private static IEnumerable<string> EnumerateManagedSsmsCandidates(string root)
    {
        yield return Path.Combine(root, "Release", "Common7", "IDE", "Ssms.exe");
        yield return Path.Combine(root, "Common7", "IDE", "Ssms.exe");
        yield return Path.Combine(root, "Ssms.exe");
    }

    private static IEnumerable<string> GetProgramFileRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    }

    private static IEnumerable<string> GetDesktopRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory);
    }

    private static IEnumerable<string> DistinctExistingRoots(IEnumerable<string> roots)
    {
        return roots
            .Where(path => !string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
            .Select(Path.GetFullPath)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> EnumerateTopDirectories(string root)
    {
        if (!Directory.Exists(root))
        {
            return Enumerable.Empty<string>();
        }

        try
        {
            return Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static IEnumerable<string> EnumerateTopFiles(string root, string pattern)
    {
        try
        {
            return Directory.EnumerateFiles(root, pattern, SearchOption.TopDirectoryOnly).ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Enumerable.Empty<string>();
        }
    }

    private static bool LooksLikeToolShortcut(DatabaseToolKind kind, string name)
    {
        return kind == DatabaseToolKind.Navicat
            ? name.IndexOf("navicat", StringComparison.OrdinalIgnoreCase) >= 0
            : name.IndexOf("sql server management studio", StringComparison.OrdinalIgnoreCase) >= 0 ||
              name.Equals("ssms", StringComparison.OrdinalIgnoreCase) ||
              name.StartsWith("ssms ", StringComparison.OrdinalIgnoreCase);
    }

    private static string? TryResolveShortcutTarget(string shortcutPath)
    {
        object? shell = null;
        object? shortcut = null;
        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType is null)
            {
                return null;
            }

            shell = Activator.CreateInstance(shellType);
            shortcut = shellType.InvokeMember(
                "CreateShortcut",
                BindingFlags.InvokeMethod,
                binder: null,
                target: shell,
                args: [shortcutPath]);
            var targetPath = shortcut?.GetType().InvokeMember(
                "TargetPath",
                BindingFlags.GetProperty,
                binder: null,
                target: shortcut,
                args: null) as string;
            return string.IsNullOrWhiteSpace(targetPath)
                ? null
                : Environment.ExpandEnvironmentVariables(targetPath);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or ArgumentException)
        {
            return null;
        }
        finally
        {
            if (shortcut is not null && Marshal.IsComObject(shortcut))
            {
                Marshal.FinalReleaseComObject(shortcut);
            }

            if (shell is not null && Marshal.IsComObject(shell))
            {
                Marshal.FinalReleaseComObject(shell);
            }
        }
    }
}
