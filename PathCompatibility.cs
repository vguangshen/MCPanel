using System.IO;

namespace MCPanel;

/// <summary>
/// Keeps native components that still use the Windows ANSI file APIs away from
/// non-ASCII installation paths.  The panel itself remains free to live in a
/// Chinese directory; only the component-owned executable trees need this
/// compatibility location.
/// </summary>
internal static class PathCompatibility
{
    public static bool ContainsNonAscii(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        foreach (var character in path)
        {
            if (character > 0x7f)
            {
                return true;
            }
        }

        return false;
    }

    public static string GetNativeComponentRoot(string applicationRoot, string componentName)
    {
        var normalizedRoot = Path.GetFullPath(applicationRoot);
        if (!ContainsNonAscii(normalizedRoot))
        {
            return Path.Combine(normalizedRoot, componentName);
        }

        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var safeRoot = string.IsNullOrWhiteSpace(commonData)
            ? string.Empty
            : Path.Combine(commonData, "MCPanel", componentName);
        if (!ContainsNonAscii(safeRoot))
        {
            return safeRoot;
        }

        var systemDrive = Environment.GetEnvironmentVariable("SystemDrive");
        if (string.IsNullOrWhiteSpace(systemDrive))
        {
            systemDrive = "C:\\";
        }
        else if (!systemDrive.EndsWith("\\", StringComparison.Ordinal))
        {
            systemDrive += "\\";
        }

        return Path.Combine(systemDrive, "MCPanelData", componentName);
    }
}
