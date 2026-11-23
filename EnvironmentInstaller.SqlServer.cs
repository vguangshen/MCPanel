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
    internal sealed record SqlServerInstallPlan(
        string DisplayName,
        string InstallerFileName,
        string? DownloadUrl,
        int? ProductMajor,
        string? MediaDirectoryName,
        string? ExtractDirectoryName,
        bool UsesSseiInstaller,
        string MediaPackageSearchPattern = "SQLEXPR*.exe",
        string MediaType = "Core");

    private async Task InstallSqlServerAsync(
        EnvironmentDownloadSettings downloads,
        string selectedReleaseId,
        Action<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var selectedRelease = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
        if (!selectedRelease.IsSupported)
        {
            throw new InvalidOperationException(selectedRelease.SupportNote);
        }

        var plan = GetSqlServerInstallPlan(downloads, selectedRelease.Id);
        var credentials = WindowsServiceExists("MSSQLSERVER")
            ? SqlServerCredentialStore.Load()
            : SqlServerCredentialStore.CreateNew();
        progress(new InstallProgress(0, $"准备安装 {plan.DisplayName}...", InstallProgressStage.Preparing, 0));
        var installer = await DownloadAbsoluteFileAsync(
            plan.DownloadUrl!,
            GetPackageDirectory(),
            plan.InstallerFileName,
            progress,
            0,
            plan.UsesSseiInstaller ? 45 : 80,
            cancellationToken,
            expectedProductMajor: plan.ProductMajor);
        var dataRoot = ComponentPaths.SelectSqlServerDataRoot(WindowsServiceExists("MSSQLSERVER"));
        Directory.CreateDirectory(dataRoot);

        var script = Path.Combine(GetTempDirectory(), "install-sqlserver.ps1");
        await FileCompat.WriteAllTextAsync(
            script,
            plan.UsesSseiInstaller
                ? BuildModernSqlServerScript(installer, dataRoot, plan, credentials.Password)
                : BuildSqlServer2008Script(installer, dataRoot, credentials.Password, plan.DisplayName),
            new UTF8Encoding(true),
            cancellationToken);

        progress(InstallingProgress(84, $"正在通过官方安装包安装 {plan.DisplayName}，可能需要较长时间...", 0));
        await RunElevatedPowerShellAsync(script, progress, 5, 95, cancellationToken, requireExistingAdministrator: true);
        SqlServerCredentialStore.Save(credentials);
        progress(InstallingProgress(100, "SQL Server 安装流程完成。连接信息：127.0.0.1,1433，账号 sa。"));
    }

    internal static SqlServerInstallPlan GetSqlServerInstallPlan(
        EnvironmentDownloadSettings downloads,
        string? selectedReleaseId = null)
    {
        var selected = SqlServerReleaseCatalog.Resolve(selectedReleaseId);
        return selected.Id switch
        {
            SqlServerReleaseCatalog.SqlServer2025Id => new SqlServerInstallPlan("SQL Server 2025 Express", "SQL2025-SSEI-Expr.exe", downloads.SqlServer2025ExpressUrl, 17, "SqlServer2025ExpressMedia", "SQLEXPR_2025", true),
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId => new SqlServerInstallPlan(
                "SQL Server 2025 Enterprise Developer",
                "SQL2025-SSEI-EntDev.exe",
                downloads.SqlServer2025EnterpriseDeveloperUrl,
                17,
                "SqlServer2025EnterpriseDeveloperMedia",
                "SQLSERVER_2025_ENTERPRISE_DEVELOPER",
                true,
                "*",
                "ISO"),
            SqlServerReleaseCatalog.SqlServer2022Id => new SqlServerInstallPlan("SQL Server 2022 Express", "SQL2022-SSEI-Expr.exe", downloads.SqlServer2022ExpressUrl, 16, "SqlServer2022ExpressMedia", "SQLEXPR_2022", true),
            SqlServerReleaseCatalog.SqlServer2017Id => new SqlServerInstallPlan("SQL Server 2017 Express", "SQLServer2017-SSEI-Expr.exe", downloads.SqlServer2017ExpressUrl, 14, "SqlServer2017ExpressMedia", "SQLEXPR_2017", true),
            SqlServerReleaseCatalog.SqlServer2012Id => new SqlServerInstallPlan(
                "SQL Server 2012 Express SP4",
                Environment.Is64BitOperatingSystem ? "SQLEXPR_x64_ENU.exe" : "SQLEXPR_x86_ENU.exe",
                Environment.Is64BitOperatingSystem ? downloads.SqlServer2012ExpressX64Url : downloads.SqlServer2012ExpressX86Url,
                11,
                null,
                null,
                false),
            _ => new SqlServerInstallPlan(
                "SQL Server 2008 Express",
                Environment.Is64BitOperatingSystem ? "SQLEXPR_2008_x64.exe" : "SQLEXPR_2008_x86.exe",
                Environment.Is64BitOperatingSystem ? downloads.SqlServer2008ExpressX64Url : downloads.SqlServer2008ExpressX86Url,
                null,
                null,
                null,
                false)
        };
    }

    internal static string BuildSqlServer2008Script(
        string installer,
        string dataRoot,
        string saPassword,
        string displayName = "SQL Server 2008 Express")
    {
        var logFileName = displayName.Contains("2012", StringComparison.OrdinalIgnoreCase)
            ? "install-sqlserver-2012.log"
            : "install-sqlserver-2008.log";
        var features = displayName.Contains("2012", StringComparison.OrdinalIgnoreCase) ? "SQL" : "SQL,Tools";
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            {{WindowsFirewallScript.Functions}}
            $installer='{{PathUtil.EscapePowerShellLiteral(installer)}}'
            $dataRoot='{{PathUtil.EscapePowerShellLiteral(dataRoot)}}'
            $saPassword='{{PathUtil.EscapePowerShellLiteral(saPassword)}}'
            $log=Join-Path (Split-Path $installer) '{{logFileName}}'
            Start-Transcript -Path $log -Append | Out-Null

            function Step($name) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
            }

            function Fail($message) {
                Write-Output ('安装失败：' + $message)
                Stop-Transcript | Out-Null
                exit 1
            }

            function Ensure-Dir($path) {
                if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
                icacls.exe $path /inheritance:e | Out-Null
                icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-18:(OI)(CI)F' /T /C | Out-Null
                icacls.exe $path /grant '*S-1-5-20:(OI)(CI)F' /T /C | Out-Null
            }

            function Get-CurrentWindowsUser {
                try {
                    $identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
                    if ($identity -and ![string]::IsNullOrWhiteSpace($identity.Name) -and
                        $identity.Name -notlike 'NT AUTHORITY\*') {
                        return $identity.Name
                    }
                } catch { }

                try {
                    $name = (& whoami.exe 2>$null | Select-Object -First 1).Trim()
                    if ($name -and $name -notlike 'NT AUTHORITY\*') { return $name }
                } catch { }

                return $null
            }

            function Get-SqlAdminAccounts {
                $accounts = @()
                try {
                    $group = ([System.Security.Principal.SecurityIdentifier]'S-1-5-32-544').Translate([System.Security.Principal.NTAccount]).Value
                    if ($group) { $accounts += $group }
                } catch {
                    $accounts += 'BUILTIN\Administrators'
                }

                $currentUser = Get-CurrentWindowsUser
                if ($currentUser) { $accounts += $currentUser }
                return @($accounts | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Unique)
            }

            function Format-SqlSysadminAccountsArgument($accounts) {
                return (($accounts | ForEach-Object { '"' + $_.Replace('"', '\"') + '"' }) -join ' ')
            }

            function Ensure-CurrentWindowsSqlLogin {
                $currentUser = Get-CurrentWindowsUser
                if ([string]::IsNullOrWhiteSpace($currentUser)) {
                    Write-Output '无法识别当前 Windows 用户，跳过 SQL Server Windows 登录修复。'
                    return $false
                }

                $identifier = $currentUser.Replace(']', ']]')
                $literal = $currentUser.Replace("'", "''")
                $query = @"
            IF NOT EXISTS (SELECT 1 FROM sys.server_principals WHERE name = N'$literal')
            BEGIN
                CREATE LOGIN [$identifier] FROM WINDOWS;
            END;
            IF NOT EXISTS (
                SELECT 1
                FROM sys.server_role_members AS rm
                INNER JOIN sys.server_principals AS rolePrincipal ON rolePrincipal.principal_id = rm.role_principal_id
                INNER JOIN sys.server_principals AS memberPrincipal ON memberPrincipal.principal_id = rm.member_principal_id
                WHERE rolePrincipal.name = N'sysadmin' AND memberPrincipal.name = N'$literal'
            )
            BEGIN
                ALTER SERVER ROLE [sysadmin] ADD MEMBER [$identifier];
            END;
            "@

                $connection = $null
                $command = $null
                try {
                    $connection = New-Object System.Data.SqlClient.SqlConnection
                    $connection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;User ID=sa;Password=' + $saPassword + ';Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $connection.Open()
                    $command = $connection.CreateCommand()
                    $command.CommandText = $query
                    $command.CommandTimeout = 30
                    [void]$command.ExecuteNonQuery()
                } catch {
                    Write-Output ('使用 sa 修复当前 Windows 登录失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($command) { $command.Dispose() }
                    if ($connection) { $connection.Dispose() }
                }

                $verifyConnection = $null
                $verifyCommand = $null
                try {
                    $verifyConnection = New-Object System.Data.SqlClient.SqlConnection
                    $verifyConnection.ConnectionString = 'Data Source=localhost,1433;Initial Catalog=master;Integrated Security=True;Encrypt=False;TrustServerCertificate=True;Connect Timeout=15'
                    $verifyConnection.Open()
                    $verifyCommand = $verifyConnection.CreateCommand()
                    $verifyCommand.CommandText = 'SELECT 1'
                    [void]$verifyCommand.ExecuteScalar()
                    Write-Output ('已确认当前 Windows 用户可以登录 SQL Server：' + $currentUser)
                    return $true
                } catch {
                    Write-Output ('当前 Windows 登录验证失败：' + $_.Exception.Message)
                    return $false
                } finally {
                    if ($verifyCommand) { $verifyCommand.Dispose() }
                    if ($verifyConnection) { $verifyConnection.Dispose() }
                }
            }

            function Wait-ServiceState($serviceName, $target, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    $svc = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status.ToString() -eq $target) { return $true }
                    Start-Sleep -Seconds 1
                }
                return $false
            }

            function Clear-PendingFileRenameOperations {
                $sessionManagerPath = 'HKLM:\SYSTEM\CurrentControlSet\Control\Session Manager'
                try {
                    $session = Get-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -ErrorAction SilentlyContinue
                    if ($null -ne $session -and $null -ne $session.PendingFileRenameOperations) {
                        Remove-ItemProperty -Path $sessionManagerPath -Name PendingFileRenameOperations -Force -ErrorAction Stop
                        Write-Output '已直接清理 Windows PendingFileRenameOperations。'
                    } else {
                        Write-Output '未发现 Windows PendingFileRenameOperations。'
                    }
                } catch {
                    Fail ('清理 Windows PendingFileRenameOperations 失败：' + $_.Exception.Message)
                }
            }

            function Configure-SqlServer {
                Step '配置 SQL Server 默认连接'
                $instanceId = $null
                $instanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
                if (Test-Path $instanceKey) {
                    $instanceId = (Get-ItemProperty $instanceKey -ErrorAction SilentlyContinue).MSSQLSERVER
                }
                if ([string]::IsNullOrWhiteSpace($instanceId)) { $instanceId = 'MSSQL10.SQLEXPRESS' }

                $loginKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $instanceId + '\MSSQLServer'
                if (Test-Path $loginKey) { Set-ItemProperty -Path $loginKey -Name LoginMode -Value 2 -ErrorAction SilentlyContinue }

                $tcpKey = $loginKey + '\SuperSocketNetLib\Tcp\IPAll'
                if (Test-Path $tcpKey) {
                    Set-ItemProperty -Path $tcpKey -Name TcpDynamicPorts -Value '' -ErrorAction SilentlyContinue
                    Set-ItemProperty -Path $tcpKey -Name TcpPort -Value '1433' -ErrorAction SilentlyContinue
                }
                Set-McPanelFirewallPort 'MCPanel SQL Server 1433' 'TCP' 1433
            }

            try {
                Step '清理 Windows 待处理文件重命名项'
                Clear-PendingFileRenameOperations

                if (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue) {
                    Configure-SqlServer
                    Start-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue
                    if (Wait-ServiceState MSSQLSERVER Running 60) {
                        if (!(Ensure-CurrentWindowsSqlLogin)) {
                            Write-Output '现有 SQL Server 已启动，但未能自动修复当前 Windows 登录。'
                        }
                    }
                    Stop-Transcript | Out-Null
                    exit 0
                }

                Step '准备 SQL Server 数据目录'
                Ensure-Dir $dataRoot

                Step '运行官方 {{displayName}} 安装包'
                $adminAccounts = Get-SqlAdminAccounts
                $adminArgument = Format-SqlSysadminAccountsArgument $adminAccounts
                Write-Output ('SQL Server 管理员账户：' + ($adminAccounts -join ', '))
                $args='/QS /ACTION=Install /FEATURES={{features}} /INSTANCENAME=MSSQLSERVER /SECURITYMODE=SQL /SAPWD="' + $saPassword + '" /SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE" /SQLSYSADMINACCOUNTS=' + $adminArgument + ' /TCPENABLED=1 /INSTALLSQLDATADIR="' + $dataRoot + '" /IACCEPTSQLSERVERLICENSETERMS'
                $install = Start-Process -FilePath $installer -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
                if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) { Fail ('官方安装包退出码：' + $install.ExitCode) }

                Configure-SqlServer
                Restart-Service -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState MSSQLSERVER Running 90)) { Fail 'MSSQLSERVER 服务启动超时。' }
                if (!(Ensure-CurrentWindowsSqlLogin)) { Fail 'SQL Server 已安装，但当前 Windows 用户登录验证失败。' }
                Write-Output '{{displayName}} 安装完成。'
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Fail $_.Exception.Message
            }
            """;
    }

    internal static string BuildModernSqlServerScript(string bootstrapper, string dataRoot, SqlServerInstallPlan plan, string saPassword)
    {
        var mediaRoot = Path.Combine(Path.GetDirectoryName(bootstrapper)!, plan.MediaDirectoryName!);
        var extractRoot = Path.Combine(mediaRoot, plan.ExtractDirectoryName!);
        var setupExe = Path.Combine(extractRoot, "SETUP.EXE");
        var logFileName = $"install-sqlserver-{plan.ProductMajor}.log";
        var bootstrapLogDirectory = $@"Microsoft SQL Server\{plan.ProductMajor}0\Setup Bootstrap\Log";
        var defaultInstanceId = $"MSSQL{plan.ProductMajor}.MSSQLSERVER";
        var displayName = plan.DisplayName;
        var restartMarker = SqlServerInstallRestartMarkerPath;
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            {{WindowsFirewallScript.Functions}}
            $bootstrapper='{{PathUtil.EscapePowerShellLiteral(bootstrapper)}}'
            $mediaRoot='{{PathUtil.EscapePowerShellLiteral(mediaRoot)}}'
            $extractRoot='{{PathUtil.EscapePowerShellLiteral(extractRoot)}}'
            $setupExe='{{PathUtil.EscapePowerShellLiteral(setupExe)}}'
            $dataRoot='{{PathUtil.EscapePowerShellLiteral(dataRoot)}}'
            $saPassword='{{PathUtil.EscapePowerShellLiteral(saPassword)}}'
            $defaultInstanceId='{{defaultInstanceId}}'
            $displayName='{{displayName}}'
            $mediaPackageSearchPattern='{{plan.MediaPackageSearchPattern}}'
            $mediaType='{{plan.MediaType}}'
            $restartMarker='{{PathUtil.EscapePowerShellLiteral(restartMarker)}}'
            $bootstrapLogDirectory='{{PathUtil.EscapePowerShellLiteral(bootstrapLogDirectory)}}'
            $script:mountedIsoPath=$null
            $log=Join-Path (Split-Path $bootstrapper) '{{logFileName}}'
            """ + "\r\n" + ModernSqlServerScriptBody.Value;
    }

    /// <summary>The script logic lives in Installer\SqlServerModernInstall.ps1 (embedded).</summary>
    private static readonly Lazy<string> ModernSqlServerScriptBody = new(() =>
    {
        using var stream = typeof(EnvironmentInstaller).Assembly.GetManifestResourceStream("MCPanel.SqlServerModernInstall.ps1")
            ?? throw new InvalidOperationException("SQL Server 安装脚本资源缺失。");
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n");
    });

    private static string BuildSqlServerFailureHint(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        if (text.Contains("0x84be0bc2", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Windows 当前存在待重启项", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("requires a restart", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("restart the computer", StringComparison.OrdinalIgnoreCase))
        {
            return "Windows 当前存在待重启项，SQL Server 官方安装器要求先重启系统。请重启 Windows 后再点击安装。";
        }

        if (text.Contains("EXCEPTION_STACK_OVERFLOW", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("0x851A001A", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Wait on the Database Engine recovery handle", StringComparison.OrdinalIgnoreCase))
        {
            if (HasSqlServerInstallContinuation)
            {
                return SqlServerInstallContinuationMessage;
            }

            return "SQL Server 官方安装器在启动数据库引擎时失败。当前版本已在安装前检查 Windows NVMe 4KB 扇区兼容项；如果刚写入兼容项，请重启 Windows 后再安装。详细日志请查看 StoreData\\Work\\install-sqlserver-*.log 和 C:\\Program Files\\Microsoft SQL Server\\*\\Setup Bootstrap\\Log。";
        }

        if (text.Contains("有效物理扇区仍大于 4KB", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("大于 4KB 的物理扇区", StringComparison.OrdinalIgnoreCase))
        {
            return SqlServerInstallContinuationMessage;
        }

        return string.Empty;
    }
}
