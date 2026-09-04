from pathlib import Path
import re


def replace_exact(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 match, got {count}")
    return text.replace(old, new, 1)


def replace_regex(text, pattern, replacement, label):
    updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)
    if count != 1:
        raise RuntimeError(f"{label}: expected 1 regex match, got {count}")
    return updated


sql_catalog = r'''using Microsoft.Win32;

namespace MCPanel;

/// <summary>
/// SQL Server releases supported by the environment installer.
/// Support flags follow Microsoft's SQL Server/Windows compatibility matrix.
/// Unsupported entries remain visible in the UI, but cannot be selected.
/// </summary>
public sealed record SqlServerReleaseDefinition(string Id, string DisplayName)
{
    public SqlServerOsSupport OsSupport => SqlServerOsCompatibility.GetCurrentSupport(Id);
    public bool IsSupported => OsSupport.IsSupported;
    public string SupportNote => OsSupport.Message;
    public string DisplayNameWithSupport => IsSupported ? DisplayName : $"{DisplayName}（当前系统不支持）";
    public override string ToString() => DisplayNameWithSupport;
}

internal enum WindowsSqlCompatibilityFamily
{
    Unknown,
    WindowsServer2025,
    WindowsServer2022,
    Windows11,
    WindowsServer2019,
    Windows10,
    WindowsServer2016,
    WindowsServer2012R2,
    Windows81,
    WindowsServer2012,
    Windows8,
    Legacy
}

public sealed record SqlServerOsSupport(bool IsSupported, string Message);

internal static class SqlServerOsCompatibility
{
    private static readonly Lazy<(WindowsSqlCompatibilityFamily Family, string DisplayName)> Current = new(DetectCurrent);

    public static SqlServerOsSupport GetCurrentSupport(string releaseId)
    {
        var current = Current.Value;
        return GetSupport(releaseId, current.Family, current.DisplayName);
    }

    internal static SqlServerOsSupport GetSupport(
        string releaseId,
        WindowsSqlCompatibilityFamily family,
        string? osDisplayName = null)
    {
        var normalized = releaseId ?? string.Empty;
        var supported = family switch
        {
            WindowsSqlCompatibilityFamily.WindowsServer2025 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id),
            WindowsSqlCompatibilityFamily.WindowsServer2022 or WindowsSqlCompatibilityFamily.Windows11 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id),
            WindowsSqlCompatibilityFamily.WindowsServer2019 or WindowsSqlCompatibilityFamily.Windows10 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2025Id,
                SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id),
            WindowsSqlCompatibilityFamily.WindowsServer2016 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2022Id,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id),
            WindowsSqlCompatibilityFamily.WindowsServer2012R2 or
            WindowsSqlCompatibilityFamily.Windows81 or
            WindowsSqlCompatibilityFamily.WindowsServer2012 or
            WindowsSqlCompatibilityFamily.Windows8 => IsOneOf(normalized,
                SqlServerReleaseCatalog.SqlServer2017Id,
                SqlServerReleaseCatalog.SqlServer2012Id,
                SqlServerReleaseCatalog.SqlServer2008Id),
            _ => true
        };

        if (supported)
        {
            if (normalized == SqlServerReleaseCatalog.SqlServer2008Id &&
                family is WindowsSqlCompatibilityFamily.WindowsServer2012R2 or
                    WindowsSqlCompatibilityFamily.Windows81 or
                    WindowsSqlCompatibilityFamily.WindowsServer2012 or
                    WindowsSqlCompatibilityFamily.Windows8)
            {
                return new(true, "Microsoft 官方兼容矩阵要求 SQL Server 2008 SP4；保留该旧版兼容入口。");
            }

            return new(true, string.Empty);
        }

        var release = SqlServerReleaseCatalog.Options.FirstOrDefault(option =>
            string.Equals(option.Id, normalized, StringComparison.OrdinalIgnoreCase));
        var releaseName = release?.DisplayName ?? normalized;
        var osName = string.IsNullOrWhiteSpace(osDisplayName) ? FamilyDisplayName(family) : osDisplayName!;
        return new(false, $"Microsoft 官方兼容矩阵不支持 {releaseName} 安装在 {osName}。请选择受支持的 SQL Server 版本。");
    }

    private static bool IsOneOf(string value, params string[] supported) =>
        supported.Any(item => string.Equals(item, value, StringComparison.OrdinalIgnoreCase));

    private static (WindowsSqlCompatibilityFamily Family, string DisplayName) DetectCurrent()
    {
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var productName = Convert.ToString(key?.GetValue("ProductName"))?.Trim() ?? string.Empty;
            var buildText = Convert.ToString(key?.GetValue("CurrentBuildNumber"))?.Trim();
            var build = int.TryParse(buildText, out var parsedBuild)
                ? parsedBuild
                : Environment.OSVersion.Version.Build;
            var isServer = productName.Contains("Server", StringComparison.OrdinalIgnoreCase);
            var family = Classify(isServer, build);
            var displayName = string.IsNullOrWhiteSpace(productName)
                ? FamilyDisplayName(family)
                : productName;
            return (family, displayName);
        }
        catch
        {
            var version = Environment.OSVersion.Version;
            return (WindowsSqlCompatibilityFamily.Unknown, $"Windows {version}");
        }
    }

    internal static WindowsSqlCompatibilityFamily Classify(bool isServer, int build)
    {
        if (isServer)
        {
            if (build >= 26100) return WindowsSqlCompatibilityFamily.WindowsServer2025;
            if (build >= 20348) return WindowsSqlCompatibilityFamily.WindowsServer2022;
            if (build >= 17763) return WindowsSqlCompatibilityFamily.WindowsServer2019;
            if (build >= 14393) return WindowsSqlCompatibilityFamily.WindowsServer2016;
            if (build >= 9600) return WindowsSqlCompatibilityFamily.WindowsServer2012R2;
            if (build >= 9200) return WindowsSqlCompatibilityFamily.WindowsServer2012;
            return WindowsSqlCompatibilityFamily.Legacy;
        }

        if (build >= 22000) return WindowsSqlCompatibilityFamily.Windows11;
        if (build >= 10240) return WindowsSqlCompatibilityFamily.Windows10;
        if (build >= 9600) return WindowsSqlCompatibilityFamily.Windows81;
        if (build >= 9200) return WindowsSqlCompatibilityFamily.Windows8;
        return WindowsSqlCompatibilityFamily.Legacy;
    }

    private static string FamilyDisplayName(WindowsSqlCompatibilityFamily family) => family switch
    {
        WindowsSqlCompatibilityFamily.WindowsServer2025 => "Windows Server 2025",
        WindowsSqlCompatibilityFamily.WindowsServer2022 => "Windows Server 2022",
        WindowsSqlCompatibilityFamily.Windows11 => "Windows 11",
        WindowsSqlCompatibilityFamily.WindowsServer2019 => "Windows Server 2019",
        WindowsSqlCompatibilityFamily.Windows10 => "Windows 10",
        WindowsSqlCompatibilityFamily.WindowsServer2016 => "Windows Server 2016",
        WindowsSqlCompatibilityFamily.WindowsServer2012R2 => "Windows Server 2012 R2",
        WindowsSqlCompatibilityFamily.Windows81 => "Windows 8.1",
        WindowsSqlCompatibilityFamily.WindowsServer2012 => "Windows Server 2012",
        WindowsSqlCompatibilityFamily.Windows8 => "Windows 8",
        _ => "当前 Windows"
    };
}

public static class SqlServerReleaseCatalog
{
    public const string SqlServer2025Id = "sqlserver-2025";
    public const string SqlServer2025EnterpriseDeveloperId = "sqlserver-2025-enterprise-developer";
    public const string SqlServer2022Id = "sqlserver-2022";
    public const string SqlServer2017Id = "sqlserver-2017";
    public const string SqlServer2012Id = "sqlserver-2012";
    public const string SqlServer2008Id = "sqlserver-2008";

    public static SqlServerReleaseDefinition SqlServer2025 { get; } = new(SqlServer2025Id, "SQL Server 2025 Express");
    public static SqlServerReleaseDefinition SqlServer2025EnterpriseDeveloper { get; } = new(SqlServer2025EnterpriseDeveloperId, "SQL Server 2025 Enterprise Developer");
    public static SqlServerReleaseDefinition SqlServer2022 { get; } = new(SqlServer2022Id, "SQL Server 2022 Express");
    public static SqlServerReleaseDefinition SqlServer2017 { get; } = new(SqlServer2017Id, "SQL Server 2017 Express");
    public static SqlServerReleaseDefinition SqlServer2012 { get; } = new(SqlServer2012Id, "SQL Server 2012 Express SP4");
    public static SqlServerReleaseDefinition SqlServer2008 { get; } = new(SqlServer2008Id, "SQL Server 2008 Express");

    public static IReadOnlyList<SqlServerReleaseDefinition> Options { get; } =
    [
        SqlServer2025,
        SqlServer2025EnterpriseDeveloper,
        SqlServer2022,
        SqlServer2017,
        SqlServer2012,
        SqlServer2008
    ];

    public static SqlServerReleaseDefinition Recommended
    {
        get
        {
            var supported = Options.FirstOrDefault(option => option.IsSupported);
            if (supported is not null)
            {
                return supported;
            }

            var version = Environment.OSVersion.Version;
            if (version.Major >= 10 && version.Build >= 22000) return SqlServer2025;
            if (version.Major >= 10) return SqlServer2022;
            if (version.Major == 6 && version.Minor >= 3) return SqlServer2017;
            if (version.Major == 6 && version.Minor == 2) return SqlServer2012;
            return SqlServer2008;
        }
    }

    public static bool Contains(string? id) =>
        !string.IsNullOrWhiteSpace(id) &&
        Options.Any(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase));

    public static bool IsSupported(string? id) => Contains(id) && Resolve(id).IsSupported;

    public static SqlServerReleaseDefinition? FindByInstallation(int majorVersion, string? edition)
    {
        var editionText = edition ?? string.Empty;
        var isDeveloper = editionText.Contains("Developer", StringComparison.OrdinalIgnoreCase);
        var isExpress = editionText.Contains("Express", StringComparison.OrdinalIgnoreCase);

        return majorVersion switch
        {
            17 when isDeveloper => SqlServer2025EnterpriseDeveloper,
            17 when isExpress => SqlServer2025,
            16 when isExpress => SqlServer2022,
            14 when isExpress => SqlServer2017,
            11 when isExpress => SqlServer2012,
            10 when isExpress => SqlServer2008,
            _ => null
        };
    }

    public static SqlServerReleaseDefinition Resolve(string? id) =>
        Options.FirstOrDefault(option => string.Equals(option.Id, id, StringComparison.OrdinalIgnoreCase))
        ?? Recommended;
}
'''
Path("SqlServerReleaseCatalog.cs").write_text(sql_catalog, encoding="utf-8")

