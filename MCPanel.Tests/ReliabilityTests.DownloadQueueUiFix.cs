using System.IO;
using System.Windows;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void DownloadQueueFlyoutClosePolicyKeepsToolbarAndFlyoutClicksInside()
    {
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueueFlyout(
            isOpen: false,
            toolbarButtonHovered: false,
            flyoutHovered: false));
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueueFlyout(
            isOpen: true,
            toolbarButtonHovered: true,
            flyoutHovered: false));
        Assert.IsFalse(MainWindow.ShouldCloseDownloadQueueFlyout(
            isOpen: true,
            toolbarButtonHovered: false,
            flyoutHovered: true));
        Assert.IsTrue(MainWindow.ShouldCloseDownloadQueueFlyout(
            isOpen: true,
            toolbarButtonHovered: false,
            flyoutHovered: false));
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
        Assert.IsFalse(source.Contains("DownloadQueuePopup"), "工具栏修复层不应再依赖独立 WPF Popup。");
        StringAssert.Contains(source, "ShouldCloseDownloadQueueFlyout");
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

    [TestMethod]
    public void DownloadQueueUsesInWindowFlyoutInsteadOfNativePopup()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var products = ReadRepositoryFile("MainWindow.Products.cs");
        var main = ReadRepositoryFile("MainWindow.xaml.cs");

        StringAssert.Contains(xaml, "x:Name=\"DownloadQueueFlyoutLayer\"");
        StringAssert.Contains(xaml, "x:Name=\"DownloadQueueFlyoutCard\"");
        StringAssert.Contains(xaml, "Panel.ZIndex=\"200\"");
        StringAssert.Contains(xaml, "ClipToBounds=\"True\"");
        Assert.IsFalse(xaml.Contains("<Popup x:Name=\"DownloadQueuePopup\""),
            "下载队列必须留在 MainWindow 视觉树内，不能再创建独立 HWND Popup。");
        StringAssert.Contains(products, "DownloadQueueFlyoutLayer.Visibility = Visibility.Visible;");
        StringAssert.Contains(products, "DownloadQueueFlyoutCard.IsMouseOver");
        StringAssert.Contains(products, "e.Key == Key.Escape");
        Assert.IsFalse(products.Contains("DownloadQueuePopup"));
        Assert.IsFalse(main.Contains("CustomPopupPlacementCallback"));
        Assert.IsFalse(main.Contains("PlaceDownloadQueuePopup"));
    }

}
