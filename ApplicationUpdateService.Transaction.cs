using System.IO;

namespace MCPanel;

public sealed partial class ApplicationUpdateService
{
    internal static void ApplyTransaction(ApplicationUpdatePlan plan)
    {
        ValidateTransactionPlan(plan);
        var updatesRoot = ComponentPaths.GetUpdatesRoot(plan.InstallDirectory);
        Directory.CreateDirectory(updatesRoot);
        using var transactionLock = new FileStream(Path.Combine(updatesRoot, "apply-update.lock"),
            FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        var rollbackRoot = Path.Combine(updatesRoot, "Rollback", "Current");
        var incomingRoot = Path.Combine(plan.StagingRoot, "Prepared-" + Guid.NewGuid().ToString("N"));
        try
        {
            // Finish all copying and configuration merging before touching the
            // installation. The original download remains available on failure.
            CopyReplaceableEntries(plan.PayloadDirectory, incomingRoot);
            DeploymentConfigurationSettings.Apply(Path.Combine(incomingRoot, "MCPanel.exe.config"),
                DeploymentConfigurationSettings.Capture(Path.Combine(plan.InstallDirectory, "MCPanel.exe.config")));
            var unchanged = FindUnchangedEntries(plan.InstallDirectory, incomingRoot);
            foreach (var entry in ReplaceableEntries(plan.InstallDirectory))
                if (!unchanged.Contains(Path.GetFileName(entry))) AssertUpdateEntryAvailable(entry);

            // A stale snapshot must never be mixed with this transaction.
            if (Directory.Exists(rollbackRoot)) DeleteUpdateEntry(rollbackRoot);
            Directory.CreateDirectory(rollbackRoot);
            CommitPreparedEntries(plan.InstallDirectory, incomingRoot, rollbackRoot, unchanged);
            TryDeleteDirectory(rollbackRoot);
        }
        finally
        {
            TryDeleteDirectory(incomingRoot);
        }
    }

    private static IEnumerable<string> ReplaceableEntries(string root) =>
        Directory.EnumerateFileSystemEntries(root)
            .Where(entry => !PreservedTopLevelNames.Contains(Path.GetFileName(entry)))
            .OrderBy(entry => Path.GetFileName(entry), StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> FindUnchangedEntries(string installRoot, string incomingRoot) =>
        new(ReplaceableEntries(incomingRoot)
            .Where(entry => !string.Equals(Path.GetFileName(entry), "MCPanel.exe", StringComparison.OrdinalIgnoreCase) &&
                UpdateEntriesEqual(entry, Path.Combine(installRoot, Path.GetFileName(entry))))
            .Select(Path.GetFileName), StringComparer.OrdinalIgnoreCase);

    private static bool UpdateEntriesEqual(string first, string second)
    {
        if (!File.Exists(second) && !Directory.Exists(second)) return false;
        var firstAttributes = File.GetAttributes(first);
        var secondAttributes = File.GetAttributes(second);
        if (((firstAttributes | secondAttributes) & FileAttributes.ReparsePoint) != 0) return false;
        if ((firstAttributes & FileAttributes.Directory) != (secondAttributes & FileAttributes.Directory)) return false;
        if ((firstAttributes & FileAttributes.Directory) == 0)
            return new FileInfo(first).Length == new FileInfo(second).Length &&
                string.Equals(ComputeSha256(first), ComputeSha256(second), StringComparison.OrdinalIgnoreCase);
        var firstChildren = Directory.GetFileSystemEntries(first);
        var secondChildren = Directory.GetFileSystemEntries(second);
        return firstChildren.Length == secondChildren.Length && firstChildren.All(child =>
            UpdateEntriesEqual(child, Path.Combine(second, Path.GetFileName(child))));
    }

    private static void AssertUpdateEntryAvailable(string entry)
    {
        var attributes = File.GetAttributes(entry);
        if ((attributes & FileAttributes.ReparsePoint) != 0) return;
        if ((attributes & FileAttributes.Directory) != 0)
        {
            foreach (var child in Directory.GetFileSystemEntries(entry)) AssertUpdateEntryAvailable(child);
            return;
        }
        try
        {
            using var probe = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (IOException error)
        {
            throw new IOException($"程序文件正在被占用，更新已停止，原程序未修改：{entry}", error);
        }
    }

    // Kept separate from preflight because locks can also appear after the
    // check. Every completed rename has a corresponding rollback operation.
    internal static void CommitPreparedEntries(string installRoot, string incomingRoot, string rollbackRoot,
        HashSet<string>? unchanged = null)
    {
        unchanged ??= FindUnchangedEntries(installRoot, incomingRoot);
        Directory.CreateDirectory(rollbackRoot);
        var executable = Path.Combine(installRoot, "MCPanel.exe");
        var executableAttributes = File.GetAttributes(executable);
        File.Copy(executable, Path.Combine(rollbackRoot, "MCPanel.exe"), false);
        var retired = new List<string>();
        var installed = new List<string>();
        try
        {
            foreach (var entry in ReplaceableEntries(installRoot).ToArray())
            {
                var name = Path.GetFileName(entry);
                if (unchanged.Contains(name) || name.Equals("MCPanel.exe", StringComparison.OrdinalIgnoreCase)) continue;
                MoveUpdateEntry(entry, Path.Combine(rollbackRoot, name));
                retired.Add(name);
            }
            foreach (var entry in ReplaceableEntries(incomingRoot).ToArray())
            {
                var name = Path.GetFileName(entry);
                if (unchanged.Contains(name) || name.Equals("MCPanel.exe", StringComparison.OrdinalIgnoreCase)) continue;
                MoveUpdateEntry(entry, Path.Combine(installRoot, name));
                installed.Add(name);
            }

            // Never unlink MCPanel.exe. The final program switch is one atomic
            // operation, after every dependency and configuration is ready.
            if ((executableAttributes & FileAttributes.ReadOnly) != 0)
                File.SetAttributes(executable, executableAttributes & ~FileAttributes.ReadOnly);
            File.Replace(Path.Combine(incomingRoot, "MCPanel.exe"), executable, null);
        }
        catch (Exception error)
        {
            var failures = new List<Exception> { error };
            foreach (var name in installed.AsEnumerable().Reverse())
            {
                try { DeleteUpdateEntry(Path.Combine(installRoot, name)); }
                catch (Exception restoreError) { failures.Add(restoreError); }
            }
            // Restore every retired entry even when another entry cannot be
            // restored. Unchanged/locked program files are never deleted.
            foreach (var name in retired)
            {
                try { MoveUpdateEntry(Path.Combine(rollbackRoot, name), Path.Combine(installRoot, name)); }
                catch (Exception restoreError) { failures.Add(restoreError); }
            }
            try { File.SetAttributes(executable, executableAttributes); }
            catch (Exception restoreError) { failures.Add(restoreError); }
            if (failures.Count > 1)
                throw new AggregateException($"更新失败且部分文件未能恢复。主程序未删除；请保留回滚目录：{rollbackRoot}", failures);
            throw;
        }
    }

    private static void MoveUpdateEntry(string source, string destination)
    {
        if (Directory.Exists(source)) Directory.Move(source, destination);
        else File.Move(source, destination);
    }
}
