using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace MCPanel;

// Tomcat deployment: per-product Service/Connector in server.xml, port selection and the Tomcat state records.
public sealed partial class ProductDeploymentService
{
    internal const int TomcatProductPortMinimum = 9000;
    internal const int TomcatProductPortMaximum = 10000;
    private static readonly SemaphoreSlim TomcatConfigurationLock = new(1, 1);
    private static readonly Mutex TomcatConfigurationMutex = new(false, "Local\\MCPanel.TomcatConfiguration.v1");

    public async Task<ProductDeploymentResult> RepairTomcatBindingAsync(ProductItem product, string installedPath, CancellationToken cancellationToken = default)
    {
        if (product.IsExternalPlatform)
        {
            var externalResult = await DeployToTomcatAsync(product, installedPath, installedPath, cancellationToken);
            await NginxProductProxyService.TrySyncAsync(cancellationToken);
            return externalResult;
        }
        if (!Directory.Exists(installedPath))
        {
            throw new DirectoryNotFoundException($"产品安装目录不存在：{installedPath}");
        }

        var configuration = ProductConfigurationService.Apply(product, installedPath, installedPath, isTomcatProduct: true);
        var result = await DeployToTomcatAsync(product, installedPath, installedPath, cancellationToken);
        if (configuration.Changed)
        {
            result = result with { Message = $"{result.Message}{Environment.NewLine}{configuration.Message}。" };
        }
        await NginxProductProxyService.TrySyncAsync(cancellationToken);
        return result;
    }

    public static TomcatProductDeploymentInfo? LoadTomcatDeploymentInfo(string productId)
    {
        ReconcileRuntimeDeploymentState();
        var safeName = PathUtil.SafeProductName(productId);
        var stateFile = GetTomcatStateFile(safeName);
        if (File.Exists(stateFile))
        {
            try
            {
                var info = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is not null && (Directory.Exists(info.PhysicalPath) || File.Exists(info.PhysicalPath)))
                {
                    return ResolveTomcatRuntimeInfo(info);
                }
            }
            catch (Exception error)
            {
                SuppressedErrorLog.Record("读取 Tomcat 部署记录", error, stateFile);
            }
        }

