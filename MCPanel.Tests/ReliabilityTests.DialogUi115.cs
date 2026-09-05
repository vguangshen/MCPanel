using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void DialogUi115_AllOwnedBusinessDialogsUseOneModalChromeAndScrim()
    {
        var modal = ReadRepositoryFile("PanelModalWindow.cs");
        var chrome = ReadRepositoryFile(Path.Combine("Resources", "PanelModalChrome.xaml"));
        var main = ReadRepositoryFile("MainWindow.xaml");
        var environment = ReadRepositoryFile("MainWindow.Environment.cs");

        StringAssert.Contains(modal, "StandardCardWidth = 820d");
        StringAssert.Contains(modal, "StandardCardHeight = 600d");
        StringAssert.Contains(modal, "Color.FromArgb(0x78, 0x00, 0x00, 0x00)");
        StringAssert.Contains(chrome, "x:Key=\"PanelModalSurface\"");
        StringAssert.Contains(chrome, "<Setter Property=\"Width\" Value=\"820\" />");
        StringAssert.Contains(chrome, "<Setter Property=\"Height\" Value=\"600\" />");

        foreach (var path in new[]
                 {
                     "PanelMessageDialog.xaml",
                     "CustomWebsiteDialog.xaml",
                     "ProductWebsiteDialog.xaml",
                     "NginxProxyDialog.xaml",
                     "PanelInputDialog.xaml"
                 })
        {
            var xaml = ReadRepositoryFile(path);
            StringAssert.Contains(xaml, "<local:PanelModalWindow");
            StringAssert.Contains(xaml, "Style=\"{StaticResource PanelModalSurface}\"");
        }

        StringAssert.Contains(main, "<Popup x:Name=\"DownloadQueuePopup\"");
        StringAssert.Contains(main, "Background=\"#78000000\"");
        StringAssert.Contains(main, "Visibility=\"{Binding InstallationProgress.IsVisible, Converter={StaticResource BooleanToVisibility}}\"");
        StringAssert.Contains(main, "Style=\"{StaticResource PanelModalSurface}\"");

        StringAssert.Contains(environment, "PanelInputDialog.CreatePortEditor(currentPort)");
        StringAssert.Contains(environment, "PanelInputDialog.CreatePasswordEditor()");
        Assert.IsFalse(environment.Contains("new Window", StringComparison.Ordinal),
            "Legacy ad-hoc MySQL windows must not bypass the unified modal system.");
    }

    [TestMethod]
    public void DialogUi115_NoLegacyXamlWindowShellsRemainOutsideMainWindow()
    {
        var root = FindRepositoryRoot115();
        var offenders = Directory.EnumerateFiles(root, "*.xaml", SearchOption.AllDirectories)
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("MainWindow.xaml", StringComparison.OrdinalIgnoreCase))
            .Where(path => File.ReadAllText(path).Contains("<Window x:Class=", StringComparison.Ordinal))
            .Select(path => path.Substring(root.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .ToArray();

        Assert.AreEqual(0, offenders.Length,
            "All application-owned XAML dialogs must derive from PanelModalWindow. Offenders: " + string.Join(", ", offenders));
    }

    private static string FindRepositoryRoot115()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        Assert.Fail("Unable to locate MCPanel repository root.");
        return string.Empty;
    }
}
