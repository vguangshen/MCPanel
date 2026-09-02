using System.IO;
using System.Net.Http;
using System.Security.Cryptography;

namespace MCPanel;

public sealed class ProductIconCache : IDisposable
{
    private const int MaximumIconBytes = 4 * 1024 * 1024;
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public async Task<int> CacheIconsAsync(IEnumerable<ProductItem> products, CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(IconDirectory);
        using var semaphore = new SemaphoreSlim(6);
        var tasks = products
            .Where(product => GetRemoteIconUrl(product) is not null)
            .Select(product => CacheIconAsync(product, semaphore, cancellationToken));

        var results = await Task.WhenAll(tasks);
        return results.Count(success => success);
    }

    public static string ResolveCachedIconPath(string productId, string? iconPath)
    {
        iconPath ??= string.Empty;
        if (!IsRemoteIcon(iconPath))
        {
            return iconPath;
        }

        if (!IsCacheableRemoteIcon(iconPath))
        {
            return "/Assets/defaultimg.png";
        }

        var cached = GetIconPath(productId, iconPath);
        return File.Exists(cached) ? cached : "/Assets/defaultimg.png";
    }

    private async Task<bool> CacheIconAsync(ProductItem product, SemaphoreSlim semaphore, CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var iconUrl = GetRemoteIconUrl(product);
            if (iconUrl is null)
            {
                return false;
            }

            var target = GetIconPath(product.ProductId, iconUrl);
            if (File.Exists(target) && new FileInfo(target).Length > 0)
            {
                product.IconPath = target;
                return true;
            }

            using var response = await _httpClient.GetAsync(
                iconUrl,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                product.IconPath = "/Assets/defaultimg.png";
                return false;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) && !LooksLikeImagePath(iconUrl))
            {
                product.IconPath = "/Assets/defaultimg.png";
                return false;
            }

            if (response.Content.Headers.ContentLength is > MaximumIconBytes)
            {
                product.IconPath = "/Assets/defaultimg.png";
                return false;
            }

            using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var memory = new MemoryStream();
            var buffer = new byte[64 * 1024];
            while (true)
            {
                var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (memory.Length + read > MaximumIconBytes)
                {
                    product.IconPath = "/Assets/defaultimg.png";
                    return false;
                }

                memory.Write(buffer, 0, read);
            }

            var bytes = memory.ToArray();
            if (bytes.Length == 0)
            {
                product.IconPath = "/Assets/defaultimg.png";
                return false;
            }

            await FileCompat.WriteAllBytesAsync(target, bytes, cancellationToken);
            product.IconPath = target;
            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Remote icons are nice-to-have; product list should remain usable without them.
            product.IconPath = "/Assets/defaultimg.png";
            return false;
        }
        finally
        {
            semaphore.Release();
        }
    }

    public void Dispose() => _httpClient.Dispose();

    private static bool IsRemoteIcon(string? iconPath) =>
        Uri.TryCreate(iconPath, UriKind.Absolute, out var uri) &&
        (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
         uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase));

    public static bool IsCacheableRemoteIcon(string? iconPath) =>
        !string.IsNullOrWhiteSpace(iconPath) && IsRemoteIcon(iconPath) && LooksLikeImagePath(iconPath!);

    private static string? GetRemoteIconUrl(ProductItem product)
    {
        if (!string.IsNullOrWhiteSpace(product.RemoteIconUrl) && IsCacheableRemoteIcon(product.RemoteIconUrl))
        {
            return product.RemoteIconUrl;
        }

        return IsCacheableRemoteIcon(product.IconPath) ? product.IconPath : null;
    }

    private static string GetIconPath(string productId, string iconUrl)
    {
        Directory.CreateDirectory(IconDirectory);
        var extension = ".png";
        if (Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri))
        {
            var candidate = Path.GetExtension(uri.LocalPath);
            if (!string.IsNullOrWhiteSpace(candidate) && candidate.Length <= 8)
            {
                extension = candidate;
            }
        }

        var safeProductId = string.Concat(productId.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        return Path.Combine(IconDirectory, $"{safeProductId}_{Hash(iconUrl)}{extension}");
    }

    private static bool LooksLikeImagePath(string iconUrl)
    {
        if (!Uri.TryCreate(iconUrl, UriKind.Absolute, out var uri))
        {
            return false;
        }

        var extension = Path.GetExtension(uri.LocalPath);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".gif", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".webp", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".ico", StringComparison.OrdinalIgnoreCase);
    }

    private static string Hash(string value)
    {
        byte[] bytes;
        using (var sha256 = SHA256.Create())
        {
            bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(value));
        }

        return BitConverter.ToString(bytes).Replace("-", string.Empty).Substring(0, 12).ToLowerInvariant();
    }

    private static string IconDirectory => ComponentPaths.ProductIconsRoot;
}
