using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Xml;
using Microsoft.Win32;

namespace MCPanel.Setup
{
    internal enum InstallStage
    {
        Verify = 0,
        Download = 1,
        Prepare = 2,
        Deploy = 3,
        Register = 4
    }

    internal sealed class InstallProgress
    {
        public InstallStage Stage { get; set; }

        /// <summary>0..1 overall progress.</summary>
        public double Overall { get; set; }
        public string Title { get; set; }
        public string Detail { get; set; }
    }

    internal sealed class InstallResult
    {
        public bool RestartRequired { get; set; }
        public List<string> Warnings { get; } = new List<string>();
    }

    internal sealed class SetupFailedException : Exception
    {
        public SetupFailedException(string phase, Exception inner)
            : base(phase + "：" + inner.Message, inner)
        {
            Phase = phase;
        }

        public string Phase { get; }
    }

    /// <summary>
    /// The installation transaction. Everything that can fail runs before the
    /// first program file changes; deployment itself is backed up and rolled
    /// back file by file, so a failure leaves the previous program working.
    /// </summary>
    internal sealed class InstallEngine
    {
        internal const string ProductionRegistrationId = "{4877D128-D808-4B6B-B4CB-D4D0476F5275}_is1";
        internal const string TestRegistrationId = "MCPanel.Setup.IntegrationTest_is1";
        internal const string UninstallRegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";
        internal const string InstallerMarkerContent = "MCPanel.Installer.v1";

#if INSTALLER_TEST_BUILD
        internal const bool IsTestBuild = true;
        internal static readonly string RegistrationId = TestRegistrationId;
        private bool _failureInjected;
#else
        internal const bool IsTestBuild = false;
        internal static readonly string RegistrationId = ProductionRegistrationId;
#endif

        private readonly SetupConfig _config;
        private readonly SetupLog _log;
        private readonly Action<InstallProgress> _report;
        private string _phase = string.Empty;
        private InstallStage _stage;

        public InstallEngine(SetupConfig config, SetupLog log, Action<InstallProgress> report)
        {
            _config = config;
            _log = log;
            _report = report ?? (_ => { });
        }

        // ---------------------------------------------------------------
        // System facts shown on the welcome page.
        // ---------------------------------------------------------------

        public static string DefaultInstallDirectory()
        {
            var previous = ReadRegisteredInstallLocation();
            if (!string.IsNullOrWhiteSpace(previous))
            {
                return previous.TrimEnd('\\');
            }

            var systemDrive = Environment.GetEnvironmentVariable("SystemDrive") ?? "C:";
            return systemDrive.TrimEnd('\\') + @"\MCPanel";
        }

