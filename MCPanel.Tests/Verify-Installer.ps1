param(
    [Parameter(Mandatory = $true)][string]$InstallerFile,
    [Parameter(Mandatory = $true)][string]$PackageFile
)

$ErrorActionPreference = 'Stop'
$InstallerFile = [IO.Path]::GetFullPath($InstallerFile)
$PackageFile = [IO.Path]::GetFullPath($PackageFile)
$root = Join-Path ([IO.Path]::GetTempPath()) ('MCI-' + [Guid]::NewGuid().ToString('N').Substring(0, 10))
[IO.Directory]::CreateDirectory($root) | Out-Null

function Invoke-Setup([string]$Directory, [string]$Package = $PackageFile, [string]$Exe = $InstallerFile, [int]$ExpectedExitCode = 0, [string]$ExtraArguments = '') {
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Exe
    $log = Join-Path $root ([Guid]::NewGuid().ToString('N') + '.log')
    $info.Arguments = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- "/DIR=' + $Directory + '" "/PACKAGEFILE=' + $Package + '" "/LOG=' + $log + '" ' + $ExtraArguments
    $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit(90000)) { $process.Kill(); throw 'Native installer timed out.' }
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }
    if ($ExpectedExitCode -eq 0 -and $exitCode -ne 0) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 50 | Write-Host }
        throw ('Installer failed: ' + $exitCode)
    }
    if ($ExpectedExitCode -ne 0 -and $exitCode -eq 0) {
        if (Test-Path -LiteralPath $log) { Get-Content -LiteralPath $log -Tail 70 | Write-Host }
        throw ('Expected the installer to stop without modifying the application: ' + $Directory)
    }
    return $exitCode
}

