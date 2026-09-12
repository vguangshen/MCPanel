$ErrorActionPreference = 'Stop'

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText((Join-Path $PWD $Path), [Text.UTF8Encoding]::new($false))
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText((Join-Path $PWD $Path), $Text, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Utf8 $Path
    if (-not $text.Contains($Old)) {
        throw "Expected block was not found in $Path.`n--- expected ---`n$Old"
    }
    Write-Utf8 $Path ($text.Replace($Old, $New))
}

Replace-Exact 'App.xaml.cs' @'
            // The embedded API follows the MCPanel process. The Windows
            // startup toggle only decides whether MCPanel itself is launched;
            // the Account API page's enable switch decides whether the API is
            // started inside that process.
            _ = EnsureAccountApiAtStartup();
'@ @'
            // The original ITMCStore-style login path restores the shared web
            // runtime in the background. Keep ordinary/manual Tomcat Start and
            // Restart interactive (visible Catalina console); only the --tray
            // Windows-logon path performs this silent shared-server restore.
            if (startInTray)
            {
                _ = TomcatLogonStartup.TryRestoreSharedTomcatAsync();
            }

            // The embedded API follows the MCPanel process. The Windows
            // startup toggle only decides whether MCPanel itself is launched;
            // the Account API page's enable switch decides whether the API is
            // started inside that process.
            _ = EnsureAccountApiAtStartup();
'@

$startupSource = @'
using System.Diagnostics;
using System.IO;

namespace MCPanel;

/// <summary>
/// Restores only the shared Tomcat server when MCPanel is launched by the
/// Windows logon Run entry (--tray). Manual Tomcat Start/Restart deliberately
/// stays on the visible Catalina-console path.
/// </summary>
internal static class TomcatLogonStartup
{
    internal static readonly TimeSpan InitialDelay = TimeSpan.FromSeconds(8);
    internal static readonly TimeSpan StartupProbeTimeout = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(8);

    internal static bool ShouldRestore(bool trayStartup, bool tomcatInstalled, bool sharedTomcatRunning) =>
        trayStartup && tomcatInstalled && !sharedTomcatRunning;

    public static async Task TryRestoreSharedTomcatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // Windows logon is noisy: let profile startup, disk and Java settle
            // before touching Tomcat. This work is fire-and-forget from App and
            // never blocks the tray icon or the interactive desktop.
            await Task.Delay(InitialDelay, cancellationToken);

            var tomcatRoot = new ComponentLocator().FindTomcatRoot();
            var installed = !string.IsNullOrWhiteSpace(tomcatRoot) &&
                            Directory.Exists(tomcatRoot) &&
                            File.Exists(Path.Combine(tomcatRoot, "bin", "catalina.bat"));
            var alreadyRunning = installed && TomcatProductInstanceManager.IsSharedTomcatRunning();
            if (!ShouldRestore(trayStartup: true, installed, alreadyRunning))
            {
                if (alreadyRunning)
                {
                    TomcatProductStartupManager.WriteLog("Windows 登录恢复：共享 Tomcat 已在运行，无需重复启动。", null);
                }
                return;
            }

            var root = Path.GetFullPath(tomcatRoot!);
            EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
            TomcatProductStartupManager.WriteLog(
                $"Windows 登录恢复：正在后台启动共享 Tomcat，CATALINA_BASE={root}",
                null);

            StartHiddenCatalina(root);
            if (await WaitForSharedTomcatAsync(root, StartupProbeTimeout, cancellationToken))
            {
                TomcatProductStartupManager.WriteLog("Windows 登录恢复：共享 Tomcat 已成功启动。", null);
                return;
            }

            // Retry only when no shared runtime can be observed. Never spawn a
            // second Java process merely because an application/connector is slow.
            if (TomcatProductInstanceManager.IsSharedTomcatRunning())
            {
                TomcatProductStartupManager.WriteLog(
                    "Windows 登录恢复：已检测到共享 Tomcat Java 进程，应用仍可能处于初始化阶段。",
                    null);
                return;
            }

            await Task.Delay(RetryDelay, cancellationToken);
            if (TomcatProductInstanceManager.IsSharedTomcatRunning())
            {
                TomcatProductStartupManager.WriteLog("Windows 登录恢复：共享 Tomcat 已在延迟检查期间启动。", null);
                return;
            }

            TomcatProductStartupManager.WriteLog("Windows 登录恢复：首次启动未检测到运行实例，执行一次后台重试。", null);
            StartHiddenCatalina(root);
            if (await WaitForSharedTomcatAsync(root, StartupProbeTimeout, cancellationToken))
            {
                TomcatProductStartupManager.WriteLog("Windows 登录恢复：共享 Tomcat 重试启动成功。", null);
                return;
            }

