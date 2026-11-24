using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
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
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Width = 1280;
                window.Height = 820;
                window.Show();
                ((FrameworkElement)window.FindName("HomePage")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("ProductsPage")).Visibility = Visibility.Visible;
                window.UpdateLayout();

                var host = (Grid)window.FindName("DownloadQueueToolbarHost");
                var ring = (CircularProgress)window.FindName("DownloadQueueProgressRing");
                var button = (Button)window.FindName("DownloadQueueButton");
                // The ring only shows while a queue is active; show it to measure its real layout.
                ring.Visibility = Visibility.Visible;
                window.UpdateLayout();

                Assert.AreEqual(52d, host.ActualHeight, 0.5d, "工具栏宿主必须比进度环更高，避免再次裁切环形进度。");
                Assert.IsFalse(host.ClipToBounds);
                Assert.AreEqual(50d, ring.ActualWidth, 0.5d);
                Assert.AreEqual(50d, ring.ActualHeight, 0.5d);
                Assert.AreEqual(2d, ring.StrokeThickness, 0.001d);
                Assert.IsTrue(ring.ActualHeight > button.ActualHeight, "进度环必须明显大于下载按钮，才能保持独立的视觉层级。");
                Assert.IsTrue(ring.ActualHeight < host.ActualHeight);

                // The numeric badge inherits the button font, not the icon font.
                var badge = FindVisualChildren<TextBlock>(button).Single(text => text.Text != null && text.Text.All(char.IsDigit));
                StringAssert.StartsWith(badge.FontFamily.Source, "Segoe UI");

                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(1280, 820, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "audit"));
                Directory.CreateDirectory(output);
                using var stream = File.Create(Path.Combine(output, "download-queue-toolbar.png"));
                encoder.Save(stream);
            }
            finally
            {
                window.Close();
            }
        });
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
