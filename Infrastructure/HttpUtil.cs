using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;

namespace MCPanel;

/// <summary>Shared HTTP helpers for the store client, the updater and the AI service.</summary>
internal static class HttpUtil
{
    /// <summary>
    /// Status codes that carry a Location to follow: 301, 302, 303, 307 and
    /// 308. Other 3xx codes (300 Multiple Choices, 304 Not Modified, 305) are
    /// not redirects.
    /// </summary>
    public static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Redirect
            or HttpStatusCode.RedirectMethod
            or HttpStatusCode.TemporaryRedirect
            || (int)statusCode == 308;

    /// <summary>
    /// Reads a response body as text, refusing more than <paramref name="maximumBytes"/>
    /// (checked against Content-Length first, then while reading). The
    /// declared charset is honoured; an unknown one falls back to UTF-8.
    /// </summary>
    public static async Task<string> ReadBoundedTextAsync(
        HttpContent content,
        long maximumBytes,
        string tooLargeMessage,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength is long length && length > maximumBytes)
        {
            throw new InvalidDataException(tooLargeMessage);
        }

        using var stream = await content.ReadAsStreamAsync();
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        while (true)
        {
            var read = await stream.ReadAsync(chunk, 0, chunk.Length, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (buffer.Length + read > maximumBytes)
            {
                throw new InvalidDataException(tooLargeMessage);
            }

            buffer.Write(chunk, 0, read);
        }

        var encoding = Encoding.UTF8;
        var charset = content.Headers.ContentType?.CharSet?.Trim('"');
        if (!string.IsNullOrWhiteSpace(charset))
        {
            try
            {
                encoding = Encoding.GetEncoding(charset);
            }
            catch (ArgumentException)
            {
                // An unknown charset label: the services MCPanel talks to send UTF-8.
            }
        }

        return encoding.GetString(buffer.ToArray());
    }

    /// <summary>The same URI with a trailing slash, so relative paths resolve inside it.</summary>
    public static Uri EnsureDirectoryUri(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);
}
