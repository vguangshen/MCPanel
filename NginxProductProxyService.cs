using System.IO;
using System.Text;

namespace MCPanel;

/// <summary>
/// Keeps the unified Nginx listener associated with the IIS and Tomcat products
/// that are deployed by MCPanel. Product rules are generated from deployment
/// state; rules created by an administrator remain untouched.
/// </summary>
public static class NginxProductProxyService
{
    private static readonly SemaphoreSlim SyncLock = new(1, 1);

    private sealed record ProductRoute(string ProductId, string LocationPath, int Port);

    public static async Task<bool> TrySyncAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await SyncAsync(cancellationToken);
            return true;
        }
        catch (OperationCanceledException ex)
        {
            WriteSyncLog("自动关联 Nginx 产品端口时被取消，已保留当前产品部署结果。", ex);
            return false;
        }
        catch (Exception ex)
        {
            WriteSyncLog("自动关联 Nginx 产品端口失败。", ex);
            return false;
        }
    }

    public static async Task SyncAsync(CancellationToken cancellationToken = default)
    {
        await SyncLock.WaitAsync(cancellationToken);
        try
        {
            if (new ComponentLocator().FindNginxExecutable() is null)
            {
                return;
            }

            var runtimeService = new EnvironmentRuntimeService();
            var current = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
            var publicPort = IsValidPort(current.ListenPort)
                ? current.ListenPort
                : NginxRuntimeManager.DefaultListenPort;

            var manualRules = current.Rules
                .Select(NginxRuntimeManager.NormalizeRule)
                .Where(rule => string.IsNullOrWhiteSpace(rule.ManagedProductId))
                .ToList();
            var generatedRules = BuildProductRules(publicPort, manualRules);
            var mergedRules = manualRules.Concat(generatedRules).ToList();

            if (mergedRules.Count == 0)
            {
                mergedRules.Add(NginxRuntimeManager.CreateDefaultRule(
                    publicPort,
                    "http://127.0.0.1:9287",
                    current.ProxyEnabled));
            }

            var merged = NginxRuntimeManager.NormalizeOptions(new NginxRuntimeOptions
            {
                ListenPort = publicPort,
                ProxyEnabled = mergedRules.Any(rule => rule.Enabled),
                ProxyTarget = mergedRules.FirstOrDefault(rule => rule.Enabled)?.ProxyTarget
                    ?? "http://127.0.0.1:9287",
                Rules = mergedRules
            });

            if (AreEquivalent(current, merged))
            {
                return;
            }

            await runtimeService.SaveNginxOptionsAsync(merged, cancellationToken);
        }
        finally
        {
            SyncLock.Release();
        }
    }

    private static IReadOnlyList<NginxProxyRule> BuildProductRules(
        int publicPort,
        IReadOnlyCollection<NginxProxyRule> manualRules)
    {
        var manualKeys = manualRules
            .Where(rule => rule.Enabled)
            .Select(GetRuleKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var generatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var rules = new List<NginxProxyRule>();

        foreach (var route in LoadProductRoutes())
        {
            var rule = new NginxProxyRule
            {
                Enabled = true,
                Name = $"产品 {route.ProductId}",
                ListenPort = publicPort,
                ServerName = "localhost",
                LocationPath = route.LocationPath,
                ProxyTarget = $"http://127.0.0.1:{route.Port}{route.LocationPath}",
                WebSocket = true,
                ManagedProductId = route.ProductId
            };
            var key = GetRuleKey(rule);

            if (manualKeys.Contains(key))
            {
                WriteSyncLog($"跳过产品 {route.ProductId} 的自动规则：管理员规则已占用 {route.LocationPath}。", null);
                continue;
            }

            if (generatedKeys.Add(key))
            {
                rules.Add(rule);
            }
        }

        return rules;
    }

    private static IEnumerable<ProductRoute> LoadProductRoutes()
    {
        var routes = ProductDeploymentService.LoadIisDeploymentInfos()
            .Select(info => CreateRoute(info.ProductId, info.ApplicationPath, info.Port))
            .Concat(ProductDeploymentService.LoadTomcatDeploymentInfos()
                .Select(info => CreateRoute(info.ProductId, info.ContextPath, info.Port)))
            .Where(route => route is not null)
            .Cast<ProductRoute>()
            .GroupBy(route => route.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(route => route.Port)
            .ThenBy(route => route.ProductId, StringComparer.OrdinalIgnoreCase);

        return routes;
    }

    private static ProductRoute? CreateRoute(string productId, string locationPath, int port)
    {
        if (string.IsNullOrWhiteSpace(productId) || !IsValidPort(port))
        {
            return null;
        }

        var normalizedPath = NormalizeLocationPath(locationPath);
        if (normalizedPath.Length <= 1)
        {
            WriteSyncLog($"跳过产品 {productId} 的自动规则：部署路径无效。", null);
            return null;
        }

        return new ProductRoute(productId.Trim(), normalizedPath, port);
    }

    private static bool AreEquivalent(NginxRuntimeOptions left, NginxRuntimeOptions right)
    {
        if (left.ListenPort != right.ListenPort ||
            left.ProxyEnabled != right.ProxyEnabled ||
            !string.Equals(left.ProxyTarget, right.ProxyTarget, StringComparison.OrdinalIgnoreCase) ||
            left.Rules.Count != right.Rules.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Rules.Count; index++)
        {
            var a = NginxRuntimeManager.NormalizeRule(left.Rules[index]);
            var b = NginxRuntimeManager.NormalizeRule(right.Rules[index]);
            if (a.Enabled != b.Enabled ||
                a.ListenPort != b.ListenPort ||
                a.WebSocket != b.WebSocket ||
                !string.Equals(a.Name, b.Name, StringComparison.Ordinal) ||
                !string.Equals(a.ServerName, b.ServerName, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.LocationPath, b.LocationPath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.ProxyTarget, b.ProxyTarget, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.ManagedProductId, b.ManagedProductId, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.ManagedWebsiteId, b.ManagedWebsiteId, StringComparison.OrdinalIgnoreCase) ||
                a.SslEnabled != b.SslEnabled ||
                a.HttpsPort != b.HttpsPort ||
                !string.Equals(a.SslCertificatePath, b.SslCertificatePath, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(a.SslCertificateKeyPath, b.SslCertificateKeyPath, StringComparison.OrdinalIgnoreCase) ||
                a.RedirectHttpToHttps != b.RedirectHttpToHttps ||
                a.MaxRateKbps != b.MaxRateKbps)
            {
                return false;
            }
        }

        return true;
    }

    private static string GetRuleKey(NginxProxyRule rule)
    {
        var normalized = NginxRuntimeManager.NormalizeRule(rule);
        return $"{normalized.ListenPort}|{normalized.ServerName.ToLowerInvariant()}|{normalized.LocationPath.ToLowerInvariant()}";
    }

    private static string NormalizeLocationPath(string? value)
    {
        var path = (value ?? string.Empty).Trim().Replace('\\', '/');
        if (path.Length == 0)
        {
            return string.Empty;
        }

        if (!path.StartsWith('/'))
        {
            path = "/" + path;
        }

        path = path.TrimEnd('/');
        if (path.Length == 0)
        {
            return "/";
        }

        return path.IndexOfAny([';', '{', '}', '\r', '\n', '\t', ' ']) >= 0
            ? string.Empty
            : path;
    }

    private static bool IsValidPort(int port) => port is > 0 and <= 65535;

    private static void WriteSyncLog(string message, Exception? exception)
    {
        try
        {
            var workDirectory = ComponentPaths.WorkRoot;
            var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            RollingLogWriter.Append(
                Path.Combine(workDirectory, "nginx-product-sync.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{detail}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // A proxy-sync diagnostic must never interrupt product installation.
        }
    }
}
