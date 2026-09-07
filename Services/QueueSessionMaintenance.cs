using System.IO;

namespace MCPanel;

internal static class QueueSessionMaintenance
{
    // Only marker-only sessions are disposable. Payloads and progress files
    // remain available for recovery even when their owner has exited.
    internal static void PruneInactiveSessions(string currentSession)
    {
        using var queueLock = ProductInstallWorker.AcquireQueueMutex(currentSession, TimeSpan.Zero);
        if (queueLock is null) return;
        try
        {
            var root = Directory.GetParent(Path.GetFullPath(currentSession))!.FullName;
            foreach (var directory in Directory.EnumerateDirectories(root))
            {
                try
                {
                    if (string.Equals(directory, currentSession, StringComparison.OrdinalIgnoreCase) ||
                        !Guid.TryParseExact(Path.GetFileName(directory), "N", out _) ||
                        (File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
                    var stop = Path.Combine(directory, "stop.flag");
                    var heartbeat = Path.Combine(directory, "heartbeat.flag");
                    var cutoff = DateTime.UtcNow.AddDays(-1);
                    if (!File.Exists(stop) || !File.Exists(heartbeat) ||
                        File.GetLastWriteTimeUtc(stop) > cutoff || File.GetLastWriteTimeUtc(heartbeat) > cutoff) continue;
                    var entries = Directory.GetFileSystemEntries(directory);
                    if (entries.Any(path => !string.Equals(path, stop, StringComparison.OrdinalIgnoreCase) &&
                                            !string.Equals(path, heartbeat, StringComparison.OrdinalIgnoreCase))) continue;
                    File.Delete(stop);
                    File.Delete(heartbeat);
                    Directory.Delete(directory, false);
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { queueLock.ReleaseMutex(); }
    }
}
