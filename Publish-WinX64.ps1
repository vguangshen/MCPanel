param(
    [string]$OutputDirectory = 'D:\MCPanel'
)

$ErrorActionPreference = 'Stop'

$projectRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectFile = Join-Path $projectRoot 'MCPanel.csproj'
$testProject = Join-Path $projectRoot 'MCPanel.Tests\MCPanel.Tests.csproj'
$outputDir = [IO.Path]::GetFullPath($OutputDirectory)
$frameworkBuildDir = Join-Path $projectRoot 'bin\Release\net462'
$stagingDir = Join-Path $projectRoot 'obj\publish\net462'
$layoutManifestPath = Join-Path $projectRoot 'deployment-layout.json'
if (!(Test-Path -LiteralPath $layoutManifestPath)) {
    throw "Deployment layout manifest was not found: $layoutManifestPath"
}
if (!(Test-Path -LiteralPath $testProject)) {
    throw "Reliability test project was not found: $testProject"
}
$layoutManifest = Get-Content -LiteralPath $layoutManifestPath -Raw | ConvertFrom-Json
$preservedNames = @($layoutManifest.preservedTopLevelNames | ForEach-Object { [string]$_ })
if ($preservedNames.Count -eq 0) {
    throw "Deployment layout manifest does not define preserved top-level names: $layoutManifestPath"
}
$nugetPackagesRoot = if ([string]::IsNullOrWhiteSpace($env:NUGET_PACKAGES)) {
    Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::UserProfile)) '.nuget\packages'
} else {
    $env:NUGET_PACKAGES
}
$referenceAssemblyRoot = Join-Path $nugetPackagesRoot 'microsoft.netframework.referenceassemblies.net462\1.0.3\build'
$frameworkRootArgument = if (Test-Path -LiteralPath $referenceAssemblyRoot) {
    "/p:TargetFrameworkRootPath=$referenceAssemblyRoot\"
} else {
    $null
}

function Assert-ChildPath([string]$Path, [string]$ExpectedParent) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $fullParent = [IO.Path]::GetFullPath($ExpectedParent).TrimEnd('\')
    if (!$fullPath.StartsWith($fullParent + '\', [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify a path outside the expected directory: $fullPath"
    }
}

function Assert-PublishTarget([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd('\')
    $rootPath = [IO.Path]::GetPathRoot($fullPath).TrimEnd('\')
    if ([string]::IsNullOrWhiteSpace($fullPath) -or
        $fullPath.Equals($rootPath, [StringComparison]::OrdinalIgnoreCase) -or
        $fullPath.Equals([IO.Path]::GetFullPath($projectRoot).TrimEnd('\'), [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to use an unsafe publish target: $fullPath"
    }
}

if (Test-Path -LiteralPath $stagingDir) {
    Assert-ChildPath $stagingDir $projectRoot
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}

Write-Host "Running MCPanel reliability tests before publish..."
dotnet test $testProject `
    -c Release `
    -f net462 `
    $frameworkRootArgument
if ($LASTEXITCODE -ne 0) {
    throw "dotnet test failed with exit code $LASTEXITCODE. Publish was stopped."
}

Write-Host "Reliability tests passed. Publishing MCPanel..."
dotnet publish $projectFile `
    -c Release `
    -f net462 `
    --no-self-contained `
    $frameworkRootArgument `
    -o $stagingDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

New-Item -ItemType Directory -Path $outputDir -Force | Out-Null
Assert-PublishTarget $outputDir

Get-ChildItem -LiteralPath $outputDir -Force | ForEach-Object {
    if ($preservedNames -contains $_.Name) {
        return
    }

    Assert-ChildPath $_.FullName $outputDir
    Remove-Item -LiteralPath $_.FullName -Recurse -Force
}

Get-ChildItem -LiteralPath $stagingDir -Force | ForEach-Object {
    if ($preservedNames -contains $_.Name -and
        (Test-Path -LiteralPath (Join-Path $outputDir $_.Name))) {
        return
    }

    Copy-Item -LiteralPath $_.FullName -Destination $outputDir -Recurse -Force
}

$legacyDownloads = Join-Path $outputDir 'StoreData\Products'
$newDownloads = Join-Path $outputDir 'web\.downloads'
if (Test-Path -LiteralPath $legacyDownloads) {
    New-Item -ItemType Directory -Path $newDownloads -Force | Out-Null
    Get-ChildItem -LiteralPath $legacyDownloads -Force | ForEach-Object {
        $target = Join-Path $newDownloads $_.Name
        if (!(Test-Path -LiteralPath $target)) {
            Move-Item -LiteralPath $_.FullName -Destination $target
        }
    }
    Remove-Item -LiteralPath $legacyDownloads -Recurse -Force
}

if (Test-Path -LiteralPath $frameworkBuildDir) {
    Assert-ChildPath $frameworkBuildDir $projectRoot
    Remove-Item -LiteralPath $frameworkBuildDir -Recurse -Force
}

if (Test-Path -LiteralPath $stagingDir) {
    Assert-ChildPath $stagingDir $projectRoot
    Remove-Item -LiteralPath $stagingDir -Recurse -Force
}

$exe = Join-Path $outputDir 'MCPanel.exe'
if (!(Test-Path -LiteralPath $exe)) {
    throw "Publish completed without MCPanel.exe: $exe"
}

Write-Host "Published integrated MCPanel application to $outputDir"
Write-Host "Executable: $exe"
