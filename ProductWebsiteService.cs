using System.Globalization;
using System.IO;
using System.Net;
using System.Security.Cryptography;

namespace MCPanel;

internal sealed record ProductWebsiteSettings(
    bool Enabled,
    string Domains,
    int HttpPort,
    bool SslEnabled,
    int HttpsPort,
    string CertificatePath,
    string CertificateKeyPath,
    bool RedirectHttpToHttps,
    int MaxRateKbps)
{
    public static ProductWebsiteSettings Default { get; } =
        new(false, string.Empty, 80, false, 443, string.Empty, string.Empty, true, 0);
}

internal sealed class ProductWebsiteService
{
    private readonly EnvironmentRuntimeService _runtimeService;

    public ProductWebsiteService(EnvironmentRuntimeService runtimeService)
    {
        _runtimeService = runtimeService;
    }

    public ProductWebsiteSettings Load(string productId)
    {
        var managedId = ManagedId(productId);
        var rule = _runtimeService.GetNginxOptions().Rules
            .Select(NginxRuntimeManager.NormalizeRule)
            .FirstOrDefault(candidate => string.Equals(candidate.ManagedWebsiteId, managedId, StringComparison.OrdinalIgnoreCase));
        return rule is null
            ? ProductWebsiteSettings.Default
            : new ProductWebsiteSettings(
                true,
                string.Join(", ", rule.ServerName.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)),
                rule.ListenPort,
                rule.SslEnabled,
                rule.HttpsPort,
                rule.SslCertificatePath,
                rule.SslCertificateKeyPath,
                rule.RedirectHttpToHttps,
                rule.MaxRateKbps);
    }

    public bool IsNginxInstalled() => FindNginxExe() is not null;

    public static async Task RemoveForProductAsync(string productId, CancellationToken cancellationToken = default)
    {
        var runtimeService = new EnvironmentRuntimeService();
        var service = new ProductWebsiteService(runtimeService);
        var managedId = ManagedId(productId);
        var current = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        var hasManagedRule = current.Rules.Any(rule =>
            string.Equals(NginxRuntimeManager.NormalizeRule(rule).ManagedWebsiteId, managedId, StringComparison.OrdinalIgnoreCase));

        // A deleted/corrupted product-state file can make Load return Default
        // while the Nginx rule and certificate directory still exist. Check the
        // persisted rule itself so uninstall remains idempotent and complete.
        // Certificate files are deliberately deleted only after the rule update
        // succeeds; otherwise a failed config save could leave Nginx referencing
        // certificate paths that MCPanel had already removed.
        if (!hasManagedRule)
        {
            DeleteManagedCertificatesFromKnownRoots(productId);
            return;
        }

        if (service.IsNginxInstalled())
        {
            await service.SaveAsync(productId, ProductWebsiteSettings.Default, cancellationToken);
            DeleteManagedCertificatesFromKnownRoots(productId);
            return;
        }

        var rules = current.Rules
            .Where(rule => !string.Equals(rule.ManagedWebsiteId, managedId, StringComparison.OrdinalIgnoreCase))
            .Select(NginxRuntimeManager.NormalizeRule)
            .ToList();
        EnsureAtLeastOneRule(rules, current);
        NginxRuntimeManager.SaveOptions(CreateOptions(current, rules));
        DeleteManagedCertificatesFromKnownRoots(productId);
    }

    public async Task<string> SaveAsync(
        string productId,
        ProductWebsiteSettings settings,
        CancellationToken cancellationToken = default)
    {
        var nginxExe = FindNginxExe()
            ?? throw new InvalidOperationException("请先到“环境”页面安装 Nginx，再配置产品域名和 SSL。");
        var current = NginxRuntimeManager.NormalizeOptions(_runtimeService.GetNginxOptions());
        var managedId = ManagedId(productId);
        var rules = current.Rules
            .Where(rule => !string.Equals(rule.ManagedWebsiteId, managedId, StringComparison.OrdinalIgnoreCase))
            .Select(NginxRuntimeManager.NormalizeRule)
            .ToList();

        if (!settings.Enabled)
        {
            EnsureAtLeastOneRule(rules, current);
            var message = await _runtimeService.SaveNginxOptionsAsync(CreateOptions(current, rules), cancellationToken);
            DeleteManagedCertificates(Path.GetDirectoryName(nginxExe)!, productId);
            var synced = await NginxProductProxyService.TrySyncAsync(cancellationToken);
            return $"已移除 {productId} 的独立域名和 SSL 配置。{message}" +
                (synced ? string.Empty : " 但自动关联产品路由未完成，请在“环境”页面重新同步 Nginx。");
        }

        var domains = NormalizeDomains(settings.Domains);
        var target = ResolveProductTarget(productId);
        var certificate = string.Empty;
        var certificateKey = string.Empty;
        if (settings.SslEnabled)
        {
            (certificate, certificateKey) = CopyManagedCertificates(
                Path.GetDirectoryName(nginxExe)!,
                productId,
                settings.CertificatePath,
                settings.CertificateKeyPath);
        }

        var primaryRule = NginxRuntimeManager.NormalizeRule(new NginxProxyRule
        {
            Enabled = true,
            Name = $"产品域名 {productId}",
            ListenPort = settings.HttpPort,
            ServerName = string.Join(" ", domains),
            LocationPath = "/",
            ProxyTarget = target,
            WebSocket = true,
            ManagedWebsiteId = managedId,
            SslEnabled = settings.SslEnabled,
            HttpsPort = settings.HttpsPort,
            SslCertificatePath = certificate,
            SslCertificateKeyPath = certificateKey,
            RedirectHttpToHttps = settings.RedirectHttpToHttps,
            MaxRateKbps = settings.MaxRateKbps
        });
        rules.Add(primaryRule);

        var upstreamPath = new Uri(target).AbsolutePath;
        if (!string.IsNullOrWhiteSpace(upstreamPath) && upstreamPath != "/")
        {
            rules.Add(NginxRuntimeManager.NormalizeRule(new NginxProxyRule
            {
                Enabled = primaryRule.Enabled,
                Name = $"产品域名兼容路径 {productId}",
                ListenPort = primaryRule.ListenPort,
                ServerName = primaryRule.ServerName,
                LocationPath = upstreamPath,
                ProxyTarget = primaryRule.ProxyTarget,
                WebSocket = primaryRule.WebSocket,
                ManagedWebsiteId = primaryRule.ManagedWebsiteId,
                SslEnabled = primaryRule.SslEnabled,
                HttpsPort = primaryRule.HttpsPort,
                SslCertificatePath = primaryRule.SslCertificatePath,
                SslCertificateKeyPath = primaryRule.SslCertificateKeyPath,
                RedirectHttpToHttps = primaryRule.RedirectHttpToHttps,
                MaxRateKbps = primaryRule.MaxRateKbps
            }));
        }

        var options = CreateOptions(current, rules);
        var result = await _runtimeService.SaveNginxOptionsAsync(options, cancellationToken);
        if (settings.SslEnabled)
        {
            CleanupManagedCertificates(Path.GetDirectoryName(nginxExe)!, productId, certificate, certificateKey);
        }
        else
        {
            DeleteManagedCertificates(Path.GetDirectoryName(nginxExe)!, productId);
        }
        var routeSynced = await NginxProductProxyService.TrySyncAsync(cancellationToken);
        var scheme = settings.SslEnabled ? "https" : "http";
        var port = settings.SslEnabled ? settings.HttpsPort : settings.HttpPort;
        var portPart = (scheme == "https" && port == 443) || (scheme == "http" && port == 80) ? string.Empty : $":{port}";
        return $"{productId} 的域名配置已生效：{scheme}://{domains[0]}{portPart}/。{result}" +
            (routeSynced ? string.Empty : " 但自动关联产品路由未完成，请在“环境”页面重新同步 Nginx。");
    }

    internal static IReadOnlyList<string> NormalizeDomains(string text)
    {
        var tokens = (text ?? string.Empty)
            .Split([',', ';', '，', '；', ' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)
            .Select(domain => domain.Trim().TrimEnd('.'))
            .Where(domain => domain.Length > 0)
            .ToArray();
        if (tokens.Length == 0)
        {
            throw new InvalidOperationException("请至少填写一个域名。");
        }

        var normalized = new List<string>();
        var idn = new IdnMapping();
        foreach (var domain in tokens)
        {
            var wildcard = domain.StartsWith("*.", StringComparison.Ordinal);
            var host = wildcard ? domain.Substring(2) : domain;
            if (domain.Contains("://", StringComparison.Ordinal) || domain.Contains('/') || domain.Contains(':') ||
                host.Length == 0)
            {
                throw new InvalidOperationException($"域名格式无效：{domain}。只需填写域名，不要包含协议、端口或路径。");
            }

            try
            {
                var asciiHost = host is "_" or "localhost" ? host : idn.GetAscii(host);
                if (asciiHost != "_" && asciiHost != "localhost" && Uri.CheckHostName(asciiHost) == UriHostNameType.Unknown)
                {
                    throw new InvalidOperationException();
                }

                normalized.Add(wildcard ? $"*.{asciiHost}" : asciiHost);
            }
            catch
            {
                throw new InvalidOperationException($"域名格式无效：{domain}。只需填写域名，不要包含协议、端口或路径。");
            }
        }

        return normalized.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static NginxRuntimeOptions CreateOptions(NginxRuntimeOptions current, List<NginxProxyRule> rules)
    {
        EnsureAtLeastOneRule(rules, current);
        return NginxRuntimeManager.NormalizeOptions(new NginxRuntimeOptions
        {
            ListenPort = current.ListenPort,
            ProxyEnabled = rules.Any(rule => rule.Enabled),
            ProxyTarget = rules.FirstOrDefault(rule => rule.Enabled)?.ProxyTarget ?? current.ProxyTarget,
            Rules = rules
        });
    }

    private static void EnsureAtLeastOneRule(List<NginxProxyRule> rules, NginxRuntimeOptions current)
    {
        if (rules.Count == 0)
        {
            rules.Add(NginxRuntimeManager.CreateDefaultRule(current.ListenPort, current.ProxyTarget, current.ProxyEnabled));
        }
    }

    private static string ResolveProductTarget(string productId)
    {
        var tomcat = ProductDeploymentService.LoadTomcatDeploymentInfo(productId);
        if (tomcat is not null)
        {
            if (tomcat.Port is <= 0 or > 65535)
            {
                throw new InvalidOperationException(
                    $"{productId} 的 Tomcat 端口配置无效，请先在产品管理中点击启动以自动修复绑定。");
            }

            return $"http://127.0.0.1:{tomcat.Port}/{tomcat.ProductId}/";
        }

        var iis = ProductDeploymentService.LoadIisDeploymentInfo(productId)
            ?? throw new InvalidOperationException($"未找到 {productId} 的 IIS 或 Tomcat 部署信息，请先修复产品绑定。");
        if (iis.Port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"{productId} 的 IIS 端口配置无效，请先修复产品绑定。");
        }

        return $"http://127.0.0.1:{iis.Port}{iis.ApplicationPath.TrimEnd('/')}/";
    }

    private static (string Certificate, string Key) CopyManagedCertificates(
        string nginxRoot,
        string productId,
        string certificateSource,
        string keySource)
    {
        if (!File.Exists(certificateSource))
        {
            throw new FileNotFoundException("SSL 证书文件不存在。", certificateSource);
        }

        if (!File.Exists(keySource))
        {
            throw new FileNotFoundException("SSL 私钥文件不存在。", keySource);
        }

        var directory = Path.Combine(nginxRoot, "conf", "mcpanel-ssl", SafeName(productId));
        Directory.CreateDirectory(directory);
        var certificateTarget = Path.Combine(directory, $"certificate-{FileHash(certificateSource)}.pem");
        var keyTarget = Path.Combine(directory, $"private-key-{FileHash(keySource)}.pem");
        CopyIfDifferent(certificateSource, certificateTarget);
        CopyIfDifferent(keySource, keyTarget);
        return (certificateTarget, keyTarget);
    }

    private static string FileHash(string path)
    {
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).Substring(0, 16).ToLowerInvariant();
    }

    private static void CopyIfDifferent(string source, string target)
    {
        if (Path.GetFullPath(source).Equals(Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        File.Copy(source, target, overwrite: true);
    }

    private static void DeleteManagedCertificates(string nginxRoot, string productId)
    {
        try
        {
            var directory = Path.Combine(nginxRoot, "conf", "mcpanel-ssl", SafeName(productId));
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
                }

                foreach (var child in Directory.EnumerateDirectories(directory, "*", SearchOption.AllDirectories))
                {
                    try { File.SetAttributes(child, FileAttributes.Normal); } catch { }
                }

                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // A stale certificate copy does not make a successfully removed rule fail.
        }
    }

    private static void DeleteManagedCertificatesFromKnownRoots(string productId)
    {
        var roots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locator = new ComponentLocator();
        foreach (var root in ComponentPaths.NginxSearchRoots)
        {
            roots.Add(root);
            var executable = locator.FindNginxExecutable(root);
            if (!string.IsNullOrWhiteSpace(executable))
            {
                roots.Add(Path.GetDirectoryName(executable!)!);
            }
        }

        foreach (var root in roots)
        {
            DeleteManagedCertificates(root, productId);
            var remaining = Path.Combine(root, "conf", "mcpanel-ssl", SafeName(productId));
            if (Directory.Exists(remaining))
            {
                throw new IOException($"无法删除产品 {productId} 的 Nginx 证书目录：{remaining}");
            }
        }
    }

    private static void CleanupManagedCertificates(string nginxRoot, string productId, params string[] activeFiles)
    {
        try
        {
            var directory = Path.Combine(nginxRoot, "conf", "mcpanel-ssl", SafeName(productId));
            if (!Directory.Exists(directory)) return;
            var keep = activeFiles.Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly))
            {
                if (!keep.Contains(Path.GetFullPath(file))) File.Delete(file);
            }
        }
        catch
        {
            // Old certificate copies can be cleaned on the next successful save.
        }
    }

    private static string? FindNginxExe() => new ComponentLocator().FindNginxExecutable();

    private static string ManagedId(string productId) => $"product-domain:{SafeName(productId)}";

    private static string SafeName(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray()).Trim('_');
        return safe.Length == 0 ? "product" : safe;
    }
}