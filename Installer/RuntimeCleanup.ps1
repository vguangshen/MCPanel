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
