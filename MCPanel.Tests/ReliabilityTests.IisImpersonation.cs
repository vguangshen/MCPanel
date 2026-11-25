using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void IisProductFoldersAlsoLetImpersonatingPagesWrite()
    {
        // An ASP.NET page with <identity impersonate="true"/> saves web.config as IUSR.
        var script = ManagedIisPermissionsScript.Functions;
        StringAssert.Contains(script, "$sids = @($sid, 'S-1-5-17') | Select-Object -Unique");
        StringAssert.Contains(script, "foreach ($grantSid in $sids) { Set-McPanelIisAcl $directory $grantSid }");
        StringAssert.Contains(script, "AreAccessRulesProtected) { foreach ($grantSid in $sids) { Set-McPanelIisAcl $item $grantSid } }",
            "Protected subfolders must get the IUSR grant too.");
        Assert.AreEqual("S-1-5-17", WebDirectorySecurity.AnonymousIisUser.Value);

        var repair = ReadRepositoryFile("Update/WebDirectorySecurity.cs");
        StringAssert.Contains(repair, "EnsureTreeAccess(physical, new[] { sid, AnonymousIisUser }, repairProtectedChildren: true);",
            "The startup repair must grant IUSR on existing IIS product folders.");
        StringAssert.Contains(repair, "\"web-permissions-v3.completed\"",
            "Installations already repaired by v2 must run the repair once more.");
    }

    [TestMethod]
    public void IusrGrantMakesNewFilesWritableInTheProductFolder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = Path.Combine(root, "web", "YX030211");
            Directory.CreateDirectory(product);
            File.WriteAllText(Path.Combine(product, "web.config"), "<configuration />");

            WebDirectorySecurity.EnsureTreeAccess(product, new[] { WebDirectorySecurity.AnonymousIisUser }, repairProtectedChildren: true);

            var rules = Directory.GetAccessControl(product).GetAccessRules(true, true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>().ToArray();
            Assert.IsTrue(rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                WebDirectorySecurity.AnonymousIisUser.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) ==
                    (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)),
                "IUSR must be able to create the temporary file next to web.config, and new files inherit it.");
        }
        finally { DeleteTemporaryTree(root); }
    }
}
