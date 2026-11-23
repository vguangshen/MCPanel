param(
    [Parameter(Mandatory = $true)][string]$PackageFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\installer'),
    [string]$PackageUrl,
    [switch]$TestBuild,
    [string]$TestDependencyCatalog
)

# Builds MCPanel-Setup.exe: the branded WPF online installer and complete
# uninstaller (Installer/MCPanel.Setup). Only release metadata is embedded,
# never the application ZIP or Microsoft redistributables; the pinned SHA-256
# values bind each installer to exactly one release.

$ErrorActionPreference = 'Stop'
$PackageFile = [IO.Path]::GetFullPath($PackageFile)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[xml]$project = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'MCPanel.csproj') -Raw
$version = [string]$project.Project.PropertyGroup.Version
if (!$PackageUrl) { $PackageUrl = "https://github.com/vguangshen/MCPanel/releases/download/v$version/MCPanel-$version.zip" }
if (!$PackageUrl.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -and
    !($TestBuild -and $PackageUrl.StartsWith('http://127.0.0.1:', [StringComparison]::OrdinalIgnoreCase))) {
    throw 'The production package URL must use HTTPS.'
}
if ($TestDependencyCatalog -and !$TestBuild) { throw 'A dependency override is allowed only in a test build.' }
$catalogFile = if ($TestDependencyCatalog) { $TestDependencyCatalog } else { Join-Path $PSScriptRoot 'Installer\dependencies.json' }
$catalog = Get-Content -LiteralPath $catalogFile -Raw | ConvertFrom-Json
foreach ($dependency in @($catalog.dotnet, $catalog.visualCpp)) {
    if ($dependency.sha256 -notmatch '^[a-fA-F0-9]{64}$') { throw 'Every installer dependency requires a SHA-256 pin.' }
    if (!$dependency.url.StartsWith('https://', [StringComparison]::OrdinalIgnoreCase) -and
        !($TestBuild -and $dependency.url.StartsWith('http://127.0.0.1:', [StringComparison]::OrdinalIgnoreCase))) {
        throw 'Dependency download URLs must use HTTPS.'
    }
}

$layout = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'deployment-layout.json') -Raw | ConvertFrom-Json
$work = Join-Path $PSScriptRoot ('obj\installer\' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($work) | Out-Null
[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($PackageFile)
try {
    $files = @()
    $expandedBytes = 0L
    foreach ($entry in $archive.Entries) {
        if (!$entry.Name) { continue }
        $name = $entry.FullName.Replace('/', '\')
        if ([IO.Path]::IsPathRooted($name) -or $name -match '(^|\\)\.\.?($|\\)' -or $name -match '[:"\r\n]' -or
            @($layout.preservedTopLevelNames) -contains ($name -split '\\')[0]) {
            throw ('Unsafe or operational-data path in the installer package: ' + $name)
        }
        if ($files -contains $name) { throw ('Duplicate package entry: ' + $name) }
        $files += $name
        $expandedBytes += $entry.Length
    }
    foreach ($name in @('MCPanel.exe', 'MCPanel.exe.config', 'deployment-layout.json')) {
        if ($files -notcontains $name) { throw ('Incomplete application package: ' + $name) }
    }
    $exe = $archive.GetEntry('MCPanel.exe')
    $extracted = Join-Path $work 'MCPanel.exe'
    [IO.Compression.ZipFileExtensions]::ExtractToFile($exe, $extracted)
    if ([Diagnostics.FileVersionInfo]::GetVersionInfo($extracted).FileVersion -ne ($version + '.0')) {
        throw 'The package executable does not match MCPanel.csproj.'
    }
} finally { $archive.Dispose() }

# MCPanel.exe is replaced last, so a failed deployment never leaves a new
# executable running against old libraries.
$programFiles = @($files | Sort-Object @{ Expression = { $_ -ieq 'MCPanel.exe' } }, { $_ })
# Windows PowerShell 5.1 does not unroll a JSON array piped from ConvertFrom-Json,
# so enumerate it with foreach instead of the pipeline.
$migrationSource = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer\download-url-migrations.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$urlMigrations = @(foreach ($migration in $migrationSource) { [ordered]@{ Key = [string]$migration.Key; PreviousUrl = [string]$migration.PreviousUrl; CurrentUrl = [string]$migration.CurrentUrl } })
$config = [ordered]@{
    version = $version
    packageUrl = $PackageUrl
    packageSha256 = (Get-FileHash -LiteralPath $PackageFile -Algorithm SHA256).Hash.ToLowerInvariant()
    requiredSpaceBytes = [Math]::Max(104857600L, $expandedBytes * 4)
    maximumRelativeLength = ($files | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
    dotNet = [ordered]@{
        name = [string]$catalog.dotnet.name; minimumRelease = [int]$catalog.dotnet.minimumRelease
        url = [string]$catalog.dotnet.url; sha256 = [string]$catalog.dotnet.sha256; arguments = [string]$catalog.dotnet.arguments
    }
    visualCpp = [ordered]@{
        name = [string]$catalog.visualCpp.name; minimumVersion = [string]$catalog.visualCpp.minimumVersion
        url = [string]$catalog.visualCpp.url; sha256 = [string]$catalog.visualCpp.sha256; arguments = [string]$catalog.visualCpp.arguments
    }
    programFiles = $programFiles
    preservedTopLevelNames = @($layout.preservedTopLevelNames)
    preservedAppSettingKeys = @($layout.preservedAppSettingKeys)
    urlMigrations = $urlMigrations
}
$configFile = Join-Path $work 'setup-config.json'
[IO.File]::WriteAllText($configFile, ($config | ConvertTo-Json -Depth 6), (New-Object Text.UTF8Encoding($false)))

$setupProject = Join-Path $PSScriptRoot 'Installer\MCPanel.Setup\MCPanel.Setup.csproj'
$buildOutput = Join-Path $work 'setup'
$arguments = @('build', $setupProject, '--configuration', 'Release', '--nologo', '--output', $buildOutput,
    ('-p:SetupConfigFile=' + $configFile), ('-p:Version=' + $version),
    ('-p:InstallerTestBuild=' + $(if ($TestBuild) { 'true' } else { 'false' })))
& dotnet @arguments
if ($LASTEXITCODE -ne 0) { throw ('Setup program build failed: ' + $LASTEXITCODE) }

$setup = Join-Path $OutputDirectory 'MCPanel-Setup.exe'
Copy-Item -LiteralPath (Join-Path $buildOutput 'MCPanel-Setup.exe') -Destination $setup -Force
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($setup + '.sha256'), ($setupHash + '  MCPanel-Setup.exe' + "`r`n"), (New-Object Text.UTF8Encoding($false)))
Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
Write-Host ('Online installer: ' + $setup)
Write-Host ('Bootstrapper size: ' + (Get-Item -LiteralPath $setup).Length + ' bytes; application ZIP is downloaded during install.')
Write-Host ('Pinned application SHA-256: ' + $config.packageSha256)
