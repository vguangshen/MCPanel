using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

/// <summary>
/// Locates application sources for source-level tests. A path is tried
/// relative to the repository root first; a bare file name is then found
/// anywhere in the application sources, so moving a file between folders
/// does not break the tests that read it.
/// </summary>
internal static class RepositorySource
{
    private static readonly Lazy<string> RootPath = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 MCPanel 源码目录。");
    });

    public static string Root => RootPath.Value;

    public static string Resolve(string relativePath)
    {
        var direct = Path.Combine(Root, relativePath);
        if (File.Exists(direct))
        {
            return direct;
        }

        var name = Path.GetFileName(relativePath);
        var matches = ApplicationFiles(name).ToArray();
        if (matches.Length == 1)
        {
            return matches[0];
        }

        Assert.Fail(matches.Length == 0
            ? "Unable to locate repository source: " + relativePath
            : "Ambiguous repository source " + relativePath + ": " + string.Join(", ", matches));
        return string.Empty;
    }

    /// <summary>Application sources matching <paramref name="pattern"/>, excluding build output and tests.</summary>
    public static IEnumerable<string> ApplicationFiles(string pattern) =>
        Directory.EnumerateFiles(Root, pattern, SearchOption.AllDirectories)
            .Where(path => !IsExcluded(path.Substring(Root.Length).TrimStart(Path.DirectorySeparatorChar)));

    private static bool IsExcluded(string relative)
    {
        var first = relative.Split(Path.DirectorySeparatorChar)[0];
        if (first is "bin" or "obj" or "artifacts" or ".git" or "MCPanel.Tests" or "Vendor")
        {
            return true;
        }

        return relative.StartsWith(@"Installer\MCPanel.Setup\", StringComparison.OrdinalIgnoreCase) ||
               relative.Split(Path.DirectorySeparatorChar).Any(part => part is "bin" or "obj");
    }
}