p = Path("EnvironmentInstaller.cs")
text = p.read_text(encoding="utf-8")
text = replace_exact(text,
'''    internal static string SqlServerInstallContinuationMessage =>
        "已写入 4KB 扇区兼容设置，请重启设备后点击“继续安装”。";
''',
'''    internal static string SqlServerInstallContinuationMessage =>
        "已写入 4KB 扇区兼容设置，请重启设备后点击“继续安装”。";

    internal static string IisInstallRestartMarkerPath => Path.Combine(
        ComponentPaths.RuntimeStateRoot,
        "iis-install-restart.pending");

    internal static bool HasIisInstallContinuation => File.Exists(IisInstallRestartMarkerPath);

    internal static string IisInstallContinuationMessage =>
        "IIS 或 URL Rewrite 安装需要重启 Windows 才能继续。请重启设备后再次点击“继续安装”。";
''', "IIS restart marker properties")

text = replace_exact(text,
'''        await RunElevatedPowerShellAsync(script, progress, 10, 95, cancellationToken, requireExistingAdministrator: true);
        TryDeleteFile(IisPendingUninstallMarker);
''',
'''        await RunElevatedPowerShellAsync(script, progress, 10, 95, cancellationToken, requireExistingAdministrator: true);
        TryDeleteFile(IisInstallRestartMarkerPath);
        TryDeleteFile(IisPendingUninstallMarker);
''', "IIS success marker cleanup")

