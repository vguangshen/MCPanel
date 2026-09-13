using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public enum InstallProgressStage
{
    Preparing,
    Downloading,
    Installing,
    Completed
}

public sealed record InstallProgress(
    double Percent,
    string Message,
    InstallProgressStage Stage = InstallProgressStage.Installing,
    double? StagePercent = null,
    string? SpeedText = null);

internal sealed class InstallRestartRequiredException(string message) : InvalidOperationException(message);

public sealed class EnvironmentInstaller : IDisposable
{
    private readonly HttpClient _httpClient;

    public EnvironmentInstaller()
    {
        // The elevated worker targets .NET Framework 4.6.2. Enable the TLS
        // versions required by current Microsoft/CDN endpoints and the legacy
        // vendor store without replacing certificate validation.
        ServicePointManager.SecurityProtocol |=
            SecurityProtocolType.Tls |
            (SecurityProtocolType)768 |
            (SecurityProtocolType)3072;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("MCPanel/1.1");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    internal sealed record SqlServerInstallPlan(
        string DisplayName,
        string InstallerFileName,
        string? DownloadUrl,
        int? ProductMajor,
        string? MediaDirectoryName,
        string? ExtractDirectoryName,
        bool UsesSseiInstaller,
        string MediaPackageSearchPattern = "SQLEXPR*.exe",
        string MediaType = "Core");

    internal static string SqlServerInstallRestartMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "sqlserver-install-restart.pending");

    internal static bool HasSqlServerInstallContinuation => File.Exists(SqlServerInstallRestartMarkerPath);

    internal static string SqlServerInstallContinuationMessage =>
        "已写入 4KB 扇区兼容设置，请重启设备后点击“继续安装”。";

