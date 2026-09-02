using System.IO;
using System.Windows.Interop;
using SharpSvn;
using SharpSvn.UI;
using WinFormsWindow = System.Windows.Forms.IWin32Window;

namespace MCPanel;

internal sealed class SvnProductTransferService
{
    private const string UserName = "_update_server";
    private const string Password = "_update086405849";

    public Task<string> SyncAsync(
        Uri repositoryUri,
        string installedDirectory,
        Action<double>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController = null)
    {
        Action<ProductDownloadProgress>? detailedProgress = progress is null
            ? null
            : update => progress(update.Percent);
        return SyncWithDetailsAsync(
            repositoryUri,
            installedDirectory,
            detailedProgress,
            status,
            cancellationToken,
            pauseController);
    }

    public Task<string> SyncWithDetailsAsync(
        Uri repositoryUri,
        string installedDirectory,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController = null)
    {
        var ownerWindow = CaptureOwnerWindow();
        return Task.Run(
            () =>
            {
                try
                {
                    return Sync(repositoryUri, installedDirectory, progress, status, cancellationToken, pauseController, ownerWindow);
                }
                catch (SvnOperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException("产品 SVN 下载已取消。", exception, cancellationToken);
                }
            },
            cancellationToken);
    }

    private static string Sync(
        Uri repositoryUri,
        string installedDirectory,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController,
        WinFormsWindow? ownerWindow)
    {
        cancellationToken.ThrowIfCancellationRequested();
        pauseController?.WaitIfPaused(cancellationToken);
        var installParent = Path.GetDirectoryName(installedDirectory)
            ?? throw new InvalidOperationException("产品安装目录无效。");
        Directory.CreateDirectory(installParent);

        using var client = CreateClient(repositoryUri, ownerWindow);
        if (Directory.Exists(Path.Combine(installedDirectory, ".svn")))
        {
            try
            {
                client.CleanUp(installedDirectory);
            }
            catch (SvnException)
            {
                // GetInfo/Update below provides the authoritative recovery result.
            }
        }

        if (IsMatchingWorkingCopy(client, installedDirectory, repositoryUri))
        {
            status?.Invoke("已发现供应商 SVN 工作副本，正在检查上次进度与产品更新...");
            progress?.Invoke(new ProductDownloadProgress(2, "正在检查产品增量更新..."));
            UpdateWorkingCopy(client, installedDirectory, progress, status, cancellationToken, pauseController);
            DeleteMigrationBackups(installedDirectory);
            progress?.Invoke(new ProductDownloadProgress(100, "产品增量更新完成"));
            return installedDirectory;
        }

        var legacyBackup = BackupLegacyInstallation(installedDirectory, installParent);
        try
        {
            status?.Invoke("正在扫描并下载产品文件...");
            progress?.Invoke(new ProductDownloadProgress(1, "正在扫描并下载产品文件..."));

            // Checkout already exposes both file notifications and transfer byte
            // progress. Starting it immediately avoids a second full recursive
            // SVN List pass before any product bytes can be downloaded.
            var transfer = new TransferTelemetry(installedDirectory);
            var args = CreateCheckOutArgs(progress, status, cancellationToken, pauseController, transfer);
            if (!client.CheckOut(new SvnUriTarget(EnsureDirectoryUri(repositoryUri)), installedDirectory, args))
            {
                throw (Exception?)args.LastException ?? new InvalidOperationException("供应商 SVN Checkout 未完成。");
            }

            EnsureProductFiles(installedDirectory);
            DeleteMigrationBackups(installedDirectory);
            progress?.Invoke(transfer.CreateProgress(100, "产品文件下载完成", forceTotal: true));
            return installedDirectory;
        }
        catch
        {
            if (IsMatchingWorkingCopy(client, installedDirectory, repositoryUri))
            {
                status?.Invoke("产品下载已中断；可恢复的 SVN 工作副本已保留，下次安装会从现有进度继续补齐。");
            }
            else
            {
                DeleteDirectory(installedDirectory);
                RestoreLegacyInstallation(legacyBackup, installedDirectory);
            }

            throw;
        }
    }

    private static string BackupLegacyInstallation(string installedDirectory, string installParent)
    {
        if (!Directory.Exists(installedDirectory))
        {
            return string.Empty;
        }

        var rollbackRoot = Path.Combine(installParent, ".rollback");
        Directory.CreateDirectory(rollbackRoot);
        var backup = Path.Combine(
            rollbackRoot,
            $"{Path.GetFileName(installedDirectory)}.svn-migration.{Guid.NewGuid():N}");
        Directory.Move(installedDirectory, backup);
        return backup;
    }

