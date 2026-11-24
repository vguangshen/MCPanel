using System.IO;
using System.Text;

namespace MCPanel;

/// <summary>Shared readers for logs and text files written by other programs.</summary>
internal static class TextFiles
{
    /// <summary>
    /// Reads up to the last <paramref name="maximumBytes"/> of a log that may
    /// still be open for writing. A BOM is detected from the real start of the
    /// file; without one, strict UTF-8 is tried first and the system ANSI code
    /// page (GBK on Chinese Windows) is the fallback.
    /// </summary>
    public static string ReadTailBestEffort(string path, int maximumBytes = 512 * 1024)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        // Detect the BOM from the real start of the file, even when only the
        // tail is read below.
        var head = new byte[3];
        var headLength = stream.Read(head, 0, head.Length);
        Encoding? bomEncoding = null;
        var bomLength = 0;
        if (headLength >= 3 && head[0] == 0xEF && head[1] == 0xBB && head[2] == 0xBF)
        {
            bomEncoding = Encoding.UTF8;
            bomLength = 3;
        }
        else if (headLength >= 2 && head[0] == 0xFF && head[1] == 0xFE)
        {
            bomEncoding = Encoding.Unicode;
            bomLength = 2;
        }
        else if (headLength >= 2 && head[0] == 0xFE && head[1] == 0xFF)
        {
            bomEncoding = Encoding.BigEndianUnicode;
            bomLength = 2;
        }

        var offset = Math.Max(bomLength, stream.Length - maximumBytes);
        if (bomLength == 2 && (offset - bomLength) % 2 != 0)
        {
            // Keep UTF-16 reads on a character boundary.
            offset++;
        }

        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Max(0, stream.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }

        var start = 0;
        if (offset > bomLength && bomEncoding is null or UTF8Encoding)
        {
            // Skip a UTF-8 sequence cut in half by the tail offset.
            while (start < read && start < 3 && (buffer[start] & 0xC0) == 0x80) start++;
        }

        if (bomEncoding is not null)
        {
            return bomEncoding.GetString(buffer, start, read - start);
        }

        // Strict UTF-8 fails on GBK and other legacy bytes (decoding them
        // leniently only produced U+FFFD), so fall back to the system ANSI
        // code page, which Windows installers use for their logs.
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(buffer, start, read - start);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Default.GetString(buffer, 0, read);
        }
    }

    /// <summary>True when <paramref name="value"/> contains any candidate, ignoring case.</summary>
    public static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));
}
