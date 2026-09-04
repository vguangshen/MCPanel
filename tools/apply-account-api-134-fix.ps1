$ErrorActionPreference = 'Stop'

function Read-Normalized([string]$Path) {
    return ([IO.File]::ReadAllText($Path)).Replace("`r`n", "`n")
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText($Path, $Text, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Text, [string]$Old, [string]$New, [string]$Label) {
    if (-not $Text.Contains($Old)) {
        throw "Patch anchor not found: $Label"
    }
    return $Text.Replace($Old, $New)
}

function Replace-RegexOnce([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Label) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one match for $Label, found $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement, 1)
}

# 1. Serialize Account API lifecycle operations across every manager instance,
#    trust the actual HttpListener bind result instead of a pre-start HTTP probe,
#    and do not wait on database-heavy /health before considering the listener started.
$managerPath = 'AccountApiManagerService.cs'
$manager = Read-Normalized $managerPath
$manager = Replace-Exact $manager `
    '    private readonly SemaphoreSlim _operationGate = new(1, 1);' `
    '    private static readonly SemaphoreSlim LifecycleGate = new(1, 1);' `
    'manager lifecycle gate'

$newStart = @'
    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            // Multiple AccountApiManagerService instances can exist (tray startup
            // and the management page). Re-read the persisted switch while holding
            // the process-wide lifecycle gate so a stale manager cannot restart a
            // listener after another manager has just disabled it.
            _enabled = LoadSettings().Enabled;
            if (!IsEnabled)
            {
                throw new InvalidOperationException("账号 API 当前未启用，请先打开“启用账号 API”开关。");
            }

            var initialConfiguration = LoadConfiguration();
            if (!initialConfiguration.ConfigExists)
            {
                EnsureDefaultConfiguration();
                initialConfiguration = LoadConfiguration();
            }

            if (!initialConfiguration.HasSigningSecret)
            {
                GenerateSigningSecret(initialConfiguration);
            }

            var configuration = RequireRunnableConfiguration();
            if (EmbeddedAccountApiRuntime.IsRunning)
            {
                return;
            }

            try
            {
                // Do not probe /health before binding. A just-stopped MCPanel
                // listener or an unrelated HTTP service can still answer briefly
                // and must not be misreported as a third-party port conflict.
                // HttpListener.Start is the authoritative bind check.
                EmbeddedAccountApiRuntime.Start(configuration.ConfigPath);
            }
            catch (HttpListenerException error)
            {
                throw new InvalidOperationException(
                    "内置 Account API 无法监听 " + configuration.Endpoint +
                    "。端口或 HTTP.sys 监听地址可能正在被其他程序使用，或者当前用户没有 HTTP 监听权限。",
                    error);
            }

            if (!EmbeddedAccountApiRuntime.IsRunning)
            {
                throw new InvalidOperationException(
                    "内置 Account API 启动后未进入监听状态。请查看 Account API 日志后重试。" +
                    Environment.NewLine + ReadLatestAccountApiError(configuration));
            }
        }
        finally
        {
            LifecycleGate.Release();
        }
    }
'@
$manager = Replace-RegexOnce $manager `
    '(?s)    public async Task StartAsync\(CancellationToken cancellationToken = default\)\n    \{.*?\n    \}\n\n(?=    public async Task StopAsync)' `
    ($newStart.TrimEnd() + "`n`n") `
    'StartAsync'

$newStop = @'
    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            // EmbeddedAccountApiRuntime.Stop does not return until HttpListener
            // has been stopped/closed and the accept loop has been given a chance
            // to exit. No arbitrary delay is required before a later restart.
            EmbeddedAccountApiRuntime.Stop();
        }
        finally
        {
            LifecycleGate.Release();
        }
    }
'@
$manager = Replace-RegexOnce $manager `
    '(?s)    public async Task StopAsync\(CancellationToken cancellationToken = default\)\n    \{.*?\n    \}\n\n(?=    public async Task RestartAsync)' `
    ($newStop.TrimEnd() + "`n`n") `
    'StopAsync'

$newSetEnabled = @'
    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await LifecycleGate.WaitAsync(cancellationToken);
        try
        {
            var previous = _enabled;
            try
            {
                _enabled = enabled;
                SaveSettings();
                if (!enabled)
                {
                    // Hold the same process-wide lifecycle gate used by StartAsync.
                    // When this method returns, the old listener is fully stopped,
                    // so an immediate enable/start cannot race its own shutdown.
                    EmbeddedAccountApiRuntime.Stop();
                }
            }
            catch
            {
                _enabled = previous;
                throw;
            }
        }
        finally
        {
            LifecycleGate.Release();
        }
    }
'@
$manager = Replace-RegexOnce $manager `
    '(?s)    public async Task SetEnabledAsync\(bool enabled, CancellationToken cancellationToken = default\)\n    \{.*?\n    \}\n\n(?=    public string GenerateSigningSecret)' `
    ($newSetEnabled.TrimEnd() + "`n`n") `
    'SetEnabledAsync'
