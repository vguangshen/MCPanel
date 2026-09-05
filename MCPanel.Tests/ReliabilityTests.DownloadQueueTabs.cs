
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void DownloadQueuePopupSeparatesActiveAndCompletedHistory()
    {
        var viewModel = ReadDownloadQueueTabsSource(Path.Combine("ViewModels", "ProductViewModels.cs"));
        var mainXaml = ReadDownloadQueueTabsSource("MainWindow.xaml");
        var products = ReadDownloadQueueTabsSource("MainWindow.Products.cs");
        var templates = ReadDownloadQueueTabsSource(Path.Combine("Resources", "MainWindowTemplates.xaml"));

        StringAssert.Contains(viewModel, "ActiveQueueItems => QueueItems");
        StringAssert.Contains(viewModel, ".Where(item => !item.IsTerminal)");
        StringAssert.Contains(viewModel, "CompletedQueueItems => QueueItems");
        StringAssert.Contains(viewModel, ".Where(item => item.IsTerminal)");
        StringAssert.Contains(viewModel, ".OrderByDescending(item => item.Sequence)");
        StringAssert.Contains(viewModel, "get => !ShowCompletedQueue;");
        StringAssert.Contains(mainXaml, "GroupName=\"DownloadQueueTabs\"");
        StringAssert.Contains(mainXaml, "Text=\"下载中\"");
        StringAssert.Contains(mainXaml, "Text=\"已完成\"");
        StringAssert.Contains(mainXaml, "ItemsSource=\"{Binding InstallationProgress.ActiveQueueItems}\"");
        StringAssert.Contains(mainXaml, "ItemsSource=\"{Binding InstallationProgress.CompletedQueueItems}\"");
        StringAssert.Contains(mainXaml, "ItemTemplate=\"{StaticResource InstallationQueueHistoryItemTemplate}\"");
        StringAssert.Contains(products, "_model.InstallationProgress.ShowCompletedQueue = false;");
        StringAssert.Contains(templates, "x:Key=\"InstallationQueueHistoryItemTemplate\"");
        StringAssert.Contains(templates, "Value=\"完成\"");
        StringAssert.Contains(templates, "Value=\"失败\"");
    }

    private static string ReadDownloadQueueTabsSource(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath);
            if (File.Exists(Path.Combine(directory.FullName, "MCPanel.csproj")) && File.Exists(candidate))
            {
                return File.ReadAllText(candidate);
            }
            directory = directory.Parent;
        }

        Assert.Fail("Unable to locate repository source: " + relativePath);
        return string.Empty;
    }
}
