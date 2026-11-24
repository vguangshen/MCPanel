using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace MCPanel;

// Rollback state for a product install and its preserved configuration.
public sealed partial class ProductDeploymentService
{
    internal sealed class ProductInstallTransaction : IDisposable
    {
        private readonly string? _backupRoot;
        private readonly string? _stagingContainer;
        private readonly bool _ownsSwap;
        private bool _finished;

        public ProductInstallTransaction(string productRoot, string preparedPath, string backupRoot, string stagingContainer)
        {
            ProductRoot = productRoot;
            PreparedPath = preparedPath;
            _backupRoot = backupRoot;
            _stagingContainer = stagingContainer;
            _ownsSwap = true;
        }

        private ProductInstallTransaction(string productRoot, string preparedPath)
        {
            ProductRoot = productRoot;
            PreparedPath = preparedPath;
        }

        public string ProductRoot { get; }
        public string PreparedPath { get; }
        public bool IsExisting => !_ownsSwap;

        public static ProductInstallTransaction ForExisting(string productRoot, string preparedPath) =>
            new(productRoot, preparedPath);

        public string? Complete()
        {
            if (_finished)
            {
                return null;
            }

            // Deployment is committed before retiring its backup. A locked
            // cleanup file must never roll back to a partially deleted backup.
            _finished = true;
            try
            {
                if (!string.IsNullOrWhiteSpace(_backupRoot))
                    FileUtil.DeleteDirectory(_backupRoot!);
                CleanupStaging();
                return null;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                return $"产品已安装成功，但临时备份清理未完成，请关闭占用后清理：{_backupRoot}；{_stagingContainer}。{error.Message}";
            }
        }

        public void Rollback()
        {
            if (_finished || !_ownsSwap)
            {
                _finished = true;
                return;
            }

            FileUtil.DeleteDirectory(ProductRoot);
            if (!string.IsNullOrWhiteSpace(_backupRoot) && Directory.Exists(_backupRoot))
            {
                Directory.Move(_backupRoot, ProductRoot);
            }

            CleanupStaging();
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Rollback();
            }
        }

        private void CleanupStaging()
        {
            if (!string.IsNullOrWhiteSpace(_stagingContainer))
            {
                FileUtil.DeleteDirectory(_stagingContainer!);
            }
        }
    }

    private sealed class ProductConfigurationSnapshot
    {
        private readonly IReadOnlyDictionary<string, byte[]?> _files;

        private ProductConfigurationSnapshot(IReadOnlyDictionary<string, byte[]?> files)
        {
            _files = files;
        }

        public static ProductConfigurationSnapshot Capture(string productRoot, string preparedPath)
        {
            var files = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ProductConfigurationService.GetConfigurationFilesForBackup(productRoot, preparedPath))
            {
                if (!File.Exists(file))
                {
                    files[file] = null;
                    continue;
                }

                var length = new FileInfo(file).Length;
                if (length > 8L * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        $"产品配置文件过大，无法在更新前安全创建回滚副本：{file}");
                }

                files[file] = File.ReadAllBytes(file);
            }

            return new ProductConfigurationSnapshot(files);
        }

        public void Restore()
        {
            foreach (var pair in _files)
            {
                if (pair.Value is null)
                {
                    if (File.Exists(pair.Key))
                    {
                        File.SetAttributes(pair.Key, FileAttributes.Normal);
                        File.Delete(pair.Key);
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                File.WriteAllBytes(pair.Key, pair.Value);
            }
        }
    }
}
