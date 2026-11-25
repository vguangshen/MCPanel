using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private static readonly SecurityIdentifier NetworkService = new(WellKnownSidType.NetworkServiceSid, null);

    [DataTestMethod]
    [DataRow("NT AUTHORITY\\NETWORK SERVICE", "S-1-5-20")]
    [DataRow("NT AUTHORITY\\NetworkService", "S-1-5-20")]
    [DataRow("NetworkService", "S-1-5-20")]
    [DataRow("LocalSystem", "S-1-5-18")]
    [DataRow("NT AUTHORITY\\LOCAL SERVICE", "S-1-5-19")]
    public void SqlServerServiceAccountsResolveToTheirSids(string account, string sid) =>
        Assert.AreEqual(sid, InstallDirectorySecurity.ResolveServiceAccount(account)!.Value);

    [TestMethod]
    public void UnknownServiceAccountsAreIgnored()
    {
        Assert.IsNull(InstallDirectorySecurity.ResolveServiceAccount(null));
        Assert.IsNull(InstallDirectorySecurity.ResolveServiceAccount("   "));
        Assert.IsNull(InstallDirectorySecurity.ResolveServiceAccount(@"NO-SUCH-DOMAIN\no-such-account-" + Guid.NewGuid().ToString("N")));
    }

    [TestMethod]
    public void WebServicesKeepWriteAccessAndTheGrantIsWrittenOnlyOnce()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            using var identity = WindowsIdentity.GetCurrent();
            InstallDirectorySecurity.Harden(root, identity.User);
            Assert.IsTrue(InstallDirectorySecurity.IsHardened(root), "A hardened root is recognised, so updates skip re-hardening.");

            var web = Path.Combine(root, "web");
            var database = Path.Combine(web, "DS0101", "db", "product.mdf");
            Directory.CreateDirectory(Path.GetDirectoryName(database)!);
            File.WriteAllText(database, "data");

            InstallDirectorySecurity.EnsureWebAccess(root, new[] { InstallDirectorySecurity.IisUsers, NetworkService });
            var before = Directory.GetAccessControl(web).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            InstallDirectorySecurity.EnsureWebAccess(root, new[] { InstallDirectorySecurity.IisUsers, NetworkService });
            Assert.AreEqual(before, Directory.GetAccessControl(web).GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                "A grant that is already present must not be written again (that walks the whole product tree).");

            // The database file below web\ inherits Modify for the SQL Server account.
            var fileRules = File.GetAccessControl(database).GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            foreach (var account in new[] { NetworkService, InstallDirectorySecurity.IisUsers })
            {
                Assert.IsTrue(fileRules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                    account.Equals(rule.IdentityReference) &&
                    (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify),
                    account.Value + " must be able to write product files under web\\.");
            }
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void AnInheritingInstallRootIsNotTreatedAsHardened()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            Assert.IsFalse(InstallDirectorySecurity.IsHardened(root));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void AManuallyStartedPanelSeesTheRunningUpdaterAndItsStage()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            UpdaterProgress.Report(root, "正在校验并解压更新包");
            var notice = UpdaterProgress.DescribeRunningUpdate(root);
            StringAssert.Contains(notice, "后台更新器仍在运行（正在校验并解压更新包");
            StringAssert.Contains(notice, "当前打开的仍是旧版本");
            StringAssert.Contains(File.ReadAllText(UpdaterProgress.LogPath(root)), "正在校验并解压更新包");

            UpdaterProgress.Finish(root);
            Assert.IsNull(UpdaterProgress.DescribeRunningUpdate(root));

            // A status left behind by an updater that is gone is cleared.
            File.WriteAllText(UpdaterProgress.StatusPath(root),
                System.Text.Json.JsonSerializer.Serialize(new UpdaterProgress.Status("正在替换程序文件", int.MaxValue, DateTime.Now, DateTime.Now)));
            Assert.IsNull(UpdaterProgress.DescribeRunningUpdate(root));
            Assert.IsFalse(File.Exists(UpdaterProgress.StatusPath(root)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void StartupCleanupLeavesARunningUpdaterAlone()
    {
        var helper = CreateTemporaryDirectory();
        try
        {
            var exe = Path.Combine(helper, "MCPanel.exe");
            File.WriteAllText(exe, "updater");
            Assert.IsFalse(ApplicationUpdateService.IsUpdaterRunning(helper), "A finished updater folder may be cleaned up.");
            using (new FileStream(exe, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Assert.IsTrue(ApplicationUpdateService.IsUpdaterRunning(helper),
                    "An updater whose program is in use must not be deleted mid-update.");
            }
        }
        finally { DeleteTemporaryTree(helper); }
    }
}
