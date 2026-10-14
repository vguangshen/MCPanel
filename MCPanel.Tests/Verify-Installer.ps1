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

    # Ownership is untrusted local metadata; it must never turn uninstall into
    # deletion of settings, operational data or files outside the installation.
    [IO.File]::AppendAllText((Join-Path $install 'Maintenance\program-files.lst'), "StoreData\keep.txt`r`nStoreData.\keep.txt`r`nStoreData \keep.txt`r`n..\outside.txt`r`nMCPanel.exe.config`r`nMCPanel.exe.config.`r`n")
    [IO.File]::WriteAllText((Join-Path $root 'outside.txt'), 'keep outside installation')
    $null = Invoke-Setup $install -Exe $uninstall
    if (Test-Path -LiteralPath $executable) { throw 'Uninstaller left the main program installed.' }
    if (!(Test-Path -LiteralPath $config) -or !(Test-Path -LiteralPath (Join-Path $root 'outside.txt'))) { throw 'Uninstaller removed configuration or outside data.' }
    foreach ($name in @('StoreData', 'web', 'MySQL', 'Nginx', 'Diagnostics')) {
        if ([IO.File]::ReadAllText((Join-Path $install ($name + '\keep.txt'))) -ne $name) { throw ('Uninstaller removed operational data: ' + $name) }
    }
    Write-Host 'Passed: uninstall removes only owned program files and preserves configuration, business data and outside files.'

    & (Join-Path $PSScriptRoot 'Verify-InstallerPrerequisites.ps1') -PackageFile $PackageFile -CaseRoot $root
    & (Join-Path $PSScriptRoot 'Verify-InstallerUI.ps1') -InstallerFile $InstallerFile -InstallDirectory (Join-Path $root 'preview')
} finally {
    if (Test-Path -LiteralPath $root) {
        Get-ChildItem -LiteralPath $root -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}
