$ErrorActionPreference = 'Stop'

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText((Join-Path $PWD $Path), [Text.UTF8Encoding]::new($false))
}

function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText((Join-Path $PWD $Path), $Content, [Text.UTF8Encoding]::new($false))
}

# 1) Shared Tomcat normal Start/Restart now uses a visible Catalina CMD console.
#    The legacy Windows service wrapper is retired on first shared-server interaction.
$path = 'EnvironmentRuntimeService.cs'
$text = Read-Utf8 $path

$methodStart = $text.IndexOf('    public Task<string> StartTomcatInCatalinaConsoleAsync', [StringComparison]::Ordinal)
$methodEnd = $text.IndexOf('    public NginxRuntimeOptions GetNginxOptions()', $methodStart, [StringComparison]::Ordinal)
if ($methodStart -lt 0 -or $methodEnd -le $methodStart) { throw 'StartTomcatInCatalinaConsoleAsync block not found.' }
$newConsoleMethods = @'
    internal static void LaunchTomcatConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var catalina = Path.Combine(binDirectory, "catalina.bat");
        if (!File.Exists(catalina))
        {
            throw new FileNotFoundException("未找到 Tomcat Catalina 启动脚本。", catalina);
        }

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

'@
$text = $text.Substring(0, $methodStart) + $newConsoleMethods + $text.Substring($methodEnd)

$startMethod = $text.IndexOf('    public async Task<string> StartAsync', [StringComparison]::Ordinal)
$caseStart = $text.IndexOf('            case EnvironmentKind.Tomcat:', $startMethod, [StringComparison]::Ordinal)
$caseEnd = $text.IndexOf('            case EnvironmentKind.Nginx:', $caseStart, [StringComparison]::Ordinal)
if ($startMethod -lt 0 -or $caseStart -lt 0 -or $caseEnd -le $caseStart) { throw 'Tomcat StartAsync case not found.' }
$newTomcatStart = @'
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
                LaunchTomcatConsole(tomcatRoot);

                return $"Tomcat Server CMD 控制台已打开；共享 Tomcat 不再通过 Windows Service 隐藏启动。请在控制台观察 Catalina 输出，日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@
$text = $text.Substring(0, $caseStart) + $newTomcatStart + $text.Substring($caseEnd)
Write-Utf8 $path $text

# 2) New Tomcat installations no longer register/start the hidden Windows service.
$path = 'EnvironmentInstaller.cs'
$text = Read-Utf8 $path
$installMethod = $text.IndexOf('    private async Task InstallTomcatAsync', [StringComparison]::Ordinal)
$serviceBlockStart = $text.IndexOf('        var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;', $installMethod, [StringComparison]::Ordinal)
$nextMethod = $text.IndexOf('    private async Task InstallNginxAsync', $serviceBlockStart, [StringComparison]::Ordinal)
if ($installMethod -lt 0 -or $serviceBlockStart -lt 0 -or $nextMethod -le $serviceBlockStart) { throw 'Tomcat installer service block not found.' }
$newInstallTail = @'
        progress(InstallingProgress(84, "正在清理旧 Tomcat 后台服务并准备可见控制台...", 58));
        TomcatProductStartupManager.RemoveRegistration();
        await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);

        if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
        {
            try { TomcatWindowsServiceManager.Stop(); } catch { }
            try { TomcatWindowsServiceManager.Delete(); } catch { }
        }

        progress(InstallingProgress(92, "正在打开 Tomcat Server CMD 控制台...", 78));
        EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并已在可见 CMD 控制台中启动；后续启动与重启不再通过 Windows Service 隐藏运行。"));
    }

'@
$text = $text.Substring(0, $serviceBlockStart) + $newInstallTail + $text.Substring($nextMethod)
Write-Utf8 $path $text

