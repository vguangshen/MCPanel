
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
        var queue = ReadDownloadQueueTabsSource("ProductInstallQueue.cs");

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
        StringAssert.Contains(templates, "Text=\"{Binding CompletionTimeText}\"");
        StringAssert.Contains(templates, "Text=\"{Binding DownloadTimeText}\"");
        StringAssert.Contains(queue, "public DateTime? CompletedAtUtc => _completedAtUtc;");
        StringAssert.Contains(queue, "public DateTime? RequestedAtUtc => _requestedAtUtc;");
        StringAssert.Contains(queue, "_completedAtUtc = DateTime.UtcNow;");
        StringAssert.Contains(queue, "CompletedAtUtc = item.CompletedAtUtc");
        StringAssert.Contains(queue, "RequestedAtUtc = item.RequestedAtUtc");
        StringAssert.Contains(queue, "requestedAtUtc: DateTime.UtcNow");
    }

    [TestMethod]
    public void DownloadQueueTimingShowsRequestTimeAndElapsedDuration()
    {
        var completedUtc = new DateTime(2026, 9, 5, 17, 30, 0, DateTimeKind.Utc);
        var requestedUtc = completedUtc.AddMinutes(-7).AddSeconds(-25);
        var request = new ProductInstallWorkerRequest(
            "TIME001",
            "下载时间测试软件",
            "在线",
            string.Empty,
            ProductSource.Online,
            null,
            null,
            null,
            null,
            null,
            null,
            false,
            null,
            null,
            false);
        var restored = new ProductInstallQueueItemViewModel(
            "time-restored",
            1,
            request,
            ProductInstallQueueStatus.Completed,
            100d,
            "产品文件与运行服务已处理完成。",
            completedUtc,
            requestedUtc);

        Assert.AreEqual(requestedUtc, restored.RequestedAtUtc);
        Assert.AreEqual(completedUtc, restored.CompletedAtUtc);
        StringAssert.Contains(restored.DownloadTimeText, requestedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Assert.IsTrue(restored.CompletionTimeText.StartsWith("用时 7分25秒 · 部署完成 · ", StringComparison.Ordinal));
        StringAssert.Contains(restored.CompletionTimeText, completedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));

        var active = new ProductInstallQueueItemViewModel(
            "time-active",
            2,
            request,
            ProductInstallQueueStatus.Pending,
            0d,
            "等待处理",
            requestedAtUtc: requestedUtc);
        Assert.AreEqual(requestedUtc, active.RequestedAtUtc);
        StringAssert.Contains(active.DownloadTimeText, requestedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm"));
        Assert.AreEqual(string.Empty, active.CompletionTimeText);

        var live = new ProductInstallQueueItemViewModel(
            "time-live",
            3,
            request,
            ProductInstallQueueStatus.Pending,
            0d,
            "等待处理",
            requestedAtUtc: DateTime.UtcNow.AddSeconds(-2));
        var before = DateTime.UtcNow.AddSeconds(-1);
        live.SetState(ProductInstallQueueStatus.Completed);
        Assert.IsNotNull(live.CompletedAtUtc);
        Assert.IsTrue(live.CompletedAtUtc >= before && live.CompletedAtUtc <= DateTime.UtcNow.AddSeconds(1));
        Assert.IsTrue(live.CompletionTimeText.StartsWith("用时 ", StringComparison.Ordinal));
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
