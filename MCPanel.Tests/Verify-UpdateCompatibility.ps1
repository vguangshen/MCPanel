param([string]$CurrentBuild = (Join-Path $PSScriptRoot '..\bin\Release\net462'))

$ErrorActionPreference = 'Stop'
$CurrentBuild = [IO.Path]::GetFullPath($CurrentBuild)
$version = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $CurrentBuild 'MCPanel.exe')).FileVersion
$root = Join-Path ([IO.Path]::GetTempPath()) ('MCPanel-UpdateCompatibility-' + [Guid]::NewGuid().ToString('N'))
$helperRoots = @()
[IO.Directory]::CreateDirectory($root) | Out-Null

function Write-Text([string]$Path, [string]$Text) {
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, $Text)
}

function New-Case([string]$Name) {
    $install = Join-Path $root $Name
    $staging = Join-Path $install ('StoreData\Updates\Staging\' + [Guid]::NewGuid().ToString('N'))
    $payload = Join-Path $staging 'Payload'
    Write-Text (Join-Path $install 'MCPanel.exe') 'old'
    Write-Text (Join-Path $install 'MCPanel.exe.config') '<configuration><appSettings><add key="Ai.Model" value="custom-model" /><add key="Ai.ApiKey" value="" /><add key="Ai.Endpoint" value="https://example.com/中文" /></appSettings></configuration>'
    # Deliberately invalid executable bytes prevent any UI/service startup after
    # the real updater finishes its file transaction; restart failure is benign.
    Write-Text (Join-Path $payload 'MCPanel.exe') 'new'
    Write-Text (Join-Path $payload 'MCPanel.exe.config') '<configuration />'
    $relative = 'Diagnostics\iis-20261010-060645\IIS-config.xml'
    $diagnostics = Join-Path $install $relative
    $rollback = Join-Path $install ('StoreData\Updates\Rollback\Current\' + $relative)
    foreach ($file in @($diagnostics, $rollback)) {
        Write-Text $file 'IIS diagnostics'
        [IO.File]::SetAttributes($file, [IO.FileAttributes]::ReadOnly)
    }
    return [pscustomobject]@{ Install = $install; Staging = $staging; Payload = $payload; Diagnostics = $diagnostics }
}

function Invoke-Updater([string]$Runtime, $Case, [string]$OldLayout) {
    $helper = Join-Path $env:LOCALAPPDATA ('MCPanel\Updater\' + [Guid]::NewGuid().ToString('N'))
    $script:helperRoots += $helper
    [IO.Directory]::CreateDirectory($helper) | Out-Null
    Copy-Item -LiteralPath (Join-Path $Runtime 'MCPanel.exe') -Destination $helper
    Get-ChildItem -LiteralPath $Runtime -File | Where-Object { $_.Name -ne 'MCPanel.exe' } |
        Copy-Item -Destination $helper
    if (Test-Path -LiteralPath (Join-Path $Runtime 'runtimes')) {
        Copy-Item -LiteralPath (Join-Path $Runtime 'runtimes') -Destination $helper -Recurse
    }
    # Even the fixed helper must work with the layout shipped in 1.3.58.
    Copy-Item -LiteralPath $OldLayout -Destination (Join-Path $helper 'deployment-layout.json') -Force
    $planFile = Join-Path $helper 'update-plan.json'
    $plan = @{
        InstallDirectory = $Case.Install; PayloadDirectory = $Case.Payload
        StagingRoot = $Case.Staging; TargetVersion = $version; MainExecutableName = 'MCPanel.exe'
        ParentProcessId = [int]::MaxValue; DeletePackageFileAfterApply = $false; PackageFile = ''
    }
    Write-Text $planFile ($plan | ConvertTo-Json)
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = Join-Path $helper 'MCPanel.exe'
    $start.WorkingDirectory = $helper
    $start.Arguments = '--apply-update "' + $planFile + '"'
    $start.UseShellExecute = $false
    $process = [Diagnostics.Process]::Start($start)
    try {
        if (!$process.WaitForExit(60000)) { $process.Kill(); throw 'Updater did not exit within 60 seconds.' }
        $exitCode = $process.ExitCode
    } finally { $process.Dispose() }
    $result = Get-Content -LiteralPath (Join-Path $Case.Install 'StoreData\Updates\last-update.json') -Raw | ConvertFrom-Json
    return [pscustomobject]@{ ExitCode = $exitCode; Result = $result }
}

function Invoke-Recovery($Case, [string]$Package, [switch]$Preview) {
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $PSScriptRoot '..\Restore-Update.ps1'),
        '-InstallDirectory', $Case.Install, '-PackageFile', $Package)
    if ($Preview) { $arguments += '-WhatIf' }
    # Match the user's Windows PowerShell 5.1, including its XML/encoding behavior.
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw ('Windows PowerShell recovery failed with exit code ' + $LASTEXITCODE) }
}

