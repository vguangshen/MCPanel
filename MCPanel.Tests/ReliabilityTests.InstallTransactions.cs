using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AtomicReplacementPreservesDestinationWhenSourceCannotBeMoved(bool locked)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "incoming.tmp");
            var target = Path.Combine(root, "server.xml");
            File.WriteAllText(target, "last valid configuration");
            if (locked) File.WriteAllText(source, "new configuration");
            using (locked ? File.Open(source, FileMode.Open, FileAccess.Read, FileShare.None) : null)
            {
                if (locked) Assert.ThrowsException<IOException>(() => FileCompat.Move(source, target, true));
                else Assert.ThrowsException<FileNotFoundException>(() => FileCompat.Move(source, target, true));
            }
            Assert.AreEqual("last valid configuration", File.ReadAllText(target));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void AtomicReplacementCommitsValidSourceAndHonorsNoOverwrite()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "incoming.tmp");
            var target = Path.Combine(root, "server.xml");
            File.WriteAllText(source, "new"); File.WriteAllText(target, "old");
            Assert.ThrowsException<IOException>(() => FileCompat.Move(source, target, false));
            Assert.AreEqual("old", File.ReadAllText(target));
            FileCompat.Move(source, target, true);
            Assert.AreEqual("new", File.ReadAllText(target));
            Assert.IsFalse(File.Exists(source));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProductCleanupFailureCannotRollBackACommittedDeployment(bool lockBackup)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = Directory.CreateDirectory(Path.Combine(root, "product")).FullName;
            var backup = Directory.CreateDirectory(Path.Combine(root, "backup")).FullName;
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            File.WriteAllText(Path.Combine(product, "product.txt"), "complete new deployment");
            File.WriteAllText(Path.Combine(backup, "old.txt"), "old deployment");
            var locked = Path.Combine(lockBackup ? backup : staging, "locked.txt");
            File.WriteAllText(locked, "in use");
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var transaction = new ProductDeploymentService.ProductInstallTransaction(product, product, backup, staging))
            {
                var warning = transaction.Complete();
                Assert.IsNotNull(warning);
                StringAssert.Contains(warning, "清理未完成");
                transaction.Rollback();
            }
            Assert.AreEqual("complete new deployment", File.ReadAllText(Path.Combine(product, "product.txt")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow(@"D:\中文目录\MCPanel\MySQL")]
    [DataRow(@"C:\ProgramData\MCPanel\MySQL")]
    public void MySqlStagingAndBackupStayBesideTheLiveTreeOnItsVolume(string liveRoot)
    {
        var staging = EnvironmentInstaller.CreateMySqlStagingContainer(liveRoot);
        Assert.AreEqual(Path.GetPathRoot(liveRoot), Path.GetPathRoot(staging));
        Assert.AreEqual(Path.GetDirectoryName(liveRoot), Path.GetDirectoryName(staging));
        Assert.IsFalse(staging.StartsWith(liveRoot + "\\", StringComparison.OrdinalIgnoreCase));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MySqlActivationFailureRestoresOrPreservesTheOnlyOriginalDatabase(bool blockRecovery)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var live = Path.Combine(root, "MySQL");
            Directory.CreateDirectory(Path.Combine(live, "data"));
            File.WriteAllText(Path.Combine(live, "data", "original.ibd"), "irreplaceable database");
            File.WriteAllText(Path.Combine(live, "mysql.exe"), "old runtime");
            var staging = EnvironmentInstaller.CreateMySqlStagingContainer(live);
            var staged = Directory.CreateDirectory(Path.Combine(staging, "mysql")).FullName;
            File.WriteAllText(Path.Combine(staged, "mysql.exe"), "new runtime");
            Exception? failure = null;
            try
            {
                using var transaction = EnvironmentInstaller.MySqlRuntimeTransaction.Commit(
                    staging, staged, live, Path.Combine(staging, "rollback"), (source, target) =>
                    {
                        if (source == staged)
                        {
                            Assert.AreEqual("irreplaceable database", File.ReadAllText(Path.Combine(staged, "data", "original.ibd")));
                            if (blockRecovery) File.WriteAllText(live, "injected target obstruction");
                            throw new IOException("injected activation failure");
                        }
                        Directory.Move(source, target);
                    });
            }
            catch (Exception error) { failure = error; }
            Assert.IsNotNull(failure);
            var databases = Directory.GetFiles(root, "original.ibd", SearchOption.AllDirectories);
            Assert.AreEqual(1, databases.Length, "The original database must never be deleted or duplicated during failed activation.");
            Assert.AreEqual("irreplaceable database", File.ReadAllText(databases[0]));
            if (blockRecovery)
            {
                Assert.IsInstanceOfType<AggregateException>(failure);
                Assert.IsTrue(Directory.Exists(staging), "An incomplete recovery must retain its backup.");
                StringAssert.Contains(failure.Message, staging);
            }
            else
            {
                Assert.AreEqual(Path.Combine(live, "data", "original.ibd"), databases[0]);
                Assert.AreEqual("old runtime", File.ReadAllText(Path.Combine(live, "mysql.exe")));
                Assert.IsFalse(Directory.Exists(staging));
            }
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void MySqlSuccessfulSwapCanCommitOrRollBackWithoutLosingData(bool commit)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var live = Path.Combine(root, "MySQL");
            Directory.CreateDirectory(Path.Combine(live, "data"));
            File.WriteAllText(Path.Combine(live, "data", "keep.ibd"), "database");
            File.WriteAllText(Path.Combine(live, "mysql.exe"), "old");
            var staging = EnvironmentInstaller.CreateMySqlStagingContainer(live);
            var staged = Directory.CreateDirectory(Path.Combine(staging, "mysql")).FullName;
            File.WriteAllText(Path.Combine(staged, "mysql.exe"), "new");
            using (var transaction = EnvironmentInstaller.MySqlRuntimeTransaction.Commit(staging, staged, live, Path.Combine(staging, "rollback")))
            {
                Assert.AreEqual("database", File.ReadAllText(Path.Combine(live, "data", "keep.ibd")));
                if (commit) transaction.Complete();
                else transaction.Rollback();
            }
            Assert.AreEqual(commit ? "new" : "old", File.ReadAllText(Path.Combine(live, "mysql.exe")));
            Assert.AreEqual("database", File.ReadAllText(Path.Combine(live, "data", "keep.ibd")));
            Assert.IsFalse(Directory.Exists(staging));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void MySqlRejectsBackupInsideThePayloadBeforeMovingAnExistingDatabase()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var live = Directory.CreateDirectory(Path.Combine(root, "MySQL")).FullName;
            File.WriteAllText(Path.Combine(live, "keep.ibd"), "database");
            var staging = Directory.CreateDirectory(Path.Combine(root, "staging")).FullName;
            Assert.ThrowsException<InvalidOperationException>(() => EnvironmentInstaller.MySqlRuntimeTransaction.Commit(
                staging, staging, live, Path.Combine(staging, "rollback")));
            Assert.AreEqual("database", File.ReadAllText(Path.Combine(live, "keep.ibd")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow("success")]
    [DataRow("prefix-sibling")]
    [DataRow("stop-failure")]
    [DataRow("process-alive")]
    [DataRow("reappeared")]
    [DataRow("pending-delete")]
    public async Task MySqlReinstallRequiresAConfirmedCleanShutdown(string scenario)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var live = Directory.CreateDirectory(Path.Combine(root, "MySQL")).FullName;
            var events = Path.Combine(root, "events.txt");
            File.WriteAllText(Path.Combine(live, "keep.ibd"), "database");
            var source = EnvironmentInstaller.BuildMySqlInstallCleanupScript(live);
            var mocks = $$"""
                $scenario = '{{scenario}}'
                $log = '{{PsTestPath(Path.Combine(root, "test.log"))}}'
                function Get-ComponentService($name) {
                    $root = '{{PsTestPath(live)}}'
                    if ($scenario -eq 'prefix-sibling') { $root += '-other' }
                    return [pscustomobject]@{ ImagePath = '"' + $root + '\bin\mysqld.exe" MySQL80' }
                }
                function Stop-ComponentService($name) {
                    Add-Content -LiteralPath '{{PsTestPath(events)}}' 'stop'
                    if ($scenario -eq 'stop-failure') { throw 'injected stop timeout' }
                }
                function Wait-ComponentProcesses($root, $names, $seconds) { return $scenario -ne 'process-alive' }
                function Remove-ComponentService($name) {
                    Add-Content -LiteralPath '{{PsTestPath(events)}}' 'remove'
                    if ($scenario -eq 'pending-delete') { throw 'injected pending deletion' }
                }
                function Get-ComponentProcesses($root, $names) {
                    if ($scenario -eq 'reappeared') { [pscustomobject]@{ ProcessId = 123 } }
                }
                function Stop-Process { throw 'A database must never be force-killed.' }
                """;
            source = source.Replace("Start-Transcript -Path $log -Append | Out-Null", mocks + "\nStart-Transcript -Path $log -Append | Out-Null");
            var result = await RunUninstallScriptTestAsync(root, source);
            Assert.AreEqual(scenario == "success" ? 0 : 1, result.ExitCode, result.CombinedOutput);
            Assert.AreEqual("database", File.ReadAllText(Path.Combine(live, "keep.ibd")));
            var calls = File.Exists(events) ? File.ReadAllLines(events) : Array.Empty<string>();
            var expected = scenario == "prefix-sibling" ? Array.Empty<string>() :
                scenario is "stop-failure" or "process-alive" ? new[] { "stop" } : new[] { "stop", "remove" };
            CollectionAssert.AreEqual(expected, calls);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MySqlMaintenanceUsesLegacyCompatibleShutdownAndNeverForceKills(bool remainsRunning)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var mysql = Path.Combine(root, "mysql.exe");
            File.WriteAllText(Path.Combine(root, "mysqladmin.exe"), "mock administrative client");
            var source = Path.Combine(root, "install.ps1");
            File.WriteAllText(source, EnvironmentInstaller.BuildMySqlScript(root, Path.Combine(root, "mysqld.exe"), mysql, "test-only"), new UTF8Encoding(true));
            var script = $$"""
                $ErrorActionPreference = 'Stop'
                $tokens=$null; $errors=$null
                $ast = [Management.Automation.Language.Parser]::ParseFile('{{PsTestPath(source)}}', [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw ($errors | Out-String) }
                $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Stop-TemporaryMySqlProcess' }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
                $mysql = '{{PsTestPath(mysql)}}'; $rootPassword = 'test-only'
                $script:process = [pscustomobject]@{ HasExited = $false; Id = 123 }
                $script:called = $false
                function Stop-Process { throw 'A database must not be force-killed.' }
                function Invoke-NativeQuiet($file, [string[]]$arguments) {
                    if ([IO.Path]::GetFileName($file) -ne 'mysqladmin.exe' -or $arguments[-1] -ne 'shutdown') { throw 'Wrong shutdown client/command.' }
                    $script:called = $true
                    $script:process.HasExited = ${{(!remainsRunning).ToString().ToLowerInvariant()}}
                    return 0
                }
                $failed = $false
                try { Stop-TemporaryMySqlProcess $script:process 0 } catch { $failed = $true }
                if (-not $script:called -or $failed -ne ${{remainsRunning.ToString().ToLowerInvariant()}}) { throw 'Unsafe maintenance shutdown result.' }
                """;
            var result = await RunUninstallScriptTestAsync(root, script);
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }
}
