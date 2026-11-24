using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MCPanel;

/// <summary>Shared directory, copy and hashing helpers.</summary>
internal static class FileUtil
{
    /// <summary>Best effort: makes every file and folder below <paramref name="path"/> writable.</summary>
    public static void ClearAttributesRecursively(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch (Exception error) when (IsFileSystemError(error)) { }
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(directory, FileAttributes.Normal); } catch (Exception error) when (IsFileSystemError(error)) { }
            }

            try { File.SetAttributes(path, FileAttributes.Normal); } catch (Exception error) when (IsFileSystemError(error)) { }
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
            // Directory.Delete afterwards reports the actionable error.
        }
    }

    /// <summary>
    /// Deletes a folder tree, clearing read-only attributes and retrying while
    /// a file is briefly locked. A missing folder is fine; anything still in
    /// place after the last attempt throws with the real error, so a caller
    /// never reports success while files remain.
    /// </summary>
    public static void DeleteDirectory(string path, int attempts = 3, int retryDelayMilliseconds = 100)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        ClearAttributesRecursively(path);
        for (var attempt = 1; attempt < attempts; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(retryDelayMilliseconds);
            }
            catch (UnauthorizedAccessException)
            {
                ClearAttributesRecursively(path);
                Thread.Sleep(retryDelayMilliseconds);
            }

            if (!Directory.Exists(path))
            {
                return;
            }
        }

        Directory.Delete(path, recursive: true);
    }

    /// <summary>Best-effort <see cref="DeleteDirectory"/>; returns whether the folder is gone.</summary>
    public static bool TryDeleteDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return true;
        }

        try
        {
            DeleteDirectory(path!);
        }
        catch (Exception error) when (IsFileSystemError(error))
        {
        }

        return !Directory.Exists(path);
    }

    /// <summary>
    /// Copies a folder tree, overwriting existing files. Symbolic links and
    /// junctions are skipped, never followed, so a link cannot pull files from
    /// outside the source. <paramref name="skipTopLevelFile"/> leaves one file
    /// of the source folder itself out.
    /// </summary>
    public static void CopyDirectory(string source, string destination, string? skipTopLevelFile = null)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(file) ||
                string.Equals(Path.GetFileName(file), skipTopLevelFile, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsReparsePoint(directory))
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(destination, Path.GetFileName(directory)));
        }
    }

    /// <summary>Lower-case hex SHA-256 of a file's contents.</summary>
    public static string ComputeSha256(string path)
    {
        using var sha = SHA256.Create();
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan);
        return ToHex(sha.ComputeHash(stream));
    }

    /// <summary>Lower-case hex SHA-256 of a string's UTF-8 bytes.</summary>
    public static string Sha256Hex(string value)
    {
        using var sha = SHA256.Create();
        return ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(value ?? string.Empty)));
    }

    public static string ToHex(byte[] bytes) =>
        BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();

    private static bool IsReparsePoint(string path) =>
        (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

    private static bool IsFileSystemError(Exception error) =>
        error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;
}
