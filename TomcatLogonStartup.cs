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