        var tomcatRoot = FindTomcatRoot();
        return tomcatRoot is null ? null : ReadTomcatProductService(tomcatRoot, safeName);
    }

    public static IReadOnlyList<TomcatProductDeploymentInfo> LoadTomcatDeploymentInfos()
    {
        ReconcileRuntimeDeploymentState();
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory))
        {
            return [];
        }

        var deployments = new List<TomcatProductDeploymentInfo>();
        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.tomcat.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var info = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(
                    File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is not null && (Directory.Exists(info.PhysicalPath) || File.Exists(info.PhysicalPath)))
                {
                    deployments.Add(ResolveTomcatRuntimeInfo(info));
                }
            }
            catch (Exception error)
            {
                SuppressedErrorLog.Record("读取 Tomcat 部署记录", error, stateFile);
            }
        }

        return deployments
            .GroupBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(info => info.Port)
            .ThenBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static TomcatProductDeploymentInfo ResolveTomcatRuntimeInfo(TomcatProductDeploymentInfo info)
    {
        var tomcatRoot = FindTomcatRoot();
        var configured = tomcatRoot is null ? null : ReadTomcatProductService(tomcatRoot, PathUtil.SafeProductName(info.ProductId));
        var instanceRoots = new[]
        {
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns", PathUtil.SafeProductName(info.ProductId)),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatProductRuns", PathUtil.SafeProductName(info.ProductId)),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", PathUtil.SafeProductName(info.ProductId)),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", PathUtil.SafeProductName(info.ProductId))
        };
        var generatedPorts = instanceRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(instanceRoot => ReadFirstTomcatHttpPort(Path.Combine(instanceRoot, "conf", "server.xml")))
            .Where(port => port.HasValue)
            .Select(port => port!.Value);
        var port = SelectCanonicalTomcatRuntimePort(info.Port, configured?.Port, generatedPorts);
        return port == info.Port ? info : WithTomcatPort(info, port);
    }

    internal static int SelectCanonicalTomcatRuntimePort(
        int persistedPort,
        int? configuredPort,
        IEnumerable<int> generatedInstancePorts)
    {
        // The configured Connector is runtime truth, even when an administrator
        // deliberately moves it outside MCPanel's 9000-10000 auto-allocation pool.
        if (configuredPort.HasValue && IsValidObservedTomcatProductPort(configuredPort.Value))
        {
            return configuredPort.Value;
        }

        if (IsValidObservedTomcatProductPort(persistedPort))
        {
            return persistedPort;
        }

        return generatedInstancePorts.FirstOrDefault(IsValidObservedTomcatProductPort);
    }

    private static TomcatProductDeploymentInfo WithTomcatPort(TomcatProductDeploymentInfo info, int port) =>
        info with
        {
            Port = port,
            Url = $"http://localhost:{port}{info.ContextPath.TrimEnd('/')}/"
        };

    internal static int? ReadFirstTomcatHttpPort(string serverXml)
    {
        try
        {
            var port = TomcatRuntimeProbe
                .ReadHttpPorts(Path.GetDirectoryName(Path.GetDirectoryName(serverXml)!)!)
                .FirstOrDefault(IsValidObservedTomcatProductPort);
            return IsValidObservedRuntimePort(port) ? port : null;
        }
        catch
        {
            return null;
        }
    }

    public static async Task<TomcatProductDeploymentInfo> ChangeTomcatProductPortAsync(
        string productId,
        int newPort,
        CancellationToken cancellationToken = default)
    {
        if (!IsValidTomcatProductPort(newPort))
        {
            throw new ArgumentOutOfRangeException(
                nameof(newPort),
                $"Tomcat 产品端口必须在 {TomcatProductPortMinimum} 到 {TomcatProductPortMaximum} 之间。");
        }

        var current = LoadTomcatDeploymentInfo(productId)
            ?? throw new InvalidOperationException($"未找到 {productId} 的 Tomcat 部署信息。");
        if (current.Port == newPort)
        {
            return current;
        }

        if (IsTcpPortListening(current.Port))
        {
            throw new InvalidOperationException($"{productId} 当前仍在端口 {current.Port} 运行，请先停止应用或 Tomcat Server。");
        }

        var tomcatRoot = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");

        await TomcatConfigurationLock.WaitAsync(cancellationToken);
        EnterTomcatConfigurationMutex();
        try
        {
            var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
            var document = XDocument.Load(serverXml, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("Tomcat server.xml 缺少 Server 根节点。");
            var ownServices = root.Elements("Service")
                .Where(service => service.Descendants("Context")
                    .Any(context => ContextMatchesProduct(context, productId)))
                .ToList();

            var configuredByAnotherService = root.Descendants("Connector")
                .Where(connector => !ownServices.Any(service => connector.Ancestors("Service").Contains(service)))
                .Any(connector => int.TryParse(connector.Attribute("port")?.Value,
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) && port == newPort);
            var reservedByAnotherProduct = LoadTomcatDeploymentInfos()
                .Any(info => !string.Equals(info.ProductId, productId, StringComparison.OrdinalIgnoreCase) &&
                             info.Port == newPort);
            if (configuredByAnotherService || reservedByAnotherProduct || IsTcpPortListening(newPort))
            {
                throw new InvalidOperationException($"端口 {newPort} 已被其他 Tomcat 产品或程序占用。");
            }

            foreach (var service in ownServices)
            {
                service.Remove();
            }

            var safeProductId = PathUtil.SafeProductName(current.ProductId);
            var updated = current with
            {
                ProductId = safeProductId,
                ServiceName = $"Catalina{newPort}",
                EngineName = $"Catalina{newPort}",
                HostAppBase = $"webapps{newPort}",
                ContextPath = $"/{safeProductId}",
                Port = newPort,
                Url = $"http://localhost:{newPort}/{safeProductId}/"
            };
            Directory.CreateDirectory(Path.Combine(tomcatRoot, updated.HostAppBase));
            root.Add(CreateTomcatProductService(root, updated));

            await SaveTomcatServerXmlAsync(serverXml, document, cancellationToken);
            var stateFile = GetTomcatStateFile(safeProductId);
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.WriteAllText(
                stateFile,
                JsonSerializer.Serialize(updated, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
            return updated;
        }
        finally
        {
            ExitTomcatConfigurationMutex();
            TomcatConfigurationLock.Release();
        }
    }

    private static async Task<ProductDeploymentResult> DeployToTomcatAsync(ProductItem product, string packagePath, string preparedPath, CancellationToken cancellationToken)
    {
        var tomcatRoot = FindTomcatRoot();
        if (tomcatRoot is null)
        {
            throw new InvalidOperationException("未找到 Tomcat Server。请先在“环境”里安装 Tomcat，再安装该产品。");
        }

        await RemoveStaleIisBindingAsync(product.ProductId, cancellationToken);

        var contextName = PathUtil.SafeProductName(product.ProductId);
        var appRoot = Directory.Exists(preparedPath) ? preparedPath : Path.GetDirectoryName(preparedPath)!;
        if (!ProductInstallPathResolver.IsInsideProductRoot(appRoot, product))
        {
            throw new InvalidOperationException($"Tomcat 产品目录必须位于产品安装根目录内：{appRoot}");
        }

        var docBase = product.ExternalJavaDocBase ?? FindTomcatDocBase(preparedPath)
            ?? throw new InvalidDataException("Tomcat 产品包中未找到 WAR 文件或 WEB-INF 目录，请重新下载完整产品包后重试。");
        DeleteDirectoryIfExists(Path.Combine(tomcatRoot, "webapps", contextName));
        DeleteFileIfExists(Path.Combine(tomcatRoot, "webapps", $"{contextName}.war"));
        DeleteFileIfExists(GetTomcatContextFile(tomcatRoot, contextName));

        var deployment = await ConfigureTomcatProductServiceAsync(
            tomcatRoot,
            contextName,
            docBase,
            cancellationToken);
        var stateFile = GetTomcatStateFile(contextName);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteAllText(
            stateFile,
            JsonSerializer.Serialize(deployment, new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        TomcatProductStartupManager.RefreshRegistration();

        var modeText = "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。";
        return new ProductDeploymentResult(
            $"产品已安装到 {appRoot}，并分配 Tomcat 端口 {deployment.Port}。访问地址：{deployment.Url}。{modeText}",
            appRoot);
    }

    private static async Task<TomcatProductDeploymentInfo> ConfigureTomcatProductServiceAsync(
        string tomcatRoot,
        string productId,
        string docBase,
        CancellationToken cancellationToken)
    {
        await TomcatConfigurationLock.WaitAsync(cancellationToken);
        EnterTomcatConfigurationMutex();
        try
        {
            var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
            var document = XDocument.Load(serverXml, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("Tomcat server.xml 缺少 Server 根节点。");

            var existing = FindTomcatProductService(root, productId);
            var saved = LoadTomcatStateFile(productId);
            var preferredPort = existing?.Port ?? saved?.Port;

            if (existing is not null)
            {
                existing.Service.Remove();
            }

            var port = SelectTomcatProductPort(
                root,
                productId,
                preferredPort,
                allowActivePreferredPort: existing is not null || saved is not null);
            var serviceName = $"Catalina{port}";
            var engineName = serviceName;
            var appBase = $"webapps{port}";
            var deployment = new TomcatProductDeploymentInfo(
                productId,
                serviceName,
                engineName,
                appBase,
                $"/{productId}",
                docBase!,
                port,
                $"http://localhost:{port}/{productId}/");
            Directory.CreateDirectory(Path.Combine(tomcatRoot, appBase));
            root.Add(CreateTomcatProductService(root, deployment));
            await SaveTomcatServerXmlAsync(serverXml, document, cancellationToken);
            return deployment;
        }
        finally
        {
            ExitTomcatConfigurationMutex();
            TomcatConfigurationLock.Release();
        }
    }

    internal static async Task EnsureTomcatProductServiceAsync(
        TomcatProductDeploymentInfo deployment,
        CancellationToken cancellationToken = default)
    {
        var tomcatRoot = FindTomcatRoot()
            ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");
        var safeProductId = PathUtil.SafeProductName(deployment.ProductId);
        await TomcatConfigurationLock.WaitAsync(cancellationToken);
        EnterTomcatConfigurationMutex();
        try
        {
            var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
            var document = XDocument.Load(serverXml, LoadOptions.PreserveWhitespace);
            var root = document.Root ?? throw new InvalidDataException("Tomcat server.xml 缺少 Server 根节点。");
            var ownServices = root.Elements("Service")
                .Where(service => service.Descendants("Context")
                    .Any(context => ContextMatchesProduct(context, safeProductId)))
                .ToList();
            foreach (var service in ownServices)
            {
                service.Remove();
            }

            // Old or partially generated instance files may contain Connector
            // port="0". Tomcat interprets that as "pick a random free port",
            // which is incompatible with MCPanel's persisted product routes.
            // Repair such state while holding the same cross-process lock used
            // for normal allocation so an upgrade heals existing deployments.
            int? preferredPort = IsValidObservedTomcatProductPort(deployment.Port)
                ? deployment.Port
                : null;
            var port = SelectTomcatProductPort(
                root,
                safeProductId,
                preferredPort,
                allowActivePreferredPort: ownServices.Count > 0);
            deployment = deployment with
            {
                ProductId = safeProductId,
                ServiceName = $"Catalina{port}",
                EngineName = $"Catalina{port}",
                HostAppBase = $"webapps{port}",
                ContextPath = $"/{safeProductId}",
                Port = port,
                Url = $"http://localhost:{port}/{safeProductId}/"
            };

            Directory.CreateDirectory(Path.Combine(tomcatRoot, deployment.HostAppBase));
            root.Add(CreateTomcatProductService(root, deployment));
            await SaveTomcatServerXmlAsync(serverXml, document, cancellationToken);
            var stateFile = GetTomcatStateFile(safeProductId);
            Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
            cancellationToken.ThrowIfCancellationRequested();
            AtomicFile.WriteAllText(
                stateFile,
                JsonSerializer.Serialize(deployment, new JsonSerializerOptions { WriteIndented = true }),
                new UTF8Encoding(false));
        }
        finally
        {
            ExitTomcatConfigurationMutex();
            TomcatConfigurationLock.Release();
        }
    }

    private static XElement CreateTomcatProductService(XElement serverRoot, TomcatProductDeploymentInfo deployment)
    {
        if (!IsValidObservedTomcatProductPort(deployment.Port))
        {
            throw new InvalidDataException(
                $"不能为 {deployment.ProductId} 写入无效的 Tomcat 产品端口 {deployment.Port}。");
        }

        var realm = serverRoot.Descendants("Engine").FirstOrDefault()?.Element("Realm");
        var realmElement = realm is not null
            ? new XElement(realm)
            : new XElement("Realm",
                new XAttribute("className", "org.apache.catalina.realm.LockOutRealm"),
                new XElement("Realm",
                    new XAttribute("className", "org.apache.catalina.realm.UserDatabaseRealm"),
                    new XAttribute("resourceName", "UserDatabase")));

        return new XElement("Service",
            new XAttribute("name", deployment.ServiceName),
            new XElement("Executor",
                new XAttribute("name", "tomcatThreadPool"),
                new XAttribute("namePrefix", "catalina-exec-"),
                new XAttribute("maxThreads", "128"),
                new XAttribute("minSpareThreads", "4"),
                new XAttribute("maxIdleTime", "60000"),
                new XAttribute("prestartminSpareThreads", "false"),
                new XAttribute("maxQueueSize", "1000")),
            new XElement("Connector",
                new XAttribute("port", deployment.Port),
                new XAttribute("executor", "tomcatThreadPool"),
                new XAttribute("protocol", "org.apache.coyote.http11.Http11NioProtocol"),
                new XAttribute("connectionTimeout", "300000"),
                new XAttribute("maxConnections", "2000"),
                new XAttribute("acceptCount", "200"),
                new XAttribute("acceptorThreadCount", "1"),
                new XAttribute("URIEncoding", "UTF-8"),
                new XAttribute("enableLookups", "false"),
                new XAttribute("redirectPort", "8443"),
                new XAttribute("server", "Tomcat8"),
                new XAttribute("maxPostSize", "-1")),
            new XElement("Engine",
                new XAttribute("name", deployment.EngineName),
                new XAttribute("defaultHost", "localhost"),
                realmElement,
                new XElement("Host",
                    new XAttribute("name", "localhost"),
                    new XAttribute("appBase", deployment.HostAppBase),
                    new XAttribute("unpackWARs", "true"),
                    new XAttribute("autoDeploy", "false"),
                    new XElement("Context",
                        new XAttribute("path", deployment.ContextPath),
                        new XAttribute("docBase", deployment.PhysicalPath),
                        new XAttribute("reloadable", "false"),
                        new XAttribute("crossContext", "true")))));
    }

    private static async Task RemoveTomcatProductServiceAsync(string tomcatRoot, string productId, CancellationToken cancellationToken)
    {
        await TomcatConfigurationLock.WaitAsync(cancellationToken);
        EnterTomcatConfigurationMutex();
        try
        {
            var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
            if (!File.Exists(serverXml))
            {
                return;
            }

            var document = XDocument.Load(serverXml, LoadOptions.PreserveWhitespace);
            var root = document.Root;
            var existing = root is null ? null : FindTomcatProductService(root, productId);
            if (existing is null)
            {
                return;
            }

            existing.Service.Remove();
            await SaveTomcatServerXmlAsync(serverXml, document, cancellationToken);
        }
        finally
        {
            ExitTomcatConfigurationMutex();
            TomcatConfigurationLock.Release();
        }
    }

    private static TomcatProductDeploymentInfo? LoadTomcatStateFile(string productId)
    {
        var file = GetTomcatStateFile(productId);
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception error)
        {
            SuppressedErrorLog.Record("读取 Tomcat 部署记录", error, file);
            return null;
        }
    }

    private static TomcatProductDeploymentInfo? ReadTomcatProductService(string tomcatRoot, string productId)
    {
        try
        {
            var document = XDocument.Load(Path.Combine(tomcatRoot, "conf", "server.xml"));
            var match = document.Root is null ? null : FindTomcatProductService(document.Root, productId);
            if (match is null)
            {
                return null;
            }

            var engine = match.Service.Element("Engine");
            var host = engine?.Element("Host");
            var context = host?.Elements("Context").FirstOrDefault(element => ContextMatchesProduct(element, productId));
            var docBase = context?.Attribute("docBase")?.Value;
            if (string.IsNullOrWhiteSpace(docBase))
            {
                return null;
            }

            var serviceName = match.Service.Attribute("name")?.Value ?? $"Catalina{match.Port}";
            var engineName = engine?.Attribute("name")?.Value ?? serviceName;
            var appBase = host?.Attribute("appBase")?.Value ?? $"webapps{match.Port}";
            var contextPath = NormalizeContextPath(context?.Attribute("path")?.Value, productId);
            return new TomcatProductDeploymentInfo(
                productId,
                serviceName,
                engineName,
                appBase,
                contextPath,
                docBase!,
                match.Port,
                $"http://localhost:{match.Port}{contextPath}/");
        }
        catch
        {
            return null;
        }
    }

    private static TomcatServiceMatch? FindTomcatProductService(XElement server, string productId)
    {
        foreach (var service in server.Elements("Service"))
        {
            var context = service.Descendants("Context")
                .FirstOrDefault(element => ContextMatchesProduct(element, productId));
            if (context is null)
            {
                continue;
            }

            var connector = service.Elements("Connector")
                .FirstOrDefault(element =>
                    int.TryParse(element.Attribute("port")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _) &&
                    !string.Equals(element.Attribute("protocol")?.Value, "AJP/1.3", StringComparison.OrdinalIgnoreCase));
            if (int.TryParse(
                    connector?.Attribute("port")?.Value,
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var port) &&
                IsValidObservedTomcatProductPort(port))
            {
                return new TomcatServiceMatch(service, port);
            }
        }

        return null;
    }

    private static bool ContextMatchesProduct(XElement context, string productId)
    {
        var path = context.Attribute("path")?.Value?.Trim().Trim('/');
        if (string.Equals(path, productId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var docBase = context.Attribute("docBase")?.Value;
        if (string.IsNullOrWhiteSpace(docBase))
        {
            return false;
        }

        var name = Path.GetFileName(docBase!.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.Equals(name, productId, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(Path.GetFileNameWithoutExtension(name), productId, StringComparison.OrdinalIgnoreCase);
    }

    private static string NormalizeContextPath(string? path, string productId)
    {
        var normalized = string.IsNullOrWhiteSpace(path) ? productId : path!.Trim().Trim('/');
        return $"/{normalized}";
    }

    private static int SelectTomcatProductPort(
        XElement server,
        string productId,
        int? preferredPort,
        bool allowActivePreferredPort)
    {
        var configuredPorts = server.Descendants("Connector")
            .Select(element => int.TryParse(element.Attribute("port")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) ? port : -1)
            .Where(port => port > 0)
            .ToHashSet();
        var stateDirectory = Path.GetDirectoryName(GetTomcatStateFile(productId));
        if (!string.IsNullOrWhiteSpace(stateDirectory) && Directory.Exists(stateDirectory))
        {
            foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.tomcat.json", SearchOption.TopDirectoryOnly))
            {
                if (string.Equals(stateFile, GetTomcatStateFile(productId), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    var state = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(
                        File.ReadAllText(stateFile, Encoding.UTF8));
                    if (state is not null && IsValidObservedTomcatProductPort(state.Port) &&
                        (Directory.Exists(state.PhysicalPath) || File.Exists(state.PhysicalPath)))
                    {
                        configuredPorts.Add(state.Port);
                    }
                }
                catch (Exception error)
                {
                    SuppressedErrorLog.Record("读取 Tomcat 产品端口记录", error, stateFile);
                }
            }
        }

        var activePorts = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();

        if (preferredPort.HasValue && IsValidObservedTomcatProductPort(preferredPort.Value) &&
            !configuredPorts.Contains(preferredPort.Value) &&
            (allowActivePreferredPort || !activePorts.Contains(preferredPort.Value)))
        {
            return preferredPort.Value;
        }

        for (var port = TomcatProductPortMinimum; port <= TomcatProductPortMaximum; port++)
        {
            if (!configuredPorts.Contains(port) && !activePorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException(
            $"Tomcat 产品端口池 {TomcatProductPortMinimum}-{TomcatProductPortMaximum} 已无可用端口。");
    }

    internal static bool IsValidTomcatProductPort(int port) =>
        port is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum;

    // Auto-allocation intentionally stays in 9000-10000, but runtime discovery
    // must respect any valid TCP port an administrator configured by hand.
    internal static bool IsValidObservedRuntimePort(int port) => port is > 0 and <= 65535;

    // 8080 belongs to the shared Tomcat Server. A product can be manually moved
    // to any other valid TCP port (for example 8085, 9314 or 12000), but recovery
    // must never persist the shared listener as an independent product binding.
    internal static bool IsValidObservedTomcatProductPort(int port) =>
        IsValidObservedRuntimePort(port) && port != 8080;

    private static async Task SaveTomcatServerXmlAsync(string serverXml, XDocument document, CancellationToken cancellationToken)
    {
        var backup = $"{serverXml}.mcpanel.bak";
        if (!File.Exists(backup))
        {
            File.Copy(serverXml, backup);
        }

        var temporary = $"{serverXml}.tmp";
        var settings = new System.Xml.XmlWriterSettings
        {
            Async = true,
            Encoding = new UTF8Encoding(false),
            Indent = true,
            IndentChars = "  ",
            NewLineChars = Environment.NewLine,
            NewLineHandling = System.Xml.NewLineHandling.Replace
        };
        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
        using (var writer = System.Xml.XmlWriter.Create(stream, settings))
        {
            document.Save(writer);
            await writer.FlushAsync();
        }

        XDocument.Load(temporary);
        FileCompat.Move(temporary, serverXml, overwrite: true);
    }

    private static bool IsTcpPortListening(int port)
    {
        try
        {
            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpListeners()
                .Any(endpoint => endpoint.Port == port);
        }
        catch
        {
            return false;
        }
    }

    private static string? FindTomcatRoot()
    {
        return new ComponentLocator().FindTomcatRoot(
            TomcatComponentRequirements.StartupScript |
            TomcatComponentRequirements.ServerXml |
            TomcatComponentRequirements.WebAppsDirectory);
    }

    internal static string? FindTomcatDocBase(string preparedPath)
    {
        var targets = LocalPlatformInspection.FindJavaTargets(preparedPath);
        if (targets.Count > 1) throw new InvalidDataException("发现多个 Java 应用，请通过手动绑定选择具体的软件目录和应用。");
        return targets.FirstOrDefault();
    }

    private static bool IsSafeTomcatAppBase(string value) =>
        value.IndexOfAny(['/', '\\']) < 0 &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^webapps[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string GetTomcatContextFile(string tomcatRoot, string safeName) =>
        Path.Combine(tomcatRoot, "conf", "Catalina", "localhost", $"{safeName}.xml");

    private static string GetTomcatStateFile(string safeName) =>
        Path.Combine(ComponentPaths.ProductStateRoot, $"{safeName}.tomcat.json");

    private sealed record TomcatServiceMatch(XElement Service, int Port);

    private static void EnterTomcatConfigurationMutex()
    {
        try
        {
            TomcatConfigurationMutex.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The previous writer exited while holding the lock; ownership is transferred here.
        }
    }

    private static void ExitTomcatConfigurationMutex()
    {
        try { TomcatConfigurationMutex.ReleaseMutex(); } catch { }
    }
}
