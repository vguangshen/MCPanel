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
                    $net = Join-Path $env:windir 'System32\net.exe'
                    if (!(Test-Path -LiteralPath $net)) { throw '未找到 Windows 服务管理工具 net.exe。' }
                    $was = Get-Service -Name WAS -ErrorAction SilentlyContinue
                    $w3svc = Get-Service -Name W3SVC -ErrorAction SilentlyContinue
                    if ($was -and $was.Status -ne 'Stopped') {
                        & $net stop WAS /y
                        if ($LASTEXITCODE -ne 0) { throw ('停止 WAS 失败，退出码：' + $LASTEXITCODE) }
                    }
                    elseif ($w3svc -and $w3svc.Status -ne 'Stopped') {
                        & $net stop W3SVC
                        if ($LASTEXITCODE -ne 0) { throw ('停止 W3SVC 失败，退出码：' + $LASTEXITCODE) }
                    }
                    if ($w3svc) { $w3svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) }
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

    private static EnvironmentRuntimeState GetIisState(ComponentLocator locator)
    {
        var serviceExists = ServiceExists("W3SVC");
        return EvaluateIisState(
            serviceExists,
            serviceExists ? ServiceStatus("W3SVC") : RuntimeStatusKind.NotInstalled,
            File.Exists(IisPendingUninstallMarker),
            EnvironmentInstaller.HasIisInstallContinuation,
            File.Exists(IisUninstalledMarker),
            EnvironmentInstaller.HasIisConfigurationRepairPending,
            serviceExists ? TryProbeIisRewriteModule() : null,
            serviceExists ? TryProbeIisDefaultDocuments() : null);
    }

    // W3SVC is the IIS WWW publishing service, not a proof that optional
    // configuration (including the third-party URL Rewrite module) succeeded.
    // Keep core installation separate from the optional repair state.
    internal static EnvironmentRuntimeState EvaluateIisState(
        bool serviceExists,
        RuntimeStatusKind serviceStatus,
        bool pendingUninstall,
        bool pendingInstallRestart,
        bool uninstalledMarker,
        bool repairPending,
        bool? rewriteModuleInstalled,
        bool? defaultDocumentsConfigured = null)
    {
        if (pendingUninstall)
        {
            return new EnvironmentRuntimeState(false, false, IisUninstallContinuationMessage, RuntimeStatusKind.Unknown);
        }

        if (!serviceExists)
        {
            if (pendingInstallRestart)
            {
                return new EnvironmentRuntimeState(false, false, EnvironmentInstaller.IisInstallContinuationMessage, RuntimeStatusKind.Unknown);
            }

            return uninstalledMarker
                ? new EnvironmentRuntimeState(false, false, "IIS 已卸载。", RuntimeStatusKind.NotInstalled)
                : NotInstalled();
        }

        if (pendingInstallRestart)
        {
            return Installed(serviceStatus, "IIS Web 服务已安装，但 Windows 功能或 URL Rewrite 仍需重启后继续安装。");
        }

        if (repairPending || rewriteModuleInstalled == false || defaultDocumentsConfigured == false)
        {
            var detail = rewriteModuleInstalled == false
                ? "未检测到 URL Rewrite 模块。"
                : defaultDocumentsConfigured == false
                    ? "默认文档配置不完整。"
                    : "上次 IIS 安装或配置中断。";
            return new EnvironmentRuntimeState(
                true,
                serviceStatus == RuntimeStatusKind.Running,
                "IIS 已安装。" + detail + "配置尚未完成，请点击“修复配置”。",
                serviceStatus,
                NeedsConfigurationRepair: true);
        }

        return Installed(serviceStatus, rewriteModuleInstalled == true
            ? "IIS 已安装，URL Rewrite 已检测到，可管理网站。"
            : "IIS 已安装，可管理网站；URL Rewrite 状态暂无法核验。");
    }

    // Microsoft's supported appcmd command lists the installed/active IIS modules.
    // An unreadable result is unknown, not a missing module. Cache to keep
    // periodic environment-card refreshes from spawning appcmd continuously.
    internal static void InvalidateIisModuleProbe()
    {
        lock (IisModuleProbeLock)
        {
            _iisModuleProbeTimestamp = DateTime.MinValue;
            _iisModuleProbeResult = null;
            _iisDocumentProbeTimestamp = DateTime.MinValue;
            _iisDocumentProbeResult = null;
        }
    }

    internal static bool? TryProbeIisRewriteModule()
    {
        lock (IisModuleProbeLock)
        {
            if (DateTime.UtcNow - _iisModuleProbeTimestamp < TimeSpan.FromSeconds(30))
            {
                return _iisModuleProbeResult;
            }

            bool? result = null;
            try
            {
                var appcmd = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "inetsrv", "appcmd.exe");
                if (File.Exists(appcmd))
                {
                    var probe = ProcessRunner.RunSynchronously(
                        appcmd,
                        "list modules",
                        ComponentPaths.ApplicationRoot,
                        captureOutput: true,
                        timeout: TimeSpan.FromSeconds(3));
                    if (probe is not null && probe.ExitCode == 0)
                    {
                        result = probe.StandardOutput.Contains(
                            "RewriteModule", StringComparison.OrdinalIgnoreCase);
                    }
                }
            }
            catch
            {
                // Failure to inspect the module does not mean IIS is uninstalled.
            }

            _iisModuleProbeResult = result;
            _iisModuleProbeTimestamp = DateTime.UtcNow;
            return result;
        }
    }

    internal static bool? TryProbeIisDefaultDocuments()
    {
        lock (IisModuleProbeLock)
        {
            if (DateTime.UtcNow - _iisDocumentProbeTimestamp < TimeSpan.FromSeconds(30))
            {
                return _iisDocumentProbeResult;
            }

            bool? result = null;
            try
            {
                var appcmd = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                    "System32", "inetsrv", "appcmd.exe");
                if (File.Exists(appcmd))
                {
                    var probe = ProcessRunner.RunSynchronously(
                        appcmd,
                        "list config /section:defaultDocument",
                        ComponentPaths.ApplicationRoot,
                        captureOutput: true,
                        timeout: TimeSpan.FromSeconds(3));
                    if (probe is not null && probe.ExitCode == 0)
                    {
                        result = HasRequiredIisDefaultDocuments(probe.StandardOutput);
                    }
                }
            }
            catch
            {
                // Missing permissions or malformed output is unknown, not an IIS failure.
            }

            _iisDocumentProbeResult = result;
            _iisDocumentProbeTimestamp = DateTime.UtcNow;
            return result;
        }
    }

    internal static bool HasRequiredIisDefaultDocuments(string configurationXml)
    {
        var document = XDocument.Parse("<configuration>" + configurationXml + "</configuration>");
        var names = document.Descendants("defaultDocument")
            .Elements("files")
            .Elements("add")
            .Select(element => (string?)element.Attribute("value"))
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return RequiredIisDefaultDocuments.All(names.Contains);
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

    internal static string BuildIisServiceActionScript(string action)
    {
        if (action is not ("START" or "STOP" or "RESTART"))
            throw new ArgumentOutOfRangeException(nameof(action));

        var expected = action == "STOP" ? "Stopped" : "Running";
        var command = action switch
        {
            "START" => @"if ($service.Status -ne 'Running') {
    & $net start W3SVC
    if ($LASTEXITCODE -ne 0) { throw ('IIS 启动失败，退出码：' + $LASTEXITCODE) }
}",
            "STOP" => @"if ($service.Status -ne 'Stopped') {
    & $net stop W3SVC
    if ($LASTEXITCODE -ne 0) { throw ('IIS 停止失败，退出码：' + $LASTEXITCODE) }
}",
            _ => @"& $net stop W3SVC
if ($LASTEXITCODE -ne 0) { throw ('IIS 重启时停止失败，退出码：' + $LASTEXITCODE) }
$service.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30))
& $net start W3SVC
if ($LASTEXITCODE -ne 0) { throw ('IIS 重启时启动失败，退出码：' + $LASTEXITCODE) }"
        };
        return $@"$net = Join-Path $env:windir 'System32\net.exe'
if (!(Test-Path -LiteralPath $net)) {{ throw '未找到 Windows 服务管理工具 net.exe。' }}
$service = Get-Service -Name 'W3SVC' -ErrorAction Stop
{command}
$service = Get-Service -Name 'W3SVC' -ErrorAction Stop
$service.WaitForStatus('{expected}', [TimeSpan]::FromSeconds(30))
if ($service.Status -ne '{expected}') {{ throw 'W3SVC 未进入 {expected} 状态。' }}";
    }
}