text = replace_exact(text,
'''        var plan = GetSqlServerInstallPlan(downloads, selectedReleaseId);
        var credentials = WindowsServiceExists("MSSQLSERVER")
''',
'''        var selectedRelease = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
        if (!selectedRelease.IsSupported)
        {
            throw new InvalidOperationException(selectedRelease.SupportNote);
        }

        var plan = GetSqlServerInstallPlan(downloads, selectedRelease.Id);
        var credentials = WindowsServiceExists("MSSQLSERVER")
''', "SQL backend OS compatibility guard")

iis_method = r'''    internal static string BuildIisScript(string rewriteMsi)
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
        var log = Path.Combine(GetTempDirectory(), "install-iis.log");
        var restartMarker = IisInstallRestartMarkerPath;
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference='Stop'");
        sb.AppendLine($"$log='{EscapePowerShellPath(log)}'");
        sb.AppendLine($"$restartMarker='{EscapePowerShellPath(restartMarker)}'");
        sb.AppendLine("$restartNeeded=$false");
        sb.AppendLine("Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("Start-Transcript -Path $log -Append | Out-Null");
        sb.AppendLine("function Test-RestartNeeded($result) { if ($null -eq $result -or $null -eq $result.RestartNeeded) { return $false }; $text=$result.RestartNeeded.ToString(); return $text -eq 'True' -or $text -eq 'Yes' }");
        sb.AppendLine("function Mark-RestartRequired($reason) { $dir=Split-Path $restartMarker -Parent; New-Item -ItemType Directory -Path $dir -Force | Out-Null; Set-Content -LiteralPath $restartMarker -Value $reason -Encoding UTF8; Write-Output $reason }");
        sb.AppendLine("try {");
        foreach (var feature in requiredFeatures)
        {
            sb.AppendLine($"  $featureResult=Enable-WindowsOptionalFeature -Online -FeatureName {feature} -All -NoRestart -ErrorAction Stop");
            sb.AppendLine("  if (Test-RestartNeeded $featureResult) { $restartNeeded=$true }");
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
        sb.AppendLine("  if ($restartNeeded) { Mark-RestartRequired 'Windows 功能安装要求重启后继续 IIS 安装。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine(@"  $appcmd = Join-Path $env:windir 'System32\inetsrv\appcmd.exe'");
        sb.AppendLine(@"  if (!(Test-Path $appcmd)) { throw '未找到 IIS 配置工具 appcmd.exe。' }");
        sb.AppendLine(@"  $docs=@('default.html','default.asp','default.aspx','index.php','index.asp','index.aspx')");
        sb.AppendLine(@"  foreach($doc in $docs) { & $appcmd set config /section:defaultDocument /+files.[value=$doc] 2>$null }");
        sb.AppendLine(@"  & $appcmd set config /section:asp /enableParentPaths:True");
        var escapedRewriteMsi = EscapePowerShellPath(rewriteMsi);
        sb.AppendLine($"  $rewriteMsi = '{escapedRewriteMsi}'");
        sb.AppendLine("  if (!(Test-Path -LiteralPath $rewriteMsi)) { throw '未找到 URL Rewrite 安装包。' }");
        sb.AppendLine("  $rewriteProcess = Start-Process msiexec.exe -ArgumentList ('/i \\"' + $rewriteMsi + '\\" /qn /norestart') -Wait -PassThru -WindowStyle Hidden -ErrorAction Stop");
        sb.AppendLine("  if ($rewriteProcess.ExitCode -eq 3010) { Mark-RestartRequired 'URL Rewrite 安装完成，但 Windows Installer 要求重启后继续。'; throw 'IIS_RESTART_REQUIRED' }");
        sb.AppendLine("  if ($rewriteProcess.ExitCode -ne 0) { throw ('URL Rewrite 安装失败，退出码：' + $rewriteProcess.ExitCode) }");
        sb.AppendLine("  $moduleOutput = (& $appcmd list modules 2>&1 | Out-String)");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0 -or $moduleOutput -notmatch 'RewriteModule') { throw 'URL Rewrite MSI 已完成，但 IIS 未检测到 RewriteModule。' }");
        sb.AppendLine("  $iisreset = Join-Path $env:windir 'System32\\iisreset.exe'");
        sb.AppendLine("  if (!(Test-Path -LiteralPath $iisreset)) { throw '未找到 IIS 重置工具 iisreset.exe。' }");
        sb.AppendLine("  & $iisreset /START");
        sb.AppendLine("  if ($LASTEXITCODE -ne 0) { throw ('IIS 启动失败，退出码：' + $LASTEXITCODE) }");
        sb.AppendLine("  foreach($serviceName in @('WAS','W3SVC')) { $service=Get-Service -Name $serviceName -ErrorAction Stop; if ($service.Status -ne 'Running') { Start-Service -Name $serviceName -ErrorAction Stop }; $service=Get-Service -Name $serviceName -ErrorAction Stop; $service.WaitForStatus('Running',[TimeSpan]::FromSeconds(30)); if ($service.Status -ne 'Running') { throw ($serviceName + ' 未进入 Running 状态。') } }");
        sb.AppendLine("  Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue");
        sb.AppendLine("} finally { try { Stop-Transcript | Out-Null } catch { } }");
        sb.AppendLine("exit 0");
        return sb.ToString();
    }

'''
text = replace_regex(text, r'    internal static string BuildIisScript\(string rewriteMsi\)\n    \{.*?\n    \}\n\n    internal static string BuildMySqlScript', iis_method + '    internal static string BuildMySqlScript', "BuildIisScript replacement")

