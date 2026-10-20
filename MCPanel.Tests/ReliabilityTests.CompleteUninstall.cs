using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void CompleteUninstallOwnershipUsesPathBoundariesAndDatabaseIdentity()
    {
        const string root = @"C:\MCPanel";
        Assert.IsTrue(InstallationCleanupWorker.IsUnderRoot(@"C:\MCPanel\web\site", root));
        Assert.IsFalse(InstallationCleanupWorker.IsUnderRoot(@"C:\MCPanelOther\web", root));
        Assert.IsFalse(InstallationCleanupWorker.IsUnderRoot(@"C:\MCPanel\..\Other", root));
        Assert.IsTrue(InstallationCleanupWorker.OwnsMySql(@"C:\MCPanel\MySQL\bin\mysqld.exe", root, []));
        Assert.IsFalse(InstallationCleanupWorker.OwnsMySql(@"C:\Other\MySQL\bin\mysqld.exe", root, []));
        Assert.IsFalse(InstallationCleanupWorker.OwnsMySql(@"C:\MCPanel\Tools\unrelated.exe", root, []));
        Assert.IsTrue(InstallationCleanupWorker.OwnsSqlServer([@"-dC:\MCPanel\MSSQL\DATA\master.mdf"], root));
        Assert.IsFalse(InstallationCleanupWorker.OwnsSqlServer([@"-dC:\Other\master.mdf", @"-eC:\MCPanel\Logs\ERRORLOG"], root));
    }

    [TestMethod]
    public void MaintenanceModesDoNotRegisterNormalStartupOrLaunchTheTray()
    {
        Assert.IsTrue(App.IsNonInteractiveLaunch([InstallerRegistration.RegisterArgument]));
        Assert.IsTrue(App.IsNonInteractiveLaunch([InstallationCleanupWorker.Argument, "result.json"]));
        Assert.IsFalse(App.IsNonInteractiveLaunch([]));
    }

    [TestMethod]
    public void PortableFolderCannotCreateSystemUninstallRegistration()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, InstallerRegistration.UninstallScriptName), "uninstaller");
            Assert.IsNull(InstallerRegistration.FindRegistration(root));
            Assert.IsFalse(InstallerRegistration.NeedsRefresh(root));
            Assert.ThrowsException<InvalidOperationException>(() => InstallerRegistration.Refresh(root));
        }
        finally { DeleteTemporaryTree(root); }
    }
}
