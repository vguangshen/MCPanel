using System;
using MarchCenter.AccountApi;

namespace MCPanel;

/// <summary>
/// Owns the embedded Account API listener for the lifetime of MCPanel.
/// Multiple UI/tray entry points share this single host, so disposing a
/// management page cannot accidentally stop the API.
/// </summary>
internal static class EmbeddedAccountApiRuntime
{
    private static readonly object Gate = new();
    private static AccountApiHost? _host;

    public static bool IsRunning
    {
        get
        {
            lock (Gate)
            {
                return _host is not null && _host.IsRunning;
            }
        }
    }

    public static void Start(string configPath)
    {
        if (string.IsNullOrWhiteSpace(configPath))
        {
            throw new InvalidOperationException("Account API 配置文件路径为空。");
        }

        lock (Gate)
        {
            if (_host is not null)
            {
                if (_host.IsRunning)
                {
                    return;
                }

                // A faulted accept loop must not leave a stale host blocking
                // recovery. Dispose it while still holding the global gate.
                try
                {
                    _host.Dispose();
                }
                finally
                {
                    _host = null;
                }
            }

            var options = IniConfiguration.LoadOptions(configPath);
            var identity = DeviceIdentityStore.LoadOrCreate();
            try
            {
                RemoteConfigurationStore.ApplyPersisted(options, identity);
            }
            catch (Exception error)
            {
                // A stale/corrupt encrypted database profile must not make
                // the listener disappear. The API stays online and reports
                // the affected provider as unconfigured until the web panel
                // sends a valid profile again.
                ServiceLog.Write("警告", "load_persisted_configuration", error.Message);
            }

            var host = new AccountApiHost(options);
            try
            {
                host.Start();
                _host = host;
            }
            catch
            {
                host.Dispose();
                throw;
            }
        }
    }

    public static void Stop()
    {
        lock (Gate)
        {
            if (_host is null)
            {
                return;
            }

            // Keep the global gate until HttpListener.Stop/Close and the accept
            // loop shutdown are complete. Start() therefore cannot observe a
            // false "not running" state while the old listener still owns 8088.
            try
            {
                _host.Dispose();
            }
            finally
            {
                _host = null;
            }
        }
    }
}
