using System.Net.NetworkInformation;
using Microsoft.Win32;

namespace MCPanel;

/// <summary>Shared read-only probes of the local machine.</summary>
internal static class SystemProbe
{
    /// <summary>Ports with an active TCP listener; empty when the table is unavailable.</summary>
    public static HashSet<int> GetActiveTcpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            // A probe: callers treat "unknown" as "no listeners seen".
            return new HashSet<int>();
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