try {
    $zip = Join-Path $root 'MCPanel-1.3.58.zip'
    Invoke-WebRequest -Uri 'https://github.com/vguangshen/MCPanel/releases/download/v1.3.58/MCPanel-1.3.58.zip' -OutFile $zip
    if ((Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash -ine '614d7d1b5fb4d804c3253b6536750f4710a722c5a103f4806a7f091db4219b05') {
        throw 'The pinned 1.3.58 release package digest did not match.'
    }
    $legacy = Join-Path $root 'legacy-runtime'
    Expand-Archive -LiteralPath $zip -DestinationPath $legacy
    $oldVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo((Join-Path $legacy 'MCPanel.exe')).FileVersion
    if ($oldVersion -ne '1.3.58.0') { throw 'The legacy test must execute the real 1.3.58 updater.' }
    $layout = Join-Path $legacy 'deployment-layout.json'

    $failedCase = New-Case 'legacy-failure'
    $old = Invoke-Updater $legacy $failedCase $layout
    if ($old.Result.Success -or $old.ExitCode -eq 0 -or $old.Result.Message -notmatch 'UnauthorizedAccessException') {
        throw ('Expected the 1.3.58 read-only backup collision: ' + $old.Result.Message)
    }
    if ([IO.File]::ReadAllText((Join-Path $failedCase.Install 'MCPanel.exe')) -ne 'old') { throw 'Failed legacy backup touched the original program.' }
    Write-Host 'Reproduced: real 1.3.58 updater fails with UnauthorizedAccessException on the read-only Diagnostics rollback.'

    $fixedCase = New-Case 'fixed-updater'
    $fixed = Invoke-Updater $CurrentBuild $fixedCase $layout
    if (!$fixed.Result.Success -or $fixed.ExitCode -ne 0) { throw ('Fixed updater failed: ' + $fixed.Result.Message) }
    if ([IO.File]::ReadAllText((Join-Path $fixedCase.Install 'MCPanel.exe')) -ne 'new') { throw 'Fixed updater did not replace the program.' }
    if ([IO.File]::ReadAllText($fixedCase.Diagnostics) -ne 'IIS diagnostics') { throw 'Fixed updater lost diagnostics.' }
    if (([IO.File]::GetAttributes($fixedCase.Diagnostics) -band [IO.FileAttributes]::ReadOnly) -eq 0) { throw 'Fixed updater modified diagnostic attributes.' }
    if (Test-Path -LiteralPath (Join-Path $fixedCase.Install 'StoreData\Updates\Rollback\Current')) { throw 'Fixed updater retained its obsolete snapshot.' }
    Write-Host ('Passed: fixed ' + $version + ' updater handles the same fixture with the old layout manifest.')

    $repairCase = New-Case 'repaired-legacy-updater'
    & (Join-Path $PSScriptRoot '..\Repair-Update.ps1') -InstallDirectory $repairCase.Install
    $repaired = Invoke-Updater $legacy $repairCase $layout
    if (!$repaired.Result.Success -or $repaired.ExitCode -ne 0) { throw ('Repaired legacy updater failed: ' + $repaired.Result.Message) }
    $preserved = @(Get-ChildItem -LiteralPath (Join-Path $repairCase.Install 'StoreData') -Filter 'IIS-config.xml' -File -Recurse)
    if ($preserved.Count -ne 2) { throw 'Repair did not retain both original diagnostic copies.' }
    foreach ($file in $preserved) {
        if ([IO.File]::ReadAllText($file.FullName) -ne 'IIS diagnostics') { throw 'Repair changed diagnostic contents.' }
    }
    Write-Host 'Passed: the one-time repair lets the real 1.3.58 updater finish without deleting diagnostic files.'

    $lockedCase = New-Case 'legacy-readable-locked-library'
    & (Join-Path $PSScriptRoot '..\Repair-Update.ps1') -InstallDirectory $lockedCase.Install
    $lockedLibrary = Join-Path $lockedCase.Install 'System.Buffers.dll'
    Write-Text $lockedLibrary 'shared runtime'
    Write-Text (Join-Path $lockedCase.Payload 'System.Buffers.dll') 'shared runtime'
    Write-Text (Join-Path $lockedCase.Install 'StoreData\user-data.txt') 'keep database and website settings'
    $libraryHandle = [IO.File]::Open($lockedLibrary, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $lockedFailure = Invoke-Updater $legacy $lockedCase $layout
        if ($lockedFailure.Result.Success -or $lockedFailure.ExitCode -eq 0) { throw 'Expected the real legacy updater to fail on a readable but deletion-locked DLL.' }
        if (Test-Path -LiteralPath (Join-Path $lockedCase.Install 'MCPanel.exe')) { throw 'The missing-EXE regression was not reproduced.' }
        if (!(Test-Path -LiteralPath (Join-Path $lockedCase.Install 'StoreData\Updates\Rollback\Current\MCPanel.exe.config'))) { throw 'Legacy rollback did not retain the original configuration.' }
        Write-Host 'Reproduced: real 1.3.58 deletes MCPanel.exe, then both replacement and rollback fail on a readable but deletion-locked DLL.'
        Write-Text (Join-Path $lockedCase.Install 'MCPanel.exe.config') '<configuration><appSettings><add key="Ai.Model" value="partially-copied-default" /></appSettings></configuration>'

        $recoveryPackage = Join-Path $root 'recovery.zip'
        Compress-Archive -Path (Join-Path $lockedCase.Payload '*') -DestinationPath $recoveryPackage
        # Include the actual shipped layout in the otherwise inert test payload.
        $recoveryPayload = Join-Path $root 'recovery-payload'
        Expand-Archive -LiteralPath $recoveryPackage -DestinationPath $recoveryPayload
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot '..\deployment-layout.json') -Destination $recoveryPayload
        Compress-Archive -Path (Join-Path $recoveryPayload '*') -DestinationPath $recoveryPackage -Force
        Write-Text ($recoveryPackage + '.sha256') ((Get-FileHash -LiteralPath $recoveryPackage -Algorithm SHA256).Hash + '  recovery.zip')
        Invoke-Recovery $lockedCase $recoveryPackage -Preview
        if (Test-Path -LiteralPath (Join-Path $lockedCase.Install 'MCPanel.exe')) { throw 'Recovery preview changed installed files.' }
        Invoke-Recovery $lockedCase $recoveryPackage
        Invoke-Recovery $lockedCase $recoveryPackage
        if ([IO.File]::ReadAllText((Join-Path $lockedCase.Install 'MCPanel.exe')) -ne 'new') { throw 'Recovery did not restore the missing executable.' }
        [xml]$recoveredConfig = Get-Content -LiteralPath (Join-Path $lockedCase.Install 'MCPanel.exe.config') -Raw -Encoding UTF8
        $settings = @{}
        foreach ($setting in $recoveredConfig.configuration.appSettings.add) { $settings[[string]$setting.key] = [string]$setting.value }
        if ($settings['Ai.Model'] -ne 'custom-model' -or $settings['Ai.ApiKey'] -ne '' -or $settings['Ai.Endpoint'] -ne 'https://example.com/中文') { throw 'Recovery lost original or explicitly empty/Unicode settings.' }
        if ([IO.File]::ReadAllText((Join-Path $lockedCase.Install 'StoreData\user-data.txt')) -ne 'keep database and website settings') { throw 'Recovery modified operational data.' }
        if ([IO.File]::ReadAllText($lockedLibrary) -ne 'shared runtime') { throw 'Recovery replaced the unchanged locked runtime.' }
        Write-Host 'Passed: missing-EXE recovery previews safely, restores original settings, skips an unchanged locked DLL, and can run again.'
    } finally { $libraryHandle.Dispose() }

    $fixedLockedCase = New-Case 'fixed-readable-locked-library'
    & (Join-Path $PSScriptRoot '..\Repair-Update.ps1') -InstallDirectory $fixedLockedCase.Install
    $fixedLibrary = Join-Path $fixedLockedCase.Install 'System.Buffers.dll'
    Write-Text $fixedLibrary 'shared runtime'
    Write-Text (Join-Path $fixedLockedCase.Payload 'System.Buffers.dll') 'shared runtime'
    $libraryHandle = [IO.File]::Open($fixedLibrary, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $fixedLocked = Invoke-Updater $CurrentBuild $fixedLockedCase $layout
        if (!$fixedLocked.Result.Success -or $fixedLocked.ExitCode -ne 0) { throw ('Fixed updater failed with an unchanged locked DLL: ' + $fixedLocked.Result.Message) }
        Write-Host 'Passed: the fixed real updater leaves the unchanged locked DLL in place and completes the executable switch.'
    } finally { $libraryHandle.Dispose() }

    $changedLockedCase = New-Case 'fixed-changed-locked-library'
    & (Join-Path $PSScriptRoot '..\Repair-Update.ps1') -InstallDirectory $changedLockedCase.Install
    $changedLibrary = Join-Path $changedLockedCase.Install 'System.Buffers.dll'
    Write-Text $changedLibrary 'old runtime'
    Write-Text (Join-Path $changedLockedCase.Payload 'System.Buffers.dll') 'new runtime'
    $libraryHandle = [IO.File]::Open($changedLibrary, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    try {
        $changedLocked = Invoke-Updater $CurrentBuild $changedLockedCase $layout
        if ($changedLocked.Result.Success -or $changedLocked.ExitCode -eq 0) { throw 'Changed locked DLL should stop the update.' }
        if ([IO.File]::ReadAllText((Join-Path $changedLockedCase.Install 'MCPanel.exe')) -ne 'old') { throw 'Locked changed DLL left the old executable missing or replaced.' }
        Write-Host 'Passed: a changed locked DLL stops the fixed real updater before any installed program files change.'
    } finally { $libraryHandle.Dispose() }
} finally {
    foreach ($directory in @($root) + $helperRoots) {
        if (Test-Path -LiteralPath $directory) {
            Get-ChildItem -LiteralPath $directory -File -Recurse -Force | ForEach-Object { $_.IsReadOnly = $false }
            Remove-Item -LiteralPath $directory -Recurse -Force
        }
    }
}