text = replace_exact(text,
'''        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $installer='{{EscapePowerShellPath(installer)}}'
''',
'''        var features = displayName.Contains("2012", StringComparison.OrdinalIgnoreCase) ? "SQL" : "SQL,Tools";
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $installer='{{EscapePowerShellPath(installer)}}'
''', "SQL 2012 feature variable")
text = replace_exact(text,
'''                $args='/QS /ACTION=Install /FEATURES=SQL,Tools /INSTANCENAME=MSSQLSERVER /SECURITYMODE=SQL /SAPWD="' + $saPassword + '" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSYSADMINACCOUNTS=' + $adminArgument + ' /TCPENABLED=1 /INSTALLSQLDATADIR="' + $dataRoot + '" /IACCEPTSQLSERVERLICENSETERMS'
''',
'''                $args='/QS /ACTION=Install /FEATURES={{features}} /INSTANCENAME=MSSQLSERVER /SECURITYMODE=SQL /SAPWD="' + $saPassword + '" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSYSADMINACCOUNTS=' + $adminArgument + ' /TCPENABLED=1 /INSTALLSQLDATADIR="' + $dataRoot + '" /IACCEPTSQLSERVERLICENSETERMS'
''', "SQL 2012 SQL-only features")
text = replace_exact(text, "                    '/UpdateEnabled=False',\n", "", "Use SQL Setup default updates")

