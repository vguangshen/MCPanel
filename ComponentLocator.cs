using System.IO;

namespace MCPanel;

[Flags]
internal enum TomcatComponentRequirements
{
    StartupScript = 1,
    ServerXml = 2,
    WebAppsDirectory = 4,
    CatalinaScript = 8
}

/// <summary>
/// Resolves installed component roots from the current and legacy layouts.
/// A locator instance caches the result of each probe so one runtime refresh
/// can share the same filesystem observations across state and path queries.
/// </summary>
internal sealed class ComponentLocator
{
    private const TomcatComponentRequirements DefaultTomcatRequirements =
        TomcatComponentRequirements.StartupScript | TomcatComponentRequirements.ServerXml;

    private readonly Dictionary<string, string?> _nginxExecutables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _mysqlExecutables = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _mysqlRoots = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _tomcatRoots = new(StringComparer.OrdinalIgnoreCase);
    private string? _allMySqlExecutable;
    private bool _allMySqlExecutableResolved;

    public string? FindNginxExecutable()
    {
        foreach (var root in ComponentPaths.NginxSearchRoots)
        {
            var executable = FindNginxExecutable(root);
            if (executable is not null)
            {
                return executable;
            }
        }

        return null;
    }

    public string? FindNginxExecutable(string root)
    {
        var key = NormalizePath(root);
        if (_nginxExecutables.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = NginxRuntimeManager.FindNginxExe(root);
        _nginxExecutables[key] = result;
        return result;
    }

    public string? FindTomcatRoot(TomcatComponentRequirements requirements = DefaultTomcatRequirements)
    {
        foreach (var root in ComponentPaths.TomcatSearchRoots)
        {
            var result = FindTomcatRoot(root, requirements);
            if (result is not null)
            {
                return result;
            }
        }

        return null;
    }

    public string? FindTomcatRoot(
        string root,
        TomcatComponentRequirements requirements = DefaultTomcatRequirements)
    {
        var key = ((int)requirements).ToString(System.Globalization.CultureInfo.InvariantCulture) +
                  ":" +
                  NormalizePath(root);
        if (_tomcatRoots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = FindTomcatRootCore(root, requirements);
        _tomcatRoots[key] = result;
        return result;
    }

    public string? FindMySqlExecutable()
    {
        if (_allMySqlExecutableResolved)
        {
            return _allMySqlExecutable;
        }

        foreach (var root in ComponentPaths.MySqlSearchRoots)
        {
            var executable = FindMySqlExecutable(root);
            if (executable is not null)
            {
                _allMySqlExecutable = executable;
                break;
            }
        }

        _allMySqlExecutableResolved = true;
        return _allMySqlExecutable;
    }

    public string? FindMySqlExecutable(string root, string? excludedRoot = null)
    {
        var key = NormalizePath(root) + "\0" + NormalizePath(excludedRoot);
        if (string.IsNullOrWhiteSpace(excludedRoot) &&
            _mysqlExecutables.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var result = FindMySqlExecutableCore(root, excludedRoot);
        if (string.IsNullOrWhiteSpace(excludedRoot))
        {
            _mysqlExecutables[key] = result;
        }

        return result;
    }

    public string? FindMySqlRoot(string? serviceExecutable = null)
    {
        var key = NormalizePath(serviceExecutable);
        if (_mysqlRoots.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var executable = !string.IsNullOrWhiteSpace(serviceExecutable) && File.Exists(serviceExecutable)
            ? serviceExecutable
            : FindMySqlExecutable() ?? serviceExecutable;
        var result = GetInstallationRoot(executable);
        _mysqlRoots[key] = result;
        return result;
    }

    private static string? FindTomcatRootCore(string root, TomcatComponentRequirements requirements)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        try
        {
            var known = Path.Combine(root, "apache-tomcat-8.5.57");
            if (MatchesTomcatRequirements(known, requirements))
            {
                return known;
            }

            return Directory.EnumerateDirectories(root, "apache-tomcat-*", SearchOption.TopDirectoryOnly)
                .Concat(Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault(path => MatchesTomcatRequirements(path, requirements));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool MatchesTomcatRequirements(string root, TomcatComponentRequirements requirements)
    {
        if (requirements.HasFlag(TomcatComponentRequirements.StartupScript) &&
            !File.Exists(Path.Combine(root, "bin", "startup.bat")))
        {
            return false;
        }

        if (requirements.HasFlag(TomcatComponentRequirements.ServerXml) &&
            !File.Exists(Path.Combine(root, "conf", "server.xml")))
        {
            return false;
        }

        if (requirements.HasFlag(TomcatComponentRequirements.WebAppsDirectory) &&
            !Directory.Exists(Path.Combine(root, "webapps")))
        {
            return false;
        }

        return !requirements.HasFlag(TomcatComponentRequirements.CatalinaScript) ||
               File.Exists(Path.Combine(root, "bin", "catalina.bat"));
    }

    private static string? FindMySqlExecutableCore(string root, string? excludedRoot)
    {
        try
        {
            var candidates = new List<string>
            {
                Path.Combine(root, "bin", "mysql.exe"),
                Path.Combine(root, "mysql", "bin", "mysql.exe")
            };

            if (Directory.Exists(root))
            {
                candidates.AddRange(Directory.EnumerateDirectories(root, "*", SearchOption.TopDirectoryOnly)
                    .Select(directory => Path.Combine(directory, "bin", "mysql.exe")));
            }

            return candidates.FirstOrDefault(path =>
                File.Exists(path) && !IsUnderDirectory(path, excludedRoot));
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool IsUnderDirectory(string path, string? directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            return false;
        }

        var candidate = NormalizePath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var excluded = NormalizePath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        return candidate.StartsWith(excluded, StringComparison.OrdinalIgnoreCase);
    }

    private static string? GetInstallationRoot(string? executable)
    {
        if (string.IsNullOrWhiteSpace(executable))
        {
            return null;
        }

        var executableDirectory = Path.GetDirectoryName(executable);
        return string.IsNullOrWhiteSpace(executableDirectory)
            ? null
            : Directory.GetParent(executableDirectory)?.FullName;
    }

    private static string NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return string.Empty;
        }

        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path!.Trim();
        }
    }
}
