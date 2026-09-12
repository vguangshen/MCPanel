using System.Windows;

namespace MCPanel;

public partial class App
{
    private void App_StartupRegistration(object sender, StartupEventArgs e)
    {
        if (IsNonInteractiveLaunch(e.Args))
        {
            return;
        }

        try
        {
            StartupRegistrationBootstrapper.EnsureDefaultRegistration();
        }
        catch (Exception ex)
        {
            // Startup registration must never block MCPanel from opening.
            WriteLifecycleError("注册开机自启动失败", ex);
        }
    }

    internal static bool IsNonInteractiveLaunch(string[] arguments)
    {
        if (NginxWindowsServiceHost.IsServiceRequest(arguments) ||
            TomcatWindowsServiceHost.IsServiceRequest(arguments) ||
            FrpWindowsServiceHost.IsServiceRequest(arguments) ||
            EnvironmentInstallWorker.IsWorkerRequest(arguments) ||
            ProductInstallWorker.IsWorkerRequest(arguments) ||
            TomcatProductStartupManager.IsRestoreRequest(arguments))
        {
            return true;
        }

        return arguments.Any(argument =>
            string.Equals(argument, "--wait-update-plan", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, "--apply-update", StringComparison.OrdinalIgnoreCase));
    }
}
