using System.IO;
using System.Text;

namespace MCPanel;

/// <summary>
/// Keeps the unified Nginx listener associated with the IIS and Tomcat products
/// that are deployed by MCPanel. Automatically generated product rules continue
/// to follow deployment state, while rules explicitly edited or removed by an
/// administrator are preserved until that product is uninstalled.
/// </summary>
public static class NginxProductProxyService
{
    private const string UserOverridePrefix = "__mcpanel_user_override__:";
    private const string SuppressedPrefix = "__mcpanel_suppressed__:";

    internal enum ManagedProductRuleKind
    {
        Automatic,
        UserOverride,
        Suppressed
    }

    internal sealed record ProductRoute(string ProductId, string LocationPath, int Port);

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

    public static Task SyncAsync(CancellationToken cancellationToken = default) =>
        NginxConfigurationCoordinator.RunAsync(() => SyncCoreAsync(cancellationToken), cancellationToken);

    private static async Task SyncCoreAsync(CancellationToken cancellationToken)
    {
        if (new ComponentLocator().FindNginxExecutable() is null)
        {
            return;
        }

        var runtimeService = new EnvironmentRuntimeService();
        var current = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        var defaultPublicPort = ResolveDefaultPublicPort(current);
        var generatedRules = BuildProductRules(defaultPublicPort, current.Rules, LoadProductRoutes());
        var mergedRules = MergeProductRules(current.Rules, generatedRules);

        if (mergedRules.Count == 0)
        {
            mergedRules.Add(NginxRuntimeManager.CreateDefaultRule(
                defaultPublicPort,
                "http://127.0.0.1:9287",
                current.ProxyEnabled));
        }

        var merged = NginxRuntimeManager.NormalizeOptions(new NginxRuntimeOptions
        {
            ListenPort = defaultPublicPort,
            ProxyEnabled = mergedRules.Any(rule => rule.Enabled),
            ProxyTarget = mergedRules.FirstOrDefault(rule => rule.Enabled)?.ProxyTarget
                ?? current.ProxyTarget,
            Rules = mergedRules
        });

        if (AreEquivalent(current, merged))
        {
            return;
        }

        await runtimeService.SaveNginxOptionsAsync(merged, cancellationToken);
    }

    internal static int ResolveDefaultPublicPort(NginxRuntimeOptions current)
    {
        var rules = current.Rules.Select(NginxRuntimeManager.NormalizeRule).ToArray();

        // Prefer an untouched generated rule. This prevents changing the listen
        // port of one user-overridden product from silently moving every other
        // generated product to that port on the next synchronization.
        foreach (var rule in rules)
        {
            if (!rule.Enabled || !IsValidPort(rule.ListenPort))
            {
                continue;
            }

            if (TryParseManagedProductId(rule.ManagedProductId, out _, out var kind) &&
                kind == ManagedProductRuleKind.Automatic)
            {
                return rule.ListenPort;
            }
        }

        // A plain administrator rule is a safer fallback than a product-domain
        // rule because product-domain rules intentionally use independent ports.
        foreach (var rule in rules)
        {
            if (rule.Enabled &&
                IsValidPort(rule.ListenPort) &&
                string.IsNullOrWhiteSpace(rule.ManagedProductId) &&
                string.IsNullOrWhiteSpace(rule.ManagedWebsiteId))
            {
                return rule.ListenPort;
            }
        }

        return IsValidPort(current.ListenPort)
            ? current.ListenPort
            : NginxRuntimeManager.DefaultListenPort;
    }

    internal static IReadOnlyList<NginxProxyRule> BuildProductRules(
        int defaultPublicPort,
        IReadOnlyCollection<NginxProxyRule> currentRules,
        IEnumerable<ProductRoute> routes)
    {
        var existingAutomaticPorts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var currentRule in currentRules.Select(NginxRuntimeManager.NormalizeRule))
        {
            if (!TryParseManagedProductId(currentRule.ManagedProductId, out var productId, out var kind) ||
                kind != ManagedProductRuleKind.Automatic ||
                !IsValidPort(currentRule.ListenPort))
            {
                continue;
            }

            if (!existingAutomaticPorts.ContainsKey(productId))
            {
                existingAutomaticPorts[productId] = currentRule.ListenPort;
            }
        }

