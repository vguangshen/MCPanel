using System.IO;
using System.Text;
using System.Threading;

namespace MCPanel;

internal static class AtomicFile
{
    private static readonly object WriteLock = new();

    public static void WriteAllText(string path, string contents, Encoding? encoding = null)
    {
        encoding ??= new UTF8Encoding(false);
        WriteAllBytes(path, encoding.GetPreamble().Concat(encoding.GetBytes(contents)).ToArray());
    }

    public static void WriteAllBytes(string path, byte[] contents)
    {
        lock (WriteLock)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            // Flush to disk before the rename: otherwise a power loss right after
            // File.Replace can leave an empty or truncated settings file.
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(contents, 0, contents.Length);
                stream.Flush(flushToDisk: true);
            }
            try
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        if (File.Exists(path))
                        {
                            File.Replace(temporary, path, null);
                        }
                        else
                        {
                            File.Move(temporary, path);
                        }
                        break;
                    }
                    // Antivirus and indexers briefly hold new files open, which
                    // File.Replace reports as either exception type.
                    catch (Exception error) when (
                        attempt < 5 && (error is IOException || error is UnauthorizedAccessException))
                    {
                        Thread.Sleep(40 * (attempt + 1));
                    }
                }
            }
            finally
            {
                try
                {
                    if (File.Exists(temporary)) File.Delete(temporary);
                }
                catch
                {
                }
            }
        }
    }
}
