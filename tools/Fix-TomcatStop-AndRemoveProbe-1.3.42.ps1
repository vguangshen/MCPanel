$ErrorActionPreference = 'Stop'

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText((Join-Path $PWD $Path), [Text.UTF8Encoding]::new($false))
}

function Write-Utf8([string]$Path, [string]$Content) {
    [IO.File]::WriteAllText((Join-Path $PWD $Path), $Content, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Utf8 $Path
    if (-not $text.Contains($Old)) {
        throw "Expected source block not found in $Path"
    }
    Write-Utf8 $Path ($text.Replace($Old, $New))
}

# 1) Remove the manual website HTTP probe completely. Runtime state continues to be
# refreshed by the lightweight process/service snapshot, but there is no ad-hoc GET button.
$path = 'MainWindow.Websites.cs'
$text = Read-Utf8 $path
$text = $text.Replace("using System.Net.Http;`r`n", '').Replace("using System.Net.Http;`n", '')
$text = [regex]::Replace($text,
    '(?m)^\s*private static readonly HttpClient WebsiteCheckClient.*\r?\n\s*private readonly HashSet<string> _websiteChecks.*\r?\n',
    '')
$text = [regex]::Replace($text,
    '(?s)\r?\n\s*internal async void ProbeWebsite_Click\(object sender, RoutedEventArgs e\)\s*\{.*?\r?\n\s*\}\s*\r?\n\}',
    "`r`n}")
if ($text.Contains('ProbeWebsite_Click') -or $text.Contains('WebsiteCheckClient') -or $text.Contains('_websiteChecks')) {
    throw 'Website probe implementation was not fully removed.'
}
Write-Utf8 $path $text

$path = 'Resources/MainWindowTemplates.xaml'
$text = Read-Utf8 $path
$before = $text
$text = [regex]::Replace($text,
    '(?m)^\s*<Button Content="检测访问"[^\r\n]*Click="ProbeWebsite_Click"\s*/>\r?\n',
    '')
if ($text -eq $before -or $text.Contains('ProbeWebsite_Click') -or $text.Contains('检测访问')) {
    throw 'Website probe button was not removed from the template.'
}
Write-Utf8 $path $text

# 2) Do not call a listener "Java" when PID ownership could not be resolved.
# The old `Count == 0 || ...` branch was intentionally permissive, but it can turn a
# stale/unrelated listening port into a phantom Tomcat-running state.
Replace-Exact 'TomcatProductInstanceManager.cs' @'
        var processIds = GetListeningProcessIds(new[] { port });
        return processIds.Count == 0 || processIds.Any(IsJavaProcessId);
'@ @'
        var processIds = GetListeningProcessIds(new[] { port });
        // Unknown ownership is not proof of a Java listener. Requiring a positively
        // identified java.exe PID prevents stale/unrelated ports from creating a
        // phantom Tomcat-running state after the JVM has already exited.
        return processIds.Count > 0 && processIds.Any(IsJavaProcessId);
'@

# 3) The Environment card represents the shared Tomcat Server, not independent
# per-product debug/Catalina instances. Independent instances remain visible/manageable
# on the Websites page and must not keep the shared Stop button looking ineffective.
$path = 'EnvironmentRuntimeService.cs'
$text = Read-Utf8 $path
$pattern = '(?s)        var sharedProcessRunning = TomcatProductInstanceManager\.IsSharedTomcatRunning\(\);\s*        var sharedRunning = sharedProcessRunning &&\s*            TomcatRuntimeProbe\.ArePortsListening\(TomcatRuntimeProbe\.ReadHttpPorts\(tomcatRoot\)\);\s*        var managedRunning = TomcatProductInstanceManager\.IsAnyManagedTomcatHealthy\(\);\s*        var anyRunning = sharedRunning \|\| managedRunning;\s*        var detail = sharedRunning\s*            \? "全部应用模式正在运行。"\s*            : managedRunning\s*                \? "一个或多个应用正在单独运行。"\s*                : sharedProcessRunning\s*                    \? "检测到 Tomcat Java 进程，但配置端口未全部监听。"\s*                    : "当前没有运行中的 Tomcat。";\s*        return Installed\(anyRunning, \$"Tomcat 已安装到 \{tomcatRoot\}。\{detail\}"\);'
$replacement = @'
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
'@
$newText = [regex]::Replace($text, $pattern, $replacement, 1)
if ($newText -eq $text) { throw 'GetTomcatState block was not updated.' }
$text = $newText

# 4) Remove the redundant 20-second wait after sending SCM Stop. The Windows service
# wrapper is only lifecycle plumbing; the controller immediately stops the real shared
# CATALINA_BASE and verifies the JVM. This also makes Stop responsive when the wrapper
# is stale but no JVM exists.
$oldStop = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    await Task.Run(() => TomcatWindowsServiceManager.Stop(), cancellationToken);
                    // sc.exe only acknowledges the stop request. Verify the real
                    // shared CATALINA_BASE process, never the SCM status value.
                    for (var attempt = 0; attempt < 80 && TomcatProductInstanceManager.IsSharedTomcatRunning(); attempt++)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                    if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                    {
                        return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
                    }
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    await TomcatProductInstanceManager.StopSharedTomcatAsync(cancellationToken);
                    if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                    {
                        throw new InvalidOperationException("Tomcat Server 共享进程未能停止；单应用实例未受影响。请检查共享 Tomcat Java 进程。");
                    }
                }
                return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
