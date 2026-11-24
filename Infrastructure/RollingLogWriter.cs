using System.IO;
using System.Text;

namespace MCPanel;

/// <summary>
/// Keeps MCPanel-owned diagnostic files bounded without interrupting the user
/// operation that produced the diagnostic entry.
/// </summary>
internal static class RollingLogWriter
{
    private const long MaximumLogBytes = 2L * 1024 * 1024;
    private const int MaximumBackups = 2;
    private static readonly object Sync = new();

    public static void Append(string path, string text, Encoding? encoding = null)
    {
        if (string.IsNullOrWhiteSpace(path) || string.IsNullOrEmpty(text))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrWhiteSpace(directory))
                {
                    Directory.CreateDirectory(directory);
                }

                var selectedEncoding = encoding ?? new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
                RotateIfNeeded(path, selectedEncoding.GetByteCount(text));
                using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, selectedEncoding);
                writer.Write(text);
            }
        }
        catch
        {
            // Diagnostics must never make the original product operation fail.
        }
    }

    public static void EnsureCapacity(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            lock (Sync)
            {
                RotateIfNeeded(path, 0);
            }
        }
        catch
        {
            // A locked diagnostic file can be rotated on the next operation.
        }
    }

    private static void RotateIfNeeded(string path, long incomingBytes)
    {
        if (!File.Exists(path))
        {
            return;
        }

        var currentLength = new FileInfo(path).Length;
        if (currentLength <= 0 || currentLength + incomingBytes <= MaximumLogBytes)
        {
            return;
        }

        var oldest = path + "." + MaximumBackups;
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = MaximumBackups - 1; index >= 1; index--)
        {
            var source = path + "." + index;
            var destination = path + "." + (index + 1);
            if (File.Exists(source))
            {
                File.Move(source, destination);
            }
        }

        File.Move(path, path + ".1");
    }
}
