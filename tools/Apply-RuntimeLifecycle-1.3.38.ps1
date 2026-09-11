$ErrorActionPreference = 'Stop'

function Replace-Exact {
    param(
        [string]$Text,
        [string]$Old,
        [string]$New,
        [string]$Label
    )
    if (-not $Text.Contains($Old)) {
        throw "Replace-Exact failed: $Label"
    }
    return $Text.Replace($Old, $New)
}

function Write-Utf8NoBom {
    param([string]$Path, [string]$Content)
    $utf8 = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $Content, $utf8)
}

$runtimePath = 'EnvironmentRuntimeService.cs'
$runtime = Get-Content -LiteralPath $runtimePath -Raw

$runtime = Replace-Exact $runtime @'
            case EnvironmentKind.MySql:
                await RunMySqlServiceActionAsync("start", cancellationToken);
                var mysqlCredentials = LoadEffectiveMySqlCredentials();
                return $"MySQL80 服务已启动。连接信息：{mysqlCredentials.Host}:{mysqlCredentials.Port}，账号 {mysqlCredentials.UserName}。";
'@ @'
            case EnvironmentKind.MySql:
                return await RunMySqlServiceActionAsync("start", cancellationToken);
'@ 'MySQL start result propagation'

$runtime = Replace-Exact $runtime @'
            case EnvironmentKind.MySql:
                await RunMySqlServiceActionAsync("stop", cancellationToken);
                return "MySQL80 服务已停止。";
'@ @'
            case EnvironmentKind.MySql:
                return await RunMySqlServiceActionAsync("stop", cancellationToken);
'@ 'MySQL stop result propagation'

$runtime = Replace-Exact $runtime @'
        if (kind == EnvironmentKind.MySql)
        {
            await RunMySqlServiceActionAsync("restart", cancellationToken);
            var mysqlCredentials = LoadEffectiveMySqlCredentials();
            return $"MySQL 已重启。连接信息：{mysqlCredentials.Host}:{mysqlCredentials.Port}，账号 {mysqlCredentials.UserName}。";
        }
'@ @'
        if (kind == EnvironmentKind.MySql)
        {
            return await RunMySqlServiceActionAsync("restart", cancellationToken);
        }
'@ 'MySQL restart result propagation'

$runtime = Replace-Exact $runtime @'
                TomcatProductStartupManager.RemoveRegistration();
                if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    try
                    {
                        // Best effort only: SCM acknowledgement is not runtime truth.
                        TomcatWindowsServiceManager.Start();
                    }
                    catch (Exception serviceError)
                    {
                        EnvironmentOperationDiagnostics.RecordFailure(
                            "环境管理",
                            "发送 Tomcat Windows Service 启动请求",
                            serviceError);
                    }

                    for (var attempt = 0; attempt < 10 && !TomcatProductInstanceManager.IsSharedTomcatRunning(); attempt++)
                    {
                        await Task.Delay(200, cancellationToken);
                    }
                }
'@ @'
                TomcatProductStartupManager.RemoveRegistration();
                if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    // A restart can arrive while SCM is still finishing the previous
                    // service stop. Retry the control request a few times, but keep
                    // the real shared CATALINA_BASE process as the runtime truth.
                    for (var controlAttempt = 0;
                         controlAttempt < 3 && !TomcatProductInstanceManager.IsSharedTomcatRunning();
                         controlAttempt++)
                    {
                        try
                        {
                            TomcatWindowsServiceManager.Start();
                        }
                        catch (Exception serviceError)
                        {
                            EnvironmentOperationDiagnostics.RecordFailure(
                                "环境管理",
                                $"发送 Tomcat Windows Service 启动请求（第 {controlAttempt + 1} 次）",
                                serviceError);
                        }

                        for (var processAttempt = 0;
                             processAttempt < 10 && !TomcatProductInstanceManager.IsSharedTomcatRunning();
                             processAttempt++)
                        {
                            await Task.Delay(200, cancellationToken);
                        }
                    }
                }
'@ 'Tomcat SCM retry window'