'@
$newStop = @'
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

                return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
'@
if (-not $text.Contains($oldStop)) { throw 'Tomcat StopAsync block was not found.' }
$text = $text.Replace($oldStop, $newStop)
Write-Utf8 $path $text

# 5) Normal SCM Stop must not launch shutdown.bat itself. That duplicated the panel
# controller's shutdown path, could wait inside the service handler, and reintroduced
# the old stop/restart race. OS shutdown still keeps the force-cleanup path.
Replace-Exact 'ManagedComponentWindowsServices.cs' @'
    protected override void OnStop() => StopTomcat(waitForExitAndForce: false);
'@ @'
    protected override void OnStop()
    {
        if (Interlocked.Exchange(ref _stopStarted, 1) != 0)
        {
            return;
        }

        WriteServiceLog("Tomcat Windows service wrapper stopped; runtime cleanup delegated to MCPanel controller.");
    }
'@

# 6) Version + notes.
$path = 'MCPanel.csproj'
$text = Read-Utf8 $path
$text = $text.Replace('<Version>1.3.41</Version>', '<Version>1.3.42</Version>')
$text = $text.Replace('<FileVersion>1.3.41.0</FileVersion>', '<FileVersion>1.3.42.0</FileVersion>')
$text = $text.Replace('<AssemblyVersion>1.3.41.0</AssemblyVersion>', '<AssemblyVersion>1.3.42.0</AssemblyVersion>')
if (-not $text.Contains('<Version>1.3.42</Version>')) { throw 'Version bump failed.' }
Write-Utf8 $path $text

$notes = @'
# MCPanel 1.3.42

- 删除“已安装网站”里的“检测访问”按钮以及对应的临时 HTTP GET 探测代码；网站页继续使用运行时快照刷新服务/进程状态。
- 修复 Tomcat 偶发显示“运行中”但任务管理器已经没有 Java 进程的问题：端口监听只有在能够明确确认监听 PID 为 java.exe 时，才作为 Java 运行证据，未知 PID 不再按 Java 处理。
- “环境 → Tomcat Server”现在只表示共享/总 Tomcat Server 的状态；单应用独立/Catalina 实例继续在“网站”页各自管理，不再让环境页的“停止”按钮看起来无效。
- Tomcat 停止链移除 Windows Service Stop 后最多 20 秒的冗余等待，发送 SCM 停止请求后直接按真实 CATALINA_BASE Java 进程清理并验证。
- Windows Service 的普通 OnStop 不再重复执行 shutdown.bat；运行时停止统一交给 MCPanel 控制器，避免重复 shutdown、长时间无响应和旧停止动作打到新实例的竞态。系统关机路径仍保留强制清理。
'@
Write-Utf8 'RELEASE-NOTES.md' $notes

# 7) Regression guards.
$test = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebsiteManualHttpProbeIsRemoved()
    {
        var code = ReadRepositoryFile("MainWindow.Websites.cs");
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        Assert.IsFalse(code.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("WebsiteCheckClient", StringComparison.Ordinal));
        Assert.IsFalse(code.Contains("_websiteChecks", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("检测访问", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("ProbeWebsite_Click", StringComparison.Ordinal));
    }

    [TestMethod]
    public void UnknownPortOwnerIsNotTreatedAsJava()
    {
        var code = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var start = code.IndexOf("private static bool IsJavaPortListening", StringComparison.Ordinal);
        var end = code.IndexOf("private static async Task<string> PrepareInstanceAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "processIds.Count > 0 && processIds.Any(IsJavaProcessId)");
        Assert.IsFalse(method.Contains("processIds.Count == 0 ||", StringComparison.Ordinal));
    }

    [TestMethod]
    public void EnvironmentTomcatStateTracksSharedServerNotIndependentInstances()
    {
        var code = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = code.IndexOf("private static EnvironmentRuntimeState GetTomcatState", StringComparison.Ordinal);
        var end = code.IndexOf("private static EnvironmentRuntimeState GetSqlServerState", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "var anyRunning = sharedProcessRunning;");
        StringAssert.Contains(method, "一个或多个应用正在单独运行，请在“网站”页面管理");
        Assert.IsFalse(method.Contains("var anyRunning = sharedRunning || managedRunning;", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatStopDoesNotWaitTwentySecondsOnScm()
    {
        var code = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var start = code.IndexOf("public async Task<string> StopAsync", StringComparison.Ordinal);
        var end = code.IndexOf("public async Task<string> RestartAsync", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "TomcatWindowsServiceManager.Stop()");
        StringAssert.Contains(method, "StopSharedTomcatAsync(cancellationToken)");
        Assert.IsFalse(method.Contains("attempt < 80", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("Task.Delay(250", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatServiceNormalStopDoesNotRunShutdownBat()
    {
        var code = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var start = code.IndexOf("protected override void OnStop()", StringComparison.Ordinal);
        var end = code.IndexOf("protected override void OnShutdown()", start, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0 && end > start);
        var method = code.Substring(start, end - start);
        StringAssert.Contains(method, "runtime cleanup delegated to MCPanel controller");
        Assert.IsFalse(method.Contains("StopTomcat", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("shutdown.bat", StringComparison.Ordinal));
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.TomcatStopAndWebsiteProbe142.cs' $test

Write-Host 'MCPanel 1.3.42 Tomcat stop and website probe cleanup applied.'
