using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ProductTransferOnlyShowsAuthoritativeTotals()
    {
        var uncertain = new ProductDownloadProgress(
            42, "SVN", 6L * 1024 * 1024 * 1024, 7L * 1024 * 1024 * 1024,
            "20.0 MB/s", HasReliableTotal: false);
        StringAssert.Contains(uncertain.TransferText, "已下载 6 GB");
        Assert.IsFalse(uncertain.TransferText.Contains(" / "));
        var reliable = uncertain with { HasReliableTotal = true };
        StringAssert.Contains(reliable.TransferText, "已下载 6 GB / 7 GB");
    }

    [TestMethod]
    public void QueueUsesActivityProgressForUnknownSizeSvn()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var queue = new ProductInstallQueueService(new InstallationProgressViewModel(), root, autoStartWorker: false);
            var item = queue.Enqueue(new ProductItem("PROGRESS-SVN", "SVN 产品", "测试", string.Empty, ProductSource.Online), false);
            item.SetState(ProductInstallQueueStatus.Running);
            item.ApplyProgress(new ProductInstallWorkerProgress(
                "running", 0.9, "正在扫描并下载", InstallProgressStage.Downloading, 1,
                "18.5 MB/s", 6L * 1024 * 1024 * 1024, 7L * 1024 * 1024 * 1024,
                ScannedFiles: 12846, HasReliableTotal: false));
            Assert.IsTrue(item.IsProgressIndeterminate);
            Assert.AreEqual(0d, item.DisplayDownloadProgress, 0.001);
            Assert.AreEqual("下载中", item.ProgressText);
            StringAssert.Contains(item.TransferText, "已下载 6 GB");
            StringAssert.Contains(item.TransferText, "速度 18.5 MB/s");
            StringAssert.Contains(item.TransferText, "已扫描 12,846 项");
            Assert.IsFalse(item.TransferText.Contains(" / "));

            item.ApplyProgress(new ProductInstallWorkerProgress(
                "running", 27, "HTTP 下载", InstallProgressStage.Downloading, 30,
                "25.0 MB/s", 3L * 1024 * 1024 * 1024, 10L * 1024 * 1024 * 1024,
                HasReliableTotal: true));
            Assert.IsFalse(item.IsProgressIndeterminate);
            Assert.AreEqual(30d, item.DisplayDownloadProgress, 0.001);
            Assert.AreEqual("30%", item.ProgressText);
            StringAssert.Contains(item.TransferText, "3 GB / 10 GB");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CircularProgressSupportsActivityMode()
    {
        var ring = new CircularProgress { IsIndeterminate = true };
        Assert.IsTrue(ring.IsIndeterminate);
    }
}
