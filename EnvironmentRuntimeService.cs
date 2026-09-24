using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Sockets;
using System.Text;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace MCPanel;

public enum RuntimeStatusKind
{
    NotInstalled,
    Stopped,
    Running,
    Starting,
    Stopping,
    Unknown
}

public sealed record EnvironmentRuntimeState(
    bool IsInstalled,
    bool IsRunning,
    string StatusText,
    RuntimeStatusKind StatusKind = RuntimeStatusKind.Stopped,
    string? DetectedMySqlReleaseId = null,
    string? DetectedMySqlVersion = null,
    string? DetectedSqlServerReleaseId = null,
    string? DetectedSqlServerDisplayName = null);

public sealed record EnvironmentRuntimeSnapshot(
    IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> States,
    IReadOnlyDictionary<EnvironmentKind, string?> InstallDirectories);

public sealed record TomcatStartupProgress(
    double Percent,
    string Message,
    int ReadyApplications,
    int TotalApplications);

public sealed class EnvironmentRuntimeService
{
    private static readonly SemaphoreSlim ElevatedActionLock = new(1, 1);

    public EnvironmentRuntimeState GetState(EnvironmentKind kind)
    {
        return GetState(kind, new ComponentLocator());
    }

    private static EnvironmentRuntimeState GetState(EnvironmentKind kind, ComponentLocator locator)
    {
        return kind switch
        {
            EnvironmentKind.Tomcat => GetTomcatState(locator),
            EnvironmentKind.Nginx => GetNginxState(locator),
            EnvironmentKind.MySql => GetMySqlState(locator),
            EnvironmentKind.SqlServer => GetSqlServerState(locator),
            EnvironmentKind.Iis => GetIisState(locator),
            _ => NotInstalled()
        };
    }

    public IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> GetStates()
    {
        var locator = new ComponentLocator();
        var result = new Dictionary<EnvironmentKind, EnvironmentRuntimeState>();
        foreach (var kind in new[] { EnvironmentKind.Iis, EnvironmentKind.Nginx, EnvironmentKind.MySql, EnvironmentKind.SqlServer, EnvironmentKind.Tomcat })
        {
            result[kind] = GetState(kind, locator);
        }

        return result;
    }

    public string? GetInstallDirectory(EnvironmentKind kind)
    {
        return GetInstallDirectory(kind, new ComponentLocator());
    }

    private static string? GetInstallDirectory(EnvironmentKind kind, ComponentLocator locator)
    {
        try
        {
            var directory = kind switch
            {
                EnvironmentKind.Iis => ServiceExists("W3SVC") ? FindIisRoot() : null,
                EnvironmentKind.Nginx => Path.GetDirectoryName(locator.FindNginxExecutable()),
                EnvironmentKind.MySql => locator.FindMySqlRoot(GetServiceExecutablePath("MySQL80")),
                EnvironmentKind.SqlServer => FindSqlServerDataRoot() ?? FindServiceExecutableDirectory("MSSQLSERVER"),
                EnvironmentKind.Tomcat => locator.FindTomcatRoot(),
                EnvironmentKind.FrpTunnel => ComponentPaths.FrpRoot,
                _ => null
            };

            return !string.IsNullOrWhiteSpace(directory) && Directory.Exists(directory)
                ? Path.GetFullPath(directory)
                : null;
        }
        catch
        {
            return null;
        }
    }

    public IReadOnlyDictionary<EnvironmentKind, string?> GetInstallDirectories()
    {
        var locator = new ComponentLocator();
        var result = new Dictionary<EnvironmentKind, string?>();
        foreach (EnvironmentKind kind in Enum.GetValues(typeof(EnvironmentKind)))
        {
            result[kind] = GetInstallDirectory(kind, locator);
        }

        return result;
    }

    public EnvironmentRuntimeSnapshot GetSnapshot()
    {
        var locator = new ComponentLocator();
        var states = new Dictionary<EnvironmentKind, EnvironmentRuntimeState>();
        var installDirectories = new Dictionary<EnvironmentKind, string?>();

        foreach (var kind in new[]
                 {
                     EnvironmentKind.Iis,
                     EnvironmentKind.Nginx,
                     EnvironmentKind.MySql,
                     EnvironmentKind.SqlServer,
                     EnvironmentKind.Tomcat
                 })
        {
            states[kind] = GetState(kind, locator);
            installDirectories[kind] = GetInstallDirectory(kind, locator);
        }

        installDirectories[EnvironmentKind.FrpTunnel] = GetInstallDirectory(EnvironmentKind.FrpTunnel, locator);
        return new EnvironmentRuntimeSnapshot(states, installDirectories);
    }

    public bool IsRunning(EnvironmentKind kind)
    {
        var locator = new ComponentLocator();
        return kind switch
        {
            EnvironmentKind.Tomcat => GetTomcatState(locator).IsRunning,
            EnvironmentKind.Nginx => GetNginxState(locator).IsRunning,
            EnvironmentKind.MySql => GetMySqlState(locator).IsRunning,
            EnvironmentKind.SqlServer => GetSqlServerState(locator).IsRunning,
            EnvironmentKind.Iis => GetIisState(locator).IsRunning,
            _ => false
        };
    }

    public void OpenIisManager()
    {
        var inetmgr = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "InetMgr.exe");
        if (!File.Exists(inetmgr))
        {
            throw new FileNotFoundException("未找到 IIS 管理器。请先安装 IIS 管理控制台。", inetmgr);
        }