$manager = Replace-Exact $manager "        _operationGate.Dispose();`n" '' 'dispose instance lifecycle gate'

# An unrelated HTTP response must never be treated as a healthy Account API.
$oldParsePreamble = @'
            var status = ReadString(root, "status");
            var version = ReadString(root, "version");
            var deviceId = ReadString(root, "deviceId");
            var effectiveBindAddress = ReadString(root, "bindAddress");
            var ok = root.TryGetProperty("ok", out var okElement) &&
                     okElement.ValueKind == JsonValueKind.True;
'@
$newParsePreamble = @'
            var status = ReadString(root, "status");
            var service = ReadString(root, "service");
            var version = ReadString(root, "version");
            var deviceId = ReadString(root, "deviceId");
            var effectiveBindAddress = ReadString(root, "bindAddress");
            if (!string.Equals(service, "MarchCenter Account API", StringComparison.OrdinalIgnoreCase))
            {
                return AccountApiHealthProbe.Unreachable(
                    "监听地址返回了其他 HTTP 服务的响应，并非 MCPanel Account API。");
            }

            var ok = root.TryGetProperty("ok", out var okElement) &&
                     okElement.ValueKind == JsonValueKind.True;
'@
$manager = Replace-Exact $manager $oldParsePreamble $newParsePreamble 'health service identity'
$oldJsonCatch = @'
        catch (JsonException)
        {
            return new AccountApiHealthProbe
            {
                Reachable = true,
                Healthy = statusCode is >= 200 and < 300,
                HttpStatusCode = statusCode,
                Status = "响应格式异常",
                Error = "内置 Account API 返回的健康检查不是有效 JSON。"
            };
        }
'@
$newJsonCatch = @'
        catch (JsonException)
        {
            return AccountApiHealthProbe.Unreachable(
                "监听地址返回的内容不是 MCPanel Account API 健康检查响应。");
        }
'@
$manager = Replace-Exact $manager $oldJsonCatch $newJsonCatch 'invalid health response handling'
Write-Utf8 $managerPath $manager

# 2. Make the process-wide runtime own the whole stop/dispose boundary and use
#    the listener's actual state instead of merely checking whether _host is non-null.
$runtimePath = 'EmbeddedAccountApiRuntime.cs'
$runtime = Read-Normalized $runtimePath
$runtime = Replace-Exact $runtime `
    '                return _host is not null;' `
    '                return _host is not null && _host.IsRunning;' `
    'runtime IsRunning'
$oldExistingHost = @'
            if (_host is not null)
            {
                return;
            }
'@
$newExistingHost = @'
            if (_host is not null)
            {
                if (_host.IsRunning)
                {
                    return;
                }

                // A faulted accept loop must not leave a stale host blocking
                // recovery. Dispose it while still holding the global gate.
                try
                {
                    _host.Dispose();
                }
                finally
                {
                    _host = null;
                }
            }
'@
$runtime = Replace-Exact $runtime $oldExistingHost $newExistingHost 'stale host cleanup'
$oldRuntimeStop = @'
    public static void Stop()
    {
        AccountApiHost? host;
        lock (Gate)
        {
            host = _host;
            _host = null;
        }

        host?.Dispose();
    }
'@
$newRuntimeStop = @'
    public static void Stop()
    {
        lock (Gate)
        {
            if (_host is null)
            {
                return;
            }

            // Keep the global gate until HttpListener.Stop/Close and the accept
            // loop shutdown are complete. Start() therefore cannot observe a
            // false "not running" state while the old listener still owns 8088.
            try
            {
                _host.Dispose();
            }
            finally
            {
                _host = null;
            }
        }
    }
'@
$runtime = Replace-Exact $runtime $oldRuntimeStop $newRuntimeStop 'runtime Stop'
Write-Utf8 $runtimePath $runtime

# 3. Expose the real HttpListener/accept-loop state and make host disposal idempotent.
$hostPath = 'EmbeddedAccountApi/AccountApiHost.cs'
$host = Read-Normalized $hostPath
$host = Replace-Exact $host `
    '        private Task _loop;' `
    "        private Task _loop;`n        private int _disposed;" `
    'host disposed field'
$host = Replace-Exact $host `
    '        public string EffectiveBindAddress { get { return _effectiveBindAddress; } }' `
    @'
        public string EffectiveBindAddress { get { return _effectiveBindAddress; } }

        public bool IsRunning
        {
            get
            {
                if (Volatile.Read(ref _disposed) != 0) return false;
                try
                {
                    return _listener != null && _listener.IsListening && _loop != null && !_loop.IsCompleted;
                }
                catch (ObjectDisposedException)
                {
                    return false;
                }
            }
        }
'@.TrimEnd() `
    'host IsRunning'
$oldHostDispose = @'
        public void Dispose()
        {
            _stop.Cancel(); try { _listener.Stop(); } catch { } try { _listener.Close(); } catch { } try { if (_loop != null) _loop.Wait(2000); } catch { } _stop.Dispose();
        }
