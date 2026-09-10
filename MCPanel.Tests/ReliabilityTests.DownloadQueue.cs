using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml.Linq;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using SharpSvn;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void ProductDownloadPauseBlocksUntilExplicitlyResumed()
    {
        using var pauseController = new DownloadPauseController();

        Assert.IsTrue(pauseController.Pause());
        Assert.IsTrue(pauseController.IsPaused);
        var waitTask = pauseController.WaitIfPausedAsync(CancellationToken.None);
        Assert.IsFalse(waitTask.IsCompleted, "暂停后下载门必须保持关闭。");

        Assert.IsTrue(pauseController.Resume());
        Assert.IsTrue(waitTask.Wait(TimeSpan.FromSeconds(2)), "继续下载后等待任务应立即释放。");
        Assert.IsFalse(pauseController.IsPaused);
    }

    [TestMethod]
    public void InstallationPanelShowsPauseOnlyDuringDownload()
    {
        var product = new ProductItem("TEST1", "测试产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.Begin(product, isUpdate: false);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, progress.PauseButtonVisibility);

        progress.SetDownloadStage();
        Assert.IsTrue(progress.CanPause);
        Assert.IsTrue(progress.IsDownloading);
        Assert.AreEqual(0, progress.DownloadProgress, 0.001);
        Assert.AreEqual(System.Windows.Visibility.Visible, progress.PauseButtonVisibility);
        Assert.AreEqual("暂停下载", progress.PauseActionText);

        progress.ApplyWorkerProgress(new ProductInstallWorkerProgress(
            "running",
            45,
            "正在下载 product.zip",
            InstallProgressStage.Downloading,
            50));
        Assert.AreEqual(50, progress.DownloadProgress, 0.001, "顶部环形进度应使用下载阶段进度，而不是安装总进度。");

        progress.MarkDownloadPaused();
        Assert.IsTrue(progress.IsPaused);
        Assert.AreEqual("继续下载", progress.PauseActionText);
        progress.ReportStatus("正在下载：payload.zip");
        StringAssert.Contains(progress.DetailText, "已暂停");

        progress.MarkDownloadResumed();
        Assert.IsFalse(progress.IsPaused);
        Assert.AreEqual("正在下载：payload.zip", progress.DetailText);

        progress.SetDeploymentStage(isUpdate: false);
        Assert.IsFalse(progress.CanPause);
        Assert.IsFalse(progress.IsDownloading);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, progress.PauseButtonVisibility);
    }

    [TestMethod]
    public void InstalledProductUsesCompletionIndicatorInsteadOfZeroProgress()
    {
        var product = new ProductItem("TEST-COMPLETE", "已安装产品", "测试", string.Empty, ProductSource.Online)
        {
            DownloadProgress = 0
        };

        Assert.IsFalse(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.CompletedIndicatorVisibility);

        product.IsInstalled = true;

        Assert.IsTrue(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.CompletedIndicatorVisibility);

        product.IsBusy = true;

        Assert.IsFalse(product.IsInstallationComplete, "更新或卸载期间不能显示安装完成勾号。");
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.CompletedIndicatorVisibility);

        product.IsBusy = false;
        product.SetQueueState(ProductInstallQueueStatus.Pending, 1);

        Assert.IsFalse(product.IsInstallationComplete, "排队期间不能显示安装完成勾号。");
        Assert.AreEqual(System.Windows.Visibility.Visible, product.ProgressIndicatorVisibility);

        product.SetQueueState(null, 0);

        Assert.IsTrue(product.IsInstallationComplete);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, product.ProgressIndicatorVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, product.CompletedIndicatorVisibility);
    }

    [TestMethod]
    public void HidingInstallationPanelKeepsDownloadControlsAndStateAlive()
    {
        var product = new ProductItem("TEST-HIDE", "后台下载产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.Begin(product, isUpdate: false);
        progress.SetDownloadStage();
        progress.Hide();

        Assert.IsFalse(progress.IsVisible);
        Assert.IsTrue(progress.CanPause, "隐藏进度层不能关闭后台下载的暂停控制。");
        Assert.IsTrue(progress.CanCancel, "隐藏进度层不能关闭后台安装的取消控制。");

        progress.Show();
        Assert.IsTrue(progress.IsVisible);
        Assert.AreEqual("下载产品文件", progress.StageText);
    }

    [TestMethod]
    public void ProductInstallQueuePreservesClickOrderAcrossRecovery()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var firstProduct = new ProductItem("QUEUE-1", "第一个产品", "测试", string.Empty, ProductSource.Online);
            var secondProduct = new ProductItem("QUEUE-2", "第二个产品", "测试", string.Empty, ProductSource.Online);
            var thirdProduct = new ProductItem("QUEUE-3", "第三个产品", "测试", string.Empty, ProductSource.Online);
            ProductInstallQueueItemViewModel first;
            ProductInstallQueueItemViewModel second;
            ProductInstallQueueItemViewModel third;

            using (var queue = new ProductInstallQueueService(
                       new InstallationProgressViewModel(),
                       root,
                       autoStartWorker: false))
            {
                first = queue.Enqueue(firstProduct, isUpdate: false);
                second = queue.Enqueue(secondProduct, isUpdate: false);
                third = queue.Enqueue(thirdProduct, isUpdate: false);

                Assert.AreEqual(1, first.Sequence);
                Assert.AreEqual(2, second.Sequence);
                Assert.AreEqual(3, third.Sequence);
                Assert.AreEqual(first.QueueId, queue.CurrentItem!.QueueId);
                Assert.AreEqual(1, first.QueuePosition);
                Assert.AreEqual(2, second.QueuePosition);
                Assert.AreEqual(3, third.QueuePosition);
                Assert.IsTrue(queue.Cancel(second.QueueId));
                Assert.AreEqual(ProductInstallQueueStatus.Cancelled, second.State);
                Assert.AreEqual(2, third.QueuePosition);
            }

            using var restored = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            Assert.AreEqual(3, restored.Items.Count, "最近的终态记录和未完成队列都应在重启后恢复。");
            Assert.AreEqual(first.QueueId, restored.CurrentItem!.QueueId);
            Assert.AreEqual(ProductInstallQueueStatus.Pending, restored.Items[0].State);
            Assert.AreEqual(second.QueueId, restored.Items[1].QueueId);
            Assert.AreEqual(ProductInstallQueueStatus.Cancelled, restored.Items[1].State);
            Assert.AreEqual("已取消排队，未开始下载。", restored.Items[1].Message);
            Assert.AreEqual(0, restored.Items[1].QueuePosition);
            Assert.AreEqual(1, restored.Items[0].QueuePosition);
            Assert.AreEqual(third.QueueId, restored.Items[2].QueueId);
            Assert.AreEqual(ProductInstallQueueStatus.Pending, restored.Items[2].State);
            Assert.AreEqual(2, restored.Items[2].QueuePosition);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ProductInstallQueueRestoresCompletedHistoryWithoutRequeueing()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            string queueId;
            using (var queue = new ProductInstallQueueService(
                       new InstallationProgressViewModel(),
                       root,
                       autoStartWorker: false))
            {
                var item = queue.Enqueue(
                    new ProductItem("QUEUE-COMPLETED", "已完成产品", "测试", string.Empty, ProductSource.Online),
                    isUpdate: false);
                item.SetState(ProductInstallQueueStatus.Completed);
                item.SetProgress(100);
                item.SetMessage("产品文件与运行服务已处理完成。");
                queueId = item.QueueId;
            }

            using var restored = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            Assert.AreEqual(1, restored.Items.Count, "已完成的下载记录应在重启后保留。");
            Assert.AreEqual(queueId, restored.Items[0].QueueId);
            Assert.AreEqual(ProductInstallQueueStatus.Completed, restored.Items[0].State);
            Assert.AreEqual(100d, restored.Items[0].Progress, 0.001d);
            Assert.AreEqual("产品文件与运行服务已处理完成。", restored.Items[0].Message);
            Assert.IsNull(restored.CurrentItem, "已完成记录不能在重启后重新进入安装队列。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancellingPendingQueueItemPublishesCancelMarkerBeforeClaim()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = new ProductItem("QUEUE-CANCEL", "取消排队产品", "测试", string.Empty, ProductSource.Online);
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(product, isUpdate: false);

            Assert.IsTrue(queue.Cancel(item.QueueId));
            Assert.AreEqual(ProductInstallQueueStatus.Cancelled, item.State);
            Assert.IsFalse(File.Exists(item.RequestPath));
            Assert.IsTrue(
                File.Exists(item.CancelPath),
                "取消排队项时必须先留下取消标记，避免后台进程恰好接管请求后继续执行。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void DownloadQueueRowPausesAndResumesOnlyTheActiveDownload()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(
                progress,
                root,
                autoStartWorker: false);
            var active = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-1", "正在下载的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var waiting = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-2", "排队中的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);

            active.SetState(ProductInstallQueueStatus.Running);
            active.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                20,
                "正在下载软件包...",
                InstallProgressStage.Downloading,
                25));

            Assert.IsTrue(active.CanTogglePause);
            Assert.IsFalse(waiting.CanTogglePause);
            Assert.AreEqual("\uE769", active.PauseActionGlyph);
            Assert.AreEqual("\uE74D", active.RemoveActionGlyph);
            Assert.AreEqual(true, queue.TogglePause(active.QueueId));
            Assert.IsTrue(active.IsPaused);
            Assert.IsTrue(active.CanTogglePause, "暂停后必须保留同一按钮，供用户继续下载。");
            Assert.AreEqual("继续", active.PauseActionText);
            Assert.AreEqual("\uE768", active.PauseActionGlyph);
            Assert.IsTrue(File.Exists(active.PausePath));
            Assert.IsNull(queue.TogglePause(waiting.QueueId), "等待项不能错误地暂停当前下载。");

            Assert.AreEqual(false, queue.TogglePause(active.QueueId));
            Assert.IsFalse(active.IsPaused);
            Assert.IsTrue(active.CanTogglePause);
            Assert.AreEqual("暂停", active.PauseActionText);
            Assert.IsFalse(File.Exists(active.PausePath));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void RemovingPendingQueueRowDeletesItsSessionFilesAndLegacyOverlayStaysClosed()
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
                new ProductItem("QUEUE-REMOVE", "待删除的软件", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var requestPrefix = item.RequestPath.Substring(
                0,
                item.RequestPath.Length - ".request.json".Length);
            var activeEnvelope = requestPrefix + ".active.json";
            var finishedEnvelope = requestPrefix + ".finished.json";
            File.WriteAllText(activeEnvelope, "claimed");
            File.WriteAllText(finishedEnvelope, "finished");
            var paths = new[]
            {
                item.RequestPath,
                activeEnvelope,
                finishedEnvelope,
                item.ProgressPath,
                item.CancelPath,
                item.PausePath
            };

            Assert.IsFalse(progress.IsVisible, "加入下载队列不应再弹出旧的全屏安装队列。");
            Assert.IsTrue(queue.Remove(item.QueueId));
            Assert.AreEqual(0, queue.Items.Count);
            Assert.IsTrue(paths.All(path => !File.Exists(path)), "删除排队项后应清理该项的会话文件。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void RemovingRequestAlreadyClaimedByWorkerKeepsCancellationUntilTerminalProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-CLAIM-RACE", "接管竞态产品", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var activeEnvelope = item.RequestPath.Substring(
                0,
                item.RequestPath.Length - ".request.json".Length) + ".active.json";
            File.Move(item.RequestPath, activeEnvelope);

            Assert.IsTrue(queue.Remove(item.QueueId));
            Assert.AreEqual(1, queue.Items.Count, "后台已经接管请求时，行必须保留到后台确认终态。");
            Assert.IsTrue(item.IsRemovalRequested);
            Assert.IsFalse(item.CanRemove, "删除请求发出后必须立即禁用删除按钮，避免重复点击。");
            Assert.IsFalse(item.CanTogglePause, "删除请求发出后不能再暂停或继续下载。");
            Assert.AreEqual("正在删除", item.StateText);
            Assert.AreEqual("正在取消并删除此任务，请稍候...", item.Message);
            Assert.IsTrue(File.Exists(item.CancelPath), "后台确认取消前不能提前删除取消标记。");
            Assert.IsTrue(File.Exists(activeEnvelope));

            File.WriteAllText(
                item.ProgressPath,
                JsonSerializer.Serialize(new ProductInstallWorkerProgress(
                    "cancelled",
                    0,
                    "已取消。",
                    InstallProgressStage.Preparing,
                    0)));
            queue.Tick();

            Assert.AreEqual(0, queue.Items.Count);
            Assert.IsFalse(File.Exists(item.CancelPath));
            Assert.IsFalse(File.Exists(activeEnvelope));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void PauseMarkerIsClearedWhenDownloadMovesToDeployment()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var queue = new ProductInstallQueueService(
                new InstallationProgressViewModel(),
                root,
                autoStartWorker: false);
            var item = queue.Enqueue(
                new ProductItem("QUEUE-PAUSE-STAGE", "暂停阶段产品", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            item.SetState(ProductInstallQueueStatus.Running);
            item.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                40,
                "正在下载...",
                InstallProgressStage.Downloading,
                50));
            Assert.AreEqual(true, queue.TogglePause(item.QueueId));
            Assert.IsTrue(File.Exists(item.PausePath));

            File.WriteAllText(
                item.ProgressPath,
                JsonSerializer.Serialize(new ProductInstallWorkerProgress(
                    "running",
                    70,
                    "正在部署...",
                    InstallProgressStage.Installing,
                    20)));
            queue.Tick();

            Assert.IsFalse(item.IsPaused);
            Assert.IsFalse(File.Exists(item.PausePath));
            Assert.AreEqual("正在部署", item.StateText);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancelledFutureQueueRowDoesNotInflateOverallDownloadProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var progress = new InstallationProgressViewModel();
            using var queue = new ProductInstallQueueService(progress, root, autoStartWorker: false);
            var active = queue.Enqueue(
                new ProductItem("QUEUE-RING-1", "当前下载", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            var cancelled = queue.Enqueue(
                new ProductItem("QUEUE-RING-2", "取消排队", "测试", string.Empty, ProductSource.Online),
                isUpdate: false);
            active.SetState(ProductInstallQueueStatus.Running);
            active.ApplyProgress(new ProductInstallWorkerProgress(
                "running",
                30,
                "正在下载...",
                InstallProgressStage.Downloading,
                40));
            Assert.IsTrue(queue.Cancel(cancelled.QueueId));

            Assert.AreEqual(40, progress.OverallDownloadProgress, 0.001);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ClaimedCancelledWorkerDoesNotStartProductDownload()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var product = new ProductItem("WORKER-CANCEL", "已取消产品", "测试", string.Empty, ProductSource.Online);
            var request = ProductInstallWorker.CreateRequest(product, isUpdate: false);
            var progressPath = ProductInstallWorker.CreateProgressFile(root, request.ProductId);
            var cancelPath = ProductInstallWorker.CreateControlFile(root, request.ProductId, "cancel");
            var pausePath = ProductInstallWorker.CreateControlFile(root, request.ProductId, "pause");
            File.WriteAllText(cancelPath, "1");

            var exitCode = ProductInstallWorker.RunOneAsync(
                    request,
                    progressPath,
                    cancelPath,
                    pausePath)
                .GetAwaiter()
                .GetResult();

            Assert.AreEqual(2, exitCode);
            Assert.IsTrue(ProductInstallWorker.TryReadProgress(progressPath, out var progress));
            Assert.AreEqual("cancelled", progress.State);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UninstallationUsesTheSameProgressPanelWithMonotonicProgress()
    {
        var product = new ProductItem("TEST-UNINSTALL", "测试卸载产品", "测试", string.Empty, ProductSource.Online);
        var progress = new InstallationProgressViewModel();

        progress.BeginUninstall(product);
        Assert.IsTrue(progress.IsVisible);
        Assert.AreEqual("正在卸载产品", progress.OperationTitle);
        Assert.AreEqual("准备卸载", progress.StageText);
        Assert.IsFalse(progress.CanPause);
        Assert.IsFalse(progress.CanCancel);

        progress.ReportUninstallProgress(new ProductUninstallProgress(42, "正在移除 IIS 应用、应用池和共享站点端口..."));
        Assert.AreEqual(42, progress.Progress, 0.001);
        Assert.AreEqual("清理 IIS 绑定", progress.StageText);
        Assert.AreEqual(42d, progress.DisplayDownloadProgress);
        Assert.AreEqual("42%", progress.ProgressText);
        Assert.IsFalse(progress.IsProgressIndeterminate);
        progress.ReportUninstallProgress(new ProductUninstallProgress(20, "旧阶段消息不应倒退进度"));
        Assert.AreEqual(42, progress.Progress, 0.001);
        progress.Complete();
        Assert.AreEqual(100d, progress.DisplayDownloadProgress);
        progress.Begin(product, false);
        Assert.AreEqual(0d, progress.DisplayDownloadProgress);
        Assert.IsTrue(progress.IsProgressIndeterminate);
    }

    [TestMethod]
    public void LegacyProductInstallRootAndIisMetadataArePreserved()
    {
        var product = new ProductItem("DS0102", "评价系统", "初级", string.Empty, ProductSource.Online)
        {
            InstallRoot = Path.Combine(Path.GetTempPath(), "MCPanel-Products"),
            SysType = "64",
            RunEnvironment = "Framework4.5"
        };

        var resolved = ProductInstallPathResolver.ResolveProductDirectory(product);
        StringAssert.EndsWith(resolved, Path.Combine("MCPanel-Products", "DS0102"));
        Assert.IsTrue(ProductInstallPathResolver.IsInsideProductRoot(resolved, product));

        var script = ProductDeploymentService.BuildIisBindScript(
            "C:\\Windows\\System32\\inetsrv\\appcmd.exe",
            "MCPanel",
            "C:\\StoreData\\Runtime\\IISRoot",
            "/DS0102",
            resolved,
            "DS0102",
            "v4.0",
            ProductDeploymentService.ResolveIisPipelineMode(product.RunEnvironment, "v4.0"),
            enable32Bit: false,
            port: 8088);

        StringAssert.Contains(script, "/managedPipelineMode:$managedPipelineMode");
        StringAssert.Contains(script, "/enable32BitAppOnWin64:$enable32Bit");
        StringAssert.Contains(script, "$managedPipelineMode='Classic'");
        StringAssert.Contains(script, "$enable32Bit=$false");
    }

    [TestMethod]
    public void ProductCleanupDeletesReadOnlySvnWorkingCopy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var svn = Path.Combine(root, ".svn", "pristine");
            Directory.CreateDirectory(svn);
            var file = Path.Combine(svn, "entry");
            File.WriteAllText(file, "metadata");
            File.SetAttributes(file, FileAttributes.ReadOnly | FileAttributes.Hidden);

            ProductDeploymentService.DeleteDirectoryForTest(root);
            Assert.IsFalse(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
