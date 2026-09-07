using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MainWindowEnvironmentRendersAtCompactAndFullSize()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();
                ((System.Windows.FrameworkElement)window.FindName("HomePage")).Visibility = System.Windows.Visibility.Collapsed;
                var page = (System.Windows.FrameworkElement)window.FindName("EnvironmentPage");
                page.Visibility = System.Windows.Visibility.Visible;
                foreach (var width in new[] { 1024d, 1280d })
                {
                    window.Width = width;
                    window.Height = width * 820d / 1280d;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
                    Assert.IsTrue(page.ActualWidth > 0 && page.ActualHeight > 0);
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap(
                        (int)window.ActualWidth, (int)window.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(window);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    var directory = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "audit"));
                    Directory.CreateDirectory(directory);
                    using var stream = File.Create(Path.Combine(directory, $"environment-{width:0}.png"));
                    encoder.Save(stream);
                }
            }
            catch (Exception ex) { failure = ex; }
            finally { window?.Close(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(thread.Join(TimeSpan.FromSeconds(30)), "UI rendering timed out.");
        if (failure is not null) Assert.Fail(failure.ToString());
    }

    [TestMethod]
    public void QueueProgressKeepsListsStableAndStateTransitionsRegroupItems()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var model = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(model, root, autoStartWorker: false);
            var first = queue.Enqueue(new ProductItem("OPT1", "First", "", "", ProductSource.Local), false);
            var second = queue.Enqueue(new ProductItem("OPT2", "Second", "", "", ProductSource.Local), false);
            var active = model.ActiveQueueItems;
            var history = model.CompletedQueueItems;
            first.SetProgress(25);
            first.SetMessage("Downloading");
            Assert.AreSame(active, model.ActiveQueueItems);
            Assert.AreSame(history, model.CompletedQueueItems);
            Assert.AreEqual(25d, model.ActiveQueueItems[0].Progress);
            first.SetState(ProductInstallQueueStatus.Completed);
            second.SetState(ProductInstallQueueStatus.Failed);
            Assert.AreEqual(0, model.ActiveQueueItems.Count);
            CollectionAssert.AreEqual(new[] { second, first }, model.CompletedQueueItems.ToArray());
            model.QueueItems.Clear();
            var notifications = 0;
            model.PropertyChanged += (_, _) => notifications++;
            first.SetProgress(50);
            Assert.AreEqual(0, notifications, "Reset must detach removed task subscriptions.");
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void QueueCleanupPreservesRecentSessionsAndRecoveryPayloads()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            string Session(bool old)
            {
                var path = Path.Combine(root, Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(path);
                foreach (var name in new[] { "stop.flag", "heartbeat.flag" })
                {
                    var file = Path.Combine(path, name);
                    File.WriteAllText(file, "1");
                    if (old) File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddDays(-2));
                }
                return path;
            }
            var current = Session(false);
            var stale = Session(true);
            var recent = Session(false);
            var recovery = Session(true);
            File.WriteAllText(Path.Combine(recovery, "progress.json"), "recovery");
            using var locked = new ManualResetEventSlim();
            using var unlock = new ManualResetEventSlim();
            var worker = new Thread(() =>
            {
                using var mutex = ProductInstallWorker.AcquireQueueMutex(current);
                if (mutex is null) return;
                locked.Set();
                unlock.Wait(TimeSpan.FromSeconds(10));
                mutex.ReleaseMutex();
            });
            worker.Start();
            try
            {
                Assert.IsTrue(locked.Wait(TimeSpan.FromSeconds(5)));
                QueueSessionMaintenance.PruneInactiveSessions(current);
                Assert.IsTrue(Directory.Exists(stale), "An active worker must prevent cleanup.");
            }
            finally { unlock.Set(); worker.Join(); }
            QueueSessionMaintenance.PruneInactiveSessions(current);
            Assert.IsFalse(Directory.Exists(stale));
            Assert.IsTrue(Directory.Exists(current));
            Assert.IsTrue(Directory.Exists(recent));
            Assert.AreEqual("recovery", File.ReadAllText(Path.Combine(recovery, "progress.json")));
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public async Task DriveSamplingThrottlesRefreshAndPreservesLastKnownDrivesOnPartialFailure()
    {
        var calls = 0;
        var model = new MainViewModel(false, _ =>
        {
            calls++;
            return new DriveSnapshotResult(new[] { new DriveSnapshot("C:\\", 20, 100) }, false);
        });
        try
        {
            model.Drives.Add(new DriveItem("Z:\\", 10, 100));
            await model.RefreshDrivesAsync();
            await model.RefreshDrivesAsync();
            Assert.AreEqual(1, calls, "Repeated ticks must use the cached disk snapshot for ten seconds.");
            Assert.AreEqual(2, model.Drives.Count, "An incomplete sample must retain the last known unavailable drive.");
            Assert.AreEqual(20d, model.Drives.Single(drive => drive.Name == "C:\\").PercentUsed);
        }
        finally { model.Dispose(); }
    }

    [TestMethod]
    public async Task DriveSamplingDoesNotOverlapOrUpdateDisposedModel()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var calls = 0;
        var model = new MainViewModel(false, token =>
        {
            Interlocked.Increment(ref calls);
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(10));
            return new DriveSnapshotResult(new[] { new DriveSnapshot("Z:\\", 10, 100) }, true);
        });
        var pending = model.RefreshDrivesAsync();
        try
        {
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(5)));
            await model.RefreshDrivesAsync();
            Assert.AreEqual(1, calls);
            model.Dispose();
        }
        finally { release.Set(); }
        await pending;
        Assert.AreEqual(0, model.Drives.Count);
    }
}
