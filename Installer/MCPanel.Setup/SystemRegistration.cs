using System;
using System.IO;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32;

namespace MCPanel.Setup
{
    /// <summary>
    /// Windows integration: uninstall entry, shortcuts and the maintenance copy
    /// of this setup program.
    /// </summary>
    internal static class SystemRegistration
    {
        /// <summary>
        /// The uninstaller lives outside the install folder so it can delete
        /// that folder completely while it is running. The folder is limited
        /// to Administrators and SYSTEM because it runs elevated.
        /// </summary>
        public static string MaintenanceDirectory =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MCPanel.Setup");

        public static string MaintenanceExecutable => Path.Combine(MaintenanceDirectory, "MCPanel-Setup.exe");

        public static void WriteInstallerMarker(string root)
        {
            var marker = Path.Combine(root, "Maintenance", "installer.marker");
            Directory.CreateDirectory(Path.GetDirectoryName(marker));
            File.WriteAllText(marker, InstallEngine.InstallerMarkerContent, new UTF8Encoding(false));
        }

        public static string InstallSetupExecutable(SetupLog log)
        {
            var self = Path.GetFullPath(typeof(SystemRegistration).Assembly.Location);
            var destination = MaintenanceExecutable;
            CreateProtectedDirectory(MaintenanceDirectory);
            if (!string.Equals(self, Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            {
                var staged = destination + ".new";
                File.Copy(self, staged, true);
                if (File.Exists(destination))
                {
                    File.SetAttributes(destination, FileAttributes.Normal);
                    Native.ReplaceFile(destination, staged);
                }
                else
                {
                    File.Move(staged, destination);
                }
            }

            log.Write("Maintenance setup program: " + destination);
            return destination;
        }

        private static void CreateProtectedDirectory(string path)
        {
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null),
                FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
            if (Directory.Exists(path))
            {
                if (Native.IsReparsePoint(path))
                {
                    throw new InvalidOperationException("安装程序维护目录不能是符号链接：" + path);
                }

                Directory.SetAccessControl(path, security);
            }
            else
            {
                Directory.CreateDirectory(path, security);
            }
        }

        /// <summary>Inno Setup's uninstaller from earlier versions is superseded.</summary>
        public static void RemoveLegacyInnoUninstaller(string root, SetupLog log)
        {
            foreach (var pattern in new[] { "unins*.exe", "unins*.dat", "unins*.msg" })
            {
                var maintenance = Path.Combine(root, "Maintenance");
                if (!Directory.Exists(maintenance))
                {
                    continue;
                }

                foreach (var file in Directory.GetFiles(maintenance, pattern))
                {
                    try
                    {
                        File.SetAttributes(file, FileAttributes.Normal);
                        File.Delete(file);
                        log.Write("Removed legacy uninstaller file: " + file);
                    }
                    catch (Exception error)
                    {
                        log.Write("Legacy uninstaller file kept: " + file + " (" + error.Message + ")");
                    }
                }
            }
        }

        public static void CreateShortcuts(string root, bool desktop, SetupLog log)
        {
            var target = Path.Combine(root, "MCPanel.exe");
            var programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), "MCPanel", "MCPanel.lnk");
            Native.CreateShortcut(programs, target, root, "MCPanel 部署与运维控制台");
            var desktopShortcut = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), "MCPanel.lnk");
            if (desktop)
            {
                Native.CreateShortcut(desktopShortcut, target, root, "MCPanel 部署与运维控制台");
            }

            log.Write("Shortcuts created (desktop: " + desktop + ")");
        }

        public static string BuildUninstallCommand(string setupExecutable, string root, bool silent) =>
            "\"" + setupExecutable + "\" /UNINSTALL \"" + root.TrimEnd('\\') + "\"" + (silent ? " /VERYSILENT" : string.Empty);

        public static void RegisterUninstall(string root, string setupExecutable, string version, string registrationId)
        {
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.CreateSubKey(InstallEngine.UninstallRegistryPath + registrationId))
            {
                if (key == null)
                {
                    throw new InvalidOperationException("完整卸载入口注册失败，请重新运行安装程序修复。");
                }

                key.SetValue("DisplayName", "MCPanel");
                key.SetValue("DisplayVersion", version);
                key.SetValue("Publisher", "MCPanel");
                key.SetValue("InstallLocation", root.TrimEnd('\\') + "\\");
                key.SetValue("DisplayIcon", Path.Combine(root, "MCPanel.exe"));
                key.SetValue("UninstallString", BuildUninstallCommand(setupExecutable, root, silent: false));
                key.SetValue("QuietUninstallString", BuildUninstallCommand(setupExecutable, root, silent: true));
                key.SetValue("URLInfoAbout", "https://github.com/vguangshen/MCPanel");
                key.SetValue("HelpLink", "https://github.com/vguangshen/MCPanel/issues");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                key.SetValue("MCPanelUninstallProtocol", 2, RegistryValueKind.DWord);
                key.SetValue("MCPanelSetupProtocol", 1, RegistryValueKind.DWord);
                try
                {
                    var size = DirectorySize(root) / 1024;
                    key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, size), RegistryValueKind.DWord);
                }
                catch
                {
                }
            }
        }

        private static long DirectorySize(string root)
        {
            long total = 0;
            foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.TopDirectoryOnly))
            {
                total += new FileInfo(file).Length;
            }

            return total;
        }
    }
}
