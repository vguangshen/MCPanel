$ErrorActionPreference = 'Stop'

function Read-Utf8([string]$Path) {
    return [IO.File]::ReadAllText((Join-Path $PWD $Path), [Text.UTF8Encoding]::new($false))
}

function Write-Utf8([string]$Path, [string]$Text) {
    [IO.File]::WriteAllText((Join-Path $PWD $Path), $Text, [Text.UTF8Encoding]::new($false))
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Utf8 $Path
    if (-not $text.Contains($Old)) {
        throw "Expected block was not found in $Path.`n--- expected ---`n$Old"
    }
    Write-Utf8 $Path ($text.Replace($Old, $New))
}

# ProductDeploymentService becomes partial so runtime-truth reconciliation can live
# in a focused source file rather than expanding the already large deployment file.
Replace-Exact 'ProductDeploymentService.cs' `
    'public sealed class ProductDeploymentService' `
    'public sealed partial class ProductDeploymentService'

Replace-Exact 'ProductDeploymentService.cs' @'
    public static IisProductDeploymentInfo? LoadIisDeploymentInfo(string productId)
    {
        var file = GetIisStateFile(SafeName(productId));
'@ @'
    public static IisProductDeploymentInfo? LoadIisDeploymentInfo(string productId)
    {
        ReconcileRuntimeDeploymentState();
        var file = GetIisStateFile(SafeName(productId));
'@

Replace-Exact 'ProductDeploymentService.cs' @'
    public static IReadOnlyList<IisProductDeploymentInfo> LoadIisDeploymentInfos()
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
'@ @'
    public static IReadOnlyList<IisProductDeploymentInfo> LoadIisDeploymentInfos()
    {
        ReconcileRuntimeDeploymentState();
        var stateDirectory = ComponentPaths.ProductStateRoot;
'@

Replace-Exact 'ProductDeploymentService.cs' @'
    public static TomcatProductDeploymentInfo? LoadTomcatDeploymentInfo(string productId)
    {
        var safeName = SafeName(productId);
'@ @'
    public static TomcatProductDeploymentInfo? LoadTomcatDeploymentInfo(string productId)
    {
        ReconcileRuntimeDeploymentState();
        var safeName = SafeName(productId);
'@

Replace-Exact 'ProductDeploymentService.cs' @'
    public static IReadOnlyList<TomcatProductDeploymentInfo> LoadTomcatDeploymentInfos()
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
'@ @'
    public static IReadOnlyList<TomcatProductDeploymentInfo> LoadTomcatDeploymentInfos()
    {
        ReconcileRuntimeDeploymentState();
        var stateDirectory = ComponentPaths.ProductStateRoot;
'@

Replace-Exact 'ProductDeploymentService.cs' @'
    internal static int SelectCanonicalTomcatRuntimePort(
        int persistedPort,
        int? configuredPort,
        IEnumerable<int> generatedInstancePorts)
    {
        if (configuredPort.HasValue && IsValidTomcatProductPort(configuredPort.Value))
        {
            return configuredPort.Value;
        }

        if (IsValidTomcatProductPort(persistedPort))
        {
            return persistedPort;
        }

        return generatedInstancePorts.FirstOrDefault(IsValidTomcatProductPort);
    }
'@ @'
    internal static int SelectCanonicalTomcatRuntimePort(
        int persistedPort,
        int? configuredPort,
        IEnumerable<int> generatedInstancePorts)
    {
        // The configured Connector is runtime truth, even when an administrator
        // deliberately moves it outside MCPanel's 9000-10000 auto-allocation pool.
        if (configuredPort.HasValue && IsValidObservedRuntimePort(configuredPort.Value))
        {
            return configuredPort.Value;
        }

        if (IsValidObservedRuntimePort(persistedPort))
        {
            return persistedPort;
        }

        return generatedInstancePorts.FirstOrDefault(IsValidObservedRuntimePort);
    }
'@

Replace-Exact 'ProductDeploymentService.cs' @'
            var port = TomcatRuntimeProbe
                .ReadHttpPorts(Path.GetDirectoryName(Path.GetDirectoryName(serverXml)!)!)
                .FirstOrDefault(IsValidTomcatProductPort);
            return IsValidTomcatProductPort(port) ? port : null;
'@ @'
            var port = TomcatRuntimeProbe
                .ReadHttpPorts(Path.GetDirectoryName(Path.GetDirectoryName(serverXml)!)!)
                .FirstOrDefault(IsValidObservedRuntimePort);
            return IsValidObservedRuntimePort(port) ? port : null;
'@

Replace-Exact 'ProductDeploymentService.cs' @'
            int? preferredPort = IsValidTomcatProductPort(deployment.Port)
                ? deployment.Port
                : null;
'@ @'
            int? preferredPort = IsValidObservedRuntimePort(deployment.Port)
                ? deployment.Port
                : null;
'@

Replace-Exact 'ProductDeploymentService.cs' @'
        if (!IsValidTomcatProductPort(deployment.Port))
        {
            throw new InvalidDataException(
                $"不能为 {deployment.ProductId} 写入无效的 Tomcat 产品端口 {deployment.Port}。");
        }
'@ @'
        if (!IsValidObservedRuntimePort(deployment.Port))
        {
            throw new InvalidDataException(
                $"不能为 {deployment.ProductId} 写入无效的 Tomcat 产品端口 {deployment.Port}。");
        }
'@

Replace-Exact 'ProductDeploymentService.cs' @'
                IsValidTomcatProductPort(port))
            {
                return new TomcatServiceMatch(service, port);
'@ @'
                IsValidObservedRuntimePort(port))
            {
                return new TomcatServiceMatch(service, port);
'@

Replace-Exact 'ProductDeploymentService.cs' @'
                    if (state?.Port is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum &&
                        (Directory.Exists(state.PhysicalPath) || File.Exists(state.PhysicalPath)))
                    {
                        configuredPorts.Add(state.Port);
                    }
'@ @'
                    if (state is not null && IsValidObservedRuntimePort(state.Port) &&
                        (Directory.Exists(state.PhysicalPath) || File.Exists(state.PhysicalPath)))
                    {
                        configuredPorts.Add(state.Port);
                    }
'@

Replace-Exact 'ProductDeploymentService.cs' @'
        if (preferredPort is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum &&
            !configuredPorts.Contains(preferredPort.Value) &&
            (allowActivePreferredPort || !activePorts.Contains(preferredPort.Value)))
        {
            return preferredPort.Value;
        }
'@ @'
        if (preferredPort.HasValue && IsValidObservedRuntimePort(preferredPort.Value) &&
            !configuredPorts.Contains(preferredPort.Value) &&
            (allowActivePreferredPort || !activePorts.Contains(preferredPort.Value)))
        {
            return preferredPort.Value;
        }
'@

Replace-Exact 'ProductDeploymentService.cs' @'
    internal static bool IsValidTomcatProductPort(int port) =>
        port is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum;
'@ @'
    internal static bool IsValidTomcatProductPort(int port) =>
        port is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum;

    // Auto-allocation intentionally stays in 9000-10000, but runtime discovery
    // must respect any valid TCP port an administrator configured by hand.
    internal static bool IsValidObservedRuntimePort(int port) => port is > 0 and <= 65535;
'@

$runtimeSync = @'
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed partial class ProductDeploymentService
{
    private static readonly object RuntimeBindingReconcileGate = new();
    private static DateTime RuntimeBindingReconcileNextUtc = DateTime.MinValue;
    private static readonly TimeSpan RuntimeBindingReconcileCache = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Reconciles MCPanel's deployment-state cache with the real IIS/Tomcat
    /// configuration. The runtime configuration is authoritative; state JSON is
    /// only a cache used by UI and routing code.
    /// </summary>
    public static IReadOnlyCollection<string> ReconcileRuntimeDeploymentState(bool force = false)
    {
        lock (RuntimeBindingReconcileGate)
        {
            var now = DateTime.UtcNow;
            if (!force && now < RuntimeBindingReconcileNextUtc)
            {
                return Array.Empty<string>();
            }

            RuntimeBindingReconcileNextUtc = now + RuntimeBindingReconcileCache;
            var changed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                ReconcileIisDeploymentState(changed);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog("回读 IIS 实际绑定失败，保留现有缓存并等待下次同步。", ex);
            }

            try
            {
                ReconcileTomcatDeploymentState(changed);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog("回读 Tomcat 实际端口失败，保留现有缓存并等待下次同步。", ex);
            }

            return changed.OrderBy(id => id, StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }

    private static void ReconcileIisDeploymentState(HashSet<string> changed)
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory)) return;

        var appcmd = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appcmd)) return;

        var portsBySite = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".tomcat.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var info = JsonSerializer.Deserialize<IisProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is null || !Directory.Exists(info.PhysicalPath) || string.IsNullOrWhiteSpace(info.SiteName))
                {
                    continue;
                }

                if (!portsBySite.TryGetValue(info.SiteName, out var actualPort))
                {
                    actualPort = TryReadIisSiteHttpPort(appcmd, info.SiteName);
                    portsBySite[info.SiteName] = actualPort;
                }

                if (!actualPort.HasValue || !IsValidObservedRuntimePort(actualPort.Value) || actualPort.Value == info.Port)
                {
                    continue;
                }

                var updated = info with
                {
                    Port = actualPort.Value,
                    Url = $"http://localhost:{actualPort.Value}{info.ApplicationPath.TrimEnd('/')}/"
                };
                AtomicFile.WriteAllText(
                    stateFile,
                    JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                changed.Add(updated.ProductId);
                WriteRuntimeBindingSyncLog(
                    $"检测到 IIS 外部端口变更：{updated.ProductId} {info.Port} -> {updated.Port}，已同步状态缓存。",
                    null);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog($"同步 IIS 状态文件失败：{stateFile}", ex);
            }
        }
    }

    private static void ReconcileTomcatDeploymentState(HashSet<string> changed)
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory)) return;

        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.tomcat.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is null || (!Directory.Exists(info.PhysicalPath) && !File.Exists(info.PhysicalPath)))
                {
                    continue;
                }

                var actual = ResolveTomcatRuntimeInfo(info);
                if (!IsValidObservedRuntimePort(actual.Port) ||
                    (actual.Port == info.Port && string.Equals(actual.Url, info.Url, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                AtomicFile.WriteAllText(
                    stateFile,
                    JsonSerializer.Serialize(actual, new JsonSerializerOptions { WriteIndented = true }),
                    new UTF8Encoding(false));
                changed.Add(actual.ProductId);
                WriteRuntimeBindingSyncLog(
                    $"检测到 Tomcat 外部端口变更：{actual.ProductId} {info.Port} -> {actual.Port}，已同步状态缓存。",
                    null);
            }
            catch (Exception ex)
            {
                WriteRuntimeBindingSyncLog($"同步 Tomcat 状态文件失败：{stateFile}", ex);
            }
        }
    }

    private static int? TryReadIisSiteHttpPort(string appcmd, string siteName)
    {
        try
        {
            var result = ProcessRunner.RunSynchronously(
                appcmd,
                $"list site {Compat.QuoteCommandLineArgument(siteName)} /text:bindings",
                Path.GetDirectoryName(appcmd),
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3));
            return result.ExitCode == 0 ? ParseIisHttpBindingPort(result.StandardOutput) : null;
        }
        catch
        {
            return null;
        }
    }

    internal static int? ParseIisHttpBindingPort(string? bindings)
    {
        if (string.IsNullOrWhiteSpace(bindings)) return null;
        var matches = Regex.Matches(
            bindings,
            @"(?i)(?:^|,)\s*http/[^,]*?:(?<port>\d+):[^,]*");
        foreach (Match match in matches)
        {
            if (int.TryParse(match.Groups["port"].Value, out var port) && IsValidObservedRuntimePort(port))
            {
                return port;
            }
        }

        return null;
    }

    private static void WriteRuntimeBindingSyncLog(string message, Exception? exception)
    {
        try
        {
            var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            RollingLogWriter.Append(
                Path.Combine(ComponentPaths.WorkRoot, "runtime-binding-sync.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{detail}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Passive reconciliation must never interrupt normal panel work.
        }
    }
}
'@
Write-Utf8 'ProductDeploymentService.RuntimeSync.cs' $runtimeSync

# Reuse the same binding parser in SelectIisPort so creation and passive discovery
# interpret IIS bindings identically.
Replace-Exact 'ProductDeploymentService.cs' @'
            if (result.ExitCode == 0)
            {
                var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"http/[^:]*:(?<port>\d+):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out var existingPort))
                {
                    return existingPort;
                }
            }
'@ @'
            if (result.ExitCode == 0)
            {
                var existingPort = ParseIisHttpBindingPort(result.StandardOutput);
                if (existingPort.HasValue)
                {
                    return existingPort.Value;
                }
            }
'@

# Nginx automatic product rules already compare before saving; add managed
# product-domain/SSL backend rebinding without touching administrator rules.
Replace-Exact 'NginxProductProxyService.cs' @'
        var runtimeService = new EnvironmentRuntimeService();
        var current = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        var defaultPublicPort = ResolveDefaultPublicPort(current);
        var generatedRules = BuildProductRules(defaultPublicPort, current.Rules, LoadProductRoutes());
        var mergedRules = MergeProductRules(current.Rules, generatedRules);
'@ @'
        var runtimeService = new EnvironmentRuntimeService();
        var current = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        var routes = LoadProductRoutes().ToArray();
        var synchronizedRules = RebindManagedWebsiteTargets(current.Rules, routes);
        var defaultPublicPort = ResolveDefaultPublicPort(current);
        var generatedRules = BuildProductRules(defaultPublicPort, synchronizedRules, routes);
        var mergedRules = MergeProductRules(synchronizedRules, generatedRules);
'@

Replace-Exact 'NginxProductProxyService.cs' @'
    private const string UserOverridePrefix = "__mcpanel_user_override__:";
    private const string SuppressedPrefix = "__mcpanel_suppressed__:";
'@ @'
    private const string UserOverridePrefix = "__mcpanel_user_override__:";
    private const string SuppressedPrefix = "__mcpanel_suppressed__:";
    private const string ManagedWebsitePrefix = "product-domain:";
'@

$nginxPath = 'NginxProductProxyService.cs'
$nginxText = Read-Utf8 $nginxPath
$marker = '    internal static IReadOnlyList<NginxProxyRule> BuildProductRules('
$insertAt = $nginxText.IndexOf($marker, [StringComparison]::Ordinal)
if ($insertAt -lt 0) { throw 'BuildProductRules marker was not found.' }
$managedWebsiteSync = @'
    internal static IReadOnlyList<NginxProxyRule> RebindManagedWebsiteTargets(
        IReadOnlyCollection<NginxProxyRule> currentRules,
        IReadOnlyCollection<ProductRoute> routes)
    {
        var routesById = routes
            .GroupBy(route => NormalizeManagedWebsiteProductId(route.ProductId), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Key.Length > 0)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var result = new List<NginxProxyRule>(currentRules.Count);
        foreach (var source in currentRules)
        {
            var rule = NginxRuntimeManager.NormalizeRule(source);
            var managedWebsiteId = rule.ManagedWebsiteId?.Trim() ?? string.Empty;
            if (!managedWebsiteId.StartsWith(ManagedWebsitePrefix, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rule);
                continue;
            }

            var productKey = NormalizeManagedWebsiteProductId(managedWebsiteId.Substring(ManagedWebsitePrefix.Length));
            if (!routesById.TryGetValue(productKey, out var route))
            {
                result.Add(rule);
                continue;
            }

            var target = $"http://127.0.0.1:{route.Port}{route.LocationPath.TrimEnd('/')}/";
            if (string.Equals(rule.ProxyTarget, target, StringComparison.OrdinalIgnoreCase))
            {
                result.Add(rule);
                continue;
            }

            result.Add(NginxRuntimeManager.NormalizeRule(new NginxProxyRule
            {
                Enabled = rule.Enabled,
                Name = rule.Name,
                ListenPort = rule.ListenPort,
                ServerName = rule.ServerName,
                LocationPath = rule.LocationPath,
                ProxyTarget = target,
                WebSocket = rule.WebSocket,
                ManagedProductId = rule.ManagedProductId,
                ManagedWebsiteId = rule.ManagedWebsiteId,
                SslEnabled = rule.SslEnabled,
                HttpsPort = rule.HttpsPort,
                SslCertificatePath = rule.SslCertificatePath,
                SslCertificateKeyPath = rule.SslCertificateKeyPath,
                RedirectHttpToHttps = rule.RedirectHttpToHttps,
                MaxRateKbps = rule.MaxRateKbps
            }));
        }

        return result;
    }

    private static string NormalizeManagedWebsiteProductId(string value)
    {
        var safe = new string((value ?? string.Empty)
            .Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_')
            .ToArray()).Trim('_');
        return safe;
    }

'@
$nginxText = $nginxText.Insert($insertAt, $managedWebsiteSync)
Write-Utf8 $nginxPath $nginxText

# Background synchronization continues when the window is hidden in the tray.
Replace-Exact 'MainWindow.xaml.cs' @'
    private static readonly TimeSpan EnvironmentRuntimeRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackgroundRuntimeRefreshInterval = TimeSpan.FromSeconds(20);
'@ @'
    private static readonly TimeSpan EnvironmentRuntimeRefreshInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan BackgroundRuntimeRefreshInterval = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan ProductRouteForegroundSyncInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProductRouteBackgroundSyncInterval = TimeSpan.FromSeconds(30);
'@

Replace-Exact 'MainWindow.xaml.cs' @'
    private bool _runtimeRefreshInFlight;
    private DateTime _nextRuntimeRefreshUtc = DateTime.MinValue;
'@ @'
    private bool _runtimeRefreshInFlight;
    private DateTime _nextRuntimeRefreshUtc = DateTime.MinValue;
    private bool _productRouteSyncInFlight;
    private DateTime _nextProductRouteSyncUtc = DateTime.MinValue;
'@

Replace-Exact 'MainWindow.xaml.cs' @'
        Activated += (_, _) =>
        {
            _nextRuntimeRefreshUtc = DateTime.MinValue;
'@ @'
        Activated += (_, _) =>
        {
            _nextRuntimeRefreshUtc = DateTime.MinValue;
            _nextProductRouteSyncUtc = DateTime.MinValue;
'@

Replace-Exact 'MainWindow.xaml.cs' @'
            if (!_monitoringStarted) return;

            if (IsVisible && WindowState != WindowState.Minimized && HomePage.IsVisible)
            {
                _model.TickSystemState();
            }

            if (_runtimeRefreshInFlight || !IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            var now = DateTime.UtcNow;
            if (now < _nextRuntimeRefreshUtc)
'@ @'
            if (!_monitoringStarted) return;

            var now = DateTime.UtcNow;
            if (!_productRouteSyncInFlight && now >= _nextProductRouteSyncUtc)
            {
                await RefreshProductRuntimeBindingsAsync(force: true);
            }

            if (IsVisible && WindowState != WindowState.Minimized && HomePage.IsVisible)
            {
                _model.TickSystemState();
            }

            if (_runtimeRefreshInFlight || !IsVisible || WindowState == WindowState.Minimized)
            {
                return;
            }

            now = DateTime.UtcNow;
            if (now < _nextRuntimeRefreshUtc)
'@

Replace-Exact 'MainWindow.xaml.cs' @'
                _productInstallQueue.ResumePending();
                await NginxProductProxyService.TrySyncAsync();
                await RefreshEnvironmentStatesAsync();
'@ @'
                _productInstallQueue.ResumePending();
                await RefreshProductRuntimeBindingsAsync(force: true);
                await RefreshEnvironmentStatesAsync();
'@

Replace-Exact 'MainWindow.xaml.cs' @'
        else if (page == "Sites")
        {
            _model.RefreshInstalledProducts();
            _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
        }
'@ @'
        else if (page == "Sites")
        {
            _model.RefreshInstalledProducts();
            _model.RefreshCustomWebsites(_customWebsiteService.LoadAll());
            _nextProductRouteSyncUtc = DateTime.MinValue;
        }
'@

$mainSync = @'
namespace MCPanel;

public partial class MainWindow
{
    private async Task RefreshProductRuntimeBindingsAsync(bool force)
    {
        if (_productRouteSyncInFlight || _isClosed)
        {
            return;
        }

        _productRouteSyncInFlight = true;
        try
        {
            var changed = await Task.Run(() =>
                ProductDeploymentService.ReconcileRuntimeDeploymentState(force));

            // NginxProductProxyService is change-aware: identical effective
            // configuration returns without writing or reloading Nginx.
            await NginxProductProxyService.TrySyncAsync();

            if (_isClosed || changed.Count == 0)
            {
                return;
            }

            foreach (var item in _model.InstalledProducts)
            {
                item.RefreshRuntime();
            }
            _model.RefreshWebsiteFilterForRuntimeChange();
        }
        catch (Exception ex)
        {
            WriteRuntimeRefreshError(ex);
        }
        finally
        {
            var cadence = SitesPage.IsVisible && IsVisible && WindowState != System.Windows.WindowState.Minimized
                ? ProductRouteForegroundSyncInterval
                : ProductRouteBackgroundSyncInterval;
            _nextProductRouteSyncUtc = DateTime.UtcNow + cadence;
            _productRouteSyncInFlight = false;
        }
    }
}
'@
Write-Utf8 'MainWindow.RuntimeBindingSync.cs' $mainSync

$tests = @'
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void RuntimePortSync146_IisBindingParserTracksManualHttpPort()
    {
        Assert.AreEqual(8872, ProductDeploymentService.ParseIisHttpBindingPort("http/*:8872:"));
        Assert.AreEqual(8872, ProductDeploymentService.ParseIisHttpBindingPort("https/*:443:,http/*:8872:"));
        Assert.AreEqual(9080, ProductDeploymentService.ParseIisHttpBindingPort("http/127.0.0.1:9080:,https/*:443:"));
        Assert.IsNull(ProductDeploymentService.ParseIisHttpBindingPort("https/*:443:"));
    }

    [TestMethod]
    public void RuntimePortSync146_TomcatManualPortOutsideAutoPoolIsRuntimeTruth()
    {
        Assert.IsTrue(ProductDeploymentService.IsValidObservedRuntimePort(8085));
        Assert.IsFalse(ProductDeploymentService.IsValidTomcatProductPort(8085));
        Assert.AreEqual(8085,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(9000, 8085, Array.Empty<int>()));
        Assert.AreEqual(8085,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(8085, null, Array.Empty<int>()));
        Assert.AreEqual(9001,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, null, new[] { 9001 }));
    }

    [TestMethod]
    public void RuntimePortSync146_ProductDomainRuleFollowsBackendWithoutLosingFrontendSettings()
    {
        var source = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品域名 YX030101",
            ListenPort = 80,
            ServerName = "demo.example.com",
            LocationPath = "/",
            ProxyTarget = "http://127.0.0.1:9000/YX030101/",
            WebSocket = true,
            ManagedWebsiteId = "product-domain:YX030101",
            SslEnabled = true,
            HttpsPort = 443,
            SslCertificatePath = @"C:\cert\fullchain.pem",
            SslCertificateKeyPath = @"C:\cert\key.pem",
            RedirectHttpToHttps = true,
            MaxRateKbps = 2048
        };
        var route = new NginxProductProxyService.ProductRoute("YX030101", "/YX030101", 9123);

        var updated = NginxProductProxyService.RebindManagedWebsiteTargets(new[] { source }, new[] { route }).Single();

        Assert.AreEqual("http://127.0.0.1:9123/YX030101/", updated.ProxyTarget);
        Assert.AreEqual(source.ListenPort, updated.ListenPort);
        Assert.AreEqual(source.ServerName, updated.ServerName);
        Assert.AreEqual(source.LocationPath, updated.LocationPath);
        Assert.AreEqual(source.SslEnabled, updated.SslEnabled);
        Assert.AreEqual(source.HttpsPort, updated.HttpsPort);
        Assert.AreEqual(source.SslCertificatePath, updated.SslCertificatePath);
        Assert.AreEqual(source.SslCertificateKeyPath, updated.SslCertificateKeyPath);
        Assert.AreEqual(source.RedirectHttpToHttps, updated.RedirectHttpToHttps);
        Assert.AreEqual(source.MaxRateKbps, updated.MaxRateKbps);
    }

    [TestMethod]
    public void RuntimePortSync146_OrdinaryAdminRuleIsNeverRebound()
    {
        var source = new NginxProxyRule
        {
            Name = "管理员自定义规则",
            ListenPort = 8872,
            LocationPath = "/YX030101",
            ProxyTarget = "http://127.0.0.1:7777/custom",
            ManagedWebsiteId = null,
            ManagedProductId = null
        };
        var route = new NginxProductProxyService.ProductRoute("YX030101", "/YX030101", 9123);

        var updated = NginxProductProxyService.RebindManagedWebsiteTargets(new[] { source }, new[] { route }).Single();
        Assert.AreEqual("http://127.0.0.1:7777/custom", updated.ProxyTarget);
    }
}
'@
Write-Utf8 'MCPanel.Tests/ReliabilityTests.RuntimePortSync146.cs' $tests

# Version and release notes.
Replace-Exact 'MCPanel.csproj' '<Version>1.3.45</Version>' '<Version>1.3.46</Version>'
Replace-Exact 'MCPanel.csproj' '<FileVersion>1.3.45.0</FileVersion>' '<FileVersion>1.3.46.0</FileVersion>'
Replace-Exact 'MCPanel.csproj' '<AssemblyVersion>1.3.45.0</AssemblyVersion>' '<AssemblyVersion>1.3.46.0</AssemblyVersion>'

$notes = @'
# MCPanel 1.3.46

- IIS 产品端口改为“运行配置为事实来源”：用户在 IIS 管理器手动修改 `MCPanel` 网站 HTTP 绑定端口后，MCPanel 会自动回读真实端口并原子更新部署状态缓存，网站页访问链接随之更新。
- Java/Tomcat 产品同样回读实际 `server.xml` Connector 端口；手工设置的任意 1-65535 有效端口都可被识别并保留，而 MCPanel 新产品的自动端口分配仍保持 9000-10000。
- MCPanel 运行时自动检查端口漂移：网站页前台约 5 秒一次，后台/托盘约 30 秒一次；启动、重新激活和进入网站页会加速下一次同步。
- Nginx 自动产品代理会跟随真实 IIS/Tomcat 后端端口更新；产品独立域名/SSL 规则也只更新后端目标，保留域名、HTTPS、证书、限速与前端监听设置。普通管理员规则和显式产品代理覆盖不会被自动改写。
- Nginx 同步继续使用差异比较，配置未变化时不会写文件或 reload，避免周期检查造成无意义重载。
'@
Write-Utf8 'RELEASE-NOTES.md' $notes

# Source-contract checks before the expensive CI run.
$deployment = Read-Utf8 'ProductDeploymentService.cs'
if (-not $deployment.Contains('ReconcileRuntimeDeploymentState();')) { throw 'Runtime reconciliation was not wired into deployment loaders.' }
if (-not $deployment.Contains('IsValidObservedRuntimePort')) { throw 'Observed Tomcat runtime port support is missing.' }
$nginx = Read-Utf8 'NginxProductProxyService.cs'
if (-not $nginx.Contains('RebindManagedWebsiteTargets')) { throw 'Managed product-domain route rebinding is missing.' }
$window = Read-Utf8 'MainWindow.xaml.cs'
if (-not $window.Contains('ProductRouteBackgroundSyncInterval')) { throw 'Background route synchronization cadence is missing.' }
if ($window.Contains('Tag="CatalinaRun"')) {
    # MainWindow.xaml.cs normally does not own this XAML tag; no-op.
}
$xaml = Read-Utf8 'Resources/MainWindowTemplates.xaml'
if (-not $xaml.Contains('Tag="CatalinaRun"') -or -not $xaml.Contains('以 Catalina 方式启动')) {
    throw 'Dedicated Catalina start action must remain present.'
}
