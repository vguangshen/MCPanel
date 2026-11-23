using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void InstallDirectoryHardeningRemovesBroadWritersAndKeepsOwnerAndIis()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "acl-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "MCPanel.exe"), string.Empty);
            var authenticatedUsers = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
            var seeded = Directory.GetAccessControl(root);
            seeded.AddAccessRule(new FileSystemAccessRule(
                authenticatedUsers,
                FileSystemRights.Modify,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None,
                AccessControlType.Allow));
            Directory.SetAccessControl(root, seeded);

            using var identity = WindowsIdentity.GetCurrent();
            InstallDirectorySecurity.Harden(root, identity.User);

            var rules = Directory.GetAccessControl(root)
                .GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>()
                .ToArray();
            Assert.IsTrue(Directory.GetAccessControl(root).AreAccessRulesProtected, "安装目录不应再继承磁盘根目录权限。");
            foreach (var broad in InstallDirectorySecurity.BroadWriterGroups)
            {
                Assert.IsFalse(
                    rules.Any(rule => rule.IdentityReference.Equals(broad) &&
                        rule.AccessControlType == AccessControlType.Allow &&
                        (rule.FileSystemRights & (FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete)) != 0),
                    $"{broad} 不应保留写入权限。");
            }

            Assert.IsTrue(rules.Any(rule => rule.IdentityReference.Equals(identity.User) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify));
            Assert.IsTrue(rules.Any(rule => rule.IdentityReference.Equals(InstallDirectorySecurity.Users) &&
                (rule.FileSystemRights & FileSystemRights.ReadAndExecute) == FileSystemRights.ReadAndExecute));

            var webRules = Directory.GetAccessControl(Path.Combine(root, "web"))
                .GetAccessRules(includeExplicit: true, includeInherited: false, typeof(SecurityIdentifier))
                .Cast<FileSystemAccessRule>();
            Assert.IsTrue(webRules.Any(rule => rule.IdentityReference.Equals(InstallDirectorySecurity.IisUsers) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void InstallDirectoryHardeningRejectsGroupsAsOwnerAndProtectedRoots()
    {
        Assert.IsFalse(InstallDirectorySecurity.IsAcceptableOwner(new SecurityIdentifier(WellKnownSidType.WorldSid, null)));
        Assert.IsFalse(InstallDirectorySecurity.IsAcceptableOwner(new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null)));
        Assert.IsFalse(InstallDirectorySecurity.IsAcceptableOwner(new SecurityIdentifier("S-1-5-21-1-2-3-513")));
        using var identity = WindowsIdentity.GetCurrent();
        Assert.IsTrue(InstallDirectorySecurity.IsAcceptableOwner(identity.User));

        Assert.ThrowsException<InvalidOperationException>(() =>
            InstallDirectorySecurity.ValidateInstallRoot(Path.GetPathRoot(Path.GetTempPath())!));
        Assert.ThrowsException<InvalidOperationException>(() =>
            InstallDirectorySecurity.ValidateInstallRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)));
    }

    [TestMethod]
    public void ElevatedUpdaterReverifiesPackageAndRejectsTampering()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "upd-" + Guid.NewGuid().ToString("N"));
        var install = Path.Combine(root, "Install");
        Directory.CreateDirectory(install);
        try
        {
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), string.Empty);
            var downloads = Path.Combine(ComponentPaths.GetUpdatesRoot(install), "Downloads");
            Directory.CreateDirectory(downloads);
            var package = Path.Combine(downloads, "MCPanel-test.zip");
            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "MCPanel.exe"), "MCPanel.exe");
                archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config"), "MCPanel.exe.config");
            }

            var copy = Path.Combine(root, "hash-copy.zip");
            var hash = ApplicationUpdateService.CopyAndComputeSha256(package, copy);
            var version = ApplicationUpdateService.CurrentVersion.ToString();

            var plan = new ApplicationUpdatePlan
            {
                InstallDirectory = install,
                PackageFile = package,
                TargetVersion = version,
                ParentProcessId = 1
            };
            ApplicationUpdateService.StageVerifiedPayload(plan, hash);
            Assert.IsTrue(File.Exists(Path.Combine(plan.PayloadDirectory, "MCPanel.exe")));
            StringAssert.StartsWith(
                Path.GetFullPath(plan.StagingRoot),
                Path.GetFullPath(Path.Combine(ComponentPaths.GetUpdatesRoot(install), "Staging")));

            // Tampering after authorization: the pinned hash no longer matches.
            File.AppendAllText(package, "tampered");
            var tamperedPlan = new ApplicationUpdatePlan
            {
                InstallDirectory = install,
                PackageFile = package,
                TargetVersion = version,
                ParentProcessId = 1
            };
            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.StageVerifiedPayload(tamperedPlan, hash));
            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.StageVerifiedPayload(tamperedPlan, string.Empty));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CapturedConsoleOutputUsesSystemCodePage()
    {
        // sc.exe prints a localized message; decoding it as UTF-8 on a
        // non-UTF-8 code page produced U+FFFD replacement characters.
        var result = ProcessRunner.RunSynchronously(
            "sc.exe",
            "query MCPanelNoSuchService" + Guid.NewGuid().ToString("N"),
            Path.GetTempPath(),
            captureOutput: true,
            timeout: TimeSpan.FromSeconds(10));
        Assert.AreEqual(1060, result.ExitCode);
        StringAssert.Contains(result.CombinedOutput, "1060");
        Assert.IsFalse(result.CombinedOutput.Contains('�'), result.CombinedOutput);
    }

    [TestMethod]
    public void ShippedScriptsAvoidPowerShell3OnlyComparisonOperators()
    {
        var sourceRoot = FindRepositoryRoot();
        foreach (var relative in new[] { "Uninstall-MCPanel.ps1", @"Installer\RuntimeCleanup.ps1" })
        {
            var text = File.ReadAllText(Path.Combine(sourceRoot, relative));
            Assert.IsFalse(System.Text.RegularExpressions.Regex.IsMatch(text, @"\s-(not)?in\s"),
                $"{relative} 必须能被 Windows PowerShell 2.0 解析，才能显示版本提示。");
        }

        StringAssert.Contains(
            File.ReadAllText(Path.Combine(sourceRoot, "Uninstall-MCPanel.ps1")),
            "$PSVersionTable.PSVersion.Major -lt 3");
    }

    [TestMethod]
    public void InstallLogReaderDecodesLegacyCodePageAndBomTails()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            const string message = "SQL Server 安装失败：找不到指定的实例";

            // Legacy code-page log (GBK on Chinese Windows).
            var legacy = Path.Combine(root, "legacy.log");
            var legacyBytes = System.Text.Encoding.Default.GetBytes(message);
            File.WriteAllBytes(legacy, legacyBytes);
            // On a non-Chinese code page the CJK text round-trips as '?'.
            Assert.AreEqual(System.Text.Encoding.Default.GetString(legacyBytes), EnvironmentInstaller.ReadTextBestEffort(legacy));

            // UTF-16 log larger than the tail window, with an odd padding so
            // the tail offset would land in the middle of a character.
            var utf16 = Path.Combine(root, "utf16.log");
            var body = new string('x', 2049) + Environment.NewLine + message;
            File.WriteAllText(utf16, body, System.Text.Encoding.Unicode);
            StringAssert.EndsWith(EnvironmentInstaller.ReadTextBestEffort(utf16, maximumBytes: 1001), message);

            // UTF-8 with BOM, tail cut inside a multi-byte character.
            var utf8 = Path.Combine(root, "utf8.log");
            File.WriteAllText(utf8, new string('中', 1000) + message, new System.Text.UTF8Encoding(true));
            var tail = EnvironmentInstaller.ReadTextBestEffort(utf8, maximumBytes: 1000);
            StringAssert.EndsWith(tail, message);
            Assert.IsFalse(tail.Contains('\uFFFD'), tail);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void TomcatJavaArgumentsPinRedirectedOutputEncoding()
    {
        var arguments = TomcatProductInstanceManager.BuildTomcatJavaArguments(@"C:\Tomcat", @"C:\Tomcat\base", null);
        StringAssert.Contains(arguments, "-Dstdout.encoding=UTF-8");
        StringAssert.Contains(arguments, "-Dstderr.encoding=UTF-8");
    }

    [TestMethod]
    public void SqlServerScriptsUseFirewallComApiAvailableOnWindows7()
    {
        var sourceRoot = FindRepositoryRoot();
        foreach (var file in new[] { "EnvironmentInstaller.SqlServer.cs", "EnvironmentRuntimeService.SqlServer.cs" })
        {
            var text = File.ReadAllText(Path.Combine(sourceRoot, file));
            Assert.IsFalse(text.Contains("NetFirewallRule"), $"{file} 仍在使用 Windows 8+ 才有的防火墙 cmdlet。");
            StringAssert.Contains(text, "{{WindowsFirewallScript.Functions}}");
        }

        var modernInstallBody = File.ReadAllText(Path.Combine(sourceRoot, "Installer", "SqlServerModernInstall.ps1"));
        Assert.IsFalse(modernInstallBody.Contains("NetFirewallRule"), "SqlServerModernInstall.ps1 仍在使用 Windows 8+ 才有的防火墙 cmdlet。");

        StringAssert.Contains(WindowsFirewallScript.Functions, "HNetCfg.FwPolicy2");
    }

    [TestMethod]
    public void TreeKillStopsTheRootProcess()
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "ping.exe"),
            Arguments = "-n 60 127.0.0.1",
            UseShellExecute = false,
            CreateNoWindow = true
        })!;
        System.Diagnostics.LegacyProcessExtensions.Kill(process, entireProcessTree: true);
        Assert.IsTrue(process.WaitForExit(5000), "Kill(entireProcessTree: true) 返回后进程必须已退出。");
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("找不到 MCPanel 源码目录。");
    }
}
