using System.Configuration;
using System.IO;

namespace MCPanel;

/// <summary>
/// Resolves the product directory using the same precedence as the original
/// client: a product-level Url, then the legacy path/cpath settings, then the
/// panel's web directory.  The product id is always appended as a child so a
/// cleanup can never target the configured parent itself.
/// </summary>
internal static class ProductInstallPathResolver
{
    public static string ResolveProductDirectory(ProductItem product)
    {
        if (product is null) throw new ArgumentNullException(nameof(product));
        return Path.Combine(ResolveProductRoot(product.InstallRoot), SafeName(product.ProductId));
    }

    public static string ResolveProductRoot(string? productUrl = null)
    {
        var configured = NormalizeCandidate(productUrl);
        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ExistingLegacyRoot("path") ?? ExistingLegacyRoot("cpath");
        }

        if (string.IsNullOrWhiteSpace(configured))
        {
            configured = ComponentPaths.WebRoot;
        }

        if (!Path.IsPathRooted(configured))
        {
            configured = Path.Combine(ComponentPaths.ApplicationRoot, configured);
        }

        return Path.GetFullPath(configured!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
    }

    public static bool IsInsideProductRoot(string path, ProductItem product)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var root = ResolveProductRoot(product.InstallRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return fullPath.Equals(root, StringComparison.OrdinalIgnoreCase) ||
               fullPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static string SafeName(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray())
            .Trim('_');
        return safe.Length == 0 ? "product" : safe;
    }

    private static string? ExistingLegacyRoot(string key)
    {
        try
        {
            var value = NormalizeCandidate(ConfigurationManager.AppSettings[key]);
            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                return value;
            }
        }
        catch
        {
            // A malformed legacy setting must not prevent the normal web root fallback.
        }

        return null;
    }

    private static string? NormalizeCandidate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = Environment.ExpandEnvironmentVariables(value!.Trim().Trim('"'));
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }
}
