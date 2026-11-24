using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace MCPanel;

// Package and product file handling: checksum, safe ZIP extraction, copy and best-effort deletion.
public sealed partial class ProductDeploymentService
{
    private static void VerifyFileSha256(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || expected!.Length != 64 || expected.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("产品 SHA-256 清单格式无效。");
        if (!File.Exists(path)) throw new InvalidDataException($"产品文件缺失：{path}");
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"产品文件完整性校验失败：{Path.GetFileName(path)}");
    }

    private static bool DirectoryContains(string packagePath, string directoryName)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateDirectories(packagePath, directoryName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectoryContainsFile(string packagePath, string fileName)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(packagePath, fileName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectoryContainsExtension(string packagePath, string extension)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(packagePath, $"*{extension}", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractZipSafely(string archivePath, string targetDirectory)
    {
        var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包包含越界路径，已取消安装：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string NormalizeProductRoot(string productRoot)
    {
        var current = productRoot;
        while (true)
        {
            var directories = Directory.GetDirectories(current);
            var files = Directory.GetFiles(current);
            if (directories.Length != 1 || files.Length != 0)
            {
                return current;
            }

            current = directories[0];
        }
    }

    private static void EnsureDirectoryContainsFiles(string path)
    {
        if (Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
        {
            return;
        }

        Directory.Delete(path, recursive: true);
        throw new InvalidDataException("产品包解压后没有任何文件，已取消安装。");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void DeleteDownloadedPackage(string packagePath)
    {
        try
        {
            if (!PathUtil.IsPathInside(DownloadRoot, packagePath))
            {
                return;
            }

            if (Directory.Exists(packagePath))
            {
                Directory.Delete(packagePath, recursive: true);
            }
            else if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
        catch
        {
            // A successful deployment must not be reported as failed only because its cache could not be removed.
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearAttributesRecursively(path);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(80);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                ClearAttributesRecursively(path);
                Thread.Sleep(80);
            }
        }

        // Surface the final exception with the exact path.  The caller must not
        // report a successful uninstall while an SVN working copy remains.
        Directory.Delete(path, recursive: true);
    }

    internal static void DeleteDirectoryForTest(string path) => DeleteDirectoryIfExists(path);

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void ClearAttributesRecursively(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }

            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        }
        catch
        {
            // Directory.Delete below supplies the actionable error if traversal
            // itself was blocked.
        }
    }

    private static void DeleteProductFiles(ProductItem product, string safeName)
    {
        if (product.IsExternalPlatform) throw new InvalidOperationException("本地平台仅允许解除绑定，不能删除用户软件目录。");
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProductInstallPathResolver.ResolveProductDirectory(product),
            Path.Combine(ComponentPaths.WebRoot, safeName),
            Path.Combine(ComponentPaths.ProductDownloadRoot, safeName),
            $"{Path.Combine(ComponentPaths.ProductDownloadRoot, safeName)}.downloading",
            Path.Combine(ComponentPaths.LegacyProductDownloadRoot, safeName),
            $"{Path.Combine(ComponentPaths.LegacyProductDownloadRoot, safeName)}.downloading",
            Path.Combine(ComponentPaths.RuntimeRoot, "IISApps", safeName),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns", safeName),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "IISApps", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatProductRuns", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", safeName),
            Path.Combine(ComponentPaths.LegacyInstalledProductRoot, safeName)
        };

        foreach (var root in new[]
                 {
                     ProductInstallPathResolver.ResolveProductRoot(product.InstallRoot),
                     WebRoot
                 })
        {
            foreach (var containerName in new[] { ".deploying", ".rollback" })
            {
                var container = Path.Combine(root, containerName);
                if (!Directory.Exists(container)) continue;
                foreach (var directory in Directory.EnumerateDirectories(container, $"{safeName}.*", SearchOption.TopDirectoryOnly))
                {
                    directories.Add(directory);
                }
            }
        }

        foreach (var directory in directories)
        {
            DeleteDirectoryIfExists(directory);
        }

        DeleteFileIfExists(GetIisStateFile(safeName));
        DeleteFileIfExists(GetTomcatStateFile(safeName));
        foreach (var scriptName in new[]
                 {
                     $"unbind-product-{safeName}.ps1",
                     $"remove-stale-iis-{safeName}.ps1",
                     $"bind-iis-{safeName}.ps1"
                 })
        {
            DeleteFileIfExists(Path.Combine(ComponentPaths.WorkRoot, scriptName));
        }
    }
}
