param([string]$PackageFile, [string]$CaseRoot)

$ErrorActionPreference = 'Stop'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$catalog = Get-Content -LiteralPath (Join-Path $project 'Installer\dependencies.json') -Raw | ConvertFrom-Json
foreach ($item in @(@('dotnet', $catalog.dotnet), @('vc', $catalog.visualCpp))) {
    $download = Join-Path $CaseRoot ($item[0] + '-official.exe')
    Invoke-WebRequest -Uri $item[1].url -OutFile $download -UseBasicParsing
    if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ine $item[1].sha256) { throw 'Microsoft redistributable digest mismatch.' }
    $signature = Get-AuthenticodeSignature -FilePath $download
    if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch 'Microsoft Corporation') {
        throw ('Prerequisite is not a valid Microsoft-signed installer: ' + $item[0])
    }
}
Write-Host 'Passed: both pinned prerequisite downloads have valid Microsoft Authenticode signatures.'

$web = Join-Path $CaseRoot 'http-fixture'
[IO.Directory]::CreateDirectory($web) | Out-Null
[IO.File]::Copy($PackageFile, (Join-Path $web 'application.zip'))
$source = Join-Path $CaseRoot 'runtime.cs'
[IO.File]::WriteAllText($source, @'
using System;
using System.IO;
using System.Diagnostics;
class RuntimeFixture {
  static int Main() {
    var root = Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_STATE");
    Directory.CreateDirectory(root);
    var name = Path.GetFileName(Process.GetCurrentProcess().MainModule.FileName).StartsWith("dotnet") ? "dotnet" : "vc";
    var exit = Environment.GetEnvironmentVariable("MCPANEL_SETUP_TEST_RUNTIME_EXIT") ?? "0";
    File.AppendAllText(Path.Combine(root, "calls.txt"), name + Environment.NewLine);
    File.AppendAllText(Path.Combine(root, "exit-codes.txt"), exit + Environment.NewLine);
    if (exit == "0" || exit == "3010") File.WriteAllText(Path.Combine(root, name + ".done"), "installed");
    return int.Parse(exit);
  }
}
'@)
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
$runtime = Join-Path $web 'runtime.exe'
& $compiler /nologo /target:winexe ('/out:' + $runtime) $source
if ($LASTEXITCODE -ne 0) { throw 'Unable to compile inert prerequisite fixture.' }
$listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
$listener.Start()
$port = $listener.LocalEndpoint.Port
$listener.Stop()
$server = Start-Process -FilePath (Get-Command python.exe).Source -ArgumentList @('-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', ('"' + $web + '"')) -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $CaseRoot 'http-out.txt') -RedirectStandardError (Join-Path $CaseRoot 'http-errors.txt')
$baseUrl = 'http://127.0.0.1:' + $port
$names = @('MCPANEL_SETUP_TEST_DOTNET', 'MCPANEL_SETUP_TEST_VC', 'MCPANEL_SETUP_TEST_STATE', 'MCPANEL_SETUP_TEST_RUNTIME_EXIT', 'MCPANEL_SETUP_TEST_FAIL_FILE')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name) }
try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        try { $null = Invoke-WebRequest -Uri ($baseUrl + '/runtime.exe') -Method Head -UseBasicParsing; $ready = $true; break }
        catch { Start-Sleep -Milliseconds 250 }
    }
    if (!$ready) { throw 'Loopback installer fixture did not start.' }
    $runtimeHash = (Get-FileHash -LiteralPath $runtime -Algorithm SHA256).Hash
    $catalog.dotnet.url = $baseUrl + '/runtime.exe'
    $catalog.visualCpp.url = $baseUrl + '/runtime.exe'
    $catalog.dotnet.sha256 = $runtimeHash
    $catalog.visualCpp.sha256 = $runtimeHash
    $testCatalog = Join-Path $CaseRoot 'dependencies-test.json'
    $catalog | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $testCatalog -Encoding utf8
    $testOutput = Join-Path $CaseRoot 'test-installer'
    & (Join-Path $project 'Build-OnlineInstaller.ps1') -PackageFile $PackageFile -OutputDirectory $testOutput -PackageUrl ($baseUrl + '/application.zip') -TestBuild -TestDependencyCatalog $testCatalog
    $testSetup = Join-Path $testOutput 'MCPanel-Setup.exe'
    $env:MCPANEL_SETUP_TEST_DOTNET = 'missing'
    $env:MCPANEL_SETUP_TEST_VC = 'missing'
    $env:MCPANEL_SETUP_TEST_RUNTIME_EXIT = '0'
    $env:MCPANEL_SETUP_TEST_STATE = Join-Path $CaseRoot 'missing-state'
    [IO.File]::WriteAllText((Join-Path $web 'application.zip'), 'corrupt downloaded payload')
    $badDownload = Join-Path $CaseRoot 'download-checksum-failure'
    $null = Invoke-Setup $badDownload -Package '' -Exe $testSetup -ExpectedExitCode 1
    if ((Test-Path -LiteralPath (Join-Path $badDownload 'MCPanel.exe')) -or
        (Test-Path -LiteralPath (Join-Path $env:MCPANEL_SETUP_TEST_STATE 'calls.txt'))) { throw 'Download checksum failure modified the application or installed prerequisites.' }
    [IO.File]::Copy($PackageFile, (Join-Path $web 'application.zip'), $true)
    Write-Host 'Passed: a downloaded package checksum failure stops before prerequisite or application installation.'
    $install = Join-Path $CaseRoot 'downloaded'
    $null = Invoke-Setup $install -Package '' -Exe $testSetup
    $calls = [IO.File]::ReadAllLines((Join-Path $env:MCPANEL_SETUP_TEST_STATE 'calls.txt'))
    if (($calls -join ',') -ne 'dotnet,vc') { throw 'Missing prerequisites were not installed in the expected order.' }
    if (!(Test-Path -LiteralPath (Join-Path $install 'MCPanel.exe'))) { throw 'Downloaded payload was not installed.' }
    Write-Host 'Passed: native HTTP download, checksum validation, missing .NET/VC++ installation order and application deployment.'
    Invoke-RegisteredUninstall $install

    $env:MCPANEL_SETUP_TEST_STATE = Join-Path $CaseRoot 'failure-state'
    $env:MCPANEL_SETUP_TEST_RUNTIME_EXIT = '1603'
    $failed = Join-Path $CaseRoot 'runtime-failure'
    try { $null = Invoke-Setup $failed -Package '' -Exe $testSetup -ExpectedExitCode 1 }
    catch {
        Write-Host ('Fixture failure-state directory: ' + $env:MCPANEL_SETUP_TEST_STATE)
        if (Test-Path -LiteralPath $env:MCPANEL_SETUP_TEST_STATE) {
            Get-ChildItem -LiteralPath $env:MCPANEL_SETUP_TEST_STATE -File | ForEach-Object { Write-Host ($_.Name + ': ' + [IO.File]::ReadAllText($_.FullName)) }
        }
        throw
    }
    if (Test-Path -LiteralPath (Join-Path $failed 'MCPanel.exe')) { throw 'Prerequisite failure still installed the main program.' }
    if (([IO.File]::ReadAllLines((Join-Path $env:MCPANEL_SETUP_TEST_STATE 'calls.txt')) -join ',') -ne 'dotnet') { throw 'A failed prerequisite did not stop the remaining installation steps.' }
    Write-Host 'Passed: failed prerequisite installation leaves the main application unmodified.'

    $env:MCPANEL_SETUP_TEST_STATE = Join-Path $CaseRoot 'restart-state'
    $env:MCPANEL_SETUP_TEST_RUNTIME_EXIT = '3010'
    $reboot = Join-Path $CaseRoot 'restart-required'
    $exitCode = Invoke-Setup $reboot -Package '' -Exe $testSetup -ExpectedExitCode 1 -ExtraArguments '/RESTARTEXITCODE=3010'
    if ($exitCode -ne 3010 -or !(Test-Path -LiteralPath (Join-Path $reboot 'MCPanel.exe'))) { throw 'Restart requirement was not propagated while honoring /NORESTART.' }
    Write-Host 'Passed: a prerequisite restart request installs the payload, reports 3010 and honors /NORESTART.'
    Invoke-RegisteredUninstall $reboot

    $env:MCPANEL_SETUP_TEST_RUNTIME_EXIT = '0'
    $late = Join-Path $CaseRoot 'late-failure'
    [IO.Directory]::CreateDirectory($late) | Out-Null
    [IO.File]::WriteAllText((Join-Path $late 'MCPanel.exe'), 'original executable')
    [IO.File]::WriteAllText((Join-Path $late 'MCPanel.exe.config'), '<configuration><appSettings><add key="Ai.Model" value="original" /></appSettings></configuration>')
    $configBefore = [IO.File]::ReadAllBytes((Join-Path $late 'MCPanel.exe.config'))
    $env:MCPANEL_SETUP_TEST_FAIL_FILE = 'MCPanel.exe'
    $null = Invoke-Setup $late -Package $PackageFile -Exe $testSetup -ExpectedExitCode 1
    if ([IO.File]::ReadAllText((Join-Path $late 'MCPanel.exe')) -ne 'original executable' -or
        [Convert]::ToBase64String([IO.File]::ReadAllBytes((Join-Path $late 'MCPanel.exe.config'))) -ne [Convert]::ToBase64String($configBefore) -or
        (Test-Path -LiteralPath (Join-Path $late 'System.Buffers.dll'))) { throw 'Late native replacement failure did not roll back all completed program/configuration changes.' }
    Write-Host 'Passed: injected final executable replacement failure restores all prior changes and leaves the old EXE present.'
} finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name]) }
    if (!$server.HasExited) { $server.Kill(); $server.WaitForExit(5000) | Out-Null }
    $server.Dispose()
}
