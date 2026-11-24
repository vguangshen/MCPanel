using System.IO;

namespace MCPanel;

/// <summary>
/// The panel's diagnostic logs: one format for every log file, size-bounded
/// through <see cref="RollingLogWriter"/>, and never throwing, so writing a
/// diagnostic can never change the outcome of the operation it describes.
/// </summary>
internal static class PanelLog
{
    /// <summary>A log file in StoreData\Work.</summary>
    public static string WorkLog(string fileName) => Path.Combine(ComponentPaths.WorkRoot, fileName);

    /// <summary>
    /// Appends <c>[yyyy-MM-dd HH:mm:ss] message</c>. An exception follows on
    /// the next lines, and its entry ends with a blank line so stack traces
    /// stay readable.
    /// </summary>
    public static void Write(string path, string message, Exception? error = null)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var entry = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}";
            if (error is not null)
            {
                entry += error + Environment.NewLine + Environment.NewLine;
            }

            RollingLogWriter.Append(path, entry);
        }
        catch
        {
            // Diagnostics must never interrupt the operation being logged.
        }
    }
}
