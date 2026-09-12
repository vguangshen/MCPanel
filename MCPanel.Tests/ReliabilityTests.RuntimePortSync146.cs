using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void RuntimePortSync146_IisBindingParserTracksManualHttpPort()
    {
        Assert.AreEqual(8872, ProductDeploymentService.ParseIisHttpBindingPort("http/*:8872:"));
        Assert.AreEqual(8872, ProductDeploymentService.ParseIisHttpBindingPort("https/*:443:,http/*:8872:"));
        Assert.AreEqual(9080, ProductDeploymentService.ParseIisHttpBindingPort("http/127.0.0.1:9080:,https/*:443:"));
        Assert.IsNull(ProductDeploymentService.ParseIisHttpBindingPort("https/*:443:"));
    }

    [TestMethod]
    public void RuntimePortSync146_TomcatManualPortOutsideAutoPoolIsRuntimeTruth()
    {
        Assert.IsTrue(ProductDeploymentService.IsValidObservedRuntimePort(8085));
        Assert.IsFalse(ProductDeploymentService.IsValidTomcatProductPort(8085));
        Assert.AreEqual(8085,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(9000, 8085, Array.Empty<int>()));
        Assert.AreEqual(8085,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(8085, null, Array.Empty<int>()));
        Assert.AreEqual(9001,
            ProductDeploymentService.SelectCanonicalTomcatRuntimePort(0, null, new[] { 9001 }));
    }

    [TestMethod]
    public void RuntimePortSync146_ProductDomainRuleFollowsBackendWithoutLosingFrontendSettings()
    {
        var source = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品域名 YX030101",
            ListenPort = 80,
            ServerName = "demo.example.com",
            LocationPath = "/",
            ProxyTarget = "http://127.0.0.1:9000/YX030101/",
            WebSocket = true,
            ManagedWebsiteId = "product-domain:YX030101",
            SslEnabled = true,
            HttpsPort = 443,
            SslCertificatePath = @"C:\cert\fullchain.pem",
            SslCertificateKeyPath = @"C:\cert\key.pem",
            RedirectHttpToHttps = true,
            MaxRateKbps = 2048
        };
        var route = new NginxProductProxyService.ProductRoute("YX030101", "/YX030101", 9123);

        var updated = NginxProductProxyService.RebindManagedWebsiteTargets(new[] { source }, new[] { route }).Single();

        Assert.AreEqual("http://127.0.0.1:9123/YX030101/", updated.ProxyTarget);
        Assert.AreEqual(source.ListenPort, updated.ListenPort);
        Assert.AreEqual(source.ServerName, updated.ServerName);
        Assert.AreEqual(source.LocationPath, updated.LocationPath);
        Assert.AreEqual(source.SslEnabled, updated.SslEnabled);
        Assert.AreEqual(source.HttpsPort, updated.HttpsPort);
        Assert.AreEqual(source.SslCertificatePath, updated.SslCertificatePath);
        Assert.AreEqual(source.SslCertificateKeyPath, updated.SslCertificateKeyPath);
        Assert.AreEqual(source.RedirectHttpToHttps, updated.RedirectHttpToHttps);
        Assert.AreEqual(source.MaxRateKbps, updated.MaxRateKbps);
    }

    [TestMethod]
    public void RuntimePortSync146_OrdinaryAdminRuleIsNeverRebound()
    {
        var source = new NginxProxyRule
        {
            Name = "管理员自定义规则",
            ListenPort = 8872,
            LocationPath = "/YX030101",
            ProxyTarget = "http://127.0.0.1:7777/custom",
            ManagedWebsiteId = null,
            ManagedProductId = null
        };
        var route = new NginxProductProxyService.ProductRoute("YX030101", "/YX030101", 9123);

        var updated = NginxProductProxyService.RebindManagedWebsiteTargets(new[] { source }, new[] { route }).Single();
        Assert.AreEqual("http://127.0.0.1:7777/custom", updated.ProxyTarget);
    }
}