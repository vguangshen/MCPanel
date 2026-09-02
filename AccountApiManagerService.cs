using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using MarchCenter.AccountApi;

namespace MCPanel;

/// <summary>
/// Controls the Account API listener embedded in the MCPanel process.
/// The original HTTP routes, HMAC protocol and database methods are compiled
/// into MCPanel; no helper executable or Windows service is required.
/// </summary>
public sealed class AccountApiManagerService : IDisposable
{
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly CancellationTokenSource _disposeCancellation = new();
    private readonly string _settingsPath;
    private string _configuredRuntimeDirectory;
    private bool _enabled;
    private string _initializationError = string.Empty;
    private bool _disposed;

    public AccountApiManagerService()
    {
        _settingsPath = AccountApiStorage.SettingsPath;
        _configuredRuntimeDirectory = AccountApiStorage.RootPath;

        AccountApiManagerSettings startupSettings;
        try
        {
            startupSettings = LoadSettings();
            _enabled = startupSettings.Enabled;
        }
        catch (Exception error)
        {
            startupSettings = new AccountApiManagerSettings();
            _initializationError = "账号 API 设置读取失败：" + error.Message;
        }

        var legacyDirectory = startupSettings.RuntimeDirectory;
        try
        {
            AccountApiStorage.Initialize(string.IsNullOrWhiteSpace(legacyDirectory)
                ? Array.Empty<string>()
                : new[] { legacyDirectory });
            EnsureDefaultConfiguration();

            // Persist the migrated enable flag and runtime location beside
            // the new AccountApi directory.  The old StoreData settings file
            // is removed only after the new file has been written.
            if (File.Exists(_settingsPath) || File.Exists(AccountApiStorage.LegacySettingsPath))
            {
                SaveSettings();
                TryDeleteLegacySettings();
            }
        }
        catch (Exception error)
        {
            // A read-only or locked data directory should not terminate the
            // entire WPF process while the page is being constructed.
            _initializationError = "账号 API 数据目录初始化失败：" + error.Message;
        }

    }

    public string ConfiguredRuntimeDirectory => _configuredRuntimeDirectory;
    public bool IsEnabled => _enabled;

    public string? DiscoverRuntimeDirectory() =>
        Directory.Exists(AccountApiStorage.RootPath) ? AccountApiStorage.RootPath : null;