    private static void RestoreLegacyInstallation(string backup, string installedDirectory)
    {
        if (string.IsNullOrWhiteSpace(backup) || !Directory.Exists(backup))
        {
            return;
        }

        Directory.Move(backup, installedDirectory);
    }

    private static void DeleteMigrationBackups(string installedDirectory)
    {
        var installParent = Path.GetDirectoryName(installedDirectory);
        var productDirectoryName = Path.GetFileName(installedDirectory.TrimEnd(Path.DirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(installParent) || string.IsNullOrWhiteSpace(productDirectoryName))
        {
            return;
        }

        var rollbackRoot = Path.Combine(installParent, ".rollback");
        if (!Directory.Exists(rollbackRoot))
        {
            return;
        }

        var prefix = $"{productDirectoryName}.svn-migration.";
        foreach (var backup in Directory.EnumerateDirectories(rollbackRoot, $"{prefix}*", SearchOption.TopDirectoryOnly))
        {
            if (Path.GetFileName(backup).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                DeleteDirectory(backup);
            }
        }
    }

    private static void UpdateWorkingCopy(
        SvnClient client,
        string workingCopy,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController)
    {
        cancellationToken.ThrowIfCancellationRequested();
        status?.Invoke("正在整理产品工作副本...");

        try
        {
            client.Upgrade(workingCopy);
        }
        catch (SvnException)
        {
            // Current-format working copies do not need an upgrade.
        }

        try
        {
            client.CleanUp(workingCopy);
        }
        catch (SvnException)
        {
            // Update provides the authoritative error if cleanup cannot run.
        }

        cancellationToken.ThrowIfCancellationRequested();
        var revertArgs = new SvnRevertArgs
        {
            Depth = SvnDepth.Infinity,
            ThrowOnError = true,
            ThrowOnCancel = true
        };
        revertArgs.Cancel += (_, e) =>
        {
            e.Cancel = !WaitForTransferPermission(pauseController, cancellationToken);
        };
        if (!client.Revert(workingCopy, revertArgs))
        {
            throw (Exception?)revertArgs.LastException ?? new InvalidOperationException("供应商 SVN Revert 未完成。");
        }

        status?.Invoke("工作副本已整理，正在扫描并下载产品增量更新...");
        var transfer = new TransferTelemetry(workingCopy);
        var args = CreateUpdateArgs(progress, status, cancellationToken, pauseController, transfer);
        if (!client.Update(workingCopy, args))
        {
            throw (Exception?)args.LastException ?? new InvalidOperationException("供应商 SVN Update 未完成。");
        }

        EnsureProductFiles(workingCopy);
    }

    private static SvnClient CreateClient(Uri repositoryUri, WinFormsWindow? ownerWindow)
    {
        var client = new SvnClient();
        client.Authentication.UserNamePasswordHandlers += (_, e) =>
        {
            e.UserName = UserName;
            e.Password = Password;
        };

        // This is the same UI binding used by the original UpdateClient. It
        // supplies SharpSvn's certificate-trust flow instead of failing with
        // "issuer is not trusted" before the repository can be checked out.
        // Local file:// repositories used by regression tests do not need a
        // UI handler and must remain non-interactive.
        if (repositoryUri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) ||
            repositoryUri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            // SvnUI.Bind is the non-obsolete name for the same SharpSvn.UI
            // binding exposed as SharpSvnUI.Bind by the original binary.
            SvnUI.Bind(client, ownerWindow ?? new NativeWindowHandle(IntPtr.Zero));
        }

        return client;
    }

    private static WinFormsWindow? CaptureOwnerWindow()
    {
        var window = System.Windows.Application.Current?.MainWindow;
        if (window is null)
        {
            return null;
        }

        var handle = new WindowInteropHelper(window).Handle;
        return handle == IntPtr.Zero ? null : new NativeWindowHandle(handle);
    }

    private static SvnCheckOutArgs CreateCheckOutArgs(
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController,
        TransferTelemetry transfer)
    {
        var args = new SvnCheckOutArgs
        {
            Depth = SvnDepth.Infinity,
            IgnoreExternals = false,
            ThrowOnError = true,
            ThrowOnCancel = true
        };
        AttachProgress(args, progress, status, cancellationToken, pauseController, "扫描并下载", transfer);
        return args;
    }

    private static SvnUpdateArgs CreateUpdateArgs(
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController,
        TransferTelemetry transfer)
    {
        var args = new SvnUpdateArgs
        {
            Depth = SvnDepth.Infinity,
            KeepDepth = true,
            IgnoreExternals = false,
            ThrowOnError = true,
            ThrowOnCancel = true
        };
        AttachProgress(args, progress, status, cancellationToken, pauseController, "扫描并更新", transfer);
        return args;
    }

