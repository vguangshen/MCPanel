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