# 3) Normal Start is now the visible Catalina experience, so remove the duplicate
#    environment-card Catalina button. Use indexes instead of attribute-order regex.
$path = 'Resources/MainWindowTemplates.xaml'
$text = Read-Utf8 $path
$buttonStart = $text.IndexOf('<Button Content="以 Catalina 方式启动"', [StringComparison]::Ordinal)
if ($buttonStart -lt 0) { throw 'Environment Catalina button start not found.' }
$buttonEnd = $text.IndexOf('/>', $buttonStart, [StringComparison]::Ordinal)
if ($buttonEnd -lt 0) { throw 'Environment Catalina button end not found.' }
$text = $text.Remove($buttonStart, $buttonEnd + 2 - $buttonStart)
if ($text.Contains('Tag="CatalinaRun"') -or $text.Contains('以 Catalina 方式启动')) {
    throw 'Redundant environment Catalina button was not removed.'
}
Write-Utf8 $path $text

# 4) Replace old regression contracts that described the retired hidden-service path.
$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatRuntimePathsUseProcessesAndPortsInsteadOfScmRunningState()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var managerStart = services.IndexOf("internal static class TomcatWindowsServiceManager", StringComparison.Ordinal);
        var managerEnd = services.IndexOf("internal static class FrpWindowsServiceManager", managerStart, StringComparison.Ordinal);
        Assert.IsTrue(managerStart >= 0 && managerEnd > managerStart);
        var manager = services.Substring(managerStart, managerEnd - managerStart);
        Assert.IsFalse(manager.Contains("IsRunningForRoot", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("public static bool IsRunning()", StringComparison.Ordinal));
        StringAssert.Contains(manager, "StopWithoutStatusWait");
        StringAssert.Contains(manager, "DeleteWithoutStatusWait");

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(instances.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(instances, "IsSharedTomcatRunning()");
        StringAssert.Contains(instances, "ContainsJavaOptionPath(process.CommandLine, \"-Dcatalina.base\", home)");

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "共享 Tomcat 不再通过 Windows Service 隐藏启动");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));

        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(deployment, "TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot)");
        StringAssert.Contains(deployment, "await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);");
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.TomcatServiceDecoupling137.cs' $test

