param(
    [Parameter(Mandatory = $true)][string]$PackageFile,
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts\installer'),
    [string]$PackageUrl,
    [string]$CompilerPath,
    [switch]$TestBuild,
    [string]$TestDependencyCatalog
)

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
foreach ($dependency in @($catalog.compiler, $catalog.dotnet, $catalog.visualCpp)) {
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

# Only metadata is compiled into the bootstrapper, never the application ZIP or
# Microsoft redistributables. Its immutable package hash binds each release.
function Pascal([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }
function Define([string]$Name, [string]$Value) {
    if ($Value -match '["\r\n]') { throw ('Invalid compiler definition: ' + $Name) }
    return '#define ' + $Name + ' "' + $Value + '"'
}
$hash = (Get-FileHash -LiteralPath $PackageFile -Algorithm SHA256).Hash.ToLowerInvariant()
$vcMinimum = [Version]$catalog.visualCpp.minimumVersion
$vcMS = ([uint32]$vcMinimum.Major -shl 16) -bor [uint32]$vcMinimum.Minor
$vcLS = ([uint32]$vcMinimum.Build -shl 16) -bor [uint32]$vcMinimum.Revision
$maximumRelativeLength = ($files | ForEach-Object { $_.Length } | Measure-Object -Maximum).Maximum
$header = @(
    (Define 'AppVersion' $version), (Define 'PackageUrl' $PackageUrl), (Define 'PackageHash' $hash),
    (Define 'RequiredSpace' ([string]([Math]::Max(104857600L, $expandedBytes * 4)))),
    (Define 'DotNetUrl' $catalog.dotnet.url), (Define 'DotNetHash' $catalog.dotnet.sha256),
    (Define 'DotNetArguments' $catalog.dotnet.arguments),
    (Define 'VcUrl' $catalog.visualCpp.url), (Define 'VcHash' $catalog.visualCpp.sha256),
    (Define 'VcArguments' $catalog.visualCpp.arguments), (Define 'MinimumNetRelease' ([string]$catalog.dotnet.minimumRelease)),
    (Define 'MinimumVcMS' ([string]$vcMS)), (Define 'MinimumVcLS' ([string]$vcLS)),
    (Define 'MaximumRelativeLength' ([string]$maximumRelativeLength))
)
if ($TestBuild) { $header += '#define InstallerTestBuild' }
[IO.File]::WriteAllLines((Join-Path $work 'Header.iss'), $header, [Text.UTF8Encoding]::new($true))
$generated = @('procedure LoadDeploymentLists;', 'begin')
foreach ($name in @($files | Sort-Object @{ Expression = { $_ -ieq 'MCPanel.exe' } }, { $_ })) {
    $generated += '  ProgramFiles.Add(' + (Pascal $name) + ');'
}
foreach ($name in $layout.preservedTopLevelNames) { $generated += '  PreservedNames.Add(' + (Pascal $name) + ');' }
foreach ($key in $layout.preservedAppSettingKeys) { $generated += '  PreservedKeys.Add(' + (Pascal $key) + ');' }
$generated += 'end;'
[IO.File]::WriteAllLines((Join-Path $work 'Layout.iss'), $generated, [Text.UTF8Encoding]::new($true))

if (!$CompilerPath) {
    $compilerParent = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $compilerRoot = Join-Path $compilerParent ('MCPanel-InnoSetup-' + $catalog.compiler.version)
    $CompilerPath = Join-Path $compilerRoot 'ISCC.exe'
    if (!(Test-Path -LiteralPath $CompilerPath)) {
        $bootstrap = Join-Path $work 'innosetup.exe'
        Invoke-WebRequest -Uri $catalog.compiler.url -OutFile $bootstrap -UseBasicParsing
        if ((Get-FileHash -LiteralPath $bootstrap -Algorithm SHA256).Hash -ine $catalog.compiler.sha256) { throw 'Inno Setup compiler checksum mismatch.' }
        $install = Start-Process -FilePath $bootstrap -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/SP-', ('/DIR="' + $compilerRoot + '"')) -PassThru -Wait
        if ($install.ExitCode -ne 0) { throw ('Inno Setup compiler installation failed: ' + $install.ExitCode) }
    }
}
$compilerVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($CompilerPath).FileVersion
if ($compilerVersion -ne $catalog.compiler.version -and !$compilerVersion.StartsWith($catalog.compiler.version + '.')) { throw ('Expected pinned Inno Setup ' + $catalog.compiler.version + ', got ' + $compilerVersion) }
& $CompilerPath ('/DGeneratedDirectory=' + $work) ('/O' + $OutputDirectory) (Join-Path $PSScriptRoot 'Installer\MCPanel.iss')
if ($LASTEXITCODE -ne 0) { throw ('Inno Setup compilation failed: ' + $LASTEXITCODE) }
$setup = Join-Path $OutputDirectory 'MCPanel-Setup.exe'
$setupHash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText(($setup + '.sha256'), ($setupHash + '  MCPanel-Setup.exe' + "`r`n"), [Text.UTF8Encoding]::new($false))
Write-Host ('Online installer: ' + $setup)
Write-Host ('Bootstrapper size: ' + (Get-Item -LiteralPath $setup).Length + ' bytes; application ZIP is downloaded during install.')
Write-Host ('Pinned application SHA-256: ' + $hash)
