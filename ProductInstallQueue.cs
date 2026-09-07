using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace MCPanel;

public enum ProductInstallQueueStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    Cancelled
}

/// <summary>
/// Owns the durable product installation queue in the unelevated UI process.
/// The elevated worker only consumes request files from the current session;
/// all ordering, recovery, cancellation and UI state stays here.
/// </summary>
internal sealed class ProductInstallQueueService : IDisposable
{
    private const int DocumentVersion = 1;
    private const int HistoryLimit = 20;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly InstallationProgressViewModel _progress;
    private readonly string _storeDataRoot;
    private readonly string _statePath;
    private readonly string _queueWorkRoot;
    private readonly bool _autoStartWorker;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly string _sessionRoot;
    private readonly string _stopPath;
    private readonly string _heartbeatPath;
    private Process? _worker;
    private DateTime _lastHeartbeatUtc = DateTime.MinValue;
    private DateTime _lastPersistUtc = DateTime.MinValue;
    private int _nextSequence;
    private bool _disposed;

    public ProductInstallQueueService(
        InstallationProgressViewModel progress,
        string? storeDataRoot = null,
        bool autoStartWorker = true)
    {
        _progress = progress;
        _storeDataRoot = Path.GetFullPath(storeDataRoot ?? ComponentPaths.StoreDataRoot);
        _statePath = Path.Combine(_storeDataRoot, "ProductState", "install-queue.json");
        _queueWorkRoot = Path.Combine(_storeDataRoot, "Work", "ProductInstallQueue");
        _autoStartWorker = autoStartWorker;
        _sessionRoot = Path.Combine(_queueWorkRoot, _sessionId);
        _stopPath = Path.Combine(_sessionRoot, "stop.flag");
        _heartbeatPath = Path.Combine(_sessionRoot, "heartbeat.flag");

        Directory.CreateDirectory(_sessionRoot);
        TouchHeartbeat();
        LoadPersistedQueue();
        RefreshQueuePositions();
        QueueSessionMaintenance.PruneInactiveSessions(_sessionRoot);
    }

    public ObservableCollection<ProductInstallQueueItemViewModel> Items => _progress.QueueItems;

    public bool HasActiveItems => Items.Any(item => !item.IsTerminal);

