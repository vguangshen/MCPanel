using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace MCPanel.Setup
{
    /// <summary>
    /// Complete uninstall. The proven PowerShell cleanup engine
    /// (Uninstall-MCPanel.ps1) does the work; this class runs it headless and
    /// turns its status lines into UI progress.
    /// </summary>
    internal sealed class UninstallRunner
    {
        internal const string ErrorPrefix = "MCPANEL-ERROR:";

        private readonly SetupLog _log;
        private readonly Action<string> _status;

        public UninstallRunner(SetupLog log, Action<string> status)
        {
            _log = log;
            _status = status ?? (_ => { });
        }

        public static string ResolveRoot(string requested)
        {
            var root = string.IsNullOrWhiteSpace(requested) ? InstallEngine.ReadRegisteredInstallLocation() : requested;
            if (string.IsNullOrWhiteSpace(root))
            {
                throw new InvalidOperationException("没有找到 MCPanel 的安装记录。");
            }

            return Path.GetFullPath(root.Trim().Trim('"')).TrimEnd('\\');
        }

        /// <summary>Runs the cleanup; throws with the engine's message on failure.</summary>
        public void Run(string root)
        {
            var script = Path.Combine(root, "Uninstall-MCPanel.ps1");
            if (!File.Exists(script))
            {
                throw new FileNotFoundException("卸载支持文件缺失，请使用新版安装程序修复后再卸载。", script);
            }

            var powerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
            var info = new ProcessStartInfo
            {
                FileName = powerShell,
                Arguments = "-NoProfile -ExecutionPolicy Bypass -File \"" + script + "\" -InstallRoot \"" + root + "\" -Silent",
                WorkingDirectory = Path.GetTempPath(),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Native.ConsoleEncoding,
                StandardErrorEncoding = Native.ConsoleEncoding
            };

            string engineError = null;
            var lastStatus = string.Empty;
            var errorText = new System.Text.StringBuilder();
            _log.Write("Starting complete uninstall: " + root);
            using (var process = new Process { StartInfo = info })
            {
                process.OutputDataReceived += (sender, args) =>
                {
                    var line = (args.Data ?? string.Empty).Trim();
                    if (line.Length == 0)
                    {
                        return;
                    }

                    _log.Write("uninstall: " + line);
                    if (line.StartsWith(ErrorPrefix, StringComparison.Ordinal))
                    {
                        engineError = line.Substring(ErrorPrefix.Length).Trim();
                    }
                    else if (!line.StartsWith("at ", StringComparison.Ordinal) && !line.StartsWith("在 ", StringComparison.Ordinal))
                    {
                        lastStatus = line;
                        _status(line);
                    }
                };
                process.ErrorDataReceived += (sender, args) =>
                {
                    if (!string.IsNullOrWhiteSpace(args.Data))
                    {
                        _log.Write("uninstall stderr: " + args.Data);
                        lock (errorText) { errorText.AppendLine(args.Data.Trim()); }
                    }
                };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                process.WaitForExit();
                process.WaitForExit(2000);
                _log.Write("Complete uninstall exit code: " + process.ExitCode);
                if (process.ExitCode != 0)
                {
                    var message = engineError;
                    if (string.IsNullOrWhiteSpace(message))
                    {
                        lock (errorText)
                        {
                            message = FirstMeaningfulLine(errorText.ToString());
                        }
                    }

                    throw new InvalidOperationException(string.IsNullOrWhiteSpace(message)
                        ? "完整卸载未完成（代码 " + process.ExitCode + "）。" + lastStatus
                        : message);
                }
            }

            // The uninstaller is still running from the maintenance folder; the
            // folder is removed after this process exits (see App.OnExit).
            CleanupPending = true;
        }

        /// <summary>Set after a successful complete uninstall.</summary>
        public static bool CleanupPending { get; private set; }

        private static string FirstMeaningfulLine(string text)
        {
            foreach (var line in text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = line.Trim();
                if (trimmed.Length > 0 && !trimmed.StartsWith("+", StringComparison.Ordinal) && !trimmed.StartsWith("At ", StringComparison.Ordinal))
                {
                    // Windows PowerShell prefixes errors with "C:\...\script.ps1 : ".
                    var marker = trimmed.IndexOf(".ps1 : ", StringComparison.OrdinalIgnoreCase);
                    return marker >= 0 ? trimmed.Substring(marker + 7) : trimmed;
                }
            }

            return null;
        }

        /// <summary>
        /// Once no MCPanel registration points at the maintenance copy, remove
        /// it shortly after this process exits.
        /// </summary>
        public static void ScheduleMaintenanceCleanup(SetupLog log)
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                {
                    foreach (var id in new[] { InstallEngine.ProductionRegistrationId, InstallEngine.TestRegistrationId })
                    {
                        using (var key = machine.OpenSubKey(InstallEngine.UninstallRegistryPath + id))
                        {
                            var command = key?.GetValue("UninstallString") as string;
                            if (command != null && command.IndexOf(SystemRegistration.MaintenanceExecutable, StringComparison.OrdinalIgnoreCase) >= 0)
                            {
                                return;
                            }
                        }
                    }
                }

                var directory = SystemRegistration.MaintenanceDirectory;
                if (!Directory.Exists(directory))
                {
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
                    Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand " +
                                Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(
                                    BuildMaintenanceCleanupScript(Process.GetCurrentProcess().Id, directory))),
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetTempPath()
                })?.Dispose();
                log.Write("Scheduled removal of " + directory);
            }
            catch (Exception error)
            {
                log.Write("Maintenance cleanup was not scheduled: " + error.Message);
            }
        }

        /// <summary>
        /// Waits for this uninstaller to exit, then deletes the maintenance
        /// folder while holding the setup instance mutex. Every install holds
        /// that mutex from start to finish, so the registration re-check and
        /// the delete can never interleave with an install that is copying a
        /// fresh uninstaller into the folder before registering it.
        /// </summary>
        internal static string BuildMaintenanceCleanupScript(int uninstallerProcessId, string directory)
        {
            Func<string, string> quote = value => "'" + value.Replace("'", "''") + "'";
            return string.Join("\n", new[]
            {
                "$ErrorActionPreference = 'Stop'",
                "Wait-Process -Id " + uninstallerProcessId + " -Timeout 120 -ErrorAction SilentlyContinue",
                "$mutex = New-Object System.Threading.Mutex($false, " + quote(App.InstanceMutexName) + ")",
                "$owned = $false",
                "try { $owned = $mutex.WaitOne(120000) } catch [System.Threading.AbandonedMutexException] { $owned = $true }",
                "if (!$owned) { exit 1 }",
                "try {",
                "  $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')",
                "  foreach ($id in @(" + quote(InstallEngine.ProductionRegistrationId) + ", " + quote(InstallEngine.TestRegistrationId) + ")) {",
                "    $key = $machine.OpenSubKey(" + quote(InstallEngine.UninstallRegistryPath) + " + $id)",
                "    if ($key) { $key.Dispose(); exit 0 }",
                "  }",
                "  $directory = " + quote(directory),
                "  if (Test-Path -LiteralPath $directory) { Remove-Item -LiteralPath $directory -Recurse -Force }",
                "} finally { $mutex.ReleaseMutex(); $mutex.Dispose() }"
            });
        }
    }
}
