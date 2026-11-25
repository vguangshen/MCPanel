using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32;

namespace MCPanel;

/// <summary>
/// Locks the installation directory down to Administrators, SYSTEM and the
/// account that operates the panel.
///
/// The default install location (C:\MCPanel) inherits
/// "Authenticated Users: Modify" from the drive root, while MCPanel registers
/// Windows services that run as LocalSystem from programs inside that folder.
/// Without this, any local user could replace those programs and run code as
/// SYSTEM. Other users keep read/execute access so they can still start
/// programs, and IIS application pools keep write access to the site root.
/// </summary>
internal static class InstallDirectorySecurity
{
    internal const string Argument = "--secure-install-directory";

    internal static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    internal static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    internal static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    // IIS_IUSRS (BUILTIN alias S-1-5-32-568). Application pool identities are
    // members, so sites under web\ keep the write access they had before.
    internal static readonly SecurityIdentifier IisUsers = new("S-1-5-32-568");

    /// <summary>Groups that must never hold write access to the install root.</summary>
    internal static readonly IReadOnlyList<SecurityIdentifier> BroadWriterGroups = new[]
    {
        new SecurityIdentifier(WellKnownSidType.WorldSid, null),
        new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
        new SecurityIdentifier(WellKnownSidType.InteractiveSid, null),
        Users
    };

    private const InheritanceFlags ContainerAndObject =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    /// <summary>
    /// Replaces the install root's inherited permissions. Explicit entries for
    /// specific accounts (for example earlier panel operators, or SQL Server
    /// service accounts on their own data folders) are kept; explicit grants
    /// to broad groups are removed.
    /// </summary>
    internal static void Harden(string installRoot, SecurityIdentifier? owner)
    {
        var root = ValidateInstallRoot(installRoot);
        var security = Directory.GetAccessControl(root, AccessControlSections.Access);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        foreach (var group in BroadWriterGroups)
        {
            security.PurgeAccessRules(group);
        }

        security.AddAccessRule(Allow(Administrators, FileSystemRights.FullControl));
        security.AddAccessRule(Allow(LocalSystem, FileSystemRights.FullControl));
        security.AddAccessRule(Allow(Users, FileSystemRights.ReadAndExecute | FileSystemRights.Synchronize));
        if (IsAcceptableOwner(owner))
        {
            security.AddAccessRule(Allow(owner!, FileSystemRights.Modify | FileSystemRights.Synchronize));
        }

        // SetAccessControl persists by name (SetNamedSecurityInfo), which
        // propagates the new inheritable entries to the existing tree.
        Directory.SetAccessControl(root, security);
        EnsureWebAccess(root, WebServiceAccounts());
    }

