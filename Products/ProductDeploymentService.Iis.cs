using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text;
using System.Xml.Linq;

namespace MCPanel;

// IIS deployment: site/application binding, application pool runtime and the IIS state records.
public sealed partial class ProductDeploymentService
{
    public async Task<ProductDeploymentResult> RepairIisBindingAsync(ProductItem product, string installedPath, CancellationToken cancellationToken = default)
    {
        if (product.IsExternalPlatform)
        {
            var externalResult = await DeployToIisAsync(product, installedPath, cancellationToken);
            await NginxProductProxyService.TrySyncAsync(cancellationToken);
            return externalResult;
        }
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

    public static IisProductDeploymentInfo? LoadIisDeploymentInfo(string productId)
    {
        ReconcileRuntimeDeploymentState();
        var file = GetIisStateFile(PathUtil.SafeProductName(productId));
        if (!File.Exists(file))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<IisProductDeploymentInfo>(File.ReadAllText(file, Encoding.UTF8));
        }
        catch (Exception error)
        {
            SuppressedErrorLog.Record("读取 IIS 部署记录", error, file);
            return null;
        }
    }

    public static IReadOnlyList<IisProductDeploymentInfo> LoadIisDeploymentInfos()
    {
        ReconcileRuntimeDeploymentState();
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
            catch (Exception error)
            {
                SuppressedErrorLog.Record("读取 IIS 部署记录", error, stateFile);
            }
        }

        return deployments
            .GroupBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(info => info.Port)
            .ThenBy(info => info.ProductId, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task RemoveStaleIisBindingAsync(string productId, CancellationToken cancellationToken)
    {
        var safeName = PathUtil.SafeProductName(productId);
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
                $appcmd='{{PathUtil.EscapePowerShellLiteral(appcmd)}}'
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
        var safeName = PathUtil.SafeProductName(product.ProductId);
        if (!ProductInstallPathResolver.IsInsideProductRoot(sourceRoot, product))
        {
            throw new InvalidOperationException($"IIS 产品目录必须位于产品安装根目录内：{sourceRoot}");
        }
        var appRoot = sourceRoot;

        const string siteName = "MCPanel";
        var port = SelectIisPort(appcmd, "MCPanel");
        var appPath = $"/{safeName}";
        var poolName = safeName;
        var managedRuntimeVersion = product.ExternalIisRuntime ?? DetectIisManagedRuntime(appRoot);
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
            $appcmd='{{PathUtil.EscapePowerShellLiteral(appcmd)}}'
            $siteName='{{PathUtil.EscapePowerShellLiteral(siteName)}}'
            $siteRoot='{{PathUtil.EscapePowerShellLiteral(siteRoot)}}'
            $appPath='{{PathUtil.EscapePowerShellLiteral(appPath)}}'
            $appRoot='{{PathUtil.EscapePowerShellLiteral(appRoot)}}'
            $poolName='{{PathUtil.EscapePowerShellLiteral(poolName)}}'
            $managedRuntimeVersion='{{PathUtil.EscapePowerShellLiteral(managedRuntimeVersion)}}'
            $managedPipelineMode='{{PathUtil.EscapePowerShellLiteral(managedPipelineMode)}}'
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
        // Match the original ITMCStore application-pool behavior while
        // retaining MCPanel's current IIS 7+ appcmd management path.
        // CLR selection (v2.0/v4.0/no managed CLR) remains independent;
        // every IIS product uses the Integrated request-processing pipeline.
        _ = runEnvironment;
        _ = managedRuntimeVersion;
        return "Integrated";
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
                var existingPort = ParseIisHttpBindingPort(result.StandardOutput);
                if (existingPort.HasValue)
                {
                    return existingPort.Value;
                }
            }
        }
        catch
        {
        }

        var activePorts = SystemProbe.GetActiveTcpPorts(throwOnError: true);
        for (var port = 8088; port <= 8188; port++)
        {
            if (!activePorts.Contains(port))
            {
                return port;
            }
        }

        throw new InvalidOperationException("IIS 端口池 8088-8188 已无可用端口。");
    }

    private static string GetIisStateFile(string safeName) =>
        Path.Combine(ComponentPaths.ProductStateRoot, $"{safeName}.json");
}
