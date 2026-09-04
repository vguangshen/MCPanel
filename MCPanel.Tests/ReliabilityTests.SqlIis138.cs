using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void SqlServer138_CompatibilityMatrix_FollowsMicrosoftWindowsMatrix()
    {
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2025Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2022Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2017Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2017Id, WindowsSqlCompatibilityFamily.WindowsServer2022).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows11).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows10).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2025Id, WindowsSqlCompatibilityFamily.WindowsServer2016).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2022Id, WindowsSqlCompatibilityFamily.WindowsServer2016).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2008Id, WindowsSqlCompatibilityFamily.WindowsServer2012R2).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2008Id, WindowsSqlCompatibilityFamily.Windows10).IsSupported);
    }

    [TestMethod]
    public void SqlServer138_UnknownWindows_LeavesEveryVersionEnabled()
    {
        foreach (var release in SqlServerReleaseCatalog.Options)
        {
            var support = SqlServerOsCompatibility.GetSupport(release.Id, WindowsSqlCompatibilityFamily.Unknown, "无法识别的 Windows");
            Assert.IsTrue(support.IsSupported, $"{release.DisplayName} should remain selectable when Windows cannot be identified.");
            StringAssert.Contains(support.Message, "未应用 SQL Server 版本限制");
        }
    }

    [TestMethod]
    public void SqlServer138_LegacyWindows_RespectsMicrosoftServicePackRequirements()
    {
        Assert.AreEqual(WindowsSqlCompatibilityFamily.Windows7, SqlServerOsCompatibility.Classify(false, 7600));
        Assert.AreEqual(WindowsSqlCompatibilityFamily.Windows7Sp1, SqlServerOsCompatibility.Classify(false, 7601));
        Assert.AreEqual(WindowsSqlCompatibilityFamily.WindowsServer2008R2, SqlServerOsCompatibility.Classify(true, 7600));
        Assert.AreEqual(WindowsSqlCompatibilityFamily.WindowsServer2008R2Sp1, SqlServerOsCompatibility.Classify(true, 7601));
        Assert.AreEqual(WindowsSqlCompatibilityFamily.WindowsVistaSp2, SqlServerOsCompatibility.Classify(false, 6002));
        Assert.AreEqual(WindowsSqlCompatibilityFamily.WindowsServer2008Sp2, SqlServerOsCompatibility.Classify(true, 6002));

        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows7).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows7Sp1).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2008Id, WindowsSqlCompatibilityFamily.Windows7).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2017Id, WindowsSqlCompatibilityFamily.Windows7Sp1).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.WindowsServer2008Sp1).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.WindowsServer2008Sp2).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.WindowsVistaSp1).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.WindowsVistaSp2).IsSupported);
    }

    [TestMethod]
    public void Iis138_UsesWindowsFeaturesWithoutAspnetRegiis_AndValidatesRewriteAndServices()
    {
        var script = EnvironmentInstaller.BuildIisScript(@"C:\Temp\URLRewrite.msi");
        StringAssert.Contains(script, "IIS-ASPNET45");
        StringAssert.Contains(script, "NetFx3");
        StringAssert.Contains(script, "RestartNeeded");
        StringAssert.Contains(script, "RewriteModule");
        StringAssert.Contains(script, "W3SVC");
        StringAssert.Contains(script, "WAS");
        Assert.IsFalse(script.Contains("aspnet_regiis", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SqlServer138_2012Express_RequestsDatabaseEngineOnly()
    {
        var script = EnvironmentInstaller.BuildSqlServer2008Script(@"C:\Temp\SQLEXPR_x64_ENU.exe", @"C:\SqlData", "Example!Pass123", "SQL Server 2012 Express SP4");
        StringAssert.Contains(script, "/FEATURES=SQL ");
        Assert.IsFalse(script.Contains("/FEATURES=SQL,Tools", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SqlServer138_ModernSetup_UsesOfficialUpdateDefault_AndKeepsStorePendingRenameBehavior()
    {
        var downloads = new EnvironmentDownloadSettings(
            "http://example/rewrite.msi", "http://example/nginx.zip", "http://example/mysql.zip",
            "https://example/sql2025.exe", "https://example/sql2025dev.exe", "https://example/sql2022.exe",
            "https://example/sql2017.exe", "https://example/sql2012x64.exe", "https://example/sql2012x86.exe",
            "http://example/sql2008x64.exe", "http://example/sql2008x86.exe",
            "http://example/tomcat.zip", "https://example/frp.zip");
        var plan = EnvironmentInstaller.GetSqlServerInstallPlan(downloads, SqlServerReleaseCatalog.SqlServer2022Id);
        var script = EnvironmentInstaller.BuildModernSqlServerScript(@"C:\Temp\SQL2022-SSEI-Expr.exe", @"C:\SqlData", plan, "Example!Pass123");
        Assert.IsFalse(script.Contains("/UpdateEnabled=False", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(script, "Remove-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations");
        Assert.IsFalse(script.Contains("Remove-PathSafe (Join-Path $env:ProgramFiles 'Microsoft SQL Server')", StringComparison.Ordinal));
        var sectorProbe = script.IndexOf("fsutil fsinfo sectorinfo", StringComparison.Ordinal);
        var sectorOverride = script.IndexOf("New-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes'", StringComparison.Ordinal);
        Assert.IsTrue(sectorProbe >= 0 && sectorOverride > sectorProbe, "Sector size must be measured before writing the NVMe compatibility override.");
    }
}
