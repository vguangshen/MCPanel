using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public enum InstallProgressStage
{
    Preparing,
    Downloading,
    Installing,
    Completed
}

public sealed record InstallProgress(
    double Percent,
    string Message,
    InstallProgressStage Stage = InstallProgressStage.Installing,
    double? StagePercent = null,
    string? SpeedText = null);

internal sealed class InstallRestartRequiredException(string message) : InvalidOperationException(message);

public sealed partial class EnvironmentInstaller : IDisposable
{
    private readonly HttpClient _httpClient;

    public EnvironmentInstaller()
    {
        // The elevated worker targets .NET Framework 4.6.2. Enable the TLS
        // versions required by current Microsoft/CDN endpoints and the legacy
        // vendor store without replacing certificate validation.
        ServicePointManager.SecurityProtocol |=
            SecurityProtocolType.Tls |
            (SecurityProtocolType)768 |
            (SecurityProtocolType)3072;

        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromMinutes(60)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd($"MCPanel/{ApplicationUpdateService.CurrentVersionText}");
        _httpClient.DefaultRequestHeaders.Accept.ParseAdd("*/*");
    }

    internal static string SqlServerInstallRestartMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "sqlserver-install-restart.pending");

    internal static bool HasSqlServerInstallContinuation => File.Exists(SqlServerInstallRestartMarkerPath);

    internal static string SqlServerInstallContinuationMessage =>
        "已写入 4KB 扇区兼容设置，请重启设备后点击“继续安装”。";

    internal static string IisInstallRestartMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "iis-install-restart.pending");

    internal static bool HasIisInstallContinuation => File.Exists(IisInstallRestartMarkerPath);

    internal static string IisInstallContinuationMessage =>
        "IIS 或 URL Rewrite 安装需要重启 Windows 才能继续。请重启设备后再次点击“继续安装”。";

    internal static string IisConfigurationRepairMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "iis-configuration-repair.pending");

    internal static bool HasIisConfigurationRepairPending => File.Exists(IisConfigurationRepairMarkerPath);

    public async Task InstallAsync(EnvironmentItem item, Action<InstallProgress> progress, CancellationToken cancellationToken = default)
    {
        var downloads = EnvironmentDownloadSettings.Load();
        switch (item.Kind)
        {
            case EnvironmentKind.Iis:
                await InstallIisAsync(downloads, progress, cancellationToken);
                break;
            case EnvironmentKind.Nginx:
                await InstallNginxAsync(downloads, progress, cancellationToken);
                break;
            case EnvironmentKind.MySql:
                await InstallMySqlAsync(
                    MySqlReleaseCatalog.Resolve(item.SelectedMySqlReleaseId, downloads.MySqlPackageUrl),
                    progress,
                    cancellationToken);
                break;
            case EnvironmentKind.SqlServer:
                await InstallSqlServerAsync(
                    downloads,
                    item.SelectedSqlServerReleaseId,
                    progress,
                    cancellationToken);
                break;
            case EnvironmentKind.Tomcat:
                await InstallTomcatAsync(downloads, progress, cancellationToken);
                break;
        }
    }

    private async Task<string> DownloadAbsoluteFileAsync(string url, string directory, string fileName, Action<InstallProgress> progress, double from, double to, CancellationToken cancellationToken)
    {
        return await DownloadCoreAsync(url, directory, fileName, progress, from, to, cancellationToken);
    }

    private static InstallProgress InstallingProgress(double percent, string message, double? stagePercent = null) =>
        new(
            percent,
            message,
            InstallProgressStage.Installing,
            stagePercent ?? Compat.Clamp(percent, 0, 100));

    private async Task<string> DownloadCoreAsync(string url, string directory, string fileName, Action<InstallProgress> progress, double from, double to, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, fileName);
        if (File.Exists(target) && ValidateEnvironmentPackage(target, throwOnFailure: false))
        {
            progress(new InstallProgress(to, $"已存在缓存文件：{fileName}", InstallProgressStage.Downloading, 100));
            return target;
        }

        TryDeleteFile(target);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var contentType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (contentType.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"服务器返回了 HTML，未找到环境包：{url}");
        }

        var result = await DownloadService.SaveResponseAsync(
            response,
            target,
            snapshot =>
            {
                var percent = snapshot.TotalBytes is > 0
                    ? from + (to - from) * snapshot.BytesReceived / snapshot.TotalBytes.Value
                    : from;
                var stagePercent = snapshot.TotalBytes is > 0
                    ? Compat.Clamp(snapshot.BytesReceived * 100d / snapshot.TotalBytes.Value, 0, 100)
                    : (double?)null;
                var downloadedText = snapshot.TotalBytes is > 0
                    ? $"{ProductTransferFormatting.FormatBytes(snapshot.BytesReceived)} / {ProductTransferFormatting.FormatBytes(snapshot.TotalBytes.Value)}"
                    : ProductTransferFormatting.FormatBytes(snapshot.BytesReceived);
                progress(new InstallProgress(
                    Compat.Clamp(percent, from, to),
                    $"正在下载 {fileName}：{downloadedText}",
                    InstallProgressStage.Downloading,
                    stagePercent,
                    ProductTransferFormatting.FormatRate(snapshot.BytesPerSecond)));
            },
            cancellationToken: cancellationToken,
            validatePartial: path => ValidateEnvironmentPackage(
                path,
                throwOnFailure: true,
                expectedFileName: fileName),
            emptyFileMessage: $"下载文件为空：{fileName}",
            incompleteFileMessage: (expected, actual) =>
                $"下载不完整：{fileName} 应为 {expected} 字节，实际 {actual} 字节。");

        progress(new InstallProgress(
            to,
            $"{fileName} 下载完成",
            InstallProgressStage.Downloading,
            100,
            ProductTransferFormatting.FormatRate(result.AverageBytesPerSecond)));
        return target;
    }

    private static bool ValidateEnvironmentPackage(string path, bool throwOnFailure, string? expectedFileName = null)
    {
        try
        {
            var fileName = expectedFileName ?? Path.GetFileName(path);
            var extension = Path.GetExtension(fileName);
            var info = new FileInfo(path);
            if (!info.Exists || info.Length == 0)
            {
                throw new InvalidDataException("文件为空。");
            }

            if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
            {
                using var archive = ZipFile.OpenRead(path);
                if (archive.Entries.Count == 0)
                {
                    throw new InvalidDataException("ZIP 包中没有文件。");
                }
            }
            else if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase))
            {
                using var stream = File.OpenRead(path);
                if (stream.ReadByte() != 'M' || stream.ReadByte() != 'Z')
                {
                    throw new InvalidDataException("EXE 文件头无效。");
                }
            }
            else if (extension.Equals(".msi", StringComparison.OrdinalIgnoreCase))
            {
                var signature = new byte[8];
                using var stream = File.OpenRead(path);
                if (stream.Read(signature, 0, signature.Length) != signature.Length ||
                    !signature.SequenceEqual(new byte[] { 0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1 }))
                {
                    throw new InvalidDataException("MSI 文件头无效。");
                }
            }

            return true;
        }
        catch (Exception ex) when (!throwOnFailure)
        {
            _ = ex;
            return false;
        }
        catch (Exception ex) when (throwOnFailure)
        {
            throw new InvalidDataException($"环境安装包校验失败：{expectedFileName ?? Path.GetFileName(path)}。{ex.Message}", ex);
        }
    }

    private static void ExtractZip(string archive, string destination)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var zip = ZipFile.OpenRead(archive);
        foreach (var entry in zip.Entries)
        {
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包包含越界路径：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(target);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            entry.ExtractToFile(target, overwrite: true);
        }
    }

    private static Task ExtractZipAsync(string archive, string destination, CancellationToken cancellationToken)
    {
        return Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ExtractZip(archive, destination);
            cancellationToken.ThrowIfCancellationRequested();
        }, cancellationToken);
    }

    private static async Task RunElevatedPowerShellAsync(
        string scriptPath,
        Action<InstallProgress> progress,
        double from,
        double to,
        CancellationToken cancellationToken,
        bool requireExistingAdministrator = false)
    {
        var isAdministrator = ProcessRunner.IsAdministrator();
        if (requireExistingAdministrator && !isAdministrator)
        {
            throw new InvalidOperationException("该组件必须在已授权的管理员安装会话中运行，请重新点击安装并允许一次 UAC 授权。");
        }

        using var process = ProcessRunner.StartPowerShellFile(scriptPath, elevated: true);
        try
        {
            while (!process.HasExited)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var percent = Math.Min(to, from + 2);
                progress(new InstallProgress(
                    percent,
                    "管理员安装进程正在运行，请等待完成...",
                    InstallProgressStage.Installing,
                    percent));
                await Task.Delay(1000, cancellationToken);
            }

            if (process.ExitCode != 0)
            {
                var logTail = ReadInstallLogTail();
                if (HasSqlServerInstallContinuation)
                {
                    throw new InstallRestartRequiredException(SqlServerInstallContinuationMessage);
                }

                if (HasIisInstallContinuation)
                {
                    throw new InstallRestartRequiredException(IisInstallContinuationMessage);
                }

                var message = $"安装脚本退出码：{process.ExitCode}。日志目录：{GetTempDirectory()}";
                if (!string.IsNullOrWhiteSpace(logTail))
                {
                    message += $"{Environment.NewLine}{Environment.NewLine}最近日志：{Environment.NewLine}{logTail}";
                }

                throw new InvalidOperationException(message);
            }
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
            }

            throw;
        }
    }

    private static string ReadInstallLogTail()
    {
        try
        {
            var directories = new[] { GetTempDirectory(), GetPackageDirectory() }
                .Where(Directory.Exists)
                .Distinct(StringComparer.OrdinalIgnoreCase);
            var latestLog = directories
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*.log", SearchOption.TopDirectoryOnly))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .FirstOrDefault();
            if (latestLog is null)
            {
                return string.Empty;
            }

            var text = ReadTextBestEffort(latestLog.FullName);
            var sqlHint = BuildSqlServerFailureHint(text);
            if (!string.IsNullOrWhiteSpace(sqlHint))
            {
                return sqlHint;
            }

            var lines = text
                .Split(["\r\n", "\n"], StringSplitOptions.None)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Where(line => !LooksLikeMojibake(line))
                .TakeLast(16);
            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }

    private static string ReadTextBestEffort(string path)
    {
        const int maximumBytes = 512 * 1024;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var offset = stream.Length > maximumBytes ? stream.Length - maximumBytes : 0;
        stream.Seek(offset, SeekOrigin.Begin);
        var buffer = new byte[(int)Math.Min(maximumBytes, stream.Length - offset)];
        var read = 0;
        while (read < buffer.Length)
        {
            var chunk = stream.Read(buffer, read, buffer.Length - read);
            if (chunk <= 0) break;
            read += chunk;
        }

        var bytes = read == buffer.Length ? buffer : buffer.Take(read).ToArray();
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
        {
            return Encoding.UTF8.GetString(bytes);
        }

        if (bytes.Length >= 2)
        {
            if (bytes[0] == 0xFF && bytes[1] == 0xFE)
            {
                return Encoding.Unicode.GetString(bytes);
            }

            if (bytes[0] == 0xFE && bytes[1] == 0xFF)
            {
                return Encoding.BigEndianUnicode.GetString(bytes);
            }
        }

        var utf8 = Encoding.UTF8.GetString(bytes);
        if (!LooksLikeMojibake(utf8))
        {
            return utf8;
        }

        try
        {
            var ansi = Encoding.GetEncoding(CultureInfo.CurrentCulture.TextInfo.ANSICodePage).GetString(bytes);
            return LooksLikeMojibake(ansi) ? utf8 : ansi;
        }
        catch
        {
            return utf8;
        }
    }

    private static bool LooksLikeMojibake(string text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var markers = new[]
        {
            "\u7ECC", "\u93C1", "\u6FB6", "\u8FAB", "\u89E6", "\u7F02", "\u7039",
            "\u9354", "\u95C3", "\u5A09", "\u7487", "\u9286", "\u20AC"
        };
        return markers.Count(marker => text.Contains(marker, StringComparison.Ordinal)) >= 2;
    }

    private static async Task RunProcessAsync(string fileName, string arguments, string workingDirectory, bool elevated, CancellationToken cancellationToken)
    {
        using var process = ProcessRunner.Start(fileName, arguments, workingDirectory, elevated);
        await ProcessLifecycle.WaitForExitAsync(process, cancellationToken);
        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"{Path.GetFileName(fileName)} 退出码：{process.ExitCode}");
        }
    }

    private static async Task RunProcessWithOutputAsync(string fileName, string arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunAsync(
            fileName,
            arguments,
            workingDirectory,
            elevated: false,
            cancellationToken: cancellationToken,
            captureOutput: true);
        if (result.ExitCode != 0)
        {
            var detail = result.CombinedOutput;
            if (string.IsNullOrWhiteSpace(detail))
            {
                detail = $"{Path.GetFileName(fileName)} 退出码：{result.ExitCode}";
            }

            throw new InvalidOperationException(detail);
        }
    }

    private static string GetTempDirectory()
    {
        var work = Path.Combine(GetStoreDataRoot(), "Work");
        Directory.CreateDirectory(work);
        return work;
    }

    private static string GetPackageDirectory()
    {
        var packages = ComponentPaths.EnvironmentDownloadRoot;
        Directory.CreateDirectory(packages);
        return packages;
    }

    private static string GetStoreDataRoot()
    {
        var root = ComponentPaths.StoreDataRoot;
        Directory.CreateDirectory(root);
        return root;
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup for local state markers.
        }
    }

    private static string? FindDirectory(string root, string name)
    {
        return Directory.Exists(Path.Combine(root, name))
            ? Path.Combine(root, name)
            : Directory.GetDirectories(root, name, SearchOption.AllDirectories).FirstOrDefault();
    }

    private static bool WindowsServiceExists(string serviceName)
    {
        try
        {
            return ProcessRunner.RunSynchronously(
                "sc.exe",
                $"query {Compat.QuoteCommandLineArgument(serviceName)}",
                ComponentPaths.ApplicationRoot,
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3)).ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsSamePath(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private static void DeleteDirectoryBestEffort(string path)
    {
        try
        {
            if (!Directory.Exists(path))
            {
                return;
            }

            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }

    private static string EscapePowerShellPath(string path) => path.Replace("'", "''");
    private static string IisPendingUninstallMarker => Path.Combine(GetStoreDataRoot(), "RuntimeState", "iis-pending-uninstall-restart.flag");
    private static string IisUninstalledMarker => Path.Combine(GetStoreDataRoot(), "RuntimeState", "iis-uninstalled.flag");

    public void Dispose() => _httpClient.Dispose();

    private sealed class MySqlRuntimeTransaction : IDisposable
    {
        private readonly string _stagingContainer;
        private readonly string _backupRoot;
        private bool _finished;

        private MySqlRuntimeTransaction(string stagingContainer, string productRoot, string backupRoot)
        {
            _stagingContainer = stagingContainer;
            ProductRoot = productRoot;
            _backupRoot = backupRoot;
        }

        public string ProductRoot { get; }

        public static MySqlRuntimeTransaction Commit(
            string stagingContainer,
            string stagedRoot,
            string targetRoot,
            string rollbackBaseRoot)
        {
            var rollbackDirectory = Path.Combine(rollbackBaseRoot, ".mysql-rollback");
            Directory.CreateDirectory(rollbackDirectory);
            var backupRoot = Path.Combine(rollbackDirectory, $"mysql.{Guid.NewGuid():N}");

            if (Directory.Exists(targetRoot))
            {
                Directory.Move(targetRoot, backupRoot);
            }

            try
            {
                var oldData = Path.Combine(backupRoot, "data");
                var stagedData = Path.Combine(stagedRoot, "data");
                if (Directory.Exists(oldData))
                {
                    DeleteDirectoryBestEffort(stagedData);
                    Directory.Move(oldData, stagedData);
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetRoot)!);
                Directory.Move(stagedRoot, targetRoot);
                return new MySqlRuntimeTransaction(stagingContainer, targetRoot, backupRoot);
            }
            catch
            {
                RestoreBackup(targetRoot, backupRoot);
                DeleteDirectoryBestEffort(stagingContainer);
                throw;
            }
        }

        public void Complete()
        {
            if (_finished)
            {
                return;
            }

            DeleteDirectoryBestEffort(_backupRoot);
            DeleteDirectoryBestEffort(_stagingContainer);
            _finished = true;
        }

        public void Rollback()
        {
            if (_finished)
            {
                return;
            }

            RestoreBackup(ProductRoot, _backupRoot);
            DeleteDirectoryBestEffort(_stagingContainer);
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Rollback();
            }
        }

        private static void RestoreBackup(string targetRoot, string backupRoot)
        {
            if (Directory.Exists(backupRoot))
            {
                var currentData = Path.Combine(targetRoot, "data");
                var backupData = Path.Combine(backupRoot, "data");
                if (Directory.Exists(currentData) && !Directory.Exists(backupData))
                {
                    Directory.Move(currentData, backupData);
                }
            }

            DeleteDirectoryBestEffort(targetRoot);
            if (Directory.Exists(backupRoot))
            {
                Directory.Move(backupRoot, targetRoot);
            }
        }
    }
}