    /// <summary>
    /// Imports a legacy directory or a standalone config.ini into the
    /// MCPanel-owned Account API data directory. The selected directory is
    /// never used as a runtime dependency after import.
    /// </summary>
    public void SetRuntimeDirectory(string directory)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ArgumentException("请选择包含 config.ini 的目录或配置文件。", nameof(directory));
        }

        var selected = Path.GetFullPath(directory.Trim());
        var source = File.Exists(selected) ? selected : Path.Combine(selected, "config.ini");
        if (!File.Exists(source))
        {
            throw new FileNotFoundException("选中的位置中没有找到 config.ini。", source);
        }

        var content = File.ReadAllText(source, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidDataException("config.ini 为空，无法导入。");
        }

        // Parse the syntax without requiring a secret so a first-run config
        // can still be imported and then receive a generated HMAC key.
        ReadIniValues(AccountApiStorage.ConfigPath, content);
        AtomicFile.WriteAllText(AccountApiStorage.ConfigPath, content);
        _configuredRuntimeDirectory = AccountApiStorage.RootPath;
        SaveSettings();
    }

    public async Task<AccountApiSnapshot> RefreshAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            _disposeCancellation.Token);
        var operationToken = linkedCancellation.Token;

        var configuration = LoadConfiguration();
        string? configurationError = null;

        if (IsEnabled && configuration.ConfigExists && !configuration.HasSigningSecret)
        {
            try
            {
                GenerateSigningSecret(configuration);
                configuration = LoadConfiguration();
            }
            catch (Exception error)
            {
                configurationError = "HMAC 密钥尚未生成：" + error.Message;
            }
        }

        var healthTask = IsEnabled
            ? ProbeAsync(configuration, operationToken)
            : Task.FromResult(AccountApiHealthProbe.Disabled());
        var runningTask = Task.Run(() => EmbeddedAccountApiRuntime.IsRunning, operationToken);
        await Task.WhenAll(healthTask, runningTask);

        var health = healthTask.Result;
        return new AccountApiSnapshot
        {
            Configuration = configuration,
            Reachable = health.Reachable,
            Healthy = health.Healthy,
            HttpStatusCode = health.HttpStatusCode,
            HealthStatus = health.Status,
            Version = string.IsNullOrWhiteSpace(health.Version) ? "2.0.2（内置）" : health.Version + "（内置）",
            EffectiveBindAddress = health.EffectiveBindAddress,
            DeviceId = health.DeviceId,
            Providers = health.Providers,
            ServiceInstalled = false,
            WindowsServiceStatus = "MCPanel 内置",
            WindowsServiceError = string.Empty,
            LocalProcessRunning = runningTask.Result,
            LogsText = ReadRecentLogs(configuration),
            Enabled = IsEnabled,
            ErrorText = JoinMessages(_initializationError, configurationError, health.Error),
            CheckedAt = DateTimeOffset.Now
        };
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
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

            var current = await ProbeAsync(configuration, cancellationToken);
            if (current.Reachable)
            {
                throw new InvalidOperationException(
                    "Account API 端口已经被其他程序占用。请停止旧的 Account API 进程后，再启动 MCPanel 内置服务。 ");
            }

            try
            {
                EmbeddedAccountApiRuntime.Start(configuration.ConfigPath);
            }
            catch (HttpListenerException error)
            {
                throw new InvalidOperationException(
                    "内置 Account API 无法监听 " + configuration.Endpoint + "。请检查端口是否被占用，以及当前用户是否有 HTTP 监听权限。",
                    error);
            }

            if (!await WaitForReachableAsync(configuration, TimeSpan.FromSeconds(12), cancellationToken))
            {
                throw new InvalidOperationException(
                    "内置 Account API 未在 12 秒内开始监听。请检查端口、HMAC 配置以及日志。" +
                    Environment.NewLine + ReadLatestAccountApiError(configuration));
            }
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            EmbeddedAccountApiRuntime.Stop();
            await Task.Delay(100, cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task RestartAsync(CancellationToken cancellationToken = default)
    {
        await StopAsync(cancellationToken);
        await StartAsync(cancellationToken);
    }

    public async Task SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            var previous = _enabled;
            try
            {
                _enabled = enabled;
                SaveSettings();
                if (!enabled)
                {
                    EmbeddedAccountApiRuntime.Stop();
                    await Task.Delay(100, cancellationToken);
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
            _operationGate.Release();
        }
    }

    public string GenerateSigningSecret()
    {
        ThrowIfDisposed();
        return GenerateSigningSecret(RequireConfiguration());
    }

    public bool IsLocalProcessRunning()
    {
        ThrowIfDisposed();
        return EmbeddedAccountApiRuntime.IsRunning;
    }

    public void OpenConfigurationFile()
    {
        var configuration = RequireConfiguration();
        Process.Start(new ProcessStartInfo
        {
            FileName = "notepad.exe",
            Arguments = QuoteArgument(configuration.ConfigPath),
            UseShellExecute = true
        });
    }

    public void OpenRuntimeDirectory()
    {
        Directory.CreateDirectory(AccountApiStorage.RootPath);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(AccountApiStorage.RootPath),
            UseShellExecute = true
        });
    }

    public void OpenLogDirectory()
    {
        var configuration = RequireConfiguration();
        Directory.CreateDirectory(configuration.LogDirectory);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = QuoteArgument(configuration.LogDirectory),
            UseShellExecute = true
        });
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _disposeCancellation.Cancel();
        _http.Dispose();
        _disposeCancellation.Dispose();
        _operationGate.Dispose();
    }

    /// <summary>
    /// Called once by App.OnExit. A page is intentionally allowed to dispose
    /// its manager while the app remains in the tray, but the listener must
    /// stop when MCPanel itself exits.
    /// </summary>
    public static void StopEmbeddedRuntime() => EmbeddedAccountApiRuntime.Stop();

    private AccountApiConfiguration RequireConfiguration()
    {
        var configuration = LoadConfiguration();
        if (!configuration.ConfigExists)
        {
            throw new FileNotFoundException("Account API 配置文件不存在。", configuration.ConfigPath);
        }

        return configuration;
    }

    private AccountApiConfiguration RequireRunnableConfiguration()
    {
        var configuration = RequireConfiguration();
        if (!configuration.HasSigningSecret)
        {
            throw new InvalidDataException("HMAC 签名密钥尚未生成，请先生成密钥。");
        }

        return configuration;
    }

    private string GenerateSigningSecret(AccountApiConfiguration configuration)
    {
        if (!configuration.ConfigExists)
        {
            EnsureDefaultConfiguration();
        }

        var bytes = new byte[48];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(bytes);
        }

        var value = Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
        var backupPath = configuration.ConfigPath + ".previous";
        try
        {
            File.Copy(configuration.ConfigPath, backupPath, overwrite: true);
        }
        catch
        {
            // The atomic config write remains authoritative.
        }

        SetIniValue(configuration.ConfigPath, "AccountApi:Authentication", "SigningSecret", value);
        return value;
    }

    private async Task<AccountApiHealthProbe> ProbeAsync(
        AccountApiConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (!configuration.ConfigExists)
        {
            return AccountApiHealthProbe.Unreachable("未找到 Account API 配置文件。");
        }

        if (!configuration.HasSigningSecret)
        {
            return AccountApiHealthProbe.Unreachable("HMAC 签名密钥尚未生成。");
        }

        try
        {
            using var request = CreateSignedRequest(configuration, "/health");
            using var response = await _http.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync();
            return ParseHealthResponse((int)response.StatusCode, body);
        }
        catch (TaskCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception error)
        {
            return AccountApiHealthProbe.Unreachable(error.Message);
        }
    }

    private async Task<bool> WaitForReachableAsync(
        AccountApiConfiguration configuration,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var startedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - startedAt < timeout)
        {
            var probe = await ProbeAsync(configuration, cancellationToken);
            if (probe.Reachable)
            {
                return true;
            }

            await Task.Delay(200, cancellationToken);
        }

        return false;
    }

    private static HttpRequestMessage CreateSignedRequest(AccountApiConfiguration configuration, string rawPath)
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var nonceBytes = new byte[16];
        using (var random = RandomNumberGenerator.Create())
        {
            random.GetBytes(nonceBytes);
        }

        var nonce = BitConverter.ToString(nonceBytes).Replace("-", string.Empty).ToLowerInvariant();
        var payload = timestamp + "\n" + nonce + "\nGET\n" + rawPath + "\n";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(configuration.SigningSecret));
        var signature = Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(payload)))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

        var request = new HttpRequestMessage(
            HttpMethod.Get,
            configuration.Endpoint.TrimEnd('/') + rawPath);
        request.Headers.TryAddWithoutValidation("X-MarchCenter-Timestamp", timestamp);
        request.Headers.TryAddWithoutValidation("X-MarchCenter-Nonce", nonce);
        request.Headers.TryAddWithoutValidation("X-MarchCenter-Signature", signature);
        return request;
    }

    private static AccountApiHealthProbe ParseHealthResponse(int statusCode, string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = ReadString(root, "status");
            var version = ReadString(root, "version");
            var deviceId = ReadString(root, "deviceId");
            var effectiveBindAddress = ReadString(root, "bindAddress");
            var ok = root.TryGetProperty("ok", out var okElement) &&
                     okElement.ValueKind == JsonValueKind.True;
            if (string.IsNullOrWhiteSpace(status))
            {
                status = statusCode == 200 ? "ok" : "http_" + statusCode;
            }

            var providers = new List<AccountApiProviderStatus>();
            if (root.TryGetProperty("systems", out var systems) &&
                systems.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in systems.EnumerateObject())
                {
                    var value = property.Value;
                    var providerOk = value.TryGetProperty("ok", out var providerOkElement) &&
                                     providerOkElement.ValueKind == JsonValueKind.True;
                    var latency = value.TryGetProperty("latencyMs", out var latencyElement) &&
                                  latencyElement.TryGetInt32(out var latencyValue)
                        ? latencyValue
                        : (int?)null;
                    providers.Add(new AccountApiProviderStatus
                    {
                        Id = property.Name,
                        Name = ReadString(value, "name", property.Name),
                        IsHealthy = providerOk,
                        LatencyMs = latency,
                        Error = ReadString(value, "error")
                    });
                }
            }

            var error = statusCode == 401
                ? "HMAC 认证失败，请确认网页后台与 MCPanel 使用同一枚密钥。"
                : statusCode >= 400 && !ok
                    ? ReadString(root, "error")
                    : string.Empty;
            return new AccountApiHealthProbe
            {
                Reachable = true,
                Healthy = ok || statusCode == 200,
                HttpStatusCode = statusCode,
                Status = status,
                Version = version,
                DeviceId = deviceId,
                EffectiveBindAddress = effectiveBindAddress,
                Providers = providers,
                Error = error
            };
        }
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
    }

    private static string ReadString(JsonElement element, string propertyName, string fallback = "") =>
        element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private AccountApiConfiguration LoadConfiguration()
    {
        var configPath = AccountApiStorage.ConfigPath;
        var values = File.Exists(configPath)
            ? ReadIniValues(configPath, null)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var bindAddress = Value(values, "AccountApi:Server", "BindAddress", "127.0.0.1");
        var port = ParsePort(Value(values, "AccountApi:Server", "Port", "8088"));
        var authMode = Value(values, "AccountApi:Authentication", "Mode", "Hmac");
        var secret = Value(values, "AccountApi:Authentication", "SigningSecret", string.Empty);
        var clockSkew = Value(values, "AccountApi:Authentication", "ClockSkewSeconds", "300");
        var auditDirectory = Value(values, "AccountApi", "AuditLogDirectory", "logs");
        var logDirectory = Path.IsPathRooted(auditDirectory)
            ? auditDirectory
            : AccountApiStorage.PathFor(string.IsNullOrWhiteSpace(auditDirectory) ? "logs" : auditDirectory);

        return new AccountApiConfiguration
        {
            RuntimeDirectory = AccountApiStorage.RootPath,
            ConfigPath = configPath,
            BindAddress = bindAddress,
            Port = port,
            AuthenticationMode = authMode,
            SigningSecret = secret,
            ClockSkewSeconds = int.TryParse(clockSkew, out var skew) ? skew : 300,
            AuditLogDirectory = auditDirectory,
            LogDirectory = logDirectory
        };
    }

    private static Dictionary<string, string> ReadIniValues(string path, string? content)
    {
        var text = content ?? File.ReadAllText(path, Encoding.UTF8);
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = string.Empty;
        var lineNumber = 0;
        foreach (var raw in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith(";", StringComparison.Ordinal) || line.StartsWith("#", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                section = line.Substring(1, line.Length - 2).Trim();
                if (section.Length == 0) throw new InvalidDataException("config.ini 第 " + lineNumber + " 行的分组名称为空。");
                continue;
            }

            var separator = line.IndexOf('=');
            if (separator <= 0) throw new InvalidDataException("config.ini 第 " + lineNumber + " 行格式错误。");
            var key = line.Substring(0, separator).Trim();
            var value = line.Substring(separator + 1).Trim();
            var fullKey = section + ":" + key;
            if (result.ContainsKey(fullKey)) throw new InvalidDataException("config.ini 存在重复配置：" + fullKey);
            result[fullKey] = value;
        }

        return result;
    }

    private static string Value(IReadOnlyDictionary<string, string> values, string section, string key, string fallback) =>
        values.TryGetValue(section + ":" + key, out var value) ? value : fallback;

    private static int ParsePort(string value) =>
        int.TryParse(value, out var port) && port is >= 1 and <= 65535 ? port : 8088;

    private static void SetIniValue(string path, string section, string key, string value)
    {
        var lines = File.ReadAllLines(path, Encoding.UTF8).ToList();
        var header = "[" + section + "]";
        var start = lines.FindIndex(line => string.Equals(line.Trim(), header, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
        {
            lines.Add(string.Empty);
            lines.Add(header);
            lines.Add(key + "=" + value);
        }
        else
        {
            var end = lines.FindIndex(start + 1, line =>
            {
                var text = line.Trim();
                return text.StartsWith("[", StringComparison.Ordinal) && text.EndsWith("]", StringComparison.Ordinal);
            });
            if (end < 0) end = lines.Count;
            var found = -1;
            for (var index = start + 1; index < end; index++)
            {
                var separator = lines[index].IndexOf('=');
                if (separator > 0 && string.Equals(lines[index].Substring(0, separator).Trim(), key, StringComparison.OrdinalIgnoreCase))
                {
                    found = index;
                    break;
                }
            }

            if (found >= 0) lines[found] = key + "=" + value;
            else lines.Insert(end, key + "=" + value);
        }

        AtomicFile.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
    }

    private void EnsureDefaultConfiguration()
    {
        if (File.Exists(AccountApiStorage.ConfigPath)) return;
        AtomicFile.WriteAllText(AccountApiStorage.ConfigPath, DefaultConfigurationText);
    }

    private static string ReadRecentLogs(AccountApiConfiguration configuration)
    {
        try
        {
            Directory.CreateDirectory(configuration.LogDirectory);
            var directories = new[]
            {
                configuration.LogDirectory,
                Path.Combine(AccountApiStorage.LegacyStoreDataRootPath, "logs"),
                Path.Combine(AccountApiStorage.LegacyRootPath, "logs")
            }
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
            var files = directories
                .SelectMany(directory => Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
                .Where(file => file.EndsWith(".log", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
                .GroupBy(Path.GetFullPath, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .Take(3)
                .Reverse()
                .ToArray();

            var output = new StringBuilder();
            foreach (var file in files)
            {
                output.AppendLine("===== " + Path.GetFileName(file) + " =====");
                foreach (var line in ReadRecentLines(file, 70)) output.AppendLine(RedactSensitiveLog(line));
            }

            return output.Length == 0
                ? "当前没有日志。启动内置服务或执行账号操作后会在这里显示。"
                : output.ToString();
        }
        catch (Exception error)
        {
            return "日志读取失败：" + error.Message;
        }
    }

    private static string RedactSensitiveLog(string value)
    {
        var redacted = Regex.Replace(value, "(?i)(\"?(?:password|pwd|signingsecret|bearertoken|accessclientsecret)\"?\\s*:\\s*\")([^\"]*)(\")", "$1***$3");
        return Regex.Replace(redacted, "(?i)(\\b(?:password|pwd|signingsecret|bearertoken|accessclientsecret)\\s*=\\s*)([^;,\\s]+)", "$1***");
    }

    private static string ReadLatestAccountApiError(AccountApiConfiguration configuration)
    {
        try
        {
            var path = Path.Combine(configuration.LogDirectory, "service.log");
            if (!File.Exists(path)) return "";
            var lines = ReadRecentLines(path, 120);
            for (var index = lines.Count - 1; index >= 0; index--)
            {
                if (lines[index].Contains("错误", StringComparison.OrdinalIgnoreCase) || lines[index].Contains("error", StringComparison.OrdinalIgnoreCase))
                    return RedactSensitiveLog(lines[index]);
            }
        }
        catch { }

        return "";
    }

    private AccountApiManagerSettings LoadSettings()
    {
        foreach (var path in new[] { _settingsPath, AccountApiStorage.LegacySettingsPath }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;

            try
            {
                return JsonSerializer.Deserialize<AccountApiManagerSettings>(
                           File.ReadAllText(path, Encoding.UTF8))
                       ?? new AccountApiManagerSettings();
            }
            catch when (!string.Equals(path, AccountApiStorage.LegacySettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                // A partially written new file should not hide a valid
                // legacy settings file during the one-time migration.
            }
        }

        return new AccountApiManagerSettings();
    }

    private static void TryDeleteLegacySettings()
    {
        try
        {
            if (!string.Equals(AccountApiStorage.SettingsPath, AccountApiStorage.LegacySettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(AccountApiStorage.LegacySettingsPath);
            }
        }
        catch
        {
            // Keeping the old settings file is safe; the new path remains
            // authoritative and will be retried on the next startup.
        }
    }

    private void SaveSettings()
    {
        var directory = Path.GetDirectoryName(_settingsPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var settings = new AccountApiManagerSettings
        {
            RuntimeDirectory = _configuredRuntimeDirectory,
            Enabled = _enabled
        };
        AtomicFile.WriteAllText(
            _settingsPath,
            JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));
    }

    private static IReadOnlyList<string> ReadRecentLines(string path, int maximumLines)
    {
        const int maximumBytes = 128 * 1024;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, maximumBytes);
            if (length <= 0) return Array.Empty<string>();
            var offset = stream.Length > length ? stream.Length - length : 0;
            stream.Seek(offset, SeekOrigin.Begin);
            var buffer = new byte[length];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            var text = Encoding.UTF8.GetString(buffer, 0, read).Trim('\uFEFF');
            if (offset > 0)
            {
                var firstBreak = text.IndexOf('\n');
                text = firstBreak >= 0 ? text.Substring(firstBreak + 1) : string.Empty;
            }

            return text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None).TakeLast(maximumLines).ToArray();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string JoinMessages(params string?[] messages) =>
        string.Join(Environment.NewLine, messages.Where(message => !string.IsNullOrWhiteSpace(message)));

    private static string QuoteArgument(string value) =>
        "\"" + value.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";

    private void ThrowIfDisposed()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AccountApiManagerService));
    }

    private static readonly string DefaultConfigurationText =
        "; MCPanel 内置 Account API 配置\r\n" +
        "; 数据库连接由网页后台加密下发，本机只保存监听和认证参数。\r\n\r\n" +
        "[AccountApi:Server]\r\n" +
        "BindAddress=127.0.0.1\r\n" +
        "Port=8088\r\n" +
        "RequestBodyLimitBytes=65536\r\n" +
        "RequestsPerMinute=120\r\n\r\n" +
        "[AccountApi:Authentication]\r\n" +
        "Mode=Hmac\r\n" +
        "SigningSecret=\r\n" +
        "ClockSkewSeconds=300\r\n\r\n" +
        "[AccountApi]\r\n" +
        "AuditLogDirectory=logs\r\n";

    private sealed class AccountApiManagerSettings
    {
        public string RuntimeDirectory { get; set; } = string.Empty;
        public bool Enabled { get; set; }
    }

    private sealed record AccountApiHealthProbe
    {
        public bool Reachable { get; init; }
        public bool Healthy { get; init; }
        public int HttpStatusCode { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Version { get; init; } = string.Empty;
        public string DeviceId { get; init; } = string.Empty;
        public string EffectiveBindAddress { get; init; } = string.Empty;
        public string Error { get; init; } = string.Empty;
        public IReadOnlyList<AccountApiProviderStatus> Providers { get; init; } = Array.Empty<AccountApiProviderStatus>();

        public static AccountApiHealthProbe Unreachable(string error) => new()
        {
            Reachable = false,
            Healthy = false,
            Status = "offline",
            Error = error
        };

        public static AccountApiHealthProbe Disabled() => new()
        {
            Reachable = false,
            Healthy = false,
            Status = "disabled"
        };
    }
}

public sealed class AccountApiConfiguration
{
    public string RuntimeDirectory { get; init; } = string.Empty;
    public string ConfigPath { get; init; } = string.Empty;
    public string BindAddress { get; init; } = "127.0.0.1";
    public int Port { get; init; } = 8088;
    public string AuthenticationMode { get; init; } = "Hmac";
    public string SigningSecret { get; init; } = string.Empty;
    public int ClockSkewSeconds { get; init; } = 300;
    public string AuditLogDirectory { get; init; } = "logs";
    public string LogDirectory { get; init; } = string.Empty;

    public bool RuntimeExists => ConfigExists;
    public bool ConfigExists => File.Exists(ConfigPath);
    public bool HasSigningSecret => SigningSecret.Length >= 32 && !SigningSecret.StartsWith("CHANGE_", StringComparison.OrdinalIgnoreCase);
    internal static string FormatUriHost(string value)
    {
        var host = string.IsNullOrWhiteSpace(value) ? "127.0.0.1" : value.Trim();
        return host.IndexOf(':') >= 0 && !(host.StartsWith("[", StringComparison.Ordinal) && host.EndsWith("]", StringComparison.Ordinal))
            ? "[" + host + "]"
            : host;
    }

    public string Endpoint
    {
        get
        {
            var host = string.Equals(BindAddress, "0.0.0.0", StringComparison.Ordinal) || string.Equals(BindAddress, "::", StringComparison.Ordinal)
                ? "127.0.0.1"
                : BindAddress;
            return "http://" + FormatUriHost(host) + ":" + Port;
        }
    }
}

public sealed class AccountApiSnapshot
{
    public AccountApiConfiguration Configuration { get; init; } = new();
    public bool Enabled { get; init; }
    public bool Reachable { get; init; }
    public bool Healthy { get; init; }
    public int HttpStatusCode { get; init; }
    public string HealthStatus { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string DeviceId { get; init; } = string.Empty;
    public string EffectiveBindAddress { get; init; } = string.Empty;
    public bool ServiceInstalled { get; init; }
    public string WindowsServiceStatus { get; init; } = "MCPanel 内置";
    public string WindowsServiceError { get; init; } = string.Empty;
    public bool LocalProcessRunning { get; init; }
    public string LogsText { get; init; } = string.Empty;
    public string ErrorText { get; init; } = string.Empty;
    public DateTimeOffset CheckedAt { get; init; }
    public IReadOnlyList<AccountApiProviderStatus> Providers { get; init; } = Array.Empty<AccountApiProviderStatus>();
}

public sealed class AccountApiProviderStatus
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool IsHealthy { get; init; }
    public int? LatencyMs { get; init; }
    public string Error { get; init; } = string.Empty;
}