        public static string ReadRegisteredInstallLocation()
        {
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(UninstallRegistryPath + RegistrationId))
                {
                    return key?.GetValue("InstallLocation") as string;
                }
            }
            catch
            {
                return null;
            }
        }

        public static bool IsExistingInstallation(string directory)
        {
            try
            {
                var root = Path.GetFullPath(directory);
                return File.Exists(Path.Combine(root, "MCPanel.exe")) ||
                       File.Exists(Path.Combine(root, "Maintenance", "installer.marker"));
            }
            catch
            {
                return false;
            }
        }

        public bool DotNetAvailable()
        {
#if INSTALLER_TEST_BUILD
            if (Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_DOTNET") == "missing")
            {
                return File.Exists(Path.Combine(Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_STATE") ?? string.Empty, "dotnet.done"));
            }
#endif
            foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
            {
                try
                {
                    using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, view))
                    using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full"))
                    {
                        var release = key?.GetValue("Release");
                        if (release is int && (int)release >= _config.DotNet.MinimumRelease)
                        {
                            return true;
                        }
                    }
                }
                catch
                {
                }
            }

            return false;
        }

        public bool VisualCppAvailable()
        {
#if INSTALLER_TEST_BUILD
            if (Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_VC") == "missing")
            {
                return File.Exists(Path.Combine(Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_STATE") ?? string.Empty, "vc.done"));
            }
#endif
            Version minimum;
            if (!Version.TryParse(_config.VisualCpp.MinimumVersion, out minimum))
            {
                return false;
            }

            foreach (var name in new[] { "MSVCR100.dll", "MSVCP100.dll" })
            {
                var path = Path.Combine(Environment.SystemDirectory, name);
                if (!File.Exists(path))
                {
                    return false;
                }

                var info = FileVersionInfo.GetVersionInfo(path);
                var version = new Version(info.FileMajorPart, info.FileMinorPart, info.FileBuildPart, info.FilePrivatePart);
                if (version < minimum)
                {
                    return false;
                }
            }

            return true;
        }

        public static bool PowerShellAvailable()
        {
#if INSTALLER_TEST_BUILD
            if (Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_POWERSHELL") == "missing")
            {
                return false;
            }
#endif
            try
            {
                using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
                using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\PowerShell\3\PowerShellEngine"))
                {
                    return !string.IsNullOrWhiteSpace(key?.GetValue("PowerShellVersion") as string);
                }
            }
            catch
            {
                return true;
            }
        }

        // ---------------------------------------------------------------
        // Installation.
        // ---------------------------------------------------------------

        public InstallResult Install(string directory, string localPackage, bool createDesktopShortcut, CancellationToken cancellation)
        {
            var result = new InstallResult();
            var work = Path.Combine(Path.GetTempPath(), "MCPanel-Setup-" + Guid.NewGuid().ToString("N"));
            FileStream updateLock = null;
            string transactionRoot = null;
            try
            {
                Begin(InstallStage.Verify, "检查系统组件", 0.02, "确认 Windows、PowerShell 与管理员权限");
                CheckSystem();

                Begin(InstallStage.Verify, "检查安装目录", 0.04, "确认安装位置可用且未被占用");
                if (Native.MutexExists("Local\\MCPanel.Uninstall.v2"))
                {
                    throw new InvalidOperationException("MCPanel 正在完整卸载，请等待卸载完成后重新安装。");
                }

                var root = ValidateInstallDirectory(directory);
                if (Native.MutexExists("Local\\MCPanel.MainWindow.v1"))
                {
                    throw new InvalidOperationException("请从托盘菜单完全退出 MCPanel，再重新点击安装。");
                }

                CheckDiskSpace(root);
                Directory.CreateDirectory(work);

                Begin(InstallStage.Download, "下载安装文件", 0.06, "正在获取 MCPanel " + _config.Version);
                var package = ObtainPackage(localPackage, work, cancellation);
                var needDotNet = !DotNetAvailable();
                var needVisualCpp = !VisualCppAvailable();
                string dotNetInstaller = null;
                string visualCppInstaller = null;
                if (needDotNet)
                {
                    dotNetInstaller = Download(_config.DotNet.Url, _config.DotNet.Sha256, Path.Combine(work, "dotnet48.exe"), _config.DotNet.Name, 0.30, 0.38, cancellation);
                }

                if (needVisualCpp)
                {
                    visualCppInstaller = Download(_config.VisualCpp.Url, _config.VisualCpp.Sha256, Path.Combine(work, "vcredist2010-x64.exe"), _config.VisualCpp.Name, 0.38, 0.42, cancellation);
                }

                Begin(InstallStage.Prepare, "解压并校验主程序", 0.43, "校验程序包完整性与版本");
                var payload = Path.Combine(work, "Payload");
                ExtractPackage(package, payload);
                var executableVersion = FileVersionInfo.GetVersionInfo(Path.Combine(payload, "MCPanel.exe")).FileVersion;
                if (!string.Equals(executableVersion, _config.Version + ".0", StringComparison.Ordinal))
                {
                    throw new InvalidDataException("安装包版本不匹配，原程序未修改。");
                }

                _log.Write("Package executable version validated: " + executableVersion);

                Begin(InstallStage.Prepare, "读取并合并原配置", 0.50, "保留已有配置、数据库与站点设置");
                updateLock = AcquireUpdateLock(root);
                MergeOriginalConfiguration(root, Path.Combine(payload, "MCPanel.exe.config"));

                Begin(InstallStage.Prepare, "检查程序文件占用", 0.54, "确认需要替换的文件没有被其他程序占用");
                CheckChangedProgramFiles(root, payload);
                cancellation.ThrowIfCancellationRequested();

                Begin(InstallStage.Prepare, "安装必备运行库", 0.58, needDotNet || needVisualCpp ? "正在静默安装 Microsoft 运行库" : "运行库已满足要求");
                if (needDotNet)
                {
                    result.RestartRequired |= RunPrerequisite(_config.DotNet, dotNetInstaller, isDotNet: true);
                }

                if (needVisualCpp)
                {
                    result.RestartRequired |= RunPrerequisite(_config.VisualCpp, visualCppInstaller, isDotNet: false);
                }

                Begin(InstallStage.Deploy, "安装主程序", 0.70, "以事务方式部署程序文件，失败会自动回滚");
                transactionRoot = DeployProgram(root, payload);

                Begin(InstallStage.Register, "注册系统入口", 0.88, "写入卸载入口与快捷方式");
                SystemRegistration.WriteInstallerMarker(root);
                var setupExecutable = SystemRegistration.InstallSetupExecutable(_log);
                SystemRegistration.RemoveLegacyInnoUninstaller(root, _log);
                SystemRegistration.CreateShortcuts(root, createDesktopShortcut, _log);
                SystemRegistration.RegisterUninstall(root, setupExecutable, _config.Version, RegistrationId);

                Begin(InstallStage.Register, "加固安装目录权限", 0.95, "仅管理员、SYSTEM 与当前账户可写入");
                var hardening = SecureInstallDirectory(root);
                if (hardening != null)
                {
                    result.Warnings.Add(hardening);
                }

                TryDeleteDirectory(transactionRoot);
                Report(InstallStage.Register, 1.0, "部署完成", "MCPanel " + _config.Version + " 已就绪");
                _log.Write("Installation completed. Restart required: " + result.RestartRequired);
                return result;
            }
            catch (OperationCanceledException)
            {
                _log.Write("Installation cancelled during: " + _phase);
                throw;
            }
            catch (Exception error)
            {
                _log.Write("MCPanel installation stopped: " + _phase + "：" + error);
                throw new SetupFailedException(_phase, error);
            }
            finally
            {
                updateLock?.Dispose();
                TryDeleteDirectory(work);
            }
        }

        private void Begin(InstallStage stage, string phase, double overall, string detail)
        {
            _phase = phase;
            _stage = stage;
            _log.Write("Phase: " + phase);
            Report(stage, overall, phase, detail);
        }

        private void Report(InstallStage stage, double overall, string title, string detail) =>
            _report(new InstallProgress { Stage = stage, Overall = Math.Max(0, Math.Min(1, overall)), Title = title, Detail = detail });

        private static void CheckSystem()
        {
            if (!Environment.Is64BitOperatingSystem)
            {
                throw new PlatformNotSupportedException("MCPanel 仅支持 64 位 Windows。");
            }

            if (Environment.OSVersion.Version < new Version(6, 1, 7601))
            {
                throw new PlatformNotSupportedException("MCPanel 需要 Windows 7 SP1 / Windows Server 2008 R2 SP1 或更高版本。");
            }

            if (!PowerShellAvailable())
            {
                throw new InvalidOperationException(
                    "此系统的 Windows PowerShell 低于 3.0，MCPanel 的组件管理和卸载脚本无法运行。" +
                    "请先安装 Windows Management Framework 5.1（KB3191566，https://www.microsoft.com/download/details.aspx?id=54616），重启后再安装 MCPanel。原程序未修改。");
            }
        }

        // ---------------------------------------------------------------
        // Directory validation (ported from Deployment.iss).
        // ---------------------------------------------------------------

        internal string ValidateInstallDirectory(string directory)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                throw new InvalidOperationException("请选择 MCPanel 的安装目录。");
            }

            string root;
            try
            {
                root = Path.GetFullPath(directory.Trim()).TrimEnd('\\');
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("安装目录无效：" + error.Message);
            }

            var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows).TrimEnd('\\') + "\\";
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles).TrimEnd('\\');
            if (root.Length <= 3 ||
                (root + "\\").StartsWith(windows, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root, programFiles, StringComparison.OrdinalIgnoreCase) ||
                root.Length + 80 + Math.Max(0, _config.MaximumRelativeLength) > 248)
            {
                throw new InvalidOperationException("请选择独立的 MCPanel 文件夹，不能使用磁盘根目录、Windows 目录或过长的路径。");
            }

            AssertNoLinks(root);
            AssertNoLinks(Path.Combine(root, "Maintenance"));
            AssertNoLinks(Path.Combine(root, "StoreData"));
            if (Directory.Exists(root) &&
                Directory.EnumerateFileSystemEntries(root).Any() &&
                !File.Exists(Path.Combine(root, "MCPanel.exe")) &&
                !File.Exists(Path.Combine(root, "MCPanel.exe.config")) &&
                !Directory.Exists(Path.Combine(root, "StoreData", "Updates")) &&
                !File.Exists(Path.Combine(root, "Maintenance", "installer.marker")))
            {
                throw new InvalidOperationException("该文件夹包含其他文件。请选择空文件夹，或已有的 MCPanel 安装目录。");
            }

            return root;
        }

        private static void AssertNoLinks(string path)
        {
            var current = path;
            while (!string.IsNullOrEmpty(current) && current.Length > 3)
            {
                if (Native.IsReparsePoint(current))
                {
                    throw new InvalidOperationException("安装目录不能通过符号链接或目录联接访问：" + current);
                }

                var parent = Path.GetDirectoryName(current);
                if (string.IsNullOrEmpty(parent) || parent == current)
                {
                    break;
                }

                current = parent;
            }
        }

        private void CheckDiskSpace(string root)
        {
            var available = Native.AvailableBytes(root);
            if (available.HasValue && (long)available.Value < _config.RequiredSpaceBytes)
            {
                throw new IOException(string.Format(
                    "磁盘空间不足：至少需要 {0:0} MB，当前可用 {1:0} MB。",
                    _config.RequiredSpaceBytes / 1048576.0,
                    available.Value / 1048576.0));
            }
        }

        // ---------------------------------------------------------------
        // Downloads.
        // ---------------------------------------------------------------

        private string ObtainPackage(string localPackage, string work, CancellationToken cancellation)
        {
            if (!string.IsNullOrWhiteSpace(localPackage))
            {
                var path = Path.GetFullPath(localPackage);
                Report(InstallStage.Download, 0.20, "下载安装文件", "校验本地安装包");
                if (!File.Exists(path) || !string.Equals(ComputeSha256(path), _config.PackageSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidDataException("本地安装包校验失败，原程序未修改。请使用与安装器版本一致的官方 ZIP。");
                }

                return path;
            }

            return Download(_config.PackageUrl, _config.PackageSha256, Path.Combine(work, "MCPanel.zip"), "MCPanel " + _config.Version, 0.06, 0.30, cancellation);
        }

        private string Download(string url, string sha256, string destination, string label, double from, double to, CancellationToken cancellation)
        {
            ValidateDownloadUrl(url);
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072 | (SecurityProtocolType)768;
            _log.Write("Downloading " + url);
            Exception lastError = null;
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                cancellation.ThrowIfCancellationRequested();
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(url);
                    request.UserAgent = "MCPanel-Setup/" + _config.Version;
                    request.Timeout = 60000;
                    request.ReadWriteTimeout = 60000;
                    request.AutomaticDecompression = DecompressionMethods.None;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    using (var input = response.GetResponseStream())
                    using (var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                    using (var sha = SHA256.Create())
                    {
                        var total = response.ContentLength;
                        var buffer = new byte[1 << 16];
                        long received = 0;
                        var stopwatch = Stopwatch.StartNew();
                        var lastReport = 0L;
                        int read;
                        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                        {
                            cancellation.ThrowIfCancellationRequested();
                            output.Write(buffer, 0, read);
                            sha.TransformBlock(buffer, 0, read, null, 0);
                            received += read;
                            if (stopwatch.ElapsedMilliseconds - lastReport >= 150)
                            {
                                lastReport = stopwatch.ElapsedMilliseconds;
                                var fraction = total > 0 ? (double)received / total : 0;
                                var speed = received / Math.Max(0.001, stopwatch.Elapsed.TotalSeconds);
                                Report(InstallStage.Download, from + (to - from) * fraction, "下载安装文件",
                                    string.Format("{0}  ·  {1}{2}  ·  {3}/s", label, FormatBytes(received),
                                        total > 0 ? " / " + FormatBytes(total) : string.Empty, FormatBytes((long)speed)));
                            }
                        }

                        sha.TransformFinalBlock(new byte[0], 0, 0);
                        var actual = BitConverter.ToString(sha.Hash).Replace("-", string.Empty);
                        if (!string.Equals(actual, sha256, StringComparison.OrdinalIgnoreCase))
                        {
                            throw new InvalidDataException(label + " 的 SHA-256 校验失败，文件可能不完整或已被替换。原程序未修改。");
                        }
                    }

                    Report(InstallStage.Download, to, "下载安装文件", label + " 已下载并通过校验");
                    return destination;
                }
                catch (InvalidDataException)
                {
                    TryDeleteFile(destination);
                    throw;
                }
                catch (OperationCanceledException)
                {
                    TryDeleteFile(destination);
                    throw;
                }
                catch (Exception error) when (error is WebException || error is IOException)
                {
                    lastError = error;
                    TryDeleteFile(destination);
                    _log.Write("Download attempt " + attempt + " failed: " + error.Message);
                    Thread.Sleep(1000 * attempt);
                }
            }

            throw new IOException("无法下载 " + label + "，请检查网络后重试。" + (lastError == null ? string.Empty : "（" + lastError.Message + "）"), lastError);
        }

        private static void ValidateDownloadUrl(string url)
        {
            Uri uri;
            if (!Uri.TryCreate(url, UriKind.Absolute, out uri))
            {
                throw new InvalidDataException("下载地址无效：" + url);
            }

            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                return;
            }

#if INSTALLER_TEST_BUILD
            if (uri.Scheme == Uri.UriSchemeHttp && uri.Host == "127.0.0.1")
            {
                return;
            }
#endif
            throw new InvalidDataException("下载地址必须使用 HTTPS：" + url);
        }

        internal static string FormatBytes(long bytes)
        {
            if (bytes >= 1L << 30) return (bytes / (double)(1L << 30)).ToString("0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (double)(1L << 20)).ToString("0.0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (double)(1L << 10)).ToString("0") + " KB";
            return bytes + " B";
        }

        internal static string ComputeSha256(string path)
        {
            using (var sha = SHA256.Create())
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan))
            {
                return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        // ---------------------------------------------------------------
        // Package extraction.
        // ---------------------------------------------------------------

        private void ExtractPackage(string package, string destination)
        {
            Directory.CreateDirectory(destination);
            var root = Path.GetFullPath(destination).TrimEnd('\\') + "\\";
            using (var archive = ZipFile.OpenRead(package))
            {
                foreach (var entry in archive.Entries)
                {
                    var target = Path.GetFullPath(Path.Combine(root, entry.FullName.Replace('/', '\\')));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                    {
                        throw new InvalidDataException("安装包包含越界路径，已停止安装：" + entry.FullName);
                    }

                    if (string.IsNullOrEmpty(entry.Name))
                    {
                        Directory.CreateDirectory(target);
                        continue;
                    }

                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    entry.ExtractToFile(target, true);
                }
            }

            foreach (var name in new[] { "MCPanel.exe", "MCPanel.exe.config" })
            {
                if (!File.Exists(Path.Combine(destination, name)))
                {
                    throw new InvalidDataException("安装包不完整，缺少 " + name + "。");
                }
            }
        }

        // ---------------------------------------------------------------
        // Configuration merge (ported from Deployment.iss).
        // ---------------------------------------------------------------

        private FileStream AcquireUpdateLock(string root)
        {
            var updates = Path.Combine(root, "StoreData", "Updates");
            // Fresh installs do not create business-data folders merely to lock.
            if (!Directory.Exists(updates))
            {
                return null;
            }

            AssertNoLinks(updates);
            return Native.AcquireExclusiveLock(Path.Combine(updates, "apply-update.lock"));
        }

        internal void MergeOriginalConfiguration(string root, string newConfig)
        {
            var original = Path.Combine(root, "MCPanel.exe.config");
            var rollback = Path.Combine(root, "StoreData", "Updates", "Rollback", "Current", "MCPanel.exe.config");
            if (!File.Exists(Path.Combine(root, "MCPanel.exe")) && File.Exists(rollback))
            {
                original = rollback;
            }

            if (!File.Exists(original))
            {
                if (File.Exists(rollback))
                {
                    original = rollback;
                }
                else
                {
                    var rollbackRoot = Path.Combine(root, "StoreData", "Updates", "Rollback");
                    if (Directory.Exists(rollbackRoot))
                    {
                        var latest = Directory.GetDirectories(rollbackRoot, "Current.failed-*")
                            .Select(directory => Path.Combine(directory, "MCPanel.exe.config"))
                            .Where(File.Exists)
                            .OrderByDescending(path => Path.GetFileName(Path.GetDirectoryName(path)), StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault();
                        if (latest != null)
                        {
                            original = latest;
                        }
                    }
                }
            }

            var newDocument = LoadXml(newConfig);
            if (!File.Exists(original))
            {
                if (Directory.Exists(Path.Combine(root, "StoreData")))
                {
                    throw new InvalidOperationException("已有业务数据，但未找到原应用配置。请保留 StoreData 和回滚副本，恢复原配置后重试。");
                }

                return;
            }

            AssertNoLinks(original);
            var oldDocument = LoadXml(original);
            var settings = newDocument.SelectSingleNode("/configuration/appSettings") as XmlElement;
            if (settings == null)
            {
                settings = newDocument.CreateElement("appSettings");
                newDocument.DocumentElement.AppendChild(settings);
            }

            var preserved = new HashSet<string>(_config.PreservedAppSettingKeys, StringComparer.Ordinal);
            foreach (XmlElement node in oldDocument.SelectNodes("/configuration/appSettings/add"))
            {
                if (!node.HasAttribute("key") || !node.HasAttribute("value"))
                {
                    continue;
                }

                var key = node.GetAttribute("key");
                if (!preserved.Contains(key))
                {
                    continue;
                }

                XmlElement target = null;
                foreach (XmlElement candidate in settings.SelectNodes("add"))
                {
                    if (string.Equals(candidate.GetAttribute("key"), key, StringComparison.OrdinalIgnoreCase))
                    {
                        target = candidate;
                    }
                }

                if (target == null)
                {
                    target = newDocument.CreateElement("add");
                    target.SetAttribute("key", key);
                    settings.AppendChild(target);
                }

                target.SetAttribute("value", _config.NormalizeDownloadUrl(key, node.GetAttribute("value")));
            }

            using (var writer = XmlWriter.Create(newConfig, new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false }))
            {
                newDocument.Save(writer);
            }

            _log.Write("Preserved original settings from " + original);
        }

        private static XmlDocument LoadXml(string path)
        {
            var document = new XmlDocument { PreserveWhitespace = true, XmlResolver = null };
            try
            {
                using (var reader = XmlReader.Create(path, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null }))
                {
                    document.Load(reader);
                }
            }
            catch (Exception error)
            {
                throw new InvalidDataException("配置文件无法读取，原程序未修改：" + path + "（" + error.Message + "）", error);
            }

            if (document.DocumentElement == null || document.DocumentElement.Name != "configuration")
            {
                throw new InvalidDataException("应用配置缺少 configuration 根节点。");
            }

            return document;
        }

        // ---------------------------------------------------------------
        // Prerequisites.
        // ---------------------------------------------------------------

        private bool RunPrerequisite(Prerequisite prerequisite, string installer, bool isDotNet)
        {
            Report(InstallStage.Prepare, isDotNet ? 0.60 : 0.65, "安装必备运行库", "正在安装 " + prerequisite.Name + "，请勿关闭窗口");
            int exitCode;
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = installer,
                    Arguments = prerequisite.Arguments ?? string.Empty,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WorkingDirectory = Path.GetDirectoryName(installer)
                }))
                {
                    process.WaitForExit();
                    exitCode = process.ExitCode;
                }
            }
            catch (Exception error)
            {
                throw new InvalidOperationException("无法启动 " + prerequisite.Name + " 安装程序：" + error.Message, error);
            }

            _log.Write("Prerequisite " + prerequisite.Name + " returned " + exitCode);
            if (exitCode == 3010 || exitCode == 1641)
            {
                return true;
            }

            if (exitCode != 0 && exitCode != 1638)
            {
                throw new InvalidOperationException(prerequisite.Name + " 安装失败（代码 " + exitCode + "）。主程序未修改，请解决运行库安装问题后重试。");
            }

            if (!(isDotNet ? DotNetAvailable() : VisualCppAvailable()))
            {
                throw new InvalidOperationException(prerequisite.Name + " 安装后仍未满足要求。主程序未修改，请重启 Windows 后重试。");
            }

            return false;
        }

        // ---------------------------------------------------------------
        // Transactional deployment (ported from Deployment.iss).
        // ---------------------------------------------------------------

        private void CheckChangedProgramFiles(string root, string incoming)
        {
            foreach (var name in _config.ProgramFiles)
            {
                var source = Path.Combine(incoming, name);
                var destination = Path.Combine(root, name);
                if (!File.Exists(source))
                {
                    throw new InvalidDataException("安装包文件不完整：" + name);
                }

                AssertNoLinks(destination);
                if (FileNeedsChange(destination, source))
                {
                    Native.AssertFileAvailable(destination);
                }
            }
        }

        private static bool FileNeedsChange(string installed, string incoming) =>
            !File.Exists(installed) ||
            !string.Equals(ComputeSha256(installed), ComputeSha256(incoming), StringComparison.OrdinalIgnoreCase);

        /// <summary>Returns the transaction folder holding the original files.</summary>
        private string DeployProgram(string root, string incoming)
        {
            var transaction = Path.Combine(root, "Maintenance", "Transactions", DateTime.Now.ToString("yyyyMMddHHmmssfff"));
            AssertNoLinks(transaction);
            var manifest = Path.Combine(root, "Maintenance", "program-files.lst");
            var changed = _config.ProgramFiles.Where(name => FileNeedsChange(Path.Combine(root, name), Path.Combine(incoming, name))).ToList();
            var originals = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var completed = new List<string>();
            Directory.CreateDirectory(Path.Combine(transaction, "Original"));
            var manifestExisted = File.Exists(manifest);
            if (manifestExisted)
            {
                File.Copy(manifest, Path.Combine(transaction, "manifest.previous"), true);
            }

            // Stage and back up everything before the first replacement.
            for (var index = 0; index < changed.Count; index++)
            {
                var name = changed[index];
                var ready = Path.Combine(transaction, "Ready", name);
                Directory.CreateDirectory(Path.GetDirectoryName(ready));
                File.Copy(Path.Combine(incoming, name), ready, true);
                File.SetAttributes(ready, FileAttributes.Normal);
                var destination = Path.Combine(root, name);
                if (File.Exists(destination))
                {
                    var backup = Path.Combine(transaction, "Original", name);
                    Directory.CreateDirectory(Path.GetDirectoryName(backup));
                    File.Copy(destination, backup, true);
                    originals.Add(name);
                }

                Report(InstallStage.Deploy, 0.70 + 0.06 * (index + 1) / Math.Max(1, changed.Count), "安装主程序", "准备 " + name);
            }

            string active = null;
            try
            {
                SaveOwnership(manifest);
                for (var index = 0; index < changed.Count; index++)
                {
                    var name = changed[index];
                    active = name;
                    var destination = Path.Combine(root, name);
                    Directory.CreateDirectory(Path.GetDirectoryName(destination));
                    ReplaceOwnedFile(Path.Combine(transaction, "Ready", name), destination);
                    completed.Add(name);
                    active = null;
                    Report(InstallStage.Deploy, 0.76 + 0.11 * (index + 1) / Math.Max(1, changed.Count), "安装主程序", "部署 " + name);
                }
            }
            catch (Exception error)
            {
                var rollbackFailed = false;
                if (active != null && originals.Contains(active))
                {
                    try
                    {
                        File.SetAttributes(Path.Combine(root, active), File.GetAttributes(Path.Combine(transaction, "Original", active)));
                    }
                    catch
                    {
                        rollbackFailed = true;
                    }
                }

                for (var index = completed.Count - 1; index >= 0; index--)
                {
                    var name = completed[index];
                    var destination = Path.Combine(root, name);
                    var backup = Path.Combine(transaction, "Original", name);
                    try
                    {
                        if (originals.Contains(name))
                        {
                            var restore = Path.Combine(transaction, "Ready", name + ".restore");
                            File.Copy(backup, restore, true);
                            File.SetAttributes(restore, FileAttributes.Normal);
                            ReplaceOwnedFile(restore, destination, allowInjectedFailure: false);
                            File.SetAttributes(destination, File.GetAttributes(backup));
                        }
                        else
                        {
                            File.Delete(destination);
                        }
                    }
                    catch (Exception rollbackError)
                    {
                        rollbackFailed = true;
                        _log.Write("Rollback: " + rollbackError.Message);
                    }
                }

                try
                {
                    if (manifestExisted)
                    {
                        File.Copy(Path.Combine(transaction, "manifest.previous"), manifest, true);
                    }
                    else
                    {
                        File.Delete(manifest);
                    }
                }
                catch
                {
                    rollbackFailed = true;
                }

                var message = error.Message;
                if (rollbackFailed)
                {
                    message += Environment.NewLine + "部分文件未能自动恢复，请保留：" + transaction;
                }

                throw new IOException(message, error);
            }

            _log.Write("Program installed. Original program files retained at " + transaction);
            return transaction;
        }

        private void SaveOwnership(string manifest)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(manifest));
            File.WriteAllLines(manifest, _config.ProgramFiles, new UTF8Encoding(true));
        }

        private void ReplaceOwnedFile(string source, string destination, bool allowInjectedFailure = true)
        {
#if INSTALLER_TEST_BUILD
            var failFile = Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_FAIL_FILE");
            if (allowInjectedFailure && !_failureInjected && !string.IsNullOrEmpty(failFile) &&
                string.Equals(Path.GetFileName(destination), failFile, StringComparison.OrdinalIgnoreCase))
            {
                _failureInjected = true;
                throw new IOException("Injected late installer replacement failure.");
            }
#endif
            if (File.Exists(destination))
            {
                File.SetAttributes(destination, File.GetAttributes(destination) & ~FileAttributes.ReadOnly);
                Native.ReplaceFile(destination, source);
            }
            else
            {
                File.Move(source, destination);
            }
        }

        // ---------------------------------------------------------------
        // Post-install.
        // ---------------------------------------------------------------

        private string SecureInstallDirectory(string root)
        {
            int code;
            try
            {
                using (var process = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(root, "MCPanel.exe"),
                    Arguments = "--secure-install-directory",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }))
                {
                    if (!process.WaitForExit(300000))
                    {
                        try { process.Kill(); } catch { }
                        code = -1;
                    }
                    else
                    {
                        code = process.ExitCode;
                    }
                }
            }
            catch (Exception error)
            {
                _log.Write("Install directory permission hardening could not start: " + error.Message);
                code = -1;
            }

            if (code == 0)
            {
                _log.Write("Install directory permissions hardened: " + root);
                return null;
            }

            _log.Write("Install directory permission hardening failed with code " + code);
            return "MCPanel 已安装，但未能收紧安装目录权限（代码 " + code + "）。其他本地用户可能可以修改此目录中的程序。请重新运行安装程序修复。";
        }

        internal static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
            {
                return;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                Directory.Delete(path, true);
            }
            catch
            {
            }
        }

        private static void TryDeleteFile(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