safe_cleanup = r'''            function Remove-BrokenSqlServer {
                Step '清理损坏的 SQL Server 默认实例残留'
                Invoke-SqlSetupUninstallBestEffort
                $instanceId = Get-SqlInstanceId
                foreach ($serviceName in @('MSSQLSERVER','SQLAgent$MSSQLSERVER','SQLTELEMETRY$MSSQLSERVER')) {
                    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($svc) {
                        if ($svc.Status -ne 'Stopped') { Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue; Start-Sleep -Seconds 2 }
                        & sc.exe delete $serviceName | Out-String | Write-Output
                    }
                }
                for ($i = 0; $i -lt 30; $i++) { if (-not (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue)) { break }; Start-Sleep -Milliseconds 500 }
                foreach ($viewRoot in @('HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server','HKLM:\SOFTWARE\WOW6432Node\Microsoft\Microsoft SQL Server')) {
                    $instanceNames = Join-Path $viewRoot 'Instance Names\SQL'
                    if (Test-Path $instanceNames) { Remove-ItemProperty -Path $instanceNames -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue }
                    if (![string]::IsNullOrWhiteSpace($instanceId)) { Remove-Item -LiteralPath (Join-Path $viewRoot $instanceId) -Recurse -Force -ErrorAction SilentlyContinue }
                }
                if (![string]::IsNullOrWhiteSpace($instanceId)) {
                    Remove-PathSafe (Join-Path $env:ProgramFiles ('Microsoft SQL Server\' + $instanceId))
                    Remove-PathSafe (Join-Path $env:ProgramData ('Microsoft\SQL Server\' + $instanceId))
                }
                Remove-PathSafe $dataRoot
            }

'''
text = replace_regex(text, r'            function Remove-BrokenSqlServer \{.*?\n            \}\n\n            function Repair-RsFxRegistry', safe_cleanup + '            function Repair-RsFxRegistry', "Instance-scoped SQL cleanup")

