$ErrorActionPreference = 'Stop'

function Get-MCPanelSqlCertificates {
    # Do not depend on Cert: or an inherited PowerShell module search path.
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadOnly)
        return $store.Certificates
    }
    finally { $store.Close() }
}

function New-MCPanelSqlCertificate {
    # certreq's self-signed RequestType=Cert is documented for Server 2012.
    # The older New-SelfSignedCertificate lacks the provider/key-spec options
    # needed by SQL Server, even when Windows PowerShell has been upgraded.
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('mcpanel-sql-cert-' + [Guid]::NewGuid().ToString('N'))
    $infPath = $temporary + '.inf'
    $cerPath = $temporary + '.cer'
    $san = 'DNS=localhost'
    if ($env:COMPUTERNAME -match '^[a-zA-Z0-9][a-zA-Z0-9.-]*$') { $san += '&DNS=' + $env:COMPUTERNAME }
    $inf = @'
[Version]
Signature="$Windows NT$"
[NewRequest]
Subject="CN=localhost"
FriendlyName="MCPanel SQL Server Local Certificate"
RequestType=Cert
MachineKeySet=TRUE
Exportable=FALSE
KeyLength=2048
KeySpec=1
KeyUsage=0xa0
ProviderName="Microsoft RSA SChannel Cryptographic Provider"
ProviderType=12
HashAlgorithm=sha256
ValidityPeriod=Years
ValidityPeriodUnits=5
[Extensions]
2.5.29.17="{text}__SAN__"
2.5.29.37="{text}1.3.6.1.5.5.7.3.1"
'@
    try {
        [IO.File]::WriteAllText($infPath, $inf.Replace('__SAN__', $san), [Text.Encoding]::Unicode)
        & (Join-Path $env:WINDIR 'System32\certreq.exe') -new -machine -q $infPath $cerPath | Out-Host
        if ($LASTEXITCODE -ne 0) { throw ('创建 SQL Server 本机证书失败，certreq 退出码：' + $LASTEXITCODE) }
        $public = New-Object Security.Cryptography.X509Certificates.X509Certificate2($cerPath)
        try { $thumbprint = $public.Thumbprint } finally { $public.Reset() }
        $certificate = Get-MCPanelSqlCertificates | Where-Object { $_.Thumbprint -eq $thumbprint } | Select-Object -First 1
        if (-not $certificate -or -not $certificate.HasPrivateKey -or -not $certificate.PrivateKey -or
            $certificate.PrivateKey.CspKeyContainerInfo.KeyNumber -ne 'Exchange') {
            throw '本机证书没有 SQL Server 所需的 RSA KeyExchange 私钥。'
        }
        return $certificate
    }
    finally {
        Remove-Item -LiteralPath $infPath, $cerPath -Force -ErrorAction SilentlyContinue
    }
}

