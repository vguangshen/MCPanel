using System.Diagnostics;
using System.ComponentModel;
using System.IO;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace MCPanel;

/// <summary>
/// Runs product installations in an elevated process. The queue mode keeps one
/// UAC-approved worker alive for the ordered download/deployment chain, while
/// the legacy single-request mode remains available for existing callers.
/// </summary>
internal static class ProductInstallWorker
{
    private const string WorkerArgument = "--product-install-worker";
    private const string QueueWorkerArgument = "--product-install-queue-worker";
    private const string QueueRequestSuffix = ".request.json";
    private const string QueueActiveSuffix = ".active.json";
    private const string QueueFinishedSuffix = ".finished.json";
    private const string QueueWithdrawnSuffix = ".withdrawn.json";
    private static readonly JsonSerializerOptions JsonOptions = new();

    public static bool IsWorkerRequest(string[] args) =>
        Array.Exists(args, argument =>
            string.Equals(argument, WorkerArgument, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(argument, QueueWorkerArgument, StringComparison.OrdinalIgnoreCase));

    public static async Task<int> RunAsync(string[] args)
    {
        if (TryGetQueueWorkerPaths(args, out var queueRoot, out var stopPath, out var heartbeatPath))
        {
            return await RunQueueAsync(queueRoot, stopPath, heartbeatPath);
        }

        if (!TryGetRequest(args, out var requestPath, out var progressPath, out var cancelPath, out var pausePath))
        {
            return 2;
        }

        var reporter = new ProgressReporter(progressPath);
        ProductInstallWorkerRequest? request = null;
        try
        {
            if (!IsAdministrator())
            {
                throw new InvalidOperationException("管理员产品安装进程未获得管理员权限。请重新点击安装并允许 UAC 授权。");
            }

            request = JsonSerializer.Deserialize<ProductInstallWorkerRequest>(
                File.ReadAllText(requestPath, Encoding.UTF8),
                JsonOptions);
            if (request is null || string.IsNullOrWhiteSpace(request.ProductId))
            {
                throw new InvalidDataException("产品安装请求无效，无法开始安装。");
            }

            return await RunOneAsync(request, progressPath, cancelPath, pausePath);
        }
        catch (Exception ex)
        {
            WriteFailure(
                reporter,
                request?.ProductId ?? "product",
                ex);
            return 1;
        }
    }

    internal static async Task<int> RunOneAsync(
        ProductInstallWorkerRequest request,
        string progressPath,
        string cancelPath,
        string pausePath,
        string? stopPath = null)
    {
        var reporter = new ProgressReporter(progressPath);
        try
        {
            reporter.Write(
                "running",
                0,
                "管理员安装会话已启动，正在准备下载...",
                force: true,
                stage: InstallProgressStage.Preparing,
                stagePercent: 0);

            var product = request.ToProduct();
            using var cancellation = new CancellationTokenSource();
            using var pauseController = new DownloadPauseController();
            var controlMonitor = MonitorControlFilesAsync(
                cancelPath,
                pausePath,
                pauseController,
                cancellation,
                stopPath);
            var stoppedTomcatForUpdate = false;
            var completed = false;
            try
            {
                // A queue request may be claimed just as the UI cancels it.
                // Check the marker before stopping services or starting any
                // download so a claimed-but-cancelled item never runs.
                if (File.Exists(cancelPath) ||
                    (stopPath is not null && File.Exists(stopPath)))
                {
                    cancellation.Cancel();
                    cancellation.Token.ThrowIfCancellationRequested();
                }

                if (request.IsUpdate &&
                    IsTomcatProduct(product) &&
                    ProductDeploymentService.LoadTomcatDeploymentInfo(product.ProductId) is not null)
                {
                    reporter.Write(
                        "running",
                        0,
                        "正在停止产品服务并准备更新...",
                        stage: InstallProgressStage.Preparing,
                        stagePercent: 0);
                    await new TomcatProductInstanceManager().StopAsync(product.ProductId, cancellation.Token);
                    stoppedTomcatForUpdate = true;
                }

                reporter.Write(
                    "running",
                    0,
                    "正在准备下载产品文件...",
                    stage: InstallProgressStage.Downloading,
                    stagePercent: 0);
                using var storeClient = new McPanelStoreClient();
                var target = await storeClient.DownloadProductWithDetailsAsync(
                    product,
                    reporter.ReportDownload,
                    reporter.ReportStatus,
                    pauseController,
                    cancellation.Token);
                if (target is null)
                {
                    throw new InvalidOperationException("供应商接口暂未返回该产品的下载地址，请刷新在线列表或检查账号权限。");
                }

                reporter.Write(
                    "running",
                    90,
                    request.IsUpdate
                        ? "更新文件已同步，正在检查部署配置..."
                        : "下载完成，正在校验并部署到运行服务...",
                    stage: InstallProgressStage.Installing,
                    stagePercent: 0,
                    speedText: null);
                await EnsureRequiredDatabaseServicesAsync(product, reporter, cancellation.Token);
                var deploymentService = new ProductDeploymentService();
                var result = await deploymentService.DeployAsync(product, target, cancellation.Token);
                completed = true;
                reporter.Write(
                    "completed",
                    100,
                    "产品文件与运行服务已处理完成。",
                    force: true,
                    stage: InstallProgressStage.Completed,
                    stagePercent: 100,
                    resultPath: result.DeployPath,
                    resultMessage: result.Message);
                return 0;
            }
            finally
            {
                cancellation.Cancel();
                try
                {
                    await controlMonitor;
                }
                catch (OperationCanceledException)
                {
                }

                if (stoppedTomcatForUpdate && !completed)
                {
                    try
                    {
                        await new TomcatProductInstanceManager().StartAsync(product.ProductId, catalinaMode: false);
                    }
                    catch (Exception restoreError)
                    {
                        EnvironmentOperationDiagnostics.RecordFailure(
                            product.ProductId,
                            "产品安装失败后恢复 Tomcat",
                            restoreError);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            reporter.Write(
                "cancelled",
                0,
                "产品安装已取消。",
                force: true,
                stage: InstallProgressStage.Installing,
                stagePercent: 0);
            return 2;
        }
        catch (Exception ex)
        {
            WriteFailure(reporter, request.ProductId, ex);
            return 1;
        }
    }

    private static async Task EnsureRequiredDatabaseServicesAsync(
        ProductItem product,
        ProgressReporter reporter,
        CancellationToken cancellationToken)
    {
        var requiredDatabases = ProductEnvironmentPreflight.GetRequiredKinds(product)
            .Where(kind => kind is EnvironmentKind.MySql or EnvironmentKind.SqlServer)
            .ToArray();
        if (requiredDatabases.Length == 0)
        {
            return;
        }

        var runtimeService = new EnvironmentRuntimeService();
        foreach (var kind in requiredDatabases)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = runtimeService.GetState(kind);
            if (!state.IsInstalled)
            {
                throw new InvalidOperationException(
                    $"部署 {product.ProductId} 前需要先安装 {ProductEnvironmentPreflight.DisplayName(kind)}。");
            }

            if (!state.IsRunning)
            {
                var displayName = ProductEnvironmentPreflight.DisplayName(kind);
                reporter.Write(
                    "running",
                    91,
                    $"正在启动并确认 {displayName}，随后继续部署产品...",
                    force: true,
                    stage: InstallProgressStage.Installing,
                    stagePercent: 10,
                    speedText: null);
                await runtimeService.StartAsync(kind, cancellationToken);
                if (!runtimeService.IsRunning(kind))
                {
                    throw new InvalidOperationException($"{displayName} 启动后未进入运行状态，已停止产品部署。");
                }
            }

            await WaitForDatabaseEndpointAsync(
                kind,
                ProductEnvironmentPreflight.DisplayName(kind),
                cancellationToken);
        }
    }

    private static async Task WaitForDatabaseEndpointAsync(
        EnvironmentKind kind,
        string displayName,
        CancellationToken cancellationToken)
    {
        (string Host, int Port) endpoint = kind switch
        {
            EnvironmentKind.MySql => ResolveMySqlEndpoint(),
            EnvironmentKind.SqlServer => ResolveSqlServerEndpoint(),
            _ => (string.Empty, 0)
        };
        if (string.IsNullOrWhiteSpace(endpoint.Host) || endpoint.Port is <= 0 or > 65535)
        {
            return;
        }

        var deadline = DateTime.UtcNow.AddSeconds(45);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(endpoint.Host, endpoint.Port);
            var completed = await Task.WhenAny(connectTask, Task.Delay(1200, cancellationToken));
            if (completed == connectTask)
            {
                try
                {
                    await connectTask;
                    if (client.Connected)
                    {
                        return;
                    }
                }
                catch (SocketException)
                {
                }
            }
            else
            {
                client.Close();
                try
                {
                    await connectTask;
                }
                catch (SocketException)
                {
                }
                catch (ObjectDisposedException)
                {
                }
            }

            await Task.Delay(500, cancellationToken);
        }

        throw new TimeoutException(
            $"{displayName} 服务已启动，但端口 {endpoint.Host}:{endpoint.Port} 在 45 秒内未能接受连接，已停止产品部署。");
    }

    private static (string Host, int Port) ResolveMySqlEndpoint()
    {
        var credentials = MySqlCredentialStore.Load();
        return (credentials.Host, credentials.Port);
    }

    private static (string Host, int Port) ResolveSqlServerEndpoint()
    {
        var credentials = SqlServerCredentialStore.Load();
        return (credentials.Host, credentials.Port);
    }

    internal static ProductInstallWorkerRequest CreateRequest(ProductItem product, bool isUpdate) =>
        new(
            product.ProductId,
            product.Name,
            product.Level,
            product.IconPath,
            product.Source,
            product.RemoteIconUrl,
            product.RunEnvironment,
            product.SqlEnvironment,
            product.DevLanguage,
            product.InstallRoot,
            product.SysType,
            product.UsesSvn,
            product.DownloadUrl,
            product.FileName,
            isUpdate);

    internal static string CreateProgressFile(string directory, string queueId)
    {
        var path = Path.Combine(directory, $"{SafeFileName(queueId)}.progress.json");
        AtomicFile.WriteAllText(
            path,
            JsonSerializer.Serialize(
                new ProductInstallWorkerProgress(
                    "waiting",
                    0,
                    "等待管理员安装会话启动。",
                    InstallProgressStage.Preparing,
                    0),
                JsonOptions));
        return path;
    }

    internal static string CreateControlFile(string directory, string queueId, string suffix)
    {
        var path = Path.Combine(directory, $"{SafeFileName(queueId)}.{suffix}");
        DeleteSessionFile(path);
        return path;
    }

    internal static string CreateQueueEnvelopeFile(string directory, int sequence, string queueId)
    {
        return Path.Combine(directory, $"{sequence:000000}-{SafeFileName(queueId)}{QueueRequestSuffix}");
    }

    public static bool TryReadProgress(string path, out ProductInstallWorkerProgress progress)
    {
        try
        {
            if (!File.Exists(path))
            {
                progress = default!;
                return false;
            }

            progress = JsonSerializer.Deserialize<ProductInstallWorkerProgress>(
                File.ReadAllText(path, Encoding.UTF8),
                JsonOptions)!;
            return progress is not null;
        }
        catch
        {
            progress = default!;
            return false;
        }
    }

    public static void DeleteSessionFile(string path)
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
        }
    }