sector = r'''            function Ensure-SqlSectorCompatibility {
                Step '检查 SQL Server 磁盘扇区兼容性'
                $volumeRoot = [IO.Path]::GetPathRoot($dataRoot).TrimEnd('\')
                $sectorText = (& fsutil fsinfo sectorinfo $volumeRoot 2>&1 | Out-String)
                if (![string]::IsNullOrWhiteSpace($sectorText)) { Write-Output $sectorText }
                $physicalAtomicity = $null
                $physicalPerformance = $null
                $effectiveAtomicity = $null
                foreach ($line in ($sectorText -split "`r?`n")) {
                    if ($line -match '^\s*PhysicalBytesPerSectorForAtomicity\s*:\s*(\d+)') { $physicalAtomicity = [int64]$Matches[1] }
                    elseif ($line -match '^\s*PhysicalBytesPerSectorForPerformance\s*:\s*(\d+)') { $physicalPerformance = [int64]$Matches[1] }
                    elseif ($line -match '^\s*FileSystemEffectivePhysicalBytesPerSectorForAtomicity\s*:\s*(\d+)') { $effectiveAtomicity = [int64]$Matches[1] }
                }
                $checkBytes = 0
                foreach ($candidate in @($physicalAtomicity, $physicalPerformance, $effectiveAtomicity)) { if ($candidate -ne $null -and $candidate -gt $checkBytes) { $checkBytes = $candidate } }
                Write-Output ('SQL Server 扇区检查：physicalAtomicity=' + $physicalAtomicity + '; physicalPerformance=' + $physicalPerformance + '; effectiveAtomicity=' + $effectiveAtomicity + '; max=' + $checkBytes)
                if ($checkBytes -le 4096) { Write-Output '当前数据盘物理扇区不大于 4KB，无需修改 NVMe 兼容注册表。'; return }
                $nvmeKey = 'HKLM:\SYSTEM\CurrentControlSet\Services\stornvme\Parameters\Device'
                New-Item -Path $nvmeKey -Force | Out-Null
                $expected = '* 4095'
                $current = @((Get-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes' -ErrorAction SilentlyContinue).ForcedPhysicalSectorSizeInBytes)
                if ($current -notcontains $expected) {
                    New-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes' -PropertyType MultiString -Value $expected -Force | Out-Null
                    Write-Output '检测到大于 4KB 的物理扇区，已按 Microsoft 官方 workaround 写入 ForcedPhysicalSectorSizeInBytes = * 4095。'
                } else { Write-Output '大于 4KB 的物理扇区仍可见，NVMe 兼容项已存在但尚未生效。' }
                Mark-SqlRestartRequired
                Fail ('当前 ' + $volumeRoot + ' 盘 SQL Server 检测到大于 4KB 的物理扇区。请重启设备，使 Microsoft 官方 NVMe 兼容设置生效后再点击“继续安装”。')
            }

'''
text = replace_regex(text, r'            function Ensure-SqlSectorCompatibility \{.*?\n            \}\n\n            function Find-SetupMedia', sector + '            function Find-SetupMedia', "SQL sector ordering")

text = replace_exact(text,
'''                if (HasSqlServerInstallContinuation)
                {
                    throw new InstallRestartRequiredException(SqlServerInstallContinuationMessage);
                }

                var message = $"安装脚本退出码：{process.ExitCode}。日志目录：{GetTempDirectory()}";
''',
'''                if (HasSqlServerInstallContinuation)
                {
                    throw new InstallRestartRequiredException(SqlServerInstallContinuationMessage);
                }

                if (HasIisInstallContinuation)
                {
                    throw new InstallRestartRequiredException(IisInstallContinuationMessage);
                }

                var message = $"安装脚本退出码：{process.ExitCode}。日志目录：{GetTempDirectory()}";
''', "IIS restart classification")
p.write_text(text, encoding="utf-8")

p = Path("EnvironmentInstallWorker.cs")
text = p.read_text(encoding="utf-8")
text = replace_exact(text,
'''            else if (kind == EnvironmentKind.SqlServer)
            {
                item.SelectedSqlServerReleaseId = selectedReleaseId!;
            }
''',
'''            else if (kind == EnvironmentKind.SqlServer)
            {
                var selectedSqlRelease = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
                if (!selectedSqlRelease.IsSupported)
                {
                    throw new InvalidOperationException(selectedSqlRelease.SupportNote);
                }
                item.SelectedSqlServerReleaseId = selectedSqlRelease.Id;
            }
''', "Worker SQL OS guard")
p.write_text(text, encoding="utf-8")