$test = @'
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
        Assert.IsFalse(runtime.Contains("if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但保存的 root 凭据无法验证", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatRestartCannotBeKilledByLateServiceStopCleanup()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var stopStart = services.IndexOf("protected override void OnStop()", StringComparison.Ordinal);
        var shutdownStart = services.IndexOf("protected override void OnShutdown()", stopStart, StringComparison.Ordinal);
        Assert.IsTrue(stopStart >= 0 && shutdownStart > stopStart);
        var normalStop = services.Substring(stopStart, shutdownStart - stopStart);
        StringAssert.Contains(normalStop, "runtime cleanup delegated to MCPanel controller");
        Assert.IsFalse(normalStop.Contains("StopTomcat", StringComparison.Ordinal));
        Assert.IsFalse(normalStop.Contains("shutdown.bat", StringComparison.Ordinal));

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        StringAssert.Contains(runtime, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("controlAttempt < 3", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "StopSharedTomcatAsync(cancellationToken)");

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        StringAssert.Contains(instances, "public static async Task StopSharedTomcatAsync");
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.RuntimeLifecycle138.cs' $test

$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void CatalinaConsoleLaunchDoesNotRunAutomaticReadinessDiagnostics()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("public Task<string> StartTomcatInCatalinaConsoleAsync", StringComparison.Ordinal);
        var end = runtime.IndexOf("public NginxRuntimeOptions GetNginxOptions()", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = runtime.Substring(start, end - start);

        StringAssert.Contains(method, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(method, "不隐藏启动，也不等待端口或执行 HTTP 就绪诊断");
        Assert.IsFalse(method.Contains("WaitForStartupAsync", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ReadHttpPorts", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("ArePortsListening", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("真正 HTTP 就绪", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("诊断模式", StringComparison.Ordinal));

        var launcherStart = runtime.IndexOf("internal static void LaunchTomcatConsole", StringComparison.Ordinal);
        var launcherEnd = runtime.IndexOf("private static void RetireLegacyTomcatWindowsService", launcherStart, StringComparison.Ordinal);
        Assert.IsTrue(launcherStart >= 0 && launcherEnd > launcherStart);
        var launcher = runtime.Substring(launcherStart, launcherEnd - launcherStart);
        StringAssert.Contains(launcher, "ProcessRunner.StartFile");
        StringAssert.Contains(launcher, "call catalina.bat run");
        StringAssert.Contains(launcher, "ProcessWindowStyle.Normal");
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.CatalinaNoReadiness139.cs' $test

# 5) Dedicated 1.3.43 guards.
$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void SharedTomcatNormalStartUsesVisibleCmdConsole()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("public async Task<string> StartAsync", StringComparison.Ordinal);
        var tomcat = runtime.IndexOf("case EnvironmentKind.Tomcat:", start, StringComparison.Ordinal);
        var nginx = runtime.IndexOf("case EnvironmentKind.Nginx:", tomcat, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && tomcat > start && nginx > tomcat);
        var block = runtime.Substring(tomcat, nginx - tomcat);

        StringAssert.Contains(block, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(block, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(block, "StopAllProductInstancesAsync(cancellationToken)");
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("startup.bat", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("WaitForStartupAsync", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SharedTomcatConsoleRunsCatalinaInVisibleNormalWindow()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = runtime.IndexOf("internal static void LaunchTomcatConsole", StringComparison.Ordinal);
        var end = runtime.IndexOf("private static void RetireLegacyTomcatWindowsService", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var block = runtime.Substring(start, end - start);

        StringAssert.Contains(block, "title MCPanel Tomcat Server");
        StringAssert.Contains(block, "call catalina.bat run");
        StringAssert.Contains(block, "ProcessWindowStyle.Normal");
        StringAssert.Contains(block, "pause >nul");
    }

    [TestMethod]
    public void TomcatInstallerDoesNotRegisterOrStartHiddenWindowsService()
    {
        var installer = ReadRepositoryFile("EnvironmentInstaller.cs");
        var start = installer.IndexOf("private async Task InstallTomcatAsync", StringComparison.Ordinal);
        var end = installer.IndexOf("private async Task InstallNginxAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var block = installer.Substring(start, end - start);

        StringAssert.Contains(block, "EnvironmentRuntimeService.LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(block, "TomcatWindowsServiceManager.Delete()");
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(block.Contains("TomcatWindowsServiceManager.Start()", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EnvironmentCardHasNoDuplicateCatalinaStartButton()
    {
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        Assert.IsFalse(xaml.Contains("Tag=\"CatalinaRun\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("以 Catalina 方式启动", StringComparison.Ordinal));
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.VisibleSharedTomcat143.cs' $test

# 6) Version and release notes.
$path = 'MCPanel.csproj'
$text = Read-Utf8 $path
$text = $text.Replace('<Version>1.3.42</Version>', '<Version>1.3.43</Version>')
$text = $text.Replace('<FileVersion>1.3.42.0</FileVersion>', '<FileVersion>1.3.43.0</FileVersion>')
$text = $text.Replace('<AssemblyVersion>1.3.42.0</AssemblyVersion>', '<AssemblyVersion>1.3.43.0</AssemblyVersion>')
if (-not $text.Contains('<Version>1.3.43</Version>')) { throw 'Version bump failed.' }
Write-Utf8 $path $text

$notes = @'
# MCPanel 1.3.43

- 总/共享 Tomcat Server 的“启动”和“重启”改为可见 CMD 控制台启动，使用 `catalina.bat run`，Catalina 标准输出和异常可直接在窗口中观察。
- 共享 Tomcat 不再通过 MCPanelTomcat Windows Service 隐藏启动；首次启动/重启会尽力停止并删除旧的服务包装器，后续不再由 SCM 在后台拉起共享 Tomcat。
- Tomcat 新安装流程不再注册或启动隐藏 Windows 服务，安装完成后直接打开可见的 Tomcat Server CMD 控制台。
- 删除环境卡片上重复的“以 Catalina 方式启动”按钮；普通“启动”现在就是可见 Catalina 控制台模式。
- 控制台中的 Tomcat 退出后窗口会保留并暂停，便于查看最后的异常和 Catalina 输出；MCPanel 仍不执行 HTTP 就绪诊断。
'@
Write-Utf8 'RELEASE-NOTES.md' $notes

Write-Host 'MCPanel 1.3.43 visible shared Tomcat changes applied.'
