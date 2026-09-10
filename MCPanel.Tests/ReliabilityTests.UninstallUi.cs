using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void UninstallWorkRunsOffUiThreadBeforeReadingOrDeletingFiles()
    {
        RunTraySta(() =>
        {
            var uiThread = Thread.CurrentThread.ManagedThreadId;
            using var entered = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var sentinel = new InvalidOperationException("在预检前终止模拟卸载");
            var progress = new UninstallProbeProgress(_ =>
            {
                Assert.AreNotEqual(uiThread, Thread.CurrentThread.ManagedThreadId);
                entered.Set();
                Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)));
                throw sentinel; // Before any deployment metadata is read or modified.
            });
            var task = new ProductDeploymentService().UninstallAsync(
                new ProductItem("UI-PROBE", "测试", "测试", string.Empty, ProductSource.Online),
                progress: progress);
            try
            {
                Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
                Assert.IsFalse(task.IsCompleted);
                var responsive = false;
                Dispatcher.CurrentDispatcher.BeginInvoke(new Action(() => responsive = true));
                Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.IsTrue(responsive, "后台卸载等待时 UI 调度器仍应处理消息。");
            }
            finally { release.Set(); }
            try { task.GetAwaiter().GetResult(); Assert.Fail(); }
            catch (InvalidOperationException error) { Assert.AreSame(sentinel, error); }
        });
    }

    [TestMethod]
    public void UninstallProgressRendersPercentageAndScrollableLongDetails()
    {
        RunTraySta(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show();
                window.Width = 1024;
                window.Height = 640;
                var progress = ((MainViewModel)window.DataContext).InstallationProgress;
                progress.BeginUninstall(new ProductItem("UI-PROBE", "卸载界面测试", "测试", string.Empty, ProductSource.Online));
                progress.ReportUninstallProgress(new ProductUninstallProgress(78,
                    string.Join("\n", Enumerable.Repeat("正在删除产品目录、SVN 元数据、下载缓存和临时文件...", 12))));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                window.UpdateLayout();
                var card = FindVisualChildren<Border>(window).Single(b => b.Width == 780 && b.Height == 482 && b.IsVisible);
                var bar = FindVisualChildren<ProgressBar>(card).Single();
                Assert.AreEqual(78d, bar.Value);
                Assert.IsFalse(bar.IsIndeterminate);
                var scroll = (ScrollViewer)card.Child;
                Assert.IsTrue(scroll.ExtentHeight > scroll.ViewportHeight);
                scroll.ScrollToBottom();
                window.UpdateLayout();
                var button = FindVisualChildren<Button>(card).Single(b => Equals(b.Content, "隐藏（后台继续）"));
                var bottom = button.TransformToAncestor(scroll).Transform(new Point(0, button.ActualHeight)).Y;
                Assert.IsTrue(bottom <= scroll.ActualHeight + 1, "长详情下底部按钮必须可以滚动到达。");
                Assert.IsTrue(button.ActualWidth >= 140);
                progress.ReportUninstallProgress(new ProductUninstallProgress(78, "正在删除产品目录、SVN 元数据、下载缓存和临时文件..."));
                scroll.ScrollToTop();
                window.UpdateLayout();
                var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "uninstall-ui-audit"));
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
                bitmap.Render(window);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(directory, "progress-fixed.png"));
                encoder.Save(stream);
            }
            finally { window.Close(); }
        });
    }

    private sealed class UninstallProbeProgress(Action<ProductUninstallProgress> report) : IProgress<ProductUninstallProgress>
    {
        public void Report(ProductUninstallProgress value) => report(value);
    }
}
