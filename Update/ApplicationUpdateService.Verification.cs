using System.Diagnostics;
using System.IO;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;

namespace MCPanel;

/// <summary>
/// Steps the elevated updater performs before it touches the installation.
/// The plan file and the staging folder were written by the non-elevated
/// panel, so neither is trusted: the package is copied into a private folder,
/// checked against the SHA-256 fixed at authorization time, and extracted
/// again by the elevated process itself.
/// </summary>
public sealed partial class ApplicationUpdateService
{
    /// <summary>
    /// Restricts the install folder to Administrators, SYSTEM and the user
    /// who started the update. Returns a warning when this fails; the update
    /// itself still continues.
    /// </summary>
    internal static string? TryHardenInstallDirectory(ApplicationUpdatePlan plan)
    {
        if (!ProcessRunner.IsAdministrator())
        {
            return null;
        }

        try
        {
            if (InstallDirectorySecurity.IsHardened(plan.InstallDirectory))
            {
                // Already locked down (by the installer or an earlier update):
                // only make sure the services under web\ can still write there.
                InstallDirectorySecurity.EnsureWebAccess(plan.InstallDirectory, InstallDirectorySecurity.WebServiceAccounts());
                return null;
            }

            var owner = InstallDirectorySecurity.TryGetProcessUser(plan.ParentProcessId);
            InstallDirectorySecurity.Harden(plan.InstallDirectory, owner);
            return null;
        }
        catch (Exception error)
        {
            return $"未能收紧安装目录权限：{error.Message}。请重新运行安装器修复。";
        }
    }

    internal static void StageVerifiedPayload(ApplicationUpdatePlan plan, string expectedSha256)
    {
        var expected = NormalizeSha256(expectedSha256);
        if (expected.Length != 64)
        {
            throw new InvalidDataException("更新计划缺少有效的更新包 SHA-256，已拒绝应用。");
        }

        if (string.IsNullOrWhiteSpace(plan.PackageFile))
        {
            throw new InvalidDataException("更新计划缺少更新包路径，已拒绝应用。");
        }

        var packageFile = Path.GetFullPath(plan.PackageFile);
        var privateRoot = CreatePrivateDirectory(
            Path.Combine(Path.GetTempPath(), "MCPanel-UpdateVerify-" + Guid.NewGuid().ToString("N")));
        try
        {
            var privatePackage = Path.Combine(privateRoot, "package.zip");
            string actual;
            try
            {
                actual = CopyAndComputeSha256(packageFile, privatePackage);
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException)
            {
                throw new IOException(
                    $"管理员更新程序无法读取更新包：{packageFile}。如果它位于网络驱动器，请先复制到本地磁盘后重试。",
                    error);
            }

            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException("更新包在授权后被修改（SHA-256 不匹配），已拒绝应用，原程序未修改。");
            }

            var stagingRoot = Path.Combine(
                ComponentPaths.GetUpdatesRoot(plan.InstallDirectory),
                "Staging",
                Guid.NewGuid().ToString("N"));
            var extractRoot = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(extractRoot);
            try
            {
                ExtractArchiveSafely(privatePackage, extractRoot, progress: null, CancellationToken.None);
                // The hash match proves these are the exact bytes whose
                // MCPanel.exe version the panel already checked while preparing.
                var payload = ResolvePayloadRoot(extractRoot);
                if (!File.Exists(Path.Combine(payload, "MCPanel.exe")) ||
                    !File.Exists(Path.Combine(payload, "MCPanel.exe.config")))
                {
                    throw new InvalidDataException("更新包缺少 MCPanel.exe 或 MCPanel.exe.config，已拒绝应用。");
                }

                plan.StagingRoot = stagingRoot;
                plan.PayloadDirectory = payload;
                ValidateTransactionPlan(plan);
            }
            catch
            {
                TryDeleteDirectory(stagingRoot);
                throw;
            }
        }
        finally
        {
            TryDeleteDirectory(privateRoot);
        }
    }

    internal static string CopyAndComputeSha256(string source, string destination)
    {
        using var sha = SHA256.Create();
        using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 1024, FileOptions.SequentialScan))
        using (var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1024 * 1024))
        {
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                sha.TransformBlock(buffer, 0, read, null, 0);
                output.Write(buffer, 0, read);
            }

            sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
            output.Flush(flushToDisk: true);
        }

        return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }

    /// <summary>Creates a folder only Administrators, SYSTEM and this account can access.</summary>
    private static string CreatePrivateDirectory(string path)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(
            InstallDirectorySecurity.Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(
            InstallDirectorySecurity.LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        using (var identity = WindowsIdentity.GetCurrent())
        {
            if (identity.User is not null)
            {
                security.AddAccessRule(new FileSystemAccessRule(
                    identity.User, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            }
        }

        Directory.CreateDirectory(path, security);
        return path;
    }
}
