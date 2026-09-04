using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed class FrpManager : IDisposable
{
    private const int PreferredPort = 57400;
    private const int MaxManagementBodyBytes = 4 * 1024 * 1024;
    private const string FrpVersion = "0.71.0";
    private const string FrpPackageFileName = "frp_0.71.0_windows_amd64.zip";
    private const string FrpVersionMarkerFileName = "frp-version.txt";
    private static readonly Encoding Utf8NoBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly HttpClient FrpDownloadClient = CreateFrpDownloadClient();
    private HttpListener? _listener;
    private CancellationTokenSource? _serverCts;
    private Process? _frpcProcess;
    private int _managementPort = PreferredPort;
    private readonly string _applicationRoot;
    private readonly SemaphoreSlim _operationLock = new(1, 1);
    private readonly SemaphoreSlim _serverLock = new(1, 1);
    private readonly SemaphoreSlim _frpFilesLock = new(1, 1);
    private readonly string _managementToken = CreateManagementToken();
    private bool _disposed;

    public FrpManager()
        : this(ComponentPaths.ApplicationRoot)
    {
    }

    internal FrpManager(string applicationRoot)
    {
        if (string.IsNullOrWhiteSpace(applicationRoot))
        {
            throw new ArgumentException("应用根目录不能为空。", nameof(applicationRoot));
        }

        _applicationRoot = Path.GetFullPath(applicationRoot);
    }

    public string ManagementUrl => $"http://127.0.0.1:{_managementPort}/?token={Uri.EscapeDataString(_managementToken)}";
    public bool IsInstalled => File.Exists(FrpcPath) && File.Exists(ConfigPath);
    public bool IsRunning
    {
        get
        {
            if (_frpcProcess is { HasExited: false })
            {
                return true;
            }

            using var process = FindManagedFrpcProcess();
            return process is not null;
        }
    }

    public EnvironmentRuntimeState GetState()
    {
        if (!IsInstalled)
        {
            return new EnvironmentRuntimeState(
                false,
                false,
                "FRP 尚未安装。点击“安装”后才会联网下载客户端；启动和管理操作不会触发下载。",
                RuntimeStatusKind.NotInstalled);
        }

        var isRunning = IsRunning;
        var configChangedAfterStart = isRunning && IsConfigChangedAfterProcessStart();
        return new EnvironmentRuntimeState(
            true,
            isRunning,
            configChangedAfterStart
                ? "FRP 客户端仍在运行，但 frpc.toml 已在启动后修改；当前进程仍使用旧配置，请重启 FRP 后生效。"
                : isRunning
                    ? "FRP 客户端运行中，可打开网页管理页面查看或修改配置。"
                : "FRP 客户端已安装但未运行，可点击“启动”或打开“管理”编辑配置。",
            isRunning ? RuntimeStatusKind.Running : RuntimeStatusKind.Stopped);
    }

    public async Task InstallAsync(
        Action<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            progress?.Invoke(new InstallProgress(5, "正在准备 FRP 安装目录..."));
            await EnsureFrpFilesAsync(progress, cancellationToken);
            progress?.Invoke(new InstallProgress(100, "FRP 客户端已安装；本次安装不会自动启动。"));
        }
        finally
        {
            _operationLock.Release();
        }
    }

    public async Task EnsureManagementServerAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await EnsureManagementServerCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task EnsureManagementServerCoreAsync()
    {
        RequireInstalled();
        await _serverLock.WaitAsync();
        try
        {
            if (_listener is { IsListening: true })
            {
                return;
            }

            StopManagementServer();
            _serverCts = new CancellationTokenSource();
            _listener = new HttpListener();

            for (var port = PreferredPort; port < PreferredPort + 20; port++)
            {
                try
                {
                    _listener.Prefixes.Clear();
                    _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                    _listener.Start();
                    _managementPort = port;
                    _ = Task.Run(() => ListenLoopAsync(_serverCts.Token));
                    return;
                }
                catch (HttpListenerException)
                {
                    if (_listener.IsListening)
                    {
                        _listener.Stop();
                    }
                }
            }

            throw new InvalidOperationException("无法启动 FRP 本地网页管理端口。");
        }
        finally
        {
            _serverLock.Release();
        }
    }

    public async Task StartAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await EnsureManagementServerCoreAsync();
            await StartCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task StartCoreAsync()
    {
        RequireInstalled();

        if (IsRunning)
        {
            return;
        }

        var config = ConfigPath;
        NormalizeConfigFileEncoding(config);
        await VerifyConfigAsync(config);

        _frpcProcess = Process.Start(new ProcessStartInfo
        {
            FileName = FrpcPath,
            Arguments = $"-c \"{config}\"",
            WorkingDirectory = FrpWorkDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (_frpcProcess is null)
        {
            throw new InvalidOperationException("无法启动 frpc.exe。");
        }

        var outputTask = _frpcProcess.StandardOutput.ReadToEndAsync();
        var errorTask = _frpcProcess.StandardError.ReadToEndAsync();
        await Task.Delay(800);
        if (_frpcProcess.HasExited)
        {
            var output = NormalizeFrpcLog($"{await outputTask}\n{await errorTask}");
            var message = string.IsNullOrWhiteSpace(output)
                ? $"frpc 启动失败，退出码：{_frpcProcess.ExitCode}"
                : $"frpc 启动失败，退出码：{_frpcProcess.ExitCode}\n\n{output}";
            _frpcProcess.Dispose();
            _frpcProcess = null;
            throw new InvalidOperationException(message);
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.WhenAll(outputTask, errorTask);
            }
            catch
            {
                // Process may be killed while streams are being drained.
            }
        });
    }

    public async Task StopAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task StopCoreAsync()
    {
        if (_frpcProcess is not null)
        {
            try
            {
                if (!_frpcProcess.HasExited)
                {
                    _frpcProcess.Kill(entireProcessTree: true);
                    await ProcessLifecycle.WaitForExitAsync(
                        _frpcProcess,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(10));
                }
            }
            finally
            {
                _frpcProcess.Dispose();
                _frpcProcess = null;
            }
        }

        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                if (Path.GetFullPath(process.MainModule?.FileName ?? string.Empty).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill(entireProcessTree: true);
                    await ProcessLifecycle.WaitForExitAsync(
                        process,
                        CancellationToken.None,
                        TimeSpan.FromSeconds(10));
                }
            }
            catch
            {
                // Ignore processes we cannot inspect.
            }
            finally
            {
                process.Dispose();
            }
        }

        if (HasManagedFrpcProcess())
        {
            throw new InvalidOperationException(
                "frpc 进程仍在运行或无法确认其可终止，已取消 FRP 文件删除。请先结束占用进程后重试。");
        }

    }

    public async Task<string> UninstallAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync(cancellationToken);
        try
        {
            StopManagementServer();
            await StopCoreAsync();
            await _frpFilesLock.WaitAsync(cancellationToken);
            try
            {
                DeleteManagedDirectory(FrpWorkDirectory);
                DeleteManagedDirectory(FrpDownloadDirectory);
            }
            finally
            {
                _frpFilesLock.Release();
            }

            return "FRP 客户端、配置文件和下载缓存已卸载。";
        }
        finally
        {
            _operationLock.Release();
        }
    }

    private async Task VerifyConfigAsync(string configPath)
    {
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = FrpcPath,
            Arguments = $"verify -c \"{configPath}\"",
            WorkingDirectory = FrpWorkDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        }) ?? throw new InvalidOperationException("无法验证 frpc 配置。");

        var outputTask = process.StandardOutput.ReadToEndAsync();
        var errorTask = process.StandardError.ReadToEndAsync();
        await ProcessLifecycle.WaitForExitAsync(
            process,
            CancellationToken.None,
            TimeSpan.FromSeconds(30));

        if (process.ExitCode != 0)
        {
            var message = $"{await outputTask}\n{await errorTask}".Trim();
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "frpc 配置验证失败。" : message);
        }
    }

    private async Task ListenLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && _listener is { IsListening: true })
        {
            try
            {
                var context = await _listener.GetContextAsync();
                _ = Task.Run(() => HandleRequestAsync(context));
            }
            catch
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(200, cancellationToken);
                }
            }
        }
    }

    private async Task HandleRequestAsync(HttpListenerContext context)
    {
        try
        {
            if (!string.Equals(context.Request.QueryString["token"], _managementToken, StringComparison.Ordinal))
            {
                await WriteHtmlAsync(context, "<!doctype html><meta charset=\"utf-8\"><title>拒绝访问</title><h1>拒绝访问</h1>", 403);
                return;
            }

            var path = context.Request.Url?.AbsolutePath ?? "/";
            if (context.Request.HttpMethod.Equals("POST", StringComparison.OrdinalIgnoreCase))
            {
                if (path.Equals("/save", StringComparison.OrdinalIgnoreCase))
                {
                    var form = await ReadFormAsync(context.Request);
                    WriteNormalizedConfig(ConfigPath, form.GetValueOrDefault("toml", DefaultClientConfig()));
                    await RedirectAsync(context, BuildManagementPath("saved=1"));
                    return;
                }

                if (path.Equals("/start", StringComparison.OrdinalIgnoreCase))
                {
                    await StartAsync();
                    await RedirectAsync(context, BuildManagementPath("started=1"));
                    return;
                }

                if (path.Equals("/stop", StringComparison.OrdinalIgnoreCase))
                {
                    await StopAsync();
                    await RedirectAsync(context, BuildManagementPath("stopped=1"));
                    return;
                }
            }

            await WriteHtmlAsync(context, BuildPageHtml());
        }
        catch (Exception ex)
        {
            await WriteHtmlAsync(context, BuildPageHtml(ex.Message), 500);
        }
    }

    private string BuildPageHtml(string? error = null)
    {
        var toml = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath, Encoding.UTF8) : DefaultClientConfig();
        var model = FrpConfigModel.Parse(toml);
        var status = IsRunning ? "运行中" : "未运行";
        var statusColor = IsRunning ? "#188038" : "#d93025";
        var errorHtml = string.IsNullOrWhiteSpace(error) ? "" : $"""<div class="error">{Html(error)}</div>""";
        var token = Html(Uri.EscapeDataString(_managementToken));

        return $$$"""
            <!doctype html>
            <html lang="zh-CN">
            <head>
              <meta charset="utf-8">
              <meta name="viewport" content="width=device-width, initial-scale=1">
              <title>MCPanel FRP 管理</title>
              <style>
                body{font-family:"Segoe UI","Microsoft YaHei",sans-serif;background:#f5f7fb;color:#202124;margin:0}
                header{height:64px;background:#1a73e8;color:#fff;display:flex;align-items:center;padding:0 28px;font-size:20px;font-weight:600}
                main{max-width:1120px;margin:28px auto;padding:0 18px}
                .card{background:#fff;border:1px solid #e3e7ee;border-radius:10px;box-shadow:0 2px 14px rgba(95,99,104,.12);padding:22px;margin-bottom:18px}
                .grid{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:16px}
                label{display:block;font-size:13px;color:#5f6368;margin-bottom:7px}
                input,textarea{width:100%;box-sizing:border-box;border:1px solid #dadce0;border-radius:8px;padding:10px 12px;font:14px Consolas,"Microsoft YaHei",monospace}
                textarea{min-height:360px;line-height:1.5}
                .row{display:flex;gap:12px;align-items:center;flex-wrap:wrap}
                button,a.button{border:0;border-radius:20px;background:#1a73e8;color:#fff;padding:10px 22px;font-size:14px;cursor:pointer;text-decoration:none;display:inline-block}
                button.tonal{background:#e8f0fe;color:#174ea6}
                button.danger{background:#fce8e6;color:#a50e0e}
                .status{color:{{{statusColor}}};font-weight:600}
                .hint{color:#5f6368;line-height:1.7}
                .error{background:#fce8e6;color:#a50e0e;border-radius:8px;padding:12px;margin-bottom:16px}
                @media(max-width:800px){.grid{grid-template-columns:1fr}}
              </style>
            </head>
            <body>
              <header>MCPanel FRP 内网穿透管理</header>
              <main>
                {{{errorHtml}}}
                <section class="card">
                  <div class="row" style="justify-content:space-between">
                    <div>
                      <h2 style="margin:0 0 8px">客户端状态：<span class="status">{{{status}}}</span></h2>
                      <div class="hint">配置文件：{{{Html(ConfigPath)}}}<br>FRP 客户端：{{{Html(FrpcPath)}}}</div>
                    </div>
                    <div class="row">
                      <form method="post" action="/start?token={{{token}}}"><button type="submit">启动 FRP</button></form>
                      <form method="post" action="/stop?token={{{token}}}"><button class="danger" type="submit">停止 FRP</button></form>
                    </div>
                  </div>
                </section>

                <section class="card">
                  <h2>客户端连接信息</h2>
                  <div class="grid">
                    <div><label>服务器地址</label><input readonly value="{{{Html(model.ServerAddr)}}}"></div>
                    <div><label>服务器端口</label><input readonly value="{{{Html(model.ServerPort)}}}"></div>
                    <div><label>认证 Token</label><input readonly value="{{{Html(model.Token)}}}"></div>
                    <div><label>代理名称</label><input readonly value="{{{Html(model.ProxyName)}}}"></div>
                    <div><label>代理类型</label><input readonly value="{{{Html(model.ProxyType)}}}"></div>
                    <div><label>远程端口</label><input readonly value="{{{Html(model.RemotePort)}}}"></div>
                  </div>
                  <p class="hint">下面是完整 TOML 配置编辑器。修改服务器连接、认证、代理映射后点击保存，再重新启动 FRP 生效。</p>
                </section>

                <section class="card">
                  <form method="post" action="/save?token={{{token}}}">
                    <label>frpc.toml</label>
                    <textarea name="toml" spellcheck="false">{{{Html(toml)}}}</textarea>
                    <div class="row" style="margin-top:16px">
                      <button type="submit">保存配置</button>
                      <a class="button tonal" href="/?token={{{token}}}">重新加载</a>
                    </div>
                  </form>
                </section>
              </main>
            </body>
            </html>
            """;
    }

    private static async Task<Dictionary<string, string>> ReadFormAsync(HttpListenerRequest request)
    {
        if (request.ContentLength64 > MaxManagementBodyBytes)
        {
            throw new InvalidOperationException("FRP 管理请求体过大。");
        }

        var initialCapacity = request.ContentLength64 > 0
            ? (int)Math.Min(request.ContentLength64, MaxManagementBodyBytes)
            : 0;
        using var bodyStream = new MemoryStream(initialCapacity);
        var buffer = new byte[81920];
        while (true)
        {
            var remaining = MaxManagementBodyBytes - (int)bodyStream.Length;
            if (remaining <= 0)
            {
                var extra = await request.InputStream.ReadAsync(buffer, 0, 1);
                if (extra > 0)
                {
                    throw new InvalidOperationException("FRP 管理请求体过大。");
                }

                break;
            }

            var read = await request.InputStream.ReadAsync(buffer, 0, Math.Min(buffer.Length, remaining));
            if (read <= 0)
            {
                break;
            }

            await bodyStream.WriteAsync(buffer, 0, read);
        }

        var body = (request.ContentEncoding ?? Utf8NoBom).GetString(bodyStream.ToArray());
        return body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(part => part.Split('=', 2))
            .Where(pair => pair.Length == 2)
            .ToDictionary(pair => WebUtility.UrlDecode(pair[0]), pair => WebUtility.UrlDecode(pair[1]));
    }

    private static async Task RedirectAsync(HttpListenerContext context, string location)
    {
        context.Response.StatusCode = 302;
        context.Response.RedirectLocation = location;
        await context.Response.OutputStream.FlushAsync();
        context.Response.Close();
    }

    private static async Task WriteHtmlAsync(HttpListenerContext context, string html, int statusCode = 200)
    {
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = statusCode;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private async Task EnsureFrpFilesAsync(Action<InstallProgress>? progress, CancellationToken cancellationToken)
    {
        await _frpFilesLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(FrpWorkDirectory);

            if (!IsCurrentFrpVersion())
            {
                progress?.Invoke(new InstallProgress(
                    15,
                    $"正在联网下载 FRP {FrpVersion}...",
                    InstallProgressStage.Downloading,
                    0));
                await DownloadAndInstallFrpAsync(progress, cancellationToken);

                if (File.Exists(FrpcPath))
                {
                    AtomicFile.WriteAllText(FrpVersionMarkerPath, FrpVersion, Encoding.UTF8);
                }
            }

            if (!File.Exists(FrpcPath))
            {
                throw new FileNotFoundException("FRP 客户端下载完成后仍未找到 frpc.exe。", FrpcPath);
            }

            if (!File.Exists(ConfigPath))
            {
                WriteNormalizedConfig(ConfigPath, DefaultClientConfig());
            }

            progress?.Invoke(new InstallProgress(
                90,
                "正在写入 FRP 默认配置...",
                InstallProgressStage.Installing,
                80));
            NormalizeConfigFileEncoding(ConfigPath);
            EnsureClientConfigOptions();
        }
        finally
        {
            _frpFilesLock.Release();
        }
    }

    private async Task DownloadAndInstallFrpAsync(
        Action<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        var archivePath = FrpArchivePath;
        var downloadUrl = EnvironmentDownloadSettings.Load().FrpPackageUrl;
        Directory.CreateDirectory(FrpDownloadDirectory);

        try
        {
            await EnsureFrpArchiveAsync(archivePath, downloadUrl, progress, cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException || ex is TaskCanceledException || ex is InvalidDataException)
        {
            throw new InvalidOperationException(
                $"未找到本地 FRP 客户端，且无法下载 FRP {FrpVersion}。请检查网络后重试。\n下载地址：{downloadUrl}",
                ex);
        }

        using var archive = ZipFile.OpenRead(archivePath);
        progress?.Invoke(new InstallProgress(
            75,
            "FRP 下载完成，正在校验并解压客户端...",
            InstallProgressStage.Installing,
            10));
        var executable = FindArchiveEntry(archive, "frpc.exe");
        var config = FindArchiveEntry(archive, "frpc.toml");
        var license = FindArchiveEntry(archive, "LICENSE");
        if (executable is null || config is null)
        {
            throw new InvalidDataException($"FRP {FrpVersion} 压缩包中缺少 frpc.exe 或 frpc.toml。\n下载地址：{downloadUrl}");
        }

        var stagedExecutable = Path.Combine(FrpWorkDirectory, "frpc.exe.download");
        var stagedLicense = Path.Combine(FrpWorkDirectory, "LICENSE.download");
        try
        {
            await ExtractArchiveEntryAsync(executable, stagedExecutable, cancellationToken);
            InstallStagedFile(stagedExecutable, FrpcPath);

            if (license is not null && !File.Exists(Path.Combine(FrpWorkDirectory, "LICENSE")))
            {
                await ExtractArchiveEntryAsync(license, stagedLicense, cancellationToken);
                InstallStagedFile(stagedLicense, Path.Combine(FrpWorkDirectory, "LICENSE"));
            }

            if (!File.Exists(ConfigPath))
            {
                await ExtractArchiveEntryAsync(config, ConfigPath, cancellationToken);
                NormalizeConfigFileEncoding(ConfigPath);
            }
        }
        finally
        {
            DeleteIfExists(stagedExecutable);
            DeleteIfExists(stagedLicense);
        }
    }

    private static async Task EnsureFrpArchiveAsync(
        string archivePath,
        string downloadUrl,
        Action<InstallProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (IsUsableFrpArchive(archivePath))
        {
            progress?.Invoke(new InstallProgress(
                75,
                "已找到 FRP 缓存安装包，准备校验并解压...",
                InstallProgressStage.Downloading,
                100));
            return;
        }

        var partialPath = archivePath + ".part";
        DeleteIfExists(partialPath);

        using var response = await FrpDownloadClient.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        await DownloadService.SaveResponseAsync(
            response,
            archivePath,
            snapshot =>
            {
                var stagePercent = snapshot.TotalBytes is > 0
                    ? Compat.Clamp(snapshot.BytesReceived * 100d / snapshot.TotalBytes.Value, 0, 100)
                    : (double?)null;
                var overallPercent = snapshot.TotalBytes is > 0
                    ? 15 + 60 * snapshot.BytesReceived / snapshot.TotalBytes.Value
                    : 15;
                var downloadedText = snapshot.TotalBytes is > 0
                    ? $"{ProductTransferFormatting.FormatBytes(snapshot.BytesReceived)} / {ProductTransferFormatting.FormatBytes(snapshot.TotalBytes.Value)}"
                    : ProductTransferFormatting.FormatBytes(snapshot.BytesReceived);
                progress?.Invoke(new InstallProgress(
                    Compat.Clamp(overallPercent, 15, 75),
                    $"正在下载 FRP：{downloadedText}",
                    InstallProgressStage.Downloading,
                    stagePercent,
                    ProductTransferFormatting.FormatRate(snapshot.BytesPerSecond)));
            },
            cancellationToken: cancellationToken,
            temporarySuffix: ".part",
            bufferSize: 81920,
            validatePartial: path =>
            {
                if (!IsUsableFrpArchive(path))
                {
                    throw new InvalidDataException("下载的 FRP 压缩包无法读取。");
                }
            },
            emptyFileMessage: "下载的 FRP 压缩包为空。",
            incompleteFileMessage: (expected, actual) =>
                $"下载的 FRP 压缩包不完整：应为 {expected} 字节，实际 {actual} 字节。");
    }

    private static bool IsUsableFrpArchive(string path)
    {
        if (!File.Exists(path) || new FileInfo(path).Length == 0)
        {
            return false;
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            return FindArchiveEntry(archive, "frpc.exe") is not null && FindArchiveEntry(archive, "frpc.toml") is not null;
        }
        catch (InvalidDataException)
        {
            return false;
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static ZipArchiveEntry? FindArchiveEntry(ZipArchive archive, string fileName)
    {
        var expectedPath = $"frp_{FrpVersion}_windows_amd64/{fileName}";
        return archive.Entries.FirstOrDefault(entry =>
                   string.Equals(entry.FullName.Replace('\\', '/'), expectedPath, StringComparison.OrdinalIgnoreCase))
               ?? archive.Entries.FirstOrDefault(entry =>
                   !entry.FullName.EndsWith("/", StringComparison.Ordinal) &&
                   string.Equals(Path.GetFileName(entry.FullName.Replace('/', Path.DirectorySeparatorChar)), fileName, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task ExtractArchiveEntryAsync(
        ZipArchiveEntry entry,
        string destination,
        CancellationToken cancellationToken)
    {
        using var input = entry.Open();
        using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await input.CopyToAsync(output, 81920, cancellationToken);
        await output.FlushAsync(cancellationToken);
    }

    private static void InstallStagedFile(string stagedPath, string targetPath)
    {
        if (!File.Exists(targetPath))
        {
            File.Move(stagedPath, targetPath);
            return;
        }

        try
        {
            File.Replace(stagedPath, targetPath, destinationBackupFileName: null);
        }
        catch (PlatformNotSupportedException)
        {
            File.Copy(stagedPath, targetPath, overwrite: true);
            DeleteIfExists(stagedPath);
        }
    }

    private static void DeleteIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static HttpClient CreateFrpDownloadClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("MCPanel/1.0");
        return client;
    }

    private void EnsureClientConfigOptions()
    {
        var toml = File.Exists(ConfigPath)
            ? NormalizeTomlForProcess(File.ReadAllText(ConfigPath, Utf8NoBom))
            : DefaultClientConfig();
        if (Regex.IsMatch(toml, @"(?m)^\s*loginFailExit\s*="))
        {
            WriteNormalizedConfig(ConfigPath, toml);
            return;
        }

        var updated = new Regex(@"(?m)^(\s*serverPort\s*=\s*[^\r\n]+)")
            .Replace(toml, "$1\r\nloginFailExit = false", 1);

        if (updated == toml)
        {
            updated = "loginFailExit = false\r\n" + toml;
        }

        WriteNormalizedConfig(ConfigPath, updated);
    }

    internal static string NormalizeTomlForProcess(string toml) =>
        toml.TrimStart('\uFEFF');

    private static void NormalizeConfigFileEncoding(string path)
    {
        if (File.Exists(path))
        {
            WriteNormalizedConfig(path, File.ReadAllText(path, Utf8NoBom));
        }
    }

    private static void WriteNormalizedConfig(string path, string toml) =>
        AtomicFile.WriteAllText(path, NormalizeTomlForProcess(toml), Utf8NoBom);

    private Process? FindManagedFrpcProcess()
    {
        Process? match = null;
        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                if (match is null &&
                    Path.GetFullPath(process.MainModule?.FileName ?? string.Empty).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    match = process;
                    continue;
                }
            }
            catch
            {
            }

            process.Dispose();
        }

        return match;
    }

    private bool HasManagedFrpcProcess()
    {
        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(executable) ||
                    Path.GetFullPath(executable).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    // An inaccessible executable path is treated as active rather
                    // than allowing deletion while a possible FRP process owns it.
                    return true;
                }
            }
            catch
            {
                return true;
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }

    private bool IsConfigChangedAfterProcessStart()
    {
        if (!File.Exists(ConfigPath))
        {
            return false;
        }

        Process? process = null;
        var isOwnedProcess = false;
        try
        {
            if (_frpcProcess is { HasExited: false })
            {
                process = _frpcProcess;
                isOwnedProcess = true;
            }
            else
            {
                process = FindManagedFrpcProcess();
            }

            if (process is null)
            {
                return false;
            }

            return File.GetLastWriteTimeUtc(ConfigPath) > process.StartTime.ToUniversalTime().AddSeconds(1);
        }
        catch
        {
            return false;
        }
        finally
        {
            if (!isOwnedProcess)
            {
                process?.Dispose();
            }
        }
    }

    private string FrpWorkDirectory => Path.Combine(_applicationRoot, "Frp");
    private string FrpcPath => Path.Combine(FrpWorkDirectory, "frpc.exe");
    private string ConfigPath => Path.Combine(FrpWorkDirectory, "frpc.toml");
    private string FrpVersionMarkerPath => Path.Combine(FrpWorkDirectory, FrpVersionMarkerFileName);
    private string FrpDownloadDirectory => Path.Combine(_applicationRoot, "Downloads", "Frp");
    private string FrpArchivePath => Path.Combine(FrpDownloadDirectory, FrpPackageFileName);

    private void RequireInstalled()
    {
        if (!IsInstalled)
        {
            throw new InvalidOperationException(
                "FRP 尚未安装。请先在“环境管理”中点击 FRP 的“安装”按钮；启动和管理操作不会自动下载客户端。");
        }
    }

    private void DeleteManagedDirectory(string directory)
    {
        var root = Path.GetFullPath(_applicationRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var target = Path.GetFullPath(directory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (target.Length == 0 ||
            string.Equals(target, root.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            !target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"FRP 卸载目录不在 MCPanel 应用目录内，已取消删除：{target}");
        }

        if (!Directory.Exists(target))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                foreach (var file in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(target, recursive: true);
                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(250);
            }
        }

        if (Directory.Exists(target))
        {
            Directory.Delete(target, recursive: true);
        }
    }

    private bool IsCurrentFrpVersion()
    {
        if (!File.Exists(FrpcPath) || !File.Exists(FrpVersionMarkerPath))
        {
            return false;
        }

        try
        {
            return string.Equals(File.ReadAllText(FrpVersionMarkerPath, Encoding.UTF8).Trim(), FrpVersion, StringComparison.Ordinal);
        }
        catch (IOException)
        {
            return false;
        }
    }

    private static string Html(string? value) => WebUtility.HtmlEncode(value ?? string.Empty);

    private static string NormalizeFrpcLog(string value)
    {
        var withoutAnsi = Regex.Replace(value, @"\x1B\[[0-9;]*[A-Za-z]", string.Empty);
        return withoutAnsi.Trim();
    }

    private string BuildManagementPath(string status) =>
        $"/?token={Uri.EscapeDataString(_managementToken)}&{status}";

    private static string CreateManagementToken()
    {
        var bytes = new byte[32];
        using var random = RandomNumberGenerator.Create();
        random.GetBytes(bytes);
        return Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    private void StopManagementServer()
    {
        try
        {
            _serverCts?.Cancel();
            _listener?.Stop();
            _listener?.Close();
        }
        catch
        {
        }
        finally
        {
            _listener = null;
            _serverCts?.Dispose();
            _serverCts = null;
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(FrpManager));
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        StopManagementServer();
        StopManagedProcessesForDispose();
        // Do not dispose the gates here: a close can race an in-flight async
        // operation whose finally block still needs to release its semaphore.
        // The manager is permanently marked disposed, so these gates are no
        // longer reachable by new operations and can be reclaimed with the
        // manager itself.
    }

    private void StopManagedProcessesForDispose()
    {
        if (_frpcProcess is not null)
        {
            try
            {
                if (!_frpcProcess.HasExited)
                {
                    ProcessLifecycle.TryKill(_frpcProcess);
                    _frpcProcess.WaitForExit(5000);
                }
            }
            catch
            {
                // Disposal must remain best-effort; the normal Stop action still
                // reports operational failures to the user.
            }
            finally
            {
                _frpcProcess.Dispose();
                _frpcProcess = null;
            }
        }

        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable) &&
                    Path.GetFullPath(executable).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    ProcessLifecycle.TryKill(process);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // A protected process may deny path inspection or termination.
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static string DefaultClientConfig() =>
        """
        serverAddr = "127.0.0.1"
        serverPort = 7000
        loginFailExit = false

        [[proxies]]
        name = "test-tcp"
        type = "tcp"
        localIP = "127.0.0.1"
        localPort = 22
        remotePort = 6000
        """;

    private sealed record FrpConfigModel(string ServerAddr, string ServerPort, string Token, string ProxyName, string ProxyType, string RemotePort)
    {
        public static FrpConfigModel Parse(string toml)
        {
            return new FrpConfigModel(
                Pick(toml, "serverAddr"),
                Pick(toml, "serverPort"),
                Pick(toml, "token"),
                Pick(toml, "name"),
                Pick(toml, "type"),
                Pick(toml, "remotePort"));
        }

        private static string Pick(string toml, string key)
        {
            var match = Regex.Match(toml, $@"(?m)^\s*{Regex.Escape(key)}\s*=\s*""?([^""\r\n#]+)""?");
            return match.Success ? match.Groups[1].Value.Trim() : "-";
        }
    }
}
