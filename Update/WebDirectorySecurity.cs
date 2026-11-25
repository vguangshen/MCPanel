using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;

namespace MCPanel;

/// <summary>
/// Keep runtime and panel-operator write access within the managed web tree.
/// Preserve other ACL entries and inheritance settings.
/// </summary>
internal static class WebDirectorySecurity
{
    internal const string RepairArgument = "--repair-managed-web-permissions";
    // v3 also grants IUSR on IIS product folders (impersonating ASP.NET sites).
    private const string MigrationFile = "web-permissions-v3.completed";

    /// <summary>IIS's anonymous user, which impersonating ASP.NET pages run file access as.</summary>
    internal static readonly SecurityIdentifier AnonymousIisUser = new("S-1-5-17");
    private static string MarkerPath => Path.Combine(ComponentPaths.StoreDataRoot, MigrationFile);
    private static string LogPath => PanelLog.WorkLog("web-permissions.log");
    private const InheritanceFlags DirectoryInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    internal static bool IsManagedProductPath(string webRoot, string path)
    {
        var root = Path.GetFullPath(webRoot).TrimEnd('\\', '/');
        var full = Path.GetFullPath(path).TrimEnd('\\', '/');
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return false;
        var downloads = Path.Combine(root, ".downloads");
        return !full.Equals(downloads, StringComparison.OrdinalIgnoreCase) &&
            !full.StartsWith(downloads + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    internal static void RejectReparseAncestors(string path)
    {
        for (DirectoryInfo? directory = new DirectoryInfo(Path.GetFullPath(path)); directory is not null; directory = directory.Parent)
        {
            if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("拒绝通过重解析点修改网站目录权限：" + directory.FullName);
        }
    }

    internal static IReadOnlyList<SecurityIdentifier> IncludePanelOperators(
        string installRoot, IEnumerable<SecurityIdentifier> accounts)
    {
        // Tomcat launches under the Windows panel operator, not necessarily
        // the elevated worker that installed the product.
        var operators = Directory.GetAccessControl(installRoot, AccessControlSections.Access)
            .GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>()
            .Where(rule => rule.AccessControlType == AccessControlType.Allow &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                (rule.InheritanceFlags & DirectoryInheritance) == DirectoryInheritance &&
                rule.PropagationFlags == PropagationFlags.None &&
                InstallDirectorySecurity.IsAcceptableOwner((SecurityIdentifier)rule.IdentityReference))
            .Select(rule => (SecurityIdentifier)rule.IdentityReference);
        return accounts.Concat(operators)
            .Concat(new[] { InstallDirectorySecurity.Administrators, InstallDirectorySecurity.LocalSystem })
            .Distinct().ToArray();
    }

    internal static void EnsureTreeAccess(string root, IEnumerable<SecurityIdentifier> accounts,
        bool repairProtectedChildren)
    {
        RejectReparseAncestors(root);
        var identities = accounts.Distinct().ToArray();
        var rootDirectory = new DirectoryInfo(root);
        EnsureAcl(rootDirectory, identities, onlyIfProtected: false);
        if (!repairProtectedChildren) return;

        var pending = new Stack<DirectoryInfo>();
        pending.Push(rootDirectory);
        var failures = new List<Exception>();
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            try
            {
                // Recheck before enumerating an existing directory.
                if ((File.GetAttributes(directory.FullName) & FileAttributes.ReparsePoint) != 0)
                    continue;
                foreach (var item in directory.EnumerateFileSystemInfos())
                {
                    try
                    {
                        if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
                            continue;
                        if (directory.FullName.Equals(rootDirectory.FullName, StringComparison.OrdinalIgnoreCase) &&
                            item.Name.Equals(".downloads", StringComparison.OrdinalIgnoreCase))
                            continue;
                        EnsureAcl(item, identities, onlyIfProtected: true);
                        if (item is DirectoryInfo child) pending.Push(child);
                    }
                    catch (Exception error)
                    {
                        failures.Add(new IOException("网站权限检查失败：" + item.FullName, error));
                    }
                }
            }
            catch (Exception error)
            {
                failures.Add(new IOException("网站目录检查失败：" + directory.FullName, error));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("部分网站目录权限未能修复。", failures);
    }

    private static void EnsureAcl(FileSystemInfo item, SecurityIdentifier[] accounts, bool onlyIfProtected)
    {
        var directory = item as DirectoryInfo;
        FileSystemSecurity security = directory is not null
            ? (FileSystemSecurity)Directory.GetAccessControl(item.FullName, AccessControlSections.Access)
            : File.GetAccessControl(item.FullName, AccessControlSections.Access);
        var inheritance = directory is not null ? DirectoryInheritance : InheritanceFlags.None;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();

        // Respect explicit deny rules; do not claim successful repair if
        // a required account is expressly denied write access.
        const FileSystemRights write = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.Delete;
        if (rules.Any(rule => rule.AccessControlType == AccessControlType.Deny &&
                accounts.Contains((SecurityIdentifier)rule.IdentityReference) &&
                (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & write) != 0))
            throw new UnauthorizedAccessException("存在拒绝运行账户写入的 ACL：" + item.FullName);

        if (onlyIfProtected && !security.AreAccessRulesProtected) return;
        var changed = false;
        foreach (var account in accounts)
        {
            var present = rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                account.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                (rule.InheritanceFlags & inheritance) == inheritance &&
                rule.PropagationFlags == PropagationFlags.None);
            if (present) continue;

            security.AddAccessRule(new FileSystemAccessRule(account,
                FileSystemRights.Modify | FileSystemRights.Synchronize, inheritance,
                PropagationFlags.None, AccessControlType.Allow));
            changed = true;
        }
        if (!changed) return;
        // Recheck the path before changing ACLs; do not traverse junctions.
        if ((File.GetAttributes(item.FullName) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("拒绝修改重解析点权限：" + item.FullName);
        if (directory is not null)
            Directory.SetAccessControl(item.FullName, (DirectorySecurity)security);
        else
            File.SetAccessControl(item.FullName, (FileSecurity)security);
    }

    internal static void MarkMigrationComplete() => AtomicFile.WriteAllText(MarkerPath, "3");

    internal static bool MigrationNeeded => !File.Exists(MarkerPath);

    internal static int RunRepair()
    {
        if (!ProcessRunner.IsAdministrator()) return 5;
        try
        {
            InstallDirectorySecurity.EnsureWebAccess(ComponentPaths.ApplicationRoot,
                InstallDirectorySecurity.WebServiceAccounts(), repairProtectedChildren: true);
            RepairExistingIisApplications();
            MarkMigrationComplete();
            PanelLog.Write(LogPath, "已检查 web 根目录、受保护子目录及 IIS 应用程序池权限。");
            return 0;
        }
        catch (Exception error)
        {
            PanelLog.Write(LogPath, "修复 web 目录权限失败", error);
            return 1;
        }
    }

    private static void RepairExistingIisApplications()
    {
        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appcmd)) return;

        string Read(string arguments)
        {
            var result = ProcessRunner.RunSynchronously(appcmd, arguments, Path.GetDirectoryName(appcmd),
                captureOutput: true, timeout: TimeSpan.FromSeconds(30));
            if (result.ExitCode != 0)
                throw new IOException("读取 IIS 配置失败：" + arguments + "；" + result.CombinedOutput);
            return result.StandardOutput.Trim();
        }

        var failures = new List<Exception>();
        foreach (var app in Read("list app /text:APP.NAME").Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var name = app.Trim();
                var vdir = Compat.QuoteCommandLineArgument(name.TrimEnd('/') + "/");
                var physical = Environment.ExpandEnvironmentVariables(Read("list vdir " + vdir + " /text:physicalPath"));
                if (string.IsNullOrWhiteSpace(physical) || !Path.IsPathRooted(physical) ||
                    !IsManagedProductPath(ComponentPaths.WebRoot, physical) || !Directory.Exists(physical))
                    continue;
                var pool = Read("list app " + Compat.QuoteCommandLineArgument(name) + " /text:applicationPool");
                var poolArg = Compat.QuoteCommandLineArgument(pool);
                var type = Read("list apppool " + poolArg + " /text:processModel.identityType");
                var account = type.Equals("ApplicationPoolIdentity", StringComparison.OrdinalIgnoreCase)
                    ? @"IIS AppPool\" + pool
                    : type.Equals("SpecificUser", StringComparison.OrdinalIgnoreCase)
                        ? Read("list apppool " + poolArg + " /text:processModel.userName")
                        : type;
                var sid = InstallDirectorySecurity.ResolveServiceAccount(account)
                    ?? throw new IOException("无法识别 IIS 应用程序池账户：" + pool);
                // The pool identity runs the site; IUSR is who an impersonating page writes as.
                EnsureTreeAccess(physical, new[] { sid, AnonymousIisUser }, repairProtectedChildren: true);
            }
            catch (Exception error)
            {
                failures.Add(new IOException("IIS 应用目录授权失败：" + app, error));
            }
        }
        if (failures.Count != 0)
            throw new AggregateException("部分 IIS 应用目录未能修复。", failures);
    }

    internal static async Task RepairAsync(CancellationToken cancellationToken = default)
    {
        if (ProcessRunner.IsAdministrator())
        {
            if (await Task.Run(RunRepair, cancellationToken) == 0) return;
        }
        else
        {
            var result = await ProcessRunner.RunAsync(Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe"),
                RepairArgument, ComponentPaths.ApplicationRoot, elevated: true, cancellationToken: cancellationToken);
            if (result.ExitCode == 0) return;
        }
        throw new UnauthorizedAccessException("修复网站目录权限失败，请查看 StoreData\\Work\\web-permissions.log。");
    }

    internal static async Task EnsureManagedProductAccessAsync(string path, CancellationToken cancellationToken)
    {
        if (!IsManagedProductPath(ComponentPaths.WebRoot, path))
            return; // External directories are not automatically re-permissioned.

        var root = InstallDirectorySecurity.ValidateInstallRoot(ComponentPaths.ApplicationRoot);
        var accounts = IncludePanelOperators(root, InstallDirectorySecurity.WebServiceAccounts());
        try
        {
            await Task.Run(() => EnsureTreeAccess(path, accounts, repairProtectedChildren: true), cancellationToken);
        }
        catch (Exception error) when (!ProcessRunner.IsAdministrator() &&
            (error is UnauthorizedAccessException || error is AggregateException))
        {
            // Only the fixed, validated web root is passed to the elevated helper.
            await RepairAsync(cancellationToken);
            await Task.Run(() => EnsureTreeAccess(path, accounts, repairProtectedChildren: true), cancellationToken);
        }
        catch (Exception error)
        {
            PanelLog.Write(LogPath, "修复产品目录权限失败：" + path, error);
            throw;
        }
    }
}
