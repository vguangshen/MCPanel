[CmdletBinding(SupportsShouldProcess = $true)]
param(
    [Parameter(Mandatory = $true)]
    [string]$InstallDirectory
)

$ErrorActionPreference = 'Stop'
$installRoot = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\', '/')
if ($installRoot -eq [IO.Path]::GetPathRoot([IO.Path]::GetFullPath($InstallDirectory)).TrimEnd('\', '/')) {
    throw 'Select the MCPanel installation directory, not a drive root.'
}
if (!(Test-Path -LiteralPath (Join-Path $installRoot 'MCPanel.exe') -PathType Leaf)) {
    throw 'The selected directory does not contain MCPanel.exe.'
}

foreach ($process in [Diagnostics.Process]::GetProcessesByName('MCPanel')) {
    try {
        # Service wrappers also use MCPanel.exe. Leave Session 0 services running.
        if ($process.SessionId -eq 0) { continue }
        $executable = $process.MainModule.FileName
        if ([string]::Equals($executable, (Join-Path $installRoot 'MCPanel.exe'), [StringComparison]::OrdinalIgnoreCase)) {
            throw 'Close MCPanel completely from its tray menu before running this repair.'
        }
    } finally { $process.Dispose() }
}

# Old clients run their own updater. Keep every file, but move the two known
# blocking directories out of its copy/overwrite paths before retrying online.
$storeData = Join-Path $installRoot 'StoreData'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$diagnostics = Join-Path $installRoot 'Diagnostics'
$current = Join-Path $storeData 'Updates\Rollback\Current'
$moves = @()
if (Test-Path -LiteralPath $diagnostics -PathType Container) {
    $moves += [pscustomobject]@{
        Source = $diagnostics
        Destination = Join-Path $storeData ('Diagnostics\Recovered-' + $stamp)
    }
}
if (Test-Path -LiteralPath $current -PathType Container) {
    $moves += [pscustomobject]@{
        Source = $current
        Destination = Join-Path $storeData ('Updates\Rollback\Current.failed-' + $stamp)
    }
}
foreach ($move in $moves) {
    if ($PSCmdlet.ShouldProcess($move.Source, ('Move without deleting files to ' + $move.Destination))) {
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($move.Destination)) | Out-Null
        [IO.Directory]::Move($move.Source, $move.Destination)
        Write-Output ('Preserved: ' + $move.Destination)
    }
}
if (!$WhatIfPreference) {
    Write-Output 'Repair complete. Start the installed MCPanel and retry Check and Update.'
}
