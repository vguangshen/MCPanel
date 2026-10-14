using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ReadableButLockedChangedLibraryStopsBeforeAnyProgramFileChanges()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var library = Path.Combine(plan.InstallDirectory, "System.Buffers.dll");
            File.WriteAllText(library, "original runtime");
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "System.Buffers.dll"), "changed runtime");
            var config = File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config"));
            using (File.Open(library, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.ApplyTransaction(plan));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("original runtime", File.ReadAllText(library));
            CollectionAssert.AreEqual(config, File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void UnchangedLockedRuntimeLibraryDoesNotBlockTheUpdate()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var library = Path.Combine(plan.InstallDirectory, "System.Buffers.dll");
            File.WriteAllText(library, "same runtime");
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "System.Buffers.dll"), "same runtime");
            File.SetAttributes(library, FileAttributes.ReadOnly);
            using (File.Open(library, FileMode.Open, FileAccess.Read, FileShare.Read))
                ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("new", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("same runtime", File.ReadAllText(library));
            Assert.IsTrue((File.GetAttributes(library) & FileAttributes.ReadOnly) != 0);
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void LateLibraryLockRestoresRenamedFilesWithoutRemovingTheExecutable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var library = Path.Combine(plan.InstallDirectory, "System.Buffers.dll");
            File.WriteAllText(library, "original runtime");
            File.WriteAllText(Path.Combine(plan.InstallDirectory, "a.dll"), "original library");
            File.SetAttributes(Path.Combine(plan.InstallDirectory, "a.dll"), FileAttributes.ReadOnly);
            var config = File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config"));
            // Exercise the commit itself, as when a new lock appears after preflight.
            using (File.Open(library, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.CommitPreparedEntries(
                    plan.InstallDirectory, plan.PayloadDirectory, DiagnosticRollbackRoot(plan)));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
            Assert.AreEqual("original library", File.ReadAllText(Path.Combine(plan.InstallDirectory, "a.dll")));
            Assert.IsTrue((File.GetAttributes(Path.Combine(plan.InstallDirectory, "a.dll")) & FileAttributes.ReadOnly) != 0);
            Assert.AreEqual("original runtime", File.ReadAllText(library));
            CollectionAssert.AreEqual(config, File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void LateExecutableLockRollsBackDependenciesAndRetainsTheOldExecutable()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var executable = Path.Combine(plan.InstallDirectory, "MCPanel.exe");
            File.WriteAllText(Path.Combine(plan.InstallDirectory, "old.dll"), "old library");
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "new.dll"), "new library");
            File.WriteAllText(Path.Combine(plan.PayloadDirectory, "MCPanel.exe.config"), "<configuration><appSettings /></configuration>");
            var config = File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config"));
            using (File.Open(executable, FileMode.Open, FileAccess.Read, FileShare.Read))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.CommitPreparedEntries(
                    plan.InstallDirectory, plan.PayloadDirectory, DiagnosticRollbackRoot(plan)));
            Assert.AreEqual("old", File.ReadAllText(executable));
            Assert.AreEqual("old library", File.ReadAllText(Path.Combine(plan.InstallDirectory, "old.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(plan.InstallDirectory, "new.dll")));
            CollectionAssert.AreEqual(config, File.ReadAllBytes(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config")));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(DiagnosticRollbackRoot(plan), "MCPanel.exe")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void ReadOnlyExecutableIsReplacedAtomically()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var executable = Path.Combine(plan.InstallDirectory, "MCPanel.exe");
            File.SetAttributes(executable, FileAttributes.ReadOnly);
            ApplicationUpdateService.ApplyTransaction(plan);
            Assert.AreEqual("new", File.ReadAllText(executable));
            Assert.IsFalse(Directory.Exists(DiagnosticRollbackRoot(plan)));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void UpdateTransactionHonorsAnExistingInstallationLock()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var plan = CreateDiagnosticUpdatePlan(root);
            var lockFile = Path.Combine(plan.InstallDirectory, "StoreData", "Updates", "apply-update.lock");
            using (File.Open(lockFile, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
                Assert.ThrowsException<IOException>(() => ApplicationUpdateService.ApplyTransaction(plan));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(plan.InstallDirectory, "MCPanel.exe")));
        }
        finally { DeleteTemporaryTree(root); }
    }
}
