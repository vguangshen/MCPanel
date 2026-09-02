using System.Diagnostics;
using System.IO;
using System.Net.Http;
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

public sealed record CleanupStorageUsage(long ProductCacheBytes);

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
        get
        {
            var root = Path.Combine(AppContext.BaseDirectory, "StoreData");
            Directory.CreateDirectory(root);
            return root;
        }
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

    public string GetNavicatPath()
    {
        return LoadSettings().NavicatPath;
    }

    public string GetNavicatPathDisplay()
    {
        var installation = GetNavicatInstallation();
        return installation is null
            ? $"未安装；面板安装目录：{ComponentPaths.NavicatLiteRoot}"
            : $"{installation.SourceText}：{installation.ExecutablePath}";
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

    public void ClearNavicatPath()
    {
        var settings = LoadSettings();
        settings.NavicatPath = string.Empty;
        SaveSettings(settings);
    }

    public string GetSqlManagementStudioPath()
    {
        return LoadSettings().SqlManagementStudioPath;
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

    public void ClearSqlManagementStudioPath()
    {
        var settings = LoadSettings();
        settings.SqlManagementStudioPath = string.Empty;
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

    public void OpenProductDirectory() => OpenDirectory(Path.Combine(AppContext.BaseDirectory, "web"));

    public void OpenLogDirectory() => OpenDirectory(Path.Combine(StoreDataRoot, "Work"));

    public void ClearProductCache()
    {
        DeleteDirectory(Path.Combine(AppContext.BaseDirectory, "Cache"));
        DeleteDirectory(ComponentPaths.LegacyProductIconsRoot);
    }

    public Task<CleanupStorageUsage> GetCleanupStorageUsageAsync(CancellationToken cancellationToken = default)
    {
        var cacheDirectories = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Cache"),
            ComponentPaths.LegacyProductIconsRoot
        };
        return Task.Run(() => new CleanupStorageUsage(
            CalculateDirectoryBytes(cacheDirectories, cancellationToken)), cancellationToken);
    }

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

    private static long CalculateDirectoryBytes(IEnumerable<string> directories, CancellationToken cancellationToken)
    {
        long total = 0;
        foreach (var root in directories.Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root)) continue;
            var pending = new Stack<string>();
            pending.Push(root);
            while (pending.Count > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = pending.Pop();
                try
                {
                    foreach (var file in Directory.EnumerateFiles(directory))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        try { total = checked(total + new FileInfo(file).Length); }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                    foreach (var child in Directory.EnumerateDirectories(directory))
                    {
                        try
                        {
                            if ((File.GetAttributes(child) & FileAttributes.ReparsePoint) == 0) pending.Push(child);
                        }
                        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                        {
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }
        }
        return total;
    }

    public string NavicatInstallRoot => ComponentPaths.NavicatLiteRoot;

    public string SsmsInstallRoot => ComponentPaths.SsmsRoot;

    public SsmsReleaseRecommendation GetSqlManagementStudioRecommendation() =>
        SsmsReleaseCatalog.ResolveForInstalledSqlServer();

    public string GetSqlManagementStudioRecommendationText() =>
        GetSqlManagementStudioRecommendation().Summary;

    internal DatabaseToolInstallation? GetNavicatInstallation() =>
        DatabaseToolLocator.FindNavicat(LoadSettings().NavicatPath);

    internal DatabaseToolInstallation? GetSqlManagementStudioInstallation() =>
        DatabaseToolLocator.FindSqlServerManagementStudio(LoadSettings().SqlManagementStudioPath);

    public bool IsNavicatAvailable() => GetNavicatInstallation() is not null;

    public bool IsSqlManagementStudioAvailable() => GetSqlManagementStudioInstallation() is not null;

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
            Path.Combine(StoreDataRoot, "Work"),
            progress,
            cancellationToken);

    public Task<string> UninstallNavicatAsync(CancellationToken cancellationToken = default)
    {
        var installation = GetNavicatInstallation() ??
                           throw new FileNotFoundException("未检测到可卸载的 Navicat。请先刷新或配置正确路径。");
        return DatabaseToolUninstaller.UninstallAsync(
            installation,
            Path.Combine(StoreDataRoot, "Work"),
            cancellationToken);
    }

    public void OpenSqlManagementStudio() => StartTool(RequireSqlManagementStudioPath());

    public async Task EnsureSqlServerLocalCertificateAsync(CancellationToken cancellationToken = default)
    {
        var workDirectory = Path.Combine(StoreDataRoot, "Work");
        Directory.CreateDirectory(workDirectory);
        var script = Path.Combine(workDirectory, "configure-sql-local-certificate.ps1");
        var log = Path.Combine(workDirectory, "configure-sql-local-certificate.log");
        var scriptText = $$"""
            $ErrorActionPreference='Stop'
            $log='{{EscapePowerShellLiteral(log)}}'
            Start-Transcript -Path $log -Force | Out-Null
            try {
                $instance=(Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -ErrorAction Stop).MSSQLSERVER
                if ([string]::IsNullOrWhiteSpace($instance)) { throw '未找到 MSSQLSERVER 默认实例。' }
                $subject='CN=localhost'
                $cert=Get-ChildItem Cert:\LocalMachine\My | Where-Object {
                    $_.Subject -eq $subject -and $_.FriendlyName -eq 'MCPanel SQL Server Local Certificate' -and $_.NotAfter -gt (Get-Date).AddDays(30)
                } | Sort-Object NotAfter -Descending | Select-Object -First 1
                if (!$cert) {
                    $cert=New-SelfSignedCertificate -Subject $subject -DnsName @('localhost',$env:COMPUTERNAME) -CertStoreLocation 'Cert:\LocalMachine\My' -FriendlyName 'MCPanel SQL Server Local Certificate' -KeyAlgorithm RSA -KeyLength 2048 -HashAlgorithm SHA256 -KeyExportPolicy Exportable -KeySpec KeyExchange -Provider 'Microsoft RSA SChannel Cryptographic Provider' -NotAfter (Get-Date).AddYears(5)
                }
                if (!(Get-ChildItem "Cert:\LocalMachine\Root\$($cert.Thumbprint)" -ErrorAction SilentlyContinue)) {
                    $cer=Join-Path $env:TEMP 'mcpanel-sql-local.cer'
                    Export-Certificate -Cert $cert -FilePath $cer -Force | Out-Null
                    Import-Certificate -FilePath $cer -CertStoreLocation 'Cert:\LocalMachine\Root' | Out-Null
                    Remove-Item $cer -Force -ErrorAction SilentlyContinue
                }
                $keyName=$cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
                $keyPath=Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $keyName)
                & icacls.exe $keyPath /grant '*S-1-5-20:R' | Out-Null
                if ($LASTEXITCODE -ne 0) { throw '无法授权 SQL Server 服务读取证书私钥。' }
                $reg="HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instance\MSSQLServer\SuperSocketNetLib"
                $target=$cert.Thumbprint.ToLowerInvariant()
                $current=(Get-ItemProperty $reg -Name Certificate -ErrorAction SilentlyContinue).Certificate
                if ($current -ne $target) {
                    Set-ItemProperty -Path $reg -Name Certificate -Value $target
                    Set-ItemProperty -Path $reg -Name ForceEncryption -Value 0
                    Restart-Service MSSQLSERVER -Force
                    (Get-Service MSSQLSERVER).WaitForStatus('Running',[TimeSpan]::FromSeconds(90))
                }
                Stop-Transcript | Out-Null
                exit 0
            } catch {
                Write-Error $_
                try { Stop-Transcript | Out-Null } catch { }
                exit 1
            }
            """;
        await FileCompat.WriteAllTextAsync(script, scriptText, new UTF8Encoding(true), cancellationToken);

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{script}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });
        if (process is null)
        {
            throw new InvalidOperationException("无法启动 SQL Server 证书配置程序。");
        }

        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode != 0)
        {
            var details = File.Exists(log) ? Environment.NewLine + string.Join(Environment.NewLine, File.ReadLines(log).TakeLast(12)) : string.Empty;
            throw new InvalidOperationException("SQL Server 本机证书配置失败。" + details);
        }

        SqlServerCredentialStore.Save(SqlServerCredentialStore.Load() with { Host = "localhost" });
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
        if (release.MajorVersion <= 18)
        {
            // SSMS 18 uses the older Burn bootstrapper. Its documented
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

        if (!File.Exists(installer) || new FileInfo(installer).Length < 1024 * 1024)
        {
            progress?.Invoke(new InstallProgress(0, $"正在连接微软下载服务器，准备下载 {release.DisplayName}..."));
            using var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
            using var response = await client.GetAsync(
                release.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength;
            var partial = installer + ".download";
            if (File.Exists(partial))
            {
                File.Delete(partial);
            }

            long received = 0;
            var startedAt = DateTime.UtcNow;
            try
            {
                using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
                using (var file = new FileStream(
                           partial,
                           FileMode.Create,
                           FileAccess.Write,
                           FileShare.None,
                           1024 * 128,
                           useAsync: true))
                {
                    var buffer = new byte[1024 * 128];
                    var lastProgressAt = DateTime.MinValue;
                    while (true)
                    {
                        var read = await stream.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await file.WriteAsync(buffer, 0, read, cancellationToken);
                        received += read;
                        var now = DateTime.UtcNow;
                        if (now - lastProgressAt >= TimeSpan.FromMilliseconds(250) ||
                            (total is > 0 && received == total.Value))
                        {
                            var elapsedSeconds = Math.Max((now - startedAt).TotalSeconds, 0.001);
                            var speed = received / elapsedSeconds;
                            var percent = total is > 0 ? received * 70d / total.Value : 0d;
                            var downloadedText = total is > 0
                                ? $"{FormatStorageSize(received)} / {FormatStorageSize(total.Value)}"
                                : FormatStorageSize(received);
                            progress?.Invoke(new InstallProgress(
                                Compat.Clamp(percent, 0, 70),
                                $"正在下载 {release.DisplayName}：{downloadedText}，速度 {FormatTransferRate(speed)}"));
                            lastProgressAt = now;
                        }
                    }

                    await file.FlushAsync(cancellationToken);
                }

                if (received <= 0)
                {
                    throw new InvalidDataException("SQL Server Management Studio 下载文件为空。");
                }

                if (total is > 0 && received != total.Value)
                {
                    throw new InvalidDataException($"SQL Server Management Studio 下载不完整：应为 {total.Value} 字节，实际 {received} 字节。");
                }

                FileCompat.Move(partial, installer, overwrite: true);
            }
            catch
            {
                if (File.Exists(partial))
                {
                    File.Delete(partial);
                }

                throw;
            }

            progress?.Invoke(new InstallProgress(
                70,
                $"{release.DisplayName} 下载完成，平均速度 {FormatTransferRate(received / Math.Max((DateTime.UtcNow - startedAt).TotalSeconds, 0.001))}，正在启动安装..."));
        }
        else
        {
            progress?.Invoke(new InstallProgress(70, $"已找到 {release.DisplayName} 缓存安装包，正在启动安装..."));
        }

        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = installer,
            Arguments = BuildSsmsInstallArguments(release),
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        });

        if (process is null)
        {
            throw new InvalidOperationException("无法启动 SQL Server Management Studio 安装器。");
        }

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
            Path.Combine(StoreDataRoot, "Work"),
            cancellationToken);
    }

    private static string FormatTransferRate(double bytesPerSecond)
    {
        if (bytesPerSecond >= 1024 * 1024)
        {
            return $"{bytesPerSecond / 1024 / 1024:0.0} MB/s";
        }

        if (bytesPerSecond >= 1024)
        {
            return $"{bytesPerSecond / 1024:0} KB/s";
        }

        return $"{bytesPerSecond:0} B/s";
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
            WriteSettingsAtomic(_settingsPath, json);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            WriteSettingsAtomic(_fallbackSettingsPath, json);
        }
    }

    private static void WriteSettingsAtomic(string path, string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json, new UTF8Encoding(false));
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (File.Exists(path)) File.Replace(temporary, path, null);
                    else File.Move(temporary, path);
                    break;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(40 * (attempt + 1));
                }
            }
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static string GetExecutablePath()
    {
        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName ?? Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
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

    private static void DeleteDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var baseDirectory = Path.GetFullPath(AppContext.BaseDirectory);
        if (!fullPath.StartsWith(baseDirectory, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("清理目录不在软件目录下，已取消。");
        }

        if (Directory.Exists(fullPath))
        {
            Directory.Delete(fullPath, recursive: true);
        }
    }

    private string RequireSqlManagementStudioPath()
    {
        return FindSqlManagementStudioPath() ??
               throw new FileNotFoundException("未找到 SQL Server Management Studio。");
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
            WorkingDirectory = Path.GetDirectoryName(fileName) ?? AppContext.BaseDirectory,
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
