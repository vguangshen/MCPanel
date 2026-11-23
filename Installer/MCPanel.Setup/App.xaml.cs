using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

namespace MCPanel.Setup
{
    public partial class App : Application
    {
        internal const int ExitSuccess = 0;
        internal const int ExitFailure = 1;
        internal const int ExitCancelled = 2;

        private Mutex _instance;
        private SetupLog _log;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            var options = SetupOptions.Parse(e.Args);
            var log = _log = new SetupLog(options.LogFile);
            log.Write("MCPanel-Setup " + typeof(App).Assembly.GetName().Version + " " + string.Join(" ", e.Args));

            if (options.Mode == SetupMode.RenderPreview)
            {
                new MainWindow(null, options, log, previewDirectory: options.PreviewDirectory).Show();
                return;
            }

            bool created;
            _instance = new Mutex(true, "MCPanel.Setup.v1", out created);
            if (!created)
            {
                log.Write("Another setup instance is running.");
                if (!options.Unattended)
                {
                    MessageBox.Show("MCPanel 安装程序已经在运行。", "MCPanel", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                Shutdown(ExitFailure);
                return;
            }

            SetupConfig config;
            try
            {
                config = SetupConfig.LoadEmbedded();
            }
            catch (Exception error)
            {
                log.Write(error.ToString());
                if (!options.Unattended)
                {
                    MessageBox.Show(error.Message, "MCPanel", MessageBoxButton.OK, MessageBoxImage.Error);
                }

                Shutdown(ExitFailure);
                return;
            }

            if (options.VerySilent)
            {
                // Headless: release automation, CI and Windows Settings quiet uninstall.
                Task.Run(() => options.Mode == SetupMode.Uninstall
                        ? RunUninstallHeadless(options, log)
                        : RunInstallHeadless(config, options, log))
                    .ContinueWith(task => Dispatcher.Invoke(() => Shutdown(task.Result)));
                return;
            }

            new MainWindow(config, options, log).Show();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (UninstallRunner.CleanupPending && _log != null)
            {
                UninstallRunner.ScheduleMaintenanceCleanup(_log);
            }

            try { _instance?.ReleaseMutex(); } catch { }
            _instance?.Dispose();
            base.OnExit(e);
        }

        private static int RunInstallHeadless(SetupConfig config, SetupOptions options, SetupLog log)
        {
            try
            {
                var engine = new InstallEngine(config, log, progress => { });
                var directory = string.IsNullOrWhiteSpace(options.Directory) ? InstallEngine.DefaultInstallDirectory() : options.Directory;
                var result = engine.Install(directory, options.PackageFile, createDesktopShortcut: true, cancellation: CancellationToken.None);
                foreach (var warning in result.Warnings)
                {
                    log.Write("Warning: " + warning);
                }

                return result.RestartRequired && options.RestartExitCode > 0 ? options.RestartExitCode : ExitSuccess;
            }
            catch (Exception error)
            {
                log.Write("Failed: " + error.Message);
                return ExitFailure;
            }
        }

        private static int RunUninstallHeadless(SetupOptions options, SetupLog log)
        {
            try
            {
                var root = UninstallRunner.ResolveRoot(options.UninstallRoot);
                new UninstallRunner(log, status => { }).Run(root);
                return ExitSuccess;
            }
            catch (Exception error)
            {
                log.Write("Uninstall failed: " + error.Message);
                return ExitFailure;
            }
        }
    }
}
