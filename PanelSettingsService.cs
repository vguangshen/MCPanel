using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MCPanel;

public enum PanelThemeMode
{
    System,
    Light,
    Dark
}

public sealed class PanelSettingsService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MCPanel";
    private readonly string _settingsPath;
    private readonly string _fallbackSettingsPath;
    private readonly NavicatPremiumLiteInstaller _navicatInstaller = new();

    public PanelSettingsService()
    {
        _settingsPath = Path.Combine(StoreDataRoot, "panel-settings.json");
        _fallbackSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCPanel",
            "panel-settings.json");
    }

    public string StoreDataRoot
    {
        get => ComponentPaths.StoreDataRoot;
    }

    public bool IsStartupEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
        return key?.GetValue(RunValueName) is string value &&
               value.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase);
    }

    public void SetStartupEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true) ??
                        Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);

        if (enabled)
        {
            key.SetValue(RunValueName, BuildStartupCommand(GetExecutablePath()));
            return;
        }

        key.DeleteValue(RunValueName, throwOnMissingValue: false);
    }

    public void EnsureStartupRegistrationUsesTrayMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(RunValueName) is not string currentCommand ||
            !currentCommand.Contains(GetExecutablePath(), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var expectedCommand = BuildStartupCommand(GetExecutablePath());
        if (!string.Equals(currentCommand, expectedCommand, StringComparison.Ordinal))
        {
            key.SetValue(RunValueName, expectedCommand);
        }
    }

    internal static string BuildStartupCommand(string executablePath) =>
        $"\"{executablePath}\" {ApplicationLaunchMode.TrayArgument}";

    public PanelThemeMode GetThemeMode()
    {
        var settings = LoadSettings();
        return Enum.TryParse<PanelThemeMode>(settings.ThemeMode, ignoreCase: true, out var mode)
            ? mode
            : PanelThemeMode.System;
    }

    public void SetThemeMode(PanelThemeMode mode)
    {
        var settings = LoadSettings();
        settings.ThemeMode = mode.ToString();
        SaveSettings(settings);
    }

    public void SetNavicatPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !DatabaseToolLocator.IsExpectedExecutable(DatabaseToolKind.Navicat, path))
        {
            throw new FileNotFoundException("请选择有效的 Navicat 可执行文件 navicat.exe。", path);
        }

        var settings = LoadSettings();
        settings.NavicatPath = Path.GetFullPath(path);
        SaveSettings(settings);
    }

    public void SetSqlManagementStudioPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !File.Exists(path) ||
            !DatabaseToolLocator.IsExpectedExecutable(DatabaseToolKind.SqlServerManagementStudio, path))
        {
            throw new FileNotFoundException("请选择有效的 SQL Server Management Studio 可执行文件 Ssms.exe。", path);
        }

        var settings = LoadSettings();
        settings.SqlManagementStudioPath = Path.GetFullPath(path);
        SaveSettings(settings);
    }

    public void OpenStoreDataDirectory() => OpenDirectory(StoreDataRoot);

    internal void OpenDatabaseToolDirectory(DatabaseToolKind kind)
    {
        var installation = kind == DatabaseToolKind.Navicat
            ? GetNavicatInstallation()
            : GetSqlManagementStudioInstallation();
        var directory = installation is null
            ? kind == DatabaseToolKind.Navicat ? NavicatInstallRoot : SsmsInstallRoot
            : Path.GetDirectoryName(installation.ExecutablePath);

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new DirectoryNotFoundException("未找到数据库工具对应的安装目录。请先配置有效的可执行文件路径。");
        }

        OpenDirectory(directory);
    }

    public void OpenRuntimeDirectory() => OpenDirectory(ComponentPaths.ApplicationRoot);

    public void OpenProductDirectory() => OpenDirectory(ComponentPaths.WebRoot);

    public void OpenLogDirectory() => OpenDirectory(ComponentPaths.WorkRoot);

    public static string FormatStorageSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return unit == 0 ? $"{value:0} {units[unit]}" : $"{value:0.##} {units[unit]}";
    }

    public string NavicatInstallRoot => ComponentPaths.NavicatLiteRoot;

    public string SsmsInstallRoot => ComponentPaths.SsmsRoot;

    public SsmsReleaseRecommendation GetSqlManagementStudioRecommendation() =>
        SsmsReleaseCatalog.ResolveForInstalledSqlServer();

    internal DatabaseToolInstallation? GetNavicatInstallation() =>
        DatabaseToolLocator.FindNavicat(LoadSettings().NavicatPath);

    internal DatabaseToolInstallation? GetSqlManagementStudioInstallation() =>
        DatabaseToolLocator.FindSqlServerManagementStudio(LoadSettings().SqlManagementStudioPath);

    public bool TryOpenNavicatForMySql()
    {
        var path = FindNavicatPath();
        if (path is null)
        {
            return false;
        }

        StartTool(path);
        return true;
    }

    public Task InstallNavicatPremiumLiteAsync(
        Action<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default) =>
        _navicatInstaller.InstallAsync(
            ComponentPaths.ToolsRoot,
            ComponentPaths.WorkRoot,
            progress,
            cancellationToken);

    public Task<string> UninstallNavicatAsync(CancellationToken cancellationToken = default)
    {
        var installation = GetNavicatInstallation() ??
                           throw new FileNotFoundException("未检测到可卸载的 Navicat。请先刷新或配置正确路径。");
        return DatabaseToolUninstaller.UninstallAsync(
            installation,
            ComponentPaths.WorkRoot,
            cancellationToken);
    }

    public async Task EnsureSqlServerLocalCertificateAsync(CancellationToken cancellationToken = default)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var script = Path.Combine(workDirectory, "configure-sql-local-certificate.ps1");
        var log = Path.Combine(workDirectory, "configure-sql-local-certificate.log");
        var scriptText = BuildSqlServerLocalCertificateScript(log);
        await FileCompat.WriteAllTextAsync(script, scriptText, new UTF8Encoding(true), cancellationToken);

        using var process = ProcessRunner.StartPowerShellFile(script, elevated: true);

        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode != 0)
        {
            var details = File.Exists(log) ? Environment.NewLine + string.Join(Environment.NewLine, File.ReadLines(log).TakeLast(12)) : string.Empty;
            throw new InvalidOperationException("SQL Server 本机证书配置失败。" + details);
        }

        SqlServerCredentialStore.Save(SqlServerCredentialStore.Load() with { Host = "localhost" });
    }

    internal static string BuildSqlServerLocalCertificateScript(string logPath)
    {
        using var stream = typeof(PanelSettingsService).Assembly.GetManifestResourceStream("MCPanel.SqlServerCertificate.ps1")
            ?? throw new InvalidOperationException("缺少 SQL Server 证书配置脚本。");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return "$log='" + EscapePowerShellLiteral(logPath) + "'" + Environment.NewLine + reader.ReadToEnd();
    }

    public bool TryOpenSqlManagementStudioForSqlServer()
    {
        var path = FindSqlManagementStudioPath();
        if (path is null)
        {
            return false;
        }

        var credentials = SqlServerCredentialStore.Load();
        StartTool(path, BuildSqlServerArguments(credentials));
        return true;
    }

    internal static string BuildSqlServerArguments(SqlServerDefaultCredentials credentials)
    {
        var serverTarget = SqlServerCredentialStore.GetServerTarget(credentials);
        // Keep the original Windows Authentication behavior without -E. SSMS
        // 2008 through SSMS 22 use Windows Authentication when -U is omitted;
        // -E is rejected by current SSMS and -P was removed in SSMS 18.
        return $"-nosplash -S {QuoteCommandLineArgument(serverTarget)}";
    }

    internal static string BuildSsmsInstallArguments(SsmsReleaseDefinition release)
    {
        if (release.MajorVersion <= 20)
        {
            // SSMS 18 through 20 use the older Burn bootstrapper. Its documented
            // switches are slash-prefixed and the install root is a Burn
            // overridable variable, not the SSMS 21/22 --installPath option.
            return string.Join(" ",
                "/quiet",
                "/norestart",
                "SSMSInstallRoot=" + Compat.QuoteCommandLineArgument(ComponentPaths.SsmsRoot));
        }

        return string.Join(" ",
            "--quiet",
            "--wait",
            "--norestart",
            "--channelUri",
            Compat.QuoteCommandLineArgument($"https://aka.ms/ssms/{release.MajorVersion}/release/channel"),
            "--installPath",
            Compat.QuoteCommandLineArgument(ComponentPaths.SsmsRoot),
            "--path",
            Compat.QuoteCommandLineArgument($"cache={ComponentPaths.SsmsCacheRoot}"));
    }

    private static string QuoteCommandLineArgument(string value) =>
        $"\"{value.Replace("\"", "\\\"", StringComparison.Ordinal)}\"";

    public async Task InstallSqlManagementStudioAsync(
        Action<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var recommendation = SsmsReleaseCatalog.ResolveForInstalledSqlServer();
        var release = recommendation.Release;
        var installerDirectory = Path.Combine(ComponentPaths.ToolsRoot, "Installers");
        Directory.CreateDirectory(installerDirectory);
        Directory.CreateDirectory(ComponentPaths.SsmsCacheRoot);
        var installer = Path.Combine(installerDirectory, release.CacheFileName);

        // Reuse the component downloader's TLS setup, package/cache validation,
        // atomic downloads and actionable, sanitized HTTP diagnostics.
        using (var downloader = new EnvironmentInstaller())
        {
            await downloader.DownloadCoreAsync(release.DownloadUrl, installerDirectory,
                release.CacheFileName, progress ?? (_ => { }), 0, 70, cancellationToken);
        }

        using var process = ProcessRunner.Start(
            installer,
            BuildSsmsInstallArguments(release),
            Path.GetDirectoryName(installer),
            elevated: true);

        progress?.Invoke(new InstallProgress(70, "正在静默安装 SQL Server Management Studio，请稍候..."));
        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode is not 0 and not 3010)
        {
            throw new InvalidOperationException($"SQL Server Management Studio 安装失败，退出码：{process.ExitCode}。");
        }

        progress?.Invoke(new InstallProgress(100, $"{release.DisplayName} 安装完成。{recommendation.Summary}"));
    }

    public Task<string> UninstallSqlManagementStudioAsync(CancellationToken cancellationToken = default)
    {
        var installation = GetSqlManagementStudioInstallation() ??
                           throw new FileNotFoundException("未检测到可卸载的 SQL Server Management Studio。请先刷新或配置正确路径。");
        return DatabaseToolUninstaller.UninstallAsync(
            installation,
            ComponentPaths.WorkRoot,
            cancellationToken);
    }

    private PanelSettings LoadSettings()
    {
        var candidates = new[] { _settingsPath, _fallbackSettingsPath }
            .Where(File.Exists)
            .OrderByDescending(path => File.GetLastWriteTimeUtc(path));
        foreach (var path in candidates)
        {
            try
            {
                var json = File.ReadAllText(path);
                var settings = JsonSerializer.Deserialize<PanelSettings>(json);
                if (settings is not null)
                {
                    return settings;
                }
            }
            catch
            {
                // A partially synchronized settings file must not prevent startup.
            }
        }
        return new PanelSettings();
    }

    private void SaveSettings(PanelSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
        try
        {
            AtomicFile.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            AtomicFile.WriteAllText(_fallbackSettingsPath, json);
        }
    }

    private static string GetExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe");
    }

    private static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    private static void OpenDirectory(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = path,
            UseShellExecute = true
        });
    }

    private string? FindNavicatPath()
    {
        return GetNavicatInstallation()?.ExecutablePath;
    }

    private string? FindSqlManagementStudioPath()
    {
        return GetSqlManagementStudioInstallation()?.ExecutablePath;
    }

    private static void StartTool(string fileName, string arguments = "")
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? ComponentPaths.ApplicationRoot,
            UseShellExecute = true
        });
    }

    private sealed class PanelSettings
    {
        public string ThemeMode { get; set; } = PanelThemeMode.System.ToString();
        public string NavicatPath { get; set; } = string.Empty;
        public string SqlManagementStudioPath { get; set; } = string.Empty;
    }
}
