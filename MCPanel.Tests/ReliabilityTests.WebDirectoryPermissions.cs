using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void WebPermissionRepairRepairsProtectedProductAndFileWithoutChangingInstallRoot()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            using var identity = WindowsIdentity.GetCurrent();
            InstallDirectorySecurity.Harden(root, identity.User);
            var web = Path.Combine(root, "web");
            var product = Path.Combine(web, "YX030211");
            Directory.CreateDirectory(product);
            var config = Path.Combine(product, "web.config");
            File.WriteAllText(config, "<configuration/>");
            var group = InstallDirectorySecurity.IisUsers;
            var directoryAcl = Directory.GetAccessControl(product);
            directoryAcl.SetAccessRuleProtection(true, true);
            directoryAcl.PurgeAccessRules(group);
            Directory.SetAccessControl(product, directoryAcl);
            var fileAcl = File.GetAccessControl(config);
            fileAcl.SetAccessRuleProtection(true, true);
            fileAcl.PurgeAccessRules(group);
            File.SetAccessControl(config, fileAcl);
            var before = Directory.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            var webBefore = Directory.GetAccessControl(web).GetSecurityDescriptorSddlForm(AccessControlSections.Access);

            InstallDirectorySecurity.EnsureWebAccess(root, new[] { group }, repairProtectedChildren: true);
            Assert.IsTrue(HasWebModify(Directory.GetAccessControl(product), group));
            Assert.IsTrue(HasWebModify(File.GetAccessControl(config), group));
            Assert.AreEqual(before, Directory.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.AreEqual(webBefore, Directory.GetAccessControl(web).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
            Assert.IsTrue(Directory.GetAccessControl(product).AreAccessRulesProtected);
            Assert.IsTrue(File.GetAccessControl(config).AreAccessRulesProtected);
            var repaired = Directory.GetAccessControl(product).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            InstallDirectorySecurity.EnsureWebAccess(root, new[] { group }, repairProtectedChildren: true);
            Assert.AreEqual(repaired, Directory.GetAccessControl(product).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void WebPermissionRepairRestoresAuthorizedTomcatOperator()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User!;
            InstallDirectorySecurity.Harden(root, user);
            var app = Path.Combine(root, "web", "JAVA01", "WEB-INF");
            Directory.CreateDirectory(app);
            var acl = Directory.GetAccessControl(app);
            acl.SetAccessRuleProtection(true, true);
            acl.PurgeAccessRules(user);
            Directory.SetAccessControl(app, acl);
            InstallDirectorySecurity.EnsureWebAccess(root, new[] { InstallDirectorySecurity.IisUsers },
                repairProtectedChildren: true);
            Assert.IsTrue(HasWebModify(Directory.GetAccessControl(app), user),
                "Tomcat's normal interactive Windows account must retain the product grant.");
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void WebPermissionRepairDoesNotRemoveExplicitDeny()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var sid = InstallDirectorySecurity.IisUsers;
            var acl = Directory.GetAccessControl(root);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.WriteData,
                InheritanceFlags.None, PropagationFlags.None, AccessControlType.Deny));
            Directory.SetAccessControl(root, acl);
            var before = Directory.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            Assert.ThrowsException<UnauthorizedAccessException>(() =>
                WebDirectorySecurity.EnsureTreeAccess(root, new[] { sid }, repairProtectedChildren: true));
            Assert.AreEqual(before, Directory.GetAccessControl(root).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void WebPermissionsAreLimitedToManagedProducts()
    {
        Assert.IsTrue(WebDirectorySecurity.IsManagedProductPath(@"C:\MCPanel\web", @"C:\MCPanel\web\YX030211"));
        Assert.IsFalse(WebDirectorySecurity.IsManagedProductPath(@"C:\MCPanel\web", @"C:\MCPanel\web"));
        Assert.IsFalse(WebDirectorySecurity.IsManagedProductPath(@"C:\MCPanel\web", @"C:\MCPanel\Tomcat"));
        Assert.IsFalse(WebDirectorySecurity.IsManagedProductPath(@"C:\MCPanel\web", @"C:\MCPanel\web-other\app"));
        Assert.IsFalse(WebDirectorySecurity.IsManagedProductPath(@"C:\MCPanel\web", @"C:\MCPanel\web\.downloads\app"));
        Assert.IsTrue(App.IsNonInteractiveLaunch(new[] { WebDirectorySecurity.RepairArgument }));
    }

    [TestMethod]
    public async Task IisBindingGrantsActualPoolIdentityBeforeStartingAndScriptParses()
    {
        var script = ProductDeploymentService.BuildIisBindScript(@"C:\Windows\System32\inetsrv\appcmd.exe",
            "MCPanel", @"C:\MCPanel\Runtime\IISRoot", "/YX030211", @"C:\MCPanel\web\YX030211",
            "YX030211", "v4.0", "Integrated", true, 8089);
        StringAssert.Contains(script, "/text:processModel.identityType");
        StringAssert.Contains(script, "IIS AppPool");
        StringAssert.Contains(script, "AreAccessRulesProtected");
        StringAssert.Contains(script, "ReparsePoint");
        var grant = script.IndexOf("Grant-McPanelIisProductAccess $managedWebRoot $appRoot $poolName $appcmd", StringComparison.Ordinal);
        var start = script.IndexOf("start apppool", StringComparison.Ordinal);
        Assert.IsTrue(grant >= 0 && start > grant);
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "binding.ps1"), script, new UTF8Encoding(true));
            var parser = Path.Combine(root, "parse.ps1");
            File.WriteAllText(parser, """
                $tokens=$null; $errors=$null
                [System.Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'binding.ps1'),[ref]$tokens,[ref]$errors) | Out-Null
                if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Host $_.Message }; exit 1 }
                exit 0
                """, new UTF8Encoding(true));
            var result = await ProcessRunner.RunPowerShellFileAsync(parser, elevated: false,
                cancellationToken: CancellationToken.None, captureOutput: true);
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }

    private static bool HasWebModify(FileSystemSecurity security, SecurityIdentifier identity) =>
        security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(rule =>
            rule.AccessControlType == AccessControlType.Allow && identity.Equals(rule.IdentityReference) &&
            (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify);
}