    private static void AttachProgress(
        SvnClientArgs args,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        CancellationToken cancellationToken,
        DownloadPauseController? pauseController,
        string operation,
        TransferTelemetry transfer)
    {
        var lastStatus = DateTime.MinValue;
        var lastProgress = 1d;

        void ReportProgress(string message)
        {
            var snapshot = transfer.CreateProgress(lastProgress, message);
            if (snapshot.TotalBytes is > 0)
            {
                var candidate = 3 + snapshot.BytesReceived * 94d / snapshot.TotalBytes.Value;
                lastProgress = Math.Max(lastProgress, Compat.Clamp(candidate, 3, 98));
            }

            progress?.Invoke(transfer.CreateProgress(lastProgress, message));
        }

        args.Cancel += (_, e) =>
        {
            e.Cancel = !WaitForTransferPermission(pauseController, cancellationToken);
        };
        args.Progress += (_, e) =>
        {
            if (!WaitForTransferPermission(pauseController, cancellationToken))
            {
                return;
            }

            transfer.ObserveProgress(Convert.ToInt64(e.Progress), Convert.ToInt64(e.TotalProgress));
            var snapshot = transfer.CreateProgress(lastProgress, $"正在{operation}：已传输 {ProductTransferFormatting.FormatBytes(transfer.DownloadedBytes)}");
            if (snapshot.TotalBytes is > 0)
            {
                var candidate = 3 + snapshot.BytesReceived * 94d / snapshot.TotalBytes.Value;
                lastProgress = Math.Max(lastProgress, Compat.Clamp(candidate, 3, 98));
            }

            progress?.Invoke(transfer.CreateProgress(lastProgress, snapshot.Message));
        };
        args.Notify += (_, e) =>
        {
            if (!WaitForTransferPermission(pauseController, cancellationToken))
            {
                return;
            }

            transfer.ObserveNotify(e);

            var now = DateTime.UtcNow;
            if (now - lastStatus < TimeSpan.FromMilliseconds(250))
            {
                return;
            }

            lastStatus = now;
            var path = string.IsNullOrWhiteSpace(e.Path) ? e.Uri?.AbsolutePath : e.Path;
            var display = string.IsNullOrWhiteSpace(path)
                ? "产品文件"
                : Path.GetFileName(path!.TrimEnd('/', '\\'));
            var statusText = $"正在{operation}：{display}（已扫描 {transfer.ScannedFiles:N0} 项，{FormatBytes(transfer.ScannedBytes)}）";
            status?.Invoke(statusText);
            ReportProgress(statusText);
        };
    }

    private static bool WaitForTransferPermission(
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return false;
        }

