using System.IO;

namespace MCPanel;

/// <summary>
/// Restores only the shared Tomcat server when MCPanel is launched by the
/// Windows logon Run entry (--tray). Logon and normal manual startup intentionally
/// use the same visible standard-start launcher; Catalina run stays diagnostic-only.
/// </summary>
internal static class TomcatLogonStartup
{
    internal static bool ShouldRestore(bool trayStartup, bool tomcatInstalled, bool sharedTomcatRunning) =>
        trayStartup && tomcatInstalled && !sharedTomcatRunning;

    public static Task TryRestoreSharedTomcatAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            var tomcatRoot = new ComponentLocator().FindTomcatRoot();
            var installed = !string.IsNullOrWhiteSpace(tomcatRoot) &&
                            Directory.Exists(tomcatRoot) &&
                            File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat"));
            var alreadyRunning = installed && TomcatProductInstanceManager.IsSharedTomcatRunning();
            if (!ShouldRestore(trayStartup: true, installed, alreadyRunning))
            {
                if (alreadyRunning)
                {
                    TomcatProductStartupManager.WriteLog("Windows 登录恢复：共享 Tomcat 已在运行，无需重复启动。", null);
                }
                return Task.CompletedTask;
            }

            var root = Path.GetFullPath(tomcatRoot!);
            TomcatProductStartupManager.RemoveRegistration();
            TomcatProductStartupManager.WriteLog(
                $"Windows 登录恢复：立即以标准 start 模式启动共享 Tomcat，CATALINA_BASE={root}",
                null);

            // Use exactly the same standard-start launcher as normal manual Tomcat
            // startup. Do not delay, hide the console, wait for readiness or retry
            // in a second background process. Explicit Catalina run remains a
            // user-invoked diagnostic action only.
            EnvironmentRuntimeService.LaunchTomcatStartConsole(root);
            TomcatProductStartupManager.WriteLog("Windows 登录恢复：已按标准 start 模式打开共享 Tomcat CMD 控制台。", null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            // Logon restoration is best-effort and must never block MCPanel.
            TomcatProductStartupManager.WriteLog("Windows 登录恢复共享 Tomcat 失败。", ex);
        }

        return Task.CompletedTask;
    }
}
