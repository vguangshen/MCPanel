using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using Microsoft.Win32;

namespace MCPanel;

/// <summary>
/// Moves the large component-owned folders out of StoreData while keeping
/// older installations upgradeable.  The operation is intentionally
/// idempotent and never overwrites a conflicting destination file.
/// </summary>
internal static class ComponentStorageMigration
{
    public static void MigrateLegacyData()
    {
        Migrate(ComponentPaths.ApplicationRoot, throwOnFailure: false);
    }

    internal static void MigrateForTest(string applicationRoot)
    {
        Migrate(Path.GetFullPath(applicationRoot), throwOnFailure: true);
    }

    private static void Migrate(string applicationRoot, bool throwOnFailure)
    {
        var storeDataRoot = Path.Combine(applicationRoot, "StoreData");
        var migrations = new[]
        {
            new StorageMove(
                Path.Combine(storeDataRoot, "Runtime"),
                Path.Combine(applicationRoot, "Runtime"),
                "Runtime"),
            new StorageMove(
                Path.Combine(storeDataRoot, "Downloads"),
                Path.Combine(applicationRoot, "Downloads"),
                "Downloads"),
            new StorageMove(
                Path.Combine(storeDataRoot, "Tools"),
                Path.Combine(applicationRoot, "Tools"),
                "Tools"),
            new StorageMove(
                Path.Combine(storeDataRoot, "ProductIcons"),
                Path.Combine(applicationRoot, "Cache", "ProductIcons"),
                "ProductIcons")
        };

        foreach (var migration in migrations)
        {
            try
            {
                if (string.Equals(migration.Name, "Runtime", StringComparison.OrdinalIgnoreCase) &&
                    !CanMigrateRuntime(migration.Source, message =>
                    {
                        if (throwOnFailure)
                        {
                            throw new IOException(message);
                        }

                        WriteMigrationWarning(applicationRoot, migration.Name, message, null);
                    }))
                {
                    continue;
                }

                MigrateDirectory(migration.Source, migration.Target, message =>
                {
                    if (throwOnFailure)
                    {
                        throw new IOException(message);
                    }

                    WriteMigrationWarning(applicationRoot, migration.Name, message, null);
                });
            }
            catch (Exception error) when (!throwOnFailure)
            {
                WriteMigrationWarning(applicationRoot, migration.Name, error.Message, error);
            }
        }
    }

    private static bool CanMigrateRuntime(string source, Action<string> warning)
    {
        var sourceRoot = NormalizeRoot(source);
        foreach (var serviceName in new[] { "MySQL80", NginxWindowsServiceManager.ServiceName })
        {
            try
            {
                using var key = Registry.LocalMachine.OpenSubKey(
                    $@"SYSTEM\CurrentControlSet\Services\{serviceName}",
                    writable: false);
                var imagePath = key?.GetValue("ImagePath")?.ToString();
                if (!string.IsNullOrWhiteSpace(imagePath) &&
                    Environment.ExpandEnvironmentVariables(imagePath)
                        .IndexOf(sourceRoot, StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    warning($"检测到 Windows 服务 {serviceName} 仍引用旧 Runtime 路径，已暂缓迁移：{imagePath}");
                    return false;
                }
            }
            catch (UnauthorizedAccessException)
            {
                // A non-elevated process cannot inspect one service key.  The
                // directory move below will still fail safely if it is locked.
            }
            catch (IOException)
            {
                // Treat a transient registry failure as an inconclusive probe.
            }
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    var executable = process.MainModule?.FileName;
                    if (!string.IsNullOrWhiteSpace(executable) &&
                        NormalizeRoot(executable!).StartsWith(sourceRoot, StringComparison.OrdinalIgnoreCase))
                    {
                        warning($"检测到进程 {process.ProcessName} 仍占用旧 Runtime 路径，已暂缓迁移：{executable}");
                        return false;
                    }
                }
                catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
                {
                    // Process inspection is best effort; the filesystem move
                    // remains the final safety check.
                }
            }
        }

        return true;
    }

    private static void MigrateDirectory(string source, string target, Action<string> warning)
    {
        if (!Directory.Exists(source))
        {
            return;
        }

        if (IsReparsePoint(source))
        {
            warning($"已跳过重解析目录：{source}");
            return;
        }

        if (!Directory.Exists(target))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            Directory.Move(source, target);
            return;
        }

        MergeDirectory(source, target, warning);
        TryDeleteEmptyDirectory(source, warning);
    }

    private static void MergeDirectory(string source, string target, Action<string> warning)
    {
        Directory.CreateDirectory(target);

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(target, Path.GetFileName(file));
            if (!File.Exists(destination))
            {
                File.Move(file, destination);
                continue;
            }

            if (FilesAreIdentical(file, destination))
            {
                File.Delete(file);
                continue;
            }

            warning($"发现同名文件，保留旧文件未覆盖：{file}");
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            var destination = Path.Combine(target, Path.GetFileName(directory));
            if (Directory.Exists(destination))
            {
                if (IsReparsePoint(directory))
                {
                    warning($"已跳过重解析目录：{directory}");
                    continue;
                }

                MergeDirectory(directory, destination, warning);
                TryDeleteEmptyDirectory(directory, warning);
                continue;
            }

            Directory.Move(directory, destination);
        }
    }

    private static bool FilesAreIdentical(string left, string right)
    {
        try
        {
            var leftInfo = new FileInfo(left);
            var rightInfo = new FileInfo(right);
            if (leftInfo.Length != rightInfo.Length)
            {
                return false;
            }

            using var leftStream = new FileStream(left, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var rightStream = new FileStream(right, FileMode.Open, FileAccess.Read, FileShare.Read);
            using var sha = SHA256.Create();
            var leftHash = sha.ComputeHash(leftStream);
            sha.Initialize();
            var rightHash = sha.ComputeHash(rightStream);
            return leftHash.SequenceEqual(rightHash);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string NormalizeRoot(string path) =>
        Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static void TryDeleteEmptyDirectory(string path, Action<string> warning)
    {
        try
        {
            if (!Directory.EnumerateFileSystemEntries(path).Any())
            {
                Directory.Delete(path, recursive: false);
            }
        }
        catch (IOException error)
        {
            warning($"旧目录尚未清空：{path}，原因：{error.Message}");
        }
        catch (UnauthorizedAccessException error)
        {
            warning($"旧目录尚未清空：{path}，原因：{error.Message}");
        }
    }

    private static void WriteMigrationWarning(string applicationRoot, string name, string message, Exception? error)
    {
        try
        {
            var workDirectory = Path.Combine(applicationRoot, "StoreData", "Work");
            Directory.CreateDirectory(workDirectory);
            var detail = error is null ? string.Empty : Environment.NewLine + error;
            RollingLogWriter.Append(
                Path.Combine(workDirectory, "storage-layout-migration.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {name}：{message}{detail}{Environment.NewLine}");
        }
        catch
        {
            // Migration diagnostics must never prevent application startup.
        }
    }

    private sealed class StorageMove
    {
        public StorageMove(string source, string target, string name)
        {
            Source = source;
            Target = target;
            Name = name;
        }

        public string Source { get; }
        public string Target { get; }
        public string Name { get; }
    }
}
