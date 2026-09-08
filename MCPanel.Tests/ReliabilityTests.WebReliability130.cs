using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow("example.test:8080", 443, "https://example.test/path?a=1")]
    [DataRow("example.test:8080", 8443, "https://example.test:8443/path?a=1")]
    [DataRow("example.test", 8443, "https://example.test:8443/path?a=1")]
    [DataRow("[::1]:8080", 8443, "https://[::1]:8443/path?a=1")]
    public void WebReliabilityRedirectReplacesSourcePort(string host, int port, string expected)
    {
        var rule = CustomWebsiteService.BuildHttpsRedirectRule(port);
        var condition = rule.Element("conditions")!.Elements("add").Last();
        var match = Regex.Match(host, condition.Attribute("pattern")!.Value);
        Assert.IsTrue(match.Success);
        var url = rule.Element("action")!.Attribute("url")!.Value
            .Replace("{C:1}", match.Groups[1].Value).Replace("{REQUEST_URI}", "/path?a=1");
        Assert.AreEqual(expected, url);
        Assert.AreEqual("false", rule.Element("action")!.Attribute("appendQueryString")!.Value);
    }

    [TestMethod]
    public void WebReliabilitySiteStatusUsesSiteAndPoolNotSharedPort()
    {
        var probe = IisWebsiteStatusProbe.Parse(
            "<appcmd><SITE SITE.NAME='stopped' state='Stopped'/><SITE SITE.NAME='running' state='Started'/></appcmd>",
            "<appcmd><APPPOOL APPPOOL.NAME='MCPanel.Site.pool' state='Started'/><APPPOOL APPPOOL.NAME='MCPanel.Site.off' state='Stopped'/></appcmd>");
        Assert.IsFalse(probe.Get(new() { Name = "stopped", Id = "pool", HttpPort = 80 }).Running);
        Assert.IsTrue(probe.Get(new() { Name = "running", Id = "pool", HttpPort = 80 }).Running);
        Assert.IsFalse(probe.Get(new() { Name = "running", Id = "off", HttpPort = 80 }).Running);
        Assert.IsFalse(probe.Get(new() { Name = "missing", Id = "pool", HttpPort = 80 }).Running);
        Assert.ThrowsException<InvalidDataException>(() => IisWebsiteStatusProbe.Parse("<appcmd><ERROR/></appcmd>", "<appcmd/>"));
    }

    [TestMethod]
    public void WebReliabilityRollbackRestoresBytesAndRemovesNewFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var config = Path.Combine(root, "nginx.conf");
            var state = Path.Combine(root, "runtime.json");
            var included = Path.Combine(root, "included.conf");
            var original = new byte[] { 239, 187, 191, 65, 13, 10 };
            File.WriteAllBytes(config, original);
            File.WriteAllText(included, "original include");
            var transaction = new ConfigurationFileTransaction();
            transaction.Capture(config);
            transaction.Capture(state);
            transaction.Capture(included);
            File.WriteAllText(config, "replacement");
            File.WriteAllText(state, "new state");
            File.WriteAllText(included, "changed include");
            transaction.Rollback();
            CollectionAssert.AreEqual(original, File.ReadAllBytes(config));
            Assert.AreEqual("original include", File.ReadAllText(included));
            Assert.IsFalse(File.Exists(state));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task WebReliabilityConfigurationUpdatesSerializeAndAllowNestedSync()
    {
        var value = 0;
        var tasks = Enumerable.Range(0, 12).Select(_ => NginxConfigurationCoordinator.RunAsync(async () =>
        {
            var prior = value;
            await Task.Yield();
            await NginxConfigurationCoordinator.RunAsync(async () => { await Task.Yield(); value = prior + 1; });
        })).ToArray();
        var all = Task.WhenAll(tasks);
        Assert.AreSame(all, await Task.WhenAny(all, Task.Delay(5000)), "Nested configuration update deadlocked.");
        await all;
        Assert.AreEqual(12, value);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => NginxConfigurationCoordinator.RunAsync(
            () => Task.FromException(new InvalidOperationException("injected"))));
        await NginxConfigurationCoordinator.RunAsync(() => Task.CompletedTask);
    }

    [TestMethod]
    public async Task WebReliabilityCancelledWaitDoesNotEnterOrLeakLock()
    {
        var entered = new TaskCompletionSource<bool>();
        var release = new TaskCompletionSource<bool>();
        var first = NginxConfigurationCoordinator.RunAsync(async () => { entered.SetResult(true); await release.Task; });
        await entered.Task;
        using var cancel = new CancellationTokenSource();
        var second = NginxConfigurationCoordinator.RunAsync(() => throw new AssertFailedException("Entered cancelled update"), cancel.Token);
        cancel.Cancel();
        try
        {
            try { await second; Assert.Fail("Expected cancellation."); }
            catch (OperationCanceledException) { }
        }
        finally { release.SetResult(true); await first; }
        await NginxConfigurationCoordinator.RunAsync(() => Task.CompletedTask);
    }

    [TestMethod]
    public void WebReliabilityStaleEditorCannotOverwriteNewRules()
    {
        var options = new NginxRuntimeOptions { Rules = [NginxRuntimeManager.CreateDefaultRule()] };
        var revision = NginxConfigurationCoordinator.Revision(options);
        options.UpdatedAt = DateTimeOffset.MinValue;
        NginxConfigurationCoordinator.EnsureUnchanged(revision, options);
        options.Rules.Add(new NginxProxyRule { ServerName = "new.test", ManagedWebsiteId = "product-domain:new" });
        Assert.ThrowsException<InvalidOperationException>(() => NginxConfigurationCoordinator.EnsureUnchanged(revision, options));
    }

    [DataTestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void WebReliabilityFailedNewSiteCleanupPreservesPreexistingResources(bool preexisting)
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var definition = new CustomWebsiteDefinition { Id = "test", Name = "test", PhysicalPath = root, SslEnabled = true };
            var resultPath = Path.Combine(root, "result");
            var configure = CustomWebsiteService.BuildConfigureScript(definition, "dummy.pfx", "", resultPath)
                .Replace("Import-Module WebAdministration", "").Replace("exit 0", "return");
            var rollback = CustomWebsiteService.BuildNewSiteRollbackScript(definition)
                .Replace("Import-Module WebAdministration", "").Replace("exit 0", "return");
            // Run the actual generated scripts against an in-memory IIS model. No IIS commands or elevation.
            var script = """
                $ErrorActionPreference='Stop'
                $script:site=$null
                $script:pool=$false
                function Test-Path($Path) {
                    if ($Path -like 'IIS:\Sites\*') { return $null -ne $script:site }
                    if ($Path -like 'IIS:\AppPools\*') { return $script:pool }
                    throw "Unexpected path: $Path"
                }
                function New-WebAppPool($Name) { $script:pool=$true }
                function Set-ItemProperty { }
                function New-Website($Name,$PhysicalPath,$Port,$IPAddress,$HostHeader,$ApplicationPool) {
                    $script:site=[pscustomobject]@{Name=$Name;physicalPath=$PhysicalPath;applicationPool=$ApplicationPool}
                }
                function New-WebBinding { }
                function Import-PfxCertificate { throw 'injected certificate failure' }
                function ConvertTo-SecureString { return [Security.SecureString]::new() }
                function Get-Website { if ($script:site) { $script:site } }
                function Get-WebApplication { }
                function Remove-Website { $script:site=$null }
                function Remove-WebAppPool { $script:pool=$false }
                """;
            script += "\nfunction Configure-Test {\n" + configure + "\n}\nfunction Rollback-Test {\n" + rollback + "\n}\n";
            if (preexisting)
                script += "$script:site=[pscustomobject]@{Name='test';physicalPath='untouched';applicationPool='external'}\n$script:pool=$true\n";
            script += "try { Configure-Test; throw 'Expected failure' } catch { $failure=$_.Exception.Message }\n";
            var marker = resultPath.Replace("'", "''") + ".created";
            script += preexisting
                ? $"if ([IO.File]::Exists('{marker}')) {{ throw 'Unowned cleanup marker' }}\nif ($script:site.physicalPath -ne 'untouched' -or -not $script:pool) {{ throw 'Changed external resources' }}\n"
                : $"if ($failure -ne 'injected certificate failure') {{ throw $failure }}\nif (-not [IO.File]::Exists('{marker}')) {{ throw 'Missing cleanup marker' }}\nRollback-Test\nif ($script:site -or $script:pool) {{ throw 'Orphaned resources' }}\n";
            var scriptPath = Path.Combine(root, "test.ps1");
            File.WriteAllText(scriptPath, script, new UTF8Encoding(true));
            var result = ProcessRunner.RunSynchronously("powershell.exe",
                "-NoProfile -ExecutionPolicy Bypass -File " + Compat.QuoteCommandLineArgument(scriptPath),
                captureOutput: true, timeout: TimeSpan.FromSeconds(20));
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            Assert.IsTrue(File.Exists(scriptPath), "Business directory must be preserved.");
        }
        finally { DeleteTemporaryTree(root); }
    }
}
