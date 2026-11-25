using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MCPanel;

/// <summary>
/// What the background updater is doing, written beside the installation.
/// MCPanel closes as soon as it hands an update to the updater, so without
/// this a slow or failed update is invisible: a manually started MCPanel
/// just shows the old version. StoreData\Updates\updater.log keeps every
/// stage and failure; updater-status.json says which stage is running.
/// </summary>
internal static class UpdaterProgress
{
    private static readonly TimeSpan StatusLifetime = TimeSpan.FromHours(12);

    internal sealed record Status(string Stage, int ProcessId, DateTime StartedAt, DateTime UpdatedAt);

    internal static string LogPath(string installRoot) =>
        Path.Combine(ComponentPaths.GetUpdatesRoot(installRoot), "updater.log");

    internal static string StatusPath(string installRoot) =>
        Path.Combine(ComponentPaths.GetUpdatesRoot(installRoot), "updater-status.json");

    /// <summary>An installation the updater may write into: an existing folder with MCPanel.exe.</summary>
    internal static bool IsInstallRoot(string? installRoot) =>
        !string.IsNullOrWhiteSpace(installRoot) &&
        Directory.Exists(installRoot) &&
        File.Exists(Path.Combine(installRoot, "MCPanel.exe"));

    /// <summary>Records the stage now running, in the log and the status file.</summary>
    internal static void Report(string? installRoot, string stage)
    {
        if (!IsInstallRoot(installRoot))
        {
            return;
        }

        Log(installRoot, stage);
        try
        {
            var path = StatusPath(installRoot!);
            var now = DateTime.Now;
            var startedAt = TryRead(installRoot!) is { ProcessId: var id } previous && id == CurrentProcess.Id()
                ? previous.StartedAt
                : now;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            AtomicFile.WriteAllText(path, JsonSerializer.Serialize(new Status(stage, CurrentProcess.Id(), startedAt, now)));
        }
        catch
        {
            // Progress reporting must never stop an update.
        }
    }

    /// <summary>Appends a timestamped line to updater.log.</summary>
    internal static void Log(string? installRoot, string message)
    {
        if (!IsInstallRoot(installRoot))
        {
            return;
        }

        try
        {
            var path = LogPath(installRoot!);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}", new UTF8Encoding(false));
        }
        catch
        {
        }
    }

    /// <summary>The updater is done (successfully or not): no stage is running any more.</summary>
    internal static void Finish(string? installRoot)
    {
        if (!IsInstallRoot(installRoot))
        {
            return;
        }

        try { File.Delete(StatusPath(installRoot!)); } catch { }
    }

    internal static Status? TryRead(string installRoot)
    {
        try
        {
            var path = StatusPath(installRoot);
            return File.Exists(path) ? JsonSerializer.Deserialize<Status>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// A notice for a MCPanel started while the updater is still working, or
    /// null. A status left by an updater that is no longer running is removed.
    /// </summary>
    internal static string? DescribeRunningUpdate(string installRoot)
    {
        var status = TryRead(installRoot);
        if (status is null)
        {
            return null;
        }

        if (DateTime.Now - status.UpdatedAt > StatusLifetime || !IsProcessRunning(status.ProcessId))
        {
            Finish(installRoot);
            return null;
        }

        return $"后台更新器仍在运行（{status.Stage}，开始于 {status.StartedAt:HH:mm}）。当前打开的仍是旧版本；" +
               "更新完成后 MCPanel 会自动关闭并以新版本重新启动，请不要重复更新。进度记录：StoreData\\Updates\\updater.log";
    }

    private static bool IsProcessRunning(int processId)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static class CurrentProcess
    {
        internal static int Id()
        {
            using var current = Process.GetCurrentProcess();
            return current.Id;
        }
    }
}
