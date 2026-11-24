using System.Diagnostics;
using System.IO;

namespace MCPanel;

/// <summary>Opens folders, files and web pages with the user's shell.</summary>
internal static class ShellOpen
{
    /// <summary>Shows a folder in File Explorer, optionally creating it first.</summary>
    public static void Folder(string? path, bool create = false)
    {
        path = RequirePath(path);
        if (create)
        {
            Directory.CreateDirectory(path);
        }

        // Quote properly: a naive "\"" + path + "\"" breaks on a trailing backslash such as C:\.
        Start(new ProcessStartInfo("explorer.exe", Compat.QuoteCommandLineArgument(path)) { UseShellExecute = true });
    }

    /// <summary>Opens a file with its associated program (a log in the text editor, a console snap-in).</summary>
    public static void File(string? path) =>
        Start(new ProcessStartInfo(RequirePath(path)) { UseShellExecute = true });

    /// <summary>
    /// Opens an http(s) address in the default browser. Other schemes and
    /// local paths are refused, so a product or service URL can never launch
    /// a program.
    /// </summary>
    public static void Url(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            throw new ArgumentException("只能打开 http 或 https 地址：" + url, nameof(url));
        }

        Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary><see cref="Url"/> that reports a refused address instead of throwing.</summary>
    public static bool TryUrl(string? url)
    {
        try
        {
            Url(url ?? string.Empty);
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static string RequirePath(string? path) =>
        string.IsNullOrWhiteSpace(path) ? throw new ArgumentException("路径不能为空。", nameof(path)) : path!;

    private static void Start(ProcessStartInfo info) => Process.Start(info)?.Dispose();
}
