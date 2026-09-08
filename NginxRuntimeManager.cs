using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed class NginxRuntimeOptions
{
    public int ListenPort { get; set; } = NginxRuntimeManager.DefaultListenPort;
    public bool ProxyEnabled { get; set; } = true;
    public string ProxyTarget { get; set; } = "http://127.0.0.1:9287";
    public List<NginxProxyRule> Rules { get; set; } = [];
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.Now;

    public IEnumerable<NginxProxyRule> EnabledRules => Rules.Where(rule => rule.Enabled);

    public string Summary
    {
        get
        {
            var enabled = EnabledRules.ToArray();
            if (enabled.Length == 0)
            {
                return $"监听端口：{ListenPort}，反向代理未启用。";
            }

            var ports = string.Join("、", NginxRuntimeManager.GetEffectiveListenPorts(this));
            return $"已配置 {enabled.Length} 条反向代理规则，监听端口：{ports}。";
        }
    }
}

public sealed class NginxProxyRule
{
    public bool Enabled { get; set; } = true;
    public string Name { get; set; } = "本地服务";
    public int ListenPort { get; set; } = NginxRuntimeManager.DefaultListenPort;
    public string ServerName { get; set; } = "localhost";
    public string LocationPath { get; set; } = "/";
    public string ProxyTarget { get; set; } = "http://127.0.0.1:9287";
    public bool WebSocket { get; set; } = true;
    public string? ManagedProductId { get; set; }
    public string? ManagedWebsiteId { get; set; }
    public bool SslEnabled { get; set; }
    public int HttpsPort { get; set; } = 443;
    public string SslCertificatePath { get; set; } = string.Empty;
    public string SslCertificateKeyPath { get; set; } = string.Empty;
    public bool RedirectHttpToHttps { get; set; }
    public int MaxRateKbps { get; set; }
}

public static class NginxRuntimeManager
{
    public const string VersionDirectoryName = "nginx-1.14.2";
    public const int DefaultListenPort = 72;

    private static readonly int[] PreferredPorts = [DefaultListenPort, 80, 8088, 8080, 8090, 8099, 18080];
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private static readonly Regex ListenDirectiveRegex = new(
        @"(?im)(?:^|[;{}])\s*listen\s+(?:(?:\[[^\]]+\]|[A-Za-z0-9_.-]+|\*)\:)?(?<port>\d+)\b",
        RegexOptions.Compiled);
    private static readonly Regex IncludeDirectiveRegex = new(
        @"(?im)(?:^|[;{}])\s*include\s+(?<path>[^;#]+);",
        RegexOptions.Compiled);

    private static string StateDirectory => ComponentPaths.RuntimeStateRoot;
    internal static string StateFile => Path.Combine(StateDirectory, "nginx-runtime.json");

