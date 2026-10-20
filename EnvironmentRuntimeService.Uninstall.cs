using System.IO;
using System.Text;

namespace MCPanel;

public sealed partial class EnvironmentRuntimeService
{
    private static async Task RunRuntimeUninstallAsync(
        string component, Func<string, string> buildScript, CancellationToken cancellationToken)
    {
        var operation = $"uninstall-{component}-{Guid.NewGuid():N}";
        var script = Path.Combine(ComponentPaths.WorkRoot, operation + ".ps1");
        var log = Path.Combine(ComponentPaths.WorkRoot, operation + ".log");
        await ElevatedActionLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ComponentPaths.WorkRoot);
            await FileCompat.WriteAllTextAsync(script, buildScript(Path.GetFileName(log)),
                new UTF8Encoding(true), cancellationToken);
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(ComponentPaths.WorkRoot, $"uninstall-{component}-*.log*", log);
            ElevatedActionLock.Release();
        }
    }

    private static string PowerShellPaths(IEnumerable<string> paths) => string.Join(", ", paths
        .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => $"'{EscapePowerShellPath(path)}'"));

    private static string BuildRuntimeUninstallScript(string body, string logFileName) =>
        BuildElevatedPowerShellScript(RuntimeUninstallFunctions + Environment.NewLine + body,
            Path.Combine(ComponentPaths.WorkRoot, logFileName));

    private static IEnumerable<string> ProtectedUninstallRoots => new[]
    {
        ComponentPaths.ApplicationRoot, ComponentPaths.StoreDataRoot,
        ComponentPaths.RuntimeRoot, ComponentPaths.LegacyRuntimeRoot
    };

    internal static string BuildNginxUninstallScript(string nginxRoot, string logFileName = "uninstall-nginx.log")
    {
        var imagePath = NginxWindowsServiceManager.BuildServiceImagePath(
            Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe"), nginxRoot);
        return BuildRuntimeUninstallScript($$"""
            $targetRoot = Assert-ComponentRoot '{{EscapePowerShellPath(nginxRoot)}}' @({{PowerShellPaths(ComponentPaths.NginxSearchRoots)}}) @({{PowerShellPaths(ProtectedUninstallRoots)}})
            $nginxExe = Join-Path $targetRoot 'nginx.exe'
            if (-not (Test-Path -LiteralPath $nginxExe)) { throw ('未找到组件 nginx.exe：' + $targetRoot) }
            $service = Get-ComponentService 'nginx'
            if ($service) {
                $serviceExe = Get-ServiceExecutable $service.ImagePath
                $panelWrapper = $service.ImagePath.Trim().Equals('{{EscapePowerShellPath(imagePath)}}', [StringComparison]::OrdinalIgnoreCase)
                $legacyWrapper = (Test-PathUnderRoot $serviceExe $targetRoot) -and ([IO.Path]::GetFileName($serviceExe) -eq 'nginx-server.exe')
                if (-not $panelWrapper -and -not $legacyWrapper) {
                    throw '检测到非当前 MCPanel 组件的 nginx 服务，已停止卸载以保护现有服务。'
                }
                Write-Output 'Stopping Nginx Windows service and watchdog.'
                Stop-ComponentService 'nginx'
            }

            $processNames = @('nginx.exe')
            if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) {
                # nginx documents -s quit (graceful), then -s stop (fast).
                # The explicit prefix works regardless of the elevated process CWD.
                foreach ($signal in @('quit', 'stop')) {
                    Write-Output ('Sending nginx -s ' + $signal)
                    try { & $nginxExe -p ($targetRoot.Replace('\', '/') + '/') -s $signal 2>&1 | Write-Output }
                    catch { Write-Output $_.Exception.Message }
                    $global:LASTEXITCODE = 0
                    if (Wait-ComponentProcesses $targetRoot $processNames 10) { break }
                }
                if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) {
                    Get-ComponentProcesses $targetRoot $processNames | ForEach-Object {
                        Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                    }
                    if (-not (Wait-ComponentProcesses $targetRoot $processNames 5)) {
                        throw 'Nginx 进程仍在运行，已停止删除组件文件。'
                    }
                }
            }
            if ($service) { Remove-ComponentService 'nginx' }
            if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) { throw 'Nginx 进程重新出现，已停止删除。' }
            Remove-ComponentDirectory $targetRoot
            if (Test-Path -LiteralPath $targetRoot) { throw ('Nginx 目录仍然存在：' + $targetRoot) }
            """, logFileName);
    }

    internal static string BuildMySqlUninstallScript(string? mysqlRoot, string logFileName = "uninstall-mysql.log") =>
        BuildRuntimeUninstallScript($$"""
            $mysqlInstallationRoots = @({{PowerShellPaths(ComponentPaths.MySqlInstallationRoots)}})
            $protectedRoots = @({{PowerShellPaths(ProtectedUninstallRoots)}})
            $targetRoot = '{{EscapePowerShellPath(mysqlRoot ?? string.Empty)}}'
            $service = Get-ComponentService 'MySQL80'
            if ($service) {
                $serviceExe = Get-ServiceExecutable $service.ImagePath
                $serviceRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($serviceExe))
                $serverName = [IO.Path]::GetFileName($serviceExe)
                $legacyParents = @({{PowerShellPaths(new[] { ComponentPaths.RuntimeRoot, ComponentPaths.LegacyRuntimeRoot })}})
                $legacyMySql = [IO.Path]::GetFileName($serviceRoot).StartsWith('mysql', [StringComparison]::OrdinalIgnoreCase) -and
                    @($legacyParents | Where-Object { [IO.Path]::GetFullPath($_).TrimEnd('\').Equals([IO.Path]::GetDirectoryName($serviceRoot), [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
                if ($serverName -notin @('mysqld.exe', 'mysqld-itmc.exe') -or
                    [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($serviceExe)) -ne 'bin' -or
                    (-not $legacyMySql -and @($mysqlInstallationRoots | Where-Object { Test-PathUnderRoot $serviceRoot $_ }).Count -eq 0)) {
                    throw ('检测到非 MCPanel 管理的 MySQL80 服务，已停止卸载以保护现有实例：' + $serviceExe)
                }
                if ($legacyMySql) { $mysqlInstallationRoots += $serviceRoot }
                # The registered service identifies the actual installation,
                # rather than whichever mysql.exe a broad search finds first.
                $targetRoot = $serviceRoot
            }
            if ([string]::IsNullOrWhiteSpace($targetRoot)) {
                foreach ($root in $mysqlInstallationRoots) {
                    if (Test-Path -LiteralPath (Join-Path $root 'bin\mysql.exe')) { $targetRoot = $root; break }
                    if (-not (Test-Path -LiteralPath $root)) { continue }
                    $candidate = Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop |
                        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\mysql.exe')) } |
                        Select-Object -First 1
                    if ($candidate) { $targetRoot = $candidate.FullName; break }
                }
            }
            if ([string]::IsNullOrWhiteSpace($targetRoot)) {
                Write-Output 'MySQL 组件和服务均不存在，无需卸载。'
            }
            else {
                # Validate the directory before stopping or removing any service.
                $targetRoot = Assert-ComponentRoot $targetRoot $mysqlInstallationRoots $protectedRoots
                if (-not $service -and (Test-Path -LiteralPath $targetRoot) -and
                    -not (Test-Path -LiteralPath (Join-Path $targetRoot 'bin\mysql.exe'))) {
                    throw ('未在待删除目录中找到 bin\mysql.exe，已停止删除：' + $targetRoot)
                }
                if ($service) {
                    Write-Output 'Stopping MySQL80 using the Windows Service Control Manager.'
                    Stop-ComponentService 'MySQL80'
                }
                $processNames = @('mysqld.exe', 'mysqld-itmc.exe')
                if (-not (Wait-ComponentProcesses $targetRoot $processNames 10)) {
                    # Do not force-kill a database and risk an unclean shutdown.
                    throw 'MySQL 进程仍在运行，请先正常关闭数据库后再次卸载；服务和数据目录尚未删除。'
                }
                if ($service) { Remove-ComponentService 'MySQL80' }
                if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) { throw 'MySQL 进程重新出现，已停止删除。' }
                Write-Output ('Removing MySQL installation directory: ' + $targetRoot)
                Remove-ComponentDirectory $targetRoot
                if (Test-Path -LiteralPath $targetRoot) { throw ('MySQL 目录仍然存在：' + $targetRoot) }
            }
            """, logFileName);

    // Windows Server 2012 ships PowerShell 3.0. Avoid newer cmdlet parameters,
    // recursive Remove-Item and recursive ACL changes, particularly at junctions.
    internal const string RuntimeUninstallFunctions = """
        Add-Type -AssemblyName System.ServiceProcess
        Add-Type -AssemblyName System.Management
        function Get-ComponentService($name) {
            # PowerShell can silently return null for a failing .NET property
            # getter. Native exit codes distinguish absent from inaccessible.
            $query = & sc.exe query $name
            $queryExitCode = $LASTEXITCODE
            $global:LASTEXITCODE = 0
            if ($queryExitCode -eq 1060) { return $null }
            if ($queryExitCode -eq 1072) {
                throw ($name + ' 服务已标记删除。请关闭服务管理器；必要时重启后再次卸载。组件文件尚未删除。')
            }
            if ($queryExitCode -ne 0) { throw ('无法查询服务 ' + $name + '，退出码：' + $queryExitCode + '。' + ($query -join ' ')) }
            $key = Get-ItemProperty -LiteralPath ('HKLM:\SYSTEM\CurrentControlSet\Services\' + $name) -ErrorAction Stop
            return [pscustomobject]@{ ImagePath = [Environment]::ExpandEnvironmentVariables($key.ImagePath) }
        }

        function Get-ServiceExecutable($imagePath) {
            $match = [regex]::Match($imagePath, '^\s*(?:"([^"]+\.exe)"|(.+?\.exe)(?:\s|$))', 'IgnoreCase')
            if (-not $match.Success) { throw ('无法识别服务程序路径：' + $imagePath) }
            if ($match.Groups[1].Success) { return $match.Groups[1].Value }
            return $match.Groups[2].Value
        }

        function Test-PathUnderRoot($path, $root) {
            $full = [IO.Path]::GetFullPath($path).TrimEnd('\')
            $base = [IO.Path]::GetFullPath($root).TrimEnd('\')
            return $full.Equals($base, [StringComparison]::OrdinalIgnoreCase) -or
                $full.StartsWith($base + '\', [StringComparison]::OrdinalIgnoreCase)
        }

        function Assert-NoComponentLink($path) {
            $current = [IO.Path]::GetFullPath($path)
            while ($current) {
                if (Test-Path -LiteralPath $current) {
                    $item = Get-Item -LiteralPath $current -Force -ErrorAction Stop
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        throw ('组件路径包含目录链接，已停止卸载：' + $current)
                    }
                }
                $parent = [IO.Directory]::GetParent($current)
                if (-not $parent) { break }
                $current = $parent.FullName
            }
        }

        function Assert-ComponentRoot($root, $allowedRoots, $protectedRoots) {
            if ([string]::IsNullOrWhiteSpace($root)) { throw '组件卸载目录为空，已停止删除。' }
            $full = [IO.Path]::GetFullPath($root).TrimEnd('\')
            if ($full -eq [IO.Path]::GetPathRoot($full).TrimEnd('\')) { throw '不能删除磁盘根目录。' }
            foreach ($protected in $protectedRoots) {
                if (Test-PathUnderRoot $protected $full) { throw ('组件卸载目录与共享目录重叠，已停止删除：' + $full) }
            }
            if (@($allowedRoots | Where-Object { Test-PathUnderRoot $full $_ }).Count -eq 0) {
                throw ('组件卸载目录不在 MCPanel 管理范围：' + $full)
            }
            Assert-NoComponentLink $full
            return $full
        }

        function Stop-ComponentService($name) {
            $controller = New-Object System.ServiceProcess.ServiceController($name)
            try {
                $controller.Refresh()
                if ($controller.Status -ne 'Stopped') {
                    if ($controller.Status -ne 'StopPending') { $controller.Stop() }
                    $controller.WaitForStatus([System.ServiceProcess.ServiceControllerStatus]::Stopped, [TimeSpan]::FromSeconds(90))
                }
                $controller.Refresh()
                if ($controller.Status -ne 'Stopped') { throw '服务尚未完全停止。' }
            }
            catch { throw ($name + ' 服务停止失败，程序和数据文件尚未删除：' + $_.Exception.Message) }
            finally { $controller.Dispose() }
        }

        function Remove-ComponentService($name) {
            # SC DELETE is documented by MySQL and works even if mysqld cannot
            # start because a runtime DLL is missing. Close all our SCM handles.
            & sc.exe delete $name | Write-Output
            if ($LASTEXITCODE -notin @(0, 1060, 1072)) { throw ('删除服务失败，退出码：' + $LASTEXITCODE) }
            $global:LASTEXITCODE = 0
            $deadline = [DateTime]::UtcNow.AddSeconds(20)
            do {
                if (-not (Get-ComponentService $name)) { return }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            throw ($name + ' 服务仍然存在或已标记删除。请关闭服务管理器；必要时重启后再次卸载。程序和数据文件尚未删除。')
        }

        function Get-ComponentProcesses($root, $names) {
            $filter = ($names | ForEach-Object { "Name='$_'" }) -join ' OR '
            # Failure to enumerate must not be treated as proof that it stopped.
            # Use the framework WMI API, without depending on a PowerShell
            # module chosen from an inherited PowerShell 7 PSModulePath.
            $searcher = New-Object System.Management.ManagementObjectSearcher('SELECT ProcessId, ExecutablePath FROM Win32_Process WHERE ' + $filter)
            $searcher.Scope.Options.EnablePrivileges = $true
            $processes = $null
            try {
                $processes = $searcher.Get()
                foreach ($process in $processes) {
                    try {
                        $executable = $process['ExecutablePath']
                        if ($executable -and (Test-PathUnderRoot $executable $root)) {
                            [pscustomobject]@{ ProcessId = $process['ProcessId']; ExecutablePath = $executable }
                        }
                    }
                    finally { $process.Dispose() }
                }
            }
            finally {
                if ($null -ne $processes) { $processes.Dispose() }
                $searcher.Dispose()
            }
        }

        function Wait-ComponentProcesses($root, $names, $seconds) {
            $deadline = [DateTime]::UtcNow.AddSeconds($seconds)
            do {
                if (@(Get-ComponentProcesses $root $names).Count -eq 0) { return $true }
                Start-Sleep -Milliseconds 250
            } while ([DateTime]::UtcNow -lt $deadline)
            return $false
        }

        function Test-ComponentAccessDenied($errorRecord) {
            $exception = $errorRecord.Exception
            while ($exception) {
                if ($exception -is [UnauthorizedAccessException] -or ($exception.HResult -band 65535) -eq 5) { return $true }
                $exception = $exception.InnerException
            }
            return $false
        }

        function Repair-ComponentEntryAccess($path) {
            Assert-NoComponentLink $path
            Write-Output ('Repairing component deletion permissions: ' + $path)
            & takeown.exe /F $path /A | Write-Output
            if ($LASTEXITCODE -ne 0) { throw ('无法取得组件文件权限：' + $path) }
            $global:LASTEXITCODE = 0
            $isDirectory = (([IO.File]::GetAttributes($path) -band [IO.FileAttributes]::Directory) -ne 0)
            if ($isDirectory) { $acl = [IO.Directory]::GetAccessControl($path) }
            else { $acl = [IO.File]::GetAccessControl($path) }
            # Preserve existing inheritable allow rules for child entries.
            $acl.SetAccessRuleProtection($true, $true)
            foreach ($rule in @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))) {
                if ($rule.AccessControlType -eq 'Deny') { $acl.RemoveAccessRuleSpecific($rule) }
            }
            foreach ($sid in @('S-1-5-32-544', 'S-1-5-18', [Security.Principal.WindowsIdentity]::GetCurrent().User.Value)) {
                $identity = New-Object Security.Principal.SecurityIdentifier($sid)
                $rule = New-Object Security.AccessControl.FileSystemAccessRule($identity, 'FullControl', 'Allow')
                $acl.AddAccessRule($rule)
            }
            # Only the failing entry receives a replacement ACL, never its
            # descendants, siblings, shared roots or a junction's target.
            if ($isDirectory) { [IO.Directory]::SetAccessControl($path, $acl) }
            else { [IO.File]::SetAccessControl($path, $acl) }
        }

        function Remove-ComponentEntry($path, $isRoot) {
            for ($accessAttempt = 0; $accessAttempt -lt 2; $accessAttempt++) {
                $canRepairThisEntry = $true
                try {
                    try { $item = Get-Item -LiteralPath $path -Force -ErrorAction Stop }
                    catch [System.Management.Automation.ItemNotFoundException] { return }
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        if ($isRoot) { throw ('不能删除链接形式的组件根目录：' + $path) }
                        if ($item.PSIsContainer) { [IO.Directory]::Delete($path, $false) }
                        else { [IO.File]::Delete($path) }
                        return
                    }
                    # A static method preserves the native exception chain;
                    # PowerShell's property setter wrapper can hide access errors.
                    [IO.File]::SetAttributes($path, ($item.Attributes -band (-bnot ([IO.FileAttributes]::ReadOnly -bor [IO.FileAttributes]::Hidden -bor [IO.FileAttributes]::System))))
                    if ($item.PSIsContainer) {
                        # Leave install markers until last, so a blocked cleanup
                        # still appears installed and can be retried from MCPanel.
                        $children = @(Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop | Sort-Object @{Expression={
                            if ($_.Name -in @('bin', 'nginx.exe', 'mysql.exe', 'mysqld.exe', 'mysqld-itmc.exe')) { 1 } else { 0 }
                        }}, Name)
                        $canRepairThisEntry = $false
                        foreach ($child in $children) { Remove-ComponentEntry $child.FullName $false }
                        $canRepairThisEntry = $true
                        [IO.Directory]::Delete($path, $false)
                    }
                    else { [IO.File]::Delete($path) }
                    return
                }
                catch {
                    if ($canRepairThisEntry -and $accessAttempt -eq 0 -and (Test-ComponentAccessDenied $_)) { Repair-ComponentEntryAccess $path }
                    else { throw }
                }
            }
        }

        function Remove-ComponentDirectory($root) {
            for ($attempt = 0; $attempt -lt 5; $attempt++) {
                try { Remove-ComponentEntry $root $true; return }
                catch {
                    if ($attempt -eq 4) { throw ('组件目录清理失败，请关闭占用程序后再次卸载：' + $root + '。' + $_.Exception.Message) }
                    Start-Sleep -Milliseconds 400
                }
            }
        }
        """;
}
