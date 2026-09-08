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
            File.WriteAllBytes(temporary, contents);
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
                    catch (IOException) when (attempt < 5)
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
