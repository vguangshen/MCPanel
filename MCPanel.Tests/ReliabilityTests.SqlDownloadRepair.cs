using System;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow("WindowsServer2012", 14, "18.12.1")]
    [DataRow("WindowsServer2012R2", 14, "18.12.1")]
    [DataRow("Windows81", 14, "18.12.1")]
    [DataRow("WindowsServer2016", 16, "20.2.1")]
    [DataRow("Windows10", 16, "20.2.1")]
    [DataRow("WindowsServer2019", 14, "22")]
    [DataRow("Windows11", 17, "22")]
    [DataRow("WindowsServer2025", 11, "18.12.1")]
    public void SsmsRecommendationMatchesBothOperatingSystemAndSqlServer(string family, int engineMajor, string expectedRelease)
    {
        var os = (WindowsSqlCompatibilityFamily)Enum.Parse(typeof(WindowsSqlCompatibilityFamily), family);
        Assert.AreEqual(expectedRelease, SsmsReleaseCatalog.ResolveForPlatform(engineMajor, os).Id);
    }

    [TestMethod]
    public async Task RecoveryLatestManifestPinsOneReleaseAndRejectsInvalidPackageMetadata()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var recovery = Path.Combine(root, "Restore-Update.ps1");
            File.WriteAllText(recovery, ReadRepositoryFile("Restore-Update.ps1"));
            var script = Path.Combine(root, "verify-release.ps1");
            File.WriteAllText(script, """
                $ErrorActionPreference = 'Stop'
                $tokens = $null; $errors = $null
                $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Restore-Update.ps1'), [ref]$tokens, [ref]$errors)
                if ($errors.Count) { throw 'Recovery script does not parse in Windows PowerShell 5.1.' }
                $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-RecoveryRelease' }, $true)
                . ([scriptblock]::Create($function.Extent.Text))
                $manifest = @{ format='mcpanel-github-release-v1'; version='9.8.7'; packageName='MCPanel-9.8.7.zip'; sha256=('a' * 64) }
                $release = Get-RecoveryRelease ($manifest | ConvertTo-Json)
                $url = 'https://github.com/vguangshen/MCPanel/releases/download/v9.8.7/MCPanel-9.8.7.zip'
                if ($release.PackageUrl -cne $url -or $release.ChecksumUrl -cne ($url + '.sha256') -or $release.Sha256 -ne ('a' * 64)) { throw 'Recovery did not bind all assets to the manifest version.' }
                foreach ($bad in @(
                    @{ Key='packageName'; Value='../other.zip' },
                    @{ Key='packageName'; Value='MCPanel-1.3.61.zip' },
                    @{ Key='version'; Value='9.8.7/bad' },
                    @{ Key='format'; Value='unknown' },
                    @{ Key='sha256'; Value='invalid' }
                )) {
                    $copy = $manifest.Clone(); $copy[$bad.Key] = $bad.Value
                    $rejected = $false
                    try { $null = Get-RecoveryRelease ($copy | ConvertTo-Json) } catch { $rejected = $true }
                    if (!$rejected) { throw ('Recovery accepted invalid ' + $bad.Key) }
                }
                """);
            var result = await ProcessRunner.RunPowerShellFileAsync(script, false, default,
                captureOutput: true, timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void SqlDownloadSettingsRepairRetiredDefaultsWithoutReplacingCustomSources()
    {
        foreach (var migration in EnvironmentDownloadCatalog.Migrations)
        {
            var settings = new NameValueCollection { [migration.Key] = "  " + migration.PreviousUrl.ToUpperInvariant() + "  " };
            var loaded = EnvironmentDownloadSettings.Load(settings);
            var actual = migration.Key == EnvironmentDownloadSettings.SqlServer2017ExpressKey
                ? loaded.SqlServer2017ExpressUrl : loaded.SqlServer2022ExpressUrl;
            Assert.AreEqual(migration.CurrentUrl, actual, "Upgrading with the old config must immediately repair the retired default.");
            Assert.AreEqual(EnvironmentDownloadCatalog.Find(migration.Key)!.DefaultUrl, actual);

            const string custom = "https://private.example.test/sql/setup.exe?access=custom";
            settings[migration.Key] = custom;
            loaded = EnvironmentDownloadSettings.Load(settings);
            Assert.AreEqual(custom, migration.Key == EnvironmentDownloadSettings.SqlServer2017ExpressKey
                ? loaded.SqlServer2017ExpressUrl : loaded.SqlServer2022ExpressUrl);
            Assert.AreEqual(migration.PreviousUrl, EnvironmentDownloadCatalog.NormalizeUrl("Ai.Model", migration.PreviousUrl));
        }
    }

    [TestMethod]
    public async Task EnvironmentDownload404IdentifiesPackageAndBothSourcesWithoutExposingUrlCredentials()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var handler = new PublicUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://cdn.example.test/retired.exe?secret=final")
            });
            using var installer = new EnvironmentInstaller(handler);
            var error = await Assert.ThrowsExceptionAsync<HttpRequestException>(() => installer.DownloadCoreAsync(
                "https://person:password@source.example.test/package.exe?secret=initial#fragment", root,
                "SQLServer2017-SSEI-Expr.exe", _ => { }, 0, 100, default, 14));
            foreach (var expected in new[] { "404", "SQLServer2017-SSEI-Expr.exe", "https://source.example.test/package.exe", "https://cdn.example.test/retired.exe" })
                StringAssert.Contains(error.Message, expected);
            foreach (var sensitive in new[] { "person", "password", "secret", "initial", "final", "fragment" })
                Assert.IsFalse(error.Message.Contains(sensitive), error.Message);
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task SqlDownloadRejectsOfficialRedirectToDifferentReleaseBeforeSaving()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var handler = new PublicUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://download.microsoft.com/download/current/SQL2025-SSEI-Expr.exe"),
                Content = new ByteArrayContent(new byte[] { 77, 90 })
            });
            using var installer = new EnvironmentInstaller(handler);
            var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => installer.DownloadCoreAsync(
                EnvironmentDownloadSettings.DefaultSqlServer2022ExpressUrl, root, "SQL2022-SSEI-Expr.exe", _ => { }, 0, 100, default, 16));
            StringAssert.Contains(error.Message, "SQL2025-SSEI-Expr.exe");
            Assert.AreEqual(0, Directory.GetFiles(root).Length);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task SqlDownloadRejectsWrongProductVersionAndRemovesPartialFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // A real valid PE with product major 1 must not be accepted as SQL Server major 14.
            var bytes = File.ReadAllBytes(typeof(EnvironmentInstaller).Assembly.Location);
            using var handler = new PublicUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var installer = new EnvironmentInstaller(handler);
            var error = await Assert.ThrowsExceptionAsync<InvalidDataException>(() => installer.DownloadCoreAsync(
                "https://custom.example.test/setup.exe", root, "SQLServer2017-SSEI-Expr.exe", _ => { }, 0, 100, default, 14));
            StringAssert.Contains(error.Message, "版本不匹配");
            Assert.AreEqual(0, Directory.GetFiles(root).Length, "A rejected download must not become a reusable cache.");
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task VersionInvalidCacheIsReplacedAndValidCacheIsReusedWithoutAnotherRequest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(root, "fixture.exe");
            File.WriteAllBytes(target, new byte[] { 77, 90 });
            var bytes = File.ReadAllBytes(typeof(EnvironmentInstaller).Assembly.Location);
            using var handler = new PublicUpdateHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
            using var installer = new EnvironmentInstaller(handler);
            for (var attempt = 0; attempt < 2; attempt++)
                Assert.AreEqual(target, await installer.DownloadCoreAsync("https://custom.example.test/setup.exe", root,
                    "fixture.exe", _ => { }, 0, 100, CancellationToken.None, 1));
            CollectionAssert.AreEqual(bytes, File.ReadAllBytes(target));
            Assert.AreEqual(1, handler.Urls.Count);
            Assert.IsFalse(Directory.GetFiles(root).Any(file => file.EndsWith(".part", StringComparison.OrdinalIgnoreCase)));
        }
        finally { DeleteTemporaryTree(root); }
    }
}
