using System.IO;
using System.Text.Json;

namespace MCPanel;

public sealed class ProductCacheStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _cacheFile;

    public ProductCacheStore()
    {
        var directory = ComponentPaths.CacheRoot;
        Directory.CreateDirectory(directory);
        _cacheFile = Path.Combine(directory, "products-cache.json");
        MigrateLegacyCacheIfNeeded();
    }

    public IReadOnlyList<ProductItem> Load()
    {
        if (!File.Exists(_cacheFile))
        {
            return [];
        }

        try
        {
            var json = File.ReadAllText(_cacheFile);
            var records = JsonSerializer.Deserialize<List<ProductCacheRecord>>(json, JsonOptions) ?? [];
            return records
                .Where(IsUsableRecord)
                .Select(ToProduct)
                .ToList();
        }
        catch
        {
            return [];
        }
    }

    public void Save(IEnumerable<ProductItem> products)
    {
        var records = products.Select(ProductCacheRecord.FromProduct).ToList();
        var json = JsonSerializer.Serialize(records, JsonOptions);
        AtomicFile.WriteAllText(_cacheFile, json);
    }

    private void MigrateLegacyCacheIfNeeded()
    {
        if (File.Exists(_cacheFile))
        {
            return;
        }

        var legacyFile = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "MCPanel",
            "products-cache.json");

        if (File.Exists(legacyFile))
        {
            try
            {
                File.Copy(legacyFile, _cacheFile, overwrite: false);
            }
            catch (Exception error)
            {
                RollingLogWriter.Append(
                    Path.Combine(ComponentPaths.WorkRoot, "product-cache.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 迁移旧产品缓存失败：{error}{Environment.NewLine}");
            }
        }
    }

    private static ProductItem ToProduct(ProductCacheRecord record)
    {
        var downloadUrl = record.DownloadUrl;
        var installRoot = record.InstallRoot;
        // Older caches stored the original ProductInfo.Url in DownloadUrl.  Keep
        // those products installable while restoring the original field meaning.
        if (string.IsNullOrWhiteSpace(installRoot) && !LooksLikeRemoteSource(downloadUrl))
        {
            installRoot = downloadUrl;
            downloadUrl = null;
        }

        var usesSvn = record.UsesSvn;
        if (!usesSvn && Uri.TryCreate(downloadUrl, UriKind.Absolute, out var sourceUri))
        {
            usesSvn = McPanelStoreClient.IsVendorSvnRepository(sourceUri);
        }

        var remoteIconUrl = !string.IsNullOrWhiteSpace(record.RemoteIconUrl)
            ? record.RemoteIconUrl
            : ProductIconCache.IsCacheableRemoteIcon(record.IconPath)
                ? record.IconPath
                : null;
        var iconPath = ResolveDisplayIcon(record.ProductId, record.IconPath, remoteIconUrl);
        var product = new ProductItem(
            record.ProductId,
            record.Name,
            record.Level,
            iconPath,
            record.Source)
        {
            RemoteIconUrl = remoteIconUrl,
            RunEnvironment = record.RunEnvironment,
            SqlEnvironment = record.SqlEnvironment,
            DevLanguage = record.DevLanguage,
            InstallRoot = installRoot,
            SysType = record.SysType,
            UsesSvn = usesSvn,
            DownloadUrl = downloadUrl,
            FileName = record.FileName,
            StatusText = string.IsNullOrWhiteSpace(record.StatusText) ? "已从本地缓存加载。" : record.StatusText
        };

        return product;
    }

    private static bool IsUsableRecord(ProductCacheRecord record) =>
        record is not null &&
        !string.IsNullOrWhiteSpace(record.ProductId) &&
        record.ProductId.IndexOfAny(['\r', '\n', '\t']) < 0 &&
        !record.ProductId.TrimStart().StartsWith("[", StringComparison.Ordinal) &&
        !record.ProductId.TrimStart().StartsWith("{", StringComparison.Ordinal);

    private static bool LooksLikeRemoteSource(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (!Uri.TryCreate(value!.Trim(), UriKind.Absolute, out var uri)) return false;
        return uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
               uri.Scheme.Equals("svn", StringComparison.OrdinalIgnoreCase);
    }

    private static string ResolveDisplayIcon(string productId, string iconPath, string? remoteIconUrl)
    {
        if (!string.IsNullOrWhiteSpace(remoteIconUrl))
        {
            var cached = ProductIconCache.ResolveCachedIconPath(productId, remoteIconUrl!);
            if (!ProductIconCache.IsCacheableRemoteIcon(cached))
            {
                return cached;
            }
        }

        if (string.IsNullOrWhiteSpace(iconPath))
        {
            return "/Assets/defaultimg.png";
        }

        if (ProductIconCache.IsCacheableRemoteIcon(iconPath))
        {
            return "/Assets/defaultimg.png";
        }

        if (Path.IsPathRooted(iconPath) && !File.Exists(iconPath))
        {
            return "/Assets/defaultimg.png";
        }

        return iconPath;
    }

    private sealed record ProductCacheRecord(
        string ProductId,
        string Name,
        string Level,
        string IconPath,
        ProductSource Source,
        string? DownloadUrl,
        string? FileName,
        string StatusText,
        string? RemoteIconUrl = null,
        string? RunEnvironment = null,
        string? SqlEnvironment = null,
        string? DevLanguage = null,
        string? InstallRoot = null,
        string? SysType = null,
        bool UsesSvn = false)
    {
        public static ProductCacheRecord FromProduct(ProductItem product)
        {
            return new ProductCacheRecord(
                product.ProductId,
                product.Name,
                product.Level,
                product.IconPath,
                product.Source,
                product.DownloadUrl,
                product.FileName,
                product.StatusText,
                product.RemoteIconUrl,
                product.RunEnvironment,
                product.SqlEnvironment,
                product.DevLanguage,
                product.InstallRoot,
                product.SysType,
                product.UsesSvn);
        }
    }
}
