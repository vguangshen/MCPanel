param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$PackageBaseUrl = '',

    [string]$ReleaseNotes = '',
    [string]$OutputDirectory,
    [string]$PublishDirectory,
    [switch]$Mandatory
)

$ErrorActionPreference = 'Stop'
$scriptRoot = if ([string]::IsNullOrWhiteSpace($PSScriptRoot)) {
    Split-Path -Parent $MyInvocation.MyCommand.Path
} else {
    $PSScriptRoot
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $scriptRoot 'artifacts\updates'
}
if ([string]::IsNullOrWhiteSpace($PublishDirectory)) {
    $publishDirectory = Join-Path $scriptRoot 'bin\Release\publish\net462\win-x64'
} else {
    $publishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
}
$executable = Join-Path $publishDirectory 'MCPanel.exe'
$preservedNames = @(
    'StoreData', 'AccountApi', 'Runtime', 'Downloads', 'Tools', 'web', 'Cache', 'Frp', 'Nginx', 'MySQL', 'MSSQL', 'Tomcat',
    'SSMS', 'Navicat Premium Lite', 'config.ini', 'config.ini.previous', 'device.identity',
    'database.config', 'database.config.previous', 'logs'
)

if (!(Test-Path -LiteralPath $executable)) {
    throw "Published application was not found: $executable"
}

if (![string]::IsNullOrWhiteSpace($PackageBaseUrl) -and $PackageBaseUrl -notmatch '^https://') {
    throw 'PackageBaseUrl must be an HTTPS base URL when provided.'
}

$normalizedVersion = $Version.Trim().TrimStart('v', 'V')
$parsedVersion = $null
if (![Version]::TryParse($normalizedVersion, [ref]$parsedVersion)) {
    throw "Invalid version: $Version"
}

$publishedVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable).FileVersion
if (!$publishedVersion.StartsWith($normalizedVersion + '.', [StringComparison]::OrdinalIgnoreCase) -and
    !$publishedVersion.Equals($normalizedVersion, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Published executable version $publishedVersion does not match requested package version $normalizedVersion. Update MCPanel.csproj and publish first."
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$packageName = "MCPanel-$normalizedVersion.zip"
$packageFile = Join-Path $OutputDirectory $packageName
$manifestFile = Join-Path $OutputDirectory 'update-manifest.json'
$hashFile = $packageFile + '.sha256'

if (Test-Path -LiteralPath $packageFile) {
    Remove-Item -LiteralPath $packageFile -Force
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::Open($packageFile, [IO.Compression.ZipArchiveMode]::Create)
try {
    Get-ChildItem -LiteralPath $publishDirectory -File -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($publishDirectory.Length).TrimStart('\')
        $topLevel = $relative.Split('\')[0]
        if ($preservedNames -contains $topLevel) {
            return
        }

        $entryName = $relative.Replace('\', '/')
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile(
            $archive,
            $_.FullName,
            $entryName,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
}

$hash = (Get-FileHash -LiteralPath $packageFile -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText($hashFile, "$hash  $packageName`r`n", [Text.UTF8Encoding]::new($false))

if (![string]::IsNullOrWhiteSpace($PackageBaseUrl)) {
    $packageUrl = $PackageBaseUrl.TrimEnd('/') + '/' + $packageName
    $manifest = [ordered]@{
        version = $normalizedVersion
        packageUrl = $packageUrl
        sha256 = $hash
        releaseNotes = $ReleaseNotes
        mandatory = [bool]$Mandatory
    }
    [IO.File]::WriteAllText(
        $manifestFile,
        ($manifest | ConvertTo-Json -Depth 4),
        [Text.UTF8Encoding]::new($false))
} elseif (Test-Path -LiteralPath $manifestFile) {
    Remove-Item -LiteralPath $manifestFile -Force
}

Write-Host "Update package: $packageFile"
Write-Host "SHA-256:       $hashFile"
if (![string]::IsNullOrWhiteSpace($PackageBaseUrl)) {
    Write-Host "Online manifest: $manifestFile"
    Write-Host "Upload the ZIP and update-manifest.json to the HTTPS website."
} else {
    Write-Host "Online manifest: not generated (provide -PackageBaseUrl for online updates)."
}
