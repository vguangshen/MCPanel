[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)][string]$InstallDirectory,
    [string]$PackageFile
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/')
if ($installRoot -eq [IO.Path]::GetPathRoot($installRoot).TrimEnd('\', '/') -or
    !(Test-Path -LiteralPath (Join-Path $installRoot 'StoreData') -PathType Container)) {
    throw 'Select the existing MCPanel installation containing StoreData, not a drive root.'
}
foreach ($process in [Diagnostics.Process]::GetProcessesByName('MCPanel')) {
    try {
        if ($process.SessionId -eq 0) { continue }
        if ([string]::Equals($process.MainModule.FileName, (Join-Path $installRoot 'MCPanel.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close MCPanel completely from its tray menu before running this recovery.'
        }
    } finally { $process.Dispose() }
}

$temporary = Join-Path ([IO.Path]::GetTempPath()) ('MCPanel-Recovery-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$transactionLock = $null
try {
    if ([string]::IsNullOrWhiteSpace($PackageFile)) {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $PackageFile = Join-Path $temporary 'MCPanel-1.3.61.zip'
        $release = 'https://github.com/vguangshen/MCPanel/releases/download/v1.3.61/'
        Invoke-WebRequest -Uri ($release + 'MCPanel-1.3.61.zip.sha256') -OutFile ($PackageFile + '.sha256') -UseBasicParsing
        Invoke-WebRequest -Uri ($release + 'MCPanel-1.3.61.zip') -OutFile $PackageFile -UseBasicParsing
    }
    $PackageFile = [IO.Path]::GetFullPath($PackageFile)
    $sidecar = $PackageFile + '.sha256'
    if (!(Test-Path -LiteralPath $sidecar -PathType Leaf)) { throw 'The package needs its matching .zip.sha256 file.' }
    $expected = ((Get-Content -LiteralPath $sidecar -Raw).Trim() -split '\s+')[0]
    if ($expected -notmatch '^[a-fA-F0-9]{64}$' -or (Get-FileHash -LiteralPath $PackageFile -Algorithm SHA256).Hash -ine $expected) {
        throw 'Package SHA-256 verification failed. No installed files were changed.'
    }
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $payload = Join-Path $temporary 'Payload'
    [IO.Compression.ZipFile]::ExtractToDirectory($PackageFile, $payload)
    foreach ($name in @('MCPanel.exe', 'MCPanel.exe.config', 'deployment-layout.json')) {
        if (!(Test-Path -LiteralPath (Join-Path $payload $name) -PathType Leaf)) { throw ('Incomplete package: ' + $name) }
    }
    $layout = Get-Content -LiteralPath (Join-Path $payload 'deployment-layout.json') -Raw | ConvertFrom-Json
    $preserved = @('StoreData', 'AccountApi', 'Runtime', 'Downloads', 'Diagnostics', 'Tools', 'web', 'Cache', 'Frp',
        'Nginx', 'MySQL', 'MSSQL', 'Tomcat', 'SSMS', 'Navicat Premium Lite', 'config.ini', 'config.ini.previous',
        'device.identity', 'database.config', 'database.config.previous', 'logs') + @($layout.preservedTopLevelNames)

    if (!$WhatIfPreference) {
        $updates = Join-Path $installRoot 'StoreData\Updates'
        [IO.Directory]::CreateDirectory($updates) | Out-Null
        $transactionLock = [IO.FileStream]::new((Join-Path $updates 'apply-update.lock'),
            [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None, 1, [IO.FileOptions]::DeleteOnClose)
    }
    # A missing EXE can coexist with a partially copied default configuration.
    # In that case the current rollback snapshot is the original source of truth.
    $rollback = Join-Path $installRoot 'StoreData\Updates\Rollback'
    $liveConfiguration = Join-Path $installRoot 'MCPanel.exe.config'
    $rollbackConfiguration = Join-Path $rollback 'Current\MCPanel.exe.config'
    $candidates = if (Test-Path -LiteralPath (Join-Path $installRoot 'MCPanel.exe') -PathType Leaf) {
        @($liveConfiguration, $rollbackConfiguration)
    } else { @($rollbackConfiguration, $liveConfiguration) }
    if (Test-Path -LiteralPath $rollback) {
        $candidates += @(Get-ChildItem -LiteralPath $rollback -Directory -Filter 'Current.failed-*' |
            Sort-Object Name -Descending | ForEach-Object { Join-Path $_.FullName 'MCPanel.exe.config' })
    }
    $configurationSource = $candidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
    if (!$configurationSource) { throw 'Original MCPanel.exe.config not found. Preserve StoreData and recover the original configuration before continuing.' }
    [xml]$oldConfiguration = Get-Content -LiteralPath $configurationSource -Raw -Encoding UTF8
    [xml]$newConfiguration = Get-Content -LiteralPath (Join-Path $payload 'MCPanel.exe.config') -Raw -Encoding UTF8
    foreach ($setting in $oldConfiguration.SelectNodes('/configuration/appSettings/add')) {
        $key = $setting.GetAttribute('key')
        if (@($layout.preservedAppSettingKeys) -notcontains $key) { continue }
        $appSettings = $newConfiguration.SelectSingleNode('/configuration/appSettings')
        if ($null -eq $appSettings) {
            $appSettings = $newConfiguration.CreateElement('appSettings')
            $newConfiguration.DocumentElement.AppendChild($appSettings) | Out-Null
        }
        $target = @($appSettings.SelectNodes('add')) | Where-Object { $_.GetAttribute('key') -ieq $key } | Select-Object -First 1
        if ($null -eq $target) {
            $target = $newConfiguration.CreateElement('add')
            $target.SetAttribute('key', $key)
            $appSettings.AppendChild($target) | Out-Null
        }
        $target.SetAttribute('value', $setting.GetAttribute('value'))
    }
    $newConfiguration.Save((Join-Path $payload 'MCPanel.exe.config'))

    $changes = @()
    foreach ($file in Get-ChildItem -LiteralPath $payload -File -Recurse) {
        $relative = $file.FullName.Substring($payload.Length).TrimStart('\', '/')
        if ($preserved -contains ($relative -split '[\\/]')[0]) { continue }
        $destination = Join-Path $installRoot $relative
        $ancestor = $destination
        while ($ancestor.Length -ge $installRoot.Length) {
            if ((Test-Path -LiteralPath $ancestor) -and
                ((Get-Item -LiteralPath $ancestor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw ('Recovery cannot replace a path through a junction or symbolic link: ' + $ancestor)
            }
            $ancestor = [IO.Path]::GetDirectoryName($ancestor)
        }
        $exists = Test-Path -LiteralPath $destination -PathType Leaf
        if ($exists -and (Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash -eq (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash) { continue }
        if ($exists) {
            try {
                $probe = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
                $probe.Dispose()
            } catch { throw ('Program file is in use; no installed files were changed: ' + $destination) }
        }
        $changes += [pscustomobject]@{ Relative = $relative; Source = $file.FullName; Destination = $destination; Existed = $exists }
    }
    if (!$PSCmdlet.ShouldProcess($installRoot, ('Restore ' + $changes.Count + ' program files; preserve original settings and operational data'))) { return }

    $recoveryRoot = Join-Path $installRoot ('StoreData\Updates\Recovery\' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
    $backup = Join-Path $recoveryRoot 'Original'
    $incoming = Join-Path $recoveryRoot 'Incoming'
    [IO.Directory]::CreateDirectory($backup) | Out-Null
    [IO.File]::Copy($configurationSource, (Join-Path $recoveryRoot 'MCPanel.exe.config.original'))
    foreach ($change in $changes) {
        $ready = Join-Path $incoming $change.Relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($ready)) | Out-Null
        [IO.File]::Copy($change.Source, $ready)
        if ($change.Existed) {
            $saved = Join-Path $backup $change.Relative
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($saved)) | Out-Null
            [IO.File]::Copy($change.Destination, $saved)
        }
    }
    $completed = @()
    $active = $null
    try {
        # Keep the old executable until its atomic replacement, or create the
        # missing executable only after every dependency/configuration is ready.
        foreach ($change in $changes | Sort-Object @{ Expression = { $_.Relative -ieq 'MCPanel.exe' } }, Relative) {
            $active = $change
            [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($change.Destination)) | Out-Null
            $ready = Join-Path $incoming $change.Relative
            if ($change.Existed) {
                [IO.File]::SetAttributes($change.Destination, ([IO.File]::GetAttributes($change.Destination) -band (-bnot [IO.FileAttributes]::ReadOnly)))
                [IO.File]::Replace($ready, $change.Destination, $null)
            } else { [IO.File]::Move($ready, $change.Destination) }
            $completed += $change
            $active = $null
        }
    } catch {
        $failure = $_
        if ($null -ne $active -and $active.Existed) {
            try { [IO.File]::SetAttributes($active.Destination, [IO.File]::GetAttributes((Join-Path $backup $active.Relative))) } catch { Write-Warning $_ }
        }
        [array]::Reverse($completed)
        foreach ($change in $completed) {
            try {
                if ($change.Existed) {
                    $saved = Join-Path $backup $change.Relative
                    $restore = Join-Path $incoming ($change.Relative + '.restore')
                    [IO.File]::Copy($saved, $restore, $true)
                    [IO.File]::SetAttributes($restore, [IO.FileAttributes]::Normal)
                    [IO.File]::SetAttributes($change.Destination, [IO.FileAttributes]::Normal)
                    [IO.File]::Replace($restore, $change.Destination, $null)
                    [IO.File]::SetAttributes($change.Destination, [IO.File]::GetAttributes($saved))
                } else { [IO.File]::Delete($change.Destination) }
            } catch { Write-Warning $_ }
        }
        throw ('Recovery failed. Keep the original files at ' + $recoveryRoot + '. ' + $failure.Exception.Message)
    }
    Write-Output ('Configuration preserved from: ' + $configurationSource)
    Write-Output ('Original files retained at: ' + $recoveryRoot)
    Write-Output 'Recovery complete. Start MCPanel.exe directly; no old online updater is needed.'
} finally {
    if ($null -ne $transactionLock) { $transactionLock.Dispose() }
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -Recurse -Force }
}
