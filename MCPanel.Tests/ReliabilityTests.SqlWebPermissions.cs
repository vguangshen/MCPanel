using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void SqlWebPermissions_UsesActualAccountAndInheritedModifyWithoutResettingAcl()
    {
        var script = SqlServerWebPermissionsScript.Functions;
        StringAssert.Contains(script, "Win32_Service");
        StringAssert.Contains(script, "StartName");
        StringAssert.Contains(script, "S-1-5-18");
        StringAssert.Contains(script, "S-1-5-20");
        StringAssert.Contains(script, "SecurityIdentifier");
        StringAssert.Contains(script, "(OI)(CI)M");
        StringAssert.Contains(script, "AreAccessRulesProtected");
        StringAssert.Contains(script, "ReparsePoint");
        StringAssert.Contains(script, "$LASTEXITCODE -ne 0");
        Assert.IsFalse(script.Contains("/reset", StringComparison.OrdinalIgnoreCase));
        Assert.IsFalse(script.Contains("S-1-1-0", StringComparison.Ordinal));
    }

    [TestMethod]
    public void SqlWebPermissions_InstallRepairAndRuntimeUseManagedWebRoot()
    {
        var legacy = EnvironmentInstaller.BuildSqlServer2008Script(@"C:\Temp\SQLEXPR.exe", @"C:\SqlData", "Example!Pass123");
        var downloads = new EnvironmentDownloadSettings(
            "http://example/rewrite.msi", "http://example/nginx.zip", "http://example/mysql.zip",
            "https://example/sql2025.exe", "https://example/sql2025dev.exe", "https://example/sql2022.exe",
            "https://example/sql2017.exe", "https://example/sql2012x64.exe", "https://example/sql2012x86.exe",
            "http://example/sql2008x64.exe", "http://example/sql2008x86.exe",
            "http://example/tomcat.zip", "https://example/frp.zip");
        var plan = EnvironmentInstaller.GetSqlServerInstallPlan(downloads, SqlServerReleaseCatalog.SqlServer2022Id);
        var modern = EnvironmentInstaller.BuildModernSqlServerScript(@"C:\Temp\SQL2022.exe", @"C:\SqlData", plan, "Example!Pass123");
        var runtime = EnvironmentRuntimeService.BuildSqlServerServiceActionScript("restart", "test.log", "test.result");
        foreach (var script in new[] { legacy, modern, runtime })
        {
            StringAssert.Contains(script, PathUtil.EscapePowerShellLiteral(ComponentPaths.WebRoot));
            StringAssert.Contains(script, "function Grant-McPanelSqlWebAccess");
            StringAssert.Contains(script, "Grant-McPanelSqlWebAccess $webRoot");
        }
        Assert.AreEqual(2, legacy.Split(new[] { "Grant-McPanelSqlWebAccess $webRoot" }, StringSplitOptions.None).Length - 1);
        Assert.AreEqual(2, modern.Split(new[] { "Grant-McPanelSqlWebAccess $webRoot" }, StringSplitOptions.None).Length - 1);
    }
}
