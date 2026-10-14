using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
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
    string? DetectedSqlServerDisplayName = null,
    bool NeedsConfigurationRepair = false,
    string? ConnectionDescription = null);

public sealed record EnvironmentRuntimeSnapshot(
    IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> States,
    IReadOnlyDictionary<EnvironmentKind, string?> InstallDirectories,
    IReadOnlyDictionary<EnvironmentKind, string>? ProbeErrors = null);

public sealed partial class EnvironmentRuntimeService
{
    private static readonly SemaphoreSlim ElevatedActionLock = new(1, 1);
    private static readonly object IisModuleProbeLock = new();
    private static DateTime _iisModuleProbeTimestamp = DateTime.MinValue;
    private static bool? _iisModuleProbeResult;
    private static DateTime _iisDocumentProbeTimestamp = DateTime.MinValue;
    private static bool? _iisDocumentProbeResult;
    private static readonly string[] RequiredIisDefaultDocuments =
        ["default.html", "default.asp", "default.aspx", "index.php", "index.asp", "index.aspx"];

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

    public EnvironmentRuntimeSnapshot GetSnapshot()
    {
        var locator = new ComponentLocator();
        return GetSnapshot(kind => GetState(kind, locator), kind => GetInstallDirectory(kind, locator));
    }

    internal static EnvironmentRuntimeSnapshot GetSnapshot(
        Func<EnvironmentKind, EnvironmentRuntimeState> probe,
        Func<EnvironmentKind, string?> findDirectory)
    {
        var states = new Dictionary<EnvironmentKind, EnvironmentRuntimeState>();
        var installDirectories = new Dictionary<EnvironmentKind, string?>();
        var errors = new Dictionary<EnvironmentKind, string>();

        foreach (var kind in new[]
                 {
                     EnvironmentKind.Iis,
                     EnvironmentKind.Nginx,
                     EnvironmentKind.MySql,
                     EnvironmentKind.SqlServer,
                     EnvironmentKind.Tomcat
                 })
        {
            try { states[kind] = probe(kind); }
            catch (Exception error) { errors[kind] = error.Message; }
            try { installDirectories[kind] = findDirectory(kind); }
            catch { installDirectories[kind] = null; }
        }

        try { installDirectories[EnvironmentKind.FrpTunnel] = findDirectory(EnvironmentKind.FrpTunnel); }
        catch { installDirectories[EnvironmentKind.FrpTunnel] = null; }
        return new EnvironmentRuntimeSnapshot(states, installDirectories, errors);
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

    public async Task<string> StartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)
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

                return $"Tomcat Server 已按标准 start 模式启动，普通 Tomcat CMD 窗口已打开。需要持续查看 Catalina 前台输出时请使用“以 Catalina 方式启动”。日志目录：{Path.Combine(tomcatRoot, "logs")}";

            case EnvironmentKind.Nginx:
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

    public async Task<string> RestartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)
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
    private static string StoreDataRoot => ComponentPaths.StoreDataRoot;
    private static string IisPendingUninstallMarker => Path.Combine(ComponentPaths.RuntimeStateRoot, "iis-pending-uninstall-restart.flag");
    private static string IisUninstalledMarker => Path.Combine(ComponentPaths.RuntimeStateRoot, "iis-uninstalled.flag");
    internal static bool HasIisUninstallContinuation => File.Exists(IisPendingUninstallMarker);
    internal static string IisUninstallContinuationMessage => "IIS 组件卸载已暂存，请重启设备后点击“继续卸载”。";
}