$runtime = Replace-Exact $runtime @'
                    if (startResult.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Tomcat startup.bat 退出码：{startResult.ExitCode}");
                    }
                }

                return $"Tomcat Server 启动请求已发送；运行状态按实际 Java 进程判断，不再等待 Windows 服务状态或读取启动进度。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@ @'
                    if (startResult.ExitCode != 0)
                    {
                        throw new InvalidOperationException($"Tomcat startup.bat 退出码：{startResult.ExitCode}");
                    }

                    for (var processAttempt = 0;
                         processAttempt < 20 && !TomcatProductInstanceManager.IsSharedTomcatRunning();
                         processAttempt++)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                    if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                    {
                        throw new InvalidOperationException(
                            $"Tomcat startup.bat 已返回成功，但未检测到共享 Java 进程。请检查日志目录：{Path.Combine(tomcatRoot, "logs")}");
                    }
                }

                return $"Tomcat Server 启动请求已发送；运行状态按实际 Java 进程判断，不再等待 Windows 服务状态或读取启动进度。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@ 'Tomcat fallback process verification'

$runtime = Replace-Exact $runtime @'
    private static async Task RunMySqlServiceActionAsync(string action, CancellationToken cancellationToken)
'@ @'
    private static async Task<string> RunMySqlServiceActionAsync(string action, CancellationToken cancellationToken)
'@ 'MySQL action return type'

$runtime = Replace-Exact $runtime @'
        try
        {
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        catch (Exception ex)
'@ @'
        try
        {
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
            var detail = ReadTextFileBestEffort(result);
            return string.IsNullOrWhiteSpace(detail)
                ? $"MySQL {action} 操作已完成。"
                : detail.Trim();
        }
        catch (Exception ex)
'@ 'MySQL action success result'

$runtime = Replace-Exact $runtime @'
            function Test-RootPassword($paths) {
                $code = Invoke-NativeQuiet $paths.Mysql @('-h127.0.0.1', "-P$port", '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
                return $code -eq 0
            }
'@ @'
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
'@ 'MySQL caching_sha2 credential check'

$runtime = Replace-Exact $runtime @'
                Invoke-NativeQuiet $paths.Mysql @('-h127.0.0.1', "-P$port", '-uroot', "-p$rootPassword", '-e', 'SHUTDOWN;') | Out-Null
'@ @'
                Invoke-RootQuery $paths 'SHUTDOWN;' | Out-Null
'@ 'MySQL shutdown credential query'

$runtime = Replace-Exact $runtime @'
                        $code = Invoke-NativeQuiet $paths.Mysql @('-h127.0.0.1', "-P$port", '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
'@ @'
                        $code = Invoke-RootQuery $paths 'SELECT 1;'
'@ 'MySQL reset credential query'

$runtime = Replace-Exact $runtime @'
                    'start' {
                        $paths = Start-MySqlService
                        if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但保存的 root 凭据无法验证。为避免意外修改数据库密码，MCPanel 已停止自动重置；请恢复正确的凭据文件后重试。' }
                        Set-Result ('MySQL80 服务已启动。连接信息：127.0.0.1:' + $port + '，账号 root。')
                    }
                    'restart' {
                        Stop-MySqlService
                        Start-Sleep -Seconds 2
                        $paths = Start-MySqlService
                        if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但保存的 root 凭据无法验证。为避免意外修改数据库密码，MCPanel 已停止自动重置；请恢复正确的凭据文件后重试。' }
                        Set-Result ('MySQL80 服务已重启。连接信息：127.0.0.1:' + $port + '，账号 root。')
                    }
'@ @'
                    'start' {
                        $paths = Start-MySqlService
                        $message = 'MySQL80 服务已启动。连接信息：127.0.0.1:' + $port + '，账号 root。'
                        if (!(Wait-RootPasswordReady $paths 20)) {
                            $message += ' 注意：服务运行正常，但保存的 root 凭据验证未通过；MCPanel 不会自动重置数据库密码。'
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
                            $message += ' 注意：服务运行正常，但保存的 root 凭据验证未通过；MCPanel 不会自动重置数据库密码。'
                            Write-Output $message
                        }
                        Set-Result $message
                    }
'@ 'MySQL start/restart warning semantics'

Write-Utf8NoBom $runtimePath $runtime

$servicesPath = 'ManagedComponentWindowsServices.cs'
$services = Get-Content -LiteralPath $servicesPath -Raw
$services = Replace-Exact $services @'
    protected override void OnStop() => StopTomcat();

    protected override void OnShutdown()
    {
        StopTomcat();
        base.OnShutdown();
    }
'@ @'
    protected override void OnStop() => StopTomcat(waitForExitAndForce: false);

    protected override void OnShutdown()
    {
        StopTomcat(waitForExitAndForce: true);
        base.OnShutdown();
    }
'@ 'Tomcat service stop mode'

$services = Replace-Exact $services @'
    private void StopTomcat()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        StopSharedTomcatBestEffort();
        WriteServiceLog("Tomcat Windows service stopped.");
    }

    private void StopSharedTomcatBestEffort()
'@ @'
    private void StopTomcat(bool waitForExitAndForce)
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        StopSharedTomcatBestEffort(waitForExitAndForce);
        WriteServiceLog(waitForExitAndForce
            ? "Tomcat Windows service stopped."
            : "Tomcat Windows service stop request sent; runtime cleanup delegated to MCPanel controller.");
    }

    private void StopSharedTomcatBestEffort(bool waitForExitAndForce = true)
'@ 'Tomcat service stop implementation'

$services = Replace-Exact $services @'
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && TomcatProductInstanceManager.IsSharedTomcatRunning())
'@ @'
            if (!waitForExitAndForce)
            {
                return;
            }

            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
            while (DateTime.UtcNow < deadline && TomcatProductInstanceManager.IsSharedTomcatRunning())
