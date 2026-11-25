using System;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private static ProductInstallQueueItemViewModel HistoryRecord(string id, string name, ProductInstallQueueStatus state, string message)
    {
        var request = new ProductInstallWorkerRequest(id, name, "在线", string.Empty, ProductSource.Online,
            null, "Tomcat", null, "Java", null, null, true, null, null, false);
        var completed = DateTime.UtcNow.AddMinutes(-3);
        return new ProductInstallQueueItemViewModel($"q-{id}", 1, request, state, state == ProductInstallQueueStatus.Completed ? 100d : 0d,
            message, completed, completed.AddMinutes(-53));
    }

    [TestMethod]
    public void FailedQueueRecordsOfferRetryAndShowTheReasonInRed()
    {
        RunWebsiteUiTest(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
            var window = CreateUiTestWindow();
            try
            {
                window.Width = 1280; window.Height = 800;
                window.Show();
                WaitOnDispatcher(System.Threading.Tasks.Task.Delay(1500)); // let the window finish loading before navigating
                var model = (MainViewModel)window.DataContext;
                var progress = model.InstallationProgress;
                var failed = HistoryRecord("DS3504", "网店开设装修（中职电商模块一）", ProductInstallQueueStatus.Failed,
                    "安装失败：供应商 SVN 同步失败（仓库：https://update.example.test/udp/online_store_decoration_project；尝试 5/5 次）");
                var done = HistoryRecord("DS3402", "客户服务实训", ProductInstallQueueStatus.Completed, "产品文件与运行服务已处理完成。");
                var cancelled = HistoryRecord("DS50112", "模块三 直播销售及客户服务", ProductInstallQueueStatus.Cancelled, "产品安装或更新已取消。");
                progress.QueueItems.Add(done);
                progress.QueueItems.Add(failed);
                progress.QueueItems.Add(cancelled);
                Assert.IsTrue(failed.IsFailed);
                Assert.IsFalse(done.IsFailed);
                Assert.IsFalse(cancelled.IsFailed);

                System.Linq.Enumerable.Single(System.Linq.Enumerable.OfType<RadioButton>(((StackPanel)window.FindName("NavItemsPanel")).Children),
                    b => (b.CommandParameter as string) == "Products").IsChecked = true;
                typeof(MainWindow).GetMethod("OpenDownloadQueueFlyout", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
                progress.ShowCompletedQueue = true;
                for (var i = 0; i < 15; i++) { window.UpdateLayout(); WaitOnDispatcher(System.Threading.Tasks.Task.Delay(20)); }

                var list = (ItemsControl)window.FindName("CompletedDownloadQueueItemsControl");
                FrameworkElement Row(ProductInstallQueueItemViewModel item) =>
                    (FrameworkElement)list.ItemContainerGenerator.ContainerFromItem(item);
                Button Retry(ProductInstallQueueItemViewModel item) =>
                    FindVisualChildren<Button>(Row(item)).Single(button => button.Name == "QueueRetryButton");
                TextBlock Message(ProductInstallQueueItemViewModel item) =>
                    FindVisualChildren<TextBlock>(Row(item)).Single(text => text.Name == "QueueHistoryMessage");
                bool BadgeVisible(ProductInstallQueueItemViewModel item) =>
                    FindVisualChildren<TextBlock>(Row(item)).Any(text => text.Text == item.StateText && text.IsVisible);

                Assert.IsTrue(Retry(failed).IsVisible, $"A failed record offers 重试. wrapper={((FrameworkElement)Retry(failed).Parent).Visibility} flyout={((FrameworkElement)window.FindName("DownloadQueueFlyoutLayer")).Visibility} products={((FrameworkElement)window.FindName("ProductsPage")).IsVisible} row={Row(failed).IsVisible} list={list.IsVisible}");
                Assert.IsFalse(BadgeVisible(failed), "The 安装失败 badge is replaced by the retry button.");
                Assert.IsFalse(Retry(done).IsVisible);
                Assert.IsFalse(Retry(cancelled).IsVisible);
                Assert.IsTrue(BadgeVisible(done), "Successful records keep their badge.");

                var danger = ((SolidColorBrush)window.FindResource("DangerTextBrush")).Color;
                var muted = ((SolidColorBrush)window.FindResource("MutedBrush")).Color;
                Assert.AreEqual(danger, ((SolidColorBrush)Message(failed).Foreground).Color, "The failure reason is red.");
                Assert.AreEqual(muted, ((SolidColorBrush)Message(done).Foreground).Color);
                var idRun = Message(failed).Inlines.OfType<Run>().First();
                Assert.AreEqual(muted, ((SolidColorBrush)idRun.Foreground).Color, "The product ID stays muted.");
            }
            finally { window.Close(); }
        });
    }
}
