using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatDebug_RuntimeStatus_UsesDistinctProfessionalModes()
    {
        Assert.AreEqual(
            "总 Tomcat 运行",
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Shared, 10081, true)));
        StringAssert.StartsWith(
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Independent, 10081, true, 1234)),
            "独立运行 · PID 1234");
        StringAssert.StartsWith(
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.Catalina, 10081, true, 5678)),
            "Catalina 运行 · PID 5678");
        Assert.AreEqual(
            "端口 10081 被其他进程占用",
            TomcatProductInstanceManager.FormatRuntimeStatus(
                new TomcatProductRuntimeInfo(TomcatProductRuntimeMode.PortConflict, 10081, true)));
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
    private static string ReadRepositoryFile(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }

        Assert.Fail($"Unable to locate repository file: {relativePath}");
        return string.Empty;
    }
}
