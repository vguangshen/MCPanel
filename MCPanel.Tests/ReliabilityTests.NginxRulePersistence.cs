using System;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void NginxDialogMarksEditedGeneratedProductRuleAsPersistentOverride()
    {
        var original = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品 DS0101",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = "/DS0101",
            ProxyTarget = "http://127.0.0.1:8088/DS0101",
            WebSocket = true,
            ManagedProductId = "DS0101"
        };

        var untouched = NginxProxyDialog.NginxProxyRuleRow.FromRule(original).ToRule();
        Assert.AreEqual("DS0101", untouched.ManagedProductId, "未编辑的自动规则必须继续由部署状态管理。");

        var editedRow = NginxProxyDialog.NginxProxyRuleRow.FromRule(original);
        editedRow.ProxyTarget = "http://127.0.0.1:9090/DS0101";
        var edited = editedRow.ToRule();

        Assert.IsTrue(NginxProductProxyService.TryParseManagedProductId(
            edited.ManagedProductId,
            out var productId,
            out var ownership));
        Assert.AreEqual("DS0101", productId);
        Assert.AreEqual(NginxProductProxyService.ManagedProductRuleKind.UserOverride, ownership);
        Assert.AreEqual("http://127.0.0.1:9090/DS0101", edited.ProxyTarget);
    }

    [TestMethod]
    public void NginxDialogDisableOfGeneratedRuleAlsoBecomesPersistentOverride()
    {
        var row = NginxProxyDialog.NginxProxyRuleRow.FromRule(new NginxProxyRule
        {
            Enabled = true,
            Name = "产品 DS0105",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = "/DS0105",
            ProxyTarget = "http://127.0.0.1:8088/DS0105",
            WebSocket = true,
            ManagedProductId = "DS0105"
        });

        row.Enabled = false;
        var saved = row.ToRule();

        Assert.IsFalse(saved.Enabled);
        Assert.IsTrue(NginxProductProxyService.TryParseManagedProductId(
            saved.ManagedProductId,
            out var productId,
            out var ownership));
        Assert.AreEqual("DS0105", productId);
        Assert.AreEqual(NginxProductProxyService.ManagedProductRuleKind.UserOverride, ownership);
    }

    [TestMethod]
    public void NginxProductSyncPreservesOverridesSuppressionsAndWebsiteRules()
    {
        var manual = NginxRuntimeManager.CreateDefaultRule(72, "http://127.0.0.1:9287", true);
        var website = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品域名 DS0101",
            ListenPort = 80,
            ServerName = "example.test",
            LocationPath = "/",
            ProxyTarget = "http://127.0.0.1:8088/DS0101/",
            WebSocket = true,
            ManagedWebsiteId = "product-domain:DS0101"
        };
        var userOverride = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品 DS0101",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = "/DS0101",
            ProxyTarget = "http://127.0.0.1:9090/DS0101",
            WebSocket = false,
            ManagedProductId = NginxProductProxyService.CreateUserOverrideId("DS0101")
        };
        var suppressed = new NginxProxyRule
        {
            Enabled = false,
            Name = "产品 DS0105",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = "/DS0105",
            ProxyTarget = "http://127.0.0.1:8088/DS0105",
            WebSocket = true,
            ManagedProductId = NginxProductProxyService.CreateSuppressedId("DS0105")
        };
        var staleOverride = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品 OLD",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = "/OLD",
            ProxyTarget = "http://127.0.0.1:9999/OLD",
            ManagedProductId = NginxProductProxyService.CreateUserOverrideId("OLD")
        };

        var generated = new[]
        {
            Generated("DS0101", 8088),
            Generated("DS0105", 8088),
            Generated("DS0107", 8088)
        };

        var merged = NginxProductProxyService.MergeProductRules(
            new[] { manual, website, userOverride, suppressed, staleOverride },
            generated);

        var ds0101 = FindManaged(merged, "DS0101");
        Assert.AreEqual("http://127.0.0.1:9090/DS0101", ds0101.ProxyTarget);
        Assert.IsFalse(ds0101.WebSocket);
        AssertManagedKind(ds0101, NginxProductProxyService.ManagedProductRuleKind.UserOverride);

        var ds0105 = FindManaged(merged, "DS0105");
        Assert.IsFalse(ds0105.Enabled, "删除自动规则后必须保留隐藏的抑制标记，不能被同步重新创建。");
        AssertManagedKind(ds0105, NginxProductProxyService.ManagedProductRuleKind.Suppressed);

        var ds0107 = FindManaged(merged, "DS0107");
        Assert.AreEqual("http://127.0.0.1:8088/DS0107", ds0107.ProxyTarget);
        AssertManagedKind(ds0107, NginxProductProxyService.ManagedProductRuleKind.Automatic);

        Assert.IsTrue(merged.Any(rule => rule.ManagedWebsiteId == "product-domain:DS0101"),
            "产品域名/SSL 规则属于独立用户配置，产品路由同步不得覆盖。 ");
        Assert.IsFalse(merged.Any(rule =>
            NginxProductProxyService.TryParseManagedProductId(rule.ManagedProductId, out var id, out _) &&
            string.Equals(id, "OLD", StringComparison.OrdinalIgnoreCase)),
            "产品卸载后遗留的用户覆盖标记必须自动清理。");
    }

    [TestMethod]
    public void NginxSingleProductPortOverrideDoesNotMoveOtherGeneratedRules()
    {
        var overridden = new NginxProxyRule
        {
            Enabled = true,
            Name = "产品 DS0101",
            ListenPort = 9001,
            ServerName = "localhost",
            LocationPath = "/DS0101",
            ProxyTarget = "http://127.0.0.1:9090/DS0101",
            WebSocket = true,
            ManagedProductId = NginxProductProxyService.CreateUserOverrideId("DS0101")
        };
        var untouched = Generated("DS0107", 8088);
        untouched.ListenPort = 72;
        var current = new NginxRuntimeOptions
        {
            ListenPort = 9001,
            ProxyEnabled = true,
            ProxyTarget = overridden.ProxyTarget,
            Rules = new() { overridden, untouched }
        };

        var defaultPort = NginxProductProxyService.ResolveDefaultPublicPort(current);
        Assert.AreEqual(72, defaultPort, "单条覆盖规则的端口不能变成所有自动规则的新默认端口。");

        var generated = NginxProductProxyService.BuildProductRules(
            defaultPort,
            current.Rules,
            new[]
            {
                new NginxProductProxyService.ProductRoute("DS0101", "/DS0101", 8088),
                new NginxProductProxyService.ProductRoute("DS0107", "/DS0107", 8088),
                new NginxProductProxyService.ProductRoute("DS0109", "/DS0109", 8088)
            });
        var merged = NginxProductProxyService.MergeProductRules(current.Rules, generated);

        Assert.AreEqual(9001, FindManaged(merged, "DS0101").ListenPort);
        Assert.AreEqual(72, FindManaged(merged, "DS0107").ListenPort);
        Assert.AreEqual(72, FindManaged(merged, "DS0109").ListenPort);
    }

    private static NginxProxyRule Generated(string productId, int backendPort)
    {
        return NginxRuntimeManager.NormalizeRule(new NginxProxyRule
        {
            Enabled = true,
            Name = $"产品 {productId}",
            ListenPort = 72,
            ServerName = "localhost",
            LocationPath = $"/{productId}",
            ProxyTarget = $"http://127.0.0.1:{backendPort}/{productId}",
            WebSocket = true,
            ManagedProductId = productId
        });
    }

    private static NginxProxyRule FindManaged(System.Collections.Generic.IEnumerable<NginxProxyRule> rules, string productId)
    {
        return rules.Single(rule =>
            NginxProductProxyService.TryParseManagedProductId(rule.ManagedProductId, out var id, out _) &&
            string.Equals(id, productId, StringComparison.OrdinalIgnoreCase));
    }

    private static void AssertManagedKind(
        NginxProxyRule rule,
        NginxProductProxyService.ManagedProductRuleKind expected)
    {
        Assert.IsTrue(NginxProductProxyService.TryParseManagedProductId(
            rule.ManagedProductId,
            out _,
            out var actual));
        Assert.AreEqual(expected, actual);
    }
}
