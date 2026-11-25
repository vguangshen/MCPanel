namespace MCPanel;

/// <summary>Shared Windows ACL repair for SQL databases restored under the managed web root.</summary>
internal static class SqlServerWebPermissionsScript
{
    internal const string Functions = """
        function Grant-McPanelSqlWebAccess($webRoot) {
            $service = Get-WmiObject -Class Win32_Service -Filter "Name='MSSQLSERVER'" -ErrorAction Stop
            if (!$service -or [string]::IsNullOrWhiteSpace($service.StartName)) {
                throw '无法识别 MSSQLSERVER 服务账户，未配置网站目录权限。'
            }
            $account = [string]$service.StartName
            switch -Regex ($account) {
                '^(LocalSystem|NT AUTHORITY\\SYSTEM)$' { $sid = 'S-1-5-18'; break }
                '^(NetworkService|NT AUTHORITY\\NETWORK SERVICE)$' { $sid = 'S-1-5-20'; break }
                '^(LocalService|NT AUTHORITY\\LOCAL SERVICE)$' { $sid = 'S-1-5-19'; break }
                default {
                    if ($account.StartsWith('.\')) {
                        $account = $env:COMPUTERNAME + '\' + $account.Substring(2)
                    }
                    $identity = New-Object System.Security.Principal.NTAccount -ArgumentList $account
                    $sid = $identity.Translate([System.Security.Principal.SecurityIdentifier]).Value
                }
            }

            [IO.Directory]::CreateDirectory($webRoot) | Out-Null
            $root = Get-Item -LiteralPath $webRoot -Force -ErrorAction Stop
            if (($root.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw ('网站根目录是重解析点，拒绝自动修改权限：' + $webRoot)
            }

            function Set-McPanelSqlWebAcl($item) {
                $rights = if ($item.PSIsContainer) { '(OI)(CI)M' } else { 'M' }
                $grant = '*' + $sid + ':' + $rights
                & icacls.exe $item.FullName /grant $grant /L | Out-Host
                if ($LASTEXITCODE -ne 0) {
                    throw ('SQL Server 网站目录授权失败：' + $item.FullName + ' (icacls ' + $LASTEXITCODE + ')')
                }
            }

            Write-Output ('配置 SQL Server 网站目录权限：' + $webRoot + '；服务账户：' + $account)
            Set-McPanelSqlWebAcl $root

            # Normal children inherit the root grant. Repair existing protected ACLs
            # without resetting their other entries or walking junctions/symlinks.
            $pending = New-Object 'System.Collections.Generic.Stack[System.IO.DirectoryInfo]'
            $pending.Push($root)
            while ($pending.Count -gt 0) {
                $directory = $pending.Pop()
                foreach ($item in (Get-ChildItem -LiteralPath $directory.FullName -Force -ErrorAction Stop)) {
                    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                        continue
                    }
                    if ($item.GetAccessControl().AreAccessRulesProtected) {
                        Set-McPanelSqlWebAcl $item
                    }
                    if ($item.PSIsContainer) { $pending.Push($item) }
                }
            }
            Write-Output 'SQL Server 网站目录权限配置完成。'
        }
        """;
}
