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

public sealed partial class EnvironmentRuntimeService
{
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

    private static Task UninstallMySqlCompletelyAsync(CancellationToken cancellationToken) =>
        RunRuntimeUninstallAsync("mysql", log => BuildMySqlUninstallScript(FindDedicatedMySqlRoot(), log), cancellationToken);

    private static EnvironmentRuntimeState GetMySqlState(ComponentLocator locator)
    {
        var serviceExists = ServiceExists("MySQL80");
        var serviceExe = serviceExists ? GetServiceExecutablePath("MySQL80") : null;
        var mysqlRoot = locator.FindMySqlRoot(serviceExe);
        var credentials = LoadEffectiveMySqlCredentials(locator, mysqlRoot);

        EnvironmentRuntimeState WithConnection(EnvironmentRuntimeState state)
        {
            if (!state.IsInstalled) return state;
            return state with { ConnectionDescription = $"{credentials.Host}:{credentials.Port} / {credentials.UserName}" };
        }
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
                return WithConnection(new EnvironmentRuntimeState(
                    true,
                    false,
                    $"MySQL80 服务报告正在运行，但 my.ini 配置端口 {configuredPort.Value} 未监听，已判定为启动异常。{versionText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                    RuntimeStatusKind.Stopped,
                    detectedReleaseId,
                    detectedVersion));
            }

            return WithConnection(Installed(
                serviceStatus,
                $"MySQL 已安装到 {mysqlRoot}。{versionText}{portText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                detectedReleaseId,
                detectedVersion));
        }

        if (serviceExists)
        {
            if (!string.IsNullOrWhiteSpace(serviceExe) && !File.Exists(serviceExe))
            {
                return WithConnection(new EnvironmentRuntimeState(true, false, $"MySQL80 服务已注册，但程序文件缺失：{serviceExe}。请点击卸载清理残留后重新安装。", RuntimeStatusKind.Unknown));
            }

            var versionText = string.IsNullOrWhiteSpace(detectedVersion) ? string.Empty : $"版本 {detectedVersion}。";
            return WithConnection(Installed(
                ServiceStatus("MySQL80"),
                $"MySQL80 服务已注册。{versionText}连接信息：{credentials.Host}:{credentials.Port}，账号 {credentials.UserName}。",
                detectedReleaseId,
                detectedVersion));
        }

        return WithConnection(NotInstalled());
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

            {{EnvironmentInstaller.MySqlMaintenanceShutdownFunction}}

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
                    try { Stop-TemporaryMySqlProcess $proc $paths.Mysql $port $rootPassword }
                    finally { Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue }
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

    private static string? FindMySqlExe() => new ComponentLocator().FindMySqlExecutable();

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
}
