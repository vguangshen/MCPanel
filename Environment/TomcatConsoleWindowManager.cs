using System.Diagnostics;
using System.Management;
using System.IO;

namespace MCPanel;

/// <summary>
/// Owns the visible CMD wrapper used by the shared Tomcat Catalina console.
/// It intentionally targets only MCPanel's run-tomcat-server.cmd launcher (or
/// its exact console title) so unrelated command prompts are never touched.
/// </summary>
internal static class TomcatConsoleWindowManager
{
    internal const string ConsoleTitle = "MCPanel Tomcat Server";

    internal static string LauncherPath =>
        Path.Combine(ComponentPaths.WorkRoot, "run-tomcat-server.cmd");

    internal static bool IsManagedConsole(
        string? commandLine,
        string? windowTitle,
        string launcherPath)
    {
        var fullLauncher = Path.GetFullPath(launcherPath);
        var commandMatches = !string.IsNullOrWhiteSpace(commandLine) &&
                             commandLine!.IndexOf(fullLauncher, StringComparison.OrdinalIgnoreCase) >= 0;
        var titleMatches = string.Equals(
            windowTitle?.Trim(),
            ConsoleTitle,
            StringComparison.OrdinalIgnoreCase);
        return commandMatches || titleMatches;
    }

    public static void CloseExistingConsoleWindows()
    {
        var launcher = LauncherPath;
        foreach (var process in Process.GetProcessesByName("cmd"))
        {
            using (process)
            {
                string? title = null;
                try
                {
                    process.Refresh();
                    title = process.MainWindowTitle;
                }
                catch
                {
                }

                var commandLine = TryReadCommandLine(process.Id);
                if (!IsManagedConsole(commandLine, title, launcher))
                {
                    continue;
                }

                CloseProcess(process);
            }
        }
    }

    private static string? TryReadCommandLine(int processId)
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                $"SELECT CommandLine FROM Win32_Process WHERE ProcessId = {processId}");
            using var results = searcher.Get();
            foreach (ManagementObject item in results)
            {
                using (item)
                {
                    return item["CommandLine"]?.ToString();
                }
            }
        }
        catch
        {
            // WMI command-line access can be restricted. The exact console
            // title remains a compatibility fallback for those machines.
        }

        return null;
    }

    private static void CloseProcess(Process process)
    {
        try
        {
            if (process.HasExited)
            {
                return;
            }

            if (process.CloseMainWindow() && process.WaitForExit(1200))
            {
                return;
            }
        }
        catch
        {
        }

        try
        {
            process.Refresh();
            if (!process.HasExited)
            {
                process.Kill();
                process.WaitForExit(1200);
            }
        }
        catch
        {
            // Console cleanup is best effort. The Tomcat runtime has already
            // been stopped before this path is used by Stop/Restart.
        }
    }
}