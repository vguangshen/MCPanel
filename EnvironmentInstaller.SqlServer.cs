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
            cancellationToken);
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
            $installer='{{EscapePowerShellPath(installer)}}'
            $dataRoot='{{EscapePowerShellPath(dataRoot)}}'
            $saPassword='{{EscapePowerShellPath(saPassword)}}'
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
                New-NetFirewallRule -DisplayName 'MCPanel SQL Server 1433' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1433 -ErrorAction SilentlyContinue | Out-Null
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
            $bootstrapper='{{EscapePowerShellPath(bootstrapper)}}'
            $mediaRoot='{{EscapePowerShellPath(mediaRoot)}}'
            $extractRoot='{{EscapePowerShellPath(extractRoot)}}'
            $setupExe='{{EscapePowerShellPath(setupExe)}}'
            $dataRoot='{{EscapePowerShellPath(dataRoot)}}'
            $saPassword='{{EscapePowerShellPath(saPassword)}}'
            $defaultInstanceId='{{defaultInstanceId}}'
            $displayName='{{displayName}}'
            $mediaPackageSearchPattern='{{plan.MediaPackageSearchPattern}}'
            $mediaType='{{plan.MediaType}}'
            $restartMarker='{{EscapePowerShellPath(restartMarker)}}'
            $script:mountedIsoPath=$null
            $log=Join-Path (Split-Path $bootstrapper) '{{logFileName}}'
            Remove-Item -LiteralPath $log -Force -ErrorAction SilentlyContinue
            Start-Transcript -Path $log -Force | Out-Null

            function Step($name) {
                Write-Output ''
                Write-Output ('==== ' + $name + ' ====')
            }

            function Dismount-InstallMedia {
                if (![string]::IsNullOrWhiteSpace($script:mountedIsoPath)) {
                    Dismount-DiskImage -ImagePath $script:mountedIsoPath -ErrorAction SilentlyContinue | Out-Null
                    $script:mountedIsoPath=$null
                }
            }

            function Fail($message) {
                Write-Output ('安装失败：' + $message)
                Dismount-InstallMedia
                Write-SetupSummary
                Stop-Transcript | Out-Null
                exit 1
            }

            function Read-TextLocal($path) {
                $bytes = [IO.File]::ReadAllBytes($path)
                if ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF) {
                    return [Text.Encoding]::UTF8.GetString($bytes)
                }
                if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
                    return [Text.Encoding]::Unicode.GetString($bytes)
                }
                if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
                    return [Text.Encoding]::BigEndianUnicode.GetString($bytes)
                }
                return [Text.Encoding]::Default.GetString($bytes)
            }

            function Write-SetupSummary {
                $bootstrapLogRoot = Join-Path $env:ProgramFiles '{{EscapePowerShellPath(bootstrapLogDirectory)}}'
                $summary = Get-ChildItem $bootstrapLogRoot -Filter Summary.txt -Recurse -ErrorAction SilentlyContinue | Sort-Object LastWriteTime -Descending | Select-Object -First 1
                if ($summary) {
                    Write-Output ''
                    Write-Output '最近 SQL Server 安装摘要：'
                    $text = Read-TextLocal $summary.FullName
                    $text -split "`r?`n" | Where-Object { ![string]::IsNullOrWhiteSpace($_) } | Select-Object -Last 120 | ForEach-Object { Write-Output $_ }
                }
            }

            function Ensure-Dir($path) {
                if (!(Test-Path $path)) { New-Item -ItemType Directory -Path $path -Force | Out-Null }
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

            function Mark-SqlRestartRequired {
                $markerDirectory = Split-Path $restartMarker -Parent
                New-Item -ItemType Directory -Path $markerDirectory -Force | Out-Null
                Set-Content -LiteralPath $restartMarker -Value 'SQL Server installation paused for sector compatibility. Restart Windows, then continue installation.' -Encoding UTF8
            }

            function Clear-SqlRestartMarker {
                Remove-Item -LiteralPath $restartMarker -Force -ErrorAction SilentlyContinue
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

            function Get-SqlInstanceId {
                $instanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL'
                if (Test-Path $instanceKey) {
                    $id = (Get-ItemProperty $instanceKey -ErrorAction SilentlyContinue).MSSQLSERVER
                    if (![string]::IsNullOrWhiteSpace($id)) { return $id }
                }
                return $defaultInstanceId
            }

            function Configure-SqlServer {
                Step '配置 SQL Server 默认连接'
                $instanceId = Get-SqlInstanceId
                $loginKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $instanceId + '\MSSQLServer'
                if (Test-Path $loginKey) {
                    Set-ItemProperty -Path $loginKey -Name LoginMode -Value 2 -ErrorAction SilentlyContinue
                }

                $tcpRoot = $loginKey + '\SuperSocketNetLib\Tcp'
                if (Test-Path $tcpRoot) {
                    Get-ChildItem $tcpRoot -ErrorAction SilentlyContinue | ForEach-Object {
                        if ($_.PSChildName -like 'IP*') {
                            Set-ItemProperty -Path $_.PSPath -Name Enabled -Value 1 -ErrorAction SilentlyContinue
                        }
                    }
                }

                $tcpKey = $tcpRoot + '\IPAll'
                if (Test-Path $tcpKey) {
                    Set-ItemProperty -Path $tcpKey -Name TcpDynamicPorts -Value '' -ErrorAction SilentlyContinue
                    Set-ItemProperty -Path $tcpKey -Name TcpPort -Value '1433' -ErrorAction SilentlyContinue
                }

                & sc.exe config MSSQLSERVER start= auto | Out-Null
                & sc.exe config SQLBrowser start= demand | Out-Null
                New-NetFirewallRule -DisplayName 'MCPanel SQL Server 1433' -Direction Inbound -Action Allow -Protocol TCP -LocalPort 1433 -ErrorAction SilentlyContinue | Out-Null
                New-NetFirewallRule -DisplayName 'MCPanel SQL Browser 1434' -Direction Inbound -Action Allow -Protocol UDP -LocalPort 1434 -ErrorAction SilentlyContinue | Out-Null
            }

            function Remove-PathSafe($path) {
                if ([string]::IsNullOrWhiteSpace($path) -or !(Test-Path $path)) { return }
                Write-Output ('清理残留目录：' + $path)
                try {
                    Get-ChildItem $path -Recurse -Force -ErrorAction SilentlyContinue | ForEach-Object {
                        try { $_.Attributes = 'Normal' } catch { }
                    }
                    takeown.exe /F $path /R /D Y | Out-Null
                    icacls.exe $path /grant '*S-1-5-32-544:(OI)(CI)F' /T /C | Out-Null
                    Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction SilentlyContinue
                } catch {
                    Write-Output ('目录清理警告：' + $_.Exception.Message)
                }
            }

            function Invoke-SqlSetupUninstallBestEffort {
                Step '调用官方 SQL Server 安装器清理残留'
                $setupCandidates = @()
                foreach ($root in @(
                    $extractRoot,
                    (Join-Path $mediaRoot 'SQLSERVER_2025_ENTERPRISE_DEVELOPER'),
                    (Join-Path $mediaRoot 'SQLSERVER_2025'),
                    (Join-Path $mediaRoot 'SQLEXPR_2025'),
                    (Join-Path $mediaRoot 'SQLEXPR_2022'),
                    (Join-Path $mediaRoot 'SQLEXPR_2017'),
                    (Join-Path $env:ProgramFiles 'Microsoft SQL Server'),
                    (Join-Path ${env:ProgramFiles(x86)} 'Microsoft SQL Server')
                )) {
                    if (Test-Path $root) {
                        $setupCandidates += Get-ChildItem $root -Filter setup.exe -Recurse -ErrorAction SilentlyContinue
                    }
                }

                $setupCandidates |
                    Sort-Object FullName -Unique |
                    ForEach-Object {
                        $setupPath = $_.FullName
                        Write-Output ('尝试官方卸载：' + $setupPath)
                        $args = '/Q /ACTION=Uninstall /FEATURES=SQLENGINE /INSTANCENAME=MSSQLSERVER'
                        $process = Start-Process -FilePath $setupPath -ArgumentList $args -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                        if ($process) { Write-Output ('官方卸载退出码：' + $process.ExitCode) }
                    }
            }

            function Remove-SqlMsiComponentsBestEffort {
                Step '清理 SQL Server MSI 组件残留'
                $roots = @(
                    'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall',
                    'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall'
                )
                $items = foreach ($root in $roots) {
                    if (Test-Path $root) {
                        Get-ChildItem $root -ErrorAction SilentlyContinue | ForEach-Object {
                            $props = Get-ItemProperty $_.PSPath -ErrorAction SilentlyContinue
                            if ($props.DisplayName -and $props.DisplayName -match 'SQL Server 2025|SQL Server 2022|SQL Server 2019|SQL Server 2017|SQL Server Browser|SQL Server VSS Writer|Microsoft ODBC Driver.*SQL|Microsoft OLE DB Driver.*SQL') {
                                [pscustomobject]@{ Name = $props.DisplayName; KeyName = $_.PSChildName }
                            }
                        }
                    }
                }

                $items | Sort-Object Name -Unique | ForEach-Object {
                    Write-Output ('卸载 MSI 组件：' + $_.Name)
                    if ($_.KeyName -match '^\{[0-9A-Fa-f-]+\}$') {
                        $process = Start-Process -FilePath msiexec.exe -ArgumentList ('/x ' + $_.KeyName + ' /qn /norestart') -Wait -PassThru -WindowStyle Hidden -ErrorAction SilentlyContinue
                        if ($process) { Write-Output ('MSI 卸载退出码：' + $process.ExitCode) }
                    }
                }
            }

            function Remove-BrokenSqlServer {
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

            function Repair-RsFxRegistry {
                $rsfx = 'HKLM:\SYSTEM\CurrentControlSet\Services\RsFx0700'
                if (!(Test-Path $rsfx)) { return }

                Step '修复 SQL Server RsFx 驱动注册表'
                $driver = Join-Path $env:windir 'System32\drivers\RsFx0700.sys'
                if (!(Test-Path $driver)) {
                    Write-Output 'RsFx0700.sys 不存在，删除损坏服务项，交给官方安装器重新创建。'
                    $svc = Get-Service -Name RsFx0700 -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status -ne 'Stopped') {
                        Stop-Service -Name RsFx0700 -Force -ErrorAction SilentlyContinue
                    }
                    & sc.exe delete RsFx0700 | Out-String | Write-Output
                    Remove-Item -LiteralPath $rsfx -Recurse -Force -ErrorAction SilentlyContinue
                    return
                }

                $instances = Join-Path $rsfx 'Instances'
                $instance = Join-Path $instances 'RsFx0700 MiniFilter Instance'
                $shares = Join-Path $rsfx 'InstancesShares'
                if (!(Test-Path $instances)) { New-Item -Path $instances -Force | Out-Null }
                if (!(Test-Path $instance)) { New-Item -Path $instance -Force | Out-Null }
                if (!(Test-Path $shares)) { New-Item -Path $shares -Force | Out-Null }
                New-ItemProperty -Path $instances -Name DefaultInstance -PropertyType String -Value 'RsFx0700 MiniFilter Instance' -Force | Out-Null
                New-ItemProperty -Path $instance -Name Altitude -PropertyType String -Value '41007.00' -Force | Out-Null
                New-ItemProperty -Path $instance -Name Flags -PropertyType DWord -Value 0 -Force | Out-Null
                New-ItemProperty -Path $rsfx -Name Start -PropertyType DWord -Value 4 -Force | Out-Null
            }

            function Ensure-SqlSectorCompatibility {
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

            function Find-SetupMedia {
                return Get-ChildItem $mediaRoot -Filter 'setup.exe' -File -Recurse -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
            }

            function Find-CompressedMedia {
                return Get-ChildItem $mediaRoot -Filter $mediaPackageSearchPattern -File -Recurse -ErrorAction SilentlyContinue |
                    Where-Object {
                        $_.Name -ne 'setup.exe' -and
                        $_.FullName -ne $bootstrapper -and
                        $_.Extension -in @('.exe', '.iso')
                    } |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
            }

            try {
                if (Get-Service -Name MSSQLSERVER -ErrorAction SilentlyContinue) {
                    Step '检测到已存在 MSSQLSERVER，先执行配置修复'
                    Configure-SqlServer
                    try {
                        Restart-Service -Name MSSQLSERVER -Force -ErrorAction Stop
                    } catch {
                        Write-Output ('现有 SQL Server 重启失败：' + $_.Exception.Message)
                    }

                    if (Wait-ServiceState MSSQLSERVER Running 90) {
                        if (!(Ensure-CurrentWindowsSqlLogin)) {
                            Write-Output '现有 SQL Server 已启动，但未能自动修复当前 Windows 登录。'
                        }
                        Write-Output 'SQL Server 已存在，配置修复完成。'
                        Clear-SqlRestartMarker
                        Stop-Transcript | Out-Null
                        exit 0
                    }

                    Write-Output '现有 MSSQLSERVER 无法启动，按半安装残留处理并重新安装。'
                    Remove-BrokenSqlServer
                }

                $staleInstanceKey = 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\' + $defaultInstanceId
                if (Test-Path $staleInstanceKey) {
                    Write-Output '检测到 SQL Server 注册表残留但服务不可用，执行安装前清理。'
                    Remove-BrokenSqlServer
                }

                Step '准备 SQL Server 安装兼容项'
                $adminAccounts = Get-SqlAdminAccounts
                $adminArgument = Format-SqlSysadminAccountsArgument $adminAccounts
                Write-Output ('SQL Server 管理员账户：' + ($adminAccounts -join ', '))
                if (!(Test-Path $mediaRoot)) { New-Item -ItemType Directory -Path $mediaRoot -Force | Out-Null }
                Ensure-SqlSectorCompatibility
                Clear-SqlRestartMarker
                Repair-RsFxRegistry
                Step '清理 Windows 待处理文件重命名项'
                Clear-PendingFileRenameOperations

                Step '准备 SQL Server 数据目录'
                Ensure-Dir $dataRoot
                Ensure-Dir (Join-Path $dataRoot 'Data')
                Ensure-Dir (Join-Path $dataRoot 'Log')
                Ensure-Dir (Join-Path $dataRoot 'TempDB')
                Ensure-Dir (Join-Path $dataRoot 'TempDBLog')
                Ensure-Dir (Join-Path $dataRoot 'Backup')

                Step ('准备官方 ' + $displayName + ' 安装媒体')
                $downloadedSetup = Find-SetupMedia
                if ($downloadedSetup) { $setupExe = $downloadedSetup.FullName }
                $package = Find-CompressedMedia
                if (!(Test-Path $setupExe) -and !$package) {
                    $downloadArgs='/ACTION=Download /MEDIATYPE=' + $mediaType + ' /QUIET /MEDIAPATH="' + $mediaRoot + '"'
                    $download = Start-Process -FilePath $bootstrapper -ArgumentList $downloadArgs -Wait -PassThru -WindowStyle Hidden
                    if ($download.ExitCode -ne 0) { Fail ('官方安装媒体下载失败，退出码：' + $download.ExitCode) }
                    $downloadedSetup = Find-SetupMedia
                    if ($downloadedSetup) { $setupExe = $downloadedSetup.FullName }
                    $package = Find-CompressedMedia
                }

                if (!(Test-Path $setupExe)) {
                    if (!$package) { Fail ('未找到 ' + $displayName + ' 官方安装媒体。') }
                    if ($package.Extension -ieq '.iso') {
                        Step '挂载官方 SQL Server ISO 安装媒体'
                        try {
                            Mount-DiskImage -ImagePath $package.FullName -PassThru -ErrorAction Stop | Out-Null
                            $script:mountedIsoPath=$package.FullName
                            $image = Get-DiskImage -ImagePath $package.FullName -ErrorAction Stop
                            $volume=$null
                            for ($attempt=0; $attempt -lt 20 -and !$volume; $attempt++) {
                                $volume = Get-Volume -ErrorAction SilentlyContinue |
                                    Where-Object {
                                        $_.DriveLetter -and
                                        $_.FileSystem -eq 'CDFS' -and
                                        $_.Size -eq $image.Size
                                    } |
                                    Select-Object -First 1
                                if (!$volume) { Start-Sleep -Milliseconds 500 }
                            }
                            if (!$volume) { Fail '官方 SQL Server ISO 已挂载，但未找到可用盘符。' }
                            $isoRoot=$volume.DriveLetter + ':\'
                            $setupCandidate = Get-ChildItem -LiteralPath $isoRoot -Filter 'setup.exe' -File -Recurse -ErrorAction SilentlyContinue |
                                Select-Object -First 1
                            if (!$setupCandidate) { Fail '官方 SQL Server ISO 中未找到 setup.exe。' }
                            $setupExe=$setupCandidate.FullName
                        }
                        catch {
                            Fail ('官方 SQL Server ISO 挂载失败：' + $_.Exception.Message)
                        }
                    }
                    else {
                        if (Test-Path $extractRoot) { Remove-Item -LiteralPath $extractRoot -Recurse -Force -ErrorAction SilentlyContinue }
                        New-Item -ItemType Directory -Path $extractRoot -Force | Out-Null
                        $extractArgs='/x:"' + $extractRoot + '" /q'
                        $extract = Start-Process -FilePath $package.FullName -ArgumentList $extractArgs -Wait -PassThru -WindowStyle Hidden
                        if ($extract.ExitCode -ne 0 -or !(Test-Path $setupExe)) { Fail ('官方安装媒体解压失败，退出码：' + $extract.ExitCode) }
                    }
                }

                Step ('运行官方 ' + $displayName + ' 安装程序')
                $args = @(
                    '/QS',
                    '/INDICATEPROGRESS',
                    '/ACTION=Install',
                    '/FEATURES=SQLENGINE',
                    '/INSTANCENAME=MSSQLSERVER',
                    '/IACCEPTSQLSERVERLICENSETERMS',
                    '/SQLSVCACCOUNT="NT AUTHORITY\NETWORK SERVICE"',
                    '/SQLSVCSTARTUPTYPE=Automatic',
                    ('/SQLSYSADMINACCOUNTS=' + $adminArgument),
                    '/SECURITYMODE=SQL',
                    ('/SAPWD="' + $saPassword + '"'),
                    '/TCPENABLED=1',
                    ('/INSTALLSQLDATADIR="' + $dataRoot + '"'),
                    ('/SQLUSERDBDIR="' + (Join-Path $dataRoot 'Data') + '"'),
                    ('/SQLUSERDBLOGDIR="' + (Join-Path $dataRoot 'Log') + '"'),
                    ('/SQLTEMPDBDIR="' + (Join-Path $dataRoot 'TempDB') + '"'),
                    ('/SQLTEMPDBLOGDIR="' + (Join-Path $dataRoot 'TempDBLog') + '"'),
                    ('/SQLBACKUPDIR="' + (Join-Path $dataRoot 'Backup') + '"')
                )

                $install = Start-Process -FilePath $setupExe -ArgumentList $args -Wait -PassThru -WindowStyle Hidden
                if ($install.ExitCode -ne 0 -and $install.ExitCode -ne 3010) { Fail ('官方安装程序退出码：' + $install.ExitCode) }

                Configure-SqlServer
                Restart-Service -Name MSSQLSERVER -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState MSSQLSERVER Running 120)) { Fail 'MSSQLSERVER 服务启动超时。' }
                if (!(Ensure-CurrentWindowsSqlLogin)) { Fail 'SQL Server 已安装，但当前 Windows 用户登录验证失败。' }

                Clear-SqlRestartMarker
                Dismount-InstallMedia
                Write-Output ($displayName + ' 安装完成。')
                Stop-Transcript | Out-Null
                exit 0
            }
            catch {
                Fail $_.Exception.Message
            }
            """;
    }

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
