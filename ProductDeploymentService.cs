using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace MCPanel;

public sealed record ProductDeploymentResult(string Message, string DeployPath);
public sealed record ProductUninstallProgress(double Percent, string Status);
public sealed record IisProductDeploymentInfo(string ProductId, string SiteName, string ApplicationPath, string ApplicationPool, string PhysicalPath, int Port, string Url);
public sealed record TomcatProductDeploymentInfo(string ProductId, string ServiceName, string EngineName, string HostAppBase, string ContextPath, string PhysicalPath, int Port, string Url);

public sealed class ProductDeploymentService
{
    internal const int TomcatProductPortMinimum = 9000;
    internal const int TomcatProductPortMaximum = 10000;
    private static readonly SemaphoreSlim TomcatConfigurationLock = new(1, 1);
    private static readonly Mutex TomcatConfigurationMutex = new(false, "Local\\MCPanel.TomcatConfiguration.v1");

    public void PruneStaleDeploymentState()
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory))
        {
            return;
        }

        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.tomcat.json", SearchOption.TopDirectoryOnly))
        {
            try
            {
                var state = JsonSerializer.Deserialize<TomcatProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (state is not null && (Directory.Exists(state.PhysicalPath) || File.Exists(state.PhysicalPath)))
                {
                    continue;
                }

                DeleteFileIfExists(stateFile);
                if (state is not null)
                {
                    DeleteDirectoryIfExists(Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", SafeName(state.ProductId)));
                    DeleteDirectoryIfExists(Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", SafeName(state.ProductId)));
                }
            }
            catch (Exception error)
            {
                QuarantineCorruptDeploymentState(stateFile, error);
            }
        }

        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".tomcat.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var state = JsonSerializer.Deserialize<IisProductDeploymentInfo>(File.ReadAllText(stateFile, Encoding.UTF8));
                if (state is null || !Directory.Exists(state.PhysicalPath))
                {
                    DeleteFileIfExists(stateFile);
                }
            }
            catch (Exception error)
            {
                QuarantineCorruptDeploymentState(stateFile, error);
            }
        }
    }

    private static void QuarantineCorruptDeploymentState(string stateFile, Exception error)
    {
        try
        {
            if (!File.Exists(stateFile))
            {
                return;
            }

            var timestamp = DateTime.Now.ToString("yyyyMMdd-HHmmss");
            var quarantine = stateFile + $".corrupt-{timestamp}";
            for (var suffix = 1; File.Exists(quarantine); suffix++)
            {
                quarantine = stateFile + $".corrupt-{timestamp}-{suffix}";
            }

            File.Move(stateFile, quarantine);
            Directory.CreateDirectory(ComponentPaths.WorkRoot);
            RollingLogWriter.Append(
                Path.Combine(ComponentPaths.WorkRoot, "deployment-state-recovery.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] 部署状态文件损坏，已隔离而不是删除：{stateFile} -> {quarantine}{Environment.NewLine}{error}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // A damaged state file must not prevent MCPanel from opening. If it
            // cannot be quarantined, leave it in place for a later repair.
        }
    }

    public async Task<ProductDeploymentResult> DeployAsync(ProductItem product, string packagePath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(packagePath) && !Directory.Exists(packagePath))
        {
            throw new FileNotFoundException("产品安装包不存在。", packagePath);
        }

        VerifyDownloadedPackageAudit(packagePath);

        if (Directory.Exists(packagePath) &&
            !Directory.EnumerateFiles(packagePath, "*", SearchOption.AllDirectories).Any())
        {
            throw new InvalidDataException("产品下载目录为空，已取消安装。请重新下载产品。");
        }

        cancellationToken.ThrowIfCancellationRequested();
        var kind = DetectRuntime(product, packagePath);
        using var transaction = PrepareProductFiles(product, packagePath, kind);
        cancellationToken.ThrowIfCancellationRequested();
        var existingConfiguration = transaction.IsExisting
            ? ProductConfigurationSnapshot.Capture(transaction.ProductRoot, transaction.PreparedPath)
            : null;
        try
        {
            var configuration = ProductConfigurationService.Apply(
                product,
                transaction.ProductRoot,
                transaction.PreparedPath,
                kind == ProductRuntimeKind.Tomcat);
            cancellationToken.ThrowIfCancellationRequested();
            var result = kind switch
            {
                ProductRuntimeKind.Tomcat => await DeployToTomcatAsync(product, packagePath, transaction.PreparedPath, cancellationToken),
                ProductRuntimeKind.Iis => await DeployToIisAsync(product, transaction.PreparedPath, cancellationToken),
                _ => new ProductDeploymentResult($"产品已下载并解压到：{transaction.ProductRoot}。未识别到 IIS/Tomcat 运行环境，请手动绑定。", transaction.ProductRoot)
            };

            if (configuration.Changed)
            {
                result = result with { Message = $"{result.Message}{Environment.NewLine}{configuration.Message}。" };
            }

            transaction.Complete();
            await NginxProductProxyService.TrySyncAsync(cancellationToken);
            DeleteDownloadedPackage(packagePath);
            return result;
        }
        catch (Exception error)
        {
            Exception? rollbackError = null;
            try
            {
                transaction.Rollback();
            }
            catch (Exception exception)
            {
                rollbackError = exception;
            }

            if (existingConfiguration is not null)
            {
                try
                {
                    existingConfiguration.Restore();
                }
                catch (Exception exception)
                {
                    rollbackError = rollbackError is null
                        ? exception
                        : new AggregateException(rollbackError, exception);
                }
            }

            if (rollbackError is not null)
            {
                throw new InvalidOperationException(
                    $"产品安装失败，且原有产品文件回滚不完整：{rollbackError.Message}。原始错误：{error.Message}",
                    rollbackError);
            }

            throw;
        }
    }

    private static ProductRuntimeKind DetectRuntime(ProductItem product, string packagePath)
    {
        var declaredRuntime = product.RunEnvironment?.Trim() ?? string.Empty;
        if (ContainsAny(declaredRuntime, "tomcat", "java web", "jsp", "servlet"))
        {
            return ProductRuntimeKind.Tomcat;
        }

        if (ContainsAny(declaredRuntime, "iis", "asp", ".net", "framework"))
        {
            return ProductRuntimeKind.Iis;
        }

        if (packagePath.EndsWith(".war", StringComparison.OrdinalIgnoreCase) ||
            DirectoryContains(packagePath, "WEB-INF"))
        {
            return ProductRuntimeKind.Tomcat;
        }

        if (DirectoryContainsFile(packagePath, "web.config") ||
            DirectoryContainsExtension(packagePath, ".aspx") ||
            DirectoryContainsExtension(packagePath, ".asmx"))
        {
            return ProductRuntimeKind.Iis;
        }

        var language = product.DevLanguage?.Trim() ?? string.Empty;
        if (ContainsAny(language, "java", "jsp"))
        {
            return ProductRuntimeKind.Tomcat;
        }

        if (ContainsAny(language, "c#", "visual basic", "vb.net", ".net"))
        {
            return ProductRuntimeKind.Iis;
        }

        return ProductRuntimeKind.Unknown;
    }

    private static void VerifyDownloadedPackageAudit(string packagePath)
    {
        var downloadsRoot = Path.GetFullPath(ComponentPaths.ProductDownloadRoot)
            .TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(packagePath);
        if (!fullPath.StartsWith(downloadsRoot, StringComparison.OrdinalIgnoreCase)) return;

        if (File.Exists(fullPath))
        {
            var sidecar = fullPath + ".sha256";
            if (!File.Exists(sidecar)) throw new InvalidDataException("产品包缺少 SHA-256 下载清单，请重新下载。");
            var expected = File.ReadAllText(sidecar).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
            VerifyFileSha256(fullPath, expected);
            return;
        }

        var manifest = Path.Combine(fullPath, "SHA256SUMS.txt");
        if (!File.Exists(manifest)) throw new InvalidDataException("产品目录缺少 SHA-256 下载清单，请重新下载。");
        foreach (var line in File.ReadLines(manifest))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var separator = line.IndexOf("  ", StringComparison.Ordinal);
            if (separator != 64) throw new InvalidDataException("产品 SHA-256 清单格式无效。");
            var relative = line.Substring(separator + 2).Replace('/', Path.DirectorySeparatorChar);
            var candidate = Path.GetFullPath(Path.Combine(fullPath, relative));
            var root = fullPath.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("产品 SHA-256 清单包含不安全路径。");
            VerifyFileSha256(candidate, line.Substring(0, 64));
        }
    }

    private static void VerifyFileSha256(string path, string? expected)
    {
        if (string.IsNullOrWhiteSpace(expected) || expected!.Length != 64 || expected.Any(ch => !Uri.IsHexDigit(ch)))
            throw new InvalidDataException("产品 SHA-256 清单格式无效。");
        if (!File.Exists(path)) throw new InvalidDataException($"产品文件缺失：{path}");
        using var sha = SHA256.Create();
        using var stream = File.OpenRead(path);
        var actual = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty);
        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"产品文件完整性校验失败：{Path.GetFileName(path)}");
    }

    private static bool ContainsAny(string value, params string[] candidates) =>
        candidates.Any(candidate => value.Contains(candidate, StringComparison.OrdinalIgnoreCase));

    private static bool DirectoryContains(string packagePath, string directoryName)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateDirectories(packagePath, directoryName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectoryContainsFile(string packagePath, string fileName)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(packagePath, fileName, SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static bool DirectoryContainsExtension(string packagePath, string extension)
    {
        if (!Directory.Exists(packagePath))
        {
            return false;
        }

        try
        {
            return Directory.EnumerateFiles(packagePath, $"*{extension}", SearchOption.AllDirectories).Any();
        }
        catch
        {
            return false;
        }
    }

    private static ProductInstallTransaction PrepareProductFiles(ProductItem product, string packagePath, ProductRuntimeKind kind)
    {
        var installRoot = ProductInstallPathResolver.ResolveProductRoot(product.InstallRoot);
        Directory.CreateDirectory(installRoot);

        var safeName = SafeName(product.ProductId);
        var productRoot = Path.Combine(installRoot, safeName);
        if (PathsEqual(packagePath, productRoot))
        {
            EnsureDirectoryContainsFiles(productRoot);
            var existingPreparedPath = NormalizeProductRoot(productRoot);
            ValidatePreparedProduct(kind, existingPreparedPath);
            return ProductInstallTransaction.ForExisting(productRoot, existingPreparedPath);
        }

        var deployingRoot = Path.Combine(installRoot, ".deploying");
        var rollbackRoot = Path.Combine(installRoot, ".rollback");
        Directory.CreateDirectory(deployingRoot);
        Directory.CreateDirectory(rollbackRoot);

        var operationId = Guid.NewGuid().ToString("N");
        var stagingContainer = Path.Combine(deployingRoot, $"{safeName}.{operationId}");
        var stagingContent = Path.Combine(stagingContainer, "content");
        var backupRoot = Path.Combine(rollbackRoot, $"{safeName}.{operationId}");
        Directory.CreateDirectory(stagingContent);

        try
        {
            string stagedPreparedPath;
            if (Directory.Exists(packagePath))
            {
                CopyDirectory(packagePath, stagingContent);
                EnsureDirectoryContainsFiles(stagingContent);
                stagedPreparedPath = NormalizeProductRoot(stagingContent);
            }
            else
            {
                var extension = Path.GetExtension(packagePath);
                if (extension.Equals(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractZipSafely(packagePath, stagingContent);
                    EnsureDirectoryContainsFiles(stagingContent);
                    stagedPreparedPath = NormalizeProductRoot(stagingContent);
                }
                else if (extension.Equals(".war", StringComparison.OrdinalIgnoreCase))
                {
                    stagedPreparedPath = Path.Combine(stagingContent, $"{safeName}.war");
                    File.Copy(packagePath, stagedPreparedPath, overwrite: true);
                }
                else
                {
                    stagedPreparedPath = Path.Combine(stagingContent, Path.GetFileName(packagePath));
                    File.Copy(packagePath, stagedPreparedPath, overwrite: true);
                }
            }

            ValidatePreparedProduct(kind, stagedPreparedPath);
            var preparedRelativePath = PathCompat.GetRelativePath(stagingContent, stagedPreparedPath);

            if (Directory.Exists(productRoot))
            {
                Directory.Move(productRoot, backupRoot);
            }

            try
            {
                Directory.Move(stagingContent, productRoot);
            }
            catch
            {
                if (Directory.Exists(backupRoot) && !Directory.Exists(productRoot))
                {
                    Directory.Move(backupRoot, productRoot);
                }

                throw;
            }

            var preparedPath = preparedRelativePath == "."
                ? productRoot
                : Path.Combine(productRoot, preparedRelativePath);
            return new ProductInstallTransaction(productRoot, preparedPath, backupRoot, stagingContainer);
        }
        catch
        {
            DeleteDirectoryIfExists(stagingContainer);
            throw;
        }
    }

    private static void ValidatePreparedProduct(ProductRuntimeKind kind, string preparedPath)
    {
        if (kind == ProductRuntimeKind.Tomcat)
        {
            if (FindTomcatRoot() is null)
            {
                throw new InvalidOperationException("未找到 Tomcat Server。请先在“环境”里安装 Tomcat，再安装该产品。");
            }

            if (FindTomcatDocBase(preparedPath) is null)
            {
                throw new InvalidDataException("Tomcat 产品包中未找到 WAR 文件或 WEB-INF 目录。请重新下载完整产品包。");
            }
        }

        if (kind == ProductRuntimeKind.Iis)
        {
            var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
            if (!File.Exists(appcmd))
            {
                throw new InvalidOperationException("未找到 IIS 管理工具 appcmd.exe。请先在“环境”里安装 Web Server/IIS。");
            }
        }
    }

    public async Task UninstallAsync(
        ProductItem product,
        CancellationToken cancellationToken = default,
        IProgress<ProductUninstallProgress>? progress = null)
    {
        var safeName = SafeName(product.ProductId);
        ReportUninstallProgress(progress, 0, "正在准备卸载，读取现有绑定和运行状态...");
        cancellationToken.ThrowIfCancellationRequested();
        var iisState = LoadIisDeploymentInfo(product.ProductId);
        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        if (iisState is not null && !File.Exists(appcmd))
        {
            throw new InvalidOperationException(
                "检测到该产品仍有 IIS 绑定，但当前找不到 appcmd.exe。已保留产品文件，请先恢复 IIS 管理工具后重试卸载。");
        }

        var tomcatRoot = FindTomcatRoot();
        var tomcatState = LoadTomcatStateFile(safeName);
        var tomcatService = tomcatRoot is null ? null : ReadTomcatProductService(tomcatRoot, safeName);
        var tomcatArtifactsExist = tomcatRoot is not null &&
            (tomcatState is not null ||
             tomcatService is not null ||
             File.Exists(GetTomcatContextFile(tomcatRoot!, safeName)) ||
             Directory.Exists(Path.Combine(tomcatRoot!, "webapps", safeName)) ||
             File.Exists(Path.Combine(tomcatRoot!, "webapps", $"{safeName}.war")));
        var sharedTomcatWasRunning = tomcatArtifactsExist && TomcatProductInstanceManager.IsSharedTomcatRunning();

        ReportUninstallProgress(progress, 25, "正在移除 IIS 应用、应用池和共享站点端口...");
        if (iisState is not null && File.Exists(appcmd))
        {
            var script = Path.Combine(ComponentPaths.WorkRoot, $"unbind-product-{safeName}.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            await FileCompat.WriteAllTextAsync(script, $$"""
                $ErrorActionPreference='Stop'
                $appcmd='{{EscapePowerShellPath(appcmd)}}'
                $appName='MCPanel/{{safeName}}'
                $siteName='MCPanel'
                $siteRoot='{{EscapePowerShellPath(Path.Combine(ComponentPaths.RuntimeRoot, "IISRoot"))}}'
                $poolName='{{safeName}}'
                $appNames=@(& $appcmd list app /text:APP.NAME)
                if ($appNames -contains $appName) {
                    & $appcmd delete app "$appName"
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                }
                $poolNames=@(& $appcmd list apppool /text:name)
                if ($poolNames -contains $poolName) {
                    & $appcmd delete apppool "$poolName"
                    if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                }

                # MCPanel is a shared site.  Once its last product application
                # disappears, delete the site itself so its HTTP binding/port is
                # released just like the original DeleteProduct routine.
                $remainingApps=@(& $appcmd list app /text:APP.NAME | Where-Object { $_ -like "$siteName/*" })
                if ($remainingApps.Count -eq 0) {
                    $siteNames=@(& $appcmd list site /text:name)
                    if ($siteNames -contains $siteName) {
                        & $appcmd stop site "$siteName" | Out-Null
                        & $appcmd delete site "$siteName"
                        if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
                    }
                    if (Test-Path -LiteralPath $siteRoot) {
                        Remove-Item -LiteralPath $siteRoot -Recurse -Force
                    }
                }
                exit 0
                """, new UTF8Encoding(true), cancellationToken);
            try
            {
                await RunElevatedPowerShellAsync(script, cancellationToken);
            }
            finally
            {
                DeleteFileIfExists(script);
            }
        }

        if (sharedTomcatWasRunning)
        {
            // server.xml cannot be safely edited while the shared Catalina
            // process owns the connector.  Stop it, remove the Service, then
            // restore the previous all-applications mode in the finally block.
            ReportUninstallProgress(progress, 42, "正在停止 Tomcat 全部应用模式...");
            await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
        }

        try
        {
            if (tomcatArtifactsExist && tomcatRoot is not null)
            {
                ReportUninstallProgress(progress, 55, "正在删除 Tomcat 服务、端口和应用目录...");
                var expectedTomcatPort = tomcatState?.Port ?? tomcatService?.Port;
                await new TomcatProductInstanceManager().RemoveAsync(safeName, expectedTomcatPort, cancellationToken);
                DeleteFileIfExists(GetTomcatContextFile(tomcatRoot, safeName));
                DeleteDirectoryIfExists(Path.Combine(tomcatRoot, "webapps", safeName));
                DeleteFileIfExists(Path.Combine(tomcatRoot, "webapps", $"{safeName}.war"));
                await RemoveTomcatProductServiceAsync(tomcatRoot, safeName, cancellationToken);

                // Each product Service uses a private webapps<port> appBase.
                // It is not part of the SVN product root and therefore needs an
                // explicit removal as well.
                var appBase = tomcatState?.HostAppBase ?? tomcatService?.HostAppBase;
                if (!string.IsNullOrWhiteSpace(appBase) && IsSafeTomcatAppBase(appBase!))
                {
                    DeleteDirectoryIfExists(Path.Combine(tomcatRoot, appBase));
                }
            }

            // Keep the product route alive until the IIS/Tomcat bindings have
            // been removed successfully.  If this stage fails, the product
            // files and runtime state are still present for a clean retry.
            ReportUninstallProgress(progress, 68, "正在移除 Nginx 域名、SSL 和代理配置...");
            await ProductWebsiteService.RemoveForProductAsync(product.ProductId, cancellationToken);

            ReportUninstallProgress(progress, 78, "正在删除产品目录、SVN 元数据、下载缓存和临时文件...");
            cancellationToken.ThrowIfCancellationRequested();
            DeleteProductFiles(product, safeName);
            new ProductInstallOrderStore().Remove(product.ProductId);
            TomcatProductStartupManager.RefreshRegistration();
            ReportUninstallProgress(progress, 92, "正在同步剩余产品的 Nginx 路由和运行状态...");
            var routeSynced = await NginxProductProxyService.TrySyncAsync(CancellationToken.None);
            ReportUninstallProgress(
                progress,
                100,
                routeSynced
                    ? "卸载完成，产品目录和安装记录已清理。"
                    : "卸载完成，但 Nginx 剩余产品路由未自动同步；请在“环境”页面重新同步 Nginx。");
        }
        finally
        {
            if (sharedTomcatWasRunning)
            {
                try
                {
                    await new EnvironmentRuntimeService().StartAsync(EnvironmentKind.Tomcat, CancellationToken.None);
                }
                catch (Exception restartError)
                {
                    EnvironmentOperationDiagnostics.RecordFailure(
                        "产品管理",
                        $"卸载 {product.ProductId} 后恢复 Tomcat",
                        restartError);
                    throw new InvalidOperationException(
                        $"产品文件已清理，但 Tomcat 全部应用模式恢复失败：{restartError.Message}。请在“环境”页面重新启动 Tomcat。",
                        restartError);
                }
            }
        }
    }

    public async Task<ProductDeploymentResult> RepairIisBindingAsync(ProductItem product, string installedPath, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(installedPath))
        {
            throw new DirectoryNotFoundException($"产品安装目录不存在：{installedPath}");
        }

        var configuration = ProductConfigurationService.Apply(product, installedPath, installedPath, isTomcatProduct: false);
        var result = await DeployToIisAsync(product, installedPath, cancellationToken);
        if (configuration.Changed)
        {
            result = result with { Message = $"{result.Message}{Environment.NewLine}{configuration.Message}。" };
        }
        await NginxProductProxyService.TrySyncAsync(cancellationToken);
        return result;
    }

    public async Task<ProductDeploymentResult> RepairTomcatBindingAsync(ProductItem product, string installedPath, CancellationToken cancellationToken = default)
    {
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

    public static IisProductDeploymentInfo? LoadIisDeploymentInfo(string productId)
    {
        var file = GetIisStateFile(SafeName(productId));
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IisProductDeploymentInfo>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch
        {
            return null;
        }
    }

    public static IReadOnlyList<IisProductDeploymentInfo> LoadIisDeploymentInfos()
    {
        var stateDirectory = ComponentPaths.ProductStateRoot;
        if (!Directory.Exists(stateDirectory))
        {
            return [];
        }

        var deployments = new List<IisProductDeploymentInfo>();
        foreach (var stateFile in Directory.EnumerateFiles(stateDirectory, "*.json", SearchOption.TopDirectoryOnly)
                     .Where(path => !path.EndsWith(".tomcat.json", StringComparison.OrdinalIgnoreCase)))
        {
            try
            {
                var info = JsonSerializer.Deserialize<IisProductDeploymentInfo>(
                    File.ReadAllText(stateFile, Encoding.UTF8));
                if (info is not null && Directory.Exists(info.PhysicalPath))
                {
                    deployments.Add(info);
                }
            }
            catch
            {
            }
        }

        return deployments
            .GroupBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(info => info.Port)
            .ThenBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public static TomcatProductDeploymentInfo? LoadTomcatDeploymentInfo(string productId)
    {
        var safeName = SafeName(productId);
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
            catch
            {
            }
        }

        var tomcatRoot = FindTomcatRoot();
        return tomcatRoot is null ? null : ReadTomcatProductService(tomcatRoot, safeName);
    }

    public static IReadOnlyList<TomcatProductDeploymentInfo> LoadTomcatDeploymentInfos()
    {
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
            catch
            {
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
        var configured = tomcatRoot is null ? null : ReadTomcatProductService(tomcatRoot, SafeName(info.ProductId));
        var instanceRoots = new[]
        {
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns", SafeName(info.ProductId)),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatProductRuns", SafeName(info.ProductId)),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", SafeName(info.ProductId)),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", SafeName(info.ProductId))
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
                .FirstOrDefault(IsValidTomcatProductPort);
            return IsValidTomcatProductPort(port) ? port : null;
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

            var safeProductId = SafeName(current.ProductId);
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

        var contextName = SafeName(product.ProductId);
        var appRoot = Directory.Exists(preparedPath) ? preparedPath : Path.GetDirectoryName(preparedPath)!;
        if (!ProductInstallPathResolver.IsInsideProductRoot(appRoot, product))
        {
            throw new InvalidOperationException($"Tomcat 产品目录必须位于产品安装根目录内：{appRoot}");
        }

        var docBase = FindTomcatDocBase(preparedPath)
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

        var sharedServiceRestarted = false;
        if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatRoot))
        {
            TomcatWindowsServiceManager.Stop();
            TomcatWindowsServiceManager.Start();
            sharedServiceRestarted = true;
        }

        var modeText = sharedServiceRestarted
            ? "共享 Tomcat Server 已重启并加载新应用。"
            : "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。";
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
        var safeProductId = SafeName(deployment.ProductId);
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
            int? preferredPort = IsValidTomcatProductPort(deployment.Port)
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
        if (!IsValidTomcatProductPort(deployment.Port))
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

    private static async Task RemoveStaleIisBindingAsync(string productId, CancellationToken cancellationToken)
    {
        var safeName = SafeName(productId);
        var stateFile = GetIisStateFile(safeName);
        if (!File.Exists(stateFile))
        {
            return;
        }

        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        if (File.Exists(appcmd))
        {
            var script = Path.Combine(ComponentPaths.WorkRoot, $"remove-stale-iis-{safeName}.ps1");
            Directory.CreateDirectory(Path.GetDirectoryName(script)!);
            await FileCompat.WriteAllTextAsync(script, $$"""
                $ErrorActionPreference='Stop'
                $appcmd='{{EscapePowerShellPath(appcmd)}}'
                $appName='MCPanel/{{safeName}}'
                $poolName='{{safeName}}'
                $appNames=@(& $appcmd list app /text:APP.NAME)
                if ($appNames -contains $appName) { & $appcmd delete app "$appName" | Out-Null }
                $poolNames=@(& $appcmd list apppool /text:name)
                if ($poolNames -contains $poolName) { & $appcmd delete apppool "$poolName" | Out-Null }
                exit 0
                """, new UTF8Encoding(true), cancellationToken);
            try
            {
                await RunElevatedPowerShellAsync(script, cancellationToken);
            }
            finally
            {
                DeleteFileIfExists(script);
            }
        }

        DeleteFileIfExists(stateFile);
    }

    private static async Task<ProductDeploymentResult> DeployToIisAsync(ProductItem product, string preparedPath, CancellationToken cancellationToken)
    {
        var appcmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "inetsrv", "appcmd.exe");
        if (!File.Exists(appcmd))
        {
            throw new InvalidOperationException("未找到 IIS 管理工具 appcmd.exe。请先在“环境”里安装 Web Server/IIS。");
        }

        var sourceRoot = Directory.Exists(preparedPath) ? preparedPath : Path.GetDirectoryName(preparedPath)!;
        var safeName = SafeName(product.ProductId);
        if (!ProductInstallPathResolver.IsInsideProductRoot(sourceRoot, product))
        {
            throw new InvalidOperationException($"IIS 产品目录必须位于产品安装根目录内：{sourceRoot}");
        }
        var appRoot = sourceRoot;

        const string siteName = "MCPanel";
        var port = SelectIisPort(appcmd, "MCPanel");
        var appPath = $"/{safeName}";
        var poolName = safeName;
        var managedRuntimeVersion = DetectIisManagedRuntime(appRoot);
        var siteRoot = Path.Combine(ComponentPaths.RuntimeRoot, "IISRoot");
        Directory.CreateDirectory(siteRoot);
        var indexFile = Path.Combine(siteRoot, "index.html");
        if (!File.Exists(indexFile))
        {
            await FileCompat.WriteAllTextAsync(indexFile, "<!doctype html><meta charset=\"utf-8\"><title>MCPanel</title><h1>MCPanel</h1>", new UTF8Encoding(false), cancellationToken);
        }

        var script = Path.Combine(ComponentPaths.WorkRoot, $"bind-iis-{safeName}.ps1");
        Directory.CreateDirectory(Path.GetDirectoryName(script)!);
        var enable32Bit = !string.Equals(product.SysType?.Trim(), "64", StringComparison.OrdinalIgnoreCase);
        var managedPipelineMode = ResolveIisPipelineMode(product.RunEnvironment, managedRuntimeVersion);
        await FileCompat.WriteAllTextAsync(
            script,
            BuildIisBindScript(appcmd, siteName, siteRoot, appPath, appRoot, poolName, managedRuntimeVersion, managedPipelineMode, enable32Bit, port),
            new UTF8Encoding(true),
            cancellationToken);
        try
        {
            await RunElevatedPowerShellAsync(script, cancellationToken);
        }
        finally
        {
            DeleteFileIfExists(script);
        }

        var info = new IisProductDeploymentInfo(product.ProductId, siteName, appPath, poolName, appRoot, port, $"http://localhost:{port}{appPath}/");
        var stateFile = GetIisStateFile(safeName);
        Directory.CreateDirectory(Path.GetDirectoryName(stateFile)!);
        cancellationToken.ThrowIfCancellationRequested();
        AtomicFile.WriteAllText(stateFile, JsonSerializer.Serialize(info, new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

        return new ProductDeploymentResult($"已创建 IIS 应用程序池 {poolName}，并绑定到 {siteName}{appPath}。访问地址：{info.Url}", appRoot);
    }

    internal static string BuildIisBindScript(string appcmd, string siteName, string siteRoot, string appPath, string appRoot, string poolName, string managedRuntimeVersion, string managedPipelineMode, bool enable32Bit, int port)
    {
        return $$"""
            $ErrorActionPreference='Stop'
            $appcmd='{{EscapePowerShellPath(appcmd)}}'
            $siteName='{{EscapePowerShellPath(siteName)}}'
            $siteRoot='{{EscapePowerShellPath(siteRoot)}}'
            $appPath='{{EscapePowerShellPath(appPath)}}'
            $appRoot='{{EscapePowerShellPath(appRoot)}}'
            $poolName='{{EscapePowerShellPath(poolName)}}'
            $managedRuntimeVersion='{{EscapePowerShellPath(managedRuntimeVersion)}}'
            $managedPipelineMode='{{EscapePowerShellPath(managedPipelineMode)}}'
            $enable32Bit={{(enable32Bit ? "$true" : "$false")}}
            $port={{port}}
            $appName="$siteName$appPath"

            function Invoke-AppCmd {
                param([Parameter(ValueFromRemainingArguments=$true)][string[]]$Arguments)
                & $appcmd @Arguments
                if ($LASTEXITCODE -ne 0) { throw "appcmd 执行失败，退出码：$LASTEXITCODE，参数：$($Arguments -join ' ')" }
            }

            $poolNames = @(& $appcmd list apppool /text:name)
            if ($poolNames -notcontains $poolName) {
                Invoke-AppCmd add apppool "/name:$poolName"
            }
            Invoke-AppCmd set apppool "$poolName" "/managedRuntimeVersion:$managedRuntimeVersion" "/managedPipelineMode:$managedPipelineMode" "/enable32BitAppOnWin64:$enable32Bit" /startMode:AlwaysRunning

            $siteNames = @(& $appcmd list site /text:name)
            if ($siteNames -notcontains $siteName) {
                Invoke-AppCmd add site "/name:$siteName" "/physicalPath:$siteRoot" "/bindings:http/*:${port}:"
            } else {
                Invoke-AppCmd set vdir "$siteName/" "/physicalPath:$siteRoot"
                Invoke-AppCmd set site "$siteName" "/bindings:http/*:${port}:"
            }
            Invoke-AppCmd set site "$siteName" /serverAutoStart:true

            $appNames = @(& $appcmd list app /text:APP.NAME)
            if ($appNames -contains $appName) {
                Invoke-AppCmd delete app "$appName"
            }
            Invoke-AppCmd add app "/site.name:$siteName" "/path:$appPath" "/physicalPath:$appRoot" "/applicationPool:$poolName"

            & $appcmd start apppool "$poolName" | Out-Null
            if ($LASTEXITCODE -ne 0 -and ((& $appcmd list apppool "$poolName" /text:state) -ne 'Started')) {
                throw "无法启动应用程序池 $poolName"
            }
            & $appcmd start site "$siteName" | Out-Null
            if ($LASTEXITCODE -ne 0 -and ((& $appcmd list site "$siteName" /text:state) -ne 'Started')) {
                throw "无法启动网站 $siteName"
            }

            $poolNames = @(& $appcmd list apppool /text:name)
            $siteNames = @(& $appcmd list site /text:name)
            $appNames = @(& $appcmd list app /text:APP.NAME)
            if ($poolNames -notcontains $poolName) { throw "未能验证应用程序池 $poolName" }
            if ($siteNames -notcontains $siteName) { throw "未能验证网站 $siteName" }
            if ($appNames -notcontains $appName) { throw "未能验证 IIS 应用 $appName" }
            exit 0
            """;
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
        catch
        {
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
                IsValidTomcatProductPort(port))
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
                    if (state?.Port is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum &&
                        (Directory.Exists(state.PhysicalPath) || File.Exists(state.PhysicalPath)))
                    {
                        configuredPorts.Add(state.Port);
                    }
                }
                catch
                {
                }
            }
        }

        var activePorts = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();

        if (preferredPort is >= TomcatProductPortMinimum and <= TomcatProductPortMaximum &&
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

    private static async Task RunElevatedPowerShellAsync(string scriptPath, CancellationToken cancellationToken)
    {
        var result = await ProcessRunner.RunPowerShellFileAsync(
            scriptPath,
            elevated: true,
            cancellationToken: cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"IIS 配置脚本执行失败，退出码：{result.ExitCode}。");
        }
    }

    private static string? FindTomcatRoot()
    {
        return new ComponentLocator().FindTomcatRoot(
            TomcatComponentRequirements.StartupScript |
            TomcatComponentRequirements.ServerXml |
            TomcatComponentRequirements.WebAppsDirectory);
    }

    private static string? FindWar(string preparedPath)
    {
        if (File.Exists(preparedPath) && preparedPath.EndsWith(".war", StringComparison.OrdinalIgnoreCase))
        {
            return preparedPath;
        }

        if (!Directory.Exists(preparedPath))
        {
            return null;
        }

        return Directory.GetFiles(preparedPath, "*.war", SearchOption.AllDirectories).FirstOrDefault();
    }

    private static void ExtractZipSafely(string archivePath, string targetDirectory)
    {
        var targetRoot = Path.GetFullPath(targetDirectory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        using var archive = ZipFile.OpenRead(archivePath);
        foreach (var entry in archive.Entries)
        {
            var destination = Path.GetFullPath(Path.Combine(targetDirectory, entry.FullName));
            if (!destination.StartsWith(targetRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"压缩包包含越界路径，已取消安装：{entry.FullName}");
            }

            if (string.IsNullOrEmpty(entry.Name))
            {
                Directory.CreateDirectory(destination);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
        }
    }

    private static string? FindTomcatDocBase(string preparedPath)
    {
        var war = FindWar(preparedPath);
        if (!string.IsNullOrWhiteSpace(war))
        {
            return war;
        }

        if (!Directory.Exists(preparedPath))
        {
            return null;
        }

        if (Directory.Exists(Path.Combine(preparedPath, "WEB-INF")))
        {
            return preparedPath;
        }

        var webInf = Directory.EnumerateDirectories(preparedPath, "WEB-INF", SearchOption.AllDirectories).FirstOrDefault();

        return webInf is null ? null : Directory.GetParent(webInf)?.FullName;
    }

    private static string NormalizeProductRoot(string productRoot)
    {
        var current = productRoot;
        while (true)
        {
            var directories = Directory.GetDirectories(current);
            var files = Directory.GetFiles(current);
            if (directories.Length != 1 || files.Length != 0)
            {
                return current;
            }

            current = directories[0];
        }
    }

    private static void EnsureDirectoryContainsFiles(string path)
    {
        if (Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Any())
        {
            return;
        }

        Directory.Delete(path, recursive: true);
        throw new InvalidDataException("产品包解压后没有任何文件，已取消安装。");
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.GetFiles(source))
        {
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(source))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
            {
                continue;
            }

            CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
        }
    }

    private static void DeleteDownloadedPackage(string packagePath)
    {
        try
        {
            if (!IsPathInside(packagePath, DownloadRoot))
            {
                return;
            }

            if (Directory.Exists(packagePath))
            {
                Directory.Delete(packagePath, recursive: true);
            }
            else if (File.Exists(packagePath))
            {
                File.Delete(packagePath);
            }
        }
        catch
        {
            // A successful deployment must not be reported as failed only because its cache could not be removed.
        }
    }

    private static string DetectIisManagedRuntime(string appRoot)
    {
        try
        {
            if (Directory.EnumerateFiles(appRoot, "*.runtimeconfig.json", SearchOption.AllDirectories).Any())
            {
                return string.Empty;
            }

            var webConfig = Directory.EnumerateFiles(appRoot, "web.config", SearchOption.AllDirectories).FirstOrDefault();
            if (webConfig is null)
            {
                return "v4.0";
            }

            var text = File.ReadAllText(webConfig, Encoding.UTF8);
            if (text.Contains("<aspNetCore", StringComparison.OrdinalIgnoreCase))
            {
                return string.Empty;
            }

            if (System.Text.RegularExpressions.Regex.IsMatch(
                    text,
                    "targetFramework\\s*=\\s*[\\\"'](?:2\\.0|3\\.0|3\\.5)[\\\"']",
                    System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return "v2.0";
            }
        }
        catch
        {
            // Use the CLR 4 application-pool default when a legacy package has an unreadable config file.
        }

        return "v4.0";
    }

    internal static string ResolveIisPipelineMode(string? runEnvironment, string managedRuntimeVersion)
    {
        // ASP.NET Core pools have no CLR runtime and use the integrated module
        // pipeline.  For legacy products, retain the original AddSite mapping:
        // Framework4.0 => Integrated, every other declared framework => Classic.
        if (string.IsNullOrWhiteSpace(managedRuntimeVersion) || string.IsNullOrWhiteSpace(runEnvironment))
        {
            return "Integrated";
        }

        return runEnvironment!.Trim().Equals("Framework4.0", StringComparison.OrdinalIgnoreCase)
            ? "Integrated"
            : "Classic";
    }

    private static int SelectIisPort(string appcmd, string siteName)
    {
        try
        {
            var result = ProcessRunner.RunSynchronously(
                appcmd,
                $"list site {Compat.QuoteCommandLineArgument(siteName)} /text:bindings",
                Path.GetDirectoryName(appcmd),
                captureOutput: true,
                timeout: TimeSpan.FromSeconds(3));
            if (result.ExitCode == 0)
            {
                var match = System.Text.RegularExpressions.Regex.Match(result.StandardOutput, @"http/[^:]*:(?<port>\d+):", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
                if (match.Success && int.TryParse(match.Groups["port"].Value, out var existingPort))
                {
                    return existingPort;
                }
            }
        }
        catch
        {
        }

        var activePorts = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners()
            .Select(endpoint => endpoint.Port)
            .ToHashSet();
        for (var port = 8088; port <= 8188; port++)
        {
            if (!activePorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("IIS 端口池 8088-8188 已无可用端口。");
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        ClearAttributesRecursively(path);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Directory.Delete(path, recursive: true);
                return;
            }
            catch (IOException) when (attempt < 2)
            {
                Thread.Sleep(80);
            }
            catch (UnauthorizedAccessException) when (attempt < 2)
            {
                ClearAttributesRecursively(path);
                Thread.Sleep(80);
            }
        }

        // Surface the final exception with the exact path.  The caller must not
        // report a successful uninstall while an SVN working copy remains.
        Directory.Delete(path, recursive: true);
    }

    internal static void DeleteDirectoryForTest(string path) => DeleteDirectoryIfExists(path);

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.SetAttributes(path, FileAttributes.Normal);
            File.Delete(path);
        }
    }

    private static void ClearAttributesRecursively(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.SetAttributes(path, FileAttributes.Normal);
                return;
            }

            if (!Directory.Exists(path)) return;
            foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(file, FileAttributes.Normal); } catch { }
            }

            foreach (var directory in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
            {
                try { File.SetAttributes(directory, FileAttributes.Normal); } catch { }
            }

            try { File.SetAttributes(path, FileAttributes.Normal); } catch { }
        }
        catch
        {
            // Directory.Delete below supplies the actionable error if traversal
            // itself was blocked.
        }
    }

    private static void DeleteProductFiles(ProductItem product, string safeName)
    {
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ProductInstallPathResolver.ResolveProductDirectory(product),
            Path.Combine(ComponentPaths.WebRoot, safeName),
            Path.Combine(ComponentPaths.ProductDownloadRoot, safeName),
            $"{Path.Combine(ComponentPaths.ProductDownloadRoot, safeName)}.downloading",
            Path.Combine(ComponentPaths.LegacyProductDownloadRoot, safeName),
            $"{Path.Combine(ComponentPaths.LegacyProductDownloadRoot, safeName)}.downloading",
            Path.Combine(ComponentPaths.RuntimeRoot, "IISApps", safeName),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatProductRuns", safeName),
            Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "IISApps", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatProductRuns", safeName),
            Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", safeName),
            Path.Combine(ComponentPaths.LegacyInstalledProductRoot, safeName)
        };

        foreach (var root in new[]
                 {
                     ProductInstallPathResolver.ResolveProductRoot(product.InstallRoot),
                     WebRoot
                 })
        {
            foreach (var containerName in new[] { ".deploying", ".rollback" })
            {
                var container = Path.Combine(root, containerName);
                if (!Directory.Exists(container)) continue;
                foreach (var directory in Directory.EnumerateDirectories(container, $"{safeName}.*", SearchOption.TopDirectoryOnly))
                {
                    directories.Add(directory);
                }
            }
        }

        foreach (var directory in directories)
        {
            DeleteDirectoryIfExists(directory);
        }

        DeleteFileIfExists(GetIisStateFile(safeName));
        DeleteFileIfExists(GetTomcatStateFile(safeName));
        foreach (var scriptName in new[]
                 {
                     $"unbind-product-{safeName}.ps1",
                     $"remove-stale-iis-{safeName}.ps1",
                     $"bind-iis-{safeName}.ps1"
                 })
        {
            DeleteFileIfExists(Path.Combine(ComponentPaths.WorkRoot, scriptName));
        }
    }

    private static void ReportUninstallProgress(
        IProgress<ProductUninstallProgress>? progress,
        double percent,
        string status)
    {
        progress?.Report(new ProductUninstallProgress(Compat.Clamp(percent, 0, 100), status));
    }

    private static bool IsSafeTomcatAppBase(string value) =>
        value.IndexOfAny(['/', '\\']) < 0 &&
        System.Text.RegularExpressions.Regex.IsMatch(value, "^webapps[0-9]+$", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private static string SafeName(string value)
    {
        var chars = value.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray();
        var safe = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(safe) ? "product" : safe;
    }

    private static string EscapePowerShellPath(string path) => path.Replace("'", "''");

    private static bool PathsEqual(string left, string right) =>
        Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar)
            .Equals(Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);

    private static bool IsPathInside(string path, string root)
    {
        var fullPath = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetTomcatContextFile(string tomcatRoot, string safeName) =>
        Path.Combine(tomcatRoot, "conf", "Catalina", "localhost", $"{safeName}.xml");

    private static string GetIisStateFile(string safeName) =>
        Path.Combine(ComponentPaths.ProductStateRoot, $"{safeName}.json");

    private static string GetTomcatStateFile(string safeName) =>
        Path.Combine(ComponentPaths.ProductStateRoot, $"{safeName}.tomcat.json");

    private static string WebRoot => ComponentPaths.WebRoot;

    private static string DownloadRoot => ComponentPaths.ProductDownloadRoot;

    private static string StoreDataRoot => ComponentPaths.StoreDataRoot;

    private enum ProductRuntimeKind
    {
        Unknown,
        Iis,
        Tomcat
    }

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

    private sealed class ProductInstallTransaction : IDisposable
    {
        private readonly string? _backupRoot;
        private readonly string? _stagingContainer;
        private readonly bool _ownsSwap;
        private bool _finished;

        public ProductInstallTransaction(string productRoot, string preparedPath, string backupRoot, string stagingContainer)
        {
            ProductRoot = productRoot;
            PreparedPath = preparedPath;
            _backupRoot = backupRoot;
            _stagingContainer = stagingContainer;
            _ownsSwap = true;
        }

        private ProductInstallTransaction(string productRoot, string preparedPath)
        {
            ProductRoot = productRoot;
            PreparedPath = preparedPath;
        }

        public string ProductRoot { get; }
        public string PreparedPath { get; }
        public bool IsExisting => !_ownsSwap;

        public static ProductInstallTransaction ForExisting(string productRoot, string preparedPath) =>
            new(productRoot, preparedPath);

        public void Complete()
        {
            if (_finished)
            {
                return;
            }

            if (!string.IsNullOrWhiteSpace(_backupRoot))
            {
                DeleteDirectoryIfExists(_backupRoot!);
            }

            CleanupStaging();
            _finished = true;
        }

        public void Rollback()
        {
            if (_finished || !_ownsSwap)
            {
                _finished = true;
                return;
            }

            DeleteDirectoryIfExists(ProductRoot);
            if (!string.IsNullOrWhiteSpace(_backupRoot) && Directory.Exists(_backupRoot))
            {
                Directory.Move(_backupRoot, ProductRoot);
            }

            CleanupStaging();
            _finished = true;
        }

        public void Dispose()
        {
            if (!_finished)
            {
                Rollback();
            }
        }

        private void CleanupStaging()
        {
            if (!string.IsNullOrWhiteSpace(_stagingContainer))
            {
                DeleteDirectoryIfExists(_stagingContainer!);
            }
        }
    }

    private sealed class ProductConfigurationSnapshot
    {
        private readonly IReadOnlyDictionary<string, byte[]?> _files;

        private ProductConfigurationSnapshot(IReadOnlyDictionary<string, byte[]?> files)
        {
            _files = files;
        }

        public static ProductConfigurationSnapshot Capture(string productRoot, string preparedPath)
        {
            var files = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase);
            foreach (var file in ProductConfigurationService.GetConfigurationFilesForBackup(productRoot, preparedPath))
            {
                if (!File.Exists(file))
                {
                    files[file] = null;
                    continue;
                }

                var length = new FileInfo(file).Length;
                if (length > 8L * 1024 * 1024)
                {
                    throw new InvalidDataException(
                        $"产品配置文件过大，无法在更新前安全创建回滚副本：{file}");
                }

                files[file] = File.ReadAllBytes(file);
            }

            return new ProductConfigurationSnapshot(files);
        }

        public void Restore()
        {
            foreach (var pair in _files)
            {
                if (pair.Value is null)
                {
                    if (File.Exists(pair.Key))
                    {
                        File.SetAttributes(pair.Key, FileAttributes.Normal);
                        File.Delete(pair.Key);
                    }

                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(pair.Key)!);
                File.WriteAllBytes(pair.Key, pair.Value);
            }
        }
    }
}

