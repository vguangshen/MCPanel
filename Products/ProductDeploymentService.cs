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

public sealed partial class ProductDeploymentService
{
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
                    FileUtil.DeleteDirectory(Path.Combine(ComponentPaths.RuntimeRoot, "TomcatInstances", PathUtil.SafeProductName(state.ProductId)));
                    FileUtil.DeleteDirectory(Path.Combine(ComponentPaths.LegacyRuntimeRoot, "TomcatInstances", PathUtil.SafeProductName(state.ProductId)));
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
            PanelLog.Write(PanelLog.WorkLog("deployment-state-recovery.log"), $"部署状态文件损坏，已隔离而不是删除：{stateFile} -> {quarantine}", error);
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

            var cleanupWarning = transaction.Complete();
            if (cleanupWarning is not null)
                result = result with { Message = result.Message + Environment.NewLine + cleanupWarning };
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

    internal static ProductRuntimeKind DetectRuntime(ProductItem product, string packagePath)
    {
        var declaredRuntime = product.RunEnvironment?.Trim() ?? string.Empty;
        if (TextFiles.ContainsAny(declaredRuntime, "tomcat", "java web", "jsp", "servlet"))
        {
            return ProductRuntimeKind.Tomcat;
        }

        if (TextFiles.ContainsAny(declaredRuntime, "iis", "asp", ".net", "framework"))
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
        if (TextFiles.ContainsAny(language, "java", "jsp"))
        {
            return ProductRuntimeKind.Tomcat;
        }

        if (TextFiles.ContainsAny(language, "c#", "visual basic", "vb.net", ".net"))
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

    private static ProductInstallTransaction PrepareProductFiles(ProductItem product, string packagePath, ProductRuntimeKind kind)
    {
        var installRoot = ProductInstallPathResolver.ResolveProductRoot(product.InstallRoot);
        Directory.CreateDirectory(installRoot);

        var safeName = PathUtil.SafeProductName(product.ProductId);
        var productRoot = Path.Combine(installRoot, safeName);
        if (PathUtil.PathsEqual(packagePath, productRoot))
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
                FileUtil.CopyDirectory(packagePath, stagingContent);
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
            FileUtil.DeleteDirectory(stagingContainer);
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

    public Task UninstallAsync(
        ProductItem product,
        CancellationToken cancellationToken = default,
        IProgress<ProductUninstallProgress>? progress = null)
        => Task.Run(() => UninstallCoreAsync(product, cancellationToken, progress), cancellationToken);

    private async Task UninstallCoreAsync(ProductItem product,
        CancellationToken cancellationToken, IProgress<ProductUninstallProgress>? progress)
    {
        var safeName = PathUtil.SafeProductName(product.ProductId);
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
                $appcmd='{{PathUtil.EscapePowerShellLiteral(appcmd)}}'
                $appName='MCPanel/{{safeName}}'
                $siteName='MCPanel'
                $siteRoot='{{PathUtil.EscapePowerShellLiteral(Path.Combine(ComponentPaths.RuntimeRoot, "IISRoot"))}}'
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
            // Stop the service wrapper first so the later restore is not blocked
            // by an SCM service that still says Running while its Java child is gone.
            // SCM status itself is never used as the success criterion.
            ReportUninstallProgress(progress, 42, "正在停止 Tomcat 全部应用模式...");
            if (tomcatRoot is not null && TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
            {
                try
                {
                    TomcatWindowsServiceManager.Stop();
                }
                catch (Exception serviceError)
                {
                    EnvironmentOperationDiagnostics.RecordFailure(
                        "产品管理",
                        $"卸载 {product.ProductId} 时发送 Tomcat Windows Service 停止请求",
                        serviceError);
                }
            }
            await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
        }

        var routeSynced = false;
        try
        {
            if (tomcatArtifactsExist && tomcatRoot is not null)
            {
                ReportUninstallProgress(progress, 55, "正在删除 Tomcat 服务、端口和应用目录...");
                var expectedTomcatPort = tomcatState?.Port ?? tomcatService?.Port;
                await new TomcatProductInstanceManager().RemoveAsync(safeName, expectedTomcatPort, cancellationToken);
                DeleteFileIfExists(GetTomcatContextFile(tomcatRoot, safeName));
                if (!product.IsExternalPlatform)
                {
                    FileUtil.DeleteDirectory(Path.Combine(tomcatRoot, "webapps", safeName));
                    DeleteFileIfExists(Path.Combine(tomcatRoot, "webapps", $"{safeName}.war"));
                }
                await RemoveTomcatProductServiceAsync(tomcatRoot, safeName, cancellationToken);

                // Each product Service uses a private webapps<port> appBase.
                // It is not part of the SVN product root and therefore needs an
                // explicit removal as well.
                var appBase = tomcatState?.HostAppBase ?? tomcatService?.HostAppBase;
                if (!product.IsExternalPlatform && !string.IsNullOrWhiteSpace(appBase) && IsSafeTomcatAppBase(appBase!))
                {
                    FileUtil.DeleteDirectory(Path.Combine(tomcatRoot, appBase));
                }
            }

            // Keep the product route alive until the IIS/Tomcat bindings have
            // been removed successfully.  If this stage fails, the product
            // files and runtime state are still present for a clean retry.
            ReportUninstallProgress(progress, 68, "正在移除 Nginx 域名、SSL 和代理配置...");
            await ProductWebsiteService.RemoveForProductAsync(product.ProductId, cancellationToken);

            ReportUninstallProgress(progress, 78, "正在删除产品目录、SVN 元数据、下载缓存和临时文件...");
            cancellationToken.ThrowIfCancellationRequested();
            if (product.IsExternalPlatform)
            {
                DeleteFileIfExists(GetIisStateFile(safeName));
                DeleteFileIfExists(GetTomcatStateFile(safeName));
                new ManualPlatformStore().Remove(product.ProductId);
            }
            else DeleteProductFiles(product, safeName);
            new ProductInstallOrderStore().Remove(product.ProductId);
            TomcatProductStartupManager.RefreshRegistration();
            ReportUninstallProgress(progress, 92, "正在同步剩余产品的 Nginx 路由和运行状态...");
            routeSynced = await NginxProductProxyService.TrySyncAsync(CancellationToken.None);
        }
        finally
        {
            if (sharedTomcatWasRunning)
            {
                try
                {
                    ReportUninstallProgress(progress, 96, "正在恢复 Tomcat 全部应用模式，请稍候...");
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
        ReportUninstallProgress(progress, 100, routeSynced
            ? "卸载完成，产品目录和安装记录已清理。"
            : "卸载完成，但 Nginx 剩余产品路由未自动同步；请在“环境”页面重新同步 Nginx。");
    }

    private static void ReportUninstallProgress(
        IProgress<ProductUninstallProgress>? progress,
        double percent,
        string status)
    {
        progress?.Report(new ProductUninstallProgress(Compat.Clamp(percent, 0, 100), status));
    }

    private static string WebRoot => ComponentPaths.WebRoot;

    private static string DownloadRoot => ComponentPaths.ProductDownloadRoot;

    private static string StoreDataRoot => ComponentPaths.StoreDataRoot;

    internal enum ProductRuntimeKind
    {
        Unknown,
        Iis,
        Tomcat
    }

}