        try
        {
            pauseController?.WaitIfPaused(cancellationToken);
            return !cancellationToken.IsCancellationRequested;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private static string FormatBytes(long bytes) => ProductTransferFormatting.FormatBytes(bytes);

    private sealed class TransferTelemetry
    {
        private readonly string _workingCopyRoot;
        private readonly object _sync = new();
        private readonly HashSet<string> _scannedPaths = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _pendingSizePaths = new(StringComparer.OrdinalIgnoreCase);
        private long _scannedBytes;
        private int _scannedFiles;
        private long _downloadedBytes;
        private long? _totalBytes;
        private long _lastRateBytes;
        private DateTime _lastRateAt;
        private bool _hasRateSample;
        private string? _speedText;

        public TransferTelemetry(string workingCopyRoot)
        {
            _workingCopyRoot = Path.GetFullPath(workingCopyRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }

        public int ScannedFiles
        {
            get
            {
                lock (_sync)
                {
                    return _scannedFiles;
                }
            }
        }

        public long ScannedBytes
        {
            get
            {
                lock (_sync)
                {
                    RefreshPendingSizesLocked();
                    return _scannedBytes;
                }
            }
        }

        public long DownloadedBytes
        {
            get
            {
                lock (_sync)
                {
                    RefreshPendingSizesLocked();
                    return _downloadedBytes;
                }
            }
        }

        public void ObserveNotify(SvnNotifyEventArgs notification)
        {
            if (notification.NodeKind != SvnNodeKind.File)
            {
                return;
            }

            var path = ResolveLocalPath(notification);
            if (path is null)
            {
                return;
            }

            lock (_sync)
            {
                if (!_scannedPaths.Add(path))
                {
                    return;
                }

                _scannedFiles++;
                _pendingSizePaths.Add(path);
                RefreshPendingSizesLocked();
                RecordDownloadedLocked(_scannedBytes, DateTime.UtcNow);
            }
        }

        public void ObserveProgress(long downloadedBytes, long totalBytes)
        {
            lock (_sync)
            {
                RefreshPendingSizesLocked();
                if (totalBytes > 0)
                {
                    _totalBytes = Math.Max(_totalBytes ?? 0, totalBytes);
                }

                RecordDownloadedLocked(Math.Max(downloadedBytes, _scannedBytes), DateTime.UtcNow);
            }
        }

        public ProductDownloadProgress CreateProgress(double percent, string message, bool forceTotal = false)
        {
            lock (_sync)
            {
                RefreshPendingSizesLocked();
                var totalBytes = forceTotal
                    ? Math.Max(_totalBytes ?? 0, _downloadedBytes)
                    : _totalBytes;
                var speedText = _hasRateSample &&
                                 DateTime.UtcNow - _lastRateAt <= TimeSpan.FromSeconds(2)
                    ? _speedText
                    : null;
                return new ProductDownloadProgress(
                    percent,
                    message,
                    _downloadedBytes,
                    totalBytes > 0 ? totalBytes : null,
                    speedText,
                    _scannedFiles,
                    _scannedBytes);
            }
        }

        private void RefreshPendingSizesLocked()
        {
            foreach (var path in _pendingSizePaths.ToArray())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                try
                {
                    _scannedBytes += Math.Max(0, new FileInfo(path).Length);
                    _pendingSizePaths.Remove(path);
                }
                catch (IOException)
                {
                    // The file can still be in the process of being materialized.
                }
                catch (UnauthorizedAccessException)
                {
                    // The file may be temporarily locked by the checkout process.
                }
            }
        }

        private void RecordDownloadedLocked(long candidateBytes, DateTime now)
        {
            candidateBytes = Math.Max(candidateBytes, _downloadedBytes);
            if (candidateBytes <= _downloadedBytes)
            {
                return;
            }

            if (_hasRateSample)
            {
                var elapsedSeconds = (now - _lastRateAt).TotalSeconds;
                if (elapsedSeconds >= 0.05)
                {
                    _speedText = ProductTransferFormatting.FormatRate(
                        (candidateBytes - _lastRateBytes) / elapsedSeconds);
                }
            }

            _downloadedBytes = candidateBytes;
            _lastRateBytes = candidateBytes;
            _lastRateAt = now;
            _hasRateSample = true;
        }

        private string? ResolveLocalPath(SvnNotifyEventArgs notification)
        {
            var candidate = notification.FullPath;
            if (string.IsNullOrWhiteSpace(candidate))
            {
                candidate = notification.Path;
            }

            if (string.IsNullOrWhiteSpace(candidate))
            {
                return null;
            }

            try
            {
                if (!Path.IsPathRooted(candidate))
                {
                    candidate = Path.Combine(_workingCopyRoot, candidate);
                }

                var fullPath = Path.GetFullPath(candidate)
                    .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                if (fullPath.Equals(_workingCopyRoot, StringComparison.OrdinalIgnoreCase) ||
                    !fullPath.StartsWith(_workingCopyRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                var relative = fullPath.Substring(_workingCopyRoot.Length + 1);
                if (relative.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
                    relative.StartsWith($".svn{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                return fullPath;
            }
            catch (ArgumentException)
            {
                return null;
            }
        }
    }

    private static bool IsMatchingWorkingCopy(SvnClient client, string path, Uri repositoryUri)
    {
        if (!Directory.Exists(path) || !Directory.Exists(Path.Combine(path, ".svn")))
        {
            return false;
        }

        try
        {
            return client.GetInfo(new SvnPathTarget(path), out var info) &&
                   info.Uri is not null &&
                   Uri.Compare(
                       EnsureDirectoryUri(info.Uri),
                       EnsureDirectoryUri(repositoryUri),
                       UriComponents.SchemeAndServer | UriComponents.Path,
                       UriFormat.SafeUnescaped,
                       StringComparison.OrdinalIgnoreCase) == 0;
        }
        catch (SvnException)
        {
            return false;
        }
    }

    private static void EnsureProductFiles(string path)
    {
        var hasProductFile = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)
            .Any(file => !IsSvnMetadata(file, path));
        if (!hasProductFile)
        {
            throw new InvalidDataException("供应商 SVN 返回的产品目录为空。");
        }
    }

    private static bool IsSvnMetadata(string file, string root)
    {
        var relative = file.Substring(root.TrimEnd(Path.DirectorySeparatorChar).Length + 1);
        return relative.Equals(".svn", StringComparison.OrdinalIgnoreCase) ||
               relative.StartsWith($".svn{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri EnsureDirectoryUri(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri(uri.AbsoluteUri + "/", UriKind.Absolute);

    private static void DeleteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, recursive: true);
    }

    private sealed class NativeWindowHandle : WinFormsWindow
    {
        public NativeWindowHandle(IntPtr handle)
        {
            Handle = handle;
        }

        public IntPtr Handle { get; }
    }
}
