using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void UpdatePreservesLockedReadOnlyIisDiagnostics()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var log = WriteDiagnosticFixture(plan.InstallDirectory);
            File.SetAttributes(log, FileAttributes.ReadOnly);
            using (File.Open(log, FileMode.Open, FileAccess.Read, FileShare.None))
                ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("IIS diagnostics", File.ReadAllText(log));
            Assert.IsTrue((File.GetAttributes(log) & FileAttributes.ReadOnly) != 0);
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void UpdateClearsReadOnlyFilesFromFailedDiagnosticRollback()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var log = WriteDiagnosticFixture(plan.InstallDirectory);
            var staleLog = WriteDiagnosticFixture(DiagnosticRollbackRoot(plan));
            File.SetAttributes(log, FileAttributes.ReadOnly);
            File.SetAttributes(staleLog, FileAttributes.ReadOnly);
            ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("IIS diagnostics", File.ReadAllText(log));
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void LockedOldRollbackStopsBeforeInstalledFilesAreTouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            Directory.CreateDirectory(DiagnosticRollbackRoot(plan));
            var locked = Path.Combine(DiagnosticRollbackRoot(plan), "locked.dll");
            File.WriteAllText(locked, "previous snapshot");
            using (File.Open(locked, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.ApplyTransaction(plan));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("previous snapshot", File.ReadAllText(locked));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void UpdateReplacesReadOnlyProgramFilesAndCleansTheirBackup()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var program = Path.Combine(plan.InstallDirectory, "protected.dll");
            File.WriteAllText(program, "old-library");
            File.SetAttributes(program, FileAttributes.ReadOnly);
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "protected.dll"), "new-library");
            ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("new-library", File.ReadAllText(program));
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void FailedReplacementRestoresReadOnlyOriginalProgramFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var original = Path.Combine(plan.InstallDirectory, "protected.dll");
            File.WriteAllText(original, "old-library");
            File.SetAttributes(original, FileAttributes.ReadOnly);
            var blocked = Path.Combine(plan.PayloadDirectory, "new.dll");
            File.WriteAllText(blocked, "new-library");
            using (File.Open(blocked, FileMode.Open, FileAccess.Read, FileShare.None))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.ApplyTransaction(plan));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("old-library", File.ReadAllText(original));
            Assert.IsTrue((File.GetAttributes(original) & FileAttributes.ReadOnly) != 0);
            Assert.IsFalse(File.Exists(Path.Combine(plan.InstallDirectory, "new.dll")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void OlderLayoutManifestStillProtectsDiagnosticsAndCustomData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            File.WriteAllText(Path.Combine(root, "deployment-layout.json"),
                "{\"preservedTopLevelNames\":[\"StoreData\",\"CustomData\"]}");
            var names = DeploymentLayoutManifest.LoadPreservedTopLevelNames(root);
            Assert.IsTrue(names.Contains("Diagnostics"));
            Assert.IsTrue(names.Contains("web"));
            Assert.IsTrue(names.Contains("CustomData"));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task LegacyUpdateRepairPreviewsAndMovesFilesWithoutDeletingData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var log = WriteDiagnosticFixture(plan.InstallDirectory);
            var rollbackLog = WriteDiagnosticFixture(DiagnosticRollbackRoot(plan));
            File.SetAttributes(log, FileAttributes.ReadOnly);
            File.SetAttributes(rollbackLog, FileAttributes.ReadOnly);
            var config = File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config"));
            var helper = Path.Combine(root, "Repair-Update.ps1");
            File.WriteAllText(helper, ReadRepositoryFile("Repair-Update.ps1"));
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var script = Path.Combine(root, "repair-test.ps1");
            File.WriteAllText(script, "$ErrorActionPreference='Stop'\n" +
                "& " + Quote(helper) + " -InstallDirectory " + Quote(plan.InstallDirectory) + " -WhatIf\n" +
                "if (!(Test-Path -LiteralPath " + Quote(log) + ")) { throw 'Preview moved diagnostics' }\n" +
                "& " + Quote(helper) + " -InstallDirectory " + Quote(plan.InstallDirectory) + "\n" +
                "& " + Quote(helper) + " -InstallDirectory " + Quote(plan.InstallDirectory) + "\n");
            var result = await ProcessRunner.RunPowerShellFileAsync(script, false, default,
                captureOutput: true, timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.IsFalse(Directory.Exists(Path.Combine(plan.InstallDirectory, "Diagnostics")));
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
            var preserved = Directory.GetFiles(Path.Combine(plan.InstallDirectory, "StoreData"), "IIS-config.xml", SearchOption.AllDirectories);
            Assert.AreEqual(2, preserved.Length);
            foreach (var file in preserved)
            {
                Assert.AreEqual("IIS diagnostics", File.ReadAllText(file));
                Assert.IsTrue((File.GetAttributes(file) & FileAttributes.ReadOnly) != 0);
            }
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            CollectionAssert.AreEqual(config, File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    private static ApplicationUpdatePlan CreateDiagnosticUpdatePlan(string root)
    {
        var install = Path.Combine(root, "install");
        var staging = Path.Combine(install, "StoreData", "Updates", "Staging", Guid.NewGuid().ToString("N"));
        var payload = Path.Combine(staging, "Payload");
        Directory.CreateDirectory(payload);
        File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
        File.WriteAllText(Path.Combine(install, "MCPanel.exe.config"), "<configuration />");
        File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
        File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"), "<configuration />");
        return new ApplicationUpdatePlan
        {
            InstallDirectory = install, PayloadDirectory = payload,
            StagingRoot = staging, MainExecutableName = "MCPanel.exe", TargetVersion = "1.3.60"
        };
    }

    private static string DiagnosticRollbackRoot(ApplicationUpdatePlan plan) =>
        Path.Combine(plan.InstallDirectory, "StoreData", "Updates", "Rollback", "Current");

    private static string WriteDiagnosticFixture(string root)
    {
        var directory = Path.Combine(root, "Diagnostics", "iis-20261010-060645");
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "IIS-config.xml");
        File.WriteAllText(file, "IIS diagnostics");
        return file;
    }
}
