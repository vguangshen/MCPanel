using System.IO;
using System.Text.Json;

namespace MCPanel;

internal sealed record ManualPlatformDefinition(string Id, string Name, string Path, bool Java)
{
    public string Architecture { get; init; } = "Auto";
    public string DetectedArchitecture { get; init; } = "64";
    public string? JavaDocBase { get; init; }
    public string? IisRuntime { get; init; }
    public ProductItem ToProduct() => new(Id, Name, "", "/Assets/defaultimg.png", ProductSource.Online)
    {
        ExternalInstallPath = Path,
        RunEnvironment = Java ? "Tomcat" : "IIS",
        DevLanguage = Java ? "Java" : ".NET",
        SysType = Architecture == "Auto" ? DetectedArchitecture : Architecture,
        ExternalJavaDocBase = JavaDocBase,
        ExternalIisRuntime = IisRuntime,
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
            try
            {
                return ReadRecords(_file);
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                throw new IOException("本地平台记录无法读取，已暂停保存。可恢复最近有效备份，或检查记录文件：" + _file, ex);
            }
        }
    }
    internal ManualPlatformDefinition Prepare(string name, string directory, bool java, string? existingId = null,
        string? javaDocBase = null, string architecture = "Auto", string? iisRuntime = null,
        CancellationToken cancellationToken = default)
    {
        name = (name ?? "").Trim();
        if (name.Length == 0 || name.Length > 100) throw new ArgumentException("请输入 1–100 个字符的平台名称。");
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Path.IsPathRooted(directory)) throw new ArgumentException("请选择有效的完整软件目录。");
        var path = NormalizeFullPath(directory);
        if (!Directory.Exists(path)) throw new DirectoryNotFoundException("所选软件目录不存在。");
        if (!Directory.EnumerateFileSystemEntries(path).Any()) throw new InvalidDataException("所选软件目录为空。");
        cancellationToken.ThrowIfCancellationRequested();
        if (java)
        {
            if (javaDocBase is null)
            {
                var targets = LocalPlatformInspection.FindJavaTargets(path, cancellationToken);
                if (targets.Count != 1) throw new InvalidDataException(targets.Count == 0
                    ? "Java 软件目录中需包含 WAR 文件或 WEB-INF 目录。" : "发现多个 Java 应用，请选择实际部署的应用。");
                javaDocBase = targets[0];
            }
            LocalPlatformInspection.ValidateJavaTarget(path, javaDocBase);
        }
        if (architecture is not ("Auto" or "32" or "64")) throw new ArgumentException("请选择有效的程序架构。");
        if (iisRuntime is not (null or "" or "v2.0" or "v4.0")) throw new ArgumentException("请选择有效的 IIS 运行时。");
        var records = Load();
        var existing = records.FirstOrDefault(item => item.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (existing is not null && existingId is not null && existing.Id != existingId)
            throw new InvalidOperationException("此目录已被另一平台使用，请编辑该平台。");
        if (existing is not null && existing.Java != java) throw new InvalidOperationException("此目录已绑定到另一种平台，请先解除原绑定。");
        return new ManualPlatformDefinition(existingId ?? existing?.Id ?? "local_" + Guid.NewGuid().ToString("N"), name, path, java)
        {
            JavaDocBase = javaDocBase, Architecture = architecture, IisRuntime = iisRuntime,
            DetectedArchitecture = java ? "64" : architecture != "Auto" ? architecture : LocalPlatformInspection.DetectArchitecture(path, cancellationToken)
        };
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
        if (File.Exists(_file))
        {
            ReadRecords(_file); // Never rotate corrupt input over the last valid backup.
            AtomicFile.WriteAllBytes(_file + ".bak", File.ReadAllBytes(_file));
        }
        AtomicFile.WriteAllText(_file, JsonSerializer.Serialize(records, new JsonSerializerOptions { WriteIndented = true }));
        if (!File.Exists(_file + ".bak")) AtomicFile.WriteAllBytes(_file + ".bak", File.ReadAllBytes(_file));
    }

    internal void RestoreBackup()
    {
        lock (Gate)
        {
            ReadRecords(_file + ".bak");
            if (File.Exists(_file)) AtomicFile.WriteAllBytes(_file + ".before-recovery", File.ReadAllBytes(_file));
            AtomicFile.WriteAllBytes(_file, File.ReadAllBytes(_file + ".bak"));
        }
    }

    private static List<ManualPlatformDefinition> ReadRecords(string file)
    {
        var records = JsonSerializer.Deserialize<List<ManualPlatformDefinition>>(File.ReadAllText(file))
            ?? throw new JsonException("平台记录内容为空。");
        if (records.Any(item => item is null || string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.Path)) ||
            records.Select(item => item.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != records.Count)
            throw new JsonException("平台记录格式不正确。");
        return records;
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
