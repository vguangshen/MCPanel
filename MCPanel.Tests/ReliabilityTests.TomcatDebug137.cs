using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatProductStartControlsFollowTheCurrentRuntimeMode()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = new ProductItem("TEST-MODES", "测试", "测试", string.Empty, ProductSource.Local)
            {
                ExternalInstallPath = root,
                RunEnvironment = "Tomcat"
            };
            var item = new InstalledProductItem(product, root);
            Assert.IsTrue(item.IsTomcatDeployment);
            foreach (TomcatProductRuntimeMode mode in Enum.GetValues(typeof(TomcatProductRuntimeMode)))
            {
                item.ApplyTomcatRuntime(new TomcatProductRuntimeInfo(mode, 10081, mode != TomcatProductRuntimeMode.Stopped));
                Assert.AreEqual(mode is TomcatProductRuntimeMode.Stopped or TomcatProductRuntimeMode.Shared, item.CanStartTomcatProduct);
            }
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void TomcatDebug_IndependentSwitch_DoesNotUseAllProductPortsAsStopFallback()
    {
        var source = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        StringAssert.Contains(source, "await StopCatalinaBaseAsync(tomcatHome, cancellationToken, Array.Empty<int>());");
        Assert.IsFalse(source.Contains(
            "await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));"),
            "Independent mode must never kill other product Java processes merely because their ports are part of the shared server.xml.");
    }

    [TestMethod]
    public void TomcatDebug_ProductManagement_KeepsOnlyCoreStartAndLogActions()
    {
        var xaml = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
        var products = ReadRepositoryFile("MainWindow.Products.cs");
        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "ProductViewModels.cs"));

        StringAssert.Contains(xaml, "Content=\"单独启动\"");
        StringAssert.Contains(xaml, "Content=\"以 Catalina 方式启动\"");
        StringAssert.Contains(xaml, "Content=\"查看日志\"");
        Assert.IsFalse(xaml.Contains("Content=\"停止应用\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"清理缓存并重启\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"重启应用\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"实例目录\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("Content=\"清理 work/temp\"", StringComparison.Ordinal));

        Assert.IsFalse(products.Contains("\"Stop\" =>", StringComparison.Ordinal));
        Assert.IsFalse(products.Contains("ClearCacheRestart", StringComparison.Ordinal));
        Assert.IsFalse(products.Contains("ClearCacheAndRestartAsync", StringComparison.Ordinal));
        StringAssert.Contains(products, "\"Start\" => await _tomcatInstanceManager.StartAsync");
        StringAssert.Contains(products, "\"Catalina\" => await _tomcatInstanceManager.StartAsync");

        Assert.IsFalse(manager.Contains("ClearCacheAndRestartAsync(", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanRestartTomcatProduct", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanClearTomcatCache", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("CanClearCacheAndRestartTomcatProduct", StringComparison.Ordinal));

        // StopAsync remains an internal lifecycle primitive for uninstall and
        // global Tomcat management; it is no longer exposed as a product button.
        StringAssert.Contains(manager, "public async Task<string> StopAsync(");
    }
    private static string ReadRepositoryMethod(string relativePath, string signature)
    {
        var source = ReadRepositoryFile(relativePath);
        var start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.IsTrue(start >= 0, $"Unable to find method {signature} in {relativePath}.");
        var tail = source.Substring(start + signature.Length);
        var next = System.Text.RegularExpressions.Regex.Match(tail,
            @"(?m)^    (?:public|private|internal|protected)\s");
        var end = next.Success ? start + signature.Length + next.Index : source.Length;
        return source.Substring(start, end - start);
    }

    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                var text = File.ReadAllText(candidate);
                var name = Path.GetFileName(candidate);
                if (name is "EnvironmentRuntimeService.cs" or "EnvironmentInstaller.cs")
                {
                    var prefix = Path.GetFileNameWithoutExtension(candidate) + ".";
                    foreach (var partial in Directory.EnumerateFiles(directory.FullName, prefix + "*.cs").OrderBy(path => path, StringComparer.Ordinal))
                        text += Environment.NewLine + File.ReadAllText(partial);
                }
                if (name == "MainWindow.Environment.cs")
                    text += Environment.NewLine + File.ReadAllText(Path.Combine(directory.FullName, "MainWindow.ComponentActions.cs"));
                return text;
            }
            directory = directory.Parent;
        }

        Assert.Fail($"Unable to locate repository file: {relativePath}");
        return string.Empty;
    }
}
