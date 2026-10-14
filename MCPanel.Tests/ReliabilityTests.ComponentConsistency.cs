using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml;
using System.Xml.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public async Task ComponentOperationsRejectDuplicatesAndReleaseBothCardsAfterFailure()
    {
        using var model = new MainViewModel(false);
        var card = model.EnvironmentItems.Single(item => item.Kind == EnvironmentKind.Tomcat);
        var service = model.Services.Single(item => item.Kind == EnvironmentKind.Tomcat);
        var ready = new EnvironmentRuntimeState(true, false, "ready");
        card.ApplyRuntimeState(ready);
        service.ApplyRuntimeState(ready);
        var started = new TaskCompletionSource<bool>();
        var finish = new TaskCompletionSource<bool>();
        var first = model.ComponentOperations.RunAsync(EnvironmentKind.Tomcat, () => true, async () =>
        {
            started.SetResult(true);
            await finish.Task;
            throw new InvalidOperationException("simulated failure");
        });
        await started.Task;
        Assert.IsTrue(card.IsBusy && service.IsBusy);
        Assert.IsFalse(card.CanStart || service.CanStart || card.CanUninstall);
        var duplicateRan = false;
        var duplicate = await model.ComponentOperations.RunAsync(EnvironmentKind.Tomcat, () => true, () =>
        {
            duplicateRan = true;
            return Task.FromResult("duplicate");
        });
        Assert.IsNull(duplicate);
        Assert.IsFalse(duplicateRan);
        using (var independent = model.ComponentOperations.TryBegin(EnvironmentKind.Nginx))
            Assert.IsNotNull(independent, "Other components must remain usable.");
        finish.SetResult(true);
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(async () => { await first; });
        Assert.IsFalse(card.IsBusy || service.IsBusy);
        Assert.IsTrue(card.CanStart && service.CanStart);
    }

    [TestMethod]
    public async Task ComponentOperationCancellationDoesNotExecuteAndAllowsRetry()
    {
        var coordinator = new ComponentOperationCoordinator();
        var executed = 0;
        var cancelled = await coordinator.RunAsync(EnvironmentKind.Tomcat, () => false,
            () => { executed++; return Task.FromResult("started"); });
        Assert.IsNull(cancelled);
        Assert.AreEqual(0, executed);
        var retried = await coordinator.RunAsync(EnvironmentKind.Tomcat, () => true,
            () => { executed++; return Task.FromResult("started"); });
        Assert.AreEqual("started", retried);
        Assert.AreEqual(1, executed);
    }

    [TestMethod]
    public async Task OlderRuntimeSampleCannotOverwriteOperationOrNewerRefresh()
    {
        var refresh = new RuntimeRefreshCoordinator();
        var delayed = new TaskCompletionSource<string>();
        var displayed = "initial";
        var oldRefresh = refresh.RefreshAsync(() => delayed.Task, value => displayed = value);
        refresh.Invalidate(); // A component operation has started and completed.
        Assert.IsTrue(await refresh.RefreshAsync(() => Task.FromResult("running"), value => displayed = value));
        delayed.SetResult("stopped");
        Assert.IsFalse(await oldRefresh);
        Assert.AreEqual("running", displayed);
    }

    [TestMethod]
    public void ComponentProbeFailureRetainsKnownInstallationAndDoesNotBlockOtherComponents()
    {
        var snapshot = EnvironmentRuntimeService.GetSnapshot(kind =>
            kind == EnvironmentKind.MySql ? throw new IOException("unreadable credentials")
                : new EnvironmentRuntimeState(true, true, "running", RuntimeStatusKind.Running), _ => null);
        using var model = new MainViewModel(false);
        var mysql = model.EnvironmentItems.Single(item => item.Kind == EnvironmentKind.MySql);
        mysql.ApplyRuntimeState(new EnvironmentRuntimeState(true, true, "previous", RuntimeStatusKind.Running));
        model.ApplyRuntimeStates(snapshot.States, null, snapshot.InstallDirectories, probeErrors: snapshot.ProbeErrors);
        Assert.IsTrue(mysql.IsInstalled && mysql.IsRunning, "A failed probe must retain the last known installation.");
        Assert.AreEqual("无法确认", mysql.BadgeText);
        Assert.IsFalse(mysql.CanStart || mysql.CanRestart);
        Assert.IsTrue(model.EnvironmentItems.Single(item => item.Kind == EnvironmentKind.Nginx).IsRunning);
        Assert.IsTrue(snapshot.ProbeErrors!.ContainsKey(EnvironmentKind.MySql));
    }

    [TestMethod]
    public void TransitionalServiceStatesDisableConflictingControlsOnBothCards()
    {
        foreach (var status in new[] { RuntimeStatusKind.Starting, RuntimeStatusKind.Stopping })
        {
            var state = new EnvironmentRuntimeState(true, false, "transition", status);
            var card = new EnvironmentItem(EnvironmentKind.Tomcat, "Tomcat", "", "");
            var service = new ServiceItem("Tomcat", EnvironmentKind.Tomcat, "");
            card.ApplyRuntimeState(state);
            service.ApplyRuntimeState(state);
            Assert.IsFalse(card.CanStart || card.CanRestart || card.CanUninstall || card.CanStartCatalina);
            Assert.IsFalse(service.CanStart || service.CanRestart || service.CanUninstall);
        }
    }

    [TestMethod]
    public async Task FrpLogCollectionFindsCurrentAndLegacyLogs()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var current = Path.Combine(root, EnvironmentComponentCatalog.FrpDirectoryName, "frp-service.log");
            var legacy = Path.Combine(root, "StoreData", "Work", "frpc-client.log");
            foreach (var path in new[] { current, legacy }) Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(current, "current service recovery");
            File.WriteAllText(legacy, "legacy client connection");
            var result = await new LogCollectorService(root).CollectAsync(AiLogTarget.Frp);
            CollectionAssert.AreEquivalent(new[] { current, legacy }, result.Files.ToArray());
            StringAssert.Contains(result.Content, "current service recovery");
            StringAssert.Contains(result.Content, "legacy client connection");
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void ServiceDescriptionsFollowDetectedDatabaseConnections()
    {
        foreach (var kind in new[] { EnvironmentKind.MySql, EnvironmentKind.SqlServer })
        {
            var card = new ServiceItem("database", kind, "connection pending");
            card.ApplyRuntimeState(new EnvironmentRuntimeState(true, true, "ready", RuntimeStatusKind.Running,
                ConnectionDescription: "localhost:4406 / operator"));
            Assert.AreEqual("localhost:4406 / operator", card.Description);
            card.ApplyRuntimeState(new EnvironmentRuntimeState(true, false, "stopped",
                ConnectionDescription: "localhost:5506 / operator"));
            Assert.AreEqual("localhost:5506 / operator", card.Description);
        }
    }

    [TestMethod]
    public async Task PublishingAndInAppUpdatesPreserveTheSameCustomAndEmptySettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var oldConfig = Path.Combine(root, "old.config");
            var appConfig = Path.Combine(root, "app.config");
            var publishConfig = Path.Combine(root, "publish.config");
            const string oldXml = "<configuration><appSettings><add key='Ai.Model' value='custom-model'/><add key='Ai.ApiKey' value=''/><add key='Environment.Frp.PackageUrl' value='https://example.test/frp.zip'/><add key='Internal.Build' value='old'/></appSettings></configuration>";
            const string newXml = "<configuration><appSettings><add key='Ai.Model' value='new-default'/><add key='Ai.ApiKey' value='new-default-key'/><add key='Environment.Frp.PackageUrl' value='https://example.test/default.zip'/><add key='Internal.Build' value='new'/><add key='New.Feature' value='enabled'/></appSettings></configuration>";
            File.WriteAllText(oldConfig, oldXml);
            File.WriteAllText(appConfig, newXml);
            File.WriteAllText(publishConfig, newXml);
            DeploymentConfigurationSettings.Apply(appConfig, DeploymentConfigurationSettings.Capture(oldConfig));
            var helper = Path.Combine(root, "Deployment-Configuration.ps1");
            File.WriteAllText(helper, ReadRepositoryFile("Deployment-Configuration.ps1"));
            var manifest = Path.Combine(root, "deployment-layout.json");
            File.WriteAllText(manifest, ReadRepositoryFile("deployment-layout.json"));
            string Quote(string value) => "'" + value.Replace("'", "''") + "'";
            var script = Path.Combine(root, "test-publish.ps1");
            File.WriteAllText(script,
                "$ErrorActionPreference = 'Stop'\n. " + Quote(helper) + "\n" +
                "$keys = @((Get-Content -LiteralPath " + Quote(manifest) + " -Raw | ConvertFrom-Json).preservedAppSettingKeys)\n" +
                "$settings = Get-PreservedAppSettings " + Quote(oldConfig) + " $keys\n" +
                "Set-PreservedAppSettings " + Quote(publishConfig) + " $settings\n");
            var result = await ProcessRunner.RunPowerShellFileAsync(script, false, default,
                captureOutput: true, timeout: TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, result.ExitCode, result.CombinedOutput);
            Dictionary<string, string> Settings(string file) => XDocument.Load(file).Root!.Element("appSettings")!
                .Elements("add").ToDictionary(element => (string)element.Attribute("key")!, element => (string)element.Attribute("value")!);
            var inApp = Settings(appConfig);
            var published = Settings(publishConfig);
            CollectionAssert.AreEquivalent(inApp.ToArray(), published.ToArray());
            Assert.AreEqual("custom-model", inApp["Ai.Model"]);
            Assert.AreEqual("", inApp["Ai.ApiKey"], "An explicitly empty setting must remain empty.");
            Assert.AreEqual("https://example.test/frp.zip", inApp["Environment.Frp.PackageUrl"]);
            Assert.AreEqual("new", inApp["Internal.Build"]);
            Assert.AreEqual("enabled", inApp["New.Feature"]);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void UnreadableDeploymentConfigFailsBeforeOverwritingSettings()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var oldConfig = Path.Combine(root, "old.config");
            File.WriteAllText(oldConfig, "<configuration>");
            Assert.ThrowsException<XmlException>(() => DeploymentConfigurationSettings.Capture(oldConfig));
            Assert.AreEqual("<configuration>", File.ReadAllText(oldConfig));
        }
        finally { DeleteTemporaryTree(root); }
    }
}
