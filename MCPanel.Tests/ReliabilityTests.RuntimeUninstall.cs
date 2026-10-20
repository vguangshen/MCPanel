using System;
using System.Diagnostics;
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
    public async Task RuntimeCleanupRemovesReadOnlyNginxTempDirectoriesAndFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(root, "nginx-1.14.2");
            var temp = Directory.CreateDirectory(Path.Combine(target, "ajp_temp"));
            var file = Path.Combine(temp.FullName, "cached-response");
            File.WriteAllText(file, "temporary response");
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System);
            temp.Attributes |= FileAttributes.ReadOnly;
            new DirectoryInfo(target).Attributes |= FileAttributes.ReadOnly;
            var result = await RunRuntimeCleanupTestAsync(root, $"Remove-ComponentDirectory '{PsTestPath(target)}'");
            AssertPowerShellSuccess(result);
            Assert.IsFalse(Directory.Exists(target));
        }
        finally { DeleteRuntimeUninstallTestTree(root); }
    }

    [TestMethod]
    public async Task RuntimeCleanupReportsLockedFilePreservesInstallMarkerAndAllowsRetry()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "nginx")).FullName;
            var locked = Path.Combine(target, "access.log");
            var marker = Path.Combine(target, "nginx.exe");
            File.WriteAllText(locked, "in use");
            File.WriteAllText(marker, "install marker");
            using (File.Open(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var failure = await RunRuntimeCleanupTestAsync(root, $"Remove-ComponentDirectory '{PsTestPath(target)}'");
                Assert.AreNotEqual(0, failure.ExitCode);
                StringAssert.Contains(failure.StandardOutput + failure.StandardError, target);
                Assert.IsTrue(File.Exists(locked));
                Assert.IsTrue(File.Exists(marker), "A failed cleanup must remain discoverable for another uninstall attempt.");
            }
            AssertPowerShellSuccess(await RunRuntimeCleanupTestAsync(root, $"Remove-ComponentDirectory '{PsTestPath(target)}'"));
            Assert.IsFalse(Directory.Exists(target));
        }
        finally { DeleteRuntimeUninstallTestTree(root); }
    }

    [TestMethod]
    public async Task RuntimeCleanupUnlinksJunctionWithoutChangingExternalFilesOrAttributes()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Directory.CreateDirectory(Path.Combine(root, "nginx")).FullName;
            var outside = Directory.CreateDirectory(Path.Combine(root, "unrelated-data")).FullName;
            var preserved = Path.Combine(outside, "keep.txt");
            File.WriteAllText(preserved, "external data");
            File.SetAttributes(preserved, FileAttributes.ReadOnly);
            var link = Path.Combine(target, "ajp_temp");
            var createLink = ProcessRunner.RunSynchronously("cmd.exe",
                $"/d /c mklink /J {Compat.QuoteCommandLineArgument(link)} {Compat.QuoteCommandLineArgument(outside)}",
                root, captureOutput: true, timeout: TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, createLink.ExitCode, createLink.StandardOutput + createLink.StandardError);
            AssertPowerShellSuccess(await RunRuntimeCleanupTestAsync(root, $"Remove-ComponentDirectory '{PsTestPath(target)}'"));
            Assert.IsFalse(Directory.Exists(target));
            Assert.AreEqual("external data", File.ReadAllText(preserved));
            Assert.IsTrue((File.GetAttributes(preserved) & FileAttributes.ReadOnly) != 0);
        }
        finally { DeleteRuntimeUninstallTestTree(root); }
    }

    [TestMethod]
    public async Task ElevatedRuntimeCleanupRepairsOnlyDeniedComponentEntry()
    {
        if (!ProcessRunner.IsAdministrator()) Assert.Inconclusive("This ACL integration test needs an administrator token.");
        var root = CreateTemporaryDirectory();
        var denied = Path.Combine(root, "component", "ajp_temp");
        DirectorySecurity? original = null;
        try
        {
            Directory.CreateDirectory(denied);
            var sibling = Directory.CreateDirectory(Path.Combine(root, "other-component")).FullName;
            var siblingAcl = Directory.GetAccessControl(sibling).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
            original = Directory.GetAccessControl(denied);
            var acl = Directory.GetAccessControl(denied);
            acl.AddAccessRule(new FileSystemAccessRule(WindowsIdentity.GetCurrent().User!,
                FileSystemRights.WriteAttributes, AccessControlType.Deny));
            Directory.SetAccessControl(denied, acl);
            var cleanup = await RunRuntimeCleanupTestAsync(root,
                $"Remove-ComponentDirectory '{PsTestPath(Path.GetDirectoryName(denied)!)}'");
            Console.WriteLine(cleanup.StandardOutput + Environment.NewLine + cleanup.StandardError);
            AssertPowerShellSuccess(cleanup);
            Assert.IsFalse(Directory.Exists(Path.GetDirectoryName(denied)));
            Assert.AreEqual(siblingAcl, Directory.GetAccessControl(sibling).GetSecurityDescriptorSddlForm(AccessControlSections.Access));
        }
        finally
        {
            if (original is not null && Directory.Exists(denied))
            {
                var restored = new DirectorySecurity();
                restored.SetSecurityDescriptorSddlForm(original.GetSecurityDescriptorSddlForm(AccessControlSections.Access),
                    AccessControlSections.Access);
                Directory.SetAccessControl(denied, restored);
            }
            DeleteRuntimeUninstallTestTree(root);
        }
    }

    [DataTestMethod]
    [DataRow("mysql")]
    [DataRow("mysql-legacy")]
    [DataRow("nginx")]
    public async Task ManagedUninstallStopsAndRemovesServiceBeforeDeletingReadOnlyFiles(string component)
    {
        var root = CreateManagedUninstallFixture(component);
        var work = CreateTemporaryDirectory();
        try
        {
            var events = Path.Combine(work, "events.txt");
            var script = BuildUninstallFixtureScript(component.StartsWith("mysql") ? "mysql" : component, root, events, "success");
            AssertPowerShellSuccess(await RunUninstallScriptTestAsync(work, script));
            CollectionAssert.AreEqual(new[] { "stop", "remove" }, File.ReadAllLines(events));
            Assert.IsFalse(Directory.Exists(root));
        }
        finally { DeleteRuntimeUninstallTestTree(root); DeleteRuntimeUninstallTestTree(work); }
    }

    [DataTestMethod]
    [DataRow("foreign")]
    [DataRow("prefix-sibling")]
    [DataRow("stop-failure")]
    [DataRow("process-alive")]
    [DataRow("pending-delete")]
    public async Task MySqlUninstallPreservesFilesWhenServiceOrProcessIsUnsafe(string scenario)
    {
        var root = CreateManagedUninstallFixture("mysql");
        var work = CreateTemporaryDirectory();
        try
        {
            var events = Path.Combine(work, "events.txt");
            var failure = await RunUninstallScriptTestAsync(work, BuildUninstallFixtureScript("mysql", root, events, scenario));
            Assert.AreNotEqual(0, failure.ExitCode, failure.StandardOutput + failure.StandardError);
            Assert.IsTrue(File.Exists(Path.Combine(root, "bin", "mysql.exe")));
            Assert.AreEqual("database", File.ReadAllText(Path.Combine(root, "data", "keep.ibd")));
            var calls = File.Exists(events) ? File.ReadAllLines(events) : Array.Empty<string>();
            if (scenario is "foreign" or "prefix-sibling") Assert.AreEqual(0, calls.Length);
            else if (scenario != "pending-delete") CollectionAssert.AreEqual(new[] { "stop" }, calls);
            else CollectionAssert.AreEqual(new[] { "stop", "remove" }, calls);
        }
        finally { DeleteRuntimeUninstallTestTree(root); DeleteRuntimeUninstallTestTree(work); }
    }

    [DataTestMethod]
    [DataRow("shared")]
    [DataRow("outside")]
    public async Task UninstallRejectsUnsafeRootBeforeTouchingServices(string scenario)
    {
        var work = CreateTemporaryDirectory();
        try
        {
            var target = scenario == "shared" ? ComponentPaths.RuntimeRoot : work;
            var events = Path.Combine(work, "events.txt");
            var script = EnvironmentRuntimeService.BuildNginxUninstallScript(target, "uninstall-guard-test.log");
            var mocks = $"function Get-ComponentService($name) {{ Add-Content -LiteralPath '{PsTestPath(events)}' 'queried'; throw 'service should not be queried' }}\n";
            script = InjectUninstallMocks(script, "$targetRoot = Assert-ComponentRoot", mocks);
            Assert.AreNotEqual(0, (await RunUninstallScriptTestAsync(work, script)).ExitCode);
            Assert.IsFalse(File.Exists(events));
        }
        finally { DeleteRuntimeUninstallTestTree(work); }
    }

    [TestMethod]
    public async Task ComponentServiceDeletionClosesScmHandlesBeforeWaitingForRemoval()
    {
        if (!ProcessRunner.IsAdministrator()) Assert.Inconclusive("Service integration needs an administrator token.");
        var root = CreateTemporaryDirectory();
        var name = "MCPanelUninstallTest" + Guid.NewGuid().ToString("N");
        try
        {
            var create = ProcessRunner.RunSynchronously("sc.exe",
                $"create {name} binPath= {Compat.QuoteCommandLineArgument(Path.Combine(Environment.SystemDirectory, "cmd.exe"))} start= demand",
                root, captureOutput: true, timeout: TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, create.ExitCode, create.StandardOutput + create.StandardError);
            AssertPowerShellSuccess(await RunRuntimeCleanupTestAsync(root,
                $"$service = Get-ComponentService '{name}'\nif (-not $service) {{ throw 'test service missing' }}\n" +
                $"Stop-ComponentService '{name}'\nRemove-ComponentService '{name}'\nif (Get-ComponentService '{name}') {{ throw 'service was not deleted' }}"));
        }
        finally
        {
            ProcessRunner.RunSynchronously("sc.exe", "delete " + name, root, captureOutput: true, timeout: TimeSpan.FromSeconds(10));
            DeleteRuntimeUninstallTestTree(root);
        }
    }

    private static string CreateManagedUninstallFixture(string component)
    {
        var root = component == "mysql-legacy"
            ? Path.Combine(ComponentPaths.RuntimeRoot, "mysql-uninstall-test-" + Guid.NewGuid().ToString("N"))
            : Path.Combine(component == "mysql" ? ComponentPaths.MySqlRoot : ComponentPaths.NginxRoot,
                "uninstall-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (component.StartsWith("mysql"))
        {
            Directory.CreateDirectory(Path.Combine(root, "bin"));
            File.WriteAllText(Path.Combine(root, "bin", "mysql.exe"), "client marker");
            File.WriteAllText(Path.Combine(root, "bin", "mysqld.exe"), "server marker");
            Directory.CreateDirectory(Path.Combine(root, "data"));
            File.WriteAllText(Path.Combine(root, "data", "keep.ibd"), "database");
        }
        else File.WriteAllText(Path.Combine(root, "nginx.exe"), "server marker");
        var temp = Directory.CreateDirectory(Path.Combine(root, "ajp_temp"));
        temp.Attributes |= FileAttributes.ReadOnly;
        return root;
    }

    private static string BuildUninstallFixtureScript(string component, string root, string events, string scenario)
    {
        var image = component == "mysql"
            ? Compat.QuoteCommandLineArgument(Path.Combine(root, "bin", "mysqld.exe"))
            : NginxWindowsServiceManager.BuildServiceImagePath(Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe"), root);
        if (scenario == "foreign") image = @"C:\UnrelatedDatabase\bin\mysqld.exe";
        if (scenario == "prefix-sibling") image = Path.Combine(ComponentPaths.MySqlRoot + "-foreign", "bin", "mysqld.exe");
        var script = component == "mysql"
            ? EnvironmentRuntimeService.BuildMySqlUninstallScript(root, "uninstall-mysql-fixture.log")
            : EnvironmentRuntimeService.BuildNginxUninstallScript(root, "uninstall-nginx-fixture.log");
        var mocks = $$"""
            function Get-ComponentService($name) { return [pscustomobject]@{ Status='Running'; ImagePath='{{PsTestPath(image)}}' } }
            function Stop-ComponentService($name) {
                Add-Content -LiteralPath '{{PsTestPath(events)}}' 'stop'
                if ('{{scenario}}' -eq 'stop-failure') { throw 'simulated stop failure' }
            }
            function Remove-ComponentService($name) {
                if (-not (Test-Path -LiteralPath '{{PsTestPath(root)}}')) { throw 'files removed before service' }
                Add-Content -LiteralPath '{{PsTestPath(events)}}' 'remove'
                if ('{{scenario}}' -eq 'pending-delete') { throw 'simulated pending deletion' }
            }
            function Get-ComponentProcesses($root, $names) { }
            function Wait-ComponentProcesses($root, $names, $seconds) { return '{{scenario}}' -ne 'process-alive' }
            """;
        return InjectUninstallMocks(script, component == "mysql" ? "$mysqlInstallationRoots = @(" : "$targetRoot = Assert-ComponentRoot", mocks);
    }

    private static string InjectUninstallMocks(string script, string marker, string mocks)
    {
        var index = script.IndexOf(marker, StringComparison.Ordinal);
        Assert.IsTrue(index >= 0);
        return script.Insert(index, mocks + Environment.NewLine);
    }

    private static void DeleteRuntimeUninstallTestTree(string path)
    {
        if (!Directory.Exists(path)) return;
        foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(directory, FileAttributes.Directory);
        File.SetAttributes(path, FileAttributes.Directory);
        DeleteTemporaryTree(path);
    }

    private static string PsTestPath(string path) => path.Replace("'", "''");
    private static Task<ProcessRunResult> RunRuntimeCleanupTestAsync(string work, string body) =>
        RunUninstallScriptTestAsync(work, "$ErrorActionPreference='Stop'\n" + EnvironmentRuntimeService.RuntimeUninstallFunctions + "\n" + body);

    private static async Task<ProcessRunResult> RunUninstallScriptTestAsync(string work, string script)
    {
        var file = Path.Combine(work, "test-uninstall.ps1");
        File.WriteAllText(file, script, new UTF8Encoding(true));
        return await ProcessRunner.RunPowerShellFileAsync(file, elevated: false, CancellationToken.None,
            captureOutput: true, timeout: TimeSpan.FromSeconds(45));
    }

    private static void AssertPowerShellSuccess(ProcessRunResult result) =>
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + Environment.NewLine + result.StandardError);
}
