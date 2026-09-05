using System.IO;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void DownloadQueuePopupClosePolicyKeepsToolbarAndPopupClicksInside()
    {
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueuePopup(
            isOpen: false,
            toolbarButtonHovered: false,
            popupHovered: false));
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueuePopup(
            isOpen: true,
            toolbarButtonHovered: true,
            popupHovered: false));
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueuePopup(
            isOpen: true,
            toolbarButtonHovered: false,
            popupHovered: true));
        Assert.IsTrue(MainWindow.ShouldCloseDownloadQueuePopup(
            isOpen: true,
            toolbarButtonHovered: false,
            popupHovered: false));
    }

    [TestMethod]
    public void DownloadQueueToolbarLeavesRoomForRingAndUsesNormalBadgeFont()
    {
        Assert.AreEqual(52d, MainWindow.DownloadQueueToolbarHostHeight, 0.001d);
        Assert.AreEqual(50d, MainWindow.DownloadQueueProgressRingSize, 0.001d);
        Assert.AreEqual(2d, MainWindow.DownloadQueueProgressRingStroke, 0.001d);
        Assert.IsTrue(
            MainWindow.DownloadQueueToolbarHostHeight > MainWindow.DownloadQueueProgressRingSize,
            "工具栏宿主必须比进度环更高，避免再次裁切环形进度。 ");
        Assert.IsTrue(
            MainWindow.DownloadQueueProgressRingSize > 40d,
            "进度环必须明显大于 40px 下载按钮，才能保持独立的视觉层级。");

        var source = ReadRepositoryFile("MainWindow.DownloadQueueFix.cs");
        StringAssert.Contains(source, "PreviewMouseLeftButtonDown -= MainWindow_PreviewMouseLeftButtonDown;");
        StringAssert.Contains(source, "popupChild.IsMouseOver");
        StringAssert.Contains(source, "new FontFamily(\"Segoe UI, Microsoft YaHei UI\")");
        StringAssert.Contains(source, "ring.StrokeThickness = DownloadQueueProgressRingStroke;");
    }

    [TestMethod]
    public void RemovingLastPendingQueueItemLeavesCoherentEmptyPopupState()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(
                progress,
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-EMPTY-STATE", "立即删除测试", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);

            Assert.IsTrue(progress.HasQueueItems);
            Assert.IsTrue(progress.HasActiveQueue);
            Assert.AreEqual(Visibility.Collapsed, progress.QueueEmptyVisibility);
            Assert.AreEqual(Visibility.Visible, progress.QueueItemsVisibility);

            Assert.IsTrue(queue.Remove(item.QueueId));

            Assert.AreEqual(0, queue.Items.Count);
            Assert.IsFalse(progress.HasQueueItems);
            Assert.IsFalse(progress.HasActiveQueue);
            Assert.AreEqual(0, progress.ActiveQueueCount);
            Assert.AreEqual(Visibility.Visible, progress.QueueEmptyVisibility);
            Assert.AreEqual(Visibility.Collapsed, progress.QueueItemsVisibility);
Assert.AreEqual("当前没有下载或安装任务", progress.QueueSummaryText);
Assert.IsTrue(progress.ShowActiveQueue);
Assert.IsFalse(progress.ShowCompletedQueue);
Assert.AreEqual(0, progress.CompletedQueueCount);
Assert.AreEqual(Visibility.Visible, progress.ActiveQueueEmptyVisibility);
Assert.AreEqual(Visibility.Collapsed, progress.ActiveQueueItemsVisibility);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
