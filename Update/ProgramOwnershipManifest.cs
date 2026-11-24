using System.IO;
using System.Text;

namespace MCPanel;

internal static class ProgramOwnershipManifest
{
    internal static void RefreshIfInstalled(string installRoot, string payloadRoot)
    {
        var maintenance = Path.Combine(installRoot, "Maintenance");
        var marker = Path.Combine(maintenance, "installer.marker");
        try
        {
            if (!File.Exists(marker) || File.ReadAllText(marker).Trim() != "MCPanel.Installer.v1") return;
            var prefix = Path.GetFullPath(payloadRoot).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var owned = Directory.GetFiles(payloadRoot, "*", SearchOption.AllDirectories)
                .Select(file => Path.GetFullPath(file).Substring(prefix.Length).Replace('/', '\\'))
                .Where(name => !DeploymentLayoutManifest.PreservedTopLevelNames.Contains(name.Split('\\')[0]))
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase);
            AtomicFile.WriteAllText(Path.Combine(maintenance, "program-files.lst"),
                string.Join("\r\n", owned) + "\r\n", new UTF8Encoding(true));
        }
        catch (Exception error)
        {
            // A metadata write must never report an already committed program
            // replacement as failed. Existing uninstall ownership remains valid.
            System.Diagnostics.Debug.WriteLine("Unable to refresh installer file ownership: " + error.Message);
        }
    }
}