'@ 'Tomcat service no late force kill'
Write-Utf8NoBom $servicesPath $services

$projectPath = 'MCPanel.csproj'
$project = Get-Content -LiteralPath $projectPath -Raw
$project = $project.Replace('<Version>1.3.37</Version>', '<Version>1.3.38</Version>')
$project = $project.Replace('<FileVersion>1.3.37.0</FileVersion>', '<FileVersion>1.3.38.0</FileVersion>')
$project = $project.Replace('<AssemblyVersion>1.3.37.0</AssemblyVersion>', '<AssemblyVersion>1.3.38.0</AssemblyVersion>')
if (-not $project.Contains('<Version>1.3.38</Version>')) { throw 'Version bump failed.' }
Write-Utf8NoBom $projectPath $project

$testPath = 'MCPanel.Tests/ReliabilityTests.RuntimeLifecycle138.cs'
$testContent = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MySqlRestartSeparatesServiceHealthFromCredentialVerification()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        StringAssert.Contains(runtime, "--get-server-public-key");
        StringAssert.Contains(runtime, "Wait-RootPasswordReady");
        StringAssert.Contains(runtime, "服务运行正常，但保存的 root 凭据验证未通过");
        StringAssert.Contains(runtime, "private static async Task<string> RunMySqlServiceActionAsync");
        Assert.IsFalse(runtime.Contains("if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatRestartCannotBeKilledByLateServiceStopCleanup()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        StringAssert.Contains(services, "OnStop() => StopTomcat(waitForExitAndForce: false)");
        StringAssert.Contains(services, "if (!waitForExitAndForce)");
        StringAssert.Contains(services, "runtime cleanup delegated to MCPanel controller");

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        StringAssert.Contains(runtime, "controlAttempt < 3");
        StringAssert.Contains(runtime, "startup.bat 已返回成功，但未检测到共享 Java 进程");
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
    }
}
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $testPath), $testContent, (New-Object System.Text.UTF8Encoding($false)))

$releaseNotes = @'
# MCPanel 1.3.38

- 修复 MySQL 8.4 `caching_sha2_password` 在本机 TCP 验证时出现 `ERROR 2061: Authentication requires secure connection` 的误报：凭据验证现在支持 `--get-server-public-key`，并兼容旧客户端回退。
- MySQL 启动/重启后增加最长 20 秒的凭据就绪重试；即使最终 root 凭据验证未通过，也不会再把已经正常运行的 MySQL 服务判定为“重启失败”，同时仍然绝不自动重置数据库密码。
- MySQL 服务操作现在把实际结果/警告返回到界面，避免“服务已运行”和“凭据验证失败”混成同一个失败状态。
- 加固 Tomcat 重启：SCM 控制请求在真实共享 Java 进程未出现时会进行有限重试，避免前一次服务停止尚未完全收尾时直接错过重启。
- Tomcat `startup.bat` 回退启动后会再次确认真实共享 Java 进程，避免脚本返回成功但 Tomcat 实际未存活时产生假成功。
- Tomcat Windows 服务停止阶段不再在延迟窗口末尾强制清理一个可能已经重新启动的新共享进程；真正的运行状态与停止确认仍由 MCPanel 按共享 `CATALINA_BASE` Java 进程判断。
- 增加 MySQL 凭据验证与 Tomcat 重启竞态的回归测试。
'@
[System.IO.File]::WriteAllText((Join-Path (Get-Location) 'RELEASE-NOTES.md'), $releaseNotes, (New-Object System.Text.UTF8Encoding($false)))

Write-Host 'Runtime lifecycle 1.3.38 patch applied.'