    /// <summary>
    /// True when the root is already locked down: inheritance is off, no broad
    /// group can write, and Administrators and SYSTEM have full control. Then
    /// an update skips <see cref="Harden"/>, which re-applies permissions to
    /// every file below (minutes on large product trees, with MCPanel closed).
    /// </summary>
    internal static bool IsHardened(string installRoot)
    {
        var security = Directory.GetAccessControl(ValidateInstallRoot(installRoot), AccessControlSections.Access);
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(true, false, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        const FileSystemRights writeRights = FileSystemRights.WriteData | FileSystemRights.AppendData |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        bool Grants(SecurityIdentifier sid, FileSystemRights rights) => rules.Any(rule =>
            rule.AccessControlType == AccessControlType.Allow &&
            sid.Equals(rule.IdentityReference) &&
            (rule.FileSystemRights & rights) == rights &&
            (rule.InheritanceFlags & ContainerAndObject) == ContainerAndObject);
        return !rules.Any(rule => rule.AccessControlType == AccessControlType.Allow &&
                   BroadWriterGroups.Contains((SecurityIdentifier)rule.IdentityReference) &&
                   (rule.FileSystemRights & writeRights) != 0) &&
               Grants(Administrators, FileSystemRights.FullControl) &&
               Grants(LocalSystem, FileSystemRights.FullControl);
    }

    /// <summary>
    /// Accounts that run software from web\ and must be able to write there:
    /// IIS application pools, and the SQL Server service, which restores product
    /// databases under web\. Before the install root was hardened they wrote
    /// through "Authenticated Users: Modify" inherited from the drive; without
    /// these grants products under web\ fail with permission errors.
    /// </summary>
    internal static IReadOnlyList<SecurityIdentifier> WebServiceAccounts()
    {
        var accounts = new List<SecurityIdentifier> { IisUsers };
        try
        {
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            foreach (var name in services?.GetSubKeyNames() ?? [])
            {
                if (!name.Equals("MSSQLSERVER", StringComparison.OrdinalIgnoreCase) &&
                    !name.StartsWith("MSSQL$", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                using var service = services!.OpenSubKey(name);
                AddAccount(accounts, ResolveServiceAccount(service?.GetValue("ObjectName") as string));
                // SQL Server setup also grants its per-service SID on data folders.
                AddAccount(accounts, TryTranslate(@"NT SERVICE\" + name));
            }
        }
        catch (Exception error) when (error is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // IIS access is still granted; a later repair can add SQL Server.
        }

        return accounts;
    }

    /// <summary>The SID of a service's logon account (sc.exe "obj=" / ObjectName).</summary>
    internal static SecurityIdentifier? ResolveServiceAccount(string? account)
    {
        var name = (account ?? string.Empty).Trim();
        if (name.Length == 0)
        {
            return null;
        }

        if (name.Equals("LocalSystem", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(@"NT AUTHORITY\SYSTEM", StringComparison.OrdinalIgnoreCase))
        {
            return LocalSystem;
        }

        if (name.Equals("NetworkService", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(@"NT AUTHORITY\NETWORK SERVICE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(@"NT AUTHORITY\NetworkService", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(WellKnownSidType.NetworkServiceSid, null);
        }

        if (name.Equals("LocalService", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(@"NT AUTHORITY\LOCAL SERVICE", StringComparison.OrdinalIgnoreCase) ||
            name.Equals(@"NT AUTHORITY\LocalService", StringComparison.OrdinalIgnoreCase))
        {
            return new SecurityIdentifier(WellKnownSidType.LocalServiceSid, null);
        }

        if (name.StartsWith(@".\", StringComparison.Ordinal))
        {
            name = Environment.MachineName + @"\" + name.Substring(2);
        }

        return TryTranslate(name);
    }

    /// <summary>
    /// Grants each account Modify on web\ (inherited by everything below).
    /// Only writes when a grant is missing: re-applying an ACL to web\ walks
    /// the whole product tree.
    /// </summary>
    internal static void EnsureWebAccess(string installRoot, IEnumerable<SecurityIdentifier> accounts)
    {
        var webRoot = Path.Combine(ValidateInstallRoot(installRoot), "web");
        Directory.CreateDirectory(webRoot);
        var security = Directory.GetAccessControl(webRoot, AccessControlSections.Access);
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
        var changed = false;
        foreach (var account in accounts.Distinct())
        {
            // SYSTEM and Administrators already have full control from the root.
            if (account.Equals(LocalSystem) || account.Equals(Administrators))
            {
                continue;
            }

            var present = rules.Any(rule =>
                rule.AccessControlType == AccessControlType.Allow &&
                account.Equals(rule.IdentityReference) &&
                (rule.FileSystemRights & FileSystemRights.Modify) == FileSystemRights.Modify &&
                (rule.InheritanceFlags & ContainerAndObject) == ContainerAndObject);
            if (!present)
            {
                security.AddAccessRule(Allow(account, FileSystemRights.Modify | FileSystemRights.Synchronize));
                changed = true;
            }
        }

        if (changed)
        {
            Directory.SetAccessControl(webRoot, security);
        }
    }

    private static void AddAccount(List<SecurityIdentifier> accounts, SecurityIdentifier? account)
    {
        if (account is not null && !accounts.Contains(account))
        {
            accounts.Add(account);
        }
    }

    private static SecurityIdentifier? TryTranslate(string account)
    {
        try
        {
            return (SecurityIdentifier)new NTAccount(account).Translate(typeof(SecurityIdentifier));
        }
        catch (Exception error) when (error is IdentityNotMappedException or SystemException)
        {
            return null;
        }
    }

    /// <summary>
    /// True when the account may be granted write access: a real user or
    /// computer account, never a well-known group such as Everyone.
    /// </summary>
    internal static bool IsAcceptableOwner(SecurityIdentifier? owner) =>
        owner is not null &&
        owner.IsAccountSid() &&
        !BroadWriterGroups.Contains(owner) &&
        // Domain Users (RID 513) and Domain Computers (RID 515) are groups.
        !owner.Value.EndsWith("-513", StringComparison.Ordinal) &&
        !owner.Value.EndsWith("-515", StringComparison.Ordinal);

    internal static string ValidateInstallRoot(string installRoot)
    {
        if (string.IsNullOrWhiteSpace(installRoot))
        {
            throw new ArgumentException("安装目录不能为空。", nameof(installRoot));
        }

        var root = NormalizeDirectory(installRoot);
        var protectedRoots = new[]
            {
                Path.GetPathRoot(root) ?? string.Empty,
                Environment.GetFolderPath(Environment.SpecialFolder.Windows),
                Environment.GetFolderPath(Environment.SpecialFolder.System),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Path.GetDirectoryName(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) ?? string.Empty
            }
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeDirectory);
        if (protectedRoots.Any(path => string.Equals(path, root, StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException($"拒绝修改系统目录的权限：{root}");
        }

        if (!Directory.Exists(root) || !File.Exists(Path.Combine(root, "MCPanel.exe")))
        {
            throw new DirectoryNotFoundException($"不是有效的 MCPanel 安装目录：{root}");
        }

        return root;
    }

    /// <summary>
    /// Full path without a trailing separator, except for drive roots: "C:\"
    /// must stay "C:\" because "C:" means the current directory on drive C.
    /// </summary>
    private static string NormalizeDirectory(string path)
    {
        var full = Path.GetFullPath(path);
        var driveRoot = Path.GetPathRoot(full) ?? string.Empty;
        if (string.Equals(full.TrimEnd('\\', '/'), driveRoot.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
        {
            return driveRoot;
        }

        return full.TrimEnd('\\', '/');
    }

    /// <summary>
    /// Entry point for the installer: run elevated from the installed
    /// MCPanel.exe. The account running setup becomes the owner.
    /// </summary>
    internal static int RunFromInstaller()
    {
        try
        {
            if (!ProcessRunner.IsAdministrator())
            {
                return 5;
            }

            using var identity = WindowsIdentity.GetCurrent();
            Harden(ComponentPaths.ApplicationRoot, identity.User);
            return 0;
        }
        catch (Exception error)
        {
            try { Console.Error.WriteLine(error.Message); } catch { }
            return 1;
        }
    }

    /// <summary>The user account a running process belongs to, or null.</summary>
    internal static SecurityIdentifier? TryGetProcessUser(int processId)
    {
        if (processId <= 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (!OpenProcessToken(process.Handle, TokenQuery, out var token))
            {
                return null;
            }

            try
            {
                using var identity = new WindowsIdentity(token);
                return identity.User;
            }
            finally
            {
                CloseHandle(token);
            }
        }
        catch
        {
            return null;
        }
    }

    private static FileSystemAccessRule Allow(IdentityReference identity, FileSystemRights rights) =>
        new(identity, rights, ContainerAndObject, PropagationFlags.None, AccessControlType.Allow);

    private const uint TokenQuery = 0x0008;

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr processHandle, uint desiredAccess, out IntPtr tokenHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);
}
