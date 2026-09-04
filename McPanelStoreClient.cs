using System.IO;
using System.IO.Compression;
using System.Globalization;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;

namespace MCPanel;

public sealed class McPanelStoreClient : IDisposable
{
    private const string Namespace = "http://www.itmc.cn/webservices/productServer/";
    // The vendor catalog SOAP endpoint currently has no working HTTPS virtual host.
    // It carries only the vendor's fixed read-only catalog identity; package bytes never use it.
    private const string ServiceEndpoint = "http://regservice.itmc.cn/Service.asmx";
    private const string SoapUserName = "itmc";
    private const string SoapPassword = "itmc";
    private const string DownloadUserName = "_update_server";
    private const string DownloadPassword = "_update086405849";
    private const int MaxDownloadRedirects = 8;
    private const int MaxDownloadAttempts = 3;
    private const int MaxConcurrentFileDownloads = 6;
    private static readonly string[] ProductModuleIds = ["DS3107", "DS3102", "DS0102", "DS0103", "YX030104", "YX030106", "QT0501"];
    private static readonly AuthenticationHeaderValue DownloadAuthorization = new(
        "Basic",
        Convert.ToBase64String(Encoding.ASCII.GetBytes($"{DownloadUserName}:{DownloadPassword}")));

    private readonly HttpClient _soapClient;
    private readonly HttpClient _downloadClient;
    private readonly SvnProductTransferService _svnProductTransfer = new();

    public McPanelStoreClient()
    {
        ConfigureLegacyFrameworkNetworking();
        _soapClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(25)
        };

