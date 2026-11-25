namespace MCPanel;

/// <summary>
/// Shared by both elevated IIS binding paths. Grants Modify, only within the
/// managed site's own directory and keeping existing ACLs, to the actual pool
/// identity and to IUSR (S-1-5-17). Legacy ASP.NET products often impersonate
/// (&lt;identity impersonate="true"/&gt;), so their setup pages write web.config
/// as the anonymous user IUSR rather than the pool identity; IUSR used to
/// write through "Authenticated Users" inherited from the drive, which the
/// hardened install root no longer passes down.
/// </summary>
internal static class ManagedIisPermissionsScript
{
    internal const string Functions = """
        function Grant-McPanelIisProductAccess($managedRoot, $productRoot, $poolName, $appcmd) {
            $root = [IO.Path]::GetFullPath($managedRoot).TrimEnd('\', '/')
            $path = [IO.Path]::GetFullPath($productRoot).TrimEnd('\', '/')
            if (!$path.StartsWith($root + '\', [StringComparison]::OrdinalIgnoreCase)) { return }
            $downloads = Join-Path $root '.downloads'
            if ($path.Equals($downloads, [StringComparison]::OrdinalIgnoreCase) -or
                $path.StartsWith($downloads + '\', [StringComparison]::OrdinalIgnoreCase)) { return }

            $directory = Get-Item -LiteralPath $path -Force -ErrorAction Stop
            if (!$directory.PSIsContainer) { throw ('网站目录不存在：' + $path) }
            for ($ancestor = $directory; $ancestor; $ancestor = $ancestor.Parent) {
                if (($ancestor.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw ('拒绝通过重解析点修改网站目录权限：' + $ancestor.FullName)
                }
            }
            $identityType = [string](& $appcmd list apppool "$poolName" /text:processModel.identityType)
            if ($LASTEXITCODE -ne 0) { throw ('无法读取应用程序池身份：' + $poolName) }
            switch ($identityType.Trim()) {
                'LocalSystem' { $sid = 'S-1-5-18' }
                'LocalService' { $sid = 'S-1-5-19' }
                'NetworkService' { $sid = 'S-1-5-20' }
                'ApplicationPoolIdentity' {
                    $account = New-Object System.Security.Principal.NTAccount -ArgumentList ('IIS AppPool\' + $poolName)
                    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
                }
                'SpecificUser' {
                    $name = [string](& $appcmd list apppool "$poolName" /text:processModel.userName)
                    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($name)) { throw ('无法读取应用程序池账户：' + $poolName) }
                    if ($name.StartsWith('.\')) { $name = $env:COMPUTERNAME + '\' + $name.Substring(2) }
                    $account = New-Object System.Security.Principal.NTAccount -ArgumentList $name
                    $sid = $account.Translate([System.Security.Principal.SecurityIdentifier]).Value
                }
                default { throw ('未知的 IIS 应用程序池身份：' + $identityType) }
            }

            $sids = @($sid, 'S-1-5-17') | Select-Object -Unique
            function Set-McPanelIisAcl($item, $sid) {
                if (([IO.File]::GetAttributes($item.FullName) -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                    throw ('拒绝修改重解析点权限：' + $item.FullName)
                }
                $acl = $item.GetAccessControl()
                $identity = New-Object System.Security.Principal.SecurityIdentifier -ArgumentList $sid
                $modify = [Security.AccessControl.FileSystemRights]::Modify
                $inheritance = if ($item.PSIsContainer) {
                    [Security.AccessControl.InheritanceFlags]::ContainerInherit -bor [Security.AccessControl.InheritanceFlags]::ObjectInherit
                } else { [Security.AccessControl.InheritanceFlags]::None }
                $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
                $write = [Security.AccessControl.FileSystemRights]::WriteData -bor [Security.AccessControl.FileSystemRights]::AppendData -bor [Security.AccessControl.FileSystemRights]::Delete
                $denied = @($rules | Where-Object {
                    $_.IdentityReference.Equals($identity) -and
                    $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Deny -and
                    ($_.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and
                    ($_.FileSystemRights -band $write) -ne 0
                }).Count -gt 0
                if ($denied) { throw ('存在拒绝应用池写入的 ACL：' + $item.FullName) }
                $present = @($rules | Where-Object {
                    $_.IdentityReference.Equals($identity) -and
                    $_.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and
                    ($_.FileSystemRights -band $modify) -eq $modify -and
                    ($_.InheritanceFlags -band $inheritance) -eq $inheritance -and
                    $_.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None
                }).Count -gt 0
                if ($present) { return }
                $rights = if ($item.PSIsContainer) { '(OI)(CI)M' } else { 'M' }
                $grant = '*' + $sid + ':' + $rights
                & icacls.exe $item.FullName /grant $grant /L | Out-Host
                if ($LASTEXITCODE -ne 0) { throw ('IIS 网站目录授权失败：' + $item.FullName + ' (icacls ' + $LASTEXITCODE + ')') }
            }
            foreach ($grantSid in $sids) { Set-McPanelIisAcl $directory $grantSid }
            $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
            $pending.Push($directory)
            while ($pending.Count -gt 0) {
                $current = $pending.Pop()
                if (([IO.File]::GetAttributes($current.FullName) -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                foreach ($item in (Get-ChildItem -LiteralPath $current.FullName -Force -ErrorAction Stop)) {
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                    if ($item.GetAccessControl().AreAccessRulesProtected) { foreach ($grantSid in $sids) { Set-McPanelIisAcl $item $grantSid } }
                    if ($item.PSIsContainer) { $pending.Push($item) }
                }
            }
        }
        """;
}
