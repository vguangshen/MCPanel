using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed partial class EnvironmentInstaller
{
    internal Task RepairIisConfigurationAsync(Action<InstallProgress> progress, CancellationToken cancellationToken = default)
    {
        if (!new EnvironmentRuntimeService().GetState(EnvironmentKind.Iis).IsInstalled)
        {
            throw new InvalidOperationException("未检测到 IIS Web 服务，无法只修复配置；请先安装 IIS。");
        }

        return InstallIisAsync(EnvironmentDownloadSettings.Load(), progress, cancellationToken, repairOnly: true);
    }

    private async Task InstallIisAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken, bool repairOnly = false)
    {
        TryDeleteFile(IisPendingUninstallMarker);
        TryDeleteFile(IisUninstalledMarker);
        var temp = GetTempDirectory();
        var rewriteMsi = Path.Combine(GetPackageDirectory(), "URLRewrite.msi");
        // A configuration-only repair needs no network connection when URL Rewrite
        // is already registered in IIS. The script independently rechecks the module.
        if (!repairOnly || EnvironmentRuntimeService.TryProbeIisRewriteModule() != true)
        {
            progress(new InstallProgress(5, "正在下载 URL Rewrite 组件...", InstallProgressStage.Downloading, 0));
            rewriteMsi = await DownloadAbsoluteFileAsync(downloads.IisUrlRewriteUrl, GetPackageDirectory(), "URLRewrite.msi", progress, 5, 20, cancellationToken);
        }

        progress(InstallingProgress(25, repairOnly ? "正在生成 IIS 配置修复脚本..." : "正在生成 IIS 安装脚本...", 0));
        var script = Path.Combine(temp, "install-iis.ps1");
        await FileCompat.WriteAllTextAsync(script, BuildIisScript(rewriteMsi, repairOnly), new UTF8Encoding(true), cancellationToken);

        // Persist an incomplete-configuration state before starting the elevated script.
        // A failed or interrupted repair must never make an existing W3SVC disappear
        // from the UI, and successful completion is the only time we clear this marker.
        var repairMarker = IisConfigurationRepairMarkerPath;
        Directory.CreateDirectory(Path.GetDirectoryName(repairMarker)!);
        AtomicFile.WriteAllText(repairMarker, "IIS 配置尚未完成，请执行修复配置。", Encoding.UTF8);
        progress(InstallingProgress(35, repairOnly ? "正在修复 IIS 配置..." : "正在启用 IIS 组件...", 8));
        await RunElevatedPowerShellAsync(script, progress, 10, 95, cancellationToken, requireExistingAdministrator: true);
        TryDeleteFile(repairMarker);
        EnvironmentRuntimeService.InvalidateIisModuleProbe();
        TryDeleteFile(IisInstallRestartMarkerPath);
        TryDeleteFile(IisPendingUninstallMarker);
        TryDeleteFile(IisUninstalledMarker);
        progress(InstallingProgress(100, "IIS 安装和基础配置完成。"));
    }

    internal static string BuildIisScript(string rewriteMsi, bool repairOnly = false)
    {
        var requiredFeatures = new[]
        {
            "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-HttpErrors",
            "IIS-HttpRedirect", "IIS-ApplicationDevelopment", "IIS-Security", "IIS-URLAuthorization",
            "IIS-RequestFiltering", "IIS-NetFxExtensibility45", "IIS-HealthAndDiagnostics", "IIS-HttpLogging",
            "IIS-RequestMonitor", "IIS-HttpTracing", "IIS-Performance", "IIS-HttpCompressionStatic",
            "IIS-HttpCompressionDynamic", "IIS-ManagementConsole", "IIS-ManagementScriptingTools",
            "IIS-IIS6ManagementCompatibility", "IIS-WebServerManagementTools", "IIS-Metabase",
            "IIS-ISAPIExtensions", "IIS-ISAPIFilter", "IIS-StaticContent", "IIS-DefaultDocument",
            "IIS-DirectoryBrowsing", "IIS-ASPNET45", "NetFx4Extended-ASPNET45", "IIS-ASP", "IIS-CGI",
            "IIS-ServerSideIncludes", "IIS-BasicAuthentication", "IIS-WindowsAuthentication", "IIS-ODBCLogging"
        };
        var legacyFeatures = new[] { "IIS-NetFxExtensibility", "IIS-ASPNET" };
        // IIS 7/7.5 predates the DISM PowerShell module and has no IIS-ASPNET45
        // feature. pkgmgr is the documented Windows Server 2008 feature installer.
        var iis7Features = new[]
        {
            "IIS-WebServerRole", "IIS-WebServer", "IIS-CommonHttpFeatures", "IIS-StaticContent",
            "IIS-DefaultDocument", "IIS-DirectoryBrowsing", "IIS-HttpErrors", "IIS-HttpRedirect",
            "IIS-ApplicationDevelopment", "IIS-ASP", "IIS-CGI", "IIS-ISAPIExtensions",
            "IIS-ISAPIFilter", "IIS-ServerSideIncludes", "IIS-Security", "IIS-RequestFiltering",
            "IIS-URLAuthorization", "IIS-BasicAuthentication", "IIS-WindowsAuthentication",
            "IIS-HealthAndDiagnostics", "IIS-HttpLogging", "IIS-RequestMonitor",
            "IIS-HttpTracing", "IIS-ODBCLogging", "IIS-Performance",
            "IIS-HttpCompressionStatic", "IIS-HttpCompressionDynamic", "IIS-WebServerManagementTools",
            "IIS-ManagementConsole", "IIS-ManagementScriptingTools", "IIS-IIS6ManagementCompatibility",
            "IIS-Metabase", "WAS-WindowsActivationService", "WAS-ProcessModel",
            "WAS-NetFxEnvironment", "WAS-ConfigurationAPI"
        };
        var log = Path.Combine(GetTempDirectory(), "install-iis.log");
        var restartMarker = IisInstallRestartMarkerPath;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$log='{EscapePowerShellPath(log)}'");
        sb.AppendLine($"$restartMarker='{EscapePowerShellPath(restartMarker)}'");
        sb.AppendLine("$restartNeeded=$false");
        sb.AppendLine("Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Start-Transcript -Path $log | Out-Null");
        sb.AppendLine("function Test-RestartNeeded($result) { if ($null -eq $result -or $null -eq $result.RestartNeeded) { return $false }; $text=$result.RestartNeeded.ToString(); return $text -eq 'True' -or $text -eq 'Yes' }");
        sb.AppendLine("function Mark-RestartRequired($reason) { $dir=Split-Path $restartMarker -Parent; New-Item -ItemType Directory -Path $dir -Force | Out-Null; Set-Content -LiteralPath $restartMarker -Value $reason -Encoding UTF8; Write-Output $reason }");
        sb.AppendLine("try {");
        if (!repairOnly)
        {
        sb.AppendLine("  $osVersion=[Environment]::OSVersion.Version");
        sb.AppendLine("  $legacyIis=$osVersion.Major -eq 6 -and $osVersion.Minor -lt 2");
        sb.AppendLine("  if ($legacyIis) {");
        sb.AppendLine(@"    $pkgmgr=Join-Path $env:windir 'System32\pkgmgr.exe'");
        sb.AppendLine("    if (!(Test-Path -LiteralPath $pkgmgr)) { throw 'Windows Server 2008 的 pkgmgr.exe 不存在，无法安装 IIS 7。' }");
        sb.AppendLine($"    $pkgmgrArgs='/iu:{string.Join(";", iis7Features)}'");
        sb.AppendLine("    $result=Start-Process -FilePath $pkgmgr -ArgumentList $pkgmgrArgs -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop");
        sb.AppendLine("    if ($result.ExitCode -eq 3010) { $restartNeeded=$true }");
        sb.AppendLine("    elseif ($result.ExitCode -ne 0) { throw ('IIS 7 组件安装失败，pkgmgr 退出码：' + $result.ExitCode) }");
        sb.AppendLine("    if ($restartNeeded) { Mark-RestartRequired 'IIS 7 功能安装要求重启后继续。'; throw 'IIS_RESTART_REQUIRED' }");
        // Windows PowerShell 2 can load the CLR 2 Environment type even when
        // the MCPanel process itself targets .NET 4.6.2.
        sb.AppendLine(@"    $framework64=Join-Path $env:windir 'Microsoft.NET\Framework64\v4.0.30319\aspnet_regiis.exe'");
        sb.AppendLine("    $framework=if (Test-Path -LiteralPath $framework64) { 'Framework64' } else { 'Framework' }");
        sb.AppendLine(@"    $aspnetRegiis=Join-Path $env:windir ('Microsoft.NET\' + $framework + '\v4.0.30319\aspnet_regiis.exe')");
        sb.AppendLine("    if (!(Test-Path -LiteralPath $aspnetRegiis)) { throw '未找到 .NET Framework 4 的 aspnet_regiis.exe。请先安装 .NET Framework 4.6.2。' }");
        sb.AppendLine("    & $aspnetRegiis -ir");
        sb.AppendLine("    if ($LASTEXITCODE -ne 0) { throw ('ASP.NET 4 注册失败，退出码：' + $LASTEXITCODE) }");
        sb.AppendLine("  } else {");
        foreach (var feature in requiredFeatures)
        {
            sb.AppendLine($"    $featureResult=Enable-WindowsOptionalFeature -Online -FeatureName {feature} -All -NoRestart -ErrorAction Stop");
            sb.AppendLine("    if (Test-RestartNeeded $featureResult) { $restartNeeded=$true }");
        }
        sb.AppendLine("  try {");
        sb.AppendLine("    $netFx3=Get-WindowsOptionalFeature -Online -FeatureName NetFx3 -ErrorAction Stop");
        sb.AppendLine("    if ($netFx3.State -ne 'Enabled') { $netFx3Result=Enable-WindowsOptionalFeature -Online -FeatureName NetFx3 -All -NoRestart -ErrorAction Stop; if (Test-RestartNeeded $netFx3Result) { $restartNeeded=$true } }");
        foreach (var feature in legacyFeatures)
        {
            sb.AppendLine($"    $legacyResult=Enable-WindowsOptionalFeature -Online -FeatureName {feature} -All -NoRestart -ErrorAction Stop");
            sb.AppendLine("    if (Test-RestartNeeded $legacyResult) { $restartNeeded=$true }");
        }
        sb.AppendLine("  } catch { Write-Output ('兼容性提示：ASP.NET 2.0/3.5 功能未完全启用；现代 ASP.NET 4.x/IIS 功能继续安装。原因：' + $_.Exception.Message) }");
        sb.AppendLine("    if ($restartNeeded) { Mark-RestartRequired 'Windows 功能安装要求重启后继续 IIS 安装。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine("  }");
        }
        sb.AppendLine(@"  $appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'");
        sb.AppendLine(@"  if (!(Test-Path $appcmd)) { throw '未找到 IIS 配置工具 appcmd.exe。' }");
        sb.AppendLine(@"  $docs=@('default.html','default.asp','default.aspx','index.php','index.asp','index.aspx')");
        sb.AppendLine(@"  $defaultDocConfig = (& $appcmd list config /section:defaultDocument | Out-String)");
        sb.AppendLine(@"  if ($LASTEXITCODE -ne 0) { throw '无法读取 IIS 默认文档配置。' }");
        sb.AppendLine(@"  $defaultDocXml = [xml]('<configuration>' + $defaultDocConfig + '</configuration>')");
        sb.AppendLine(@"  $existingDocs = @($defaultDocXml.SelectNodes('//defaultDocument/files/add') | ForEach-Object { $_.GetAttribute('value') })");
        sb.AppendLine(@"  foreach ($doc in $docs) {");
        sb.AppendLine(@"    if ($existingDocs -contains $doc) { continue }");
        sb.AppendLine(@"    & $appcmd set config /section:defaultDocument ""/+files.[value='$doc']""");
        sb.AppendLine(@"    if ($LASTEXITCODE -ne 0) { throw ('无法添加 IIS 默认文档 ' + $doc + '；appcmd 退出码：' + $LASTEXITCODE) }");
        sb.AppendLine(@"    $existingDocs += $doc");
        sb.AppendLine(@"  }");
        sb.AppendLine(@"  & $appcmd set config /section:asp /enableParentPaths:True");
        sb.AppendLine(@"  if ($LASTEXITCODE -ne 0) { throw ('无法启用 ASP ParentPaths；appcmd 退出码：' + $LASTEXITCODE) }");
        var escapedRewriteMsi = EscapePowerShellPath(rewriteMsi);
        sb.AppendLine($"  $rewriteMsi = '{escapedRewriteMsi}'");
        sb.AppendLine("  $moduleOutput = (& $appcmd list modules 2>&1 | Out-String)");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw '无法查询 IIS 已注册的模块。' }");
        sb.AppendLine("  if ($moduleOutput -notmatch 'RewriteModule') {");
        sb.AppendLine("    if (!(Test-Path -LiteralPath $rewriteMsi)) { throw '未找到 URL Rewrite 安装包。' }");
        sb.AppendLine("    $rewriteProcess = Start-Process msiexec.exe -ArgumentList ('/i \"' + $rewriteMsi + '\" /qn /norestart') -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop");
        sb.AppendLine("    if ($rewriteProcess.ExitCode -eq 3010) { Mark-RestartRequired 'URL Rewrite 安装完成，但 Windows Installer 要求重启后继续。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine("    if ($rewriteProcess.ExitCode -ne 0) { throw ('URL Rewrite 安装失败，退出码：' + $rewriteProcess.ExitCode) }");
        sb.AppendLine("    $moduleOutput = (& $appcmd list modules 2>&1 | Out-String)");
        sb.AppendLine("    if ($LASTEXITCODE -ne 0 -or $moduleOutput -notmatch 'RewriteModule') { throw 'URL Rewrite MSI 已完成，但 IIS 未检测到 RewriteModule。' }");
        sb.AppendLine("  }");
        sb.AppendLine(@"  $net = Join-Path $env:windir 'System32\net.exe'");
        sb.AppendLine("  if (!(Test-Path -LiteralPath $net)) { throw '未找到 Windows 服务管理工具 net.exe。' }");
        sb.AppendLine("  if ((Get-Service -Name W3SVC -ErrorAction Stop).Status -ne 'Running') { & $net start W3SVC; if ($LASTEXITCODE -ne 0) { throw ('IIS 启动失败，退出码：' + $LASTEXITCODE) } }");
        sb.AppendLine("  foreach($serviceName in @('WAS','W3SVC')) { $service=Get-Service -Name $serviceName -ErrorAction Stop; if ($service.Status -ne 'Running') { Start-Service -Name $serviceName -ErrorAction Stop }; $service=Get-Service -Name $serviceName -ErrorAction Stop; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)); if ($service.Status -ne 'Running') { throw ($serviceName + ' 未进入 Running 状态。') } }");
        sb.AppendLine("  Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("} finally { try { Stop-Transcript | Out-Null } catch { } }");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }
}
