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

    private static EnvironmentRuntimeState GetSqlServerState(ComponentLocator locator)
    {
        EnvironmentRuntimeState WithConnection(EnvironmentRuntimeState state)
        {
            if (!state.IsInstalled) return state;
            var credentials = SqlServerCredentialStore.Load();
            return state with { ConnectionDescription = $"{credentials.Host},{credentials.Port} / {credentials.UserName}" };
        }

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
                return WithConnection(Installed(
                    RuntimeStatusKind.Stopped,
                    $"SQL Server 服务报告正在运行，但配置端口 {configuredPort.Value} 未监听，已判定为启动异常。{versionText}{FormatSqlServerConnectionText()}",
                    detectedSqlServerReleaseId: release?.Id,
                    detectedSqlServerDisplayName: displayName));
            }

            return WithConnection(Installed(
                serviceStatus,
                $"SQL Server 已安装为 MSSQLSERVER 服务。{versionText}{portText}{FormatSqlServerConnectionText()}",
                detectedSqlServerReleaseId: release?.Id,
                detectedSqlServerDisplayName: displayName));
        }

        return WithConnection(EnvironmentInstaller.HasSqlServerInstallContinuation
            ? new EnvironmentRuntimeState(false, false, EnvironmentInstaller.SqlServerInstallContinuationMessage, RuntimeStatusKind.NotInstalled)
            : NotInstalled());
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

    private static string? FindSqlServerDataRoot()
    {
        return ComponentPaths.SqlServerDataRoots.FirstOrDefault(Directory.Exists);
    }
}
