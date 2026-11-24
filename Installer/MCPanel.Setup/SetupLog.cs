using System;
using System.IO;
using System.Text;

namespace MCPanel.Setup
{
    /// <summary>Plain UTF-8 log; /LOG=path chooses the file, otherwise %TEMP%.</summary>
    internal sealed class SetupLog
    {
        private readonly object _gate = new object();

        public SetupLog(string path)
        {
            Path = string.IsNullOrWhiteSpace(path)
                ? System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MCPanel-Setup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".log")
                : System.IO.Path.GetFullPath(path);
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path));
            }
            catch
            {
            }
        }

        public string Path { get; }

        public void Write(string message)
        {
            lock (_gate)
            {
                try
                {
                    File.AppendAllText(Path, DateTime.Now.ToString("HH:mm:ss.fff") + "  " + message + Environment.NewLine, new UTF8Encoding(false));
                }
                catch
                {
                    // Logging never blocks installation.
                }
            }
        }
    }
}