p = Path("ViewModels/EnvironmentViewModels.cs")
text = p.read_text(encoding="utf-8")
text = replace_exact(text,
'''            var normalized = SqlServerReleaseCatalog.Contains(value)
                ? value
                : SqlServerReleaseCatalog.Recommended.Id;
            if (SetProperty(ref _selectedSqlServerReleaseId, normalized))
            {
                OnPropertyChanged(nameof(SelectedSqlServerRelease));
                OnPropertyChanged(nameof(SelectedInstallReleaseId));
                OnPropertyChanged(nameof(OptionLabel));
            }
''',
'''            var candidate = SqlServerReleaseCatalog.Contains(value)
                ? SqlServerReleaseCatalog.Resolve(value)
                : SqlServerReleaseCatalog.Recommended;
            var normalized = candidate.IsSupported
                ? candidate.Id
                : SqlServerReleaseCatalog.Recommended.Id;
            if (SetProperty(ref _selectedSqlServerReleaseId, normalized))
            {
                OnPropertyChanged(nameof(SelectedSqlServerRelease));
                OnPropertyChanged(nameof(SelectedInstallReleaseId));
                OnPropertyChanged(nameof(OptionLabel));
                OnPropertyChanged(nameof(CanInstall));
            }
''', "ViewModel supported SQL selection")
text = replace_exact(text, '    public bool CanInstall => !IsBusy;\n', '    public bool CanInstall => !IsBusy && (!IsSqlServerModule || SelectedSqlServerRelease.IsSupported);\n', "Disable unsupported SQL install")
p.write_text(text, encoding="utf-8")

p = Path("Resources/MainWindowTemplates.xaml")
text = p.read_text(encoding="utf-8")
old = '''                            <ComboBox Style="{DynamicResource EnvironmentVersionComboBox}"
                                      ItemContainerStyle="{DynamicResource EnvironmentVersionComboBoxItem}"
                                      ItemsSource="{Binding SqlServerReleaseOptions}"
                                      SelectedValuePath="Id"
                                      SelectedValue="{Binding SelectedSqlServerReleaseId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                      IsEnabled="{Binding CanSelectSqlServerVersion}"
                                      Visibility="{Binding SqlServerVersionSelectorVisibility}"
                                      Margin="0,0,8,8">
                                <ComboBox.ItemTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding DisplayName}"
                                                   Foreground="{DynamicResource TextBrush}"
                                                   TextTrimming="CharacterEllipsis" />
                                    </DataTemplate>
                                </ComboBox.ItemTemplate>
                            </ComboBox>
'''
new = '''                            <ComboBox Style="{DynamicResource EnvironmentVersionComboBox}"
                                      ItemsSource="{Binding SqlServerReleaseOptions}"
                                      SelectedValuePath="Id"
                                      SelectedValue="{Binding SelectedSqlServerReleaseId, Mode=TwoWay, UpdateSourceTrigger=PropertyChanged}"
                                      IsEnabled="{Binding CanSelectSqlServerVersion}"
                                      Visibility="{Binding SqlServerVersionSelectorVisibility}"
                                      Margin="0,0,8,8">
                                <ComboBox.ItemContainerStyle>
                                    <Style TargetType="ComboBoxItem" BasedOn="{DynamicResource EnvironmentVersionComboBoxItem}">
                                        <Style.Triggers>
                                            <DataTrigger Binding="{Binding IsSupported}" Value="False">
                                                <Setter Property="IsEnabled" Value="False" />
                                                <Setter Property="ToolTip" Value="{Binding SupportNote}" />
                                                <Setter Property="Opacity" Value="0.55" />
                                            </DataTrigger>
                                        </Style.Triggers>
                                    </Style>
                                </ComboBox.ItemContainerStyle>
                                <ComboBox.ItemTemplate>
                                    <DataTemplate>
                                        <TextBlock Text="{Binding DisplayNameWithSupport}"
                                                   Foreground="{DynamicResource TextBrush}"
                                                   ToolTip="{Binding SupportNote}"
                                                   TextTrimming="CharacterEllipsis" />
                                    </DataTemplate>
                                </ComboBox.ItemTemplate>
                            </ComboBox>
'''
text = replace_exact(text, old, new, "SQL disabled option UI")
p.write_text(text, encoding="utf-8")

