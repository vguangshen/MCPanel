using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void SqlServerUninstallPreservesSharedAndUnrelatedComponents()
    {
        var script = EnvironmentRuntimeService.BuildSqlServerUninstallScript("scope-test.log");

        StringAssert.Contains(script, "@('MSSQLSERVER','SQLSERVERAGENT')");
        StringAssert.Contains(script, "保留 SQL Native Client、ODBC/OLE DB Driver");
        StringAssert.Contains(script, "Where-Object { $_.DisplayName -like 'MCPanel SQL Server *' }");
        Assert.IsTrue(script.IndexOf("$_.Name -like 'MSSQL$*'", System.StringComparison.Ordinal) < 0);
        Assert.IsTrue(script.IndexOf("$_.Name -like 'SQLAgent$*'", System.StringComparison.Ordinal) < 0);
        Assert.IsTrue(script.IndexOf("Microsoft ODBC Driver.*SQL", System.StringComparison.Ordinal) < 0);
        Assert.IsTrue(script.IndexOf("Microsoft OLE DB Driver.*SQL", System.StringComparison.Ordinal) < 0);
        Assert.IsTrue(script.IndexOf("(Join-Path $env:ProgramFiles 'Microsoft SQL Server')", System.StringComparison.Ordinal) < 0);
        Assert.IsTrue(script.IndexOf("HKLM:\\SOFTWARE\\Microsoft\\Microsoft SQL Server'", System.StringComparison.Ordinal) < 0);
    }

    [TestMethod]
    public void MySqlStartDoesNotSilentlyResetRootPassword()
    {
        var script = EnvironmentRuntimeService.BuildMySqlServiceActionScript(
            "start",
            "mysql-action.log",
            "mysql-action.result");

        Assert.IsTrue(script.IndexOf("Reset-RootPassword $paths", System.StringComparison.Ordinal) < 0);
        StringAssert.Contains(script, "为避免意外修改数据库密码，MCPanel 已停止自动重置");
    }

    [TestMethod]
    [DoNotParallelize]
    public void DamagedSqlServerCredentialFileIsRejectedInsteadOfInventingPassword()
    {
        var file = Path.Combine(ComponentPaths.RuntimeStateRoot, "sqlserver-default.json");
        var backup = File.Exists(file) ? File.ReadAllBytes(file) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.WriteAllText(file, "{broken-json");
            Assert.ThrowsException<InvalidDataException>(() => SqlServerCredentialStore.Load());
        }
        finally
        {
            if (backup is not null)
            {
                File.WriteAllBytes(file, backup);
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [TestMethod]
    [DoNotParallelize]
    public void DamagedMySqlCredentialFileIsRejectedInsteadOfFallingBackToMike()
    {
        var file = Path.Combine(ComponentPaths.RuntimeStateRoot, "mysql-default.json");
        var backup = File.Exists(file) ? File.ReadAllBytes(file) : null;
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        try
        {
            File.WriteAllText(file, "{broken-json");
            Assert.ThrowsException<InvalidDataException>(() => MySqlCredentialStore.Load());
        }
        finally
        {
            if (backup is not null)
            {
                File.WriteAllBytes(file, backup);
            }
            else if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }
}
