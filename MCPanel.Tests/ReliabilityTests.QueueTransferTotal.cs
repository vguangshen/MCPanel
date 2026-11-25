using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private const long FiveGigabytes = 5L * 1024 * 1024 * 1024 + 512L * 1024 * 1024; // 5.5 GB

    private static ProductInstallWorkerRequest TransferRequest(string id) =>
        new(id, "大文件产品", "在线", string.Empty, ProductSource.Online,
            null, "Tomcat", null, "Java", null, null, true, null, null, false);

    [TestMethod]
    public void TransfersBeyondTwoGigabytesKeepCountingThroughTheProgressFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // The worker writes its progress to a JSON file the panel reads back.
            var path = Path.Combine(root, "progress.json");
            JsonStateFile.Write(path, new ProductInstallWorkerProgress("running", 40, "正在扫描并下载",
                InstallProgressStage.Downloading, 40, "12.0 MB/s", FiveGigabytes, null, ScannedFiles: 9000, ScannedBytes: FiveGigabytes));
            Assert.IsTrue(JsonStateFile.TryRead<ProductInstallWorkerProgress>(path, out var read));
            Assert.AreEqual(FiveGigabytes, read.BytesReceived);

            var item = new ProductInstallQueueItemViewModel("big", 1, TransferRequest("BIG01"),
                ProductInstallQueueStatus.Running, 0, "正在下载", requestedAtUtc: DateTime.UtcNow.AddHours(-2));
            item.ApplyProgress(read);
            StringAssert.Contains(item.TransferText, "已下载 5.5 GB", "Nothing may cap the display near 2 GB.");
            Assert.AreEqual(FiveGigabytes, item.TransferredBytes);
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void FinishedRecordsShowTheTotalTransferredAndNeverShrinkIt()
    {
        var item = new ProductInstallQueueItemViewModel("total", 1, TransferRequest("TOT01"),
            ProductInstallQueueStatus.Running, 0, "正在下载", requestedAtUtc: DateTime.UtcNow.AddMinutes(-90));
        item.ApplyProgress(new ProductInstallWorkerProgress("running", 50, "下载", InstallProgressStage.Downloading, 50, null, FiveGigabytes));
        // A later report with a lower counter (for example an install-stage update) keeps the peak.
        item.ApplyProgress(new ProductInstallWorkerProgress("running", 95, "部署", InstallProgressStage.Installing, 95, null, 1024));
        item.SetState(ProductInstallQueueStatus.Completed);

        Assert.AreEqual(FiveGigabytes, item.TransferredBytes);
        Assert.IsTrue(item.HistoryFooterText.StartsWith("共传输 5.5 GB · 用时 ", StringComparison.Ordinal), item.HistoryFooterText);
        StringAssert.Contains(item.HistoryFooterText, "部署完成");

        var untransferred = new ProductInstallQueueItemViewModel("none", 2, TransferRequest("NON01"),
            ProductInstallQueueStatus.Cancelled, 0, "已取消", DateTime.UtcNow, DateTime.UtcNow.AddMinutes(-1));
        Assert.AreEqual(untransferred.CompletionTimeText, untransferred.HistoryFooterText,
            "Records that transferred nothing keep the plain timing line.");
    }

    [TestMethod]
    public void TransferTotalSurvivesARestartAndOlderRecordsReadItFromTheProgressFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // Only files inside the queue work folder are read back, as in a real install.
            var progressPath = Path.Combine(root, "Work", "ProductInstallQueue", "previous-session", "older", "progress.json");
            Directory.CreateDirectory(Path.GetDirectoryName(progressPath)!);
            JsonStateFile.Write(progressPath, new ProductInstallWorkerProgress("completed", 100, "完成",
                InstallProgressStage.Completed, 100, null, 3L * 1024 * 1024 * 1024));
            var completed = DateTime.UtcNow.AddMinutes(-5);
            var document = new ProductInstallQueueDocument(1, "previous-session", new List<ProductInstallQueuePersistedItem>
            {
                new("saved", 1, TransferRequest("SAV01"), ProductInstallQueueStatus.Completed, 100, "已完成", null, null, null)
                    { CompletedAtUtc = completed, RequestedAtUtc = completed.AddMinutes(-40), TransferredBytes = FiveGigabytes },
                new("older", 2, TransferRequest("OLD01"), ProductInstallQueueStatus.Completed, 100, "已完成", progressPath, null, null)
                    { CompletedAtUtc = completed, RequestedAtUtc = completed.AddMinutes(-20) }
            });
            var statePath = Path.Combine(root, "ProductState", "install-queue.json");
            Directory.CreateDirectory(Path.GetDirectoryName(statePath)!);
            File.WriteAllText(statePath, JsonSerializer.Serialize(document));

            var model = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(model, root, autoStartWorker: false);
            var saved = model.CompletedQueueItems.Single(item => item.ProductId == "SAV01");
            var older = model.CompletedQueueItems.Single(item => item.ProductId == "OLD01");
            StringAssert.StartsWith(saved.HistoryFooterText, "共传输 5.5 GB · ");
            StringAssert.StartsWith(older.HistoryFooterText, "共传输 3 GB · ");
        }
        finally { DeleteTemporaryTree(root); }
    }
}
