using System.IO;

namespace MCPanel;

/// <summary>
/// Shared path helpers. Every comparison is case-insensitive (Windows), and an
/// empty or malformed path never matches instead of throwing.
/// </summary>
internal static class PathUtil
{
    /// <summary>
    /// Full path without a trailing separator; a drive root keeps its
    /// separator ("C:\") so it still names the root.
    /// </summary>
    public static string NormalizeDirectory(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var rootLength = (Path.GetPathRoot(fullPath) ?? string.Empty).Length;
        return fullPath.Length > rootLength
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }

    /// <summary>True when both paths name the same file or directory.</summary>
    public static bool PathsEqual(string? first, string? second)
    {
        if (string.IsNullOrWhiteSpace(first) || string.IsNullOrWhiteSpace(second))
        {
            return false;
        }

        try
        {
            return string.Equals(NormalizeDirectory(first!), NormalizeDirectory(second!), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsPathFormatError(error))
        {
            return false;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> is strictly below
    /// <paramref name="root"/>; the root itself is not "inside".
    /// </summary>
    public static bool IsPathInside(string? root, string? path)
    {
        if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var normalizedRoot = NormalizeDirectory(root!);
            if (!normalizedRoot.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal))
            {
                normalizedRoot += Path.DirectorySeparatorChar;
            }

            var normalizedPath = Path.GetFullPath(path!);
            return normalizedPath.Length > normalizedRoot.Length &&
                   normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception error) when (IsPathFormatError(error))
        {
            return false;
        }
    }

    /// <summary>
    /// File/folder/service-safe name for a product id: letters, digits, '-',
    /// '_' and '.' are kept, anything else becomes '_'.
    /// </summary>
    public static string SafeProductName(string? value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray())
            .Trim('_');
        return safe.Length == 0 ? "product" : safe;
    }

    /// <summary>Escapes a value for a single-quoted PowerShell string literal.</summary>
    public static string EscapePowerShellLiteral(string value) => value.Replace("'", "''");

    /// <summary>Best-effort delete; a missing or locked file is ignored.</summary>
    public static void TryDeleteFile(string? path)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException || IsPathFormatError(error))
        {
            // Best-effort cleanup of local state markers and temp files.
        }
    }

    private static bool IsPathFormatError(Exception error) =>
        error is ArgumentException or NotSupportedException or PathTooLongException or System.Security.SecurityException;
}