        var rules = new List<NginxProxyRule>();
        foreach (var route in routes)
        {
            var listenPort = existingAutomaticPorts.TryGetValue(route.ProductId, out var existingPort)
                ? existingPort
                : defaultPublicPort;
            rules.Add(NginxRuntimeManager.NormalizeRule(new NginxProxyRule
            {
                Enabled = true,
                Name = $"产品 {route.ProductId}",
                ListenPort = listenPort,
                ServerName = "localhost",
                LocationPath = route.LocationPath,
                ProxyTarget = $"http://127.0.0.1:{route.Port}{route.LocationPath}",
                WebSocket = true,
                ManagedProductId = route.ProductId
            }));
        }

        return rules;
    }

    internal static List<NginxProxyRule> MergeProductRules(
        IReadOnlyCollection<NginxProxyRule> currentRules,
        IReadOnlyCollection<NginxProxyRule> generatedRules)
    {
        var normalizedCurrent = currentRules.Select(NginxRuntimeManager.NormalizeRule).ToArray();
        var normalizedGenerated = generatedRules.Select(NginxRuntimeManager.NormalizeRule).ToArray();

        var activeProductIds = normalizedGenerated
            .Select(rule => TryParseManagedProductId(rule.ManagedProductId, out var productId, out var kind) &&
                            kind == ManagedProductRuleKind.Automatic
                ? productId
                : null)
            .Where(productId => !string.IsNullOrWhiteSpace(productId))
            .Cast<string>()
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var manualRules = normalizedCurrent
            .Where(rule => string.IsNullOrWhiteSpace(rule.ManagedProductId))
            .ToList();
        var manualKeys = manualRules
            .Where(rule => rule.Enabled)
            .Select(GetRuleKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var overrides = new Dictionary<string, NginxProxyRule>(StringComparer.OrdinalIgnoreCase);
        foreach (var rule in normalizedCurrent)
        {
            if (!TryParseManagedProductId(rule.ManagedProductId, out var productId, out var kind) ||
                kind == ManagedProductRuleKind.Automatic ||
                !activeProductIds.Contains(productId))
            {
                continue;
            }

            // The latest persisted override wins if an older build accidentally
            // left more than one marker for the same product.
            overrides[productId] = rule;
        }

        var merged = new List<NginxProxyRule>(manualRules);
        var generatedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var generated in normalizedGenerated)
        {
            if (!TryParseManagedProductId(generated.ManagedProductId, out var productId, out var kind) ||
                kind != ManagedProductRuleKind.Automatic)
            {
                continue;
            }

            if (overrides.TryGetValue(productId, out var administratorRule))
            {
                merged.Add(administratorRule);
                continue;
            }

            var key = GetRuleKey(generated);
            if (manualKeys.Contains(key))
            {
                WriteSyncLog($"跳过产品 {productId} 的自动规则：管理员规则已占用 {generated.LocationPath}。", null);
                continue;
            }

            if (generatedKeys.Add(key))
            {
                merged.Add(generated);
            }
        }

        // Automatic rules and administrator markers for products that no longer
        // have deployment state are deliberately omitted here. This makes product
        // uninstall clean both ordinary overrides and hidden suppression markers.
        return merged;
    }

    internal static bool TryParseManagedProductId(
        string? managedProductId,
        out string productId,
        out ManagedProductRuleKind kind)
    {
        productId = string.Empty;
        kind = ManagedProductRuleKind.Automatic;
        var value = managedProductId?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        if (value.StartsWith(UserOverridePrefix, StringComparison.OrdinalIgnoreCase))
        {
            productId = value.Substring(UserOverridePrefix.Length).Trim();
            kind = ManagedProductRuleKind.UserOverride;
            return productId.Length > 0;
        }

        if (value.StartsWith(SuppressedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            productId = value.Substring(SuppressedPrefix.Length).Trim();
            kind = ManagedProductRuleKind.Suppressed;
            return productId.Length > 0;
        }

        productId = value;
        return true;
    }

    internal static string CreateUserOverrideId(string productId) =>
        UserOverridePrefix + NormalizeProductId(productId);

    internal static string CreateSuppressedId(string productId) =>
        SuppressedPrefix + NormalizeProductId(productId);

    internal static bool IsSuppressedManagedProductId(string? managedProductId) =>
        TryParseManagedProductId(managedProductId, out _, out var kind) &&
        kind == ManagedProductRuleKind.Suppressed;

    private static string NormalizeProductId(string productId)
    {
        var normalized = (productId ?? string.Empty).Trim();
        if (normalized.Length == 0)
        {
            throw new InvalidOperationException("Nginx 产品规则缺少产品编号。");
        }

        return normalized;
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
