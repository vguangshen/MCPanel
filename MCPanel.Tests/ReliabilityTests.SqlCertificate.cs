using System;
using System.IO;
using System.Security.Principal;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private static async Task<ProcessRunResult> RunSqlCertificateFunctionsAsync(string root, string body)
    {
        var source = Path.Combine(root, "certificate-functions.ps1");
        File.WriteAllText(source, PanelSettingsService.BuildSqlServerLocalCertificateScript(Path.Combine(root, "certificate.log")), new UTF8Encoding(true));
        var runner = Path.Combine(root, "test-certificate.ps1");
        File.WriteAllText(runner, """
            $ErrorActionPreference = 'Stop'
            $tokens = $null; $errors = $null
            $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'certificate-functions.ps1'), [ref]$tokens, [ref]$errors)
            if ($errors.Count) { throw ($errors | Out-String) }
            $functions = $ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] }, $true)
            foreach ($function in $functions) { . ([scriptblock]::Create($function.Extent.Text)) }
            """ + Environment.NewLine + body, new UTF8Encoding(true));
        return await ProcessRunner.RunPowerShellFileAsync(runner, false, default, captureOutput: true, timeout: TimeSpan.FromSeconds(90));
    }

    [TestMethod]
    public async Task SqlLocalCertificateUsesLegacyCertreqWithARealSqlCompatiblePrivateKey()
    {
        if (!ProcessRunner.IsAdministrator()) Assert.Inconclusive("Certificate integration needs an administrator token.");
        var root = CreateTemporaryDirectory();
        try
        {
            var result = await RunSqlCertificateFunctionsAsync(root, """
                function New-SelfSignedCertificate { throw 'Unavailable legacy PowerShell parameters must not be used.' }
                $certificate = $null
                try {
                    $certificate = New-MCPanelSqlCertificate
                    if (-not $certificate.HasPrivateKey -or $certificate.Subject -ne 'CN=localhost') { throw 'Missing SQL certificate/private key.' }
                    if ($certificate.PrivateKey.KeySize -ne 2048 -or $certificate.PrivateKey.CspKeyContainerInfo.KeyNumber -ne 'Exchange') { throw 'Wrong SQL Server key.' }
                    if ($certificate.SignatureAlgorithm.Value -ne '1.2.840.113549.1.1.11') { throw 'Certificate is not SHA256/RSA signed.' }
                    $eku = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.37' }
                    if (@($eku.EnhancedKeyUsages | Where-Object { $_.Value -eq '1.3.6.1.5.5.7.3.1' }).Count -ne 1) { throw 'Missing server authentication EKU.' }
                    $san = $certificate.Extensions | Where-Object { $_.Oid.Value -eq '2.5.29.17' }
                    if ($san.Format($false) -notmatch 'localhost') { throw 'Missing localhost SAN.' }
                    if ($certificate.NotAfter -lt (Get-Date).AddYears(4)) { throw 'Unexpected certificate validity.' }
                }
                finally {
                    if ($null -ne $certificate) {
                        $store = New-Object Security.Cryptography.X509Certificates.X509Store('My', 'LocalMachine')
                        try { $store.Open('ReadWrite'); $store.Remove($certificate) } finally { $store.Close() }
                        $key = $certificate.PrivateKey
                        if ($key) { $key.PersistKeyInCsp = $false; $key.Clear() }
                        $certificate.Reset()
                    }
                }
                """);
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow("LocalSystem", "S-1-5-18")]
    [DataRow(@"NT AUTHORITY\NETWORK SERVICE", "S-1-5-20")]
    [DataRow(@"NT AUTHORITY\LocalService", "S-1-5-19")]
    [DataRow("current-user", "current-user")]
    public async Task SqlCertificateGrantsTheActualServiceIdentity(string account, string expected)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            if (account == "current-user")
            {
                using var current = WindowsIdentity.GetCurrent();
                account = current.Name; expected = current.User!.Value;
            }
            var result = await RunSqlCertificateFunctionsAsync(root, $$"""
                function Get-ItemProperty($LiteralPath) { return [pscustomobject]@{ ObjectName = '{{PsTestPath(account)}}' } }
                $identity = Get-MCPanelSqlServiceSid
                if ($identity.Value -ne '{{expected}}') { throw ('Wrong service identity: ' + $identity.Value) }
                """);
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow("restart-failure")]
    [DataRow("missing-previous")]
    [DataRow("stopped")]
    [DataRow("running")]
    public async Task SqlCertificateBindingPreservesEncryptionAndRestoresAfterRestartFailure(string scenario)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var result = await RunSqlCertificateFunctionsAsync(root, $$"""
                $scenario = '{{scenario}}'
                $script:settings = [pscustomobject]@{ ForceEncryption = 1 }
                if ($scenario -ne 'missing-previous') { $script:settings | Add-Member NoteProperty Certificate 'original-thumbprint' }
                $script:running = $scenario -ne 'stopped'
                $script:restarts = 0
                function Get-ItemProperty($LiteralPath) {
                    # Registry reads return a detached snapshot, not a live view.
                    $copy = [pscustomobject]@{ ForceEncryption = $script:settings.ForceEncryption }
                    if ($script:settings.PSObject.Properties['Certificate']) { $copy | Add-Member NoteProperty Certificate $script:settings.Certificate }
                    return $copy
                }
                function Set-ItemProperty($LiteralPath, $Name, $Value) {
                    if ($Name -ne 'Certificate') { throw 'Encryption policy must not be changed.' }
                    $script:settings | Add-Member NoteProperty Certificate $Value -Force
                }
                function Remove-ItemProperty($LiteralPath, $Name) { $script:settings.PSObject.Properties.Remove($Name) }
                function Get-Service($Name) {
                    $value = [pscustomobject]@{ Status = $(if ($script:running) { 'Running' } else { 'Stopped' }) }
                    $value | Add-Member ScriptMethod WaitForStatus { param($state, $timeout) if (-not $script:running) { throw 'Not running' } }
                    return $value
                }
                function Restart-Service {
                    $script:restarts++
                    if ($script:restarts -eq 1 -and $scenario -in @('restart-failure', 'missing-previous')) {
                        $script:running = $false
                        throw 'injected certificate restart failure'
                    }
                    $script:running = $true
                }
                $failure = $null
                try { Set-MCPanelSqlCertificateBinding 'mock-registry' 'new-thumbprint' } catch { $failure = $_ }
                if ($script:settings.ForceEncryption -ne 1) { throw 'Encryption policy changed.' }
                if ($scenario -in @('restart-failure', 'missing-previous')) {
                    if (-not $failure -or $script:restarts -ne 2 -or -not $script:running) { throw 'Failed to recover original running service.' }
                    if ($scenario -eq 'missing-previous') {
                        if ($script:settings.PSObject.Properties['Certificate']) { throw 'Originally absent binding was not removed.' }
                    }
                    elseif ($script:settings.Certificate -ne 'original-thumbprint') { throw 'Original certificate binding lost.' }
                }
                else {
                    if ($failure) { throw $failure }
                    if ($script:settings.Certificate -ne 'new-thumbprint') { throw 'New binding was not applied.' }
                    if ($scenario -eq 'stopped' -and ($script:restarts -ne 0 -or $script:running)) { throw 'Stopped service was unexpectedly started.' }
                    if ($scenario -eq 'running' -and $script:restarts -ne 1) { throw 'Running service was not restarted once.' }
                }
                """);
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }
}