        _downloadClient = new HttpClient(CreateDownloadHandler())
        {
            Timeout = TimeSpan.FromMinutes(30)
        };
    }

    private static HttpClientHandler CreateDownloadHandler()
    {
        return new HttpClientHandler
        {
            AllowAutoRedirect = false
        };
    }

    private static void ConfigureLegacyFrameworkNetworking()
    {
        // Some vendor SVN endpoints still negotiate TLS 1.0. Keep modern TLS enabled,
        // but do not replace the framework's certificate validation callback.
        ServicePointManager.SecurityProtocol |=
            SecurityProtocolType.Tls |
            (SecurityProtocolType)768 |
            (SecurityProtocolType)3072;
    }

    public async Task<IReadOnlyList<ProductItem>> GetProductsAsync(CancellationToken cancellationToken = default)
    {
        var results = new List<ProductItem>();

        results.AddRange(ParseProducts(await InvokeSoapAsync("GetCustProduct", "<CustUserID>itmc</CustUserID>", SoapUserName, SoapPassword, cancellationToken)));
        if (results.Count > 0)
        {
            return MergeProducts(results);
        }

        results.AddRange(ParseProducts(await InvokeSoapAsync("GetAllProduct", string.Empty, SoapUserName, SoapPassword, cancellationToken)));
        if (results.Count > 0)
        {
            return MergeProducts(results);
        }

        results.AddRange(ParseProducts(await InvokeSoapAsync("GetCustProductModule", BuildProductModuleBody(), SoapUserName, SoapPassword, cancellationToken)));
        if (results.Count > 0)
        {
            return MergeProducts(results);
        }

        return [];
    }

    public async Task<string?> DownloadProductAsync(
        ProductItem product,
        Action<double>? progress,
        Action<string>? status = null,
        DownloadPauseController? pauseController = null,
        CancellationToken cancellationToken = default)
    {
        Action<ProductDownloadProgress>? detailedProgress = progress is null
            ? null
            : update => progress(update.Percent);
        return await DownloadProductWithDetailsAsync(
            product,
            detailedProgress,
            status,
            pauseController,
            cancellationToken);
    }

    public async Task<string?> DownloadProductWithDetailsAsync(
        ProductItem product,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status = null,
        DownloadPauseController? pauseController = null,
        CancellationToken cancellationToken = default)
    {
        await WaitForDownloadPermissionAsync(pauseController, cancellationToken);
        var downloadUrl = product.DownloadUrl;
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            downloadUrl = await ResolveDownloadUrlAsync(product, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        if (!Uri.TryCreate(downloadUrl, UriKind.Absolute, out var uri))
        {
            uri = new Uri(new Uri("http://regservice.itmc.cn/down/"), downloadUrl!.TrimStart('/'));
        }
        uri = ValidateDownloadUri(uri);

        if (product.UsesSvn || IsVendorSvnRepository(uri))
        {
            var installedDirectory = ProductInstallPathResolver.ResolveProductDirectory(product);
            return await _svnProductTransfer.SyncWithDetailsAsync(
                uri,
                installedDirectory,
                progress,
                message => status?.Invoke($"{product.ProductId}：{message}"),
                cancellationToken,
                pauseController);
        }

        var downloads = ComponentPaths.ProductDownloadRoot;
        Directory.CreateDirectory(downloads);
        var productDownloadDirectory = Path.Combine(downloads, SanitizeFileName(product.ProductId));

        if (downloadUrl!.Contains(":9443/udp/", StringComparison.OrdinalIgnoreCase))
        {
            status?.Invoke($"正在准备 {product.ProductId} 的产品文件...");
            await DownloadWebDirectoryAsync(
                EnsureDirectoryUri(uri),
                productDownloadDirectory,
                progress,
                message => status?.Invoke($"{product.ProductId}：{message}"),
                pauseController,
                cancellationToken);
            return productDownloadDirectory;
        }

        var fileName = product.FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = Path.GetFileName(uri.LocalPath);
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = $"{product.ProductId}.zip";
        }

        var stagingDirectory = $"{productDownloadDirectory}.downloading";
        TryDeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);
        var stagingTarget = Path.Combine(stagingDirectory, SanitizeFileName(fileName!));
        var target = Path.Combine(productDownloadDirectory, SanitizeFileName(fileName!));
        DownloadResult? downloadResult = null;
        try
        {
            await WaitForDownloadPermissionAsync(pauseController, cancellationToken);
            using var response = await SendDownloadRequestAsync(uri, cancellationToken);
            downloadResult = await DownloadService.SaveResponseAsync(
                response,
                stagingTarget,
                snapshot =>
                {
                    var message = snapshot.TotalBytes is > 0
                        ? $"正在下载 {fileName}：{ProductTransferFormatting.FormatBytes(snapshot.BytesReceived)} / {ProductTransferFormatting.FormatBytes(snapshot.TotalBytes.Value)}"
                        : $"正在下载 {fileName}：{ProductTransferFormatting.FormatBytes(snapshot.BytesReceived)}";
                    progress?.Invoke(new ProductDownloadProgress(
                        snapshot.TotalBytes is > 0
                            ? Compat.Clamp(snapshot.BytesReceived * 100d / snapshot.TotalBytes.Value, 0, 99)
                            : 0,
                        message,
                        snapshot.BytesReceived,
                        snapshot.TotalBytes,
                        ProductTransferFormatting.FormatRate(snapshot.BytesPerSecond)));
                    status?.Invoke(message);
                },
                pauseController,
                cancellationToken,
                progressIntervalMilliseconds: 150,
                validatePartial: ValidateDownloadedPackage,
                emptyFileMessage: "下载服务器返回了空产品包。",
                incompleteFileMessage: (expected, actual) =>
                    $"产品包下载不完整：应为 {expected} 字节，实际 {actual} 字节。");
            WriteSha256Sidecar(stagingTarget);
            TryDeleteDirectory(productDownloadDirectory);
            Directory.Move(stagingDirectory, productDownloadDirectory);
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }

        progress?.Invoke(new ProductDownloadProgress(
            100,
            $"{fileName} 下载完成",
            new FileInfo(target).Length,
            new FileInfo(target).Length,
            ProductTransferFormatting.FormatRate(downloadResult?.AverageBytesPerSecond ?? 0)));
        return target;
    }

    private async Task DownloadWebDirectoryAsync(
        Uri rootUri,
        string targetDirectory,
        Action<ProductDownloadProgress>? progress,
        Action<string>? status,
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken)
    {
        var stagingDirectory = $"{targetDirectory}.downloading";
        TryDeleteDirectory(stagingDirectory);
        Directory.CreateDirectory(stagingDirectory);

        try
        {
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var files = new List<RemoteWebFile>();
            var visitedSync = new object();
            var discoveredDirectories = 0;
            progress?.Invoke(new ProductDownloadProgress(0, "正在准备下载产品文件..."));
            status?.Invoke("正在准备下载产品文件...");
            using var discoveryGate = new SemaphoreSlim(4, 4);
            await DiscoverWebDavDirectoryAsync(
                rootUri,
                rootUri,
                visited,
                visitedSync,
                files,
                discoveryGate,
                () =>
                {
                    var current = Interlocked.Increment(ref discoveredDirectories);
                    if (current == 1 || current % 8 == 0)
                    {
                        status?.Invoke($"正在准备下载文件...（已检查 {current} 个目录）");
                    }
                },
                pauseController,
                cancellationToken);

            files = files
                .DistinctBy(file => file.Uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (files.Count == 0)
            {
                throw new InvalidDataException("下载目录没有获取到任何产品文件，请检查下载地址或服务器权限后重试。");
            }

            var knownTotalBytes = files.Where(file => file.Length.HasValue).Sum(file => file.Length!.Value);
            var allLengthsKnown = files.All(file => file.Length.HasValue);
            status?.Invoke(allLengthsKnown
                ? $"已准备 {files.Count} 个文件，共 {ProductTransferFormatting.FormatBytes(knownTotalBytes)}，开始下载..."
                : $"已准备 {files.Count} 个文件，开始下载...");
            progress?.Invoke(new ProductDownloadProgress(
                3,
                allLengthsKnown
                    ? $"已准备 {files.Count} 个文件，共 {ProductTransferFormatting.FormatBytes(knownTotalBytes)}，开始下载..."
                    : $"已准备 {files.Count} 个文件，开始下载...",
                0,
                allLengthsKnown ? knownTotalBytes : null,
                null));
            long downloadedBytes = 0;
            var completedFiles = 0;
            var progressSync = new object();
            var lastProgressUpdate = DateTime.MinValue;
            var downloadStartedAt = DateTime.UtcNow;

            void ReportDownloadProgress(bool force = false)
            {
                lock (progressSync)
                {
                    var now = DateTime.UtcNow;
                    if (!force && now - lastProgressUpdate < TimeSpan.FromMilliseconds(150))
                    {
                        return;
                    }

                    lastProgressUpdate = now;
                    var bytes = Interlocked.Read(ref downloadedBytes);
                    var completed = Volatile.Read(ref completedFiles);
                    var percent = allLengthsKnown && knownTotalBytes > 0
                        ? 3 + Compat.Clamp(bytes * 96d / knownTotalBytes, 0, 96)
                        : 3 + completed * 96d / files.Count;
                    var speed = bytes / Math.Max((now - downloadStartedAt).TotalSeconds, 0.001);
                    var transferText = allLengthsKnown
                        ? $"正在下载产品文件：{ProductTransferFormatting.FormatBytes(bytes)} / {ProductTransferFormatting.FormatBytes(knownTotalBytes)}"
                        : $"正在下载产品文件：{ProductTransferFormatting.FormatBytes(bytes)}（{completed} / {files.Count} 个文件）";
                    progress?.Invoke(new ProductDownloadProgress(
                        percent,
                        transferText,
                        bytes,
                        allLengthsKnown ? knownTotalBytes : null,
                        ProductTransferFormatting.FormatRate(speed)));
                }
            }

            await ParallelCompat.ForEachAsync(
                files,
                MaxConcurrentFileDownloads,
                cancellationToken,
                async (remoteFile, token) =>
                {
                    await WaitForDownloadPermissionAsync(pauseController, token);
                    using var fileResponse = await SendDownloadRequestAsync(remoteFile.Uri, token);
                    await SaveWebFileAsync(
                        rootUri,
                        remoteFile.Uri,
                        stagingDirectory,
                        fileResponse,
                        remoteFile.Length,
                        pauseController,
                        token,
                        bytes =>
                        {
                            Interlocked.Add(ref downloadedBytes, bytes);
                            ReportDownloadProgress();
                        });

                    var completed = Interlocked.Increment(ref completedFiles);
                    ReportDownloadProgress(force: true);
                    if (completed == 1 || completed == files.Count || completed % 10 == 0)
                    {
                        var bytes = Interlocked.Read(ref downloadedBytes);
                        status?.Invoke(allLengthsKnown
                            ? $"已下载 {ProductTransferFormatting.FormatBytes(bytes)} / {ProductTransferFormatting.FormatBytes(knownTotalBytes)}（{completed} / {files.Count} 个文件，{MaxConcurrentFileDownloads} 路并发）"
                            : $"已下载 {ProductTransferFormatting.FormatBytes(bytes)}（{completed} / {files.Count} 个文件，{MaxConcurrentFileDownloads} 路并发）");
                    }
                });

            var downloadedFiles = Directory.EnumerateFiles(stagingDirectory, "*", SearchOption.AllDirectories).Count();
            if (downloadedFiles != files.Count)
            {
                throw new InvalidDataException($"产品文件下载不完整：计划 {files.Count} 个，实际 {downloadedFiles} 个。");
            }

            WriteDirectorySha256Manifest(stagingDirectory);

            TryDeleteDirectory(targetDirectory);
            Directory.Move(stagingDirectory, targetDirectory);
            var finalBytes = allLengthsKnown
                ? knownTotalBytes
                : Directory.EnumerateFiles(targetDirectory, "*", SearchOption.AllDirectories)
                    .Sum(path => new FileInfo(path).Length);
            progress?.Invoke(new ProductDownloadProgress(
                100,
                "产品文件下载完成",
                finalBytes,
                allLengthsKnown ? knownTotalBytes : finalBytes,
                ProductTransferFormatting.FormatRate(finalBytes / Math.Max((DateTime.UtcNow - downloadStartedAt).TotalSeconds, 0.001))));
        }
        catch
        {
            TryDeleteDirectory(stagingDirectory);
            throw;
        }
    }

    private async Task DiscoverWebDavDirectoryAsync(
        Uri rootUri,
        Uri currentUri,
        HashSet<string> visited,
        object visitedSync,
        List<RemoteWebFile> files,
        SemaphoreSlim discoveryGate,
        Action? directoryDiscovered,
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken)
    {
        await WaitForDownloadPermissionAsync(pauseController, cancellationToken);
        var key = currentUri.AbsoluteUri.TrimEnd('/');
        lock (visitedSync)
        {
            if (!visited.Add(key))
            {
                return;
            }
        }

        directoryDiscovered?.Invoke();
        var children = new List<Uri>();
        await discoveryGate.WaitAsync(cancellationToken);
        try
        {
            using var response = await SendWebDavRequestAsync(currentUri, cancellationToken);
            var xml = await response.Content.ReadAsStringAsync(cancellationToken);
            var document = XDocument.Parse(xml);
            var discoveredFiles = new List<RemoteWebFile>();
            foreach (var item in document.Descendants().Where(element => element.Name.LocalName == "response"))
            {
                var href = item.Descendants().FirstOrDefault(element => element.Name.LocalName == "href")?.Value;
                if (string.IsNullOrWhiteSpace(href))
                {
                    continue;
                }

                var next = new Uri(new Uri(currentUri.GetLeftPart(UriPartial.Authority)), href);
                if (!next.Host.Equals(rootUri.Host, StringComparison.OrdinalIgnoreCase) ||
                    (!next.AbsolutePath.Equals(rootUri.AbsolutePath.TrimEnd('/'), StringComparison.OrdinalIgnoreCase) &&
                     !next.AbsolutePath.StartsWith(rootUri.AbsolutePath.TrimEnd('/') + "/", StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var isCollection = item.Descendants()
                    .Any(element => element.Name.LocalName == "collection");
                if (isCollection)
                {
                    if (!next.AbsoluteUri.TrimEnd('/').Equals(currentUri.AbsoluteUri.TrimEnd('/'), StringComparison.OrdinalIgnoreCase))
                    {
                        children.Add(EnsureDirectoryUri(next));
                    }
                }
                else
                {
                    var lengthText = item.Descendants()
                        .FirstOrDefault(element => element.Name.LocalName == "getcontentlength" && !string.IsNullOrWhiteSpace(element.Value))
                        ?.Value;
                    discoveredFiles.Add(new RemoteWebFile(
                        next,
                        long.TryParse(lengthText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var length) ? length : null));
                }
            }

            lock (files)
            {
                files.AddRange(discoveredFiles);
            }
        }
        finally
        {
            discoveryGate.Release();
        }

        var childTasks = children
            .DistinctBy(uri => uri.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
            .Select(child => DiscoverWebDavDirectoryAsync(
                rootUri,
                child,
                visited,
                visitedSync,
                files,
                discoveryGate,
                directoryDiscovered,
                pauseController,
                cancellationToken))
            .ToArray();
        await Task.WhenAll(childTasks);
    }

    private static async Task SaveWebFileAsync(
        Uri rootUri,
        Uri fileUri,
        string targetDirectory,
        HttpResponseMessage response,
        long? expectedLength,
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken,
        Action<long>? bytesReceived = null)
    {
        var relative = Uri.UnescapeDataString(rootUri.MakeRelativeUri(fileUri).ToString()).Replace('/', Path.DirectorySeparatorChar);
        relative = relative.Split('?', '#')[0].TrimStart(Path.DirectorySeparatorChar);
        if (string.IsNullOrWhiteSpace(relative))
        {
            relative = Path.GetFileName(fileUri.LocalPath);
        }

        var target = Path.GetFullPath(Path.Combine(targetDirectory, relative));
        var root = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var lastReportedBytes = 0L;
        await DownloadService.SaveResponseAsync(
            response,
            target,
            snapshot =>
            {
                var delta = snapshot.BytesReceived - lastReportedBytes;
                if (delta > 0)
                {
                    bytesReceived?.Invoke(delta);
                }

                lastReportedBytes = snapshot.BytesReceived;
            },
            pauseController,
            cancellationToken,
            progressIntervalMilliseconds: 0,
            expectedBytes: expectedLength,
            emptyFileMessage: $"产品文件下载为空：{fileUri}",
            incompleteFileMessage: (expected, actual) =>
                $"文件下载不完整：{fileUri}，应为 {expected} 字节，实际 {actual} 字节。");
    }

    private static Task WaitForDownloadPermissionAsync(
        DownloadPauseController? pauseController,
        CancellationToken cancellationToken) =>
        pauseController?.WaitIfPausedAsync(cancellationToken) ?? Task.CompletedTask;

    private async Task<HttpResponseMessage> SendWebDavRequestAsync(Uri uri, CancellationToken cancellationToken)
    {
        return await SendWebDavRequestAsync(uri, _downloadClient, cancellationToken);
    }

    private static async Task<HttpResponseMessage> SendWebDavRequestAsync(Uri uri, HttpClient client, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(new HttpMethod("PROPFIND"), uri);
        request.Headers.UserAgent.ParseAdd("MCPanel/1.0");
        request.Headers.TryAddWithoutValidation("Depth", "1");
        if (IsTrustedDownloadHost(uri.Host))
        {
            request.Headers.Authorization = DownloadAuthorization;
        }

        request.Content = new StringContent(
            """<?xml version="1.0" encoding="utf-8"?><propfind xmlns="DAV:"><prop><getcontentlength/><resourcetype/></prop></propfind>""",
            Encoding.UTF8,
            "application/xml");
        var response = await client.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return response;
    }

    private async Task<HttpResponseMessage> SendDownloadRequestAsync(Uri initialUri, CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        for (var attempt = 1; attempt <= MaxDownloadAttempts; attempt++)
        {
            try
            {
                return await SendDownloadRequestOnceAsync(initialUri, _downloadClient, cancellationToken);
            }
            catch (HttpRequestException ex) when (attempt < MaxDownloadAttempts)
            {
                lastError = ex;
                await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt), cancellationToken);
            }
        }

        throw lastError ?? new HttpRequestException("产品下载请求失败。");
    }

    private static async Task<HttpResponseMessage> SendDownloadRequestOnceAsync(Uri initialUri, HttpClient client, CancellationToken cancellationToken)
    {
        var currentUri = ValidateDownloadUri(initialUri);
        for (var redirect = 0; redirect <= MaxDownloadRedirects; redirect++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
            request.Headers.UserAgent.ParseAdd("MCPanel/1.0");
            request.Headers.Accept.ParseAdd("*/*");
            if (IsTrustedDownloadHost(currentUri.Host))
            {
                request.Headers.Authorization = DownloadAuthorization;
            }

            var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if (IsRedirect(response.StatusCode) && response.Headers.Location is not null)
            {
                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(currentUri, response.Headers.Location);
                response.Dispose();
                currentUri = ValidateDownloadUri(nextUri);
                continue;
            }

            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                response.Dispose();
                throw new HttpRequestException(
                    $"下载服务器拒绝认证（401）：{currentUri.Host}。请确认下载账号仍有效或服务器权限已开放。");
            }

            if (!response.IsSuccessStatusCode)
            {
                var statusCode = response.StatusCode;
                var reason = response.ReasonPhrase;
                response.Dispose();
                throw new HttpRequestException(
                    $"下载服务器返回错误 {(int)statusCode} {reason}：{currentUri}");
            }

            return response;
        }

        throw new HttpRequestException($"下载地址重定向次数过多：{initialUri}");
    }

    private static bool IsTrustedDownloadHost(string host) =>
        host.Equals("update.itmc.org.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".itmc.org.cn", StringComparison.OrdinalIgnoreCase) ||
        host.Equals("update.product.itmc.cn", StringComparison.OrdinalIgnoreCase) ||
        host.EndsWith(".itmc.cn", StringComparison.OrdinalIgnoreCase);

    internal static bool IsVendorSvnRepository(Uri uri)
    {
        if (!IsTrustedDownloadHost(uri.Host))
        {
            return false;
        }

        if (uri.AbsolutePath.Contains("/svn/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // The original UpdateClient treats the vendor catalog's SVN field as
        // an SVN source even when the server exposes it through the /udp/
        // virtual path on port 9443.  Do not send these repositories through
        // the ordinary HTTP directory downloader.
        return uri.Host.Equals("update.product.itmc.cn", StringComparison.OrdinalIgnoreCase) &&
               uri.Port == 9443 &&
               uri.AbsolutePath.Contains("/udp/", StringComparison.OrdinalIgnoreCase);
    }

    internal static Uri ValidateDownloadUri(Uri uri)
    {
        if (uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        // The legacy ITMC store is HTTP-only. Preserve the original scheme for
        // the allowlisted vendor hosts instead of rewriting it to a broken HTTPS
        // virtual host. Other plain-HTTP catalog URLs remain rejected.
        if (uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase) &&
            IsTrustedDownloadHost(uri.Host))
        {
            return uri;
        }

        throw new InvalidOperationException($"产品下载地址不受支持：{uri}");
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode == HttpStatusCode.MovedPermanently ||
        statusCode == HttpStatusCode.Redirect ||
        statusCode == HttpStatusCode.RedirectMethod ||
        statusCode == HttpStatusCode.TemporaryRedirect ||
        (int)statusCode == 308;

    private static Uri EnsureDirectoryUri(Uri uri) =>
        uri.AbsoluteUri.EndsWith("/", StringComparison.Ordinal)
            ? uri
            : new Uri($"{uri.AbsoluteUri}/", UriKind.Absolute);

    private sealed record RemoteWebFile(Uri Uri, long? Length);

    private static void ValidateDownloadedPackage(string path)
    {
        var extension = Path.GetExtension(path);
        if (!extension.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !extension.Equals(".war", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            if (archive.Entries.Count == 0)
            {
                throw new InvalidDataException("产品压缩包中没有任何文件。");
            }
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException("下载的产品包不是完整有效的 ZIP/WAR 文件。", ex);
        }
    }

    private static void WriteSha256Sidecar(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
        AtomicFile.WriteAllText(path + ".sha256", $"{hash}  {Path.GetFileName(path)}{Environment.NewLine}");
    }

    private static void WriteDirectorySha256Manifest(string directory)
    {
        var lines = new List<string>();
        foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            using var sha = SHA256.Create();
            using var stream = File.OpenRead(file);
            var hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            var relative = file.Substring(directory.TrimEnd(Path.DirectorySeparatorChar).Length + 1).Replace('\\', '/');
            lines.Add($"{hash}  {relative}");
        }
        AtomicFile.WriteAllText(Path.Combine(directory, "SHA256SUMS.txt"), string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    public void Dispose()
    {
        _soapClient.Dispose();
        _downloadClient.Dispose();
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
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }

    private async Task<string?> ResolveDownloadUrlAsync(ProductItem product, CancellationToken cancellationToken)
    {
        var body = $"<productname>{Escape(product.ProductId)}</productname><sType></sType><VNO></VNO><norder>0</norder>";
        var result = await InvokeSoapAsync("GetUpdateFile", body, SoapUserName, SoapPassword, cancellationToken);
        var parsed = ExtractResultText(result);
        var url = FindFirstUrl(parsed);
        if (!string.IsNullOrWhiteSpace(url))
        {
            return url;
        }

        if (!string.IsNullOrWhiteSpace(parsed) && LooksLikeFilePath(parsed))
        {
            return parsed;
        }

        return null;
    }

    private async Task<string> InvokeSoapAsync(string method, string body, string userName, string password, CancellationToken cancellationToken)
    {
        var soap = $$"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance" xmlns:xsd="http://www.w3.org/2001/XMLSchema" xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/">
              <soap:Header>
                <clientinfo xmlns="{{Namespace}}">
                  <Uname>{{Escape(userName)}}</Uname>
                  <Password>{{Escape(password)}}</Password>
                </clientinfo>
              </soap:Header>
              <soap:Body>
                <{{method}} xmlns="{{Namespace}}">{{body}}</{{method}}>
              </soap:Body>
            </soap:Envelope>
            """;

        using var request = new HttpRequestMessage(HttpMethod.Post, ServiceEndpoint);
        request.Headers.TryAddWithoutValidation("SOAPAction", $"{Namespace}{method}");
        request.Content = new StringContent(soap, Encoding.UTF8, "text/xml");

        using var response = await _soapClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string BuildProductModuleBody()
    {
        var ids = string.Concat(ProductModuleIds.Select(id => $"<string>{Escape(id)}</string>"));
        return $"<CustUserID>admin</CustUserID><ProductModuleIDList>{ids}</ProductModuleIDList>";
    }

    private static IReadOnlyList<ProductItem> ParseProducts(string response)
    {
        var result = ExtractResultText(response);
        if (string.IsNullOrWhiteSpace(result))
        {
            return [];
        }

        var trimmed = result.TrimStart();
        try
        {
            if (trimmed.StartsWith("[", StringComparison.Ordinal) || trimmed.StartsWith("{", StringComparison.Ordinal))
            {
                return ParseJsonProducts(result).ToList();
            }

            if (trimmed.StartsWith("<", StringComparison.Ordinal))
            {
                return ParseXmlProducts(result).ToList();
            }
        }
        catch (Exception ex) when (ex is JsonException or XmlException)
        {
            // A malformed payload is reported as an empty catalog and can be
            // retried from the online list; never reinterpret JSON as CSV rows.
            return [];
        }

        return ParseDelimitedProducts(result).ToList();
    }

    private static IEnumerable<ProductItem> ParseJsonProducts(string result)
    {
        if (!result.TrimStart().StartsWith("[", StringComparison.Ordinal) && !result.TrimStart().StartsWith("{", StringComparison.Ordinal))
        {
            yield break;
        }

        using var document = JsonDocument.Parse(result);
        var elements = document.RootElement.ValueKind == JsonValueKind.Array
            ? document.RootElement.EnumerateArray()
            : document.RootElement.EnumerateObject().Select(p => p.Value);

        foreach (var element in elements)
        {
            foreach (var nestedProduct in TryCreateNestedProducts(element))
            {
                yield return nestedProduct;
            }

            if (TryCreateProduct(element, out var product))
            {
                yield return product;
            }
        }
    }

    private static IEnumerable<ProductItem> ParseXmlProducts(string result)
    {
        if (!result.TrimStart().StartsWith("<", StringComparison.Ordinal))
        {
            yield break;
        }

        var document = XDocument.Parse(result);
        foreach (var element in document.Descendants().Where(e => e.Elements().Any()))
        {
            var values = element.Elements().ToDictionary(e => e.Name.LocalName, e => e.Value, StringComparer.OrdinalIgnoreCase);
            if (TryCreateProduct(values, out var product))
            {
                yield return product;
            }
        }
    }

    private static IEnumerable<ProductItem> ParseDelimitedProducts(string result)
    {
        foreach (var line in result.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.Split(new[] { '|', ',', '\t', ';' }, StringSplitOptions.None)
                .Select(part => part.Trim())
                .ToArray();
            if (parts.Length < 2)
            {
                continue;
            }

            var id = parts.FirstOrDefault(p => ProductModuleIds.Contains(p, StringComparer.OrdinalIgnoreCase)) ?? parts[0];
            var name = parts.FirstOrDefault(p => p.Contains("系统", StringComparison.Ordinal) || p.Contains("平台", StringComparison.Ordinal)) ?? parts[1];
            var url = parts.Select(FindFirstUrl).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
            yield return CreateProduct(id, name, "在线", url, null);
        }
    }

    private static bool TryCreateProduct(JsonElement element, out ProductItem product)
    {
        var values = element.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        return TryCreateProduct(values, out product);
    }

    private static IEnumerable<ProductItem> TryCreateNestedProducts(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        var rootValues = element.EnumerateObject()
            .Where(p => p.Value.ValueKind is JsonValueKind.String or JsonValueKind.Number)
            .ToDictionary(p => p.Name, p => p.Value.ToString(), StringComparer.OrdinalIgnoreCase);

        if (!element.TryGetProperty("Data", out var data) || data.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (var child in data.EnumerateArray())
        {
            if (child.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var values = rootValues.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.OrdinalIgnoreCase);
            foreach (var property in child.EnumerateObject())
            {
                values[property.Name] = property.Value.ToString();
            }

            if (TryCreateProduct(values, out var product))
            {
                yield return product;
            }
        }
    }

    private static bool TryCreateProduct(IReadOnlyDictionary<string, string> values, out ProductItem product)
    {
        var id = Pick(values, "VersionID", "ProductModuleID", "ProductID", "ModuleID", "SoftVersionID", "ID", "Code");
        var name = Pick(values, "ProductName", "ModuleName", "SoftName", "Name", "Title");
        if (string.IsNullOrWhiteSpace(id) && string.IsNullOrWhiteSpace(name))
        {
            product = null!;
            return false;
        }

        id = string.IsNullOrWhiteSpace(id) ? MakeProductId(name!) : id;
        name = string.IsNullOrWhiteSpace(name) ? id : name;
        var productId = Pick(values, "ProductID") ?? id!;
        var level = Pick(values, "VersionName", "Level", "Type", "sType", "Category") ?? "在线";
        var svnUrl = Pick(values, "SVN");
        var downloadUrl = Pick(values, "DownloadUrl", "DownLoadUrl", "FileUrl", "FilePath", "Path");
        var installRoot = Pick(values, "Url");
        var fileName = Pick(values, "FileName", "PackageName", "ZipName");
        var iconUrl = Pick(values, "ICOUrl", "IconUrl", "LogoUrl");
        var runEnvironment = Pick(values, "RunLambient", "RunEnvironment", "Runtime");
        var sqlEnvironment = Pick(values, "SQLambient", "SqlEnvironment", "Database");
        var devLanguage = Pick(values, "DevLanguage", "Language");
        var details = BuildProductDetails(values);
        var sysType = Pick(values, "SysType", "SystemType", "Architecture", "Bitness");
        product = CreateProduct(
            id!,
            productId,
            name!,
            level,
            svnUrl ?? downloadUrl,
            fileName,
            iconUrl,
            details,
            runEnvironment,
            sqlEnvironment,
            devLanguage,
            installRoot,
            sysType,
            usesSvn: !string.IsNullOrWhiteSpace(svnUrl));
        return true;
    }

    private static ProductItem CreateProduct(string id, string productId, string name, string level, string? downloadUrl, string? fileName, string? iconUrl = null, string? details = null, string? runEnvironment = null, string? sqlEnvironment = null, string? devLanguage = null, string? installRoot = null, string? sysType = null, bool usesSvn = false)
    {
        var remoteIconUrl = !string.IsNullOrWhiteSpace(iconUrl) && ProductIconCache.IsCacheableRemoteIcon(iconUrl!)
            ? iconUrl
            : null;
        var icon = remoteIconUrl is not null
            ? remoteIconUrl
            : ProductModuleIds.Contains(productId, StringComparer.OrdinalIgnoreCase)
                ? $"/Assets/Logo/{productId}.png"
                : ProductModuleIds.Contains(id, StringComparer.OrdinalIgnoreCase)
            ? $"/Assets/Logo/{id}.png"
            : "/Assets/defaultimg.png";

        return new ProductItem(id, name, level, icon, ProductSource.Online)
        {
            RemoteIconUrl = remoteIconUrl,
            RunEnvironment = runEnvironment,
            SqlEnvironment = sqlEnvironment,
            DevLanguage = devLanguage,
            InstallRoot = installRoot,
            SysType = sysType,
            UsesSvn = usesSvn,
            DownloadUrl = downloadUrl,
            FileName = fileName,
            StatusText = string.IsNullOrWhiteSpace(downloadUrl)
                ? details ?? "在线产品，尚未解析到下载地址。"
                : $"{details}\n下载源：{downloadUrl}".Trim()
        };
    }

    private static ProductItem CreateProduct(string id, string name, string level, string? downloadUrl, string? fileName)
    {
        return CreateProduct(id, id, name, level, downloadUrl, fileName);
    }

    private static string BuildProductDetails(IReadOnlyDictionary<string, string> values)
    {
        var parts = new[]
        {
            Pick(values, "RunLambient") is { } run ? $"运行环境：{run}" : null,
            Pick(values, "SQLambient") is { } sql ? $"数据库：{sql}" : null,
            Pick(values, "DevLanguage") is { } lang ? $"语言：{lang}" : null,
            Pick(values, "SysType") is { } sys ? $"系统：{sys} 位" : null,
            Pick(values, "Price") is { } price ? $"价格：{price}" : null
        }.Where(p => !string.IsNullOrWhiteSpace(p));

        return string.Join("  ", parts);
    }

    private static IReadOnlyList<ProductItem> MergeProducts(IEnumerable<ProductItem> products)
    {
        return products
            .GroupBy(p => p.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(p => p.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string ExtractResultText(string response)
    {
        var document = XDocument.Parse(response);
        return document.Descendants()
            .FirstOrDefault(e => e.Name.LocalName.EndsWith("Result", StringComparison.OrdinalIgnoreCase))
            ?.Value
            ?.Trim() ?? string.Empty;
    }

    private static string? Pick(IReadOnlyDictionary<string, string> values, params string[] keys)
    {
        foreach (var key in keys)
        {
            if (values.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                return value.Trim();
            }
        }

        return null;
    }

    private static string? FindFirstUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return Regex.Match(value, @"https?://[^\s""'<>]+", RegexOptions.IgnoreCase).Value;
    }

    private static bool LooksLikeFilePath(string value)
    {
        return value.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".rar", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".7z", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            || value.EndsWith(".msi", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeProductId(string text)
    {
        var normalized = Regex.Replace(text, @"\W+", string.Empty);
        return normalized.Length > 24 ? normalized.Substring(0, 24) : normalized;
    }

    private static string Escape(string value) => SecurityElement.Escape(value) ?? string.Empty;

    private static string SanitizeFileName(string fileName)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            fileName = fileName.Replace(invalid, '_');
        }

        return fileName;
    }
}