    internal static string IisInstallRestartMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "iis-install-restart.pending");

    internal static bool HasIisInstallContinuation => File.Exists(IisInstallRestartMarkerPath);

    internal static string IisInstallContinuationMessage =>
        "IIS 或 URL Rewrite 安装需要重启 Windows 才能继续。请重启设备后再次点击“继续安装”。";

    public async Task InstallAsync(EnvironmentItem item, Action<InstallProgress> progress, CancellationToken cancellationToken = default)
    {
        var downloads = EnvironmentDownloadSettings.Load();
        switch (item.Kind)
        {
            case EnvironmentKind.Iis:
                await InstallIisAsync(downloads, progress, cancellationToken);
                break;
            case EnvironmentKind.Nginx:
                await InstallNginxAsync(downloads, progress, cancellationToken);
                break;
            case EnvironmentKind.MySql:
                await InstallMySqlAsync(
                    MySqlReleaseCatalog.Resolve(item.SelectedMySqlReleaseId, downloads.MySqlPackageUrl),
                    progress,
                    cancellationToken);
                break;
            case EnvironmentKind.SqlServer:
                await InstallSqlServerAsync(
                    downloads,
                    item.SelectedSqlServerReleaseId,
                    progress,
                    cancellationToken);
                break;
            case EnvironmentKind.Tomcat:
                await InstallTomcatAsync(downloads, progress, cancellationToken);
                break;
        }
    }

    private async Task InstallIisAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        TryDeleteFile(IisPendingUninstallMarker);
        TryDeleteFile(IisUninstalledMarker);
        progress(new InstallProgress(5, "正在下载 URL Rewrite 组件...", InstallProgressStage.Downloading, 0));
        var temp = GetTempDirectory();
        var rewriteMsi = await DownloadAbsoluteFileAsync(downloads.IisUrlRewriteUrl, GetPackageDirectory(), "URLRewrite.msi", progress, 5, 20, cancellationToken);

        progress(InstallingProgress(25, "正在生成 IIS 安装脚本...", 0));
        var script = Path.Combine(temp, "install-iis.ps1");
        await FileCompat.WriteAllTextAsync(script, BuildIisScript(rewriteMsi), new UTF8Encoding(true), cancellationToken);

        progress(InstallingProgress(35, "正在启用 IIS 组件...", 8));
        await RunElevatedPowerShellAsync(script, progress, 10, 95, cancellationToken, requireExistingAdministrator: true);
        TryDeleteFile(IisInstallRestartMarkerPath);
        TryDeleteFile(IisPendingUninstallMarker);
        TryDeleteFile(IisUninstalledMarker);
        progress(InstallingProgress(100, "IIS 安装和基础配置完成。"));
    }

    private async Task InstallTomcatAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var locator = new ComponentLocator();
        var root = SelectTomcatInstallRoot(locator);
        Directory.CreateDirectory(root);
        var tomcatRoot = locator.FindTomcatRoot(root);
        if (tomcatRoot is null || !File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat")))
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.TomcatPackageUrl, GetPackageDirectory(), "apache-tomcat-8.5.57.zip", progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Tomcat 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            tomcatRoot = new ComponentLocator().FindTomcatRoot(root) ?? Path.Combine(root, "apache-tomcat-8.5.57");
        }
        else
        {
            progress(InstallingProgress(70, "已找到现有 Tomcat，正在检查并修复配置...", 0));
        }
        ConfigureTomcat(tomcatRoot);

        var startup = Path.Combine(tomcatRoot, "bin", "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("Tomcat 解压完成，但未找到启动脚本。", startup);
        }

        progress(InstallingProgress(84, "正在清理旧 Tomcat 后台服务并准备可见控制台...", 58));
        TomcatProductStartupManager.RemoveRegistration();
        await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);

        if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
        {
            try { TomcatWindowsServiceManager.Stop(); } catch { }
            try { TomcatWindowsServiceManager.Delete(); } catch { }
        }

        progress(InstallingProgress(92, "正在以标准 start 模式打开 Tomcat Server CMD 控制台...", 78));
        EnvironmentRuntimeService.LaunchTomcatStartConsole(tomcatRoot);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并已按标准 start 模式在可见 CMD 控制台中启动；后续启动与重启不再通过 Windows Service 隐藏运行。"));
    }
    private async Task InstallNginxAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var root = ComponentPaths.SelectNginxInstallRoot();
        Directory.CreateDirectory(root);

        var locator = new ComponentLocator();
        var nginxExe = locator.FindNginxExecutable(root);
        if (nginxExe is null)
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.NginxPackageUrl, GetPackageDirectory(), "nginx-1.14.2.zip", progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Nginx 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            var nginxRoot = FindDirectory(root, NginxRuntimeManager.VersionDirectoryName) ?? Path.Combine(root, NginxRuntimeManager.VersionDirectoryName);
            nginxExe = Path.Combine(nginxRoot, "nginx.exe");
        }
        else
        {
            progress(InstallingProgress(70, "已找到现有 Nginx，正在检查并修复配置...", 0));
        }
        if (!File.Exists(nginxExe))
        {
            nginxExe = Directory.GetFiles(root, "nginx.exe", SearchOption.AllDirectories).FirstOrDefault() ?? nginxExe;
        }

        if (!File.Exists(nginxExe))
        {
            throw new FileNotFoundException("Nginx 解压完成，但未找到 nginx.exe。", nginxExe);
        }

        var nginxWorkDirectory = Path.GetDirectoryName(nginxExe)!;
        progress(InstallingProgress(76, "正在停止旧 Nginx 进程，准备注册 Windows 服务...", 20));
        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxWorkDirectory))
            {
                throw new InvalidOperationException(
                    $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已存在，但未指向当前 Nginx 目录，已停止安装以保护现有服务。");
            }

            NginxWindowsServiceManager.Stop();
        }

        NginxRuntimeManager.KillProcessesUnderRoot(nginxWorkDirectory);
        progress(InstallingProgress(82, "正在生成 Nginx 监听配置...", 32));

        progress(InstallingProgress(88, "正在校验 Nginx 配置和端口...", 48));
        var listenPort = await ConfigureAndTestNginxAsync(nginxExe, nginxWorkDirectory, cancellationToken);

        var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(serviceExecutable) || !File.Exists(serviceExecutable))
        {
            throw new InvalidOperationException("无法定位 MCPanel 主程序，不能注册 Nginx Windows 服务。");
        }

        progress(InstallingProgress(92, "正在注册 Nginx Windows 服务...", 64));
        NginxWindowsServiceManager.EnsureRegistered(serviceExecutable!, nginxWorkDirectory);
        progress(InstallingProgress(95, $"正在启动 Nginx Windows 服务，监听端口 {listenPort}...", 78));
        NginxWindowsServiceManager.Start();
        await Task.Delay(1000, cancellationToken);
        if (!IsNginxOperational(nginxWorkDirectory))
        {
            var log = NginxRuntimeManager.ReadRecentErrorLog(nginxWorkDirectory);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
                ? "Nginx 启动失败，未检测到运行中的 nginx.exe。"
                : $"Nginx 启动失败。最近日志：{log}");
        }

        progress(InstallingProgress(97, "正在同步已安装产品的 Nginx 反向代理规则...", 90));
        var proxySyncSucceeded = await NginxProductProxyService.TrySyncAsync(cancellationToken);
        progress(InstallingProgress(100, proxySyncSucceeded
            ? $"Nginx 已注册为 Windows 服务并安装到 {Path.GetDirectoryName(nginxExe)}，监听端口 {listenPort}。"
            : $"Nginx 已注册为 Windows 服务并安装到 {Path.GetDirectoryName(nginxExe)}，监听端口 {listenPort}；产品反向代理规则同步失败，请查看日志后重试。"));
    }

    private static async Task<int> ConfigureAndTestNginxAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken)
    {
        var savedOptions = NginxRuntimeManager.LoadOptions();
        if (savedOptions is { Rules.Count: > 1 })
        {
            var normalized = NginxRuntimeManager.NormalizeOptions(savedOptions);
            var configuredPorts = NginxRuntimeManager.GetEffectiveListenPorts(normalized).ToArray();
            if (configuredPorts.Length == 0)
            {
                configuredPorts = [normalized.ListenPort];
            }
            foreach (var port in configuredPorts)
            {
                if (!NginxRuntimeManager.IsPortAvailable(port))
                {
                    throw new InvalidOperationException(
                        $"Nginx 多规则配置的端口 {port} 已被占用，请先在“管理”中调整端口后再安装。");
                }
            }

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
                await RunProcessWithOutputAsync(nginxExe, "-t", nginxRoot, cancellationToken);
                NginxRuntimeManager.SaveOptions(normalized);
                return normalized.ListenPort;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 多规则配置校验失败：{ex.Message}", ex);
            }
        }

        Exception? lastError = null;
        foreach (var port in NginxRuntimeManager.BuildPortCandidates())
        {
            if (!NginxRuntimeManager.IsPortAvailable(port))
            {
                continue;
            }

            NginxRuntimeManager.CleanConfigFiles(nginxRoot);
            NginxRuntimeManager.WriteListenPort(nginxRoot, port);

            try
            {
                await RunProcessWithOutputAsync(nginxExe, "-t", nginxRoot, cancellationToken);
                NginxRuntimeManager.SaveOptions(port);
                return port;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Nginx 无法找到可用监听端口。请检查 72、80、8088、8080、8090、8099、18080 是否被占用。", lastError);
    }
    private async Task InstallMySqlAsync(
        MySqlReleaseDefinition release,
        Action<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var root = ComponentPaths.SelectMySqlInstallRoot();
        Directory.CreateDirectory(root);

        progress(new InstallProgress(0, $"准备安装 {release.DisplayName}...", InstallProgressStage.Preparing, 0));
        var archive = await DownloadAbsoluteFileAsync(
            release.PackageUrl,
            GetPackageDirectory(),
            release.PackageFileName,
            progress,
            0,
            65,
            cancellationToken);

        var stagingContainer = Path.Combine(GetTempDirectory(), "mysql-staging", $"mysql.{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingContainer);
        progress(InstallingProgress(65, $"正在暂存并校验 {release.DisplayName} 安装包...", 0));
        await ExtractZipAsync(archive, stagingContainer, cancellationToken);
        var stagedMySqlExe = Directory.GetFiles(stagingContainer, "mysql.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var stagedMysqldExe = Directory.GetFiles(stagingContainer, "mysqld*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("mysqld-itmc.exe", StringComparison.OrdinalIgnoreCase));
        if (stagedMySqlExe is null || stagedMysqldExe is null)
        {
            DeleteDirectoryBestEffort(stagingContainer);
            throw new FileNotFoundException("MySQL 暂存包中未找到 mysql.exe 或 mysqld.exe。");
        }

        var stagedMySqlRoot = Directory.GetParent(Path.GetDirectoryName(stagedMySqlExe)!)!.FullName;
        var existingMySqlExe = new ComponentLocator().FindMySqlExecutable(root, stagingContainer);
        var targetMySqlRoot = existingMySqlExe is null
            ? IsSamePath(root, ComponentPaths.MySqlRoot) ? root : Path.Combine(root, "mysql")
            : Directory.GetParent(Path.GetDirectoryName(existingMySqlExe)!)!.FullName;

        progress(InstallingProgress(67, "下载校验完成，正在停止旧 MySQL 服务...", 10));
        var prepareScript = Path.Combine(GetTempDirectory(), "prepare-mysql-install.ps1");
        await FileCompat.WriteAllTextAsync(prepareScript, BuildMySqlInstallPreparationScript(), new UTF8Encoding(true), cancellationToken);
        await RunElevatedPowerShellAsync(prepareScript, progress, 12, 20, cancellationToken, requireExistingAdministrator: true);

        progress(InstallingProgress(70, "正在切换 MySQL 运行目录并保留原数据库...", 28));
        using var runtimeTransaction = MySqlRuntimeTransaction.Commit(
            stagingContainer,
            stagedMySqlRoot,
            targetMySqlRoot,
            Path.Combine(GetTempDirectory(), "mysql-rollback"));
        var mysqlRoot = runtimeTransaction.ProductRoot;
        var mysqlExe = Directory.GetFiles(mysqlRoot, "mysql.exe", SearchOption.AllDirectories)
            .First(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var mysqldExe = Directory.GetFiles(mysqlRoot, "mysqld*.exe", SearchOption.AllDirectories)
            .First(path => Path.GetFileName(path).Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("mysqld-itmc.exe", StringComparison.OrdinalIgnoreCase));
        var credentials = existingMySqlExe is null ? MySqlCredentialStore.CreateNew() : MySqlCredentialStore.Load();
        var script = Path.Combine(GetTempDirectory(), "install-mysql.ps1");
        await FileCompat.WriteAllTextAsync(script, BuildMySqlScript(mysqlRoot, mysqldExe, mysqlExe, credentials.Password, credentials.Port), new UTF8Encoding(true), cancellationToken);

        try
        {
            progress(InstallingProgress(82, "正在注册并启动 MySQL 服务...", 45));
            await RunElevatedPowerShellAsync(script, progress, 48, 95, cancellationToken, requireExistingAdministrator: true);
            MySqlCredentialStore.Save(credentials);
            runtimeTransaction.Complete();
            progress(InstallingProgress(100, $"{release.DisplayName} 已安装到 {mysqlRoot}。连接信息：127.0.0.1:{credentials.Port}，账号 root。"));
        }
        catch
        {
            await CleanupFailedMySqlInstallAsync(mysqlRoot);
            runtimeTransaction.Rollback();
            throw;
        }
    }

    private static async Task CleanupFailedMySqlInstallAsync(string mysqlRoot)
    {
        try
        {
            var script = Path.Combine(GetTempDirectory(), "cleanup-failed-mysql-install.ps1");
            await FileCompat.WriteAllTextAsync(
                script,
                BuildMySqlInstallCleanupScript(mysqlRoot),
                new UTF8Encoding(true),
                CancellationToken.None);
            await RunElevatedPowerShellAsync(
                script,
                _ => { },
                0,
                0,
                CancellationToken.None,
                requireExistingAdministrator: true);
        }
        catch
        {
            // Preserve the original installation error.  The generated install
            // script already performs the same cleanup when it can complete.
        }
    }

    internal static string BuildMySqlInstallCleanupScript(string mysqlRoot)
    {
        var root = EscapePowerShellPath(mysqlRoot);
        var workRoot = EscapePowerShellPath(GetTempDirectory());
        return $$"""
            $ErrorActionPreference = 'Continue'
            $root = '{{root}}'
            $workRoot = '{{workRoot}}'
            if (!(Test-Path -LiteralPath $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot 'cleanup-failed-mysql-install.log'
            Start-Transcript -Path $log -Append | Out-Null

            function Get-ServiceExecutable($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $null }
                $expanded = [Environment]::ExpandEnvironmentVariables($path).Trim()
                if ($expanded.StartsWith('"')) {
                    $end = $expanded.IndexOf('"', 1)
                    if ($end -gt 1) { return $expanded.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($expanded, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            $service = Get-CimInstance Win32_Service -Filter "Name='MySQL80'" -ErrorAction SilentlyContinue
            $serviceExecutable = Get-ServiceExecutable ($service.PathName)
            $managed = $service -and $serviceExecutable -and
                ([IO.Path]::GetFullPath($serviceExecutable).StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\\') + '\\'), [StringComparison]::OrdinalIgnoreCase))
            if ($managed) {
                Get-Process -Name 'mysqld','mysqld-itmc','mysql','mysqladmin','mysqldump' -ErrorAction SilentlyContinue | ForEach-Object {
                    try {
                        if ($_.Path -and ([IO.Path]::GetFullPath($_.Path).StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\\') + '\\'), [StringComparison]::OrdinalIgnoreCase))) {
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    } catch { }
                }
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                & sc.exe delete MySQL80 | Out-String | Write-Output
            } elseif ($service) {
                Write-Output ('未清理路径不属于本次安装的 MySQL80 服务：' + $service.PathName)
            }

            try { Stop-Transcript | Out-Null } catch { }
            exit 0
            """;
    }

    internal static string BuildMySqlInstallPreparationScript()
    {
        var mysqlRoots = ComponentPaths.MySqlSearchRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => $"'{EscapePowerShellPath(path)}'");
        var mysqlRootsLiteral = string.Join(", ", mysqlRoots);
        var storeDataRoot = EscapePowerShellPath(GetStoreDataRoot());
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $mysqlRoots = @({{mysqlRootsLiteral}})
            $workRoot = Join-Path '{{storeDataRoot}}' 'Work'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot 'prepare-mysql-install.log'
            Start-Transcript -Path $log -Append | Out-Null

            function Stop-StoreDataMySqlProcesses {
                foreach ($name in @('mysqld','mysqld-itmc','mysql','mysqladmin','mysqldump')) {
                    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
                        $path = $null
                        try { $path = $_.Path } catch { }
                        if ($path -and ($mysqlRoots | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
                            Write-Output ('停止进程：' + $_.ProcessName + ' #' + $_.Id)
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                }
            }

            function Wait-ServiceGone {
                for ($i = 0; $i -lt 60; $i++) {
                    if (-not (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { return $true }
                    Start-Sleep -Milliseconds 500
                }
                return $false
            }

            function Get-ServiceExecutable($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $null }
                $expanded = [Environment]::ExpandEnvironmentVariables($path).Trim()
                if ($expanded.StartsWith('"')) {
                    $end = $expanded.IndexOf('"', 1)
                    if ($end -gt 1) { return $expanded.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($expanded, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            function Is-ManagedMySqlPath($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $false }
                try {
                    $full = [IO.Path]::GetFullPath($path)
                    foreach ($root in $mysqlRoots) {
                        $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
                        if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
                    }
                } catch { }
                return $false
            }

            $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
            $serviceExecutable = Get-ServiceExecutable ((Get-CimInstance Win32_Service -Filter "Name='MySQL80'" -ErrorAction SilentlyContinue).PathName)
            $managedService = $svc -and (Is-ManagedMySqlPath $serviceExecutable)
            if ($svc -and -not $managedService) {
                throw ('检测到非 MCPanel 管理的 MySQL80 服务，已停止安装以保护现有实例：' + $serviceExecutable)
            }
            if ($svc -and $svc.Status -ne 'Stopped') {
                Write-Output '正在停止 MySQL80 服务...'
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45)) } catch { Start-Sleep -Seconds 2 }
            }

            Stop-StoreDataMySqlProcesses

            $mysqld = $null
            foreach ($mysqlRoot in $mysqlRoots) {
                if (Test-Path $mysqlRoot) {
                    $mysqld = Get-ChildItem $mysqlRoot -Filter 'mysqld*.exe' -Recurse -ErrorAction SilentlyContinue |
                        Where-Object {
                            $_.Name -in @('mysqld.exe', 'mysqld-itmc.exe') -and
                            $_.FullName -like '*\bin\*'
                        } |
                        Select-Object -First 1
                    if ($mysqld) { break }
                }
            }
            if ($mysqld -and (!$svc -or $managedService)) {
                & $mysqld.FullName --remove MySQL80 | Out-Null
            }

            if (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue) {
                if (-not $managedService) {
                    throw 'MySQL80 服务归属无法确认，已停止安装以保护现有实例。'
                }
                & sc.exe delete MySQL80 | Out-String | Write-Output
                if (!(Wait-ServiceGone)) { throw 'MySQL80 服务正在等待系统删除，请稍后重试或重启 Windows 后再安装。' }
            }

            Stop-StoreDataMySqlProcesses

            Stop-Transcript | Out-Null
            exit 0
            """;
    }

    private async Task InstallSqlServerAsync(
        EnvironmentDownloadSettings downloads,
        string selectedReleaseId,
        Action<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var selectedRelease = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
        if (!selectedRelease.IsSupported)
        {
            throw new InvalidOperationException(selectedRelease.SupportNote);
        }

        var plan = GetSqlServerInstallPlan(downloads, selectedRelease.Id);
        var credentials = WindowsServiceExists("MSSQLSERVER")
            ? SqlServerCredentialStore.Load()
            : SqlServerCredentialStore.CreateNew();
        progress(new InstallProgress(0, $"准备安装 {plan.DisplayName}...", InstallProgressStage.Preparing, 0));
        var installer = await DownloadAbsoluteFileAsync(
            plan.DownloadUrl!,
            GetPackageDirectory(),
            plan.InstallerFileName,
            progress,
            0,
            plan.UsesSseiInstaller ? 45 : 80,
            cancellationToken);
        var dataRoot = ComponentPaths.SelectSqlServerDataRoot(WindowsServiceExists("MSSQLSERVER"));
        Directory.CreateDirectory(dataRoot);

        var script = Path.Combine(GetTempDirectory(), "install-sqlserver.ps1");
        await FileCompat.WriteAllTextAsync(
            script,
            plan.UsesSseiInstaller
                ? BuildModernSqlServerScript(installer, dataRoot, plan, credentials.Password)
                : BuildSqlServer2008Script(installer, dataRoot, credentials.Password, plan.DisplayName),
            new UTF8Encoding(true),
            cancellationToken);

        progress(InstallingProgress(84, $"正在通过官方安装包安装 {plan.DisplayName}，可能需要较长时间...", 0));
        await RunElevatedPowerShellAsync(script, progress, 5, 95, cancellationToken, requireExistingAdministrator: true);
        SqlServerCredentialStore.Save(credentials);
        progress(InstallingProgress(100, "SQL Server 安装流程完成。连接信息：127.0.0.1,1433，账号 sa。"));
    }

    internal static SqlServerInstallPlan GetSqlServerInstallPlan(
        EnvironmentDownloadSettings downloads,
        string? selectedReleaseId = null)
    {
        var selected = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
        return selected.Id switch
        {
            SqlServerReleaseCatalog.SqlServer2025Id => new SqlServerInstallPlan("SQL Server 2025 Express", "SQL2025-SSEI-Expr.exe", downloads.SqlServer2025ExpressUrl, 17, "SqlServer2025ExpressMedia", "SQLEXPR_2025", true),
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId => new SqlServerInstallPlan(
                "SQL Server 2025 Enterprise Developer",
                "SQL2025-SSEI-EntDev.exe",
                downloads.SqlServer2025EnterpriseDeveloperUrl,
                17,
                "SqlServer2025EnterpriseDeveloperMedia",
                "SQLSERVER_2025_ENTERPRISE_DEVELOPER",
                true,
                "*",
                "ISO"),
            SqlServerReleaseCatalog.SqlServer2022Id => new SqlServerInstallPlan("SQL Server 2022 Express", "SQL2022-SSEI-Expr.exe", downloads.SqlServer2022ExpressUrl, 16, "SqlServer2022ExpressMedia", "SQLEXPR_2022", true),
            SqlServerReleaseCatalog.SqlServer2017Id => new SqlServerInstallPlan("SQL Server 2017 Express", "SQLServer2017-SSEI-Expr.exe", downloads.SqlServer2017ExpressUrl, 14, "SqlServer2017ExpressMedia", "SQLEXPR_2017", true),
            SqlServerReleaseCatalog.SqlServer2012Id => new SqlServerInstallPlan(
                "SQL Server 2012 Express SP4",
                Environment.Is64BitOperatingSystem ? "SQLEXPR_x64_ENU.exe" : "SQLEXPR_x86_ENU.exe",
                Environment.Is64BitOperatingSystem ? downloads.SqlServer2012ExpressX64Url : downloads.SqlServer2012ExpressX86Url,
                11,
                null,
                null,
                false),
            _ => new SqlServerInstallPlan(
                "SQL Server 2008 Express",
                Environment.Is64BitOperatingSystem ? "SQLEXPR_2008_x64.exe" : "SQLEXPR_2008_x86.exe",
                Environment.Is64BitOperatingSystem ? downloads.SqlServer2008ExpressX64Url : downloads.SqlServer2008ExpressX86Url,
                null,
                null,
                null,
                false)
        };
    }

    private async Task<string> DownloadAbsoluteFileAsync(string url, string directory, string fileName, Action<InstallProgress> progress, double from, double to, CancellationToken cancellationToken)
    {
        return await DownloadCoreAsync(url, directory, fileName, progress, from, to, cancellationToken);
    }

    private static InstallProgress InstallingProgress(double percent, string message, double? stagePercent = null) =>
        new(
            percent,
            message,
            InstallProgressStage.Installing,
            stagePercent ?? Compat.Clamp(percent, 0, 100));

    private async Task<string> DownloadCoreAsync(string url, string directory, string fileName, Action<InstallProgress> progress, double from, double to, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target) && ValidateEnvironmentPackage(target, throwOnFailure: false))
        {
            progress(new InstallProgress(to, $"已存在缓存文件：{fileName}", InstallProgressStage.Downloading, 100));
            return target;
        }

        TryDeleteFile(target);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"服务器返回了 HTML，未找到环境包：{url}");
        }

        var result = await DownloadService.SaveResponseAsync(
            response,
            target,
            snapshot =>
            {
                var percent = snapshot.TotalBytes is > 0
                    ? from + (to - from) * snapshot.BytesReceived / snapshot.TotalBytes.Value
                    : from;
                var stagePercent = snapshot.TotalBytes is > 0
                    ? Compat.Clamp(snapshot.BytesReceived * 100d / snapshot.TotalBytes.Value, 0, 100)
                    : (double?)null;
                var downloadedText = snapshot.TotalBytes is > 0
                    ? $"{ProductTransferFormatting.FormatBytes(snapshot.BytesReceived)} / {ProductTransferFormatting.FormatBytes(snapshot.TotalBytes.Value)}"
                    : ProductTransferFormatting.FormatBytes(snapshot.BytesReceived);
                progress(new InstallProgress(
                    Compat.Clamp(percent, from, to),
                    $"正在下载 {fileName}：{downloadedText}",
                    InstallProgressStage.Downloading,
                    stagePercent,
                    ProductTransferFormatting.FormatRate(snapshot.BytesPerSecond)));
            },
            cancellationToken: cancellationToken,
            validatePartial: path => ValidateEnvironmentPackage(
                path,
                throwOnFailure: true,
                expectedFileName: fileName),
            emptyFileMessage: $"下载文件为空：{fileName}",
            incompleteFileMessage: (expected, actual) =>
                $"下载不完整：{fileName} 应为 {expected} 字节，实际 {actual} 字节。");

        progress(new InstallProgress(
            to,
            $"{fileName} 下载完成",
            InstallProgressStage.Downloading,
            100,
            ProductTransferFormatting.FormatRate(result.AverageBytesPerSecond)));
        return target;
    }

    private static bool ValidateEnvironmentPackage(string path, bool throwOnFailure, string? expectedFileName = null)
    {
        try
        {
            var fileName = expectedFileName ?? Path.GetFileName(path);
            var extension = Path.GetExtension(fileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                throw new InvalidDataException("文件为空。");
            }

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                if (archive.Entries.Count == 0)
                {
                    throw new InvalidDataException("ZIP 包中没有文件。");
                }
            }
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("EXE 文件头无效。");
                }
            }
            else if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                var signature = new byte[8];
                using var stream = File.OpenRead(path);
                if (stream.Read(signature, 0, signature.Length) != signature.Length ||
                    !signature.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                {
                    throw new InvalidDataException("MSI 文件头无效。");
                }
            }

            return true;
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            _ = ex;
            return false;
        }
        catch (Exception ex) when (throwOnFailure)
        {
            throw new InvalidDataException($"环境安装包校验失败：{expectedFileName ?? Path.GetFileName(path)}。{ex.Message}", ex);
        }
    }

    private static void ExtractZip(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包包含越界路径：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static Task ExtractZipAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractZip(archive, destination);
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    private static void ConfigureTomcat(string tomcatRoot)
    {
        var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
        if (!File.Exists(serverXml))
        {
            return;
        }

        var text = File.ReadAllText(serverXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .TrimStart('\uFEFF');
        text = text.Replace("$[ServerPort]", "8005", StringComparison.OrdinalIgnoreCase)
            .Replace("$[ConnectorPort]", "8080", StringComparison.OrdinalIgnoreCase);
        AtomicFile.WriteAllText(serverXml, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var jvmProperties = Path.Combine(tomcatRoot, "conf", "jvm.properties");
        if (File.Exists(jvmProperties))
        {
            var jvmText = NormalizeTomcatJvmProperties(
                File.ReadAllText(jvmProperties, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
            if (jvmText.IndexOf("-XX:ErrorFile=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var lines = jvmText.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n');
                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    var errorFile = Path.Combine(tomcatRoot, "logs", "hs_err_pid%p.log")
                        .Replace("\\", "/");
                    lines[0] = lines[0].TrimEnd() + " -XX:ErrorFile=\"" + errorFile + "\"";
                    jvmText = string.Join(Environment.NewLine, lines);
                }
            }

            // catalina.bat reads this file with `for /F`; a UTF-8 BOM becomes
            // part of the first JVM option (for example `﻿-Xms2048m`).
            // Always rewrite it as BOM-free UTF-8, including already-customized
            // packages that do not enter the branch above.
            AtomicFile.WriteAllText(jvmProperties, NormalizeTomcatJvmProperties(jvmText), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    internal static string NormalizeTomcatJvmProperties(string text) =>
        text.TrimStart('\uFEFF');

    internal static void NormalizeTomcatJvmPropertiesFile(
        string tomcatRoot,
        bool useInstanceLocalErrorFile = false)
    {
        var path = Path.Combine(tomcatRoot, "conf", "jvm.properties");
        if (!File.Exists(path))
        {
            return;
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var text = NormalizeTomcatJvmProperties(File.ReadAllText(path, encoding));
        if (useInstanceLocalErrorFile)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            var firstOptionLine = Array.FindIndex(lines, line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.TrimStart().StartsWith("#", StringComparison.Ordinal));
            if (firstOptionLine >= 0)
            {
                var errorFile = Path.Combine(tomcatRoot, "logs", "hs_err_pid%p.log")
                    .Replace("\\", "/", StringComparison.Ordinal);
                var replacement = $"-XX:ErrorFile=\"{errorFile}\"";
                var pattern = @"-XX:ErrorFile=(?:""[^""]*""|\S+)";
                lines[firstOptionLine] = Regex.IsMatch(
                        lines[firstOptionLine],
                        pattern,
                        RegexOptions.IgnoreCase)
                    ? Regex.Replace(
                        lines[firstOptionLine],
                        pattern,
                        replacement,
                        RegexOptions.IgnoreCase)
                    : lines[firstOptionLine].TrimEnd() + " " + replacement;
                text = string.Join(Environment.NewLine, lines);
            }
        }

        AtomicFile.WriteAllText(path, text, encoding);
    }

    internal static string BuildIisScript(string rewriteMsi)
    {
        var requiredFeatures = new[]
        {
            "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-HttpErrors",
            "IIS-HttpRedirect", "IIS-ApplicationDevelopment", "IIS-Security", "IIS-URLAuthorization",
            "IIS-RequestFiltering", "IIS-NetFxExtensibility45", "IIS-HealthAndDiagnostics", "IIS-HttpLogging",
            "IIS-RequestMonitor", "IIS-HttpTracing", "IIS-Performance", "IIS-HttpCompressionStatic",
            "IIS-HttpCompressionDynamic", "IIS-ManagementConsole", "IIS-ManagementScriptingTools",
            "IIS-IIS6ManagementCompatibility", "IIS-WebServerManagementTools", "IIS-Metabase",
            "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-StaticContent", "IIS-DefaultDocument",
            "IIS-DirectoryBrowsing", "IIS-ASPNET45", "NetFx4Extended-ASPNET45", "IIS-ASP", "IIS-CGI",
            "IIS-ServerSideIncludes", "IIS-BasicAuthentication", "IIS-WindowsAuthentication", "IIS-ODBCLogging"
        };
        var legacyFeatures = new[] { "IIS-NetFxExtensibility", "IIS-ASPNET" };
        var log = Path.Combine(GetTempDirectory(), "install-iis.log");
        var restartMarker = IisInstallRestartMarkerPath;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$log='{EscapePowerShellPath(log)}'");
        sb.AppendLine($"$restartMarker='{EscapePowerShellPath(restartMarker)}'");
        sb.AppendLine("$restartNeeded=$false");
        sb.AppendLine("Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Start-Transcript -Path $log -Append | Out-Null");
        sb.AppendLine("function Test-RestartNeeded($result) { if ($null -eq $result -or $null -eq $result.RestartNeeded) { return $false }; $text=$result.RestartNeeded.ToString(); return $text -eq 'True' -or $text -eq 'Yes' }");
        sb.AppendLine("function Mark-RestartRequired($reason) { $dir=Split-Path $restartMarker -Parent; New-Item -ItemType Directory -Path $dir -Force | Out-Null; Set-Content -LiteralPath $restartMarker -Value $reason -Encoding UTF8; Write-Output $reason }");
        sb.AppendLine("try {");
        foreach (var feature in requiredFeatures)
        {
            sb.AppendLine($"  $featureResult=Enable-WindowsOptionalFeature -Online -FeatureName {feature} -All -NoRestart -ErrorAction Stop");
            sb.AppendLine("  if (Test-RestartNeeded $featureResult) { $restartNeeded=$true }");
        }
        sb.AppendLine("  try {");
        sb.AppendLine("    $netFx3=Get-WindowsOptionalFeature -Online -FeatureName NetFx3 -ErrorAction Stop");
        sb.AppendLine("    if ($netFx3.State -ne 'Enabled') { $netFx3Result=Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All -NoRestart -ErrorAction Stop; if (Test-RestartNeeded $netFx3Result) { $restartNeeded=$true } }");
        foreach (var feature in legacyFeatures)
        {
            sb.AppendLine($"    $legacyResult=Enable-WindowsOptionalFeature -Online -FeatureName {feature} -All -NoRestart -ErrorAction Stop");
            sb.AppendLine("    if (Test-RestartNeeded $legacyResult) { $restartNeeded=$true }");
        }
        sb.AppendLine("  } catch { Write-Output ('兼容性提示：ASP.NET 2.0/3.5 功能未完全启用；现代 ASP.NET 4.x/IIS 功能继续安装。原因：' + $_.Exception.Message) }");
        sb.AppendLine("  if ($restartNeeded) { Mark-RestartRequired 'Windows 功能安装要求重启后继续 IIS 安装。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine(@"  $appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'");
        sb.AppendLine(@"  if (!(Test-Path $appcmd)) { throw '未找到 IIS 配置工具 appcmd.exe。' }");
        sb.AppendLine(@"  $docs=@('default.html','default.asp','default.aspx','index.php','index.asp','index.aspx')");
        sb.AppendLine(@"  foreach($doc in $docs) { & $appcmd set config /section:defaultDocument /+files.[value=$doc] 2>$null }");
        sb.AppendLine(@"  & $appcmd set config /section:asp /enableParentPaths:True");
        var escapedRewriteMsi = EscapePowerShellPath(rewriteMsi);
        sb.AppendLine($"  $rewriteMsi = '{escapedRewriteMsi}'");
        sb.AppendLine("  if (!(Test-Path -LiteralPath $rewriteMsi)) { throw '未找到 URL Rewrite 安装包。' }");
        sb.AppendLine("  $rewriteProcess = Start-Process msiexec.exe -ArgumentList ('/i \"' + $rewriteMsi + '\" /qn /norestart') -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop");
        sb.AppendLine("  if ($rewriteProcess.ExitCode -eq 3010) { Mark-RestartRequired 'URL Rewrite 安装完成，但 Windows Installer 要求重启后继续。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine("  if ($rewriteProcess.ExitCode -ne 0) { throw ('URL Rewrite 安装失败，退出码：' + $rewriteProcess.ExitCode) }");
        sb.AppendLine("  $moduleOutput = (& $appcmd list modules 2>&1 | Out-String)");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0 -or $moduleOutput -notmatch 'RewriteModule') { throw 'URL Rewrite MSI 已完成，但 IIS 未检测到 RewriteModule。' }");
        sb.AppendLine("  $iisreset = Join-Path $env:windir 'System32\\iisreset.exe'");
        sb.AppendLine("  if (!(Test-Path -LiteralPath $iisreset)) { throw '未找到 IIS 重置工具 iisreset.exe。' }");
        sb.AppendLine("  & $iisreset /START");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw ('IIS 启动失败，退出码：' + $LASTEXITCODE) }");
        sb.AppendLine("  foreach($serviceName in @('WAS','W3SVC')) { $service=Get-Service -Name $serviceName -ErrorAction Stop; if ($service.Status -ne 'Running') { Start-Service -Name $serviceName -ErrorAction Stop }; $service=Get-Service -Name $serviceName -ErrorAction Stop; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)); if ($service.Status -ne 'Running') { throw ($serviceName + ' 未进入 Running 状态。') } }");
        sb.AppendLine("  Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("} finally { try { Stop-Transcript | Out-Null } catch { } }");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

    internal static string BuildMySqlScript(
        string mysqlRoot,
        string mysqldExe,
        string mysqlExe,
        string rootPassword,
        int port = MySqlCredentialStore.DefaultPort)
    {
        var data = Path.Combine(mysqlRoot, "data");
        var myIni = Path.Combine(mysqlRoot, "my.ini");
        var errorLog = Path.Combine(data, "mysql-itmc.err");
        var initFile = Path.Combine(data, "mysql-itmc-init.sql");
        var workRoot = GetTempDirectory();
        var log = Path.Combine(workRoot, "install-mysql.log");
        var escapedPassword = EscapePowerShellPath(rootPassword);
        var mysqlPort = port is >= 1 and <= 65535 ? port : MySqlCredentialStore.DefaultPort;
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $root='{{EscapePowerShellPath(mysqlRoot)}}'
            $data='{{EscapePowerShellPath(data)}}'
            $mysqld='{{EscapePowerShellPath(mysqldExe)}}'
            $mysql='{{EscapePowerShellPath(mysqlExe)}}'
            $rootPassword='{{escapedPassword}}'
            $myIni='{{EscapePowerShellPath(myIni)}}'
            $errorLog='{{EscapePowerShellPath(errorLog)}}'
            $initFile='{{EscapePowerShellPath(initFile)}}'
            $log='{{EscapePowerShellPath(log)}}'
            $registeredService = $false
            $serviceWasPresent = [bool](Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)
            Start-Transcript -Path $log -Append | Out-Null

            function Step($message) {
                Write-Output ''
                Write-Output ('==== ' + $message + ' ====')
            }

            function Fail($message) {
                Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue
                if ($script:registeredService) {
                    Remove-MySqlService
                }
                $tail = Read-ErrorTail
                Write-Output ('安装失败：' + $message)
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Write-Output ('最近错误日志：' + [Environment]::NewLine + $tail)
                }
                try { Stop-Transcript | Out-Null } catch { }
                exit 1
            }

            function Invoke-Native($file, [string[]]$arguments) {
                & $file @arguments 1>$null 2>$null
                return [int]$LASTEXITCODE
            }

            function Invoke-NativeQuiet($file, [string[]]$arguments) {
                return Invoke-Native $file $arguments
            }

            function Read-ErrorTail {
                $err = Get-ChildItem $data -Filter '*.err' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($err) {
                    return ((Get-Content $err.FullName -Tail 80 -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
                }
                return ''
            }

            function Wait-ServiceState($target, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status.ToString() -eq $target) { return $true }
                    Start-Sleep -Milliseconds 500
                }
                return $false
            }

            function Remove-MySqlService {
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -ne 'Stopped') {
                    Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                    try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
                }

                if (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue) {
                    & sc.exe delete MySQL80 | Out-String | Write-Output
                    for ($i = 0; $i -lt 20; $i++) {
                        if (-not (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { break }
                        Start-Sleep -Milliseconds 500
                    }
                }
                $script:registeredService = $false
            }

            function Start-MySqlService {
                Step '启动 MySQL80 服务'
                & sc.exe config MySQL80 start= auto | Out-Null
                $lastError = ''
                for ($i = 1; $i -le 3; $i++) {
                    try {
                        $svc = Get-Service -Name 'MySQL80' -ErrorAction Stop
                        if ($svc.Status -eq 'Running') { return }
                        if ($svc.Status -eq 'Stopped') {
                            Start-Service -Name 'MySQL80' -ErrorAction Stop
                        }
                    }
                    catch {
                        $lastError = $_.Exception.Message
                        Write-Output ('第 ' + $i + ' 次启动失败：' + $lastError)
                        & sc.exe start MySQL80 | Out-String | Write-Output
                    }

                    # MySQL may spend several minutes recovering a large
                    # InnoDB data directory.  Do not issue another start while
                    # SCM reports START_PENDING; it only resets the wait and
                    # makes a healthy slow start look like a failure.
                    if (Wait-ServiceState 'Running' 60) { return }
                }

                $tail = Read-ErrorTail
                $state = (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue).Status
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Fail ("MySQL80 启动失败，当前状态：$state。最近错误日志：" + [Environment]::NewLine + $tail)
                }
                Fail ("MySQL80 启动失败，当前状态：$state。" + $lastError)
            }

            function Stop-MySqlService {
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if (-not $svc -or $svc.Status -eq 'Stopped') { return }
                Step '停止 MySQL80 服务'
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState 'Stopped' 45)) {
                    & sc.exe stop MySQL80 | Out-String | Write-Output
                    if (!(Wait-ServiceState 'Stopped' 30)) {
                        Fail 'MySQL80 停止超时，请确认没有外部程序占用服务。'
                    }
                }
            }

            function Test-RootPassword {
                $code = Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
                return $code -eq 0
            }

            function Stop-TemporaryMySqlProcess($process) {
                if (!$process -or $process.HasExited) { return }
                Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SHUTDOWN;') | Out-Null
                for ($i = 0; $i -lt 30; $i++) {
                    if ($process.HasExited) { return }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                }
            }

            function Reset-RootPassword {
                Step '重置 root 密码'
                Stop-MySqlService
                # MySQL 官方 Windows 流程使用 init-file 临时执行改密语句。
                # 不使用跳过权限表的启动模式：它会自动启用 skip_networking，
                # 在 Windows 上会关闭 TCP，导致服务以 port 0 启动或直接退出。
                $sqlPassword = $rootPassword.Replace('\', '\\').Replace("'", "''")
                $initSql = if ($isModern) {
                    "ALTER USER 'root'@'localhost' IDENTIFIED BY '$sqlPassword';"
                } else {
                    "SET PASSWORD FOR 'root'@'localhost' = PASSWORD('$sqlPassword');"
                }
                $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
                [IO.File]::WriteAllText($initFile, $initSql, $utf8NoBom)
                $proc = $null
                $updated = $false
                $failure = ''
                try {
                    $proc = Start-Process -FilePath $mysqld -ArgumentList @("--defaults-file=$myIni", "--init-file=$initFile", "--bind-address=127.0.0.1") -WindowStyle Hidden -PassThru
                    for ($i = 0; $i -lt 35; $i++) {
                        Start-Sleep -Seconds 1
                        if ($proc.HasExited) { break }
                        $code = Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
                        if ($code -eq 0) {
                            $updated = $true
                            break
                        }
                    }

                    if (!$updated) { $failure = 'MySQL root 密码重置失败，临时维护进程未能接受连接。' }
                }
                catch { $failure = $_.Exception.Message }
                finally {
                    Stop-TemporaryMySqlProcess $proc
                    Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue
                }

                if (!$updated) {
                    $tail = Read-ErrorTail
                    Fail ($failure + [Environment]::NewLine + $tail)
                }
                Start-MySqlService
            }

            try {
                Step '生成 my.ini'
                if (!(Test-Path $data)) { New-Item -ItemType Directory -Path $data -Force | Out-Null }
                $versionText = (& $mysqld --version) -join ' '
                $isModern = $versionText -match 'Ver\s+(8|9)\.|mysqld\s+(8|9)\.'
                # MySQL 8.4 and 9.7 no longer accept the legacy default
                # authentication-plugin setting. ALTER USER below lets each
                # release keep its supported default.
                $authLine = ''
                @"
            [mysqld]
            basedir={{mysqlRoot.Replace("\\", "/")}}
            datadir={{data.Replace("\\", "/")}}
            log-error={{errorLog.Replace("\\", "/")}}
            port={{mysqlPort}}
            bind-address=127.0.0.1
            character-set-server=utf8mb4
            $authLine
            [client]
            port={{mysqlPort}}
            default-character-set=utf8
            "@ | Set-Content -Encoding ASCII $myIni

                if (!(Test-Path (Join-Path $data 'mysql')) -and $isModern) {
                    Step '初始化 MySQL 数据目录'
                    $code = Invoke-Native $mysqld @("--defaults-file=$myIni", '--initialize-insecure')
                    if ($code -ne 0) { Fail "MySQL 初始化失败，退出码：$code" }
                }

                Step '注册 MySQL80 服务'
                if ($serviceWasPresent) { Fail '安装前仍检测到 MySQL80 服务，已停止安装以保护现有实例。' }
                $code = Invoke-Native $mysqld @('--install', 'MySQL80', "--defaults-file=$myIni")
                if (!$serviceWasPresent -and (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { $registeredService = $true }
                if ($code -ne 0) { Fail "MySQL 服务注册失败，退出码：$code" }

                Start-MySqlService
                Start-Sleep -Seconds 2

                if (-not (Test-RootPassword)) { Reset-RootPassword }
                if (-not (Test-RootPassword)) { Fail 'MySQL 已启动，但 root 密码校验失败。' }

                Write-Output 'MySQL 安装完成。'
                exit 0
            }
            catch {
                if ($script:registeredService) {
                    Remove-MySqlService
                }
                Write-Output ('安装失败：' + $_.Exception.Message)
                $tail = Read-ErrorTail
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Write-Output ('最近错误日志：' + [Environment]::NewLine + $tail)
                }
                exit 1
            }
            finally {
                try { Stop-Transcript | Out-Null } catch { }
            }
            """;
    }

    internal static string BuildSqlServer2008Script(
        string installer,
        string dataRoot,
        string saPassword,
        string displayName = "SQL Server 2008 Express")
    {
        var logFileName = displayName.Contains("2012", StringComparison.OrdinalIgnoreCase)
            ? "install-sqlserver-2012.log"
            : "install-sqlserver-2008.log";
        var features = displayName.Contains("2012", StringComparison.OrdinalIgnoreCase) ? "SQL" : "SQL,Tools";
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $installer='{{EscapePowerShellPath(installer)}}'
            $dataRoot='{{EscapePowerShellPath(dataRoot)}}'
            $saPassword='{{EscapePowerShellPath(saPassword)}}'
            $log=Join-Path (Split-Path $installer) '{{logFileName}}'
            Start-Transcript -Path $log -Append | Out-Null

            function Step($name) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
            }

            function Fail($message) {
                Write-Output ('安装失败：' + $message)
                Stop-Transcript | Out-Null
                exit 1
            }

            function Ensure-Dir($path) {
                if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
                icacls.exe $path /inheritance:e | Out-Null
                icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-18:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-20:(OI)(CI)F' /T /C | Out-Null
            }

            function Get-CurrentWindowsUser {
                try {
                    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
                    if ($identity -and ![string]::IsNullOrWhiteSpace($identity.Name) -and
                        $identity.Name -notlike 'NT AUTHORITY\*') {
                        return $identity.Name
                    }
                } catch { }

                try {
                    $name = (& whoami.exe 2>$null | Select-Object -First 1).Trim()
                    if ($name -and $name -notlike 'NT AUTHORITY\*') { return $name }
                } catch { }

                return $null
            }

            function Get-SqlAdminAccounts {
                $accounts = @()
                try {
                    $group = ([System.Security.Principal.SecurityIdentifier]'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value
                    if ($group) { $accounts += $group }
                } catch {
                    $accounts += 'BUILTIN\Administrators'
                }

                $currentUser = Get-CurrentWindowsUser
                if ($currentUser) { $accounts += $currentUser }
                return @($accounts | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
            }

            function Format-SqlSysadminAccountsArgument($accounts) {
                return (($accounts | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
            }

            function Ensure-CurrentWindowsSqlLogin {
                $currentUser = Get-CurrentWindowsUser
                if ([string]::IsNullOrWhiteSpace($currentUser)) {
                    Write-Output '无法识别当前 Windows 用户，跳过 SQL Server Windows 登录修复。'
                    return $false
                }

                $identifier = $currentUser.Replace(']', ']]')
                $literal = $currentUser.Replace("'", "''")
                $query = @"
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$literal')
            BEGIN
                CREATE LOGIN [$identifier] FROM WINDOWS;
            END;
            IF NOT EXISTS (
                SELECT 1
                FROM sys.server_role_members AS rm
                INNER JOIN sys.server_principals AS rolePrincipal ON rolePrincipal.principal_id = rm.role_principal_id
                INNER JOIN sys.server_principals AS memberPrincipal ON memberPrincipal.principal_id = rm.member_principal_id
                WHERE rolePrincipal.name = N'sysadmin' AND memberPrincipal.name = N'$literal'
            )
            BEGIN
                ALTER SERVER ROLE [sysadmin] ADD MEMBER [$identifier];
            END;
            "@

                $connection = $null
                $command = $null
                try {
                    $connection = New-Object System.Data.SqlClient.SqlConnection
                    $connection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;User ID=sa;Password=' + $saPassword + ';Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $connection.Open()
                    $command = $connection.CreateCommand()
                    $command.CommandText = $query
                    $command.CommandTimeout = 30
                    [void]$command.ExecuteNonQuery()
                } catch {
                    Write-Output ('使用 sa 修复当前 Windows 登录失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($command) { $command.Dispose() }
                    if ($connection) { $connection.Dispose() }
                }

                $verifyConnection = $null
                $verifyCommand = $null
                try {
                    $verifyConnection = New-Object System.Data.SqlClient.SqlConnection
                    $verifyConnection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $verifyConnection.Open()
                    $verifyCommand = $verifyConnection.CreateCommand()
                    $verifyCommand.CommandText = 'SELECT 1'
                    [void]$verifyCommand.ExecuteScalar()
                    Write-Output ('已确认当前 Windows 用户可以登录 SQL Server：' + $currentUser)
                    return $true
                } catch {
                    Write-Output ('当前 Windows 登录验证失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($verifyCommand) { $verifyCommand.Dispose() }
                    if ($verifyConnection) { $verifyConnection.Dispose() }
                }
            }

            function Wait-ServiceState($serviceName, $target, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status.ToString() -eq $target) { return $true }
                    Start-Sleep -Seconds 1
                }
                return $false
            }

            function Clear-PendingFileRenameOperations {
                $sessionManagerPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
                try {
                    $session = Get-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
                    if ($null -ne $session -and $null -ne $session.PendingFileRenameOperations) {
                        Remove-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -Force -ErrorAction Stop
                        Write-Output '已直接清理 Windows PendingFileRenameOperations。'
                    } else {
                        Write-Output '未发现 Windows PendingFileRenameOperations。'
                    }
                } catch {
                    Fail ('清理 Windows PendingFileRenameOperations 失败：' + $_.Exception.Message)
                }
            }

            function Configure-SqlServer {
                Step '配置 SQL Server 默认连接'
                $instanceId = $null
                $instanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
                if (Test-Path $instanceKey) {
                    $instanceId = (Get-ItemProperty $instanceKey -ErrorAction SilentlyContinue).MSSQLSERVER
                }
                if ([string]::IsNullOrWhiteSpace($instanceId)) { $instanceId = 'MSSQL10.SQLEXPRESS' }

                $loginKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $instanceId + '\MSSQLServer'
                if (Test-Path $loginKey) { Set-ItemProperty -Path $loginKey -Name LoginMode -Value 2 -ErrorAction SilentlyContinue }

                $tcpKey = $loginKey + '\SuperSocketNetLib\Tcp\IPAll'
                if (Test-Path $tcpKey) {
                    Set-ItemProperty -Path $tcpKey -Name TcpDynamicPorts -Value '' -ErrorAction SilentlyContinue
                    Set-ItemProperty -Path $tcpKey -Name TcpPort -Value '1433' -ErrorAction SilentlyContinue
                }
                New-NetFirewallRule -DisplayName 'MCPanel SQL Server 1433' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1433 -ErrorAction SilentlyContinue | Out-Null
            }

            try {
                Step '清理 Windows 待处理文件重命名项'
                Clear-PendingFileRenameOperations

                if (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue) {
                    Configure-SqlServer
                    Start-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
                    if (Wait-ServiceState MSSQLSERVER Running 60) {
                        if (!(Ensure-CurrentWindowsSqlLogin)) {
                            Write-Output '现有 SQL Server 已启动，但未能自动修复当前 Windows 登录。'
                        }
                    }
                    Stop-Transcript | Out-Null
                    exit 0
                }

                Step '准备 SQL Server 数据目录'
                Ensure-Dir $dataRoot

                Step '运行官方 {{displayName}} 安装包'
                $adminAccounts = Get-SqlAdminAccounts
                $adminArgument = Format-SqlSysadminAccountsArgument $adminAccounts
                Write-Output ('SQL Server 管理员账户：' + ($adminAccounts -join ', '))
                $args='/QS /ACTION=Install /FEATURES={{features}} /INSTANCENAME=MSSQLSERVER /SECURITYMODE=SQL /SAPWD="' + $saPassword + '" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSYSADMINACCOUNTS=' + $adminArgument + ' /TCPENABLED=1 /INSTALLSQLDATADIR="' + $dataRoot + '" /IACCEPTSQLSERVERLICENSETERMS'
                $install = Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
                if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) { Fail ('官方安装包退出码：' + $install.ExitCode) }

                Configure-SqlServer
                Restart-Service -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState MSSQLSERVER Running 90)) { Fail 'MSSQLSERVER 服务启动超时。' }
                if (!(Ensure-CurrentWindowsSqlLogin)) { Fail 'SQL Server 已安装，但当前 Windows 用户登录验证失败。' }
                Write-Output '{{displayName}} 安装完成。'
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Fail $_.Exception.Message
            }
            """;
    }

    internal static string BuildModernSqlServerScript(string bootstrapper, string dataRoot, SqlServerInstallPlan plan, string saPassword)
    {
        var mediaRoot = Path.Combine(Path.GetDirectoryName(bootstrapper)!, plan.MediaDirectoryName!);
        var extractRoot = Path.Combine(mediaRoot, plan.ExtractDirectoryName!);
        var setupExe = Path.Combine(extractRoot, "SETUP.EXE");
        var logFileName = $"install-sqlserver-{plan.ProductMajor}.log";
        var bootstrapLogDirectory = $@"Microsoft SQL Server\{plan.ProductMajor}0\Setup Bootstrap\Log";
        var defaultInstanceId = $"MSSQL{plan.ProductMajor}.MSSQLSERVER";
        var displayName = plan.DisplayName;
        var restartMarker = SqlServerInstallRestartMarkerPath;
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $bootstrapper='{{EscapePowerShellPath(bootstrapper)}}'
            $mediaRoot='{{EscapePowerShellPath(mediaRoot)}}'
            $extractRoot='{{EscapePowerShellPath(extractRoot)}}'
            $setupExe='{{EscapePowerShellPath(setupExe)}}'
            $dataRoot='{{EscapePowerShellPath(dataRoot)}}'
            $saPassword='{{EscapePowerShellPath(saPassword)}}'
            $defaultInstanceId='{{defaultInstanceId}}'
            $displayName='{{displayName}}'
            $mediaPackageSearchPattern='{{plan.MediaPackageSearchPattern}}'
            $mediaType='{{plan.MediaType}}'
            $restartMarker='{{EscapePowerShellPath(restartMarker)}}'
            $script:mountedIsoPath=$null
            $log=Join-Path (Split-Path $bootstrapper) '{{logFileName}}'
            Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
            Start-Transcript -Path $log -Force | Out-Null

            function Step($name) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
            }

            function Dismount-InstallMedia {
                if (![string]::IsNullOrWhiteSpace($script:mountedIsoPath)) {
                    Dismount-DiskImage -ImagePath $script:mountedIsoPath -ErrorAction SilentlyContinue | Out-Null
                    $script:mountedIsoPath=$null
                }
            }

            function Fail($message) {
                Write-Output ('安装失败：' + $message)
                Dismount-InstallMedia
                Write-SetupSummary
                Stop-Transcript | Out-Null
                exit 1
            }

            function Read-TextLocal($path) {
                $bytes = [IO.File]::ReadAllBytes($path)
                if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                    return [Text.Encoding]::UTF8.GetString($bytes)
                }
                if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
                    return [Text.Encoding]::Unicode.GetString($bytes)
                }
                if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
                    return [Text.Encoding]::BigEndianUnicode.GetString($bytes)
                }
                return [Text.Encoding]::Default.GetString($bytes)
            }

            function Write-SetupSummary {
                $bootstrapLogRoot = Join-Path $env:ProgramFiles '{{EscapePowerShellPath(bootstrapLogDirectory)}}'
                $summary = Get-ChildItem $bootstrapLogRoot -Filter Summary.txt -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($summary) {
                    Write-Output ''
                    Write-Output '最近 SQL Server 安装摘要：'
                    $text = Read-TextLocal $summary.FullName
                    $text -split "`r?`n" | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 120 | ForEach-Object { Write-Output $_ }
                }
            }

            function Ensure-Dir($path) {
                if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
                icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-18:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-20:(OI)(CI)F' /T /C | Out-Null
            }

            function Get-CurrentWindowsUser {
                try {
                    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
                    if ($identity -and ![string]::IsNullOrWhiteSpace($identity.Name) -and
                        $identity.Name -notlike 'NT AUTHORITY\*') {
                        return $identity.Name
                    }
                } catch { }

                try {
                    $name = (& whoami.exe 2>$null | Select-Object -First 1).Trim()
                    if ($name -and $name -notlike 'NT AUTHORITY\*') { return $name }
                } catch { }

                return $null
            }

            function Get-SqlAdminAccounts {
                $accounts = @()
                try {
                    $group = ([System.Security.Principal.SecurityIdentifier]'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value
                    if ($group) { $accounts += $group }
                } catch {
                    $accounts += 'BUILTIN\Administrators'
                }

                $currentUser = Get-CurrentWindowsUser
                if ($currentUser) { $accounts += $currentUser }
                return @($accounts | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
            }

            function Format-SqlSysadminAccountsArgument($accounts) {
                return (($accounts | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
            }

            function Ensure-CurrentWindowsSqlLogin {
                $currentUser = Get-CurrentWindowsUser
                if ([string]::IsNullOrWhiteSpace($currentUser)) {
                    Write-Output '无法识别当前 Windows 用户，跳过 SQL Server Windows 登录修复。'
                    return $false
                }

                $identifier = $currentUser.Replace(']', ']]')
                $literal = $currentUser.Replace("'", "''")
                $query = @"
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$literal')
            BEGIN
                CREATE LOGIN [$identifier] FROM WINDOWS;
            END;
            IF NOT EXISTS (
                SELECT 1
                FROM sys.server_role_members AS rm
                INNER JOIN sys.server_principals AS rolePrincipal ON rolePrincipal.principal_id = rm.role_principal_id
                INNER JOIN sys.server_principals AS memberPrincipal ON memberPrincipal.principal_id = rm.member_principal_id
                WHERE rolePrincipal.name = N'sysadmin' AND memberPrincipal.name = N'$literal'
            )
            BEGIN
                ALTER SERVER ROLE [sysadmin] ADD MEMBER [$identifier];
            END;
            "@

                $connection = $null
                $command = $null
                try {
                    $connection = New-Object System.Data.SqlClient.SqlConnection
                    $connection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;User ID=sa;Password=' + $saPassword + ';Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $connection.Open()
                    $command = $connection.CreateCommand()
                    $command.CommandText = $query
                    $command.CommandTimeout = 30
                    [void]$command.ExecuteNonQuery()
                } catch {
                    Write-Output ('使用 sa 修复当前 Windows 登录失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($command) { $command.Dispose() }
                    if ($connection) { $connection.Dispose() }
                }

                $verifyConnection = $null
                $verifyCommand = $null
                try {
                    $verifyConnection = New-Object System.Data.SqlClient.SqlConnection
                    $verifyConnection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $verifyConnection.Open()
                    $verifyCommand = $verifyConnection.CreateCommand()
                    $verifyCommand.CommandText = 'SELECT 1'
                    [void]$verifyCommand.ExecuteScalar()
                    Write-Output ('已确认当前 Windows 用户可以登录 SQL Server：' + $currentUser)
                    return $true
                } catch {
                    Write-Output ('当前 Windows 登录验证失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($verifyCommand) { $verifyCommand.Dispose() }
                    if ($verifyConnection) { $verifyConnection.Dispose() }
                }
            }

            function Mark-SqlRestartRequired {
                $markerDirectory = Split-Path $restartMarker -Parent
                New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
                Set-Content -LiteralPath $restartMarker -Value 'SQL Server installation paused for sector compatibility. Restart Windows, then continue installation.' -Encoding UTF8
            }

            function Clear-SqlRestartMarker {
                Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue
            }

            function Wait-ServiceState($serviceName, $target, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status.ToString() -eq $target) { return $true }
                    Start-Sleep -Seconds 1
                }
                return $false
            }

            function Clear-PendingFileRenameOperations {
                $sessionManagerPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
                try {
                    $session = Get-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
                    if ($null -ne $session -and $null -ne $session.PendingFileRenameOperations) {
                        Remove-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -Force -ErrorAction Stop
                        Write-Output '已直接清理 Windows PendingFileRenameOperations。'
                    } else {
                        Write-Output '未发现 Windows PendingFileRenameOperations。'
                    }
                } catch {
                    Fail ('清理 Windows PendingFileRenameOperations 失败：' + $_.Exception.Message)
                }
            }

            function Get-SqlInstanceId {
                $instanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
                if (Test-Path $instanceKey) {
                    $id = (Get-ItemProperty $instanceKey -ErrorAction SilentlyContinue).MSSQLSERVER
                    if (![string]::IsNullOrWhiteSpace($id)) { return $id }
                }
                return $defaultInstanceId
            }

            function Configure-SqlServer {
                Step '配置 SQL Server 默认连接'
                $instanceId = Get-SqlInstanceId
                $loginKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $instanceId + '\MSSQLServer'
                if (Test-Path $loginKey) {
                    Set-ItemProperty -Path $loginKey -Name LoginMode -Value 2 -ErrorAction SilentlyContinue
                }

                $tcpRoot = $loginKey + '\SuperSocketNetLib\Tcp'
                if (Test-Path $tcpRoot) {
                    Get-ChildItem $tcpRoot -ErrorAction SilentlyContinue | ForEach-Object {
                        if ($_.PSChildName -like 'IP*') {
                            Set-ItemProperty -Path $_.PSPath -Name Enabled -Value 1 -ErrorAction SilentlyContinue
                        }
                    }
                }

                $tcpKey = $tcpRoot + '\IPAll'
                if (Test-Path $tcpKey) {
                    Set-ItemProperty -Path $tcpKey -Name TcpDynamicPorts -Value '' -ErrorAction SilentlyContinue
                    Set-ItemProperty -Path $tcpKey -Name TcpPort -Value '1433' -ErrorAction SilentlyContinue
                }

                & sc.exe config MSSQLSERVER start= auto | Out-Null
                & sc.exe config SQLBrowser start= demand | Out-Null
                New-NetFirewallRule -DisplayName 'MCPanel SQL Server 1433' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1433 -ErrorAction SilentlyContinue | Out-Null
                New-NetFirewallRule -DisplayName 'MCPanel SQL Browser 1434' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 1434 -ErrorAction SilentlyContinue | Out-Null
            }

            function Remove-PathSafe($path) {
                if ([string]::IsNullOrWhiteSpace($path) -or !(Test-Path $path)) { return }
                Write-Output ('清理残留目录：' + $path)
                try {
                    Get-ChildItem $path -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                        try { $_.Attributes = 'Normal' } catch { }
                    }
                    takeown.exe /F $path /R /D Y | Out-Null
                    icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
                    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
                } catch {
                    Write-Output ('目录清理警告：' + $_.Exception.Message)
                }
            }

            function Invoke-SqlSetupUninstallBestEffort {
                Step '调用官方 SQL Server 安装器清理残留'
                $setupCandidates = @()
                foreach ($root in @(
                    $extractRoot,
                    (Join-Path $mediaRoot 'SQLSERVER_2025_ENTERPRISE_DEVELOPER'),
                    (Join-Path $mediaRoot 'SQLSERVER_2025'),
                    (Join-Path $mediaRoot 'SQLEXPR_2025'),
                    (Join-Path $mediaRoot 'SQLEXPR_2022'),
                    (Join-Path $mediaRoot 'SQLEXPR_2017'),
                    (Join-Path $env:ProgramFiles 'Microsoft SQL Server'),
                    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft SQL Server')
                )) {
                    if (Test-Path $root) {
                        $setupCandidates += Get-ChildItem $root -Filter setup.exe -Recurse -ErrorAction SilentlyContinue
                    }
                }

                $setupCandidates |
                    Sort-Object FullName -Unique |
                    ForEach-Object {
                        $setupPath = $_.FullName
                        Write-Output ('尝试官方卸载：' + $setupPath)
                        $args = '/Q /ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=MSSQLSERVER'
                        $process = Start-Process -FilePath $setupPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                        if ($process) { Write-Output ('官方卸载退出码：' + $process.ExitCode) }
                    }
            }

            function Remove-SqlMsiComponentsBestEffort {
                Step '清理 SQL Server MSI 组件残留'
                $roots = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                $items = foreach ($root in $roots) {
                    if (Test-Path $root) {
                        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                            if ($props.DisplayName -and $props.DisplayName -match 'SQL Server 2025|SQL Server 2022|SQL Server 2019|SQL Server 2017|SQL Server Browser|SQL Server VSS Writer|Microsoft ODBC Driver.*SQL|Microsoft OLE DB Driver.*SQL') {
                                [pscustomobject]@{ Name = $props.DisplayName; KeyName = $_.PSChildName }
                            }
                        }
                    }
                }

                $items | Sort-Object Name -Unique | ForEach-Object {
                    Write-Output ('卸载 MSI 组件：' + $_.Name)
                    if ($_.KeyName -match '^\{[0-9A-Fa-f-]+\}$') {
                        $process = Start-Process -FilePath msiexec.exe -ArgumentList ('/x ' + $_.KeyName + ' /qn /norestart') -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                        if ($process) { Write-Output ('MSI 卸载退出码：' + $process.ExitCode) }
                    }
                }
            }

            function Remove-BrokenSqlServer {
                Step '清理损坏的 SQL Server 默认实例残留'
                Invoke-SqlSetupUninstallBestEffort
                $instanceId = Get-SqlInstanceId
                foreach ($serviceName in @('MSSQLSERVER','SQLAgent$MSSQLSERVER','SQLTELEMETRY$MSSQLSERVER')) {
                    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($svc) {
                        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
                        & sc.exe delete $serviceName | Out-String | Write-Output
                    }
                }
                for ($i = 0; $i -lt 30; $i++) { if (-not (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }
                foreach ($viewRoot in @('HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server')) {
                    $instanceNames = Join-Path $viewRoot 'Instance Names\SQL'
                    if (Test-Path $instanceNames) { Remove-ItemProperty -Path $instanceNames -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue }
                    if (![string]::IsNullOrWhiteSpace($instanceId)) { Remove-Item -LiteralPath (Join-Path $viewRoot $instanceId) -Recurse -Force -ErrorAction SilentlyContinue }
                }
                if (![string]::IsNullOrWhiteSpace($instanceId)) {
                    Remove-PathSafe (Join-Path $env:ProgramFiles ('Microsoft SQL Server\' + $instanceId))
                    Remove-PathSafe (Join-Path $env:ProgramData ('Microsoft\SQL Server\' + $instanceId))
                }
                Remove-PathSafe $dataRoot
            }

            function Repair-RsFxRegistry {
                $rsfx = 'HKLM:\SYSTEM\CurrentControlSet\Services\RsFx0700'
                if (!(Test-Path $rsfx)) { return }

                Step '修复 SQL Server RsFx 驱动注册表'
                $driver = Join-Path $env:windir 'System32\drivers\RsFx0700.sys'
                if (!(Test-Path $driver)) {
                    Write-Output 'RsFx0700.sys 不存在，删除损坏服务项，交给官方安装器重新创建。'
                    $svc = Get-Service -Name RsFx0700 -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status -ne 'Stopped') {
                        Stop-Service -Name RsFx0700 -Force -ErrorAction SilentlyContinue
                    }
                    & sc.exe delete RsFx0700 | Out-String | Write-Output
                    Remove-Item -LiteralPath $rsfx -Recurse -Force -ErrorAction SilentlyContinue
                    return
                }

                $instances = Join-Path $rsfx 'Instances'
                $instance = Join-Path $instances 'RsFx0700 MiniFilter Instance'
                $shares = Join-Path $rsfx 'InstancesShares'
                if (!(Test-Path $instances)) { New-Item -Path $instances -Force | Out-Null }
                if (!(Test-Path $instance)) { New-Item -Path $instance -Force | Out-Null }
                if (!(Test-Path $shares)) { New-Item -Path $shares -Force | Out-Null }
                New-ItemProperty -Path $instances -Name DefaultInstance -PropertyType String -Value 'RsFx0700 MiniFilter Instance' -Force | Out-Null
                New-ItemProperty -Path $instance -Name Altitude -PropertyType String -Value '41007.00' -Force | Out-Null
                New-ItemProperty -Path $instance -Name Flags -PropertyType DWord -Value 0 -Force | Out-Null
                New-ItemProperty -Path $rsfx -Name Start -PropertyType DWord -Value 4 -Force | Out-Null
            }

            function Ensure-SqlSectorCompatibility {
                Step '检查 SQL Server 磁盘扇区兼容性'
                $volumeRoot = [IO.Path]::GetPathRoot($dataRoot).TrimEnd('\')
                $sectorText = (& fsutil fsinfo sectorinfo $volumeRoot 2>&1 | Out-String)
                if (![string]::IsNullOrWhiteSpace($sectorText)) { Write-Output $sectorText }
                $physicalAtomicity = $null
                $physicalPerformance = $null
                $effectiveAtomicity = $null
                foreach ($line in ($sectorText -split "`r?`n")) {
                    if ($line -match '^\s*PhysicalBytesPerSectorForAtomicity\s*:\s*(\d+)') { $physicalAtomicity = [int64]$Matches[1] }
                    elseif ($line -match '^\s*PhysicalBytesPerSectorForPerformance\s*:\s*(\d+)') { $physicalPerformance = [int64]$Matches[1] }
                    elseif ($line -match '^\s*FileSystemEffectivePhysicalBytesPerSectorForAtomicity\s*:\s*(\d+)') { $effectiveAtomicity = [int64]$Matches[1] }
                }
                $checkBytes = 0
                foreach ($candidate in @($physicalAtomicity, $physicalPerformance, $effectiveAtomicity)) { if ($candidate -ne $null -and $candidate -gt $checkBytes) { $checkBytes = $candidate } }
                Write-Output ('SQL Server 扇区检查：physicalAtomicity=' + $physicalAtomicity + '; physicalPerformance=' + $physicalPerformance + '; effectiveAtomicity=' + $effectiveAtomicity + '; max=' + $checkBytes)
                if ($checkBytes -le 4096) { Write-Output '当前数据盘物理扇区不大于 4KB，无需修改 NVMe 兼容注册表。'; return }
                $nvmeKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device'
                New-Item -Path $nvmeKey -Force | Out-Null
                $expected = '* 4095'
                $current = @((Get-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes' -ErrorAction SilentlyContinue).ForcedPhysicalSectorSizeInBytes)
                if ($current -notcontains $expected) {
                    New-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes' -PropertyType MultiString -Value $expected -Force | Out-Null
                    Write-Output '检测到大于 4KB 的物理扇区，已按 Microsoft 官方 workaround 写入 ForcedPhysicalSectorSizeInBytes = * 4095。'
                } else { Write-Output '大于 4KB 的物理扇区仍可见，NVMe 兼容项已存在但尚未生效。' }
                Mark-SqlRestartRequired
                Fail ('当前 ' + $volumeRoot + ' 盘 SQL Server 检测到大于 4KB 的物理扇区。请重启设备，使 Microsoft 官方 NVMe 兼容设置生效后再点击“继续安装”。')
            }

            function Find-SetupMedia {
                return Get-ChildItem $mediaRoot -Filter 'setup.exe' -File -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
            }

            function Find-CompressedMedia {
                return Get-ChildItem $mediaRoot -Filter $mediaPackageSearchPattern -File -Recurse -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Name -ne 'setup.exe' -and
                        $_.FullName -ne $bootstrapper -and
                        $_.Extension -in @('.exe', '.iso')
                    } |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
            }

            try {
                if (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue) {
                    Step '检测到已存在 MSSQLSERVER，先执行配置修复'
                    Configure-SqlServer
                    try {
                        Restart-Service -Name MSSQLSERVER -Force -ErrorAction Stop
                    } catch {
                        Write-Output ('现有 SQL Server 重启失败：' + $_.Exception.Message)
                    }

                    if (Wait-ServiceState MSSQLSERVER Running 90) {
                        if (!(Ensure-CurrentWindowsSqlLogin)) {
                            Write-Output '现有 SQL Server 已启动，但未能自动修复当前 Windows 登录。'
                        }
                        Write-Output 'SQL Server 已存在，配置修复完成。'
                        Clear-SqlRestartMarker
                        Stop-Transcript | Out-Null
                        exit 0
                    }

                    Write-Output '现有 MSSQLSERVER 无法启动，按半安装残留处理并重新安装。'
                    Remove-BrokenSqlServer
                }

                $staleInstanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $defaultInstanceId
                if (Test-Path $staleInstanceKey) {
                    Write-Output '检测到 SQL Server 注册表残留但服务不可用，执行安装前清理。'
                    Remove-BrokenSqlServer
                }

                Step '准备 SQL Server 安装兼容项'
                $adminAccounts = Get-SqlAdminAccounts
                $adminArgument = Format-SqlSysadminAccountsArgument $adminAccounts
                Write-Output ('SQL Server 管理员账户：' + ($adminAccounts -join ', '))
                if (!(Test-Path $mediaRoot)) { New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null }
                Ensure-SqlSectorCompatibility
                Clear-SqlRestartMarker
                Repair-RsFxRegistry
                Step '清理 Windows 待处理文件重命名项'
                Clear-PendingFileRenameOperations

                Step '准备 SQL Server 数据目录'
                Ensure-Dir $dataRoot
                Ensure-Dir (Join-Path $dataRoot 'Data')
                Ensure-Dir (Join-Path $dataRoot 'Log')
                Ensure-Dir (Join-Path $dataRoot 'TempDB')
                Ensure-Dir (Join-Path $dataRoot 'TempDBLog')
                Ensure-Dir (Join-Path $dataRoot 'Backup')

                Step ('准备官方 ' + $displayName + ' 安装媒体')
                $downloadedSetup = Find-SetupMedia
                if ($downloadedSetup) { $setupExe = $downloadedSetup.FullName }
                $package = Find-CompressedMedia
                if (!(Test-Path $setupExe) -and !$package) {
                    $downloadArgs='/ACTION=Download /MEDIATYPE=' + $mediaType + ' /QUIET /MEDIAPATH="' + $mediaRoot + '"'
                    $download = Start-Process -FilePath $bootstrapper -ArgumentList $downloadArgs -Wait -PassThru -WindowStyle Hidden
                    if ($download.ExitCode -ne 0) { Fail ('官方安装媒体下载失败，退出码：' + $download.ExitCode) }
                    $downloadedSetup = Find-SetupMedia
                    if ($downloadedSetup) { $setupExe = $downloadedSetup.FullName }
                    $package = Find-CompressedMedia
                }

                if (!(Test-Path $setupExe)) {
                    if (!$package) { Fail ('未找到 ' + $displayName + ' 官方安装媒体。') }
                    if ($package.Extension -ieq '.iso') {
                        Step '挂载官方 SQL Server ISO 安装媒体'
                        try {
                            Mount-DiskImage -ImagePath $package.FullName -PassThru -ErrorAction Stop | Out-Null
                            $script:mountedIsoPath=$package.FullName
                            $image = Get-DiskImage -ImagePath $package.FullName -ErrorAction Stop
                            $volume=$null
                            for ($attempt=0; $attempt -lt 20 -and !$volume; $attempt++) {
                                $volume = Get-Volume -ErrorAction SilentlyContinue |
                                    Where-Object {
                                        $_.DriveLetter -and
                                        $_.FileSystem -eq 'CDFS' -and
                                        $_.Size -eq $image.Size
                                    } |
                                    Select-Object -First 1
                                if (!$volume) { Start-Sleep -Milliseconds 500 }
                            }
                            if (!$volume) { Fail '官方 SQL Server ISO 已挂载，但未找到可用盘符。' }
                            $isoRoot=$volume.DriveLetter + ':\'
                            $setupCandidate = Get-ChildItem -LiteralPath $isoRoot -Filter 'setup.exe' -File -Recurse -ErrorAction SilentlyContinue |
                                Select-Object -First 1
                            if (!$setupCandidate) { Fail '官方 SQL Server ISO 中未找到 setup.exe。' }
                            $setupExe=$setupCandidate.FullName
                        }
                        catch {
                            Fail ('官方 SQL Server ISO 挂载失败：' + $_.Exception.Message)
                        }
                    }
                    else {
                        if (Test-Path $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }
                        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
                        $extractArgs='/x:"' + $extractRoot + '" /q'
                        $extract = Start-Process -FilePath $package.FullName -ArgumentList $extractArgs -Wait -PassThru -WindowStyle Hidden
                        if ($extract.ExitCode -ne 0 -or !(Test-Path $setupExe)) { Fail ('官方安装媒体解压失败，退出码：' + $extract.ExitCode) }
                    }
                }

                Step ('运行官方 ' + $displayName + ' 安装程序')
                $args = @(
                    '/QS',
                    '/INDICATEPROGRESS',
                    '/ACTION=Install',
                    '/FEATURES=SQLENGINE',
                    '/INSTANCENAME=MSSQLSERVER',
                    '/IACCEPTSQLSERVERLICENSETERMS',
                    '/SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"',
                    '/SQLSVCSTARTUPTYPE=Automatic',
                    ('/SQLSYSADMINACCOUNTS=' + $adminArgument),
                    '/SECURITYMODE=SQL',
                    ('/SAPWD="' + $saPassword + '"'),
                    '/TCPENABLED=1',
                    ('/INSTALLSQLDATADIR="' + $dataRoot + '"'),
                    ('/SQLUSERDBDIR="' + (Join-Path $dataRoot 'Data') + '"'),
                    ('/SQLUSERDBLOGDIR="' + (Join-Path $dataRoot 'Log') + '"'),
                    ('/SQLTEMPDBDIR="' + (Join-Path $dataRoot 'TempDB') + '"'),
                    ('/SQLTEMPDBLOGDIR="' + (Join-Path $dataRoot 'TempDBLog') + '"'),
                    ('/SQLBACKUPDIR="' + (Join-Path $dataRoot 'Backup') + '"')
                )

                $install = Start-Process -FilePath $setupExe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
                if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) { Fail ('官方安装程序退出码：' + $install.ExitCode) }

                Configure-SqlServer
                Restart-Service -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState MSSQLSERVER Running 120)) { Fail 'MSSQLSERVER 服务启动超时。' }
                if (!(Ensure-CurrentWindowsSqlLogin)) { Fail 'SQL Server 已安装，但当前 Windows 用户登录验证失败。' }

                Clear-SqlRestartMarker
                Dismount-InstallMedia
                Write-Output ($displayName + ' 安装完成。')
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Fail $_.Exception.Message
            }
            """;
    }

    private static async Task RunElevatedPowerShellAsync(
        string scriptPath,
        Action<InstallProgress> progress,
        double from,
        double to,
        CancellationToken cancellationToken,
        bool requireExistingAdministrator = false)
    {
        var isAdministrator = ProcessRunner.IsAdministrator();
        if (requireExistingAdministrator && !isAdministrator)
        {
            throw new InvalidOperationException("该组件必须在已授权的管理员安装会话中运行，请重新点击安装并允许一次 UAC 授权。");
        }

        using var process = ProcessRunner.StartPowerShellFile(scriptPath, elevated: true);
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percent = Math.Min(to, from + 2);
                progress(new InstallProgress(
                    percent,
                    "管理员安装进程正在运行，请等待完成...",
                    InstallProgressStage.Installing,
                    percent));
                await Task.Delay(1000, cancellationToken);
            }

            if (process.ExitCode != 0)
            {
                var logTail = ReadInstallLogTail();
                if (HasSqlServerInstallContinuation)
                {
                    throw new InstallRestartRequiredException(SqlServerInstallContinuationMessage);
                }

                if (HasIisInstallContinuation)
                {
                    throw new InstallRestartRequiredException(IisInstallContinuationMessage);
                }

                var message = $"安装脚本退出码：{process.ExitCode}。日志目录：{GetTempDirectory()}";
                if (!string.IsNullOrWhiteSpace(logTail))
                {
                    message += $"{Environment.NewLine}{Environment.NewLine}最近日志：{Environment.NewLine}{logTail}";
                }

                throw new InvalidOperationException(message);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static string ReadInstallLogTail()
    {
        try
        {
            var directories = new[] { GetTempDirectory(), GetPackageDirectory() }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var latestLog = directories
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latestLog is null)
            {
                return string.Empty;
            }

            var text = ReadTextBestEffort(latestLog.FullName);
            var sqlHint = BuildSqlServerFailureHint(text);
            if (!string.IsNullOrWhiteSpace(sqlHint))
            {
                return sqlHint;
            }

            var lines = text
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !LooksLikeMojibake(line))
                .TakeLast(16);
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadTextBestEffort(string path)
    {
        const int maximumBytes = 512 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = stream.Length > maximumBytes ? stream.Length - maximumBytes : 0;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maximumBytes, stream.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }

        var bytes = read == buffer.Length ? buffer : buffer.Take(read).ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes);
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
        }

        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!LooksLikeMojibake(utf8))
        {
            return utf8;
        }

        try
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
            return LooksLikeMojibake(ansi) ? utf8 : ansi;
        }
        catch
        {
            return utf8;
        }
    }

    private static string BuildSqlServerFailureHint(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Contains("0x84be0bc2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Windows 当前存在待重启项", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("requires a restart", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("restart the computer", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 当前存在待重启项，SQL Server 官方安装器要求先重启系统。请重启 Windows 后再点击安装。";
        }

        if (text.Contains("EXCEPTION_STACK_OVERFLOW", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("0x851A001A", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wait on the Database Engine recovery handle", StringComparison.OrdinalIgnoreCase))
        {
            if (HasSqlServerInstallContinuation)
            {
                return SqlServerInstallContinuationMessage;
            }

            return "SQL Server 官方安装器在启动数据库引擎时失败。当前版本已在安装前检查 Windows NVMe 4KB 扇区兼容项；如果刚写入兼容项，请重启 Windows 后再安装。详细日志请查看 StoreData\\Work\\install-sqlserver-*.log 和 C:\\Program Files\\Microsoft SQL Server\\*\\Setup Bootstrap\\Log。";
        }

        if (text.Contains("有效物理扇区仍大于 4KB", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("大于 4KB 的物理扇区", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerInstallContinuationMessage;
        }

        return string.Empty;
    }

    private static bool LooksLikeMojibake(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var markers = new[]
        {
            "\u7ECC", "\u93C1", "\u6FB6", "\u8FAB", "\u89E6", "\u7F02", "\u7039",
            "\u9354", "\u95C3", "\u5A09", "\u7487", "\u9286", "\u20AC"
        };
        return markers.Count(marker => text.Contains(marker, StringComparison.Ordinal)) >= 2;
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory, bool elevated, CancellationToken cancellationToken)
    {
        using var process = ProcessRunner.Start(fileName, arguments, workingDirectory, elevated);
        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 退出码：{process.ExitCode}");
        }
    }

    private static async Task RunProcessWithOutputAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            elevated: false,
            cancellationToken: cancellationToken,
            captureOutput: true);
        if (result.ExitCode != 0)
        {
            var detail = result.CombinedOutput;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"{Path.GetFileName(fileName)} 退出码：{result.ExitCode}";
            }

            throw new InvalidOperationException(detail);
        }
    }

    private static string GetTempDirectory()
    {
        var work = Path.Combine(GetStoreDataRoot(), "Work");
        Directory.CreateDirectory(work);
        return work;
    }

    private static string GetPackageDirectory()
    {
        var packages = ComponentPaths.EnvironmentDownloadRoot;
        Directory.CreateDirectory(packages);
        return packages;
    }

    private static string GetStoreDataRoot()
    {
        var root = ComponentPaths.StoreDataRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for local state markers.
        }
    }

    private static string? FindDirectory(string root, string name)
    {
        return Directory.Exists(Path.Combine(root, name))
            ? Path.Combine(root, name)
            : Directory.GetDirectories(root, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool WindowsServiceExists(string serviceName)
    {
        try
        {
            return ProcessRunner.RunSynchronously(
                "sc.exe",
                $"query {Compat.QuoteCommandLineArgument(serviceName)}",
                ComponentPaths.ApplicationRoot,
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static string SelectTomcatInstallRoot(ComponentLocator locator)
    {
        foreach (var root in ComponentPaths.TomcatSearchRoots)
        {
            if (locator.FindTomcatRoot(root) is not null)
            {
                return root;
            }
        }

        return ComponentPaths.TomcatRoot;
    }

    private static bool IsNginxOperational(string nginxRoot) =>
        NginxRuntimeManager.IsRunningUnderRoot(nginxRoot) ||
        NginxWindowsServiceManager.IsRunningForRoot(nginxRoot);

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string EscapePowerShellPath(string path) => path.Replace("'", "''");
    private static string IisPendingUninstallMarker => Path.Combine(GetStoreDataRoot(), "RuntimeState", "iis-pending-uninstall-restart.flag");
    private static string IisUninstalledMarker => Path.Combine(GetStoreDataRoot(), "RuntimeState", "iis-uninstalled.flag");

    public void Dispose() => _httpClient.Dispose();

    private sealed class MySqlRuntimeTransaction : IDisposable
    {
        private readonly string _stagingContainer;
        private readonly string _backupRoot;
        private bool _finished;

        private MySqlRuntimeTransaction(string stagingContainer, string productRoot, string backupRoot)
        {
            _stagingContainer = stagingContainer;
            ProductRoot = productRoot;
            _backupRoot = backupRoot;
        }

        public string ProductRoot { get; }

        public static MySqlRuntimeTransaction Commit(
            string stagingContainer,
            string stagedRoot,
            string targetRoot,
            string rollbackBaseRoot)
        {
            var rollbackDirectory = Path.Combine(rollbackBaseRoot, ".mysql-rollback");
            Directory.CreateDirectory(rollbackDirectory);
            var backupRoot = Path.Combine(rollbackDirectory, $"mysql.{Guid.NewGuid():N}");

            if (Directory.Exists(targetRoot))
            {
                Directory.Move(targetRoot, backupRoot);
            }

            try
            {
                var oldData = Path.Combine(backupRoot, "data");
                var stagedData = Path.Combine(stagedRoot, "data");
                if (Directory.Exists(oldData))
                {
                    DeleteDirectoryBestEffort(stagedData);
                    Directory.Move(oldData, stagedData);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);
                Directory.Move(stagedRoot, targetRoot);
                return new MySqlRuntimeTransaction(stagingContainer, targetRoot, backupRoot);
            }
            catch
            {
                RestoreBackup(targetRoot, backupRoot);
                DeleteDirectoryBestEffort(stagingContainer);
                throw;
            }
        }

        public void Complete()
        {
            if (_finished)
            {
                return;
            }

            DeleteDirectoryBestEffort(_backupRoot);
            DeleteDirectoryBestEffort(_stagingContainer);
            _finished = true;
        }

        public void Rollback()
        {
            if (_finished)
            {
                return;
            }

            RestoreBackup(ProductRoot, _backupRoot);
            DeleteDirectoryBestEffort(_stagingContainer);
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Rollback();
            }
        }

        private static void RestoreBackup(string targetRoot, string backupRoot)
        {
            if (Directory.Exists(backupRoot))
            {
                var currentData = Path.Combine(targetRoot, "data");
                var backupData = Path.Combine(backupRoot, "data");
                if (Directory.Exists(currentData) && !Directory.Exists(backupData))
                {
                    Directory.Move(currentData, backupData);
                }
            }

            DeleteDirectoryBestEffort(targetRoot);
            if (Directory.Exists(backupRoot))
            {
                Directory.Move(backupRoot, targetRoot);
            }
        }
    }
}
