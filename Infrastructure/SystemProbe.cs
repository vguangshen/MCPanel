using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace MCPanel;

/// <summary>Shared read-only probes of the local machine.</summary>
internal static class SystemProbe
{
    /// <summary>
    /// Ports with an active TCP listener. When the table is unavailable this
    /// returns an empty set, or throws when <paramref name="throwOnError"/> is
    /// set (port selection must not mistake "unknown" for "free").
    /// </summary>
    public static HashSet<int> GetActiveTcpPorts(bool throwOnError = false)
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch when (!throwOnError)
        {
            // A probe: callers treat "unknown" as "no listeners seen".
            return new HashSet<int>();
        }
    }

    /// <summary>True when something listens on <paramref name="port"/>; false if unknown.</summary>
    public static bool IsTcpPortListening(int port) => GetActiveTcpPorts().Contains(port);

    /// <summary>
    /// True when a new service could listen on <paramref name="port"/>: no
    /// active listener is reported and an exclusive bind on all addresses
    /// succeeds. Either check alone misses cases (a listener on one address
    /// only, or a port reserved without a listener).
    /// </summary>
    public static bool IsPortAvailable(int port)
    {
        if (IsTcpPortListening(port))
        {
            return false;
        }

        try
        {
            var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
            try
            {
                listener.ExclusiveAddressUse = true;
                listener.Start();
                return true;
            }
            finally
            {
                listener.Stop();
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>True when the Service Control Manager knows a service by this name.</summary>
    public static bool WindowsServiceExists(string serviceName)
    {
        try
        {
            return ProcessRunner.RunSynchronously(
                "sc.exe",
                $"query {Compat.QuoteCommandLineArgument(serviceName)}",
                ComponentPaths.ApplicationRoot,
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3)).ExitCode == 0;
        }
        catch
        {
            // A probe: an unreachable SCM means "not known".
            return false;
        }
    }

    /// <summary>
    /// Registry views to search, native first: 64-bit, 32-bit, then the
    /// process default. Each view is returned once.
    /// </summary>
    public static IEnumerable<RegistryView> RegistryViewsWithDefault()
    {
        var seen = new HashSet<RegistryView>();
        if (Environment.Is64BitOperatingSystem && seen.Add(RegistryView.Registry64))
        {
            yield return RegistryView.Registry64;
        }

        if (seen.Add(RegistryView.Registry32))
        {
            yield return RegistryView.Registry32;
        }

        if (seen.Add(RegistryView.Default))
        {
            yield return RegistryView.Default;
        }
    }
}
