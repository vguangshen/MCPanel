using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void OnlineUpdatePreservesUninstallerAndRefreshesOwnedFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var maintenance = Path.Combine(plan.InstallDirectory, "Maintenance");
            Directory.CreateDirectory(maintenance);
            File.WriteAllText(Path.Combine(maintenance, "installer.marker"), "MCPanel.Installer.v1\r\n");
            File.WriteAllText(Path.Combine(maintenance, "unins000.exe"), "native uninstaller");
            File.WriteAllText(Path.Combine(maintenance, "program-files.lst"), "old.dll\r\n");
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "added.dll"), "new library");
            Directory.CreateDirectory(Path.Combine(plan.PayloadDirectory, "web"));
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "web", "ignored.txt"), "not program ownership");
            ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("native uninstaller", File.ReadAllText(Path.Combine(maintenance, "unins000.exe")));
            var owned = File.ReadAllLines(Path.Combine(maintenance, "program-files.lst"));
            CollectionAssert.Contains(owned, "MCPanel.exe");
            CollectionAssert.Contains(owned, "added.dll");
            Assert.IsFalse(owned.Contains("old.dll"));
            Assert.IsFalse(owned.Any(name => name.StartsWith("web\\")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void PortableUpdateDoesNotCreateAnInstallerRegistration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            ApplicationUpdateService.ApplyTransaction(plan);
            Assert.IsFalse(Directory.Exists(Path.Combine(plan.InstallDirectory, "Maintenance")));
        }
        finally { DeleteTemporaryTree(root); }
    }
}