    public ProductInstallQueueItemViewModel? CurrentItem =>
        Items
            .Where(item => !item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .FirstOrDefault();

    public event Action<ProductInstallQueueItemViewModel>? ItemChanged;
    public event Action<ProductInstallQueueItemViewModel, ProductInstallWorkerProgress>? ProgressChanged;
    public event Action<ProductInstallQueueItemViewModel, ProductInstallWorkerProgress>? ItemFinished;

    public bool HasActiveOrQueued(string productId) =>
        Items.Any(item =>
            !item.IsTerminal &&
            string.Equals(item.ProductId, productId, StringComparison.OrdinalIgnoreCase));

    public ProductInstallQueueItemViewModel Enqueue(ProductItem product, bool isUpdate)
    {
        ThrowIfDisposed();
        if (HasActiveOrQueued(product.ProductId))
        {
            throw new InvalidOperationException("该产品已经在安装队列中，请等待当前任务完成。");
        }

        var sequence = ++_nextSequence;
        var queueId = Guid.NewGuid().ToString("N");
        var request = ProductInstallWorker.CreateRequest(product, isUpdate);
        var item = new ProductInstallQueueItemViewModel(
            queueId,
            sequence,
            request,
            ProductInstallQueueStatus.Pending,
            0,
            $"已加入安装队列，等待第 {sequence} 项处理。",
            requestedAtUtc: DateTime.UtcNow);

        CreateSessionFiles(item);
        Items.Add(item);
        RefreshQueuePositions();
        try
        {
            Persist(throwOnFailure: true);
            if (_autoStartWorker)
            {
                EnsureWorkerStarted();
            }
        }
        catch (Exception ex)
        {
            Items.Remove(item);
            DeleteItemFiles(item);
            RefreshQueuePositions();
            FailActiveItems($"后台安装队列启动失败：{ex.Message}");
            throw;
        }

        NotifyItemChanged(item);
        return item;
    }

    public void ResumePending()
    {
        if (_disposed || !HasActiveItems)
        {
            return;
        }

        try
        {
            if (_autoStartWorker)
            {
                EnsureWorkerStarted();
            }
        }
        catch (Exception ex)
        {
            FailActiveItems($"后台安装队列启动失败：{ex.Message}");
            return;
        }

        foreach (var item in Items.Where(item => !item.IsTerminal).OrderBy(item => item.Sequence))
        {
            NotifyItemChanged(item);
        }
    }

    /// <summary>
    /// Called from the existing one-second UI timer. It only reads small
    /// progress files and never performs download or deployment work itself.
    /// </summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        TouchHeartbeatIfNeeded();
        var activeSnapshot = Items
            .Where(item => !item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .ToArray();

        foreach (var item in activeSnapshot)
        {
            if (!ProductInstallWorker.TryReadProgress(item.ProgressPath, out var update))
            {
                continue;
            }

            var terminalState = ParseTerminalState(update.State);
            if (terminalState is not null)
            {
                FinishItem(item, terminalState.Value, update);
                continue;
            }

            if (string.Equals(update.State, "running", StringComparison.OrdinalIgnoreCase))
            {
                if (item.State != ProductInstallQueueStatus.Running)
                {
                    item.SetState(ProductInstallQueueStatus.Running);
                    NotifyItemChanged(item);
                }

                item.ApplyProgress(update);
                if (!item.IsDownloading && item.IsPaused)
                {
                    ProductInstallWorker.DeleteSessionFile(item.PausePath);
                    item.SetPaused(false);
                }
                ProgressChanged?.Invoke(item, update);
            }
        }

        var remaining = Items
            .Where(item => !item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .ToArray();
        if (_autoStartWorker && remaining.Length > 0 && IsWorkerExited())
        {
            var exitMessage = ReadWorkerError();
            var exitCode = TryGetWorkerExitCode();
            if (exitCode == 0 &&
                string.IsNullOrWhiteSpace(exitMessage) &&
                remaining.All(item =>
                    item.State == ProductInstallQueueStatus.Pending &&
                    File.Exists(item.RequestPath)))
            {
                try
                {
                    EnsureWorkerStarted();
                    PersistIfNeeded();
                    return;
                }
                catch (Exception ex)
                {
                    exitMessage = $"后台安装队列自动续接失败：{ex.Message}";
                }
            }

            var message = string.IsNullOrWhiteSpace(exitMessage)
                ? $"后台安装会话异常退出（退出码 {exitCode}）。可以点击产品按钮重试。"
                : exitMessage!;
            FailActiveItems(message);
        }

        PersistIfNeeded();
    }

    public ProductInstallQueueItemViewModel? CancelCurrent()
    {
        var item = CurrentItem;
        return item is null ? null : Cancel(item.QueueId) ? item : null;
    }

    public bool Cancel(string queueId)
    {
        ThrowIfDisposed();
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.QueueId, queueId, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.IsTerminal)
        {
            return false;
        }

        // Publish the cancellation marker before removing a pending request.
        // The worker can atomically claim the request between File.Exists and
        // File.Delete; the marker makes that hand-off safe as well.
        TouchControlFile(item.CancelPath, throwOnFailure: true);
        if (item.State == ProductInstallQueueStatus.Pending &&
            ProductInstallWorker.TryWithdrawQueueRequest(item.RequestPath))
        {
            FinishItem(
                item,
                ProductInstallQueueStatus.Cancelled,
                new ProductInstallWorkerProgress(
                    "cancelled",
                    0,
                    "已取消排队，未开始下载。",
                    InstallProgressStage.Preparing,
                    0));
            return true;
        }

        item.SetMessage("正在取消当前安装，请稍候...");
        NotifyItemChanged(item);
        Persist();
        return true;
    }

    public bool? TogglePause()
    {
        var item = Items.FirstOrDefault(candidate =>
            candidate.State == ProductInstallQueueStatus.Running);
        return item is null ? null : TogglePause(item.QueueId);
    }

    public bool? TogglePause(string queueId)
    {
        ThrowIfDisposed();
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.QueueId, queueId, StringComparison.OrdinalIgnoreCase));
        if (item is null || !item.CanTogglePause)
        {
            return null;
        }

        if (File.Exists(item.PausePath))
        {
            ProductInstallWorker.DeleteSessionFile(item.PausePath);
            if (File.Exists(item.PausePath))
            {
                throw new InvalidOperationException("无法清除暂停标记，下载尚未继续。请检查 StoreData 目录写入权限。");
            }

            item.SetPaused(false);
            item.SetMessage("正在继续下载...");
            NotifyItemChanged(item);
            Persist();
            return false;
        }