        using var manager = ProcessRunner.StartFile(
            inetmgr,
            string.Empty,
            Path.GetDirectoryName(inetmgr) ?? ComponentPaths.ApplicationRoot,
            windowStyle: ProcessWindowStyle.Normal);
    }

    internal static void LaunchTomcatStartConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var startup = Path.Combine(binDirectory, "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("未找到 Tomcat 普通启动脚本。", startup);
        }

        // Normal Start/Restart uses Tomcat's standard `start` semantics. Keep
        // the explicit Catalina entry point separate so only that operation is
        // tied to `catalina.bat run` and its diagnostic console lifecycle.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "start-tomcat-server.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Startup
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            set "TITLE=MCPanel Tomcat Server"
            cd /d "{binDirectory}"
            call startup.bat
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var tomcatWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);
    }
    internal static void LaunchTomcatConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var catalina = Path.Combine(binDirectory, "catalina.bat");
        if (!File.Exists(catalina))
        {
            throw new FileNotFoundException("未找到 Tomcat Catalina 启动脚本。", catalina);
        }

        // A previous Catalina console can remain at the post-run pause after
        // Tomcat has stopped. Close only MCPanel-owned launcher windows before
        // opening a fresh shared-server console so repeated starts/restarts do
        // not accumulate stale CMD windows.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "run-tomcat-server.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Server
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            cd /d "{binDirectory}"
            call catalina.bat run
            echo.
            echo Tomcat has exited. Review the Catalina output above.
            echo Press any key to close this window.
            pause >nul
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var tomcatWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);
    }

    private static void RetireLegacyTomcatWindowsService(string tomcatRoot)
    {
        if (!TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
        {
            return;
        }

        try
        {
            TomcatWindowsServiceManager.Stop();
        }
        catch (Exception serviceError)
        {
            EnvironmentOperationDiagnostics.RecordFailure(
                "环境管理",
                "停止旧 Tomcat Windows Service 包装器",
                serviceError);
        }

        try
        {
            TomcatWindowsServiceManager.Delete();
        }
        catch (Exception serviceError)
        {
            EnvironmentOperationDiagnostics.RecordFailure(
                "环境管理",
                "删除旧 Tomcat Windows Service 包装器",
                serviceError);
        }
    }

    public Task<string> StartTomcatInCatalinaConsoleAsync(
        CancellationToken cancellationToken = default, Action<TomcatStartupProgress>? tomcatProgress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tomcatRoot = RequireTomcatRoot();
        if (TomcatProductInstanceManager.IsSharedTomcatRunning())
        {
            throw new InvalidOperationException("Tomcat Server 已经在运行。请先停止后再重新打开控制台。");
        }

        RetireLegacyTomcatWindowsService(tomcatRoot);
        LaunchTomcatConsole(tomcatRoot);
        return Task.FromResult(
            "Tomcat Server CMD 控制台已打开。MCPanel 不隐藏启动，也不等待端口或执行 HTTP 就绪诊断；请直接查看窗口中的 Catalina 输出。");
    }
    public NginxRuntimeOptions GetNginxOptions()
    {
        return NginxRuntimeManager.LoadOptions() ?? new NginxRuntimeOptions();
    }

    public Task<string> SaveNginxOptionsAsync(NginxRuntimeOptions options, CancellationToken cancellationToken = default,
        string? expectedRevision = null) => NginxConfigurationCoordinator.RunAsync(async () =>
    {
        NginxConfigurationCoordinator.EnsureUnchanged(expectedRevision, GetNginxOptions());
        return await SaveNginxOptionsCoreAsync(options, cancellationToken);
    }, cancellationToken);

    private async Task<string> SaveNginxOptionsCoreAsync(NginxRuntimeOptions options, CancellationToken cancellationToken)
    {
        var normalized = NginxRuntimeManager.NormalizeOptions(options);
        NginxRuntimeManager.ValidateOptions(normalized);

        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;
        var isRunning = IsNginxOperational(nginxRoot);
        var savedOptions = NginxRuntimeManager.LoadOptions();
        var currentPorts = NginxRuntimeManager.ReadConfiguredListenPorts(nginxRoot).ToHashSet();
        if (currentPorts.Count == 0 && savedOptions is not null)
        {
            currentPorts = NginxRuntimeManager.GetEffectiveListenPorts(savedOptions).ToHashSet();
        }
        var portsToCheck = NginxRuntimeManager.GetEffectiveListenPorts(normalized).ToArray();
        if (portsToCheck.Length == 0)
        {
            portsToCheck = [normalized.ListenPort];
        }

        foreach (var port in portsToCheck)
        {
            var ownedByRunningNginx = isRunning && currentPorts.Contains(port);
            if (!ownedByRunningNginx && !NginxRuntimeManager.IsPortAvailable(port))
            {
                throw new InvalidOperationException($"端口 {port} 已被占用，请换一个监听端口。");
            }
        }

        var transaction = CaptureNginxTransaction(nginxRoot);
        try
        {
            NginxRuntimeManager.CleanConfigFiles(nginxRoot);
            NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
            await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            NginxRuntimeManager.SaveOptions(normalized);
        }
        catch (Exception ex)
        {
            try { transaction.Rollback(); }
            catch (Exception rollbackError) { throw new AggregateException("Nginx 配置保存失败，且回滚未完成。", ex, rollbackError); }
            throw new InvalidOperationException($"Nginx 配置保存失败，已恢复原配置：{ex.Message}", ex);
        }

        if (isRunning)
        {
            try
            {
                await RunFileAsync(nginxExe, "-s reload", nginxRoot, false, cancellationToken);
                return $"Nginx 配置已保存并热重载。{normalized.Summary}";
            }
            catch (Exception reloadError)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    return $"Nginx 配置已保存，但热重载被取消：{reloadError.Message}。请在“环境”页面重启 Nginx。";
                }

                try
                {
                    await RestartAsync(EnvironmentKind.Nginx, cancellationToken);
                    return $"Nginx 配置已保存并重启生效。{normalized.Summary}";
                }
                catch (Exception restartError)
                {
                    return $"Nginx 配置已保存，但热重载和自动重启均未完成：{restartError.Message}。请在“环境”页面重启 Nginx。";
                }
            }
        }

        return $"Nginx 配置已保存。{normalized.Summary}";
    }

    private static ConfigurationFileTransaction CaptureNginxTransaction(string nginxRoot)
    {
        var transaction = new ConfigurationFileTransaction();
        transaction.Capture(NginxRuntimeManager.StateFile);
        var directory = Path.Combine(nginxRoot, "conf");
        transaction.Capture(Path.Combine(directory, "nginx.conf"));
        if (Directory.Exists(directory))
            foreach (var path in Directory.EnumerateFiles(directory, "*.conf", SearchOption.AllDirectories))
                transaction.Capture(path);
        return transaction;
    }

    public async Task<string> StartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default, Action<TomcatStartupProgress>? tomcatProgress = null)
    {
        switch (kind)
        {
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                var tomcatPorts = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
                if (tomcatPorts.Length == 0)
                {
                    throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。请先修复产品绑定。");
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    throw new InvalidOperationException("Tomcat Server 已经在运行。请使用“重启”重新打开可见控制台。");
                }

                TomcatProductStartupManager.RemoveRegistration();
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);

                // Shared Tomcat runs in the interactive user session from 1.3.43.
                // Retire the old Session-0 service wrapper so it cannot silently
                // start a second hidden shared server after an update or reboot.
                RetireLegacyTomcatWindowsService(tomcatRoot);
                LaunchTomcatStartConsole(tomcatRoot);

                return $"Tomcat Server 已按标准 start 模式启动，普通 Tomcat CMD 窗口已打开。需要持续查看 Catalina 前台输出时请使用“以 Catalina 方式启动”。日志目录：{Path.Combine(tomcatRoot, "logs")}";            case EnvironmentKind.Nginx:
                return await StartNginxAsync(cancellationToken);
            case EnvironmentKind.MySql:
                return await RunMySqlServiceActionAsync("start", cancellationToken);
            case EnvironmentKind.SqlServer:
                await RunSqlServerServiceActionAsync("start", cancellationToken);
                return $"SQL Server 服务已启动。{FormatSqlServerConnectionText()}";
            case EnvironmentKind.Iis:
                await RunElevatedPowerShellAsync(BuildIisServiceActionScript("START"), cancellationToken);
                return "IIS 已启动。";
            default:
                throw new NotSupportedException("该环境不支持启动操作。");
        }
    }

    public async Task<string> StopAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)
    {
        switch (kind)
        {
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    try
                    {
                        await Task.Run(() => TomcatWindowsServiceManager.Stop(), cancellationToken);
                    }
                    catch (Exception serviceError)
                    {
                        // SCM is not runtime truth. Record wrapper-control failures and
                        // continue with the real CATALINA_BASE process cleanup below.
                        EnvironmentOperationDiagnostics.RecordFailure(
                            "环境管理",
                            "发送 Tomcat Windows Service 停止请求",
                            serviceError);
                    }
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    await TomcatProductInstanceManager.StopSharedTomcatAsync(cancellationToken);
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    throw new InvalidOperationException("Tomcat Server 共享进程未能停止；单应用实例未受影响。请检查共享 Tomcat Java 进程。");
                }

                // catalina.bat run returns to run-tomcat-server.cmd after the
                // Java process exits; that wrapper deliberately pauses so an
                // unexpected crash remains diagnosable. A deliberate Stop or
                // Restart must close the old managed console as well.
                TomcatConsoleWindowManager.CloseExistingConsoleWindows();
                return "Tomcat Server 已停止，旧 Catalina CMD 控制台已关闭；单应用 Tomcat 实例不受影响。";
            case EnvironmentKind.Nginx:
                return await StopNginxAsync(cancellationToken);
            case EnvironmentKind.MySql:
                return await RunMySqlServiceActionAsync("stop", cancellationToken);
            case EnvironmentKind.SqlServer:
                await RunSqlServerServiceActionAsync("stop", cancellationToken);
                return "SQL Server 服务已停止。";
            case EnvironmentKind.Iis:
                await RunElevatedPowerShellAsync(BuildIisServiceActionScript("STOP"), cancellationToken);
                return "IIS 已停止。";
            default:
                throw new NotSupportedException("该环境不支持停止操作。");
        }
    }

    public async Task<string> RestartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default,
        Action<TomcatStartupProgress>? tomcatProgress = null)
    {
        if (kind == EnvironmentKind.Iis)
        {
            await RunElevatedPowerShellAsync(BuildIisServiceActionScript("RESTART"), cancellationToken);
            return "IIS 已重启。";
        }

        if (kind == EnvironmentKind.MySql)
        {
            return await RunMySqlServiceActionAsync("restart", cancellationToken);
        }

        if (kind == EnvironmentKind.SqlServer)
        {
            await RunSqlServerServiceActionAsync("restart", cancellationToken);
            return $"SQL Server 已重启。{FormatSqlServerConnectionText()}";
        }

        try

        {
            await StopAsync(kind, cancellationToken);
        }
        catch (Exception stopError)
        {
            // A stop command may report an error even though the process/service
            // actually reached Stopped. Continue only in that benign case.
            if (IsRunning(kind))
            {
                throw new InvalidOperationException(
                    $"{DisplayName(kind)} 停止失败，当前仍在运行，已取消重启：{stopError.Message}",
                    stopError);
            }
        }

        await Task.Delay(1200, cancellationToken);
        await StartAsync(kind, cancellationToken);

        return $"{DisplayName(kind)} 已重启。";
    }
    public Task<string> UninstallAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)
        => Task.Run(() => UninstallCoreAsync(kind, cancellationToken), cancellationToken);

    private async Task<string> UninstallCoreAsync(EnvironmentKind kind, CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                await TryStopAsync(kind, cancellationToken);
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);
                await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    TomcatWindowsServiceManager.Delete();
                }
                DeleteDirectory(tomcatRoot);
                TomcatProductStartupManager.RemoveRegistration();
                return "Tomcat Windows 服务、共享运行环境及实例运行进程已卸载；产品文件保持由产品管理单独处理。";
            case EnvironmentKind.Nginx:
                var nginxRoot = Path.GetDirectoryName(RequireNginxExe())!;
                NginxRuntimeManager.EnsureSafeDeleteRoot(nginxRoot);
                await StopNginxAsync(cancellationToken);
                if (NginxWindowsServiceManager.IsRegisteredForRoot(nginxRoot))
                {
                    await RunNginxServiceActionAsync("delete", cancellationToken);
                }
                NginxRuntimeManager.DeleteRoot(nginxRoot);
                return "Nginx 已卸载。";
            case EnvironmentKind.MySql:
                await UninstallMySqlCompletelyAsync(cancellationToken);
                return "MySQL 已卸载。";
            case EnvironmentKind.SqlServer:
                await UninstallSqlServerCompletelyAsync(cancellationToken);
                return "SQL Server 默认实例及 MCPanel 管理的数据、安装缓存和防火墙规则已卸载；共享驱动、其他实例及全局 SQL Server 目录已保留。";
            case EnvironmentKind.Iis:
                return await UninstallIisCompletelyAsync(cancellationToken);
            default:
                throw new NotSupportedException("该环境不支持卸载操作。");
        }
    }

    private async Task TryStopAsync(EnvironmentKind kind, CancellationToken cancellationToken)
    {
        try
        {
            await StopAsync(kind, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception error)
        {
            throw new InvalidOperationException(
                $"{DisplayName(kind)} 停止失败，已取消卸载以避免删除正在使用的文件。请先手动停止后重试。",
                error);
        }
    }

    public async Task<string> ChangeMySqlPortAsync(int newPort, CancellationToken cancellationToken = default)
    {
        if (newPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(newPort), "MySQL 端口必须在 1 到 65535 之间。");
        }

        var mysqlRoot = FindMySqlRoot() ?? throw new DirectoryNotFoundException("未找到 MySQL 安装目录。");
        var myIni = Path.Combine(mysqlRoot, "my.ini");
        if (!File.Exists(myIni))
        {
            throw new FileNotFoundException("未找到 MySQL 配置文件。", myIni);
        }

        var credentials = LoadEffectiveMySqlCredentials();
        var originalIni = await FileCompat.ReadAllTextAsync(myIni, cancellationToken);
        var oldPort = ReadMySqlIniPort(originalIni) ?? credentials.Port;
        if (newPort == oldPort)
        {
            MySqlCredentialStore.Save(credentials with { Port = newPort });
            var refresh = ProductConfigurationService.RefreshInstalledMySqlConnections();
            return $"MySQL 当前已使用端口 {newPort}。{refresh.Message}。";
        }

        if (!IsPortAvailable(newPort))
        {
            throw new InvalidOperationException($"端口 {newPort} 已被其他程序占用，请更换端口。");
        }

        var updatedIni = UpdateMySqlIniPort(originalIni, newPort);
        var wasRunning = ServiceRunning("MySQL80");

        try
        {
            if (wasRunning)
            {
                await RunMySqlServiceActionAsync("stop", cancellationToken);
            }

            await FileCompat.WriteAllTextAsync(myIni, updatedIni, new UTF8Encoding(false), cancellationToken);
            MySqlCredentialStore.Save(credentials with { Port = newPort });

            if (wasRunning)
            {
                await RunMySqlServiceActionAsync("start", cancellationToken);
                await WaitForTcpPortAsync(credentials.Host, newPort, TimeSpan.FromSeconds(20), cancellationToken);
            }

            var refresh = ProductConfigurationService.RefreshInstalledMySqlConnections();
            var result = wasRunning
                ? $"MySQL 端口已从 {oldPort} 修改为 {newPort}，服务已重新启动。"
                : $"MySQL 端口已从 {oldPort} 修改为 {newPort}，将在下次启动时生效。";
            return $"{result}{refresh.Message}。";
        }
        catch
        {
            await FileCompat.WriteAllTextAsync(myIni, originalIni, new UTF8Encoding(false), CancellationToken.None);
            MySqlCredentialStore.Save(credentials with { Port = oldPort });

            if (wasRunning && !ServiceRunning("MySQL80"))
            {
                try
                {
                    await RunMySqlServiceActionAsync("start", CancellationToken.None);
                }
                catch
                {
                    // Preserve the original failure; the log still contains the rollback startup details.
                }
            }

            throw;
        }
    }

    public async Task<string> ChangeMySqlPasswordAsync(string newPassword, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(newPassword) || newPassword.Length > 64)
        {
            throw new ArgumentException("MySQL 密码不能为空且最多 64 个字符，允许使用纯数字密码。", nameof(newPassword));
        }

        if (newPassword.Any(char.IsControl))
        {
            throw new ArgumentException("MySQL 密码不能包含控制字符。", nameof(newPassword));
        }

        var mysqlExe = FindMySqlExe() ?? throw new FileNotFoundException("未找到 mysql.exe。");
        var credentials = LoadEffectiveMySqlCredentials();
        if (!ServiceRunning("MySQL80"))
        {
            throw new InvalidOperationException("MySQL 服务未运行，请先启动 MySQL。");
        }

        await WaitForTcpPortAsync(credentials.Host, credentials.Port, TimeSpan.FromSeconds(8), cancellationToken);
        var version = await ExecuteMySqlAsync(mysqlExe, credentials, "SELECT VERSION();", cancellationToken);
        var majorVersion = ParseMySqlMajorVersion(version);
        var escapedPassword = EscapeMySqlString(newPassword);
        var changeSql = majorVersion >= 8
            ? $"ALTER USER CURRENT_USER() IDENTIFIED BY '{escapedPassword}';"
            : $"SET PASSWORD = PASSWORD('{escapedPassword}');";

        await ExecuteMySqlAsync(mysqlExe, credentials, changeSql, cancellationToken);
        var updatedCredentials = credentials with { Password = newPassword };

        try
        {
            await ExecuteMySqlAsync(mysqlExe, updatedCredentials, "SELECT 1;", cancellationToken);
        }
        catch (Exception verificationError)
        {
            try
            {
                var rollbackPassword = EscapeMySqlString(credentials.Password);
                var rollbackSql = majorVersion >= 8
                    ? $"ALTER USER CURRENT_USER() IDENTIFIED BY '{rollbackPassword}';"
                    : $"SET PASSWORD = PASSWORD('{rollbackPassword}');";
                await ExecuteMySqlAsync(mysqlExe, updatedCredentials, rollbackSql, CancellationToken.None);
            }
            catch
            {
                // Keep the original verification error and do not save unverified credentials.
            }

            throw new InvalidOperationException("MySQL 已执行密码修改，但使用新密码验证失败，面板未保存新密码。", verificationError);
        }

        MySqlCredentialStore.Save(updatedCredentials);
        var refresh = ProductConfigurationService.RefreshInstalledMySqlConnections();
        return $"MySQL root 密码已修改并验证成功。{refresh.Message}。";
    }

    private static async Task<string> StartNginxAsync(CancellationToken cancellationToken)
    {
        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;

        if (IsNginxOperational(nginxRoot))
        {
            var current = NginxRuntimeManager.LoadOptions();
            return current is not null ? $"Nginx 已在运行。{current.Summary}" : "Nginx 已在运行。";
        }

        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxRoot))
            {
                throw new InvalidOperationException(
                    $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已存在，但未指向当前 Nginx 目录，已停止启动以保护现有服务。");
            }

            await RunNginxServiceActionAsync("start", cancellationToken);
            for (var attempt = 0; attempt < 10; attempt++)
            {
                await Task.Delay(300, cancellationToken);
                if (IsNginxOperational(nginxRoot))
                {
                    var options = NginxRuntimeManager.LoadOptions();
                    return options is not null
                        ? $"Nginx Windows 服务已启动。{options.Summary}"
                        : "Nginx Windows 服务已启动。";
                }
            }

            var serviceLog = NginxRuntimeManager.ReadRecentErrorLog(nginxRoot);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(serviceLog)
                ? "Nginx Windows 服务已启动，但未检测到 nginx.exe。"
                : $"Nginx Windows 服务启动失败。最近日志：{serviceLog}");
        }

        var listenPort = await ConfigureAndTestNginxAsync(nginxExe, nginxRoot, cancellationToken);
        StartDetached(nginxExe, nginxRoot);

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(300, cancellationToken);
            if (IsNginxOperational(nginxRoot))
            {
                var options = NginxRuntimeManager.LoadOptions();
                return options is not null ? $"Nginx 已启动。{options.Summary}" : $"Nginx 已启动，监听端口 {listenPort}。";
            }
        }

        var log = NginxRuntimeManager.ReadRecentErrorLog(nginxRoot);
        throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
            ? "Nginx 启动失败，未检测到运行中的 nginx.exe。"
            : $"Nginx 启动失败。最近日志：{log}");
    }

    private static async Task<string> StopNginxAsync(CancellationToken cancellationToken)
    {
        var nginxExe = RequireNginxExe();
        var nginxRoot = Path.GetDirectoryName(nginxExe)!;

        var nginxIsRunning = IsNginxOperational(nginxRoot);
        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxRoot))
            {
                if (NginxWindowsServiceManager.IsRunning())
                {
                    throw new InvalidOperationException(
                        $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已运行，但未指向当前 Nginx 目录，已停止操作以保护现有服务。");
                }

                // A stopped foreign service must not block stopping a
                // separately launched MCPanel Nginx process below.
            }
            else
            {
                if (!nginxIsRunning && !NginxWindowsServiceManager.IsRunning())
                {
                    return "Nginx 当前未运行。";
                }

                await RunNginxServiceActionAsync("stop", cancellationToken);
                for (var attempt = 0; attempt < 10; attempt++)
                {
                    await Task.Delay(250, cancellationToken);
                    if (!NginxWindowsServiceManager.IsRunningForRoot(nginxRoot))
                    {
                        return "Nginx Windows 服务已停止。";
                    }
                }

                NginxRuntimeManager.KillProcessesUnderRoot(nginxRoot);
                if (NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
                {
                    throw new InvalidOperationException(
                        "Nginx Windows 服务已收到停止请求，但 nginx.exe 仍在运行，已取消后续卸载。请先结束占用进程后重试。");
                }

                return "Nginx Windows 服务已停止。";
            }
        }

        if (!nginxIsRunning)
        {
            return "Nginx 当前未运行。";
        }

        try
        {
            await RunFileAsync(nginxExe, "-s quit", nginxRoot, false, cancellationToken);
        }
        catch
        {
            try
            {
                await RunFileAsync(nginxExe, "-s stop", nginxRoot, false, cancellationToken);
            }
            catch
            {
                // Fall back to killing only the nginx.exe instances under the configured Nginx root.
            }
        }

        for (var attempt = 0; attempt < 10; attempt++)
        {
            await Task.Delay(250, cancellationToken);
            if (!NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
            {
                return "Nginx 已停止。";
            }
        }

        NginxRuntimeManager.KillProcessesUnderRoot(nginxRoot);
        if (NginxRuntimeManager.IsRunningUnderRoot(nginxRoot))
        {
            throw new InvalidOperationException(
                "Nginx 进程未能完全停止，已取消后续卸载。请先结束占用进程后重试。");
        }

        return "Nginx 已停止。";
    }

    private static Task<int> ConfigureAndTestNginxAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken) =>
        NginxConfigurationCoordinator.RunAsync(async () =>
        {
            var transaction = CaptureNginxTransaction(nginxRoot);
            try { return await ConfigureAndTestNginxCoreAsync(nginxExe, nginxRoot, cancellationToken); }
            catch (Exception error)
            {
                try { transaction.Rollback(); }
                catch (Exception rollbackError) { throw new AggregateException("Nginx 启动配置失败，且回滚未完成。", error, rollbackError); }
                throw;
            }
        }, cancellationToken);

    private static async Task<int> ConfigureAndTestNginxCoreAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken)
    {
        var savedOptions = NginxRuntimeManager.LoadOptions();
        if (savedOptions is { Rules.Count: > 0 })
        {
            var normalized = NginxRuntimeManager.NormalizeOptions(savedOptions);

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
                await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
                NginxRuntimeManager.SaveOptions(normalized);
                return normalized.ListenPort;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                var ports = string.Join("、", normalized.EnabledRules.Select(rule => rule.ListenPort).Distinct().OrderBy(port => port));
                throw new InvalidOperationException($"Nginx 监听端口被占用，请在“管理”中调整端口后再启动。当前端口：{ports}。", ex);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        Exception? lastError = null;
        foreach (var port in NginxRuntimeManager.BuildPortCandidates())
        {
            if (!NginxRuntimeManager.IsPortAvailable(port))
            {
                continue;
            }

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteListenPort(nginxRoot, port);
                await RunFileAsync(nginxExe, "-t", nginxRoot, false, cancellationToken);
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

    private static async Task UninstallSqlServerCompletelyAsync(CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"uninstall-sqlserver-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"uninstall-sqlserver-{operationId}.log");
        Directory.CreateDirectory(workDirectory);
        try
        {
            await FileCompat.WriteAllTextAsync(
                script,
                BuildSqlServerUninstallScript(Path.GetFileName(log)),
                new UTF8Encoding(true),
                cancellationToken);
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "uninstall-sqlserver-*.log*", log);
        }
    }

    private static async Task UninstallMySqlCompletelyAsync(CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"uninstall-mysql-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"uninstall-mysql-{operationId}.log");
        Directory.CreateDirectory(workDirectory);
        try
        {
            await FileCompat.WriteAllTextAsync(
                script,
                BuildMySqlUninstallScript(FindDedicatedMySqlRoot(), Path.GetFileName(log)),
                new UTF8Encoding(true),
                cancellationToken);
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "uninstall-mysql-*.log*", log);
        }
    }

    private static async Task<string> UninstallIisCompletelyAsync(CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"uninstall-iis-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"uninstall-iis-{operationId}.log");
        Directory.CreateDirectory(workDirectory);
        try
        {
            await FileCompat.WriteAllTextAsync(
                script,
                BuildIisUninstallScript(Path.GetFileName(log), File.Exists(IisPendingUninstallMarker)),
                new UTF8Encoding(true),
                cancellationToken);
            // A Windows Update reboot flag can be unrelated to this uninstall. A
            // failed script must remain a failure; only its explicit 3010 requests
            // the IIS continuation flow.
            var exitCode = await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
            var restartRequired = exitCode == 3010;
            if (restartRequired)
            {
                TryDeleteFile(IisUninstalledMarker);
                WriteIisUninstallContinuationMarker();
                return IisUninstallContinuationMessage;
            }

            TryDeleteFile(IisPendingUninstallMarker);
            WriteIisUninstalledMarker();
            return "IIS 已卸载，相关 Windows 组件已禁用。";
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "uninstall-iis-*.log*", log);
        }
    }

    private static void WriteIisUninstallContinuationMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IisPendingUninstallMarker)!);
        AtomicFile.WriteAllText(
            IisPendingUninstallMarker,
            DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            new UTF8Encoding(false));
    }

    private static void WriteIisUninstalledMarker()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(IisUninstalledMarker)!);
        AtomicFile.WriteAllText(
            IisUninstalledMarker,
            DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture),
            new UTF8Encoding(false));
    }

    internal static string BuildIisUninstallScript(string logFileName = "uninstall-iis.log", bool isContinuation = false)
    {
        var storeDataRoot = EscapePowerShellPath(StoreDataRoot);
        var escapedLogFileName = EscapePowerShellPath(logFileName);
        var features = new[]
        {
            "IIS-ODBCLogging",
            "IIS-WindowsAuthentication",
            "IIS-BasicAuthentication",
            "IIS-ServerSideIncludes",
            "IIS-CGI",
            "IIS-ASP",
            "IIS-ASPNET45",
            "IIS-ASPNET",
            "IIS-DirectoryBrowsing",
            "IIS-DefaultDocument",
            "IIS-StaticContent",
            "IIS-ISAPIFilter",
            "IIS-ISAPIExtensions",
            "IIS-Metabase",
            "IIS-WebServerManagementTools",
            "IIS-IIS6ManagementCompatibility",
            "IIS-ManagementScriptingTools",
            "IIS-ManagementConsole",
            "IIS-HttpCompressionDynamic",
            "IIS-HttpCompressionStatic",
            "IIS-Performance",
            "IIS-HttpTracing",
            "IIS-RequestMonitor",
            "IIS-HttpLogging",
            "IIS-HealthAndDiagnostics",
            "IIS-NetFxExtensibility45",
            "IIS-NetFxExtensibility",
            "IIS-RequestFiltering",
            "IIS-URLAuthorization",
            "IIS-Security",
            "IIS-ApplicationDevelopment",
            "IIS-HttpRedirect",
            "IIS-HttpErrors",
            "IIS-CommonHttpFeatures",
            "IIS-WebServer",
            "IIS-WebServerRole",
            "WAS-ConfigurationAPI",
            "WAS-NetFxEnvironment",
            "WAS-ProcessModel",
            "WAS-WindowsActivationService"
        };

        var featureList = string.Join(
            $",{Environment.NewLine}",
            features.Select(feature => $"  '{feature}'"));

        return $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $workRoot = Join-Path '{{storeDataRoot}}' 'Work'
            New-Item -ItemType Directory -Path $workRoot -Force | Out-Null
            $log = Join-Path $workRoot '{{escapedLogFileName}}'
            $osVersion = [Environment]::OSVersion.Version
            $legacyIis = $osVersion.Major -eq 6 -and $osVersion.Minor -lt 2
            $continuingUninstall = ${{(isContinuation ? "true" : "false")}}
            Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
            Start-Transcript -Path $log | Out-Null

            function Invoke-Step([string]$name, [scriptblock]$body) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
                & $body
            }

            try {
                Invoke-Step 'Stop IIS services' {
                    $iisreset = Join-Path $env:windir 'System32\iisreset.exe'
                    if (Test-Path -LiteralPath $iisreset) {
                        & $iisreset /stop 2>&1 | ForEach-Object { Write-Output $_ }
                    }
                    else {
                        Write-Output '未找到 iisreset.exe，跳过 iisreset，继续停止 IIS 服务。'
                    }
                    foreach ($service in @('W3SVC', 'WAS', 'AppHostSvc')) {
                        $svc = Get-Service -Name $service -ErrorAction SilentlyContinue
                        if ($svc -and $svc.Status -ne 'Stopped') {
                            Stop-Service -Name $service -Force -ErrorAction SilentlyContinue
                        }
                    }
                }

                $script:restartNeeded = $false
                Invoke-Step 'Remove URL Rewrite' {
                    $roots = @(
                        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                    )
                    foreach ($root in $roots) {
                        if (!(Test-Path $root)) { continue }
                        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                            $item = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                            if ($item.DisplayName -and $item.DisplayName -match 'URL Rewrite') {
                                if ($item.PSChildName -match '^\{.*\}$') {
                                    $process = Start-Process msiexec.exe -ArgumentList @('/x', $item.PSChildName, '/qn', '/norestart') -Wait -PassThru -WindowStyle Hidden
                                    if (@(0, 1605, 1614, 3010) -notcontains $process.ExitCode) {
                                        throw ('URL Rewrite 卸载失败，退出码：' + $process.ExitCode)
                                    }
                                    if ($process.ExitCode -eq 3010) { $script:restartNeeded = $true }
                                }
                            }
                        }
                    }
                }

                if ($legacyIis) {
                    Invoke-Step 'Remove IIS 7 role with pkgmgr' {
                        $pkgmgr = Join-Path $env:windir 'System32\pkgmgr.exe'
                        if (!(Test-Path -LiteralPath $pkgmgr)) { throw '未找到 Windows Server 2008 的 pkgmgr.exe。' }
                        if (Get-Service W3SVC -ErrorAction SilentlyContinue) {
                            $result = Start-Process -FilePath $pkgmgr -ArgumentList '/uu:IIS-WebServerRole;WAS-WindowsActivationService;WAS-ProcessModel' -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop
                            if ($result.ExitCode -eq 3010) { $script:restartNeeded = $true }
                            elseif ($result.ExitCode -ne 0) { throw ('IIS 7 卸载失败，pkgmgr 退出码：' + $result.ExitCode) }
                        }
                    }
                }
                elseif (Get-Command Uninstall-WindowsFeature -ErrorAction SilentlyContinue) {
                    Invoke-Step 'Remove Windows Server IIS role' {
                        $result = Uninstall-WindowsFeature -Name Web-Server -IncludeManagementTools -Restart:$false
                        $script:restartNeeded = $script:restartNeeded -or ([string]$result.RestartNeeded -match '^(Yes|True)$')
                        Write-Output ('Server role success: ' + $result.Success)
                        Write-Output ('Server role restart needed: ' + $result.RestartNeeded)
                        $role = Get-WindowsFeature Web-Server
                        $rolePending = [string]$role.InstallState -match 'Pending|Staged'
                        if ($rolePending) { $script:restartNeeded = $true }
                        if (!$result.Success -and $role.Installed -and !$rolePending) {
                            throw 'Windows Server 无法移除 Web-Server 角色。'
                        }
                    }
                }
                else {
                    $features = @(
            {{featureList}}
                    )
                    Invoke-Step 'Disable Windows IIS features' {
                        foreach ($feature in $features) {
                            $state = Get-WindowsOptionalFeature -Online -FeatureName $feature -ErrorAction SilentlyContinue
                            if ($state -and $state.State -eq 'Enabled') {
                                Write-Output ('Disabling ' + $feature)
                                $result = Disable-WindowsOptionalFeature -Online -FeatureName $feature -NoRestart
                                if ([string]$result.RestartNeeded -match '^(Yes|True)$') { $script:restartNeeded = $true }
                            }
                        }
                    }
                }

                Invoke-Step 'Verify IIS removal' {
                    if ($legacyIis) {
                        if (Get-Service W3SVC -ErrorAction SilentlyContinue) {
                            if ($continuingUninstall -and !$script:restartNeeded) {
                                throw '重启后 IIS 服务仍存在；请检查 Windows 组件安装日志，避免重复提示卸载完成。'
                            }
                            $script:restartNeeded = $true
                        }
                    }
                    elseif (Get-Command Get-WindowsFeature -ErrorAction SilentlyContinue) {
                        if ((Get-WindowsFeature Web-Server).Installed) {
                            if (!$script:restartNeeded -or $continuingUninstall) {
                                throw 'IIS 角色仍处于安装状态。'
                            }
                        }
                    }
                    else {
                        $remaining = Get-WindowsOptionalFeature -Online -FeatureName IIS-WebServerRole -ErrorAction SilentlyContinue
                        $remainingState = if ($remaining) { [string]$remaining.State } else { '' }
                        Write-Output ('IIS-WebServerRole state: ' + $remainingState)
                        if ($remainingState -match 'Pending|Staged') {
                            $script:restartNeeded = $true
                        }
                        if ($remainingState -eq 'Enabled') {
                            throw 'IIS-WebServerRole 仍处于启用状态。'
                        }
                    }
                    Write-Output ('Restart needed: ' + $script:restartNeeded)
                }
            }
            catch {
                Write-Output ('FATAL: ' + $_.Exception.Message)
                Write-Output $_.ScriptStackTrace
                Stop-Transcript | Out-Null
                exit 1
            }

            if ($script:restartNeeded) {
                Stop-Transcript | Out-Null
                exit 3010
            }

            Stop-Transcript | Out-Null
            exit 0
            """;
    }

    internal static string BuildSqlServerUninstallScript(string logFileName = "uninstall-sqlserver.log")
    {
        var storeDataRoot = EscapePowerShellPath(StoreDataRoot);
        var componentRoot = EscapePowerShellPath(ComponentPaths.SqlServerRoot);
        var migratedDataRoot = EscapePowerShellPath(Path.Combine(ComponentPaths.RuntimeRoot, "MSSQL"));
        var legacyDataRoot = EscapePowerShellPath(Path.Combine(ComponentPaths.LegacyRuntimeRoot, "MSSQL"));
        var escapedLogFileName = EscapePowerShellPath(logFileName);
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $storeDataRoot = '{{storeDataRoot}}'
            $componentRoot = '{{componentRoot}}'
            $migratedDataRoot = '{{migratedDataRoot}}'
            $legacyDataRoot = '{{legacyDataRoot}}'
            $workRoot = Join-Path $storeDataRoot 'Work'
            $log = Join-Path $workRoot '{{escapedLogFileName}}'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            Start-Transcript -Path $log -Append | Out-Null

            function Invoke-Step($name, [scriptblock]$action) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
                try { & $action } catch { Write-Output ('WARN: ' + $_.Exception.Message) }
            }

            function Stop-Sql-Service($name) {
                $service = Get-Service -Name $name -ErrorAction SilentlyContinue
                if ($service) {
                    if ($service.Status -ne 'Stopped') {
                        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 2
                    }
                }
            }

            function Delete-Sql-Service($name) {
                $service = Get-Service -Name $name -ErrorAction SilentlyContinue
                if ($service) {
                    if ($service.Status -ne 'Stopped') {
                        Stop-Service -Name $name -Force -ErrorAction SilentlyContinue
                        Start-Sleep -Seconds 1
                    }
                    sc.exe delete $name | Out-String | Write-Output
                }
            }

            function Remove-Tree($path) {
                if ([string]::IsNullOrWhiteSpace($path) -or !(Test-Path $path)) { return }
                Write-Output ('Removing ' + $path)
                takeown.exe /F $path /R /D Y | Out-Null
                icacls.exe $path /grant '*S-1-5-32-544:F' /T /C | Out-Null
                Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
            }

            Invoke-Step '停止 MCPanel SQL Server 默认实例服务' {
                Get-Service -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
                } | ForEach-Object { Stop-Sql-Service $_.Name }
            }

            Invoke-Step '调用官方安装器卸载 SQL Server 默认实例' {
                $instanceNames = @('MSSQLSERVER')
                $setupCandidates = @()
                foreach ($root in @(
                    (Join-Path $workRoot 'SqlServer2012ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025EnterpriseDeveloperMedia'),
                    (Join-Path $workRoot 'SqlServer2022ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2017ExpressMedia'),
                    $componentRoot,
                    $migratedDataRoot,
                    $legacyDataRoot
                )) {
                    if (Test-Path $root) {
                        $setupCandidates += Get-ChildItem $root -Filter setup.exe -Recurse -ErrorAction SilentlyContinue
                    }
                }

                $setupCandidates |
                    Sort-Object FullName -Unique |
                    ForEach-Object {
                        $setupPath = $_.FullName
                        foreach ($instance in $instanceNames) {
                            Write-Output ('Running setup uninstall: ' + $setupPath + ' instance=' + $instance)
                            $args = '/ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=' + $instance + ' /Q'
                            $process = Start-Process -FilePath $setupPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                            if ($process) { Write-Output ('setup exit code: ' + $process.ExitCode) }
                        }
                    }
            }

            Invoke-Step '保留共享 SQL Server 客户端组件' {
                Write-Output '保留 SQL Native Client、ODBC/OLE DB Driver、SQL Browser、VSS Writer 等共享组件，避免影响其他软件或 SQL Server 实例。'
            }

            Invoke-Step '删除 MCPanel SQL Server 默认实例残留服务项' {
                Get-Service -ErrorAction SilentlyContinue | Where-Object {
                    $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
                } | ForEach-Object { Delete-Sql-Service $_.Name }
            }

            Invoke-Step '删除 MCPanel 管理目录和安装缓存' {
                $paths = @(
                    $componentRoot,
                    $migratedDataRoot,
                    $legacyDataRoot,
                    (Join-Path $workRoot 'SqlServer2012ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2022ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025ExpressMedia'),
                    (Join-Path $workRoot 'SqlServer2025EnterpriseDeveloperMedia'),
                    (Join-Path $workRoot 'SqlServer2017ExpressMedia')
                )
                foreach ($path in $paths) { Remove-Tree $path }
            }

            Invoke-Step '保留共享 SQL Server 注册表与程序目录' {
                Write-Output '全局 Microsoft SQL Server 注册表树、Program Files 和 ProgramData 由官方卸载器管理；MCPanel 不再强制删除，以保护其他实例。'
                Write-Output '保留 Windows NVMe 4KB 扇区兼容项，避免 SQL Server 卸载后立即重装需要重启系统。'
            }

            Invoke-Step '清理 MCPanel SQL Server 防火墙规则' {
                Get-NetFirewallRule -ErrorAction SilentlyContinue |
                    Where-Object { $_.DisplayName -like 'MCPanel SQL Server *' } |
                    Remove-NetFirewallRule -ErrorAction SilentlyContinue
            }

            $remainingServices = @(Get-Service -ErrorAction SilentlyContinue | Where-Object {
                $_.Name -in @('MSSQLSERVER','SQLSERVERAGENT')
            })
            $remainingManagedPaths = @($componentRoot, $migratedDataRoot, $legacyDataRoot) |
                Where-Object { -not [string]::IsNullOrWhiteSpace($_) -and (Test-Path $_) }
            if ($remainingServices.Count -gt 0 -or $remainingManagedPaths.Count -gt 0) {
                if ($remainingServices.Count -gt 0) {
                    Write-Output ('FATAL: SQL Server 服务仍然存在：' + (($remainingServices | ForEach-Object { $_.Name }) -join ', '))
                }
                if ($remainingManagedPaths.Count -gt 0) {
                    Write-Output ('FATAL: MCPanel SQL Server 目录仍然存在：' + ($remainingManagedPaths -join ', '))
                }
                Write-Output '请重启 Windows 后再次点击卸载；若仍失败，请查看本日志中的 WARN 和退出码。'
                Stop-Transcript | Out-Null
                exit 1
            }

            Stop-Transcript | Out-Null
            exit 0
            """;
    }

    private static EnvironmentRuntimeState Installed(bool isRunning, string text) => new(true, isRunning, text, isRunning ? RuntimeStatusKind.Running : RuntimeStatusKind.Stopped);
    private static EnvironmentRuntimeState Installed(
        RuntimeStatusKind status,
        string text,
        string? detectedMySqlReleaseId = null,
        string? detectedMySqlVersion = null,
        string? detectedSqlServerReleaseId = null,
        string? detectedSqlServerDisplayName = null) =>
        new(
            true,
            status == RuntimeStatusKind.Running,
            text,
            status,
            detectedMySqlReleaseId,
            detectedMySqlVersion,
            detectedSqlServerReleaseId,
            detectedSqlServerDisplayName);
    private static EnvironmentRuntimeState NotInstalled() => new(false, false, "等待安装", RuntimeStatusKind.NotInstalled);

    private static EnvironmentRuntimeState GetNginxState(ComponentLocator locator)
    {
        var nginxExe = locator.FindNginxExecutable();
        if (nginxExe is null)
        {
            return NotInstalled();
        }

        var nginxRoot = Path.GetDirectoryName(nginxExe)!;
        var runtimePresent = IsNginxProcessOrServiceRunning(nginxRoot);
        var portsHealthy = NginxRuntimeManager.AreConfiguredPortsListening(
            nginxRoot,
            out var configuredPorts,
            out var missingPorts);
        var isRunning = runtimePresent && portsHealthy;
        var options = NginxRuntimeManager.LoadOptions();
        var optionText = options is null ? string.Empty : options.Summary;
        var healthText = !runtimePresent
            ? "当前未检测到 Nginx 进程。"
            : configuredPorts.Count == 0
                ? "未从实际 nginx.conf 读取到有效监听端口，已判定为启动异常。"
                : missingPorts.Count > 0
                    ? $"检测到 Nginx 进程，但实际配置端口 {string.Join("、", missingPorts)} 未监听，已判定为启动异常。"
                    : "实际 nginx.conf 配置端口均已监听。";
        var actualPortText = configuredPorts.Count == 0
            ? string.Empty
            : $"实际监听端口：{string.Join("、", configuredPorts)}。";
        return Installed(isRunning, $"Nginx 已安装到 {nginxRoot}。{healthText}{actualPortText}{optionText}");
    }

    private static EnvironmentRuntimeState GetTomcatState(ComponentLocator locator)
    {
        var tomcatRoot = locator.FindTomcatRoot();
        if (tomcatRoot is null)
        {
            return NotInstalled();
        }

        var sharedProcessRunning = TomcatProductInstanceManager.IsSharedTomcatRunning();
        var sharedRunning = sharedProcessRunning &&
            TomcatRuntimeProbe.ArePortsListening(TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot));
        var managedRunning = TomcatProductInstanceManager.IsAnyManagedTomcatHealthy();
        // The Environment card controls the shared server only. Independent/Catalina
        // product instances are managed from the Websites page and must not make the
        // shared Tomcat Server badge/button look running when no shared JVM exists.
        var anyRunning = sharedProcessRunning;
        var detail = sharedRunning
            ? "全部应用模式正在运行。"
            : sharedProcessRunning
                ? "检测到共享 Tomcat Java 进程，但配置端口未全部监听。"
                : managedRunning
                    ? "总 Tomcat Server 未运行；一个或多个应用正在单独运行，请在“网站”页面管理。"
                    : "当前没有运行中的总 Tomcat Server。";
        return Installed(anyRunning, $"Tomcat 已安装到 {tomcatRoot}。{detail}");
    }

    private static EnvironmentRuntimeState GetSqlServerState(ComponentLocator locator)
    {
        if (ServiceExists("MSSQLSERVER"))
        {
            var installation = SqlServerInstallationDetector.FindPreferred();
            var release = installation is null
                ? null
                : SqlServerReleaseCatalog.FindByInstallation(installation.MajorVersion, installation.Edition);
            var displayName = release?.DisplayName ?? installation?.DisplayName;
            var versionText = string.IsNullOrWhiteSpace(displayName) ? string.Empty : $"实际版本：{displayName}。";
            var serviceStatus = ServiceStatus("MSSQLSERVER");
            var configuredPort = SqlServerCredentialStore.TryReadInstalledConnection()?.Port;
            var portText = configuredPort is > 0 and <= 65535
                ? $"实际 TCP 端口：{configuredPort.Value}。"
                : string.Empty;
            if (serviceStatus == RuntimeStatusKind.Running &&
                configuredPort is > 0 and <= 65535 &&
                !GetActiveTcpPorts().Contains(configuredPort.Value))
            {
                return Installed(
                    RuntimeStatusKind.Stopped,
                    $"SQL Server 服务报告正在运行，但配置端口 {configuredPort.Value} 未监听，已判定为启动异常。{versionText}{FormatSqlServerConnectionText()}",
                    detectedSqlServerReleaseId: release?.Id,
                    detectedSqlServerDisplayName: displayName);
            }

            return Installed(
                serviceStatus,
                $"SQL Server 已安装为 MSSQLSERVER 服务。{versionText}{portText}{FormatSqlServerConnectionText()}",
                detectedSqlServerReleaseId: release?.Id,
                detectedSqlServerDisplayName: displayName);
        }

        return EnvironmentInstaller.HasSqlServerInstallContinuation
            ? new EnvironmentRuntimeState(false, false, EnvironmentInstaller.SqlServerInstallContinuationMessage, RuntimeStatusKind.NotInstalled)
            : NotInstalled();
    }

    private static EnvironmentRuntimeState GetIisState(ComponentLocator locator)
    {
        if (File.Exists(IisPendingUninstallMarker))
        {
            return new EnvironmentRuntimeState(false, false, IisUninstallContinuationMessage, RuntimeStatusKind.Unknown);
        }

        if (EnvironmentInstaller.HasIisInstallContinuation)
        {
            return new EnvironmentRuntimeState(false, false, EnvironmentInstaller.IisInstallContinuationMessage, RuntimeStatusKind.Unknown);
        }

        var serviceExists = ServiceExists("W3SVC");
        if (File.Exists(IisUninstalledMarker) && !serviceExists)
        {
            return new EnvironmentRuntimeState(false, false, "IIS 已卸载。", RuntimeStatusKind.NotInstalled);
        }

        return serviceExists ? Installed(ServiceStatus("W3SVC"), "IIS 已安装，可管理 Default Web Site。") : NotInstalled();
    }

    private static EnvironmentRuntimeState GetMySqlState(ComponentLocator locator)
    {
        var serviceExists = ServiceExists("MySQL80");
        var serviceExe = serviceExists ? GetServiceExecutablePath("MySQL80") : null;
        var mysqlRoot = locator.FindMySqlRoot(serviceExe);
        var credentials = LoadEffectiveMySqlCredentials(locator, mysqlRoot);
        var mysqldExe = FindMySqlServerExecutable(mysqlRoot, serviceExe);
        var detectedVersion = ReadMySqlServerVersion(mysqldExe);
        var detectedReleaseId = MySqlReleaseCatalog.FindByServerVersion(detectedVersion)?.Id;
        if (mysqlRoot is not null)
        {
            var versionText = string.IsNullOrWhiteSpace(detectedVersion) ? string.Empty : $"版本 {detectedVersion}。";
            var serviceStatus = ServiceStatus("MySQL80");
            var configuredPort = TryReadMySqlConfiguredPort(mysqlRoot);
            var portText = configuredPort is > 0 and <= 65535
                ? $"实际配置端口：{configuredPort.Value}。"
                : string.Empty;
            if (serviceStatus == RuntimeStatusKind.Running &&
                configuredPort is > 0 and <= 65535 &&
                !GetActiveTcpPorts().Contains(configuredPort.Value))
            {
                return new EnvironmentRuntimeState(
                    true,
                    false,
                    $"MySQL80 服务报告正在运行，但 my.ini 配置端口 {configuredPort.Value} 未监听，已判定为启动异常。{versionText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                    RuntimeStatusKind.Stopped,
                    detectedReleaseId,
                    detectedVersion);
            }

            return Installed(
                serviceStatus,
                $"MySQL 已安装到 {mysqlRoot}。{versionText}{portText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                detectedReleaseId,
                detectedVersion);
        }

        if (serviceExists)
        {
            if (!string.IsNullOrWhiteSpace(serviceExe) && !File.Exists(serviceExe))
            {
                return new EnvironmentRuntimeState(true, false, $"MySQL80 服务已注册，但程序文件缺失：{serviceExe}。请点击卸载清理残留后重新安装。", RuntimeStatusKind.Unknown);
            }

            var versionText = string.IsNullOrWhiteSpace(detectedVersion) ? string.Empty : $"版本 {detectedVersion}。";
            return Installed(
                ServiceStatus("MySQL80"),
                $"MySQL80 服务已注册。{versionText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                detectedReleaseId,
                detectedVersion);
        }

        return NotInstalled();
    }

    private static string FormatSqlServerConnectionText()
    {
        var credentials = SqlServerCredentialStore.Load();
        return $"连接信息：{credentials.Host},{credentials.Port}，账号 {credentials.UserName}。";
    }

    private static async Task RunSqlServerServiceActionAsync(string action, CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"sqlserver-service-action-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"sqlserver-service-action-{operationId}.log");
        var result = Path.Combine(workDirectory, $"sqlserver-service-action-{operationId}.result");
        Directory.CreateDirectory(workDirectory);
        await FileCompat.WriteAllTextAsync(
            script,
            BuildSqlServerServiceActionScript(action, Path.GetFileName(log), Path.GetFileName(result)),
            new UTF8Encoding(true),
            cancellationToken);

        try
        {
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        catch (Exception ex)
        {
            var detail = ReadTextFileBestEffort(result);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                throw new EnvironmentOperationException(detail.Trim(), log, ex);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(script);
            TryDeleteFile(result);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "sqlserver-service-action-*.log*", log);
        }
    }

    internal static string BuildSqlServerServiceActionScript(
        string action,
        string logFileName,
        string resultFileName)
    {
        var storeDataRoot = EscapePowerShellPath(StoreDataRoot);
        var credentials = SqlServerCredentialStore.Load();
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $action = '{{EscapePowerShellPath(action)}}'
            $workRoot = Join-Path '{{storeDataRoot}}' 'Work'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot '{{EscapePowerShellPath(logFileName)}}'
            $resultFile = Join-Path $workRoot '{{EscapePowerShellPath(resultFileName)}}'
            Remove-Item -LiteralPath $resultFile -Force -ErrorAction SilentlyContinue
            Start-Transcript -Path $log -Append | Out-Null

            function Set-Result($message) {
                $message | Set-Content -Path $resultFile -Encoding UTF8
            }

            function Fail($message) {
                Write-Output $message
                Set-Result $message
                Stop-Transcript | Out-Null
                exit 1
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

            function Get-SqlInstanceId {
                $instanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
                if (Test-Path $instanceKey) {
                    $id = (Get-ItemProperty $instanceKey -ErrorAction SilentlyContinue).MSSQLSERVER
                    if (![string]::IsNullOrWhiteSpace($id)) { return $id }
                }
                return $null
            }

            function Configure-SqlRuntime {
                $instanceId = Get-SqlInstanceId
                if ([string]::IsNullOrWhiteSpace($instanceId)) { return }
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
                $tcpPort = 1433
                if (Test-Path $tcpKey) {
                    $tcpValues = Get-ItemProperty -Path $tcpKey -ErrorAction SilentlyContinue
                    $fixedPort = 0
                    $dynamicPort = 0
                    [int]::TryParse([string]$tcpValues.TcpPort, [ref]$fixedPort) | Out-Null
                    [int]::TryParse([string]$tcpValues.TcpDynamicPorts, [ref]$dynamicPort) | Out-Null
                    if ($fixedPort -gt 0 -and $fixedPort -le 65535) {
                        $tcpPort = $fixedPort
                    } elseif ($dynamicPort -gt 0 -and $dynamicPort -le 65535) {
                        $tcpPort = $dynamicPort
                    } else {
                        Set-ItemProperty -Path $tcpKey -Name TcpDynamicPorts -Value '' -ErrorAction SilentlyContinue
                        Set-ItemProperty -Path $tcpKey -Name TcpPort -Value '1433' -ErrorAction SilentlyContinue
                    }
                }
                New-NetFirewallRule -DisplayName ('MCPanel SQL Server ' + $tcpPort) -Direction Inbound -Action Allow -Protocol TCP -LocalPort $tcpPort -ErrorAction SilentlyContinue | Out-Null
            }

            function Get-RecentSqlErrors {
                $messages = @()
                try {
                    $messages += Get-EventLog -LogName Application -Source MSSQLSERVER -Newest 8 -ErrorAction SilentlyContinue |
                        ForEach-Object { $_.TimeGenerated.ToString('yyyy-MM-dd HH:mm:ss') + ' ' + $_.Message }
                } catch { }
                if ($messages.Count -eq 0) { return '' }
                return ($messages -join [Environment]::NewLine)
            }

            function Start-SqlServer {
                $svc = Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
                if (-not $svc) { Fail 'MSSQLSERVER 服务未注册，请先安装 SQL Server。' }
                Configure-SqlRuntime
                & sc.exe config MSSQLSERVER start= auto | Out-Null
                if ($svc.Status -ne 'Running') {
                    Start-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
                }
                if (!(Wait-ServiceState MSSQLSERVER Running 90)) {
                    $recent = Get-RecentSqlErrors
                    if (![string]::IsNullOrWhiteSpace($recent)) {
                        Fail ("MSSQLSERVER 启动超时。最近事件日志：" + [Environment]::NewLine + $recent)
                    }
                    Fail ('MSSQLSERVER 启动超时。请查看日志：' + $log + ' 或 SQL Server 安装日志。')
                }
            }

            function Stop-SqlServer {
                $svc = Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
                if (-not $svc) { return }
                if ($svc.Status -ne 'Stopped') {
                    Stop-Service -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue
                }
                if (!(Wait-ServiceState MSSQLSERVER Stopped 90)) {
                    Fail 'MSSQLSERVER 停止超时。请确认没有安装器或数据库工具正在占用服务。'
                }
            }

            try {
                switch ($action) {
                    'stop' {
                        Stop-SqlServer
                        Set-Result 'SQL Server 服务已停止。'
                    }
                    'start' {
                        Start-SqlServer
                        Set-Result 'SQL Server 服务已启动。连接信息：{{credentials.Host}},{{credentials.Port}}，账号 {{credentials.UserName}}。'
                    }
                    'restart' {
                        Stop-SqlServer
                        Start-Sleep -Seconds 2
                        Start-SqlServer
                        Set-Result 'SQL Server 服务已重启。连接信息：{{credentials.Host}},{{credentials.Port}}，账号 {{credentials.UserName}}。'
                    }
                    default {
                        Fail ('未知 SQL Server 操作：' + $action)
                    }
                }
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Fail ('SQL Server 操作失败：' + $_.Exception.Message)
            }
            """;
    }

    private static async Task<string> RunMySqlServiceActionAsync(string action, CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"mysql-service-action-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"mysql-service-action-{operationId}.log");
        var result = Path.Combine(workDirectory, $"mysql-service-action-{operationId}.result");
        Directory.CreateDirectory(workDirectory);
        await FileCompat.WriteAllTextAsync(
            script,
            BuildMySqlServiceActionScript(action, Path.GetFileName(log), Path.GetFileName(result)),
            new UTF8Encoding(true),
            cancellationToken);

        try
        {
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
            var detail = ReadTextFileBestEffort(result);
            return string.IsNullOrWhiteSpace(detail)
                ? $"MySQL {action} 操作已完成。"
                : detail.Trim();
        }
        catch (Exception ex)
        {
            var detail = ReadTextFileBestEffort(result);
            if (!string.IsNullOrWhiteSpace(detail))
            {
                throw new EnvironmentOperationException(detail.Trim(), log, ex);
            }

            throw;
        }
        finally
        {
            TryDeleteFile(script);
            TryDeleteFile(result);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "mysql-service-action-*.log*", log);
        }
    }

    internal static string BuildMySqlServiceActionScript(
        string action,
        string logFileName,
        string resultFileName)
    {
        var storeDataRoot = EscapePowerShellPath(StoreDataRoot);
        var mysqlRootsLiteral = string.Join(", ", ComponentPaths.MySqlSearchRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => $"'{EscapePowerShellPath(root)}'"));
        var credentials = LoadEffectiveMySqlCredentials();
        var rootPassword = EscapePowerShellPath(credentials.Password);
        var port = credentials.Port;
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $action = '{{EscapePowerShellPath(action)}}'
            $storeDataRoot = '{{storeDataRoot}}'
            $rootPassword = '{{rootPassword}}'
            $port = {{port}}
            $mysqlRoots = @({{mysqlRootsLiteral}})
            $workRoot = Join-Path $storeDataRoot 'Work'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot '{{EscapePowerShellPath(logFileName)}}'
            $resultFile = Join-Path $workRoot '{{EscapePowerShellPath(resultFileName)}}'
            Remove-Item -LiteralPath $resultFile -Force -ErrorAction SilentlyContinue
            Start-Transcript -Path $log -Append | Out-Null

            function Set-Result($message) {
                $message | Set-Content -Path $resultFile -Encoding UTF8
            }

            function Fail($message) {
                Write-Output $message
                Set-Result $message
                exit 1
            }

            function Invoke-NativeQuiet($file, [string[]]$arguments) {
                & $file @arguments 1>$null 2>$null
                return $LASTEXITCODE
            }

            function Get-ServiceExePath($name) {
                $key = Get-ItemProperty ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $name) -ErrorAction SilentlyContinue
                if (-not $key -or -not $key.ImagePath) { return $null }
                $path = [Environment]::ExpandEnvironmentVariables($key.ImagePath)
                if ($path.StartsWith('"')) {
                    $end = $path.IndexOf('"', 1)
                    if ($end -gt 1) { return $path.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($path, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            function Get-MySqlPaths {
                $serviceExe = Get-ServiceExePath 'MySQL80'
                if ([string]::IsNullOrWhiteSpace($serviceExe) -or !(Test-Path $serviceExe)) {
                    foreach ($mysqlRoot in $mysqlRoots) {
                        if (!(Test-Path $mysqlRoot)) { continue }
                        $candidate = Get-ChildItem $mysqlRoot -Filter 'mysqld*.exe' -Recurse -ErrorAction SilentlyContinue |
                            Where-Object {
                                $_.Name -in @('mysqld.exe', 'mysqld-itmc.exe') -and
                                $_.FullName -like '*\bin\*'
                            } |
                            Select-Object -First 1
                        if ($candidate) {
                            $serviceExe = $candidate.FullName
                            break
                        }
                    }
                }
                if ([string]::IsNullOrWhiteSpace($serviceExe) -or !(Test-Path $serviceExe)) {
                    Fail 'MySQL80 服务文件不存在，请先卸载残留后重新安装。'
                }

                $binRoot = Split-Path $serviceExe -Parent
                $root = Split-Path $binRoot -Parent
                $mysql = Join-Path $binRoot 'mysql.exe'
                $myIni = Join-Path $root 'my.ini'
                $data = Join-Path $root 'data'
                if (!(Test-Path $mysql)) { Fail ('找不到 mysql.exe：' + $mysql) }
                if (!(Test-Path $myIni)) { Fail ('找不到 my.ini：' + $myIni) }
                return @{
                    Mysqld = $serviceExe
                    Mysql = $mysql
                    Root = $root
                    MyIni = $myIni
                    Data = $data
                }
            }

            function Stop-StoreDataMySqlClients {
                foreach ($name in @('mysql','mysqladmin','mysqldump')) {
                    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
                        $path = $null
                        try { $path = $_.Path } catch { }
                        if ($path -and ($mysqlRoots | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                }
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

            function Stop-MySqlService {
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if (-not $svc) { return }
                if ($svc.Status -eq 'Stopped') { return }
                Write-Output 'Stopping MySQL80...'
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState 'Stopped' 45)) {
                    Write-Output 'Stop-Service timeout, fallback to sc stop.'
                    & sc.exe stop MySQL80 | Out-String | Write-Output
                    if (!(Wait-ServiceState 'Stopped' 30)) {
                        Fail 'MySQL80 停止超时，请确认没有外部程序占用服务。'
                    }
                }
                Start-Sleep -Milliseconds 800
            }

            function Read-MySqlErrorTail($paths) {
                $err = Get-ChildItem $paths.Data -Filter '*.err' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($err) {
                    return ((Get-Content $err.FullName -Tail 40 -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
                }
                return ''
            }

            function Start-MySqlService {
                $paths = Get-MySqlPaths
                Stop-StoreDataMySqlClients
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if (-not $svc) { Fail 'MySQL80 服务未注册，请先安装 MySQL。' }
                if ($svc.Status -eq 'Running') { return $paths }

                & sc.exe config MySQL80 start= auto | Out-Null
                $lastError = ''
                for ($i = 1; $i -le 6; $i++) {
                    try {
                        Write-Output ('Starting MySQL80, attempt ' + $i)
                        Start-Service -Name 'MySQL80' -ErrorAction Stop
                    }
                    catch {
                        $lastError = $_.Exception.Message
                        Write-Output ('Start-Service failed: ' + $lastError)
                        & sc.exe start MySQL80 | Out-String | Write-Output
                    }

                    if (Wait-ServiceState 'Running' 15) { return $paths }
                    Start-Sleep -Seconds 1
                }

                $tail = Read-MySqlErrorTail $paths
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Fail ("MySQL80 启动失败。最近错误日志：" + [Environment]::NewLine + $tail)
                }
                Fail ('MySQL80 启动失败：' + $lastError)
            }

            function Invoke-RootQuery($paths, $sql) {
                $mysqlArgs = @('--protocol=TCP', '--connect-timeout=3', '--get-server-public-key', '-h127.0.0.1', "-P$port", '-uroot', "-p$rootPassword", '-e', $sql)
                $code = Invoke-NativeQuiet $paths.Mysql $mysqlArgs
                if ($code -eq 0) { return 0 }

                # Older MySQL clients may not understand --get-server-public-key.
                # Retry without it for backward compatibility.
                $legacyArgs = @('--protocol=TCP', '--connect-timeout=3', '-h127.0.0.1', "-P$port", '-uroot', "-p$rootPassword", '-e', $sql)
                return Invoke-NativeQuiet $paths.Mysql $legacyArgs
            }

            function Test-RootPassword($paths) {
                return (Invoke-RootQuery $paths 'SELECT 1;') -eq 0
            }

            function Wait-RootPasswordReady($paths, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    if (Test-RootPassword $paths) { return $true }
                    Start-Sleep -Milliseconds 750
                }
                return $false
            }

            function Stop-TemporaryMySqlProcess($process, $paths) {
                if (!$process -or $process.HasExited) { return }
                Invoke-RootQuery $paths 'SHUTDOWN;' | Out-Null
                for ($i = 0; $i -lt 30; $i++) {
                    if ($process.HasExited) { return }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                }
            }

            function Reset-RootPassword($paths) {
                Write-Output 'Resetting MySQL root password...'
                Stop-MySqlService
                $versionText = (& $paths.Mysqld --version) -join ' '
                $isModern = $versionText -match 'Ver\s+(8|9)\.|mysqld\s+(8|9)\.'
                $initFile = Join-Path $paths.Data 'mysql-itmc-init.sql'
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
                    $proc = Start-Process -FilePath $paths.Mysqld -ArgumentList @("--defaults-file=$($paths.MyIni)", "--init-file=$initFile", "--bind-address=127.0.0.1") -WindowStyle Hidden -PassThru
                    for ($i = 0; $i -lt 30; $i++) {
                        Start-Sleep -Seconds 1
                        if ($proc.HasExited) { break }
                        $code = Invoke-RootQuery $paths 'SELECT 1;'
                        if ($code -eq 0) {
                            $updated = $true
                            break
                        }
                    }
                    if (!$updated) { $failure = 'MySQL root 密码修复失败，临时维护进程未能接受连接。' }
                }
                catch { $failure = $_.Exception.Message }
                finally {
                    Stop-TemporaryMySqlProcess $proc $paths
                    Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue
                }

                if (!$updated) {
                    $tail = Read-MySqlErrorTail $paths
                    Fail ($failure + [Environment]::NewLine + $tail)
                }
                $paths = Start-MySqlService
                if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但默认账号密码仍无法连接。' }
            }

            try {
                switch ($action) {
                    'stop' {
                        Stop-MySqlService
                        Set-Result 'MySQL80 服务已停止。'
                    }
                    'start' {
                        $paths = Start-MySqlService
                        $message = 'MySQL80 服务已启动。连接信息：127.0.0.1:' + $port + '，账号 root。'
                        if (!(Wait-RootPasswordReady $paths 20)) {
                            $message += ' 注意：服务运行正常，但保存的 root 凭据验证未通过。为避免意外修改数据库密码，MCPanel 已停止自动重置；不会修改现有数据库密码。'
                            Write-Output $message
                        }
                        Set-Result $message
                    }
                    'restart' {
                        Stop-MySqlService
                        Start-Sleep -Seconds 2
                        $paths = Start-MySqlService
                        $message = 'MySQL80 服务已重启。连接信息：127.0.0.1:' + $port + '，账号 root。'
                        if (!(Wait-RootPasswordReady $paths 20)) {
                            $message += ' 注意：服务运行正常，但保存的 root 凭据验证未通过。为避免意外修改数据库密码，MCPanel 已停止自动重置；不会修改现有数据库密码。'
                            Write-Output $message
                        }
                        Set-Result $message
                    }
                    default {
                        Fail ('未知 MySQL 操作：' + $action)
                    }
                }
                exit 0
            }
            catch {
                $message = 'MySQL 操作失败：' + $_.Exception.Message
                Set-Result $message
                Write-Output $message
                exit 1
            }
            finally {
                Stop-Transcript | Out-Null
            }
            """;
    }

    internal static string BuildMySqlUninstallScript(
        string? mysqlRoot,
        string logFileName = "uninstall-mysql.log")
    {
        var mysqlSearchRootsLiteral = string.Join(", ", ComponentPaths.MySqlSearchRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => $"'{EscapePowerShellPath(root)}'"));
        var mysqlInstallationRootsLiteral = string.Join(", ", ComponentPaths.MySqlInstallationRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(root => $"'{EscapePowerShellPath(root)}'"));
        var sharedRuntimeRootsLiteral = string.Join(", ", new[]
        {
            ComponentPaths.RuntimeRoot,
            ComponentPaths.LegacyRuntimeRoot
        }.Distinct(StringComparer.OrdinalIgnoreCase).Select(root => $"'{EscapePowerShellPath(root)}'"));
        var targetRoot = EscapePowerShellPath(mysqlRoot ?? string.Empty);
        var escapedLogFileName = EscapePowerShellPath(logFileName);
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $mysqlSearchRoots = @({{mysqlSearchRootsLiteral}})
            $mysqlInstallationRoots = @({{mysqlInstallationRootsLiteral}})
            $sharedRuntimeRoots = @({{sharedRuntimeRootsLiteral}})
            $targetRoot = '{{targetRoot}}'
            $workRoot = Join-Path '{{EscapePowerShellPath(StoreDataRoot)}}' 'Work'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot '{{escapedLogFileName}}'
            Start-Transcript -Path $log -Append | Out-Null

            function Stop-StoreDataMySqlProcesses {
                $names = @('mysqld','mysqld-itmc','mysql','mysqladmin','mysqldump')
                foreach ($name in $names) {
                    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
                        $path = $null
                        try { $path = $_.Path } catch { }
                        if ($path -and ($mysqlSearchRoots | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
                            Write-Output ('Stopping process ' + $_.ProcessName + ' #' + $_.Id)
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                }

                Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
                    Where-Object {
                        $process = $_
                        $process.ExecutablePath -and
                        @($mysqlSearchRoots | Where-Object { $process.ExecutablePath.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0 -and
                        @('mysqld.exe','mysqld-itmc.exe','mysql.exe','mysqladmin.exe','mysqldump.exe') -contains ([IO.Path]::GetFileName($process.ExecutablePath).ToLowerInvariant())
                    } |
                    ForEach-Object {
                        Write-Output ('Terminating process ' + $_.Name + ' #' + $_.ProcessId)
                        Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
                    }
            }

            function Get-ServiceExePath($name) {
                $key = Get-ItemProperty ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $name) -ErrorAction SilentlyContinue
                if (-not $key -or -not $key.ImagePath) { return $null }
                $path = [Environment]::ExpandEnvironmentVariables($key.ImagePath)
                if ($path.StartsWith('"')) {
                    $end = $path.IndexOf('"', 1)
                    if ($end -gt 1) { return $path.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($path, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            function Is-ManagedMySqlPath($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $false }
                try {
                    $full = [IO.Path]::GetFullPath($path)
                    foreach ($root in $mysqlSearchRoots) {
                        $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
                        if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
                    }
                } catch { }
                return $false
            }

            $serviceExe = Get-ServiceExePath 'MySQL80'
            $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
            $managedService = $svc -and (Is-ManagedMySqlPath $serviceExe)
            if ($svc -and -not $managedService) {
                throw ('检测到非 MCPanel 管理的 MySQL80 服务，已停止卸载以保护现有实例：' + $serviceExe)
            }
            if ($svc -and $svc.Status -ne 'Stopped') {
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { Start-Sleep -Seconds 2 }
            }
            Stop-StoreDataMySqlProcesses

            $mysqld = $null
            if ($serviceExe -and (Test-Path $serviceExe)) {
                $mysqld = Get-Item $serviceExe -ErrorAction SilentlyContinue
            }
            if (-not $mysqld) {
                foreach ($mysqlRoot in $mysqlSearchRoots) {
                    if (!(Test-Path $mysqlRoot)) { continue }
                    $mysqld = Get-ChildItem $mysqlRoot -Filter mysqld*.exe -Recurse -ErrorAction SilentlyContinue |
                        Where-Object {
                            $_.Name -in @('mysqld.exe', 'mysqld-itmc.exe') -and
                            $_.FullName -like '*\bin\*'
                        } |
                        Select-Object -First 1
                    if ($mysqld) { break }
                }
            }
            if ($mysqld -and (!$svc -or $managedService)) {
                & $mysqld.FullName --remove MySQL80
                if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {
                    Write-Output ('mysqld --remove returned ' + $LASTEXITCODE + ', fallback to sc delete.')
                }
            }

            $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
            if ($svc) {
                if (-not $managedService) {
                    throw 'MySQL80 服务归属无法确认，已停止卸载以保护现有实例。'
                }
                & sc.exe delete MySQL80 | Out-Null
                for ($i = 0; $i -lt 20; $i++) {
                    if (-not (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { break }
                    Start-Sleep -Milliseconds 500
                }
            }

            Stop-StoreDataMySqlProcesses

            if ([string]::IsNullOrWhiteSpace($targetRoot) -or !(Test-Path $targetRoot)) {
                foreach ($mysqlRoot in $mysqlInstallationRoots) {
                    if (!(Test-Path $mysqlRoot)) { continue }
                    if (Test-Path (Join-Path $mysqlRoot 'bin\mysql.exe')) {
                        $targetRoot = $mysqlRoot
                        break
                    }
                    $candidate = Get-ChildItem $mysqlRoot -Directory -ErrorAction SilentlyContinue |
                        Where-Object { Test-Path (Join-Path $_.FullName 'bin\mysql.exe') } |
                        Select-Object -First 1
                    if ($candidate) {
                        $targetRoot = $candidate.FullName
                        break
                    }
                }
            }

            if ([string]::IsNullOrWhiteSpace($targetRoot)) {
                Write-Output '未找到独立的 MySQL 安装目录，未删除共享 Runtime 文件。'
            }
            else {
                $fullTarget = [IO.Path]::GetFullPath($targetRoot).TrimEnd('\')
                $isSharedRuntime = $false
                foreach ($sharedRoot in $sharedRuntimeRoots) {
                    $fullSharedRoot = [IO.Path]::GetFullPath($sharedRoot).TrimEnd('\')
                    if ($fullTarget.Equals($fullSharedRoot, [StringComparison]::OrdinalIgnoreCase)) {
                        $isSharedRuntime = $true
                        break
                    }
                }

                if ($isSharedRuntime) {
                    Write-Output ('FATAL: MySQL 安装目录与共享 Runtime 根目录重叠，已停止删除：' + $fullTarget)
                    Stop-Transcript | Out-Null
                    exit 1
                }

                if (!(Test-Path (Join-Path $fullTarget 'bin\mysql.exe'))) {
                    Write-Output ('FATAL: 未在待删除目录中找到 bin\mysql.exe，已停止删除：' + $fullTarget)
                    Stop-Transcript | Out-Null
                    exit 1
                }

                if (Test-Path $fullTarget) {
                    Write-Output ('Removing MySQL installation directory: ' + $fullTarget)
                    Get-ChildItem $fullTarget -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                        try { $_.Attributes = 'Normal' } catch { }
                    }

                    $removed = $false
                    for ($i = 0; $i -lt 5; $i++) {
                        try {
                            Remove-Item -LiteralPath $fullTarget -Recurse -Force -ErrorAction Stop
                            $removed = $true
                            break
                        }
                        catch {
                            Write-Output ('Remove attempt ' + ($i + 1) + ' failed: ' + $_.Exception.Message)
                            Stop-StoreDataMySqlProcesses
                            Start-Sleep -Seconds 1
                        }
                    }

                    if (!$removed -and (Test-Path $fullTarget)) {
                        throw ('MySQL 目录删除失败，请关闭占用进程后重试：' + $fullTarget)
                    }
                }
            }

            $remainingService = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
            $remainingDirectories = @()
            if (-not [string]::IsNullOrWhiteSpace($targetRoot) -and (Test-Path $targetRoot)) {
                $remainingDirectories += [IO.Path]::GetFullPath($targetRoot)
            }
            $unsafeSharedDirectories = @()
            foreach ($sharedRoot in $sharedRuntimeRoots) {
                if (Test-Path (Join-Path $sharedRoot 'bin\mysql.exe')) {
                    $unsafeSharedDirectories += $sharedRoot
                }
            }
            if ($remainingService -or $remainingDirectories.Count -gt 0 -or $unsafeSharedDirectories.Count -gt 0) {
                if ($remainingService) { Write-Output 'FATAL: MySQL80 服务仍然存在。' }
                if ($remainingDirectories.Count -gt 0) { Write-Output ('FATAL: MySQL 目录仍然存在：' + ($remainingDirectories -join ', ')) }
                if ($unsafeSharedDirectories.Count -gt 0) {
                    Write-Output ('FATAL: 检测到 MySQL 位于共享 Runtime 目录，未自动删除：' + ($unsafeSharedDirectories -join ', '))
                }
                Write-Output '请关闭占用进程或重启 Windows 后再次点击卸载。'
                Stop-Transcript | Out-Null
                exit 1
            }

            Stop-Transcript | Out-Null
            exit 0
            """;
    }

    private static string? GetServiceExecutablePath(string serviceName)
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
            var imagePath = key?.GetValue("ImagePath")?.ToString();
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            return ExtractExecutablePath(Environment.ExpandEnvironmentVariables(imagePath));
        }
        catch
        {
            return null;
        }
    }

    private static string? ExtractExecutablePath(string commandLine)
    {
        var text = commandLine.Trim();
        if (text.Length == 0)
        {
            return null;
        }

        if (text[0] == '"')
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text.Substring(1, end - 1) : null;
        }

        var exeIndex = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exeIndex >= 0 ? text.Substring(0, exeIndex + 4).Trim() : null;
    }

    private static bool ServiceExists(string serviceName)
    {
        return QueryService(serviceName)?.ExitCode == 0;
    }

    private static bool ServiceRunning(string serviceName)
    {
        var result = QueryService(serviceName);
        return result is not null &&
               result.ExitCode == 0 &&
               result.StandardOutput.Contains("RUNNING", StringComparison.OrdinalIgnoreCase);
    }

    private static RuntimeStatusKind ServiceStatus(string serviceName)
    {
        var result = QueryService(serviceName);
        if (result is null)
        {
            return RuntimeStatusKind.Unknown;
        }

        if (result.ExitCode != 0)
        {
            return RuntimeStatusKind.NotInstalled;
        }

        var output = result.StandardOutput;
        if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase)) return RuntimeStatusKind.Running;
        if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase)) return RuntimeStatusKind.Starting;
        if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase)) return RuntimeStatusKind.Stopping;
        if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase)) return RuntimeStatusKind.Stopped;
        return RuntimeStatusKind.Unknown;
    }

    private static ProcessRunResult? QueryService(string serviceName)
    {
        try
        {
            return ProcessRunner.RunSynchronously(
                "sc.exe",
                $"query {Compat.QuoteCommandLineArgument(serviceName)}",
                ComponentPaths.ApplicationRoot,
                captureOutput: true,
                timeout: TimeSpan.FromMilliseconds(2500));
        }
        catch
        {
            return null;
        }
    }

    private static string RequireTomcatRoot() => FindTomcatRoot() ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");
    private static string RequireNginxExe() => FindNginxExe() ?? throw new FileNotFoundException("未找到 nginx.exe。");

    private static bool IsNginxProcessOrServiceRunning(string nginxRoot) =>
        NginxRuntimeManager.IsRunningUnderRoot(nginxRoot) ||
        NginxWindowsServiceManager.IsRunningForRoot(nginxRoot);

    private static bool IsNginxOperational(string nginxRoot)
    {
        if (!IsNginxProcessOrServiceRunning(nginxRoot))
        {
            return false;
        }

        return NginxRuntimeManager.AreConfiguredPortsListening(
            nginxRoot,
            out _,
            out _);
    }

    private static string? FindTomcatRoot() => new ComponentLocator().FindTomcatRoot();
    private static string? FindNginxExe() => new ComponentLocator().FindNginxExecutable();
    private static string? FindMySqlExe() => new ComponentLocator().FindMySqlExecutable();

    private static HashSet<int> GetActiveTcpPorts()
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            return [];
        }
    }

    private static async Task<string> ExecuteMySqlAsync(
        string mysqlExe,
        MySqlDefaultCredentials credentials,
        string sql,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = mysqlExe,
            WorkingDirectory = Path.GetDirectoryName(mysqlExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        startInfo.EnvironmentVariables["MYSQL_PWD"] = credentials.Password;
        startInfo.Arguments = string.Join(" ", new[]
        {
            "--protocol=TCP",
            Compat.QuoteCommandLineArgument($"--host={credentials.Host}"),
            $"--port={credentials.Port}",
            Compat.QuoteCommandLineArgument($"--user={credentials.UserName}"),
            "--batch",
            "--skip-column-names",
            Compat.QuoteCommandLineArgument($"--execute={sql}")
        });

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("无法启动 MySQL 命令行工具。");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var errorTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        var output = (await outputTask).Trim();
        var error = (await errorTask).Trim();
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(error)
                ? $"MySQL 命令执行失败，退出码：{process.ExitCode}"
                : error);
        }

        return output;
    }

    private static int ParseMySqlMajorVersion(string version)
    {
        var firstLine = version
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault() ?? string.Empty;
        var majorText = firstLine.Split('.', 2)[0];
        return int.TryParse(majorText, NumberStyles.None, CultureInfo.InvariantCulture, out var major)
            ? major
            : 5;
    }

    private static string EscapeMySqlString(string value)
    {
        return value
            .Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("'", "''", StringComparison.Ordinal);
    }

    private static string? FindMySqlRoot()
    {
        return new ComponentLocator().FindMySqlRoot(GetServiceExecutablePath("MySQL80"));
    }

    private static MySqlDefaultCredentials LoadEffectiveMySqlCredentials() =>
        LoadEffectiveMySqlCredentials(new ComponentLocator(), null);

    private static MySqlDefaultCredentials LoadEffectiveMySqlCredentials(
        ComponentLocator locator,
        string? mysqlRoot)
    {
        var credentials = MySqlCredentialStore.Load();
        var resolvedRoot = mysqlRoot ?? locator.FindMySqlRoot(GetServiceExecutablePath("MySQL80"));
        var configuredPort = resolvedRoot is null ? null : TryReadMySqlConfiguredPort(resolvedRoot);
        return configuredPort is > 0 and <= 65535
            ? credentials with { Port = configuredPort.Value }
            : credentials;
    }

    private static int? TryReadMySqlConfiguredPort(string mysqlRoot)
    {
        try
        {
            var myIni = Path.Combine(mysqlRoot, "my.ini");
            return File.Exists(myIni)
                ? ReadMySqlIniPort(File.ReadAllText(myIni, Encoding.UTF8))
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? FindMySqlServerExecutable(string? mysqlRoot, string? serviceExecutable)
    {
        var candidates = new[]
        {
            string.IsNullOrWhiteSpace(mysqlRoot) ? null : Path.Combine(mysqlRoot, "bin", "mysqld.exe"),
            serviceExecutable
        };

        return candidates.FirstOrDefault(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path));
    }

    private static string? ReadMySqlServerVersion(string? mysqldExecutable)
    {
        if (string.IsNullOrWhiteSpace(mysqldExecutable) || !File.Exists(mysqldExecutable))
        {
            return null;
        }
        var executablePath = mysqldExecutable!;

        try
        {
            var result = ProcessRunner.RunSynchronously(
                executablePath,
                "--version",
                Path.GetDirectoryName(executablePath),
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3));
            return MySqlReleaseCatalog.ExtractServerVersion(result.CombinedOutput);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindDedicatedMySqlRoot()
    {
        var runtimeRoots = new[]
        {
            Path.GetFullPath(ComponentPaths.RuntimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(ComponentPaths.LegacyRuntimeRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
        };
        var locator = new ComponentLocator();

        foreach (var root in ComponentPaths.MySqlInstallationRoots)
        {
            var mysqlExe = locator.FindMySqlExecutable(root);
            if (mysqlExe is null)
            {
                continue;
            }

            var executableDirectory = Path.GetDirectoryName(mysqlExe);
            var installationRoot = string.IsNullOrWhiteSpace(executableDirectory)
                ? null
                : Directory.GetParent(executableDirectory)?.FullName;
            if (string.IsNullOrWhiteSpace(installationRoot))
            {
                continue;
            }

            var normalizedRoot = Path.GetFullPath(installationRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (runtimeRoots.Any(sharedRoot => string.Equals(normalizedRoot, sharedRoot, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            return installationRoot;
        }

        return null;
    }

    private static string? FindSqlServerDataRoot()
    {
        return ComponentPaths.SqlServerDataRoots.FirstOrDefault(Directory.Exists);
    }

    private static string? FindIisRoot()
    {
        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
        var inetpub = Path.Combine(systemDrive + Path.DirectorySeparatorChar, "inetpub");
        if (Directory.Exists(inetpub))
        {
            return inetpub;
        }

        var inetsrv = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv");
        return Directory.Exists(inetsrv) ? inetsrv : null;
    }

    private static string? FindServiceExecutableDirectory(string serviceName)
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"SYSTEM\CurrentControlSet\Services\{serviceName}");
        var imagePath = key?.GetValue("ImagePath") as string;
        if (string.IsNullOrWhiteSpace(imagePath))
        {
            return null;
        }

        var expanded = Environment.ExpandEnvironmentVariables(imagePath!.Trim());
        string executablePath;
        if (expanded.StartsWith("\"", StringComparison.Ordinal))
        {
            var closingQuote = expanded.IndexOf('"', 1);
            executablePath = closingQuote > 1 ? expanded.Substring(1, closingQuote - 1) : expanded.Trim('"');
        }
        else
        {
            var executableEnd = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            executablePath = executableEnd >= 0 ? expanded.Substring(0, executableEnd + 4) : expanded;
        }

        return File.Exists(executablePath) ? Path.GetDirectoryName(executablePath) : null;
    }

    private static async Task<string> RunFileAsync(string fileName, string arguments, string workingDirectory, bool elevated, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunFileAsync(
            fileName,
            arguments,
            workingDirectory,
            elevated,
            cancellationToken,
            captureOutput: !elevated,
            timeout: TimeSpan.FromSeconds(30));
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.CombinedOutput)
                ? $"命令退出码：{result.ExitCode}"
                : result.CombinedOutput);
        }

        return result.CombinedOutput;
    }

    private static void StartDetached(string fileName, string workingDirectory)
    {
        if (!File.Exists(fileName))
        {
            throw new FileNotFoundException("找不到运行文件。", fileName);
        }

        ProcessRunner.StartDetached(fileName, workingDirectory);
    }

    private static bool IsPortAvailable(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static int? ReadMySqlIniPort(string ini)
    {
        var section = string.Empty;
        using var reader = new StringReader(ini);
        while (reader.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                section = trimmed;
                continue;
            }

            if (!section.Equals("[mysqld]", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0 ||
                !trimmed.Substring(0, separator).Trim().Equals("port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = trimmed.Substring(separator + 1).Trim();
            var comment = value.IndexOfAny(['#', ';']);
            if (comment >= 0)
            {
                value = value.Substring(0, comment).Trim();
            }

            if (int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                return port;
            }
        }

        return null;
    }

    private static string UpdateMySqlIniPort(string ini, int port)
    {
        var newline = ini.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = ini.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').ToList();
        var section = string.Empty;
        var mysqldPortUpdated = false;
        var clientPortUpdated = false;

        for (var index = 0; index < lines.Count; index++)
        {
            var trimmed = lines[index].Trim();
            if (trimmed.StartsWith("[", StringComparison.Ordinal) &&
                trimmed.EndsWith("]", StringComparison.Ordinal))
            {
                section = trimmed;
                continue;
            }

            var separator = trimmed.IndexOf('=');
            if (separator <= 0 ||
                !trimmed.Substring(0, separator).Trim().Equals("port", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (section.Equals("[mysqld]", StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = $"port={port}";
                mysqldPortUpdated = true;
            }
            else if (section.Equals("[client]", StringComparison.OrdinalIgnoreCase))
            {
                lines[index] = $"port={port}";
                clientPortUpdated = true;
            }
        }

        if (!mysqldPortUpdated)
        {
            var sectionIndex = lines.FindIndex(line => line.Trim().Equals("[mysqld]", StringComparison.OrdinalIgnoreCase));
            if (sectionIndex >= 0)
            {
                lines.Insert(sectionIndex + 1, $"port={port}");
            }
            else
            {
                lines.Add("[mysqld]");
                lines.Add($"port={port}");
            }
        }

        if (!clientPortUpdated)
        {
            var sectionIndex = lines.FindIndex(line => line.Trim().Equals("[client]", StringComparison.OrdinalIgnoreCase));
            if (sectionIndex >= 0)
            {
                lines.Insert(sectionIndex + 1, $"port={port}");
            }
            else
            {
                lines.Add("[client]");
                lines.Add($"port={port}");
            }
        }

        return string.Join(newline, lines);
    }

    private static async Task WaitForTcpPortAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        Exception? lastError = null;

        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var client = new TcpClient();
                await client.ConnectAsync(host, port, cancellationToken);
                return;
            }
            catch (Exception ex) when (ex is SocketException or IOException)
            {
                lastError = ex;
                await Task.Delay(400, cancellationToken);
            }
        }

        throw new TimeoutException($"MySQL 服务已启动，但端口 {port} 在等待时间内没有开始监听。", lastError);
    }

    private static Task RunNginxServiceActionAsync(string action, CancellationToken cancellationToken)
        => Task.Run(() => RunNginxServiceActionCoreAsync(action, cancellationToken), cancellationToken);

    private static async Task RunNginxServiceActionCoreAsync(string action, CancellationToken cancellationToken)
    {
        try
        {
            switch (action)
            {
                case "start":
                    NginxWindowsServiceManager.Start();
                    break;
                case "stop":
                    NginxWindowsServiceManager.Stop();
                    break;
                case "delete":
                    NginxWindowsServiceManager.Delete();
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, "不支持的 Nginx 服务操作。");
            }
        }
        catch (Exception ex) when (NginxWindowsServiceManager.IsAccessDenied(ex))
        {
            await RunElevatedPowerShellAsync($"sc.exe {action} {NginxWindowsServiceManager.ServiceName}", cancellationToken);
            switch (action)
            {
                case "start":
                    NginxWindowsServiceManager.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
                    break;
                case "stop":
                    NginxWindowsServiceManager.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
                    break;
                case "delete":
                    NginxWindowsServiceManager.WaitUntilMissing(TimeSpan.FromSeconds(20));
                    break;
            }
        }
    }

    internal static string BuildIisServiceActionScript(string action)
    {
        if (action is not ("START" or "STOP" or "RESTART"))
            throw new ArgumentOutOfRangeException(nameof(action));

        var expected = action == "STOP" ? "Stopped" : "Running";
        return $@"$iisreset = Join-Path $env:windir 'System32\iisreset.exe'
if (!(Test-Path -LiteralPath $iisreset)) {{ throw '未找到 IIS 重置工具 iisreset.exe。' }}
& $iisreset /{action}
if ($LASTEXITCODE -ne 0) {{ throw ('IIS {action} 失败，退出码：' + $LASTEXITCODE) }}
$service = Get-Service -Name 'W3SVC' -ErrorAction Stop
$service.WaitForStatus('{expected}', [TimeSpan]::FromSeconds(30))
if ($service.Status -ne '{expected}') {{ throw 'W3SVC 未进入 {expected} 状态。' }}";
    }

    private static async Task RunElevatedPowerShellAsync(string command, CancellationToken cancellationToken)
    {
        var workDirectory = ComponentPaths.WorkRoot;
        var operationId = Guid.NewGuid().ToString("N");
        var script = Path.Combine(workDirectory, $"environment-action-{operationId}.ps1");
        var log = Path.Combine(workDirectory, $"environment-action-{operationId}.log");
        await ElevatedActionLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(workDirectory);
            RollingLogWriter.EnsureCapacity(log);
            var scriptText = BuildElevatedPowerShellScript(command, log);
            await FileCompat.WriteAllTextAsync(script, scriptText, new UTF8Encoding(true), cancellationToken);
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(workDirectory, "environment-action-*.log*", log);
            ElevatedActionLock.Release();
        }
    }

    private static void PruneGeneratedLogs(string workDirectory, string pattern, string activeLog)
    {
        try
        {
            var logs = Directory.EnumerateFiles(workDirectory, pattern, SearchOption.TopDirectoryOnly)
                .Where(path => !path.Equals(activeLog, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path))
                .Skip(8)
                .ToArray();
            foreach (var log in logs)
            {
                TryDeleteFile(log);
            }
        }
        catch
        {
            // Log retention is best-effort and must not change the operation result.
        }
    }

    internal static string BuildElevatedPowerShellScript(string command, string logPath) =>
        $$"""
            $ErrorActionPreference = 'Stop'
            $ProgressPreference = 'SilentlyContinue'
            $log = '{{EscapePowerShellPath(logPath)}}'
            try {
                Start-Transcript -Path $log | Out-Null
                $global:LASTEXITCODE = 0
                {{command}}
                if ($LASTEXITCODE -ne $null -and $LASTEXITCODE -ne 0) {
                    throw ('原生命令退出码：' + $LASTEXITCODE)
                }
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Write-Output ('FATAL: ' + $_.Exception.Message)
                try { Stop-Transcript | Out-Null } catch { }
                exit 1
            }
            """;

    private static async Task<int> RunElevatedPowerShellFileAsync(
        string script,
        CancellationToken cancellationToken,
        string? logPath = null)
    {
        try
        {
            var result = await ProcessRunner.RunPowerShellFileAsync(
                script,
                elevated: true,
                cancellationToken: cancellationToken);
            var exitCode = result.ExitCode;
            if (exitCode != 0 && exitCode != 3010)
            {
                throw EnvironmentOperationDiagnostics.CreateScriptFailure(
                    Path.GetFileNameWithoutExtension(script) + " ",
                    exitCode,
                    logPath);
            }

            return exitCode;
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new EnvironmentOperationException("已取消管理员授权，操作未执行。", null, ex);
        }
    }

    private static void DeleteDirectory(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var tomcatRoot = Path.GetFullPath(ComponentPaths.TomcatRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var migratedRoot = Path.GetFullPath(ComponentPaths.RuntimeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var legacyRoot = Path.GetFullPath(ComponentPaths.LegacyRuntimeRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var isManagedTomcat = (full.StartsWith(migratedRoot, StringComparison.OrdinalIgnoreCase) ||
                               full.StartsWith(legacyRoot, StringComparison.OrdinalIgnoreCase)) &&
                             Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar)).StartsWith("apache-tomcat-", StringComparison.OrdinalIgnoreCase);
        if (!full.StartsWith(tomcatRoot, StringComparison.OrdinalIgnoreCase) && !isManagedTomcat)
        {
            throw new InvalidOperationException("卸载目录不在 MCPanel Tomcat 组件目录下，已取消删除。");
        }

        if (Directory.Exists(full))
        {
            ClearDirectoryAttributes(full);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Directory.Delete(full, recursive: true);
                    return;
                }
                catch (IOException) when (attempt < 2)
                {
                    Thread.Sleep(100);
                }
                catch (UnauthorizedAccessException) when (attempt < 2)
                {
                    ClearDirectoryAttributes(full);
                    Thread.Sleep(100);
                }
            }

            Directory.Delete(full, recursive: true);
        }
    }

    private static void ClearDirectoryAttributes(string path)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }

            File.SetAttributes(path, FileAttributes.Normal);
        }
        catch
        {
            // Directory.Delete below supplies the actionable final error.
        }
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

    private static string ReadTextFileBestEffort(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return string.Empty;
            }

            var bytes = File.ReadAllBytes(path);
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

            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string DisplayName(EnvironmentKind kind) => kind switch
    {
        EnvironmentKind.Tomcat => "Tomcat",
        EnvironmentKind.Nginx => "Nginx",
        EnvironmentKind.MySql => "MySQL",
        EnvironmentKind.SqlServer => "SQL Server",
        EnvironmentKind.Iis => "IIS",
        _ => kind.ToString()
    };

    private static string EscapePowerShellPath(string path) => path.Replace("'", "''");
    private static string AppCmdPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
    private static string StoreDataRoot => ComponentPaths.StoreDataRoot;
    private static string IisPendingUninstallMarker => Path.Combine(ComponentPaths.RuntimeStateRoot, "iis-pending-uninstall-restart.flag");
    private static string IisUninstalledMarker => Path.Combine(ComponentPaths.RuntimeStateRoot, "iis-uninstalled.flag");
    internal static bool HasIisUninstallContinuation => File.Exists(IisPendingUninstallMarker);
    internal static string IisUninstallContinuationMessage => "IIS 组件卸载已暂存，请重启设备后点击“继续卸载”。";
}
