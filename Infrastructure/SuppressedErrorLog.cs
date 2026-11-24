using System.Collections.Concurrent;
using System.IO;

namespace MCPanel;

/// <summary>
/// One-line record of an error the panel deliberately recovers from (an
/// unreadable state file, a damaged generated config) so the recovery is not
/// silent. Writing never throws, and the same error from the same place is
/// recorded at most once per <see cref="RepeatInterval"/> because most callers
/// run on every UI refresh.
/// </summary>
internal static class SuppressedErrorLog
{
    internal static readonly TimeSpan RepeatInterval = TimeSpan.FromMinutes(10);
    private const int MaximumTrackedEntries = 512;
    private static readonly ConcurrentDictionary<string, DateTime> LastWritten = new(StringComparer.Ordinal);

    internal static string LogPath => Path.Combine(ComponentPaths.WorkRoot, "suppressed-errors.log");

    /// <param name="area">What was being done, for example "读取 IIS 部署记录".</param>
    /// <param name="error">The recovered error.</param>
    /// <param name="subject">The file or object involved, if any.</param>
    public static void Record(string area, Exception error, string? subject = null)
    {
        try
        {
            var message = error.GetType().Name + ": " + Flatten(error.Message);
            var key = area + "|" + subject + "|" + message;
            var now = DateTime.UtcNow;
            if (LastWritten.TryGetValue(key, out var last) && now - last < RepeatInterval)
            {
                return;
            }

            if (LastWritten.Count >= MaximumTrackedEntries)
            {
                LastWritten.Clear();
            }

            LastWritten[key] = now;
            var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {area}" +
                       (string.IsNullOrWhiteSpace(subject) ? string.Empty : "：" + subject) +
                       " — " + message + Environment.NewLine;
            RollingLogWriter.Append(LogPath, line);
        }
        catch
        {
            // Diagnostics must never change the outcome of the recovered operation.
        }
    }

    private static string Flatten(string value) =>
        (value ?? string.Empty).Replace("\r", " ").Replace("\n", " ").Trim();

    internal static void ResetForTest() => LastWritten.Clear();
}