        TouchControlFile(item.PausePath, throwOnFailure: true);
        item.SetPaused(true);
        item.SetMessage("下载已暂停，点击“继续”恢复。");
        NotifyItemChanged(item);
        Persist();
        return true;
    }

    public bool Remove(string queueId)
    {
        ThrowIfDisposed();
        var item = Items.FirstOrDefault(candidate =>
            string.Equals(candidate.QueueId, queueId, StringComparison.OrdinalIgnoreCase));
        if (item is null || item.IsRemovalRequested)
        {
            return false;
        }

        if (item.IsTerminal)
        {
            RemoveItem(item);
            return true;
        }

        // "删除" on an active row means cancel safely first. Pending requests
        // can be removed immediately; a claimed/running request stays visible
        // as "正在删除" until the worker confirms its terminal state.
        TouchControlFile(item.CancelPath, throwOnFailure: true);
        item.MarkRemovalRequested();
        if (item.State == ProductInstallQueueStatus.Pending &&
            ProductInstallWorker.TryWithdrawQueueRequest(item.RequestPath))
        {
            FinishItem(
                item,
                ProductInstallQueueStatus.Cancelled,
                new ProductInstallWorkerProgress(
                    "cancelled",
                    0,
                    "已从安装队列删除，未开始下载。",
                    InstallProgressStage.Preparing,
                    0));
            return true;
        }

        item.SetMessage("正在取消并删除此任务，请稍候...");
        NotifyItemChanged(item);
        Persist();
        return true;
    }

    public void SyncActiveItems()
    {
        foreach (var item in Items.Where(item => !item.IsTerminal).OrderBy(item => item.Sequence))
        {
            NotifyItemChanged(item);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var item in Items.Where(item => item.State == ProductInstallQueueStatus.Running))
        {
            TouchControlFile(item.CancelPath);
        }

        TouchControlFile(_stopPath);
        Persist();
        try
        {
            _worker?.Dispose();
        }
        catch
        {
        }

        _worker = null;
    }

    private void LoadPersistedQueue()
    {
        ProductInstallQueueDocument? document = null;
        try
        {
            if (File.Exists(_statePath))
            {
                document = JsonSerializer.Deserialize<ProductInstallQueueDocument>(
                    File.ReadAllText(_statePath, Encoding.UTF8),
                    JsonOptions);
            }
        }
        catch (Exception ex)
        {
            WriteQueueDiagnostic($"读取安装队列状态失败：{ex.Message}");
        }

        if (document is null || document.Version != DocumentVersion)
        {
            Persist();
            return;
        }

        StopPreviousSession(document.SessionId);
        foreach (var persisted in document.Items ?? [])
        {
            if (persisted.Request is null ||
                string.IsNullOrWhiteSpace(persisted.QueueId) ||
                persisted.Sequence <= 0)
            {
                continue;
            }

            // A row the user deleted must never be resurrected after an
            // explicit application restart. The previous worker has already
            // received its session stop marker above.
            if (persisted.IsRemovalRequested)
            {
                continue;
            }

            var progress = TryReadPersistedProgress(persisted.ProgressPath);
            var recoveredTerminalState = !IsTerminal(persisted.State)
                ? ParseTerminalState(progress?.State)
                : null;
            var state = recoveredTerminalState ?? persisted.State;
            var isTerminal = IsTerminal(state);
            var recoveredTerminalProgress = recoveredTerminalState is not null ? progress : null;
            var message = isTerminal && !string.IsNullOrWhiteSpace(recoveredTerminalProgress?.Message)
                ? recoveredTerminalProgress!.Message
                : string.IsNullOrWhiteSpace(persisted.Message)
                    ? "已恢复安装队列，等待继续处理。"
                    : persisted.Message;
            var restoredProgress = isTerminal
                ? state == ProductInstallQueueStatus.Completed
                    ? 100d
                    : Compat.Clamp(recoveredTerminalProgress?.Percent ?? persisted.Progress, 0, 100)
                : 0d;
            var item = new ProductInstallQueueItemViewModel(
                persisted.QueueId,
                persisted.Sequence,
                persisted.Request,
                state,
                restoredProgress,
                message,
                completedAtUtc: isTerminal
                    ? persisted.CompletedAtUtc ?? TryGetCompletionTimeUtc(persisted.ProgressPath)
                    : null,
                requestedAtUtc: persisted.RequestedAtUtc ?? TryGetRequestedTimeUtc(persisted.ProgressPath));

            if (isTerminal)
            {
                item.SetResult(
                    recoveredTerminalProgress?.ResultPath ?? persisted.ResultPath,
                    recoveredTerminalProgress?.LogPath ?? persisted.LogPath);
                RestoreTerminalPaths(item, persisted);
                Items.Add(item);
                _nextSequence = Math.Max(_nextSequence, item.Sequence);
                continue;
            }

            item.SetState(ProductInstallQueueStatus.Pending);
            item.SetProgress(0);
            item.SetMessage("已恢复安装队列，等待继续处理。");
            CreateSessionFiles(item);

            Items.Add(item);
            _nextSequence = Math.Max(_nextSequence, item.Sequence);
        }

        TrimHistory();
        Persist();
    }

    private static DateTime? TryGetCompletionTimeUtc(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath))
        {
            return null;
        }

        try
        {
            return File.Exists(progressPath) ? File.GetLastWriteTimeUtc(progressPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private static DateTime? TryGetRequestedTimeUtc(string? progressPath)
    {
        if (string.IsNullOrWhiteSpace(progressPath))
        {
            return null;
        }

        try
        {
            return File.Exists(progressPath) ? File.GetCreationTimeUtc(progressPath) : null;
        }
        catch
        {
            return null;
        }
    }

    private void StopPreviousSession(string? previousSessionId)
    {
        if (string.IsNullOrWhiteSpace(previousSessionId) ||
            !Guid.TryParseExact(previousSessionId, "N", out _))
        {
            return;
        }

        var previousRoot = Path.Combine(_queueWorkRoot, previousSessionId);
        if (!IsPathInside(_queueWorkRoot, previousRoot))
        {
            return;
        }

        TouchControlFile(Path.Combine(previousRoot, "stop.flag"));
    }

    private void CreateSessionFiles(ProductInstallQueueItemViewModel item)
    {
        item.SetPaths(
            ProductInstallWorker.CreateProgressFile(_sessionRoot, item.QueueId),
            ProductInstallWorker.CreateControlFile(_sessionRoot, item.QueueId, "cancel"),
            ProductInstallWorker.CreateControlFile(_sessionRoot, item.QueueId, "pause"),
            ProductInstallWorker.CreateQueueEnvelopeFile(_sessionRoot, item.Sequence, item.QueueId));
        var envelope = new ProductInstallQueueEnvelope(
            item.QueueId,
            item.Sequence,
            item.Request,
            item.ProgressPath,
            item.CancelPath,
            item.PausePath);
        AtomicFile.WriteAllText(
            item.RequestPath,
            JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private void EnsureWorkerStarted()
    {
        if (_worker is not null)
        {
            try
            {
                if (!_worker.HasExited)
                {
                    return;
                }
            }
            catch
            {
            }

            try
            {
                _worker.Dispose();
            }
            catch
            {
            }

            _worker = null;
        }

        ProductInstallWorker.DeleteSessionFile(_stopPath);
        ProductInstallWorker.DeleteSessionFile(Path.Combine(_sessionRoot, "worker.error.txt"));
        TouchHeartbeat();
        _worker = ProductInstallWorker.StartQueue(_sessionRoot, _stopPath, _heartbeatPath);
    }

    private void FinishItem(
        ProductInstallQueueItemViewModel item,
        ProductInstallQueueStatus state,
        ProductInstallWorkerProgress update)
    {
        if (item.IsTerminal)
        {
            return;
        }

        var removeAfterFinish = item.IsRemovalRequested;
        ProductInstallWorker.DeleteSessionFile(item.PausePath);
        item.SetState(state);
        item.ApplyProgress(update);
        item.SetMessage(update.Message);
        item.SetResult(update.ResultPath, update.LogPath);
        RefreshQueuePositions();
        NotifyItemChanged(item);
        ItemFinished?.Invoke(item, update);
        if (removeAfterFinish)
        {
            RemoveItem(item);
            return;
        }

        TrimHistory();
        // Persist terminal transitions immediately. This covers fast installs
        // and cancellation of a still-pending item, where the throttled timer
        // may otherwise have no active item left to trigger a write.
        Persist();
    }

    private void RemoveItem(ProductInstallQueueItemViewModel item)
    {
        if (!Items.Remove(item))
        {
            return;
        }

        DeleteItemFiles(item);
        RefreshQueuePositions();
        Persist();
    }

    private void FailActiveItems(string message)
    {
        var active = Items
            .Where(item => !item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .ToArray();
        foreach (var item in active)
        {
            var update = new ProductInstallWorkerProgress(
                "failed",
                item.Progress,
                message,
                InstallProgressStage.Installing,
                0);
            FinishItem(item, ProductInstallQueueStatus.Failed, update);
        }

        Persist();
    }

    private bool IsWorkerExited()
    {
        if (_worker is null)
        {
            return true;
        }

        try
        {
            return _worker.HasExited;
        }
        catch
        {
            return true;
        }
    }

    private int TryGetWorkerExitCode()
    {
        if (_worker is null)
        {
            return -1;
        }

        try
        {
            return _worker.HasExited ? _worker.ExitCode : 0;
        }
        catch
        {
            return -1;
        }
    }

    private string? ReadWorkerError()
    {
        try
        {
            var path = Path.Combine(_sessionRoot, "worker.error.txt");
            return File.Exists(path)
                ? File.ReadAllText(path, Encoding.UTF8).Trim()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private ProductInstallWorkerProgress? TryReadPersistedProgress(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var persistedPath = path!;
        return IsPathInside(_queueWorkRoot, persistedPath) &&
               ProductInstallWorker.TryReadProgress(persistedPath, out var progress)
            ? progress
            : null;
    }

    private void RestoreTerminalPaths(
        ProductInstallQueueItemViewModel item,
        ProductInstallQueuePersistedItem persisted)
    {
        if (string.IsNullOrWhiteSpace(persisted.ProgressPath))
        {
            return;
        }

        try
        {
            var progressPath = Path.GetFullPath(persisted.ProgressPath);
            var sessionRoot = Path.GetDirectoryName(progressPath);
            if (string.IsNullOrWhiteSpace(sessionRoot) ||
                !IsPathInside(_queueWorkRoot, sessionRoot))
            {
                return;
            }

            item.SetPaths(
                progressPath,
                string.Empty,
                string.Empty,
                ProductInstallWorker.CreateQueueEnvelopeFile(
                    sessionRoot,
                    persisted.Sequence,
                    persisted.QueueId));
        }
        catch
        {
            // A stale session path must never prevent the history row from
            // being restored. Its files will be cleaned up by normal history
            // trimming when a valid path is available.
        }
    }

    private void RefreshQueuePositions()
    {
        var active = Items
            .Where(item => !item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .ToArray();
        for (var index = 0; index < active.Length; index++)
        {
            active[index].SetQueuePosition(index + 1);
        }

        // Product cards mirror queue state separately from the queue panel.
        // Notify every active item so removing an earlier item immediately
        // updates the visible "排队中 #n" text on all following cards.
        foreach (var item in active)
        {
            NotifyItemChanged(item);
        }
    }

    private void TrimHistory()
    {
        var terminal = Items
            .Where(item => item.IsTerminal)
            .OrderBy(item => item.Sequence)
            .ToArray();
        var removeCount = Math.Max(0, terminal.Length - HistoryLimit);
        for (var index = 0; index < removeCount; index++)
        {
            DeleteItemFiles(terminal[index]);
            Items.Remove(terminal[index]);
        }
    }

    private void DeleteItemFiles(ProductInstallQueueItemViewModel item)
    {
        ProductInstallWorker.DeleteQueueEnvelopeFiles(item.RequestPath);
        ProductInstallWorker.DeleteSessionFile(item.ProgressPath);
        ProductInstallWorker.DeleteSessionFile(item.CancelPath);
        ProductInstallWorker.DeleteSessionFile(item.PausePath);
    }

    private void NotifyItemChanged(ProductInstallQueueItemViewModel item) =>
        ItemChanged?.Invoke(item);

    private void Persist(bool throwOnFailure = false)
    {
        try
        {
            var document = new ProductInstallQueueDocument(
                DocumentVersion,
                _sessionId,
                Items
                    .OrderBy(item => item.Sequence)
                    .Select(item => new ProductInstallQueuePersistedItem(
                        item.QueueId,
                        item.Sequence,
                        item.Request,
                        item.State,
                        item.Progress,
                        item.Message,
                        item.ProgressPath,
                        item.ResultPath,
                        item.LogPath)
                    {
                        IsRemovalRequested = item.IsRemovalRequested,
                        CompletedAtUtc = item.CompletedAtUtc,
                        RequestedAtUtc = item.RequestedAtUtc
                    })
                    .ToList());
            AtomicFile.WriteAllText(
                _statePath,
                JsonSerializer.Serialize(document, JsonOptions));
            _lastPersistUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            WriteQueueDiagnostic($"保存安装队列状态失败：{ex.Message}");
            if (throwOnFailure)
            {
                throw new InvalidOperationException("无法保存安装队列状态，安装未加入队列。", ex);
            }
        }
    }

    private void TouchHeartbeatIfNeeded()
    {
        if (DateTime.UtcNow - _lastHeartbeatUtc >= TimeSpan.FromSeconds(3))
        {
            TouchHeartbeat();
        }
    }

    private void PersistIfNeeded()
    {
        if (HasActiveItems &&
            DateTime.UtcNow - _lastPersistUtc >= TimeSpan.FromSeconds(2))
        {
            Persist();
        }
    }

    private void TouchHeartbeat()
    {
        try
        {
            Directory.CreateDirectory(_sessionRoot);
            if (!File.Exists(_heartbeatPath))
            {
                File.WriteAllText(_heartbeatPath, "alive", Encoding.UTF8);
            }
            else
            {
                File.SetLastWriteTimeUtc(_heartbeatPath, DateTime.UtcNow);
            }

            _lastHeartbeatUtc = DateTime.UtcNow;
        }
        catch
        {
        }
    }

    private static void TouchControlFile(string path, bool throwOnFailure = false)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllText(path, "1", Encoding.UTF8);
        }
        catch (Exception ex)
        {
            if (throwOnFailure)
            {
                throw new InvalidOperationException(
                    "无法写入安装队列控制标记，请检查 StoreData 目录写入权限。",
                    ex);
            }
        }
    }

    private void WriteQueueDiagnostic(string message)
    {
        try
        {
            var path = Path.Combine(_storeDataRoot, "Work", "product-install-queue.log");
            RollingLogWriter.Append(
                path,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static ProductInstallQueueStatus? ParseTerminalState(string? state) =>
        state?.ToLowerInvariant() switch
        {
            "completed" => ProductInstallQueueStatus.Completed,
            "failed" => ProductInstallQueueStatus.Failed,
            "cancelled" => ProductInstallQueueStatus.Cancelled,
            _ => null
        };

    internal static bool IsTerminal(ProductInstallQueueStatus state) =>
        state is ProductInstallQueueStatus.Completed or
            ProductInstallQueueStatus.Failed or
            ProductInstallQueueStatus.Cancelled;

    private static bool IsPathInside(string root, string path)
    {
        try
        {
            var normalizedRoot = Path.GetFullPath(root)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;
            var normalizedPath = Path.GetFullPath(path);
            return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(ProductInstallQueueService));
        }
    }
}

public sealed class ProductInstallQueueItemViewModel : ObservableObject
{
    private ProductInstallQueueStatus _state;
    private double _progress;
    private double _downloadProgress;
    private InstallProgressStage _stage = InstallProgressStage.Preparing;
    private string _message;
    private int _queuePosition;
    private string? _resultPath;
    private string? _logPath;
    private bool _isPaused;
    private bool _isRemovalRequested;
    private bool _hasReliableTotal;
    private long _bytesReceived;
    private long? _totalBytes;
    private string? _speedText;
    private int _scannedFiles;
    private DateTime? _completedAtUtc;
    private DateTime? _requestedAtUtc;

    internal ProductInstallQueueItemViewModel(
        string queueId,
        int sequence,
        ProductInstallWorkerRequest request,
        ProductInstallQueueStatus state,
        double progress,
        string message,
        DateTime? completedAtUtc = null,
        DateTime? requestedAtUtc = null)
    {
        QueueId = queueId;
        Sequence = sequence;
        Request = request;
        _state = state;
        _progress = Compat.Clamp(progress, 0, 100);
        _message = message;
        _completedAtUtc = ProductInstallQueueService.IsTerminal(state) ? completedAtUtc : null;
        _requestedAtUtc = requestedAtUtc;
    }

    public string QueueId { get; }
    public int Sequence { get; }
    internal ProductInstallWorkerRequest Request { get; }
    public string ProductId => Request.ProductId;
    public string ProductName => ResolveProductName(Request);
    public string IconPath => string.IsNullOrWhiteSpace(Request.IconPath)
        ? "/Assets/defaultimg.png"
        : ProductIconCache.ResolveCachedIconPath(Request.ProductId, Request.IconPath);
    public bool IsUpdate => Request.IsUpdate;
    public ProductInstallQueueStatus State
    {
        get => _state;
        private set
        {
            if (!SetProperty(ref _state, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanCancel));
            OnPropertyChanged(nameof(IsTerminal));
            OnPropertyChanged(nameof(IsDownloading));
            OnPropertyChanged(nameof(CanTogglePause));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(RemoveToolTip));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
            OnPropertyChanged(nameof(TransferText));
        }
    }

    public double Progress
    {
        get => _progress;
        private set
        {
            if (SetProperty(ref _progress, value))
            {
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double DownloadProgress
    {
        get => _downloadProgress;
        private set
        {
            if (SetProperty(ref _downloadProgress, Compat.Clamp(value, 0, 100)))
            {
                OnPropertyChanged(nameof(DisplayDownloadProgress));
                OnPropertyChanged(nameof(ProgressText));
            }
        }
    }

    public double DisplayDownloadProgress => _hasReliableTotal ? DownloadProgress : 0d;
    public bool IsProgressIndeterminate => State == ProductInstallQueueStatus.Running &&
                                           !IsPaused &&
                                           (_stage != InstallProgressStage.Downloading || !_hasReliableTotal);
    public string ProgressText => State switch
    {
        ProductInstallQueueStatus.Pending => string.Empty,
        ProductInstallQueueStatus.Running when IsPaused => "已暂停",
        ProductInstallQueueStatus.Running when _stage == InstallProgressStage.Downloading && _hasReliableTotal => $"{DownloadProgress:0}%",
        ProductInstallQueueStatus.Running when _stage == InstallProgressStage.Downloading => "下载中",
        ProductInstallQueueStatus.Running when _stage == InstallProgressStage.Installing => "部署中",
        ProductInstallQueueStatus.Running => "准备中",
        ProductInstallQueueStatus.Completed => "完成",
        ProductInstallQueueStatus.Failed => "失败",
        ProductInstallQueueStatus.Cancelled => "已取消",
        _ => string.Empty
    };
    public string TransferText
    {
        get
        {
            if (!IsDownloading)
            {
                return string.Empty;
            }

            var parts = new List<string>();
            if (_bytesReceived > 0)
            {
                var downloaded = $"已下载 {ProductTransferFormatting.FormatBytes(_bytesReceived)}";
                if (_hasReliableTotal && _totalBytes is > 0)
                {
                    downloaded += $" / {ProductTransferFormatting.FormatBytes(_totalBytes.Value)}";
                }
                parts.Add(downloaded);
            }
            if (!string.IsNullOrWhiteSpace(_speedText))
            {
                parts.Add($"速度 {_speedText}");
            }
            if (_scannedFiles > 0)
            {
                parts.Add($"已扫描 {_scannedFiles:N0} 项");
            }
            return string.Join(" · ", parts);
        }
    }
    public string Message { get => _message; private set => SetProperty(ref _message, value); }
    public DateTime? CompletedAtUtc => _completedAtUtc;
    public DateTime? RequestedAtUtc => _requestedAtUtc;
    public string DownloadTimeText => RequestedAtUtc is DateTime requestedAtUtc
        ? $"下载时间 · {requestedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}"
        : "下载时间未记录";
    public string CompletionTimeText
    {
        get
        {
            if (!IsTerminal)
            {
                return string.Empty;
            }

            var label = State == ProductInstallQueueStatus.Completed ? "部署完成" : "任务结束";
            if (CompletedAtUtc is not DateTime completedAtUtc)
            {
                return $"{label}时间未记录";
            }

            var durationPrefix = RequestedAtUtc is DateTime requestedAtUtc && completedAtUtc >= requestedAtUtc
                ? $"用时 {FormatElapsedDuration(completedAtUtc - requestedAtUtc)} · "
                : string.Empty;
            return $"{durationPrefix}{label} · {completedAtUtc.ToLocalTime():yyyy-MM-dd HH:mm}";
        }
    }
    public int QueuePosition
    {
        get => _queuePosition;
        private set
        {
            if (!SetProperty(ref _queuePosition, value))
            {
                return;
            }

            OnPropertyChanged(nameof(StateText));
        }
    }

    public bool IsTerminal => ProductInstallQueueService.IsTerminal(State);
    public bool IsDownloading => State == ProductInstallQueueStatus.Running &&
                                  _stage == InstallProgressStage.Downloading;
    public bool CanCancel => State is ProductInstallQueueStatus.Pending or ProductInstallQueueStatus.Running;
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (!SetProperty(ref _isPaused, value))
            {
                return;
            }

            OnPropertyChanged(nameof(PauseActionText));
            OnPropertyChanged(nameof(PauseActionGlyph));
            OnPropertyChanged(nameof(PauseActionToolTip));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanTogglePause));
            OnPropertyChanged(nameof(ProgressText));
            OnPropertyChanged(nameof(IsProgressIndeterminate));
        }
    }
    public bool IsRemovalRequested
    {
        get => _isRemovalRequested;
        private set
        {
            if (!SetProperty(ref _isRemovalRequested, value))
            {
                return;
            }

            OnPropertyChanged(nameof(CanTogglePause));
            OnPropertyChanged(nameof(CanRemove));
            OnPropertyChanged(nameof(RemoveToolTip));
            OnPropertyChanged(nameof(StateText));
        }
    }
    // A paused worker is still in the downloading stage. Keep the same action
    // available so the user can explicitly resume it from the queue panel.
    public bool CanTogglePause => State == ProductInstallQueueStatus.Running &&
                                  !IsRemovalRequested &&
                                  (IsDownloading || IsPaused);
    public bool CanRemove => !IsRemovalRequested;
    public string PauseActionText => IsPaused ? "继续" : "暂停";
    public string PauseActionGlyph => IsPaused ? "\uE768" : "\uE769";
    public string PauseActionToolTip => IsPaused ? "继续下载此软件" : "暂停下载此软件";
    public string RemoveActionGlyph => "\uE74D";
    public string RemoveToolTip => IsTerminal ? "删除此记录" : "取消并删除此任务";
    public string StateText => IsRemovalRequested
        ? "正在删除"
        : IsPaused
            ? "下载已暂停"
            : State switch
            {
                ProductInstallQueueStatus.Pending => $"等待中 · 第 {QueuePosition} 项",
                ProductInstallQueueStatus.Running => _stage switch
                {
                    InstallProgressStage.Preparing => "准备中",
                    InstallProgressStage.Downloading => IsUpdate ? "正在下载更新" : "正在下载",
                    InstallProgressStage.Installing => IsUpdate ? "正在部署更新" : "正在部署",
                    _ => IsUpdate ? "正在更新" : "正在安装"
                },
                ProductInstallQueueStatus.Completed => "已完成",
                ProductInstallQueueStatus.Failed => "安装失败",
                ProductInstallQueueStatus.Cancelled => "已取消",
                _ => "处理中"
            };

    internal string ProgressPath { get; private set; } = string.Empty;
    internal string CancelPath { get; private set; } = string.Empty;
    internal string PausePath { get; private set; } = string.Empty;
    internal string RequestPath { get; private set; } = string.Empty;
    internal string? ResultPath => _resultPath;
    internal string? LogPath => _logPath;

    internal void SetPaths(string progressPath, string cancelPath, string pausePath, string requestPath)
    {
        ProgressPath = progressPath;
        CancelPath = cancelPath;
        PausePath = pausePath;
        RequestPath = requestPath;
    }

    internal void SetState(ProductInstallQueueStatus state)
    {
        State = state;
        if (ProductInstallQueueService.IsTerminal(state))
        {
            if (_completedAtUtc is null)
            {
                _completedAtUtc = DateTime.UtcNow;
                OnPropertyChanged(nameof(CompletedAtUtc));
                OnPropertyChanged(nameof(CompletionTimeText));
            }

            SetPaused(false);
        }
    }

    internal void SetProgress(double value) => Progress = Compat.Clamp(value, 0, 100);

    internal void SetMessage(string message)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            Message = message.Trim();
        }
    }

    internal void SetQueuePosition(int value) => QueuePosition = Math.Max(0, value);

    internal void SetPaused(bool value) => IsPaused = value;

    internal void MarkRemovalRequested()
    {
        SetPaused(false);
        IsRemovalRequested = true;
    }

    internal void SetResult(string? resultPath, string? logPath)
    {
        _resultPath = resultPath;
        _logPath = logPath;
    }

    internal void ApplyProgress(ProductInstallWorkerProgress update)
    {
        if (update is null)
        {
            return;
        }

        if (update.Percent >= Progress ||
            string.Equals(update.State, "completed", StringComparison.OrdinalIgnoreCase))
        {
            SetProgress(update.Percent);
        }

        _stage = update.Stage;
        _hasReliableTotal = update.HasReliableTotal && update.TotalBytes is > 0;
        _bytesReceived = update.BytesReceived;
        _totalBytes = update.TotalBytes;
        _speedText = update.SpeedText;
        _scannedFiles = update.ScannedFiles;
        DownloadProgress = update.Stage switch
        {
            InstallProgressStage.Downloading => update.StagePercent ?? update.Percent,
            InstallProgressStage.Installing or InstallProgressStage.Completed => 100,
            _ => DownloadProgress
        };
        OnPropertyChanged(nameof(IsDownloading));
        OnPropertyChanged(nameof(CanTogglePause));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(ProgressText));
        OnPropertyChanged(nameof(DisplayDownloadProgress));
        OnPropertyChanged(nameof(IsProgressIndeterminate));
        OnPropertyChanged(nameof(TransferText));
        if (!IsPaused && !IsRemovalRequested)
        {
            SetMessage(update.Message);
        }
    }

    private static string FormatElapsedDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed.TotalDays >= 1)
        {
            return $"{(int)elapsed.TotalDays}天{elapsed.Hours:D2}小时{elapsed.Minutes:D2}分";
        }

        if (elapsed.TotalHours >= 1)
        {
            return $"{(int)elapsed.TotalHours}小时{elapsed.Minutes:D2}分";
        }

        if (elapsed.TotalMinutes >= 1)
        {
            return $"{(int)elapsed.TotalMinutes}分{elapsed.Seconds:D2}秒";
        }

        return $"{Math.Max(0, (int)elapsed.TotalSeconds)}秒";
    }

    private static string ResolveProductName(ProductInstallWorkerRequest request)
    {
        if (request.Source == ProductSource.Online &&
            !string.IsNullOrWhiteSpace(request.Level) &&
            !request.Level.Equals("在线", StringComparison.OrdinalIgnoreCase) &&
            !request.Level.Equals(request.Name, StringComparison.OrdinalIgnoreCase))
        {
            return request.Level.Trim();
        }

        return request.Name;
    }
}

internal sealed record ProductInstallQueueEnvelope(
    string QueueId,
    int Sequence,
    ProductInstallWorkerRequest Request,
    string ProgressPath,
    string CancelPath,
    string PausePath);

internal sealed record ProductInstallQueueDocument(
    int Version,
    string SessionId,
    List<ProductInstallQueuePersistedItem>? Items);

internal sealed record ProductInstallQueuePersistedItem(
    string QueueId,
    int Sequence,
    ProductInstallWorkerRequest? Request,
    ProductInstallQueueStatus State,
    double Progress,
    string Message,
    string? ProgressPath,
    string? ResultPath,
    string? LogPath)
{
    public bool IsRemovalRequested { get; init; }
    public DateTime? CompletedAtUtc { get; init; }
    public DateTime? RequestedAtUtc { get; init; }
}
