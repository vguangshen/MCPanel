using System.IO;
using System.Reflection;

namespace MCPanel;

internal static class LocalPlatformInspection
{
    internal static IReadOnlyList<string> FindJavaTargets(string root, CancellationToken token = default)
    {
        if (File.Exists(root) && root.EndsWith(".war", StringComparison.OrdinalIgnoreCase)) return [root];
        if (!Directory.Exists(root)) return [];
        if (Directory.Exists(Path.Combine(root, "WEB-INF"))) return [Path.GetFullPath(root)];
        var pending = new Queue<(string Path, int Depth)>();
        pending.Enqueue((root, 0));
        var targets = new List<string>();
        var inspected = 0;
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            if (++inspected > 1024) throw new InvalidDataException("目录内容过多，请选择更接近 WAR 或 WEB-INF 的软件目录。");
            var current = pending.Dequeue();
            if (Directory.Exists(Path.Combine(current.Path, "WEB-INF")))
            {
                targets.Add(Path.GetFullPath(current.Path));
                continue;
            }
            targets.AddRange(Directory.EnumerateFiles(current.Path, "*.war", SearchOption.TopDirectoryOnly));
            if (current.Depth == 0 && targets.Count > 0) break;
            if (targets.Count > 100) throw new InvalidDataException("发现的应用过多，请选择具体的软件目录。");
            if (current.Depth >= 5) continue;
            foreach (var child in Directory.EnumerateDirectories(current.Path))
            {
                token.ThrowIfCancellationRequested();
                var name = Path.GetFileName(child);
                if (name.StartsWith(".") || name.IndexOf("backup", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.IndexOf("备份", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    name.Equals("bak", StringComparison.OrdinalIgnoreCase) || name.Equals("node_modules", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0) continue;
                pending.Enqueue((child, current.Depth + 1));
            }
        }
        return targets.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    internal static void ValidateJavaTarget(string root, string target)
    {
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullTarget = Path.GetFullPath(target);
        if ((!fullTarget.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase) &&
             !fullTarget.Equals(fullRoot.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)) ||
            !(Directory.Exists(Path.Combine(fullTarget, "WEB-INF")) ||
              (File.Exists(fullTarget) && fullTarget.EndsWith(".war", StringComparison.OrdinalIgnoreCase))))
            throw new InvalidDataException("选择的 Java 应用已失效，请重新选择目录。");
    }

    internal static string DetectArchitecture(string root, CancellationToken token = default)
    {
        var architectures = new HashSet<string>();
        foreach (var folder in new[] { root, Path.Combine(root, "bin") }.Where(Directory.Exists))
        foreach (var file in Directory.EnumerateFiles(folder).Where(file => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) ||
                     file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Take(256))
        {
            token.ThrowIfCancellationRequested();
            try
            {
                var architecture = AssemblyName.GetAssemblyName(file).ProcessorArchitecture;
                if (architecture == ProcessorArchitecture.X86) architectures.Add("32");
                else if (architecture == ProcessorArchitecture.Amd64) architectures.Add("64");
            }
            catch (BadImageFormatException)
            {
                try
                {
                    using var reader = new BinaryReader(File.OpenRead(file));
                    if (reader.ReadUInt16() != 0x5a4d) continue;
                    reader.BaseStream.Position = 0x3c;
                    var offset = reader.ReadInt32();
                    if (offset < 0 || offset > reader.BaseStream.Length - 6) continue;
                    reader.BaseStream.Position = offset;
                    if (reader.ReadUInt32() != 0x4550) continue;
                    var machine = reader.ReadUInt16();
                    if (machine == 0x14c) architectures.Add("32");
                    else if (machine == 0x8664) architectures.Add("64");
                }
                catch (IOException) { }
            }
            catch (IOException) { }
        }
        if (architectures.Count > 1) throw new InvalidDataException("目录同时包含 x86 和 x64 组件，请在高级设置中选择实际运行架构。");
        return architectures.FirstOrDefault() ?? (Environment.Is64BitOperatingSystem ? "64" : "32");
    }
}
