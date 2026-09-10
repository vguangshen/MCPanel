using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Xml.Linq;

namespace MCPanel;

/// <summary>
/// Shared Tomcat startup validation. A successful batch-file launch or an open
/// connector port is not enough: Java may exit a moment later, or a connector
/// can be listening while its web application failed to initialize.
/// </summary>
internal static class TomcatRuntimeProbe
{
    internal static readonly TimeSpan DefaultStartupTimeout = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ApplicationProbeTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ApplicationProbeCacheLifetime = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan DeploymentCacheLifetime = TimeSpan.FromSeconds(1);
    private static readonly HttpClient ApplicationProbeClient = CreateApplicationProbeClient();
    private static readonly ConcurrentDictionary<int, ApplicationProbeCacheEntry> ApplicationProbeCache = new();
    private static readonly object DeploymentCacheGate = new();
    private static DateTime _deploymentCacheExpiresUtc = DateTime.MinValue;
    private static IReadOnlyDictionary<int, TomcatProductDeploymentInfo> _deploymentByPort =
        new Dictionary<int, TomcatProductDeploymentInfo>();

    private sealed class ApplicationProbeCacheEntry
    {
        public ApplicationProbeCacheEntry(DateTime createdUtc, Task<bool> task)
        {
            CreatedUtc = createdUtc;
            Task = task;
        }

        public DateTime CreatedUtc { get; }
        public Task<bool> Task { get; }
    }

    public static IReadOnlyList<int> ReadHttpPorts(string tomcatRoot)
    {
        var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
        try
        {
            if (!File.Exists(serverXml))
            {
                return Array.Empty<int>();
            }

            var document = XDocument.Load(serverXml);
            return document.Descendants()
                .Where(element => string.Equals(element.Name.LocalName, "Connector", StringComparison.OrdinalIgnoreCase))
                .Where(element => !string.Equals(element.Attribute("protocol")?.Value, "AJP/1.3", StringComparison.OrdinalIgnoreCase))
                .Select(element => int.TryParse(
                    element.Attribute("port")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var port) ? port : -1)
                .Where(port => port > 0 && port <= 65535)
                .Distinct()
                .ToArray();
        }
        catch
        {
            return Array.Empty<int>();
        }
    }

    public static async Task WaitForStartupAsync(
        string tomcatRoot,
        IReadOnlyCollection<int> ports,
        CancellationToken cancellationToken,
        TimeSpan? timeout = null,
        Action<int, int>? progress = null)
    {
        if (ports.Count == 0)
        {
            throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。");
        }

        ResetApplicationProbeCache();
        var deadline = DateTime.UtcNow + (timeout ?? DefaultStartupTimeout);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var active = GetActiveTcpPorts();
            var ready = ports.Count(active.Contains);
            progress?.Invoke(ready, ports.Count);
            if (ready == ports.Count)
            {
                await EnsureStableAsync(ports, TimeSpan.FromSeconds(1), cancellationToken);

                var deployments = GetDeploymentsForPorts(ports);
                if (deployments.Count > 0)
                {
                    await WaitForApplicationsReadyAsync(
                        tomcatRoot,
                        deployments,
                        ports,
                        deadline,
                        cancellationToken);
                }
                return;
            }

            await Task.Delay(300, cancellationToken);
        }