tests = r'''using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void SqlServer138_CompatibilityMatrix_FollowsMicrosoftWindowsMatrix()
    {
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2025Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2022Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2017Id, WindowsSqlCompatibilityFamily.WindowsServer2025).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2017Id, WindowsSqlCompatibilityFamily.WindowsServer2022).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows11).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2012Id, WindowsSqlCompatibilityFamily.Windows10).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2025Id, WindowsSqlCompatibilityFamily.WindowsServer2016).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2022Id, WindowsSqlCompatibilityFamily.WindowsServer2016).IsSupported);
        Assert.IsTrue(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2008Id, WindowsSqlCompatibilityFamily.WindowsServer2012R2).IsSupported);
        Assert.IsFalse(SqlServerOsCompatibility.GetSupport(SqlServerReleaseCatalog.SqlServer2008Id, WindowsSqlCompatibilityFamily.Windows10).IsSupported);
    }

    [TestMethod]
    public void Iis138_UsesWindowsFeaturesWithoutAspnetRegiis_AndValidatesRewriteAndServices()
    {
        var script = EnvironmentInstaller.BuildIisScript(@"C:\Temp\URLRewrite.msi");
        StringAssert.Contains(script, "IIS-ASPNET45");
        StringAssert.Contains(script, "NetFx3");
        StringAssert.Contains(script, "RestartNeeded");
        StringAssert.Contains(script, "RewriteModule");
        StringAssert.Contains(script, "W3SVC");
        StringAssert.Contains(script, "WAS");
        Assert.IsFalse(script.Contains("aspnet_regiis", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SqlServer138_2012Express_RequestsDatabaseEngineOnly()
    {
        var script = EnvironmentInstaller.BuildSqlServer2008Script(@"C:\Temp\SQLEXPR_x64_ENU.exe", @"C:\SqlData", "Example!Pass123", "SQL Server 2012 Express SP4");
        StringAssert.Contains(script, "/FEATURES=SQL ");
        Assert.IsFalse(script.Contains("/FEATURES=SQL,Tools", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void SqlServer138_ModernSetup_UsesOfficialUpdateDefault_AndKeepsStorePendingRenameBehavior()
    {
        var downloads = new EnvironmentDownloadSettings(
            "http://example/rewrite.msi", "http://example/nginx.zip", "http://example/mysql.zip",
            "https://example/sql2025.exe", "https://example/sql2025dev.exe", "https://example/sql2022.exe",
            "https://example/sql2017.exe", "https://example/sql2012x64.exe", "https://example/sql2012x86.exe",
            "http://example/sql2008x64.exe", "http://example/sql2008x86.exe",
            "http://example/tomcat.zip", "https://example/frp.zip");
        var plan = EnvironmentInstaller.GetSqlServerInstallPlan(downloads, SqlServerReleaseCatalog.SqlServer2022Id);
        var script = EnvironmentInstaller.BuildModernSqlServerScript(@"C:\Temp\SQL2022-SSEI-Expr.exe", @"C:\SqlData", plan, "Example!Pass123");
        Assert.IsFalse(script.Contains("/UpdateEnabled=False", StringComparison.OrdinalIgnoreCase));
        StringAssert.Contains(script, "Remove-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations");
        Assert.IsFalse(script.Contains("Remove-PathSafe (Join-Path $env:ProgramFiles 'Microsoft SQL Server')", StringComparison.Ordinal));
        var sectorProbe = script.IndexOf("fsutil fsinfo sectorinfo", StringComparison.Ordinal);
        var sectorOverride = script.IndexOf("New-ItemProperty -Path $nvmeKey -Name 'ForcedPhysicalSectorSizeInBytes'", StringComparison.Ordinal);
        Assert.IsTrue(sectorProbe >= 0 && sectorOverride > sectorProbe, "Sector size must be measured before writing the NVMe compatibility override.");
    }
}
'''
Path("MCPanel.Tests/ReliabilityTests.SqlIis138.cs").write_text(tests, encoding="utf-8")

p = Path("MCPanel.csproj")
text = p.read_text(encoding="utf-8")
text = replace_exact(text, "<Version>1.3.7</Version>", "<Version>1.3.8</Version>", "Version")
text = replace_exact(text, "<FileVersion>1.3.7.0</FileVersion>", "<FileVersion>1.3.8.0</FileVersion>", "FileVersion")
text = replace_exact(text, "<AssemblyVersion>1.3.7.0</AssemblyVersion>", "<AssemblyVersion>1.3.8.0</AssemblyVersion>", "AssemblyVersion")
p.write_text(text, encoding="utf-8")
