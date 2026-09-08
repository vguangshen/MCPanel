using System.IO;

namespace MCPanel;

/// <summary>Byte-exact snapshots of all files participating in a configuration commit.</summary>
internal sealed class ConfigurationFileTransaction
{
    private readonly Dictionary<string, byte[]?> _files = new(StringComparer.OrdinalIgnoreCase);

    internal void Capture(string path)
    {
        path = Path.GetFullPath(path);
        if (!_files.ContainsKey(path)) _files.Add(path, File.Exists(path) ? File.ReadAllBytes(path) : null);
    }

    internal void Rollback()
    {
        var errors = new List<Exception>();
        foreach (var file in _files)
        {
            try
            {
                if (file.Value is null) { if (File.Exists(file.Key)) File.Delete(file.Key); }
                else AtomicFile.WriteAllBytes(file.Key, file.Value);
            }
            catch (Exception error) { errors.Add(new IOException($"无法恢复配置文件：{file.Key}", error)); }
        }
        if (errors.Count > 0) throw new AggregateException("配置回滚未完成。", errors);
    }
}