'@
$newHostDispose = @'
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _stop.Cancel();
            try { _listener.Stop(); } catch { }
            try { _listener.Close(); } catch { }
            try { if (_loop != null) _loop.Wait(2000); } catch { }
            _stop.Dispose();
        }
'@
$host = Replace-Exact $host $oldHostDispose $newHostDispose 'host Dispose'
Write-Utf8 $hostPath $host

# 4. Version bump.
$projectPath = 'MCPanel.csproj'
$project = Read-Normalized $projectPath
$project = Replace-Exact $project '<Version>1.3.3</Version>' '<Version>1.3.4</Version>' 'Version'
$project = Replace-Exact $project '<FileVersion>1.3.3.0</FileVersion>' '<FileVersion>1.3.4.0</FileVersion>' 'FileVersion'
$project = Replace-Exact $project '<AssemblyVersion>1.3.3.0</AssemblyVersion>' '<AssemblyVersion>1.3.4.0</AssemblyVersion>' 'AssemblyVersion'
Write-Utf8 $projectPath $project

# 5. Add focused lifecycle regression coverage. Use a temporary TCP port and
#    the real embedded runtime so Start -> Stop -> Start exercises HTTP.sys.
$testPath = 'MCPanel.Tests/ReliabilityTests.AccountApiLifecycle.cs'
$test = @'
using System.Net;
using System.Net.Sockets;
using System.Text;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void AccountApiHostReportsRealListenerLifecycle()
    {
        var port = ReserveFreeTcpPort();
        var options = CreateAccountApiTestOptions(port);
        var host = new AccountApiHost(options);
        try
        {
            host.Start();
            Assert.IsTrue(host.IsRunning, "HttpListener 启动后应报告真实监听状态。");
        }
        finally
        {
            host.Dispose();
        }

        Assert.IsFalse(host.IsRunning, "Dispose 返回后 Account API 不得继续报告运行中。");
    }

    [TestMethod]
    public void EmbeddedAccountApiRuntimeCanStopAndImmediatelyRestartSamePort()
    {
        var root = CreateTemporaryDirectory();
        var configPath = Path.Combine(root, "config.ini");
        var port = ReserveFreeTcpPort();
        File.WriteAllText(configPath, BuildAccountApiTestConfig(port), new UTF8Encoding(false));

        EmbeddedAccountApiRuntime.Stop();
        try
        {
            EmbeddedAccountApiRuntime.Start(configPath);
            Assert.IsTrue(EmbeddedAccountApiRuntime.IsRunning);

            EmbeddedAccountApiRuntime.Stop();
            Assert.IsFalse(EmbeddedAccountApiRuntime.IsRunning,
                "Stop 返回时必须已经释放旧 HttpListener，而不是先把 _host 置空。 ");

            EmbeddedAccountApiRuntime.Start(configPath);
            Assert.IsTrue(EmbeddedAccountApiRuntime.IsRunning,
                "关闭后应能立即重新监听同一个端口，不应误报为其他程序占用。");
        }
        finally
        {
            EmbeddedAccountApiRuntime.Stop();
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void AccountApiHostFailsOnlyWhenThePortIsActuallyBound()
    {
        using var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var host = new AccountApiHost(CreateAccountApiTestOptions(port));
        try
        {
            Assert.ThrowsException<HttpListenerException>(() => host.Start(),
                "真实端口冲突应由 HttpListener.Start 返回，而不是由预先 /health 探测猜测。");
        }
        finally
        {
            host.Dispose();
        }
    }

    private static int ReserveFreeTcpPort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static AccountApiOptions CreateAccountApiTestOptions(int port)
    {
        return new AccountApiOptions
        {
            Server = new ServerOptions
            {
                BindAddress = "127.0.0.1",
                Port = port,
                RequestBodyLimitBytes = 65536,
                RequestsPerMinute = 120
            },
            Authentication = new AuthenticationOptions
            {
                Mode = "Hmac",
                SigningSecret = new string('A', 48),
                ClockSkewSeconds = 300
            }
        };
    }

    private static string BuildAccountApiTestConfig(int port)
    {
        return
            "[AccountApi:Server]\r\n" +
            "BindAddress=127.0.0.1\r\n" +
            "Port=" + port + "\r\n" +
            "RequestBodyLimitBytes=65536\r\n" +
            "RequestsPerMinute=120\r\n\r\n" +
            "[AccountApi:Authentication]\r\n" +
            "Mode=Hmac\r\n" +
            "SigningSecret=" + new string('A', 48) + "\r\n" +
            "ClockSkewSeconds=300\r\n\r\n" +
            "[AccountApi]\r\n" +
            "AuditLogDirectory=logs\r\n";
    }
}
'@
Write-Utf8 $testPath ($test.TrimStart("`r", "`n").Replace("`r`n", "`n"))

Write-Host 'Account API 1.3.4 lifecycle patch applied.'
