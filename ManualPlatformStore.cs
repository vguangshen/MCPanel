using System.IO;
using System.Text.Json;

namespace MCPanel;

internal sealed record ManualPlatformDefinition(string Id, string Name, string Path, bool Java)
{
    public ProductItem ToProduct() => new(Id, Name, "", "/Assets/defaultimg.png", ProductSource.Online)
    {
        ExternalInstallPath = Path,
        RunEnvironment = Java ? "Tomcat" : "IIS",
        DevLanguage = Java ? "Java" : ".NET",
        StatusText = "本地手动绑定平台。",
        IsInstalled = true
    };
}

internal sealed class ManualPlatformStore
{
    private readonly string _file;
    private static readonly object Gate = new();
    internal ManualPlatformStore(string? file = null) => _file = file ?? System.IO.Path.Combine(ComponentPaths.ProductStateRoot, "manual-platforms.json");
    internal List<ManualPlatformDefinition> Load()
    {
        lock (Gate)
        {
            if (!File.Exists(_file)) return [];
            try
            {
                return JsonSerializer.Deserialize<List<ManualPlatformDefinition>>(File.ReadAllText(_file)) ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
            catch (IOException)
            {
                return [];
            }
            catch (UnauthorizedAccessException)
            {
                return [];
            }
        }
    }
    internal ManualPlatformDefinition Prepare(string name, string directory, bool java)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) throw new ArgumentException("请输入 1–100 个字符的平台名称。");
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Path.IsPathRooted(directory)) throw new ArgumentException("请选择有效的完整软件目录。");
        var path = NormalizeFullPath(directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选软件目录不存在。");
        if (!Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidDataException("所选软件目录为空。");
        if (java && ProductDeploymentService.FindTomcatDocBase(path) is null) throw new InvalidDataException("Java 软件目录中需包含 WAR 文件或 WEB-INF 目录。");
        var existing = Load().FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existing.Java != java) throw new InvalidOperationException("此目录已绑定到另一种平台，请先解除原绑定。");
        return new ManualPlatformDefinition(existing?.Id ?? "local_" + Guid.NewGuid().ToString("N"), name, path, java);
    }
    internal void Save(ManualPlatformDefinition definition)
    {
        lock (Gate)
        {
            var records = Load();
            records.RemoveAll(item => item.Id == definition.Id);
            records.Add(definition);
            Write(records);
        }
    }
    internal void Remove(string id)
    {
        lock (Gate)
        {
            var records = Load();
            records.RemoveAll(item => item.Id == id);
            Write(records);
        }
    }
    private void Write(List<ManualPlatformDefinition> records)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string NormalizeFullPath(string value)
    {
        var fullPath = System.IO.Path.GetFullPath(value.Trim());
        var root = System.IO.Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
    }
}