        throw BuildStartupTimeout(tomcatRoot, ports, Array.Empty<TomcatProductDeploymentInfo>());
    }

    public static async Task EnsureStableAsync(
        IReadOnlyCollection<int> ports,
        TimeSpan stabilityDelay,
        CancellationToken cancellationToken)
    {
        if (!ArePortsListening(ports))
        {
            throw new InvalidOperationException($"Tomcat 端口 {string.Join(", ", ports)} 未监听，Java 进程可能已退出。");
        }

        await Task.Delay(stabilityDelay, cancellationToken);
        if (!ArePortsListening(ports))
        {
            throw new InvalidOperationException($"Tomcat 启动后很快退出，端口 {string.Join(", ", ports)} 已停止监听。");
        }
    }

    public static bool ArePortsListening(IEnumerable<int> ports)
    {
        var expected = ports
            .Where(port => port > 0 && port <= 65535)
            .Distinct()
            .ToArray();
        if (expected.Length == 0)
        {
            return false;
        }

        var active = GetActiveTcpPorts();
        return expected.All(active.Contains);
    }

    /// <summary>
    /// Used by the shared-start progress UI. For a plain Tomcat connector this
    /// reports TCP readiness. For a managed product connector it only reports
    /// ready after the product URL itself answers with a usable HTTP status.
    /// </summary>
    public static bool IsPortListening(int port)
    {
        if (!GetActiveTcpPorts().Contains(port))
        {
            ApplicationProbeCache.TryRemove(port, out _);
            return false;
        }

        var deployment = GetDeploymentForPort(port);
        if (deployment is null)
        {
            return true;
        }

        var now = DateTime.UtcNow;
        if (ApplicationProbeCache.TryGetValue(port, out var cached))
        {
            if (now - cached.CreatedUtc <= ApplicationProbeCacheLifetime)
            {
                return IsCompletedSuccessfully(cached.Task);
            }

            ApplicationProbeCache.TryRemove(port, out _);
        }

        var task = ProbeManagedApplicationAsync(deployment, CancellationToken.None);
        ApplicationProbeCache[port] = new ApplicationProbeCacheEntry(now, task);
        return IsCompletedSuccessfully(task);
    }

    internal static bool IsApplicationHttpStatusReady(HttpStatusCode statusCode)
    {
        var code = (int)statusCode;
        return code is >= 200 and < 400 ||
               statusCode is HttpStatusCode.BadRequest or
                   HttpStatusCode.Unauthorized or
                   HttpStatusCode.Forbidden or
                   HttpStatusCode.MethodNotAllowed;
    }

    private static bool IsCompletedSuccessfully(Task<bool> task)
    {
        return task.Status == TaskStatus.RanToCompletion && task.Result;
    }

    private static async Task WaitForApplicationsReadyAsync(
        string tomcatRoot,
        IReadOnlyList<TomcatProductDeploymentInfo> deployments,
        IReadOnlyCollection<int> ports,
        DateTime deadline,
        CancellationToken cancellationToken)
    {
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!ArePortsListening(ports))
            {
                throw new InvalidOperationException(
                    $"Tomcat 应用初始化期间连接器停止监听，Java 进程可能已退出。端口：{string.Join(", ", ports)}");
            }

            var firstPass = await ProbeApplicationsAsync(deployments, cancellationToken);
            if (firstPass.All(result => result.Value))
            {
                // Require two consecutive HTTP-ready samples. A connector that
                // briefly answers while its Context is still failing must not
                // turn the progress bar into a false 100% success.
                await Task.Delay(750, cancellationToken);
                var secondPass = await ProbeApplicationsAsync(deployments, cancellationToken);
                if (secondPass.All(result => result.Value))
                {
                    foreach (var deployment in deployments)
                    {
                        ApplicationProbeCache[deployment.Port] = new ApplicationProbeCacheEntry(
                            DateTime.UtcNow,
                            Task.FromResult(true));
                    }
                    return;
                }
            }

            await Task.Delay(300, cancellationToken);
        }

        var finalPass = await ProbeApplicationsAsync(deployments, cancellationToken);
        var failed = deployments
            .Where(deployment => !finalPass.TryGetValue(deployment.Port, out var ready) || !ready)
            .ToArray();
        throw BuildStartupTimeout(tomcatRoot, ports, failed);
    }

    private static async Task<IReadOnlyDictionary<int, bool>> ProbeApplicationsAsync(
        IReadOnlyList<TomcatProductDeploymentInfo> deployments,
        CancellationToken cancellationToken)
    {
        var tasks = deployments.Select(async deployment =>
            new KeyValuePair<int, bool>(
                deployment.Port,
                await ProbeManagedApplicationAsync(deployment, cancellationToken))).ToArray();
        var results = await Task.WhenAll(tasks);
        return results
            .GroupBy(result => result.Key)
            .ToDictionary(group => group.Key, group => group.All(result => result.Value));
    }

    private static async Task<bool> ProbeManagedApplicationAsync(
        TomcatProductDeploymentInfo deployment,
        CancellationToken cancellationToken)
    {
        if (!GetActiveTcpPorts().Contains(deployment.Port))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, BuildProbeUri(deployment));
            request.Headers.ConnectionClose = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ApplicationProbeTimeout);
            using var response = await ApplicationProbeClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeout.Token).ConfigureAwait(false);
            return IsApplicationHttpStatusReady(response.StatusCode);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static Uri BuildProbeUri(TomcatProductDeploymentInfo deployment)
    {
        var contextPath = string.IsNullOrWhiteSpace(deployment.ContextPath)
            ? "/"
            : deployment.ContextPath.Trim();
        if (!contextPath.StartsWith("/", StringComparison.Ordinal))
        {
            contextPath = "/" + contextPath;
        }
        if (!contextPath.EndsWith("/", StringComparison.Ordinal))
        {
            contextPath += "/";
        }

        return new Uri($"http://127.0.0.1:{deployment.Port}{contextPath}", UriKind.Absolute);
    }

    private static HttpClient CreateApplicationProbeClient()
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        };
        return new HttpClient(handler)
        {
            Timeout = Timeout.InfiniteTimeSpan
        };
    }

    private static TomcatProductDeploymentInfo? GetDeploymentForPort(int port)
    {
        RefreshDeploymentCacheIfNeeded();
        return _deploymentByPort.TryGetValue(port, out var deployment) ? deployment : null;
    }

    private static IReadOnlyList<TomcatProductDeploymentInfo> GetDeploymentsForPorts(
        IReadOnlyCollection<int> ports)
    {
        RefreshDeploymentCacheIfNeeded(force: true);
        var expected = new HashSet<int>(ports);
        return _deploymentByPort.Values
            .Where(deployment => expected.Contains(deployment.Port))
            .OrderBy(deployment => deployment.Port)
            .ToArray();
    }

    private static void RefreshDeploymentCacheIfNeeded(bool force = false)
    {
        var now = DateTime.UtcNow;
        if (!force && now < _deploymentCacheExpiresUtc)
        {
            return;
        }

        lock (DeploymentCacheGate)
        {
            now = DateTime.UtcNow;
            if (!force && now < _deploymentCacheExpiresUtc)
            {
                return;
            }

            try
            {
                _deploymentByPort = ProductDeploymentService.LoadTomcatDeploymentInfos()
                    .Where(info => info.Port is > 0 and <= 65535)
                    .GroupBy(info => info.Port)
                    .ToDictionary(group => group.Key, group => group.First());
            }
            catch
            {
                _deploymentByPort = new Dictionary<int, TomcatProductDeploymentInfo>();
            }

            _deploymentCacheExpiresUtc = now + DeploymentCacheLifetime;
        }
    }

    private static void ResetApplicationProbeCache()
    {
        ApplicationProbeCache.Clear();
        lock (DeploymentCacheGate)
        {
            _deploymentCacheExpiresUtc = DateTime.MinValue;
        }
    }

    private static HashSet<int> GetActiveTcpPorts()
    {
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Select(endpoint => endpoint.Port)
                .ToHashSet();
        }
        catch
        {
            return new HashSet<int>();
        }
    }

    private static TimeoutException BuildStartupTimeout(
        string tomcatRoot,
        IReadOnlyCollection<int> ports,
        IReadOnlyCollection<TomcatProductDeploymentInfo> failedApplications)
    {
        var recentLog = ReadRecentLog(tomcatRoot);
        string message;
        if (failedApplications.Count > 0)
        {
            var failedText = string.Join(", ", failedApplications.Select(application =>
                $"{application.ProductId}({application.Port})"));
            message = $"Tomcat 连接器已启动，但以下应用未通过真实 HTTP 就绪检查：{failedText}。" +
                      $"请检查 {Path.Combine(tomcatRoot, "logs")}。";
        }
        else
        {
            message = $"Tomcat 启动失败，端口 {string.Join(", ", ports)} 未在限定时间内稳定监听。" +
                      $"请检查 {Path.Combine(tomcatRoot, "logs")}。";
        }

        if (!string.IsNullOrWhiteSpace(recentLog))
        {
            message += Environment.NewLine + Environment.NewLine +
                       "最近的 Tomcat 日志：" + Environment.NewLine + recentLog;
        }

        return new TimeoutException(message);
    }

    private static string ReadRecentLog(string tomcatRoot)
    {
        try
        {
            var logs = Path.Combine(tomcatRoot, "logs");
            if (!Directory.Exists(logs))
            {
                return string.Empty;
            }

            var file = Directory.EnumerateFiles(logs, "*", SearchOption.TopDirectoryOnly)
                .Where(path => path.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".out", StringComparison.OrdinalIgnoreCase) ||
                               path.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault();
            if (file is null)
            {
                return string.Empty;
            }

            var lines = new Queue<string>();
            foreach (var line in File.ReadLines(file))
            {
                lines.Enqueue(line);
                if (lines.Count > 60)
                {
                    lines.Dequeue();
                }
            }

            return string.Join(Environment.NewLine, lines);
        }
        catch
        {
            return string.Empty;
        }
    }
}