            TomcatProductStartupManager.WriteLog(
                "Windows 登录恢复：未能检测到共享 Tomcat。MCPanel 保持托盘运行；请查看 Tomcat 日志或手动启动可见 Catalina 控制台。",
                null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Logon restoration is best-effort and must never block MCPanel or
            // surface a modal error while Windows is signing in.
            TomcatProductStartupManager.WriteLog("Windows 登录恢复共享 Tomcat 失败。", ex);
        }
    }

    internal static void StartHiddenCatalina(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var bin = Path.Combine(root, "bin");
        var catalina = Path.Combine(bin, "catalina.bat");
        if (!File.Exists(catalina))
        {
            throw new FileNotFoundException("未找到 Tomcat catalina.bat。", catalina);
        }

        // `catalina.bat run` keeps the server attached to one hidden cmd host,
        // avoiding the extra visible console that `startup.bat`/`catalina start`
        // can create in an interactive logon session. Disposing Process here only
        // releases our handle; it does not terminate the detached runtime.
        using var process = ProcessRunner.StartFile(
            catalina,
            "run",
            bin,
            elevated: false,
            captureOutput: false,
            windowStyle: ProcessWindowStyle.Hidden);
    }

    private static async Task<bool> WaitForSharedTomcatAsync(
        string tomcatRoot,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var ports = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (TomcatProductInstanceManager.IsSharedTomcatRunning() ||
                (ports.Length > 0 && TomcatRuntimeProbe.ArePortsListening(ports)))
            {
                return true;
            }

            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        return TomcatProductInstanceManager.IsSharedTomcatRunning() ||
               (ports.Length > 0 && TomcatRuntimeProbe.ArePortsListening(ports));
    }
}
'@
Write-Utf8 'TomcatLogonStartup.cs' $startupSource

$testSource = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatLogonStartup147_OnlyRestoresInstalledStoppedServerFromTrayStartup()
    {
        Assert.IsTrue(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: false, tomcatInstalled: true, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: false, sharedTomcatRunning: false));
        Assert.IsFalse(TomcatLogonStartup.ShouldRestore(trayStartup: true, tomcatInstalled: true, sharedTomcatRunning: true));
    }

    [TestMethod]
    public void TomcatLogonStartup147_AppHooksOnlyTrayStartupAndKeepsManualCatalinaPath()
    {
        var app = ReadRepositoryFile("App.xaml.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var startup = ReadRepositoryFile("TomcatLogonStartup.cs");

        StringAssert.Contains(app, "if (startInTray)");
        StringAssert.Contains(app, "TomcatLogonStartup.TryRestoreSharedTomcatAsync()");
        StringAssert.Contains(startup, "ProcessWindowStyle.Hidden");
        StringAssert.Contains(startup, "\"run\"");
        Assert.IsFalse(startup.Contains("TomcatWindowsServiceManager.EnsureRegistered", StringComparison.Ordinal));
        Assert.IsFalse(startup.Contains("StartProduct", StringComparison.OrdinalIgnoreCase));

        // Manual environment actions must remain visible and must not be silently
        // redirected to the logon-only startup path.
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        StringAssert.Contains(runtime, "windowStyle: ProcessWindowStyle.Normal");
    }

    [TestMethod]
    public void TomcatLogonStartup147_DoesNotReviveLegacyPerProductRunRegistration()
    {
        var legacy = ReadRepositoryFile("TomcatProductStartupManager.cs");
        StringAssert.Contains(legacy, "RemoveRegistration();");
        StringAssert.Contains(legacy, "已跳过自动启动并清理启动项");
    }
}
'@
Write-Utf8 'MCPanel.Tests\ReliabilityTests.TomcatLogonStartup147.cs' $testSource

Replace-Exact 'MCPanel.csproj' '<Version>1.3.46</Version>' '<Version>1.3.47</Version>'
Replace-Exact 'MCPanel.csproj' '<FileVersion>1.3.46.0</FileVersion>' '<FileVersion>1.3.47.0</FileVersion>'
Replace-Exact 'MCPanel.csproj' '<AssemblyVersion>1.3.46.0</AssemblyVersion>' '<AssemblyVersion>1.3.47.0</AssemblyVersion>'

$notes = @'
# MCPanel 1.3.47

- 恢复原版式的登录后后台运行体验：当 MCPanel 由 Windows 开机启动项以 `--tray` 模式启动时，会自动尝试恢复共享 Tomcat Server。
- 开机恢复使用隐藏的 `catalina.bat run` 进程，不弹出 CMD 窗口；首次检查前延迟 8 秒，并在完全未检测到共享运行实例时执行一次受控重试。
- 手动“启动 / 重启 Tomcat”仍保持 1.3.43 以来的可见 Catalina CMD 控制台，不改回 Windows Service 隐藏运行；专用“以 Catalina 方式启动”入口保持不变。
- 只自动恢复共享 Tomcat，不恢复历史 `MCPanelTomcatProducts` 单产品登录启动项，也不会自动启动独立 Java 产品实例。
- 开机恢复失败不会阻塞 Windows 登录或 MCPanel 托盘；详细状态写入 `tomcat-autostart.log` 供诊断。
'@
Write-Utf8 'RELEASE-NOTES.md' $notes