function Get-MCPanelSqlServiceSid {
    $account = (Get-ItemProperty -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\MSSQLSERVER' -ErrorAction Stop).ObjectName
    switch -Regex ($account) {
        '^(LocalSystem|NT AUTHORITY\\SYSTEM)$' { return New-Object Security.Principal.SecurityIdentifier('S-1-5-18') }
        '^NT AUTHORITY\\(NETWORK SERVICE|NetworkService)$' { return New-Object Security.Principal.SecurityIdentifier('S-1-5-20') }
        '^NT AUTHORITY\\(LOCAL SERVICE|LocalService)$' { return New-Object Security.Principal.SecurityIdentifier('S-1-5-19') }
    }
    if ([string]::IsNullOrWhiteSpace($account)) { throw '无法确认 SQL Server 服务登录账户，未修改证书绑定。' }
    if ($account.StartsWith('.\')) { $account = $env:COMPUTERNAME + $account.Substring(1) }
    $identity = New-Object Security.Principal.NTAccount($account)
    return $identity.Translate([Security.Principal.SecurityIdentifier])
}

function Set-MCPanelSqlCertificateBinding($registryPath, $thumbprint) {
    $settings = Get-ItemProperty -LiteralPath $registryPath -ErrorAction Stop
    $previous = $settings.PSObject.Properties['Certificate']
    $wasRunning = (Get-Service -Name MSSQLSERVER -ErrorAction Stop).Status -eq 'Running'
    try {
        Set-ItemProperty -LiteralPath $registryPath -Name Certificate -Value $thumbprint -ErrorAction Stop
        # Preserve ForceEncryption and the administrator's chosen service state.
        if ($wasRunning) {
            Restart-Service -Name MSSQLSERVER -Force -ErrorAction Stop
            (Get-Service -Name MSSQLSERVER -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
        }
    }
    catch {
        $failure = $_.Exception.Message
        try {
            if ($null -ne $previous) {
                Set-ItemProperty -LiteralPath $registryPath -Name Certificate -Value $previous.Value -ErrorAction Stop
            }
            else { Remove-ItemProperty -LiteralPath $registryPath -Name Certificate -ErrorAction SilentlyContinue }
            if ($wasRunning) {
                Restart-Service -Name MSSQLSERVER -Force -ErrorAction Stop
                (Get-Service -Name MSSQLSERVER -ErrorAction Stop).WaitForStatus('Running', [TimeSpan]::FromSeconds(90))
            }
        }
        catch { throw ('证书切换失败且恢复原配置未完成：' + $failure + '；恢复错误：' + $_.Exception.Message) }
        throw ('证书切换失败，已恢复原证书配置：' + $failure)
    }
}

Start-Transcript -Path $log -Force | Out-Null
try {
    $instance = (Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL' -ErrorAction Stop).MSSQLSERVER
    if ([string]::IsNullOrWhiteSpace($instance)) { throw '未找到 MSSQLSERVER 默认实例。' }
    $reg = "HKLM:\SOFTWARE\Microsoft\Microsoft SQL Server\$instance\MSSQLServer\SuperSocketNetLib"
    $current = ((Get-ItemProperty -LiteralPath $reg -ErrorAction Stop).Certificate + '').Trim()
    if ($current) {
        $configured = Get-MCPanelSqlCertificates | Where-Object { $_.Thumbprint -eq $current } | Select-Object -First 1
        if (-not $configured -or $configured.FriendlyName -ne 'MCPanel SQL Server Local Certificate') {
            Write-Output 'SQL Server 已配置管理员指定的证书，保持原配置。'
            exit 0
        }
    }
    $sid = Get-MCPanelSqlServiceSid
    $cert = Get-MCPanelSqlCertificates | Where-Object {
        $_.Subject -eq 'CN=localhost' -and $_.FriendlyName -eq 'MCPanel SQL Server Local Certificate' -and
        $_.NotBefore -le (Get-Date) -and $_.NotAfter -gt (Get-Date).AddDays(30) -and
        $_.HasPrivateKey -and $_.PrivateKey -and $_.PrivateKey.CspKeyContainerInfo.KeyNumber -eq 'Exchange'
    } | Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $cert) { $cert = New-MCPanelSqlCertificate }
    $keyName = $cert.PrivateKey.CspKeyContainerInfo.UniqueKeyContainerName
    $keyPath = Join-Path $env:ProgramData ('Microsoft\Crypto\RSA\MachineKeys\' + $keyName)
    $acl = [IO.File]::GetAccessControl($keyPath)
    $rule = New-Object Security.AccessControl.FileSystemAccessRule($sid, 'Read', 'Allow')
    $acl.AddAccessRule($rule)
    [IO.File]::SetAccessControl($keyPath, $acl)
    # Import only the public certificate. No temporary private-key export.
    $store = New-Object Security.Cryptography.X509Certificates.X509Store('Root', 'LocalMachine')
    try {
        $store.Open([Security.Cryptography.X509Certificates.OpenFlags]::ReadWrite)
        $public = New-Object -TypeName Security.Cryptography.X509Certificates.X509Certificate2 -ArgumentList (,$cert.RawData)
        try { $store.Add($public) } finally { $public.Reset() }
    }
    finally { $store.Close() }
    $target = $cert.Thumbprint.ToLowerInvariant()
    if ($current -ne $target) { Set-MCPanelSqlCertificateBinding $reg $target }
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
finally { try { Stop-Transcript | Out-Null } catch { } }