    internal static void DeleteQueueEnvelopeFiles(string requestPath)
    {
        DeleteSessionFile(requestPath);
        if (!requestPath.EndsWith(QueueRequestSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var prefix = requestPath.Substring(0, requestPath.Length - QueueRequestSuffix.Length);
        DeleteSessionFile(prefix + QueueActiveSuffix);
        DeleteSessionFile(prefix + QueueFinishedSuffix);
        DeleteSessionFile(prefix + QueueWithdrawnSuffix);
    }

    /// <summary>
    /// Atomically takes a still-pending request away from the queue worker.
    /// Exactly one side can rename the request: the worker to .active.json or
    /// the UI to .withdrawn.json. A false result therefore means the caller
    /// must keep the cancellation marker until worker progress turns terminal.
    /// </summary>
    internal static bool TryWithdrawQueueRequest(string requestPath)
    {
        if (string.IsNullOrWhiteSpace(requestPath) ||
            !requestPath.EndsWith(QueueRequestSuffix, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var prefix = requestPath.Substring(0, requestPath.Length - QueueRequestSuffix.Length);
        var withdrawnPath = prefix + QueueWithdrawnSuffix;
        try
        {
            if (File.Exists(withdrawnPath))
            {
                File.Delete(withdrawnPath);
            }

            File.Move(requestPath, withdrawnPath);
            DeleteSessionFile(withdrawnPath);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    internal static Process StartQueue(string queueRoot, string stopPath, string heartbeatPath)
    {
        var executable = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(executable) || !File.Exists(executable))
        {
            throw new InvalidOperationException("无法定位 MCPanel 主程序，不能启动管理员产品安装会话。");
        }
        var executablePath = executable!;

        var arguments = string.Join(
            " ",
            QueueWorkerArgument,
            Compat.QuoteCommandLineArgument(queueRoot),
            Compat.QuoteCommandLineArgument(stopPath),
            Compat.QuoteCommandLineArgument(heartbeatPath));
        try
        {
            return ProcessRunner.Start(
                executablePath,
                arguments,
                Path.GetDirectoryName(executablePath) ?? ComponentPaths.ApplicationRoot,
                elevated: true);
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            throw new InvalidOperationException("已取消管理员授权，安装未开始。", ex);
        }
    }

    private static async Task<int> RunQueueAsync(string queueRoot, string stopPath, string heartbeatPath)
    {
        if (!IsAdministrator())
        {
            WriteQueueError(queueRoot, "管理员产品安装进程未获得管理员权限。请重新点击安装并允许 UAC 授权。");
            return 3;
        }

        // A crashed UI can leave its elevated worker alive for a short time.
        // Serialize workers for the whole queue root so recovery cannot run a
        // cloned request concurrently with the previous session's worker.
        using var queueMutex = AcquireQueueMutex(queueRoot);
        if (queueMutex is null)
        {
            WriteQueueError(queueRoot, "无法取得产品安装队列锁，已阻止并发安装会话。请稍后重试。");
            return 4;
        }

        var lastWorkUtc = DateTime.UtcNow;
        while (true)
        {
            if (File.Exists(stopPath) || IsHeartbeatStale(heartbeatPath))
            {
                return 0;
            }

            var claimedPath = TryClaimNextQueueRequest(queueRoot);
            if (claimedPath is not null)
            {
                lastWorkUtc = DateTime.UtcNow;
                try
                {
                    var envelope = JsonSerializer.Deserialize<ProductInstallQueueEnvelope>(
                        File.ReadAllText(claimedPath, Encoding.UTF8),
                        JsonOptions);
                    if (envelope is null ||
                        string.IsNullOrWhiteSpace(envelope.QueueId) ||
                        envelope.Request is null ||
                        !IsPathInside(queueRoot, envelope.ProgressPath) ||
                        !IsPathInside(queueRoot, envelope.CancelPath) ||
                        !IsPathInside(queueRoot, envelope.PausePath))
                    {
                        throw new InvalidDataException("产品安装队列请求无效。");
                    }

                    await RunOneAsync(
                        envelope.Request,
                        envelope.ProgressPath,
                        envelope.CancelPath,
                        envelope.PausePath,
                        stopPath);
                }
                catch (Exception ex)
                {
                    WriteQueueError(queueRoot, $"队列请求处理失败：{ex.Message}");
                }
                finally
                {
                    MoveClaimedRequestToFinished(claimedPath);
                }

                continue;
            }

            if (DateTime.UtcNow - lastWorkUtc > TimeSpan.FromMinutes(5))
            {
                return 0;
            }

            try
            {
                await Task.Delay(250);
            }
            catch (TaskCanceledException)
            {
                return 0;
            }
        }
    }

    private static string? TryClaimNextQueueRequest(string queueRoot)
    {
        try
        {
            foreach (var requestPath in Directory
                         .EnumerateFiles(queueRoot, $"*{QueueRequestSuffix}", SearchOption.TopDirectoryOnly)
                         .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase))
            {
                var claimedPath = requestPath.Substring(0, requestPath.Length - QueueRequestSuffix.Length) + QueueActiveSuffix;
                try
                {
                    File.Move(requestPath, claimedPath);
                    return claimedPath;
                }
                catch (IOException)
                {
                    // Another filesystem observer may have won the claim.
                }
                catch (UnauthorizedAccessException)
                {
                    // Retry on the next polling pass; the queue must not crash.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }

        return null;
    }

    private static void MoveClaimedRequestToFinished(string claimedPath)
    {
        try
        {
            var finishedPath = claimedPath.Substring(0, claimedPath.Length - QueueActiveSuffix.Length) + QueueFinishedSuffix;
            if (File.Exists(finishedPath))
            {
                File.Delete(finishedPath);
            }

            File.Move(claimedPath, finishedPath);
        }
        catch
        {
            // The progress file is the source of truth for the UI.
        }
    }

    private static async Task MonitorControlFilesAsync(
        string cancelPath,
        string pausePath,
        DownloadPauseController pauseController,
        CancellationTokenSource cancellation,
        string? stopPath = null)
    {
        var isPaused = false;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                if (File.Exists(cancelPath) ||
                    (stopPath is not null && File.Exists(stopPath)))
                {
                    cancellation.Cancel();
                    break;
                }

                var pauseRequested = File.Exists(pausePath);
                if (pauseRequested && !isPaused)
                {
                    isPaused = pauseController.Pause();
                }
                else if (!pauseRequested && isPaused)
                {
                    isPaused = !pauseController.Resume();
                }

                await Task.Delay(150, cancellation.Token);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
        }
        finally
        {
            pauseController.Resume();
        }
    }

    private static Mutex? AcquireQueueMutex(string queueRoot)
    {
        Mutex? mutex = null;
        try
        {
            var queueGroupRoot = Directory.GetParent(Path.GetFullPath(queueRoot))?.FullName ?? queueRoot;
            using var sha = SHA256.Create();
            var hash = BitConverter.ToString(
                    sha.ComputeHash(Encoding.UTF8.GetBytes(queueGroupRoot)))
                .Replace("-", string.Empty)
                .ToLowerInvariant();
            mutex = new Mutex(false, $"Local\\MCPanel.ProductInstallQueue.{hash}");
            try
            {
                if (!mutex.WaitOne(TimeSpan.FromSeconds(60)))
                {
                    mutex.Dispose();
                    return null;
                }
            }
            catch (AbandonedMutexException)
            {
                // The previous worker exited unexpectedly; ownership is now ours.
            }

            return mutex;
        }
        catch
        {
            mutex?.Dispose();
            return null;
        }
    }

    private static bool TryGetQueueWorkerPaths(
        string[] args,
        out string queueRoot,
        out string stopPath,
        out string heartbeatPath)
    {
        queueRoot = string.Empty;
        stopPath = string.Empty;
        heartbeatPath = string.Empty;
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, QueueWorkerArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 3 >= args.Length)
        {
            return false;
        }

        try
        {
            var workRoot = GetWorkRoot();
            queueRoot = Path.GetFullPath(args[index + 1])
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            stopPath = Path.GetFullPath(args[index + 2]);
            heartbeatPath = Path.GetFullPath(args[index + 3]);
            return Directory.Exists(queueRoot) &&
                   IsPathInside(workRoot, queueRoot) &&
                   IsPathInside(queueRoot, stopPath) &&
                   IsPathInside(queueRoot, heartbeatPath) &&
                   File.Exists(heartbeatPath);
        }
        catch
        {
            queueRoot = string.Empty;
            stopPath = string.Empty;
            heartbeatPath = string.Empty;
            return false;
        }
    }

    private static bool TryGetRequest(
        string[] args,
        out string requestPath,
        out string progressPath,
        out string cancelPath,
        out string pausePath)
    {
        requestPath = string.Empty;
        progressPath = string.Empty;
        cancelPath = string.Empty;
        pausePath = string.Empty;
        var index = Array.FindIndex(args, argument =>
            string.Equals(argument, WorkerArgument, StringComparison.OrdinalIgnoreCase));
        if (index < 0 || index + 4 >= args.Length)
        {
            return false;
        }

        try
        {
            var workRoot = GetWorkRoot();
            requestPath = Path.GetFullPath(args[index + 1]);
            progressPath = Path.GetFullPath(args[index + 2]);
            cancelPath = Path.GetFullPath(args[index + 3]);
            pausePath = Path.GetFullPath(args[index + 4]);
            return IsPathInside(workRoot, requestPath) &&
                   IsPathInside(workRoot, progressPath) &&
                   IsPathInside(workRoot, cancelPath) &&
                   IsPathInside(workRoot, pausePath) &&
                   File.Exists(requestPath);
        }
        catch
        {
            requestPath = string.Empty;
            progressPath = string.Empty;
            cancelPath = string.Empty;
            pausePath = string.Empty;
            return false;
        }
    }

    private static string GetWorkRoot() =>
        Path.GetFullPath(ComponentPaths.WorkRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsPathInside(string root, string path)
    {
        var normalizedRoot = Path.GetFullPath(root)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
            Path.DirectorySeparatorChar;
        var normalizedPath = Path.GetFullPath(path);
        return normalizedPath.StartsWith(normalizedRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsHeartbeatStale(string heartbeatPath)
    {
        try
        {
            return !File.Exists(heartbeatPath) ||
                   DateTime.UtcNow - File.GetLastWriteTimeUtc(heartbeatPath) > TimeSpan.FromSeconds(45);
        }
        catch
        {
            return true;
        }
    }

    private static void WriteQueueError(string queueRoot, string message)
    {
        try
        {
            AtomicFile.WriteAllText(
                Path.Combine(queueRoot, "worker.error.txt"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
        }
    }

    private static void WriteFailure(ProgressReporter reporter, string productId, Exception exception)
    {
        var logPath = EnvironmentOperationDiagnostics.RecordFailure(
            productId,
            "产品安装",
            exception,
            Path.Combine(
                ComponentPaths.WorkRoot,
                $"product-install-{SafeFileName(productId)}.log"));
        var detail = "安装失败：" + exception.Message;
        if (!string.IsNullOrWhiteSpace(logPath))
        {
            detail += $"{Environment.NewLine}{Environment.NewLine}完整诊断日志：{logPath}";
        }

        reporter.Write(
            "failed",
            0,
            detail,
            force: true,
            stage: InstallProgressStage.Installing,
            stagePercent: 0,
            logPath: logPath);
    }

    private static bool IsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    private static bool IsTomcatProduct(ProductItem product) =>
        (product.RunEnvironment?.Contains("tomcat", StringComparison.OrdinalIgnoreCase) ?? false) ||
        (product.DevLanguage?.Contains("java", StringComparison.OrdinalIgnoreCase) ?? false);

    private static string SafeFileName(string value)
    {
        var chars = (value ?? string.Empty)
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) ? '_' : character)
            .ToArray();
        return string.IsNullOrWhiteSpace(new string(chars)) ? "product" : new string(chars);
    }

    private sealed class ProgressReporter
    {
        private readonly string _path;
        private readonly object _sync = new();
        private DateTime _lastWriteUtc = DateTime.MinValue;
        private string _lastMessage = string.Empty;
        private InstallProgressStage _stage = InstallProgressStage.Preparing;
        private double? _stagePercent;
        private string? _speedText;
        private long _bytesReceived;
        private long? _totalBytes;
        private int _scannedFiles;
        private long _scannedBytes;
        private double _downloadPercent;
        private bool _hasReliableTotal;

        public ProgressReporter(string path) => _path = path;

        public void ReportDownload(ProductDownloadProgress update)
        {
            _downloadPercent = Compat.Clamp(update.Percent, 0, 100);
            _stage = InstallProgressStage.Downloading;
            _stagePercent = update.HasReliableTotal ? _downloadPercent : null;
            _speedText = update.SpeedText;
            _bytesReceived = update.BytesReceived;
            _totalBytes = update.TotalBytes;
            _scannedFiles = update.ScannedFiles;
            _scannedBytes = update.ScannedBytes;
            _hasReliableTotal = update.HasReliableTotal;
            Write(
                "running",
                Compat.Clamp(_downloadPercent * 0.9, 0, 90),
                update.Message,
                stage: InstallProgressStage.Downloading,
                stagePercent: _downloadPercent,
                speedText: update.SpeedText,
                bytesReceived: update.BytesReceived,
                totalBytes: update.TotalBytes,
                scannedFiles: update.ScannedFiles,
                scannedBytes: update.ScannedBytes,
                hasReliableTotal: update.HasReliableTotal);
        }

        public void ReportStatus(string message)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return;
            }

            Write(
                "running",
                Compat.Clamp(_downloadPercent * 0.9, 0, 90),
                message,
                stage: _stage,
                stagePercent: _stagePercent,
                speedText: _speedText,
                bytesReceived: _bytesReceived,
                totalBytes: _totalBytes,
                scannedFiles: _scannedFiles,
                scannedBytes: _scannedBytes);
        }

        public void Write(
            string state,
            double percent,
            string message,
            bool force = false,
            string? logPath = null,
            InstallProgressStage? stage = null,
            double? stagePercent = null,
            string? speedText = null,
            long bytesReceived = 0,
            long? totalBytes = null,
            string? resultPath = null,
            string? resultMessage = null,
            int scannedFiles = 0,
            long scannedBytes = 0,
            bool? hasReliableTotal = null)
        {
            lock (_sync)
            {
                var now = DateTime.UtcNow;
                if (!force && now - _lastWriteUtc < TimeSpan.FromMilliseconds(150) &&
                    string.Equals(_lastMessage, message, StringComparison.Ordinal))
                {
                    return;
                }

                _lastWriteUtc = now;
                _lastMessage = message;
                _stage = stage ?? _stage;
                _stagePercent = stagePercent ?? _stagePercent;
                _speedText = speedText ?? _speedText;
                if (bytesReceived > 0 || totalBytes is not null)
                {
                    _bytesReceived = bytesReceived;
                    _totalBytes = totalBytes;
                }
                if (scannedFiles > 0 || scannedBytes > 0)
                {
                    _scannedFiles = scannedFiles;
                    _scannedBytes = scannedBytes;
                }
                _hasReliableTotal = hasReliableTotal ?? _hasReliableTotal;

                try
                {
                    AtomicFile.WriteAllText(
                        _path,
                        JsonSerializer.Serialize(
                            new ProductInstallWorkerProgress(
                                state,
                                Compat.Clamp(percent, 0, 100),
                                message,
                                _stage,
                                _stagePercent,
                                _speedText,
                                _bytesReceived,
                                _totalBytes,
                                logPath,
                                resultPath,
                                resultMessage,
                                _scannedFiles,
                                _scannedBytes,
                                _hasReliableTotal),
                            JsonOptions));
                }
                catch
                {
                    // Progress reporting must never interrupt the installation.
                }
            }
        }
    }
}

internal sealed record ProductInstallWorkerRequest(
    string ProductId,
    string Name,
    string Level,
    string IconPath,
    ProductSource Source,
    string? RemoteIconUrl,
    string? RunEnvironment,
    string? SqlEnvironment,
    string? DevLanguage,
    string? InstallRoot,
    string? SysType,
    bool UsesSvn,
    string? DownloadUrl,
    string? FileName,
    bool IsUpdate)
{
    public ProductItem ToProduct() => new(ProductId, Name, Level, IconPath, Source)
    {
        RemoteIconUrl = RemoteIconUrl,
        RunEnvironment = RunEnvironment,
        SqlEnvironment = SqlEnvironment,
        DevLanguage = DevLanguage,
        InstallRoot = InstallRoot,
        SysType = SysType,
        UsesSvn = UsesSvn,
        DownloadUrl = DownloadUrl,
        FileName = FileName
    };
}

internal sealed record ProductInstallWorkerProgress(
    string State,
    double Percent,
    string Message,
    InstallProgressStage Stage = InstallProgressStage.Preparing,
    double? StagePercent = null,
    string? SpeedText = null,
    long BytesReceived = 0,
    long? TotalBytes = null,
    string? LogPath = null,
    string? ResultPath = null,
    string? ResultMessage = null,
    int ScannedFiles = 0,
    long ScannedBytes = 0,
    bool HasReliableTotal = false);
