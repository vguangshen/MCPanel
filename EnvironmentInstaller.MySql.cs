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
    private async Task InstallMySqlAsync(
        MySqlReleaseDefinition release,
        Action<InstallProgress> progress,
        CancellationToken cancellationToken)
    {
        var root = ComponentPaths.SelectMySqlInstallRoot();
        Directory.CreateDirectory(root);

        progress(new InstallProgress(0, $"准备安装 {release.DisplayName}...", InstallProgressStage.Preparing, 0));
        var archive = await DownloadAbsoluteFileAsync(
            release.PackageUrl,
            GetPackageDirectory(),
            release.PackageFileName,
            progress,
            0,
            65,
            cancellationToken);

        var stagingContainer = Path.Combine(GetTempDirectory(), "mysql-staging", $"mysql.{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingContainer);
        progress(InstallingProgress(65, $"正在暂存并校验 {release.DisplayName} 安装包...", 0));
        await ExtractZipAsync(archive, stagingContainer, cancellationToken);
        var stagedMySqlExe = Directory.GetFiles(stagingContainer, "mysql.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var stagedMysqldExe = Directory.GetFiles(stagingContainer, "mysqld*.exe", SearchOption.AllDirectories)
            .FirstOrDefault(path => Path.GetFileName(path).Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("mysqld-itmc.exe", StringComparison.OrdinalIgnoreCase));
        if (stagedMySqlExe is null || stagedMysqldExe is null)
        {
            DeleteDirectoryBestEffort(stagingContainer);
            throw new FileNotFoundException("MySQL 暂存包中未找到 mysql.exe 或 mysqld.exe。");
        }

        var stagedMySqlRoot = Directory.GetParent(Path.GetDirectoryName(stagedMySqlExe)!)!.FullName;
        var existingMySqlExe = new ComponentLocator().FindMySqlExecutable(root, stagingContainer);
        var targetMySqlRoot = existingMySqlExe is null
            ? IsSamePath(root, ComponentPaths.MySqlRoot) ? root : Path.Combine(root, "mysql")
            : Directory.GetParent(Path.GetDirectoryName(existingMySqlExe)!)!.FullName;

        progress(InstallingProgress(67, "下载校验完成，正在停止旧 MySQL 服务...", 10));
        var prepareScript = Path.Combine(GetTempDirectory(), "prepare-mysql-install.ps1");
        await FileCompat.WriteAllTextAsync(prepareScript, BuildMySqlInstallPreparationScript(), new UTF8Encoding(true), cancellationToken);
        await RunElevatedPowerShellAsync(prepareScript, progress, 12, 20, cancellationToken, requireExistingAdministrator: true);

        progress(InstallingProgress(70, "正在切换 MySQL 运行目录并保留原数据库...", 28));
        using var runtimeTransaction = MySqlRuntimeTransaction.Commit(
            stagingContainer,
            stagedMySqlRoot,
            targetMySqlRoot,
            Path.Combine(GetTempDirectory(), "mysql-rollback"));
        var mysqlRoot = runtimeTransaction.ProductRoot;
        var mysqlExe = Directory.GetFiles(mysqlRoot, "mysql.exe", SearchOption.AllDirectories)
            .First(path => path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase));
        var mysqldExe = Directory.GetFiles(mysqlRoot, "mysqld*.exe", SearchOption.AllDirectories)
            .First(path => Path.GetFileName(path).Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) || Path.GetFileName(path).Equals("mysqld-itmc.exe", StringComparison.OrdinalIgnoreCase));
        var credentials = existingMySqlExe is null ? MySqlCredentialStore.CreateNew() : MySqlCredentialStore.Load();
        var script = Path.Combine(GetTempDirectory(), "install-mysql.ps1");
        await FileCompat.WriteAllTextAsync(script, BuildMySqlScript(mysqlRoot, mysqldExe, mysqlExe, credentials.Password, credentials.Port), new UTF8Encoding(true), cancellationToken);

        try
        {
            progress(InstallingProgress(82, "正在注册并启动 MySQL 服务...", 45));
            await RunElevatedPowerShellAsync(script, progress, 48, 95, cancellationToken, requireExistingAdministrator: true);
            MySqlCredentialStore.Save(credentials);
            runtimeTransaction.Complete();
            progress(InstallingProgress(100, $"{release.DisplayName} 已安装到 {mysqlRoot}。连接信息：127.0.0.1:{credentials.Port}，账号 root。"));
        }
        catch
        {
            await CleanupFailedMySqlInstallAsync(mysqlRoot);
            runtimeTransaction.Rollback();
            throw;
        }
    }

    private static async Task CleanupFailedMySqlInstallAsync(string mysqlRoot)
    {
        try
        {
            var script = Path.Combine(GetTempDirectory(), "cleanup-failed-mysql-install.ps1");
            await FileCompat.WriteAllTextAsync(
                script,
                BuildMySqlInstallCleanupScript(mysqlRoot),
                new UTF8Encoding(true),
                CancellationToken.None);
            await RunElevatedPowerShellAsync(
                script,
                _ => { },
                0,
                0,
                CancellationToken.None,
                requireExistingAdministrator: true);
        }
        catch
        {
            // Preserve the original installation error.  The generated install
            // script already performs the same cleanup when it can complete.
        }
    }

    internal static string BuildMySqlInstallCleanupScript(string mysqlRoot)
    {
        var root = EscapePowerShellPath(mysqlRoot);
        var workRoot = EscapePowerShellPath(GetTempDirectory());
        return $$"""
            $ErrorActionPreference = 'Continue'
            $root = '{{root}}'
            $workRoot = '{{workRoot}}'
            if (!(Test-Path -LiteralPath $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot 'cleanup-failed-mysql-install.log'
            Start-Transcript -Path $log -Append | Out-Null

            function Get-ServiceExecutable($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $null }
                $expanded = [Environment]::ExpandEnvironmentVariables($path).Trim()
                if ($expanded.StartsWith('"')) {
                    $end = $expanded.IndexOf('"', 1)
                    if ($end -gt 1) { return $expanded.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($expanded, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            $service = Get-CimInstance Win32_Service -Filter "Name='MySQL80'" -ErrorAction SilentlyContinue
            $serviceExecutable = Get-ServiceExecutable ($service.PathName)
            $managed = $service -and $serviceExecutable -and
                ([IO.Path]::GetFullPath($serviceExecutable).StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\\') + '\\'), [StringComparison]::OrdinalIgnoreCase))
            if ($managed) {
                Get-Process -Name 'mysqld','mysqld-itmc','mysql','mysqladmin','mysqldump' -ErrorAction SilentlyContinue | ForEach-Object {
                    try {
                        if ($_.Path -and ([IO.Path]::GetFullPath($_.Path).StartsWith(([IO.Path]::GetFullPath($root).TrimEnd('\\') + '\\'), [StringComparison]::OrdinalIgnoreCase))) {
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    } catch { }
                }
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                & sc.exe delete MySQL80 | Out-String | Write-Output
            } elseif ($service) {
                Write-Output ('未清理路径不属于本次安装的 MySQL80 服务：' + $service.PathName)
            }

            try { Stop-Transcript | Out-Null } catch { }
            exit 0
            """;
    }

    internal static string BuildMySqlInstallPreparationScript()
    {
        var mysqlRoots = ComponentPaths.MySqlSearchRoots
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(path => $"'{EscapePowerShellPath(path)}'");
        var mysqlRootsLiteral = string.Join(", ", mysqlRoots);
        var storeDataRoot = EscapePowerShellPath(GetStoreDataRoot());
        return $$"""
            $ErrorActionPreference = 'Continue'
            $ProgressPreference = 'SilentlyContinue'
            $mysqlRoots = @({{mysqlRootsLiteral}})
            $workRoot = Join-Path '{{storeDataRoot}}' 'Work'
            if (!(Test-Path $workRoot)) { New-Item -ItemType Directory -Path $workRoot -Force | Out-Null }
            $log = Join-Path $workRoot 'prepare-mysql-install.log'
            Start-Transcript -Path $log -Append | Out-Null

            function Stop-StoreDataMySqlProcesses {
                foreach ($name in @('mysqld','mysqld-itmc','mysql','mysqladmin','mysqldump')) {
                    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
                        $path = $null
                        try { $path = $_.Path } catch { }
                        if ($path -and ($mysqlRoots | Where-Object { $path.StartsWith($_, [StringComparison]::OrdinalIgnoreCase) })) {
                            Write-Output ('停止进程：' + $_.ProcessName + ' #' + $_.Id)
                            Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue
                        }
                    }
                }
            }

            function Wait-ServiceGone {
                for ($i = 0; $i -lt 60; $i++) {
                    if (-not (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { return $true }
                    Start-Sleep -Milliseconds 500
                }
                return $false
            }

            function Get-ServiceExecutable($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $null }
                $expanded = [Environment]::ExpandEnvironmentVariables($path).Trim()
                if ($expanded.StartsWith('"')) {
                    $end = $expanded.IndexOf('"', 1)
                    if ($end -gt 1) { return $expanded.Substring(1, $end - 1) }
                }
                $match = [regex]::Match($expanded, '^[^\r\n]*?\.exe', 'IgnoreCase')
                if ($match.Success) { return $match.Value.Trim() }
                return $null
            }

            function Is-ManagedMySqlPath($path) {
                if ([string]::IsNullOrWhiteSpace($path)) { return $false }
                try {
                    $full = [IO.Path]::GetFullPath($path)
                    foreach ($root in $mysqlRoots) {
                        $prefix = [IO.Path]::GetFullPath($root).TrimEnd('\') + '\'
                        if ($full.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) { return $true }
                    }
                } catch { }
                return $false
            }

            $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
            $serviceExecutable = Get-ServiceExecutable ((Get-CimInstance Win32_Service -Filter "Name='MySQL80'" -ErrorAction SilentlyContinue).PathName)
            $managedService = $svc -and (Is-ManagedMySqlPath $serviceExecutable)
            if ($svc -and -not $managedService) {
                throw ('检测到非 MCPanel 管理的 MySQL80 服务，已停止安装以保护现有实例：' + $serviceExecutable)
            }
            if ($svc -and $svc.Status -ne 'Stopped') {
                Write-Output '正在停止 MySQL80 服务...'
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(45)) } catch { Start-Sleep -Seconds 2 }
            }

            Stop-StoreDataMySqlProcesses

            $mysqld = $null
            foreach ($mysqlRoot in $mysqlRoots) {
                if (Test-Path $mysqlRoot) {
                    $mysqld = Get-ChildItem $mysqlRoot -Filter 'mysqld*.exe' -Recurse -ErrorAction SilentlyContinue |
                        Where-Object {
                            $_.Name -in @('mysqld.exe', 'mysqld-itmc.exe') -and
                            $_.FullName -like '*\bin\*'
                        } |
                        Select-Object -First 1
                    if ($mysqld) { break }
                }
            }
            if ($mysqld -and (!$svc -or $managedService)) {
                & $mysqld.FullName --remove MySQL80 | Out-Null
            }

            if (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue) {
                if (-not $managedService) {
                    throw 'MySQL80 服务归属无法确认，已停止安装以保护现有实例。'
                }
                & sc.exe delete MySQL80 | Out-String | Write-Output
                if (!(Wait-ServiceGone)) { throw 'MySQL80 服务正在等待系统删除，请稍后重试或重启 Windows 后再安装。' }
            }

            Stop-StoreDataMySqlProcesses

            Stop-Transcript | Out-Null
            exit 0
            """;
    }

    internal static string BuildMySqlScript(
        string mysqlRoot,
        string mysqldExe,
        string mysqlExe,
        string rootPassword,
        int port = MySqlCredentialStore.DefaultPort)
    {
        var data = Path.Combine(mysqlRoot, "data");
        var myIni = Path.Combine(mysqlRoot, "my.ini");
        var errorLog = Path.Combine(data, "mysql-itmc.err");
        var initFile = Path.Combine(data, "mysql-itmc-init.sql");
        var workRoot = GetTempDirectory();
        var log = Path.Combine(workRoot, "install-mysql.log");
        var escapedPassword = EscapePowerShellPath(rootPassword);
        var mysqlPort = port is >= 1 and <= 65535 ? port : MySqlCredentialStore.DefaultPort;
        return $$"""
            $ErrorActionPreference='Continue'
            $ProgressPreference='SilentlyContinue'
            $root='{{EscapePowerShellPath(mysqlRoot)}}'
            $data='{{EscapePowerShellPath(data)}}'
            $mysqld='{{EscapePowerShellPath(mysqldExe)}}'
            $mysql='{{EscapePowerShellPath(mysqlExe)}}'
            $rootPassword='{{escapedPassword}}'
            $myIni='{{EscapePowerShellPath(myIni)}}'
            $errorLog='{{EscapePowerShellPath(errorLog)}}'
            $initFile='{{EscapePowerShellPath(initFile)}}'
            $log='{{EscapePowerShellPath(log)}}'
            $registeredService = $false
            $serviceWasPresent = [bool](Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)
            Start-Transcript -Path $log -Append | Out-Null

            function Step($message) {
                Write-Output ''
                Write-Output ('==== ' + $message + ' ====')
            }

            function Fail($message) {
                Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue
                if ($script:registeredService) {
                    Remove-MySqlService
                }
                $tail = Read-ErrorTail
                Write-Output ('安装失败：' + $message)
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Write-Output ('最近错误日志：' + [Environment]::NewLine + $tail)
                }
                try { Stop-Transcript | Out-Null } catch { }
                exit 1
            }

            function Invoke-Native($file, [string[]]$arguments) {
                & $file @arguments 1>$null 2>$null
                return [int]$LASTEXITCODE
            }

            function Invoke-NativeQuiet($file, [string[]]$arguments) {
                return Invoke-Native $file $arguments
            }

            function Read-ErrorTail {
                $err = Get-ChildItem $data -Filter '*.err' -ErrorAction SilentlyContinue |
                    Sort-Object LastWriteTime -Descending |
                    Select-Object -First 1
                if ($err) {
                    return ((Get-Content $err.FullName -Tail 80 -ErrorAction SilentlyContinue) -join [Environment]::NewLine)
                }
                return ''
            }

            function Wait-ServiceState($target, $seconds) {
                $deadline = (Get-Date).AddSeconds($seconds)
                while ((Get-Date) -lt $deadline) {
                    $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                    if ($svc -and $svc.Status.ToString() -eq $target) { return $true }
                    Start-Sleep -Milliseconds 500
                }
                return $false
            }

            function Remove-MySqlService {
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if ($svc -and $svc.Status -ne 'Stopped') {
                    Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                    try { $svc.WaitForStatus('Stopped', [TimeSpan]::FromSeconds(30)) } catch { }
                }

                if (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue) {
                    & sc.exe delete MySQL80 | Out-String | Write-Output
                    for ($i = 0; $i -lt 20; $i++) {
                        if (-not (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { break }
                        Start-Sleep -Milliseconds 500
                    }
                }
                $script:registeredService = $false
            }

            function Start-MySqlService {
                Step '启动 MySQL80 服务'
                & sc.exe config MySQL80 start= auto | Out-Null
                $lastError = ''
                for ($i = 1; $i -le 3; $i++) {
                    try {
                        $svc = Get-Service -Name 'MySQL80' -ErrorAction Stop
                        if ($svc.Status -eq 'Running') { return }
                        if ($svc.Status -eq 'Stopped') {
                            Start-Service -Name 'MySQL80' -ErrorAction Stop
                        }
                    }
                    catch {
                        $lastError = $_.Exception.Message
                        Write-Output ('第 ' + $i + ' 次启动失败：' + $lastError)
                        & sc.exe start MySQL80 | Out-String | Write-Output
                    }

                    # MySQL may spend several minutes recovering a large
                    # InnoDB data directory.  Do not issue another start while
                    # SCM reports START_PENDING; it only resets the wait and
                    # makes a healthy slow start look like a failure.
                    if (Wait-ServiceState 'Running' 60) { return }
                }

                $tail = Read-ErrorTail
                $state = (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue).Status
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Fail ("MySQL80 启动失败，当前状态：$state。最近错误日志：" + [Environment]::NewLine + $tail)
                }
                Fail ("MySQL80 启动失败，当前状态：$state。" + $lastError)
            }

            function Stop-MySqlService {
                $svc = Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue
                if (-not $svc -or $svc.Status -eq 'Stopped') { return }
                Step '停止 MySQL80 服务'
                Stop-Service -Name 'MySQL80' -Force -ErrorAction SilentlyContinue
                if (!(Wait-ServiceState 'Stopped' 45)) {
                    & sc.exe stop MySQL80 | Out-String | Write-Output
                    if (!(Wait-ServiceState 'Stopped' 30)) {
                        Fail 'MySQL80 停止超时，请确认没有外部程序占用服务。'
                    }
                }
            }

            function Test-RootPassword {
                $code = Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
                return $code -eq 0
            }

            function Stop-TemporaryMySqlProcess($process) {
                if (!$process -or $process.HasExited) { return }
                Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SHUTDOWN;') | Out-Null
                for ($i = 0; $i -lt 30; $i++) {
                    if ($process.HasExited) { return }
                    Start-Sleep -Milliseconds 500
                }
                if (-not $process.HasExited) {
                    Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
                    Start-Sleep -Seconds 2
                }
            }

            function Reset-RootPassword {
                Step '重置 root 密码'
                Stop-MySqlService
                # MySQL 官方 Windows 流程使用 init-file 临时执行改密语句。
                # 不使用跳过权限表的启动模式：它会自动启用 skip_networking，
                # 在 Windows 上会关闭 TCP，导致服务以 port 0 启动或直接退出。
                $sqlPassword = $rootPassword.Replace('\', '\\').Replace("'", "''")
                $initSql = if ($isModern) {
                    "ALTER USER 'root'@'localhost' IDENTIFIED BY '$sqlPassword';"
                } else {
                    "SET PASSWORD FOR 'root'@'localhost' = PASSWORD('$sqlPassword');"
                }
                $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
                [IO.File]::WriteAllText($initFile, $initSql, $utf8NoBom)
                $proc = $null
                $updated = $false
                $failure = ''
                try {
                    $proc = Start-Process -FilePath $mysqld -ArgumentList @("--defaults-file=$myIni", "--init-file=$initFile", "--bind-address=127.0.0.1") -WindowStyle Hidden -PassThru
                    for ($i = 0; $i -lt 35; $i++) {
                        Start-Sleep -Seconds 1
                        if ($proc.HasExited) { break }
                        $code = Invoke-NativeQuiet $mysql @('-h127.0.0.1', '-P{{mysqlPort}}', '-uroot', "-p$rootPassword", '-e', 'SELECT 1;')
                        if ($code -eq 0) {
                            $updated = $true
                            break
                        }
                    }

                    if (!$updated) { $failure = 'MySQL root 密码重置失败，临时维护进程未能接受连接。' }
                }
                catch { $failure = $_.Exception.Message }
                finally {
                    Stop-TemporaryMySqlProcess $proc
                    Remove-Item -LiteralPath $initFile -Force -ErrorAction SilentlyContinue
                }

                if (!$updated) {
                    $tail = Read-ErrorTail
                    Fail ($failure + [Environment]::NewLine + $tail)
                }
                Start-MySqlService
            }

            try {
                Step '生成 my.ini'
                if (!(Test-Path $data)) { New-Item -ItemType Directory -Path $data -Force | Out-Null }
                $versionText = (& $mysqld --version) -join ' '
                $isModern = $versionText -match 'Ver\s+(8|9)\.|mysqld\s+(8|9)\.'
                # MySQL 8.4 and 9.7 no longer accept the legacy default
                # authentication-plugin setting. ALTER USER below lets each
                # release keep its supported default.
                $authLine = ''
                @"
            [mysqld]
            basedir={{mysqlRoot.Replace("\\", "/")}}
            datadir={{data.Replace("\\", "/")}}
            log-error={{errorLog.Replace("\\", "/")}}
            port={{mysqlPort}}
            bind-address=127.0.0.1
            character-set-server=utf8mb4
            $authLine
            [client]
            port={{mysqlPort}}
            default-character-set=utf8
            "@ | Set-Content -Encoding ASCII $myIni

                if (!(Test-Path (Join-Path $data 'mysql')) -and $isModern) {
                    Step '初始化 MySQL 数据目录'
                    $code = Invoke-Native $mysqld @("--defaults-file=$myIni", '--initialize-insecure')
                    if ($code -ne 0) { Fail "MySQL 初始化失败，退出码：$code" }
                }

                Step '注册 MySQL80 服务'
                if ($serviceWasPresent) { Fail '安装前仍检测到 MySQL80 服务，已停止安装以保护现有实例。' }
                $code = Invoke-Native $mysqld @('--install', 'MySQL80', "--defaults-file=$myIni")
                if (!$serviceWasPresent -and (Get-Service -Name 'MySQL80' -ErrorAction SilentlyContinue)) { $registeredService = $true }
                if ($code -ne 0) { Fail "MySQL 服务注册失败，退出码：$code" }

                Start-MySqlService
                Start-Sleep -Seconds 2

                if (-not (Test-RootPassword)) { Reset-RootPassword }
                if (-not (Test-RootPassword)) { Fail 'MySQL 已启动，但 root 密码校验失败。' }

                Write-Output 'MySQL 安装完成。'
                exit 0
            }
            catch {
                if ($script:registeredService) {
                    Remove-MySqlService
                }
                Write-Output ('安装失败：' + $_.Exception.Message)
                $tail = Read-ErrorTail
                if (![string]::IsNullOrWhiteSpace($tail)) {
                    Write-Output ('最近错误日志：' + [Environment]::NewLine + $tail)
                }
                exit 1
            }
            finally {
                try { Stop-Transcript | Out-Null } catch { }
            }
            """;
    }
}
