param([string]$ApplicationDirectory = (Join-Path $PSScriptRoot '..\bin\Release\net462'))

$ErrorActionPreference = 'Stop'
$ApplicationDirectory = [IO.Path]::GetFullPath($ApplicationDirectory)
# Exercise the shipped .NET Framework downloader in its actual runtime.
if ($PSVersionTable.PSEdition -eq 'Core') {
    & "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe" -NoProfile -ExecutionPolicy Bypass -File $PSCommandPath -ApplicationDirectory $ApplicationDirectory
    if ($LASTEXITCODE -ne 0) { throw "SQL Server download verification failed: $LASTEXITCODE" }
    exit 0
}

$root = Join-Path ([IO.Path]::GetTempPath()) ('MCPanelSqlMedia-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($root) | Out-Null
$appExe = Join-Path $ApplicationDirectory 'MCPanel.exe'
[void][Reflection.Assembly]::LoadFrom($appExe)
# Keep reflection arguments and background callbacks in managed code. Windows
# PowerShell wraps cmdlet strings in PSObject when constructing object arrays,
# and script-block callbacks have no runspace on HttpClient continuations.
Add-Type -ReferencedAssemblies @($appExe, 'System.Net.Http.dll', 'System.Core.dll') -TypeDefinition @'
using System;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
public static class MCPanelSqlDownloadProbe {
    public static string Download(string url, string directory, string file, int major) {
        using (var installer = new MCPanel.EnvironmentInstaller()) {
            var method = typeof(MCPanel.EnvironmentInstaller).GetMethod("DownloadCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            var task = (Task<string>)method.Invoke(installer, new object[] {
                url, directory, file, new Action<MCPanel.InstallProgress>(p => {}), 0d, 100d, CancellationToken.None, (int?)major
            });
            return task.GetAwaiter().GetResult();
        }
    }
}
'@
[xml]$config = Get-Content -LiteralPath (Join-Path $ApplicationDirectory 'MCPanel.exe.config') -Raw -Encoding UTF8

function Assert-MicrosoftInstaller([string]$File, [int]$Major) {
    $signature = Get-AuthenticodeSignature -FilePath $File
    if ($signature.Status -ne 'Valid' -or $null -eq $signature.SignerCertificate -or
        $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') { throw "Invalid Microsoft signature: $File ($($signature.Status))" }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($File)
    if ($version.ProductMajorPart -ne $Major) { throw "Wrong product major in $File : $($version.ProductVersion), expected $Major" }
    Write-Host "Verified Microsoft installer: $([IO.Path]::GetFileName($File)), product $($version.ProductVersion), SHA256 $((Get-FileHash -LiteralPath $File -Algorithm SHA256).Hash)"
}

try {
    foreach ($release in @(
        @{ Key = 'Environment.SqlServer.Sql2017ExpressUrl'; File = 'SQLServer2017-SSEI-Expr.exe'; Major = 14 },
        @{ Key = 'Environment.SqlServer.Sql2022ExpressUrl'; File = 'SQL2022-SSEI-Expr.exe'; Major = 16 }
    )) {
        $url = [string]($config.configuration.appSettings.add | Where-Object key -eq $release.Key).value
        if (!$url) { throw "Missing shipped SQL Server URL: $($release.Key)" }
        $bootstrapper = [MCPanelSqlDownloadProbe]::Download($url, $root, $release.File, $release.Major)
        Assert-MicrosoftInstaller $bootstrapper $release.Major

        # The reported Windows Server 2012 failure uses 2017. Verify its second
        # download stage and extraction with the same official CLI as production.
        if ($release.Major -eq 14) {
            $media = Join-Path $root '2017-media'
            [IO.Directory]::CreateDirectory($media) | Out-Null
            $arguments = '/ACTION=Download /MEDIATYPE=Core /QUIET /MEDIAPATH="' + $media + '"'
            $process = Start-Process -FilePath $bootstrapper -ArgumentList $arguments -PassThru
            try {
                if (!$process.WaitForExit(600000)) { $process.Kill(); throw 'SQL Server 2017 media download timed out.' }
                if ($process.ExitCode -ne 0) { throw "SQL Server 2017 media download failed: $($process.ExitCode)" }
            } finally { $process.Dispose() }
            $package = Get-ChildItem -LiteralPath $media -Filter '*.exe' -Recurse -File |
                Where-Object Length -gt 100000000 | Select-Object -First 1
            if (!$package) { throw 'The official downloader did not retrieve the complete SQL Server 2017 Express Core media.' }
            Assert-MicrosoftInstaller $package.FullName 14
            $extract = Join-Path $root '2017-extracted'
            [IO.Directory]::CreateDirectory($extract) | Out-Null
            $process = Start-Process -FilePath $package.FullName -ArgumentList ('/x:"' + $extract + '" /q') -PassThru
            try {
                if (!$process.WaitForExit(120000)) { $process.Kill(); throw 'SQL Server 2017 media extraction timed out.' }
                if ($process.ExitCode -ne 0) { throw "SQL Server 2017 media extraction failed: $($process.ExitCode)" }
            } finally { $process.Dispose() }
            $setup = Join-Path $extract 'setup.exe'
            if (!(Test-Path -LiteralPath $setup)) { throw 'Extracted SQL Server 2017 media does not contain setup.exe.' }
            Assert-MicrosoftInstaller $setup 14
            Write-Host 'Passed: shipped downloader, SQL Server 2017 Core media download, extraction and setup signature/version validation.'
        }
    }
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
