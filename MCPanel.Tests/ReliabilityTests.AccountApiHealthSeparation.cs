using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public async Task AccountApiLivenessDoesNotProbeDatabasesAndLegacyHealthKeepsItsContract()
    {
        var port = ReserveFreeTcpPort();
        var options = CreateAccountApiTestOptions(port);
        options.Systems["broken"] = new SystemProfile { Enabled = true, DisplayName = "Invalid fixture", ConnectionString = "" };
        using var host = new AccountApiHost(options);
        host.Start();
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var config = new AccountApiConfiguration { Port = port, SigningSecret = options.Authentication.SigningSecret };
        using (var request = AccountApiManagerService.CreateSignedRequest(config, "/health/live"))
        using (var response = await client.SendAsync(request))
        {
            Assert.AreEqual(HttpStatusCode.OK, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("not_checked", json.RootElement.GetProperty("status").GetString());
        }
        using (var request = AccountApiManagerService.CreateSignedRequest(config, "/health"))
        using (var response = await client.SendAsync(request))
        {
            Assert.AreEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            Assert.AreEqual("degraded", json.RootElement.GetProperty("status").GetString());
            Assert.IsFalse(json.RootElement.GetProperty("systems").GetProperty("broken").GetProperty("ok").GetBoolean());
        }
    }

    [TestMethod]
    public void AccountApiImportRejectsRunningListenerWithoutChangingConfiguration()
    {
        var root = CreateTemporaryDirectory();
        using var manager = new AccountApiManagerService();
        var before = File.ReadAllBytes(AccountApiStorage.ConfigPath);
        var path = Path.Combine(root, "config.ini");
        File.WriteAllText(path, BuildAccountApiTestConfig(ReserveFreeTcpPort()));
        try
        {
            EmbeddedAccountApiRuntime.Start(path);
            Assert.ThrowsException<InvalidOperationException>(() => manager.SetRuntimeDirectory(path));
            CollectionAssert.AreEqual(before, File.ReadAllBytes(AccountApiStorage.ConfigPath));
            Assert.IsTrue(EmbeddedAccountApiRuntime.IsRunning);
        }
        finally { EmbeddedAccountApiRuntime.Stop(); DeleteTemporaryTree(root); }
    }
}