function Invoke-RegisteredUninstall([string]$Directory, [bool]$ExpectSuccess = $true) {
    $registration = $null
    foreach ($id in @('{4877D128-D808-4B6B-B4CB-D4D0476F5275}_is1', 'MCPanel.Setup.IntegrationTest_is1')) {
        $key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\' + $id
        $entry = Get-ItemProperty -LiteralPath $key -ErrorAction SilentlyContinue
        if ($entry -and $entry.InstallLocation.TrimEnd('\') -ieq $Directory.TrimEnd('\')) { $registration = $entry; break }
    }
    if (!$registration -or $registration.MCPanelUninstallProtocol -ne 2 -or $registration.UninstallString -notmatch '^"([^"]+)"\s+(.+)$') {
        throw 'Windows Settings is not registered to the complete MCPanel uninstaller.'
    }
    $info = [Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $Matches[1]; $info.Arguments = $Matches[2] + ' -Silent'
    $info.WorkingDirectory = [IO.Path]::GetTempPath(); $info.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($info)
    try {
        if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'Registered uninstall timed out.' }
        if (($process.ExitCode -eq 0) -ne $ExpectSuccess) { throw ('Unexpected complete uninstall exit code: ' + $process.ExitCode) }
    } finally { $process.Dispose() }
    if ($ExpectSuccess -and ((Test-Path -LiteralPath $Directory) -or (Test-Path -LiteralPath $key))) {
        throw 'Complete uninstall left its installation directory or Windows Settings entry.'
    }
}

try {
    $install = Join-Path $root '中文 安装'
    $null = Invoke-Setup $install
    $executable = Join-Path $install 'MCPanel.exe'
    $config = Join-Path $install 'MCPanel.exe.config'
    $uninstall = Join-Path $install 'Maintenance\unins000.exe'
    if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath $uninstall)) { throw 'Fresh native setup did not install both application and uninstaller.' }
    Write-Host 'Passed: native bootstrapper installs the real application into a Unicode path with spaces, without starting the UI.'

    [xml]$document = Get-Content -LiteralPath $config -Raw -Encoding UTF8
    foreach ($setting in $document.configuration.appSettings.add) {
        if ($setting.key -eq 'Ai.Model') { $setting.value = '保留原配置' }
        if ($setting.key -eq 'Ai.ApiKey') { $setting.value = '' }
    }
    $document.Save($config)
    foreach ($name in @('StoreData', 'web', 'MySQL', 'Nginx', 'Diagnostics')) {
        $directory = Join-Path $install $name
        [IO.Directory]::CreateDirectory($directory) | Out-Null
        [IO.File]::WriteAllText((Join-Path $directory 'keep.txt'), $name)
    }
    $library = Join-Path $install 'System.Buffers.dll'
    $libraryBytes = [IO.File]::ReadAllBytes($library)
    $handle = [IO.File]::Open($library, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $null = Invoke-Setup $install } finally { $handle.Dispose() }
    [xml]$document = Get-Content -LiteralPath $config -Raw -Encoding UTF8
    if (($document.configuration.appSettings.add | Where-Object key -eq 'Ai.Model').value -ne '保留原配置') { throw 'Repair lost Unicode configuration settings.' }
    if (($document.configuration.appSettings.add | Where-Object key -eq 'Ai.ApiKey').value -ne '') { throw 'Repair lost explicitly empty settings.' }
    Write-Host 'Passed: repair preserves configuration and skips an unchanged DLL that remains open in another process.'

    $rollback = Join-Path $install 'StoreData\Updates\Rollback\Current'
    [IO.Directory]::CreateDirectory($rollback) | Out-Null
    [IO.File]::Copy($config, (Join-Path $rollback 'MCPanel.exe.config'))
    [IO.File]::Delete($executable)
    [IO.File]::WriteAllText($config, '<configuration />')
    $null = Invoke-Setup $install
    [xml]$document = Get-Content -LiteralPath $config -Raw -Encoding UTF8
    if (($document.configuration.appSettings.add | Where-Object key -eq 'Ai.Model').value -ne '保留原配置') { throw 'Missing-EXE recovery did not prefer the original rollback configuration.' }
    Write-Host 'Passed: installer restores a missing executable and recovers original settings from the rollback snapshot.'

    $beforeConcurrent = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $lockFile = Join-Path $install 'StoreData\Updates\apply-update.lock'
    $handle = [IO.File]::Open($lockFile, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
    try { $null = Invoke-Setup $install -ExpectedExitCode 1 } finally { $handle.Dispose() }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $beforeConcurrent) { throw 'A concurrent updater lock did not protect the installation.' }
    [IO.File]::Delete($lockFile)
    Write-Host 'Passed: native setup honors the existing in-app update transaction lock.'

    $executableHash = (Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash
    $configBytes = [IO.File]::ReadAllBytes($config)
    [IO.File]::WriteAllText($library, 'different locked library')
    $handle = [IO.File]::Open($library, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { $null = Invoke-Setup $install -ExpectedExitCode 1 } finally { $handle.Dispose() }
    if ((Get-FileHash -LiteralPath $executable -Algorithm SHA256).Hash -ne $executableHash -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes($config)) -ne [Convert]::ToBase64String($configBytes)) { throw 'Locked changed DLL modified the existing executable or configuration.' }
    [IO.File]::WriteAllBytes($library, $libraryBytes)
    Write-Host 'Passed: a changed locked DLL stops setup before any application files are modified.'

    $corrupt = Join-Path $root 'corrupt.zip'
    [IO.File]::WriteAllText($corrupt, 'damaged package')
    $null = Invoke-Setup (Join-Path $root 'bad-package') -Package $corrupt -ExpectedExitCode 1
    if (Test-Path -LiteralPath (Join-Path $root 'bad-package\MCPanel.exe')) { throw 'Corrupt package was installed.' }
    $null = Invoke-Setup ([IO.Path]::GetPathRoot($root)) -ExpectedExitCode 1
    Write-Host 'Passed: invalid package hashes and drive-root targets are rejected.'

    & (Join-Path $PSScriptRoot 'Verify-InstallerUI.ps1') -UninstallRoot $install
    # Upgrade the legacy ARP entry through the exact headless startup migration.
    $registrationKey = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{4877D128-D808-4B6B-B4CB-D4D0476F5275}_is1'
    Set-ItemProperty -LiteralPath $registrationKey -Name UninstallString -Value ('"' + $uninstall + '"')
    $migration = Start-Process -FilePath $executable -ArgumentList '--register-uninstaller' -PassThru -Wait
    if ($migration.ExitCode -ne 0 -or (Get-ItemProperty -LiteralPath $registrationKey).UninstallString -notlike '*Uninstall-MCPanel.ps1*') { throw 'Legacy online update did not migrate the uninstall entry.' }
    $migration.Dispose()
    Write-Host 'Passed: existing installations migrate their old native uninstall entry after an online update.'

    $outside = Join-Path $root 'outside'
    [IO.Directory]::CreateDirectory($outside) | Out-Null
    [IO.File]::WriteAllText((Join-Path $outside 'keep.txt'), 'outside data')
    $link = Join-Path $install 'web\external-link'
    & cmd.exe /d /c ('mklink /J "' + $link + '" "' + $outside + '"') | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Unable to create outside-directory junction fixture.' }
    $readOnly = Join-Path $install 'Nginx\keep.txt'
    [IO.File]::SetAttributes($readOnly, [IO.FileAttributes]::ReadOnly)
    $handle = [IO.File]::Open((Join-Path $install 'StoreData\keep.txt'), [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try { Invoke-RegisteredUninstall $install $false } finally { $handle.Dispose() }
    if (!(Test-Path -LiteralPath $executable) -or !(Test-Path -LiteralPath (Join-Path $install 'Uninstall-MCPanel.ps1')) -or !(Test-Path -LiteralPath $registrationKey)) { throw 'A blocked uninstall removed the application or retry entry.' }
    Write-Host 'Passed: a locked data file blocks uninstall before removing the application, and preserves a usable retry entry.'
    Invoke-RegisteredUninstall $install
    if ([IO.File]::ReadAllText((Join-Path $outside 'keep.txt')) -ne 'outside data') { throw 'Complete uninstall followed a junction outside the installation.' }
    Write-Host 'Passed: Windows Settings uninstall removes the complete installation directory, readonly data and registration, while preserving junction targets outside it.'

    & (Join-Path $PSScriptRoot 'Verify-InstallerPrerequisites.ps1') -PackageFile $PackageFile -CaseRoot $root
    & (Join-Path $PSScriptRoot 'Verify-InstallerUI.ps1') -InstallerFile $InstallerFile -InstallDirectory (Join-Path $root 'preview')
} finally {
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -Directory -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object { [IO.Directory]::Delete($_.FullName, $false) }
        Get-ChildItem -LiteralPath $root -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