    public static string? FindNginxExe(string runtimeRoot)
    {
        var preferred = Path.Combine(runtimeRoot, VersionDirectoryName, "nginx.exe");
        if (File.Exists(preferred))
        {
            return preferred;
        }

        if (!Directory.Exists(runtimeRoot))
        {
            return null;
        }

        // Runtime also contains SQL Server data directories. Do not recursively
        // enumerate the whole tree: SQL Server intentionally restricts folders
        // such as MSSQL\Backup, and an unrelated ACL must never break Nginx or
        // environment-state detection.
        try
        {
            var direct = Path.Combine(runtimeRoot, "nginx.exe");
            if (File.Exists(direct))
            {
                return direct;
            }

            foreach (var directory in Directory.EnumerateDirectories(runtimeRoot, "*", SearchOption.TopDirectoryOnly))
            {
                var candidate = Path.Combine(directory, "nginx.exe");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch (UnauthorizedAccessException)
        {
            // An inaccessible sibling directory is not evidence that Nginx is absent.
        }
        catch (IOException)
        {
            // A transient filesystem error must not make the panel report an install failure.
        }

        return null;
    }

    public static IReadOnlyList<int> BuildPortCandidates()
    {
        var ports = new List<int>();
        var saved = LoadOptions()?.ListenPort;
        if (saved is > 0)
        {
            ports.Add(saved.Value);
        }

        ports.AddRange(PreferredPorts);
        return ports.Distinct().Where(port => port is > 0 and <= 65535).ToArray();
    }

    public static int ConfigureWithAvailablePort(string nginxRoot, IEnumerable<int>? candidates = null)
    {
        Exception? lastError = null;
        var options = LoadOptions() ?? new NginxRuntimeOptions();

        foreach (var port in candidates ?? BuildPortCandidates())
        {
            if (!IsPortAvailable(port))
            {
                continue;
            }

            try
            {
                CleanConfigFiles(nginxRoot);
                options.ListenPort = port;
                options.Rules = [CreateDefaultRule(port, options.ProxyTarget, options.ProxyEnabled)];
                ApplyConfig(nginxRoot, options);
                return port;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException("Nginx 未找到可用监听端口，请检查 72、80、8088、8080、8090、8099、18080 是否被占用。", lastError);
    }

    public static void ApplyConfig(string nginxRoot, NginxRuntimeOptions options)
    {
        var normalized = NormalizeOptions(options);
        ValidateOptions(normalized);
        WriteManagedConfig(nginxRoot, normalized);
        SaveOptions(normalized);
    }

    public static bool IsPortAvailable(int port)
    {
        try
        {
            if (IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port))
            {
                return false;
            }
        }
        catch
        {
            // Fall through to a bind test.
        }

        try
        {
            var listener = new TcpListener(IPAddress.Any, port);
            try
            {
                listener.ExclusiveAddressUse = true;
                listener.Start();
                return true;
            }
            finally
            {
                listener.Stop();
            }
        }
        catch
        {
            return false;
        }
    }

    public static bool IsPortBindFailure(string text)
    {
        return text.Contains("bind()", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("10013", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("10048", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("forbidden by its access permissions", StringComparison.OrdinalIgnoreCase) ||
               text.Contains("Only one usage of each socket address", StringComparison.OrdinalIgnoreCase);
    }

    public static void CleanConfigFiles(string nginxRoot)
    {
        var confDirectory = Path.Combine(nginxRoot, "conf");
        if (!Directory.Exists(confDirectory))
        {
            return;
        }

        foreach (var file in Directory.GetFiles(confDirectory, "*.conf", SearchOption.AllDirectories))
        {
            var bytes = File.ReadAllBytes(file);
            var hasUtf8Bom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
            var text = File.ReadAllText(file, Encoding.UTF8);
            if (!hasUtf8Bom && !text.Contains('\uFEFF'))
            {
                continue;
            }

            AtomicFile.WriteAllText(file, text.Replace("\uFEFF", string.Empty, StringComparison.Ordinal), new UTF8Encoding(false));
        }
    }

    public static void WriteListenPort(string nginxRoot, int port)
    {
        var options = LoadOptions() ?? new NginxRuntimeOptions();
        options.ListenPort = port;
        if (options.Rules.Count == 0)
        {
            options.Rules.Add(CreateDefaultRule(port, options.ProxyTarget, options.ProxyEnabled));
        }
        else if (options.Rules.Count == 1)
        {
            options.Rules[0].ListenPort = port;
        }

        WriteManagedConfig(nginxRoot, options);
    }

    public static void WriteManagedConfig(string nginxRoot, NginxRuntimeOptions options)
    {
        var normalized = NormalizeOptions(options);
        ValidateOptions(normalized);
        var confDirectory = Path.Combine(nginxRoot, "conf");
        Directory.CreateDirectory(confDirectory);
        var confFile = Path.Combine(confDirectory, "nginx.conf");
        AtomicFile.WriteAllText(confFile, BuildManagedConfig(normalized), new UTF8Encoding(false));
    }

    public static NginxRuntimeOptions NormalizeOptions(NginxRuntimeOptions options)
    {
        var rules = options.Rules.Count > 0
            ? options.Rules.Select(NormalizeRule).ToList()
            : [CreateDefaultRule(options.ListenPort, options.ProxyTarget, options.ProxyEnabled)];

        var firstEnabled = rules.FirstOrDefault(rule => rule.Enabled) ?? rules.First();
        return new NginxRuntimeOptions
        {
            ListenPort = firstEnabled.ListenPort,
            ProxyEnabled = rules.Any(rule => rule.Enabled),
            ProxyTarget = firstEnabled.ProxyTarget,
            Rules = rules,
            UpdatedAt = DateTimeOffset.Now
        };
    }

    public static NginxProxyRule CreateDefaultRule(int port = DefaultListenPort, string target = "http://127.0.0.1:9287", bool enabled = true)
    {
        return new NginxProxyRule
        {
            Enabled = enabled,
            Name = "本地服务",
            ListenPort = port is > 0 and <= 65535 ? port : DefaultListenPort,
            ServerName = "localhost",
            LocationPath = "/",
            ProxyTarget = NormalizeProxyTarget(target),
            WebSocket = true
        };
    }

    public static NginxProxyRule NormalizeRule(NginxProxyRule rule)
    {
        var location = string.IsNullOrWhiteSpace(rule.LocationPath) ? "/" : rule.LocationPath.Trim();
        if (!location.StartsWith('/'))
        {
            location = "/" + location;
        }

        return new NginxProxyRule
        {
            Enabled = rule.Enabled,
            Name = NormalizeLabel(rule.Name, "未命名规则"),
            ListenPort = rule.ListenPort,
            ServerName = string.IsNullOrWhiteSpace(rule.ServerName) ? "localhost" : rule.ServerName.Trim(),
            LocationPath = location,
            ProxyTarget = NormalizeProxyTarget(rule.ProxyTarget),
            WebSocket = rule.WebSocket,
            ManagedProductId = string.IsNullOrWhiteSpace(rule.ManagedProductId)
                ? null
                : rule.ManagedProductId!.Trim(),
            ManagedWebsiteId = string.IsNullOrWhiteSpace(rule.ManagedWebsiteId)
                ? null
                : rule.ManagedWebsiteId!.Trim(),
            SslEnabled = rule.SslEnabled,
            HttpsPort = rule.HttpsPort == 0 ? 443 : rule.HttpsPort,
            SslCertificatePath = (rule.SslCertificatePath ?? string.Empty).Trim(),
            SslCertificateKeyPath = (rule.SslCertificateKeyPath ?? string.Empty).Trim(),
            RedirectHttpToHttps = rule.SslEnabled && rule.RedirectHttpToHttps,
            MaxRateKbps = Math.Max(0, rule.MaxRateKbps)
        };
    }

    public static string NormalizeProxyTarget(string target)
    {
        var text = (target ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            return "http://127.0.0.1:9287";
        }

        if (!text.Contains("://", StringComparison.Ordinal))
        {
            text = "http://" + text;
        }

        return text;
    }

    public static void ValidateOptions(NginxRuntimeOptions options)
    {
        if (options.Rules.Count == 0)
        {
            throw new InvalidOperationException("至少需要保留一条 Nginx 代理规则。");
        }

        var normalizedRules = options.Rules.Select(NormalizeRule).ToArray();
        var enabledRules = normalizedRules.Where(rule => rule.Enabled).ToArray();

        foreach (var rule in normalizedRules)
        {
            if (rule.ListenPort is <= 0 or > 65535)
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的监听端口必须在 1 到 65535 之间。");
            }

            if (!rule.LocationPath.StartsWith('/'))
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的路径必须以 / 开头。");
            }

            if (rule.LocationPath.IndexOfAny([';', '{', '}', '\r', '\n', '\t', ' ']) >= 0)
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的代理路径不能包含空格或 Nginx 配置字符。");
            }

            if (string.IsNullOrWhiteSpace(rule.ServerName) ||
                rule.ServerName.IndexOfAny([';', '{', '}', '\"', '\r', '\n', '\t']) >= 0)
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的域名格式无效。");
            }

            if (rule.Enabled &&
                (!Uri.TryCreate(rule.ProxyTarget, UriKind.Absolute, out var uri) ||
                 uri.Scheme is not ("http" or "https") ||
                 string.IsNullOrWhiteSpace(uri.Host) ||
                 rule.ProxyTarget.IndexOfAny([';', '{', '}', '\r', '\n', '\t', ' ']) >= 0))
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的代理目标必须是完整的 http 或 https 地址。");
            }

            if (rule.MaxRateKbps is < 0 or > 1048576)
            {
                throw new InvalidOperationException($"规则“{rule.Name}”的限速必须在 0 到 1048576 KB/s 之间。");
            }

            if (rule.Enabled && rule.SslEnabled)
            {
                if (rule.HttpsPort is <= 0 or > 65535 || rule.HttpsPort == rule.ListenPort)
                {
                    throw new InvalidOperationException($"规则“{rule.Name}”的 HTTPS 端口无效，且不能与 HTTP 端口相同。");
                }

                if (!File.Exists(rule.SslCertificatePath) || !File.Exists(rule.SslCertificateKeyPath))
                {
                    throw new InvalidOperationException($"规则“{rule.Name}”的 SSL 证书或私钥文件不存在。");
                }
            }
        }

        var duplicated = enabledRules
            .GroupBy(rule => $"{rule.ListenPort}|{rule.ServerName.ToLowerInvariant()}|{rule.LocationPath.ToLowerInvariant()}")
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicated is not null)
        {
            var rule = duplicated.First();
            throw new InvalidOperationException($"存在重复代理规则：端口 {rule.ListenPort} / 主机 {rule.ServerName} / 路径 {rule.LocationPath}。");
        }


        var conflictingServer = enabledRules
            .GroupBy(rule => $"{rule.ListenPort}|{rule.ServerName.ToLowerInvariant()}")
            .FirstOrDefault(group => group
                .Select(rule => $"{rule.SslEnabled}|{rule.HttpsPort}|{rule.RedirectHttpToHttps}|{rule.SslCertificatePath}|{rule.SslCertificateKeyPath}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Count() > 1);
        if (conflictingServer is not null)
        {
            throw new InvalidOperationException($"域名 {conflictingServer.First().ServerName} 的 SSL 配置不一致，请统一后再保存。");
        }

        var duplicatedHttps = enabledRules
            .Where(rule => rule.SslEnabled)
            .GroupBy(rule => $"{rule.HttpsPort}|{rule.ServerName.ToLowerInvariant()}|{rule.LocationPath.ToLowerInvariant()}")
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatedHttps is not null)
        {
            var rule = duplicatedHttps.First();
            throw new InvalidOperationException($"存在重复 HTTPS 规则：端口 {rule.HttpsPort} / 主机 {rule.ServerName} / 路径 {rule.LocationPath}。");
        }
    }

    public static IReadOnlyList<int> GetEffectiveListenPorts(NginxRuntimeOptions options) =>
        options.EnabledRules
            .SelectMany(rule => rule.SslEnabled ? new[] { rule.ListenPort, rule.HttpsPort } : new[] { rule.ListenPort })
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .OrderBy(port => port)
            .ToArray();

    /// <summary>
    /// Reads the ports from the actual nginx.conf tree rather than from
    /// nginx-runtime.json.  The latter is MCPanel's last saved view and can
    /// legitimately be stale after an administrator edits Nginx by hand.
    /// </summary>
    public static IReadOnlyList<int> ReadConfiguredListenPorts(string nginxRoot)
    {
        if (string.IsNullOrWhiteSpace(nginxRoot))
        {
            return [];
        }

        try
        {
            var ports = new HashSet<int>();
            var pending = new Queue<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entry = Path.Combine(nginxRoot, "conf", "nginx.conf");
            pending.Enqueue(entry);

            while (pending.Count > 0)
            {
                var file = pending.Dequeue();
                if (!File.Exists(file) || !visited.Add(Path.GetFullPath(file)))
                {
                    continue;
                }

                var text = File.ReadAllText(file, Encoding.UTF8);
                foreach (var match in ListenDirectiveRegex.Matches(text).Cast<Match>())
                {
                    if (int.TryParse(match.Groups["port"].Value, out var port) && port is > 0 and <= 65535)
                    {
                        ports.Add(port);
                    }
                }

                foreach (var include in IncludeDirectiveRegex.Matches(text).Cast<Match>())
                {
                    foreach (var includedFile in ResolveIncludeFiles(nginxRoot, file, include.Groups["path"].Value))
                    {
                        pending.Enqueue(includedFile);
                    }
                }
            }

            return ports.OrderBy(port => port).ToArray();
        }
        catch
        {
            return [];
        }
    }

    internal static IReadOnlyList<int> ParseConfiguredListenPorts(string nginxConfig)
    {
        if (string.IsNullOrWhiteSpace(nginxConfig))
        {
            return [];
        }

        return ListenDirectiveRegex.Matches(nginxConfig)
            .Cast<Match>()
            .Select(match => int.TryParse(match.Groups["port"].Value, out var port) ? port : -1)
            .Where(port => port is > 0 and <= 65535)
            .Distinct()
            .OrderBy(port => port)
            .ToArray();
    }

    public static bool AreConfiguredPortsListening(string nginxRoot, out IReadOnlyList<int> configuredPorts, out IReadOnlyList<int> missingPorts)
    {
        configuredPorts = ReadConfiguredListenPorts(nginxRoot);
        var activePorts = GetActiveTcpPorts();
        missingPorts = configuredPorts.Where(port => !activePorts.Contains(port)).ToArray();
        return configuredPorts.Count > 0 && missingPorts.Count == 0;
    }

    public static bool IsRunningUnderRoot(string nginxRoot)
    {
        return EnumerateProcessesUnderRoot(nginxRoot).Any(process =>
        {
            try
            {
                return !process.HasExited;
            }
            catch
            {
                return false;
            }
            finally
            {
                process.Dispose();
            }
        });
    }

    public static void KillProcessesUnderRoot(string nginxRoot)
    {
        foreach (var process in EnumerateProcessesUnderRoot(nginxRoot))
        {
            using (process)
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
                    // Best effort cleanup before reinstall/uninstall.
                }
            }
        }
    }

    public static void EnsureSafeDeleteRoot(string nginxRoot)
    {
        if (string.IsNullOrWhiteSpace(nginxRoot))
        {
            throw new InvalidOperationException("Nginx 卸载目录为空，已取消删除。");
        }

        var fullRoot = Path.GetFullPath(nginxRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var protectedRoots = new[]
        {
            ComponentPaths.ApplicationRoot,
            ComponentPaths.StoreDataRoot,
            ComponentPaths.RuntimeRoot,
            ComponentPaths.LegacyRuntimeRoot
        };

        if (protectedRoots.Any(root => string.Equals(
                fullRoot,
                Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidOperationException(
                $"Nginx 卸载目录解析为 MCPanel 共享目录，已取消删除：{fullRoot}。请先迁移 Nginx 到独立组件目录。");
        }
    }

    public static void DeleteRoot(string nginxRoot)
    {
        EnsureSafeDeleteRoot(nginxRoot);
        KillProcessesUnderRoot(nginxRoot);
        if (IsRunningUnderRoot(nginxRoot))
        {
            throw new InvalidOperationException(
                $"Nginx 进程仍在运行，已取消删除目录：{Path.GetFullPath(nginxRoot)}。请先停止占用进程后重试。");
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(nginxRoot))
                {
                    Directory.Delete(nginxRoot, recursive: true);
                }

                return;
            }
            catch when (attempt < 4)
            {
                Thread.Sleep(400);
                KillProcessesUnderRoot(nginxRoot);
                if (IsRunningUnderRoot(nginxRoot))
                {
                    throw new InvalidOperationException(
                        $"Nginx 进程仍在运行，已取消删除目录：{Path.GetFullPath(nginxRoot)}。请先停止占用进程后重试。");
                }
            }
        }

        if (Directory.Exists(nginxRoot))
        {
            if (IsRunningUnderRoot(nginxRoot))
            {
                throw new InvalidOperationException(
                    $"Nginx 进程仍在运行，已取消删除目录：{Path.GetFullPath(nginxRoot)}。请先停止占用进程后重试。");
            }

            Directory.Delete(nginxRoot, recursive: true);
        }
    }

    public static string ReadRecentErrorLog(string nginxRoot)
    {
        var errorLog = Path.Combine(nginxRoot, "logs", "error.log");
        if (!File.Exists(errorLog))
        {
            return string.Empty;
        }

        try
        {
            var lines = File.ReadLines(errorLog, Encoding.UTF8).TakeLast(8);
            return string.Join(Environment.NewLine, lines).Trim();
        }
        catch
        {
            return string.Empty;
        }
    }

    public static NginxRuntimeOptions? LoadOptions()
    {
        try
        {
            if (!File.Exists(StateFile))
            {
                return null;
            }

            var options = JsonSerializer.Deserialize<NginxRuntimeOptions>(File.ReadAllText(StateFile, Encoding.UTF8));
            return options is null ? null : NormalizeOptions(options);
        }
        catch
        {
            return null;
        }
    }

    public static void SaveOptions(NginxRuntimeOptions options)
    {
        var normalized = NormalizeOptions(options);
        ValidateOptions(normalized);
        Directory.CreateDirectory(StateDirectory);
        AtomicFile.WriteAllText(StateFile, JsonSerializer.Serialize(normalized, JsonOptions), new UTF8Encoding(false));
    }

    public static void SaveOptions(int port)
    {
        var options = LoadOptions() ?? new NginxRuntimeOptions();
        options.ListenPort = port;
        if (options.Rules.Count == 0)
        {
            options.Rules.Add(CreateDefaultRule(port, options.ProxyTarget, options.ProxyEnabled));
        }
        else if (options.Rules.Count == 1)
        {
            options.Rules[0].ListenPort = port;
        }

        SaveOptions(options);
    }

    internal static string BuildManagedConfig(NginxRuntimeOptions options)
    {
        var enabledRules = options.EnabledRules.Select(NormalizeRule).ToArray();
        var servers = enabledRules
            .GroupBy(rule => new { rule.ListenPort, ServerName = rule.ServerName.ToLowerInvariant() })
            .OrderBy(group => group.Key.ListenPort)
            .ThenBy(group => group.Key.ServerName)
            .SelectMany(group => BuildServerBlocks(group.Key.ListenPort, group.First().ServerName, group))
            .ToArray();

        var serverBlocks = servers.Length > 0
            ? string.Join(Environment.NewLine + Environment.NewLine, servers)
            : BuildFallbackServerBlock(options.ListenPort);

        return $$"""
            worker_processes  1;

            error_log  logs/error.log;
            pid        logs/nginx.pid;

            events {
                worker_connections  1024;
            }

            http {
                include       mime.types;
                default_type  application/octet-stream;
                sendfile        on;
                keepalive_timeout  65;

            {{serverBlocks}}
            }
            """;
    }

    private static IEnumerable<string> BuildServerBlocks(int listenPort, string serverName, IEnumerable<NginxProxyRule> rules)
    {
        var normalizedRules = rules.ToArray();
        var first = normalizedRules[0];
        var locations = string.Join(Environment.NewLine, normalizedRules
            .OrderByDescending(rule => rule.LocationPath.Length)
            .ThenBy(rule => rule.LocationPath)
            .Select(BuildLocationBlock));

        if (!first.SslEnabled)
        {
            yield return BuildProxyServerBlock(listenPort, serverName, locations, sslDirectives: string.Empty);
            yield break;
        }

        if (first.RedirectHttpToHttps)
        {
            var httpsAuthority = first.HttpsPort == 443 ? "$host" : $"$host:{first.HttpsPort}";
            yield return $$"""
                server {
                    listen       {{listenPort}};
                    server_name  {{EscapeNginxToken(serverName)}};

                    return 301 https://{{httpsAuthority}}$request_uri;
                }
            """;

        }
        else
        {
            yield return BuildProxyServerBlock(listenPort, serverName, locations, sslDirectives: string.Empty);
        }

        var sslDirectives = $$"""
                    ssl_certificate      "{{EscapeNginxQuotedPath(first.SslCertificatePath)}}";
                    ssl_certificate_key  "{{EscapeNginxQuotedPath(first.SslCertificateKeyPath)}}";
                    ssl_protocols        TLSv1.2;
                    ssl_session_cache    shared:MCPanelSSL:10m;
        """;
        yield return BuildProxyServerBlock(first.HttpsPort, serverName, locations, sslDirectives, ssl: true);
    }

    private static string BuildProxyServerBlock(
        int listenPort,
        string serverName,
        string locations,
        string sslDirectives,
        bool ssl = false)
    {
        var sslSuffix = ssl ? " ssl" : string.Empty;
        return $$"""
                server {
                    listen       {{listenPort}}{{sslSuffix}};
                    server_name  {{EscapeNginxToken(serverName)}};
            {{sslDirectives}}

            {{locations}}
                }
            """;
    }

    private static string BuildLocationBlock(NginxProxyRule rule)
    {
        var upgradeHeaders = rule.WebSocket
            ? """
                        proxy_set_header Upgrade $http_upgrade;
                        proxy_set_header Connection "upgrade";
            """
            : """
                        proxy_set_header Connection "";
            """;
        var rateDirective = rule.MaxRateKbps > 0
            ? $"            limit_rate {rule.MaxRateKbps}k;{Environment.NewLine}"
            : string.Empty;
        var cookiePathDirective = string.Empty;
        if (!string.IsNullOrWhiteSpace(rule.ManagedWebsiteId) &&
            Uri.TryCreate(rule.ProxyTarget, UriKind.Absolute, out var targetUri) &&
            targetUri.AbsolutePath.Length > 1)
        {
            var upstreamPath = targetUri.AbsolutePath.EndsWith('/')
                ? targetUri.AbsolutePath
                : targetUri.AbsolutePath + "/";
            cookiePathDirective = $"            proxy_cookie_path {EscapeNginxLocation(upstreamPath)} /;{Environment.NewLine}";
        }

        return $$"""
                    location {{EscapeNginxLocation(rule.LocationPath)}} {
                        proxy_pass {{rule.ProxyTarget}};
                        proxy_http_version 1.1;
                        proxy_set_header Host $host;
                        proxy_set_header X-Real-IP $remote_addr;
                        proxy_set_header X-Forwarded-For $proxy_add_x_forwarded_for;
                        proxy_set_header X-Forwarded-Proto $scheme;
            {{rateDirective}}{{cookiePathDirective}}{{upgradeHeaders}}
                    }
            """;
    }

    private static string BuildFallbackServerBlock(int listenPort)
    {
        var port = listenPort is > 0 and <= 65535 ? listenPort : DefaultListenPort;
        return $$"""
                server {
                    listen       {{port}};
                    server_name  localhost;

                    location / {
                        return 200 "MCPanel Nginx is running.\n";
                        add_header Content-Type text/plain;
                    }
                }
            """;
    }

    private static string EscapeNginxToken(string value)
    {
        return value.Replace(";", string.Empty, StringComparison.Ordinal)
            .Replace("{", string.Empty, StringComparison.Ordinal)
            .Replace("}", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static string EscapeNginxLocation(string value)
    {
        return EscapeNginxToken(value).Replace("\\", "/", StringComparison.Ordinal);
    }

    private static string EscapeNginxQuotedPath(string value) =>
        Path.GetFullPath(value)
            .Replace("\\", "/", StringComparison.Ordinal)
            .Replace("\"", string.Empty, StringComparison.Ordinal)
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);

    private static string NormalizeLabel(string value, string fallback)
    {
        var text = (value ?? string.Empty).Trim();
        if (text.Length == 0 || LooksLikeMojibake(text))
        {
            return fallback;
        }

        return text;
    }

    private static bool LooksLikeMojibake(string value)
    {
        var markers = new[] { '鐩', '鍙', '绠', '淇', '缂', '鏉', '浠', '閰', '宸', '鏈', '鍚', '涓', '鏃', '濡', '琛', '绋', '鐞', '瑙', '蹇', '銆', '鈥' };
        return value.IndexOfAny(markers) >= 0 || value.Contains('\uFFFD');
    }

    private static IEnumerable<Process> EnumerateProcessesUnderRoot(string nginxRoot)
    {
        var root = EnsureTrailingSeparator(Path.GetFullPath(nginxRoot));
        foreach (var process in Process.GetProcessesByName("nginx"))
        {
            string? fileName = null;
            try
            {
                fileName = process.MainModule?.FileName;
            }
            catch
            {
                process.Dispose();
                continue;
            }

            if (fileName is not null && Path.GetFullPath(fileName).StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                yield return process;
            }
            else
            {
                process.Dispose();
            }
        }
    }

    private static IEnumerable<string> ResolveIncludeFiles(string nginxRoot, string includingFile, string includeValue)
    {
        var includePath = includeValue.Trim().Trim('"', '\'');
        if (includePath.Length == 0)
        {
            yield break;
        }

        var path = includePath.Replace('/', Path.DirectorySeparatorChar);
        var fullPath = Path.IsPathRooted(path)
            ? path
            : path.StartsWith("conf" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                ? Path.Combine(nginxRoot, path)
                : Path.Combine(Path.GetDirectoryName(includingFile) ?? nginxRoot, path);

        var wildcard = fullPath.IndexOfAny(['*', '?']);
        if (wildcard < 0)
        {
            yield return fullPath;
            yield break;
        }

        var directory = Path.GetDirectoryName(fullPath);
        var pattern = Path.GetFileName(fullPath);
        if (string.IsNullOrWhiteSpace(directory) || string.IsNullOrWhiteSpace(pattern) || !Directory.Exists(directory))
        {
            yield break;
        }

        string[] files;
        try
        {
            files = Directory.GetFiles(directory, pattern, SearchOption.TopDirectoryOnly);
        }
        catch
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return file;
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
            return [];
        }
    }

    private static string EnsureTrailingSeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
