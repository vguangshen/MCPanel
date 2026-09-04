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
    public void InstalledProductRemainsAvailableForSupplierUpdate()
    {
        var product = new ProductItem("DS0105", "电子商务运营沙盘平台", "实训系统", string.Empty, ProductSource.Online)
        {
            IsInstalled = true
        };

        Assert.AreEqual("卸载", product.UninstallActionText);
        Assert.AreEqual("更新", product.UpdateActionText);
        Assert.IsTrue(product.CanProductAction);
        Assert.IsTrue(product.CanUpdate);

        product.IsBusy = true;
        Assert.AreEqual("卸载中", product.UninstallActionText);
        Assert.AreEqual("更新中", product.UpdateActionText);
        Assert.IsFalse(product.CanProductAction);
        Assert.IsFalse(product.CanUpdate);
    }

    [TestMethod]
    public void RelativePathForSameProductRootIsCurrentDirectory()
    {
        var root = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", "content");

        Assert.AreEqual(".", PathCompat.GetRelativePath(root, root));
        Assert.AreEqual(".", PathCompat.GetRelativePath(root + Path.DirectorySeparatorChar, root));
    }

    [TestMethod]
    public void SupplierSvnInstallsDirectlyAndUpdatesInPlace()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "PT0404");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllText(Path.Combine(seed, "WEB-INF", "version.txt"), "v1");

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "v1" }));
            }

            var transfer = new SvnProductTransferService();
            var reportedProgress = new System.Collections.Generic.List<double>();
            var reportedDetails = new System.Collections.Generic.List<ProductDownloadProgress>();
            var reportedStatus = new System.Collections.Generic.List<string>();
            var firstResult = transfer.SyncWithDetailsAsync(
                    repositoryUri,
                    installed,
                    update =>
                    {
                        reportedProgress.Add(update.Percent);
                        reportedDetails.Add(update);
                    },
                    reportedStatus.Add,
                    CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(installed, firstResult);
            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")));
            Assert.AreEqual("v1", File.ReadAllText(Path.Combine(installed, "WEB-INF", "version.txt")));
            Assert.IsTrue(reportedStatus.Exists(status => status.Contains("已扫描", StringComparison.Ordinal)), string.Join(" | ", reportedStatus));
            Assert.IsTrue(reportedDetails.Exists(update => update.ScannedFiles >= 1), "Checkout 必须实时报告已扫描文件。");
            Assert.AreEqual(100d, reportedProgress[reportedProgress.Count - 1]);
            for (var index = 1; index < reportedProgress.Count; index++)
            {
                Assert.IsTrue(reportedProgress[index] >= reportedProgress[index - 1], "下载进度必须单调递增。");
            }

            var editor = Path.Combine(root, "editor");
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.CheckOut(new SvnUriTarget(repositoryUri), editor));
                File.WriteAllText(Path.Combine(editor, "WEB-INF", "version.txt"), "v2");
                Assert.IsTrue(client.Commit(editor, new SvnCommitArgs { LogMessage = "v2" }));
            }

            var updateResult = transfer.SyncAsync(repositoryUri, installed, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(installed, updateResult);
            Assert.AreEqual("v2", File.ReadAllText(Path.Combine(installed, "WEB-INF", "version.txt")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void SupplierSvnResumesAnExistingIncompleteWorkingCopy()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "RESUME1");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllText(Path.Combine(seed, "WEB-INF", "payload.bin"), "resume-payload");

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "seed" }));
                var partialCheckout = new SvnCheckOutArgs { Depth = SvnDepth.Empty };
                Assert.IsTrue(client.CheckOut(new SvnUriTarget(repositoryUri), installed, partialCheckout));
            }

            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")));
            Assert.IsFalse(File.Exists(Path.Combine(installed, "WEB-INF", "payload.bin")));

            var reportedStatus = new System.Collections.Generic.List<string>();
            var transfer = new SvnProductTransferService();
            transfer.SyncAsync(repositoryUri, installed, null, reportedStatus.Add, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.AreEqual("resume-payload", File.ReadAllText(Path.Combine(installed, "WEB-INF", "payload.bin")));
            Assert.IsTrue(reportedStatus.Exists(status => status.Contains("上次进度", StringComparison.Ordinal)));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void CancelledSupplierSvnCheckoutKeepsRecoverableProgress()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var repository = Path.Combine(root, "repository");
            var seed = Path.Combine(root, "seed");
            var installed = Path.Combine(root, "web", "RESUME2");
            Directory.CreateDirectory(Path.Combine(seed, "WEB-INF"));
            File.WriteAllBytes(Path.Combine(seed, "WEB-INF", "large-payload.bin"), new byte[8 * 1024 * 1024]);

            using (var repositoryClient = new SvnRepositoryClient())
            {
                Assert.IsTrue(repositoryClient.CreateRepository(repository));
            }

            var repositoryUri = new Uri(repository.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar);
            using (var client = new SvnClient())
            {
                Assert.IsTrue(client.Import(seed, repositoryUri, new SvnImportArgs { LogMessage = "seed" }));
            }

            using var cancellation = new CancellationTokenSource();
            var cancellationTriggered = false;
            var transfer = new SvnProductTransferService();
            Assert.ThrowsException<OperationCanceledException>(() =>
                transfer.SyncAsync(
                        repositoryUri,
                        installed,
                        value =>
                        {
                            if (value >= 1 &&
                                Directory.Exists(Path.Combine(installed, ".svn")) &&
                                !cancellationTriggered)
                            {
                                cancellationTriggered = true;
                                cancellation.Cancel();
                            }
                        },
                        null,
                        cancellation.Token)
                    .GetAwaiter().GetResult());

            Assert.IsTrue(cancellationTriggered, "测试必须在 Checkout 传输期间触发取消。");
            Assert.IsTrue(Directory.Exists(Path.Combine(installed, ".svn")), "取消后必须保留可恢复的 SVN 元数据。");

            transfer.SyncAsync(repositoryUri, installed, null, null, CancellationToken.None)
                .GetAwaiter().GetResult();
            Assert.AreEqual(
                8L * 1024 * 1024,
                new FileInfo(Path.Combine(installed, "WEB-INF", "large-payload.bin")).Length);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ProductInstallOrderPersistsAndReinstallMovesToEnd()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var storeFile = Path.Combine(root, "install-order.json");
            var firstInstall = new DateTime(2026, 7, 16, 8, 0, 0, DateTimeKind.Utc);
            var secondInstall = firstInstall.AddDays(1);
            var store = new ProductInstallOrderStore(storeFile);

            store.EnsureInstalled([
                new ProductInstallOrderCandidate("SECOND", secondInstall),
                new ProductInstallOrderCandidate("FIRST", firstInstall)
            ]);

            var firstSequence = store.GetSequence("FIRST");
            var secondSequence = store.GetSequence("SECOND");
            Assert.IsTrue(firstSequence < secondSequence);

            store.RecordInstalled("FIRST", secondInstall.AddHours(1));
            Assert.AreEqual(firstSequence, store.GetSequence("FIRST"), "更新已安装产品不能改变首次安装顺序。");

            var reloaded = new ProductInstallOrderStore(storeFile);
            Assert.AreEqual(firstSequence, reloaded.GetSequence("FIRST"));
            Assert.AreEqual(secondSequence, reloaded.GetSequence("SECOND"));

            reloaded.Remove("FIRST");
            reloaded.RecordInstalled("FIRST", secondInstall.AddDays(1));
            Assert.IsTrue(reloaded.GetSequence("FIRST") > secondSequence, "卸载后重新安装应排到已安装队列末尾。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateArchiveRejectsPathTraversal()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var archiveFile = Path.Combine(root, "malicious.zip");
            using (var archive = ZipFile.Open(archiveFile, ZipArchiveMode.Create))
            {
                var entry = archive.CreateEntry("../escaped.txt");
                using var writer = new StreamWriter(entry.Open());
                writer.Write("blocked");
            }

            var destination = Path.Combine(root, "payload");
            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.ExtractArchiveSafely(archiveFile, destination, null, CancellationToken.None));
            Assert.IsFalse(File.Exists(Path.Combine(root, "escaped.txt")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void LocalUpdatePreparationVerifiesSidecarAndRetainsSourcePackage()
    {
        var root = CreateTemporaryDirectory();
        string? stagingRoot = null;
        try
        {
            var package = Path.Combine(root, "MCPanel-local.zip");
            var executable = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
            var config = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config");
            Assert.IsTrue(File.Exists(executable), "测试发布目录必须包含 MCPanel.exe。");
            Assert.IsTrue(File.Exists(config), "测试发布目录必须包含 MCPanel.exe.config。");

            using (var archive = ZipFile.Open(package, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(executable, "MCPanel.exe");
                archive.CreateEntryFromFile(config, "MCPanel.exe.config");
            }

            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(package))
            {
                hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", string.Empty).ToLowerInvariant();
            }

            File.WriteAllText(package + ".sha256", $"{hash}  {Path.GetFileName(package)}");
            var service = new ApplicationUpdateService();
            var prepared = service.PrepareLocalAsync(
                    package,
                    progress: null,
                    status: null,
                    CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            stagingRoot = Directory.GetParent(prepared.PayloadDirectory)?.FullName;

            Assert.AreEqual(Path.GetFullPath(package), prepared.PackageFile);
            Assert.AreEqual(ApplicationUpdateService.CurrentVersion, new Version(prepared.Version));
            Assert.IsFalse(
                prepared.DeletePackageFileAfterApply,
                "本地更新包是用户选择的源文件，更新完成后不能被更新器删除。");
            Assert.IsTrue(File.Exists(package));

            service.DiscardPreparedUpdate(prepared);
            Assert.IsFalse(
                !string.IsNullOrWhiteSpace(stagingRoot) && Directory.Exists(stagingRoot),
                "用户取消本地更新确认后，暂存目录应被清理。");
        }
        finally
        {
            if (stagingRoot is string resolvedStagingRoot)
            {
                DeleteTemporaryTree(resolvedStagingRoot);
            }

            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task RemoteUpdateReleasesDownloadFileAndReusesVerifiedPackage()
    {
        var sourceRoot = CreateTemporaryDirectory();
        var service = new ApplicationUpdateService();
        var version = ApplicationUpdateService.CurrentVersion.ToString();
        var packageFile = Path.Combine(service.UpdatesRoot, "Downloads", $"MCPanel-{version}.zip");
        var temporaryFile = packageFile + ".part";
        PreparedApplicationUpdate? first = null;
        PreparedApplicationUpdate? second = null;
        try
        {
            if (File.Exists(packageFile)) File.Delete(packageFile);
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);

            var packageSource = Path.Combine(sourceRoot, "MCPanel-source.zip");
            var executable = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
            var config = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config");
            Assert.IsTrue(File.Exists(executable), "测试发布目录必须包含 MCPanel.exe。");
            Assert.IsTrue(File.Exists(config), "测试发布目录必须包含 MCPanel.exe.config。");

            using (var archive = ZipFile.Open(packageSource, ZipArchiveMode.Create))
            {
                archive.CreateEntryFromFile(executable, "MCPanel.exe");
                archive.CreateEntryFromFile(config, "MCPanel.exe.config");
            }

            var packageBytes = File.ReadAllBytes(packageSource);
            string hash;
            using (var sha = SHA256.Create())
            {
                hash = BitConverter.ToString(sha.ComputeHash(packageBytes)).Replace("-", string.Empty).ToLowerInvariant();
            }

            var manifest = new OnlineUpdateManifest
            {
                Version = version,
                PackageUrl = "https://updates.example.test/MCPanel.zip",
                Sha256 = hash
            };
            var downloadCount = 0;
            async Task<HttpResponseMessage> Download(CancellationToken _)
            {
                Interlocked.Increment(ref downloadCount);
                await Task.Delay(100);
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(packageBytes)
                };
            }

            var parallelTasks = new[]
            {
                service.PrepareRemotePackageAsync(
                    manifest,
                    Download,
                    response => response.EnsureSuccessStatusCode(),
                    progress: null,
                    status: null,
                    CancellationToken.None),
                service.PrepareRemotePackageAsync(
                    manifest,
                    Download,
                    response => response.EnsureSuccessStatusCode(),
                    progress: null,
                    status: null,
                    CancellationToken.None)
            };
            try
            {
                var parallel = await Task.WhenAll(parallelTasks);
                Assert.AreEqual(2, parallel.Length);
                Assert.AreEqual(1, downloadCount, "同一更新包的并发准备任务应串行化，避免 .part 文件互相覆盖。");
            }
            finally
            {
                foreach (var task in parallelTasks.Where(task => task.Status == TaskStatus.RanToCompletion))
                {
                    service.DiscardPreparedUpdate(task.Result);
                }
            }

            first = await service.PrepareRemotePackageAsync(
                manifest,
                Download,
                response => response.EnsureSuccessStatusCode(),
                progress: null,
                status: null,
                CancellationToken.None);
            service.DiscardPreparedUpdate(first);

            Assert.IsTrue(File.Exists(packageFile), "下载完成后应保留可复用的正式更新包。");
            Assert.IsFalse(File.Exists(temporaryFile), "正式化完成后不应残留被占用的 .part 文件。");

            second = await service.PrepareRemotePackageAsync(
                manifest,
                Download,
                response => response.EnsureSuccessStatusCode(),
                progress: null,
                status: null,
                CancellationToken.None);
            Assert.AreEqual(1, downloadCount, "SHA-256 一致的已下载更新包应直接复用，不应重新下载。");
        }
        finally
        {
            service.DiscardPreparedUpdate(first);
            service.DiscardPreparedUpdate(second);
            if (File.Exists(packageFile)) File.Delete(packageFile);
            if (File.Exists(temporaryFile)) File.Delete(temporaryFile);
            DeleteTemporaryTree(sourceRoot);
        }
    }

    [TestMethod]
    public void UpdateTransactionReplacesProgramAndPreservesOperationalData()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(install, "MCPanel.exe.config"),
                "<configuration><appSettings><add key=\"UpdateManifestUrl\" value=\"https://updates.example.com/manifest.json\" /><add key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel-override\" /><add key=\"Environment.Frp.PackageUrl\" value=\"https://mirror.example.com/frp.zip\" /><add key=\"Ai.Provider\" value=\"OpenAI\" /><add key=\"Ai.Endpoint\" value=\"https://mirror.example.com/v1/chat/completions\" /><add key=\"Ai.Model\" value=\"ops-model\" /><add key=\"Ai.ApiKey\" value=\"old-ai-key\" /></appSettings></configuration>");
            File.WriteAllText(Path.Combine(install, "obsolete.dll"), "obsolete");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"),
                "<configuration><appSettings><add key=\"UpdateManifestUrl\" value=\"\" /><add key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel\" /><add key=\"Environment.Frp.PackageUrl\" value=\"https://github.com/fatedier/frp/releases/download/v0.71.0/frp_0.71.0_windows_amd64.zip\" /><add key=\"Ai.Provider\" value=\"GLM\" /><add key=\"Ai.Endpoint\" value=\"https://open.bigmodel.cn/api/paas/v4/chat/completions\" /><add key=\"Ai.Model\" value=\"glm-5.2\" /><add key=\"Ai.ApiKey\" value=\"new-ai-key\" /></appSettings></configuration>");
            File.WriteAllText(Path.Combine(payload, "current.dll"), "current");

            foreach (var name in new[] { "StoreData", "AccountApi", "Runtime", "Downloads", "Tools", "web", "Cache", "Frp", "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite" })
            {
                var directory = Path.Combine(install, name);
                Directory.CreateDirectory(directory);
                File.WriteAllText(Path.Combine(directory, "preserved.txt"), name);
            }

            ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
            {
                InstallDirectory = install,
                PayloadDirectory = payload,
                StagingRoot = stagingRoot,
                MainExecutableName = "MCPanel.exe",
                TargetVersion = "1.0.1"
            });

            Assert.AreEqual("new", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(install, "current.dll")));
            Assert.IsFalse(File.Exists(Path.Combine(install, "obsolete.dll")));
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://updates.example.com/manifest.json");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"GitHubUpdateRepository\" value=\"vguangshen/MCPanel-override\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://mirror.example.com/frp.zip");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"Ai.Provider\" value=\"OpenAI\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "https://mirror.example.com/v1/chat/completions");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "key=\"Ai.Model\" value=\"ops-model\"");
            StringAssert.Contains(
                File.ReadAllText(Path.Combine(install, "MCPanel.exe.config")),
                "old-ai-key");
            foreach (var name in new[] { "StoreData", "AccountApi", "Runtime", "Downloads", "Tools", "web", "Cache", "Frp", "Nginx", "MySQL", "MSSQL", "Tomcat", "SSMS", "Navicat Premium Lite" })
            {
                Assert.AreEqual(name, File.ReadAllText(Path.Combine(install, name, "preserved.txt")));
            }

            Assert.IsFalse(
                Directory.Exists(Path.Combine(install, "StoreData", "Updates", "Rollback", "Current")),
                "成功更新后不应在安装目录中保留旧版 MCPanel 回滚副本。");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateTransactionRejectsPayloadOutsideControlledInstallStaging()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var payload = Path.Combine(root, "external-payload");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            Directory.CreateDirectory(stagingRoot);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"), "<configuration />");

            Assert.ThrowsException<InvalidDataException>(() =>
                ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
                {
                    InstallDirectory = install,
                    PayloadDirectory = payload,
                    StagingRoot = stagingRoot,
                    MainExecutableName = "MCPanel.exe",
                    TargetVersion = "1.0.1"
                }));
            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void UpdateBackupFailureLeavesOriginalProgramUntouched()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var install = Path.Combine(root, "install");
            var stagingRoot = Path.Combine(
                install,
                "StoreData",
                "Updates",
                "Staging",
                Guid.NewGuid().ToString("N"));
            var payload = Path.Combine(stagingRoot, "Payload");
            Directory.CreateDirectory(install);
            Directory.CreateDirectory(payload);
            File.WriteAllText(Path.Combine(install, "MCPanel.exe"), "old");
            File.WriteAllText(Path.Combine(install, "MCPanel.exe.config"), "<configuration />");
            var lockedFile = Path.Combine(install, "locked.dll");
            File.WriteAllText(lockedFile, "do-not-delete");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe"), "new");
            File.WriteAllText(Path.Combine(payload, "MCPanel.exe.config"), "<configuration />");

            using (File.Open(lockedFile, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.ThrowsException<IOException>(() =>
                    ApplicationUpdateService.ApplyTransaction(new ApplicationUpdatePlan
                    {
                        InstallDirectory = install,
                        PayloadDirectory = payload,
                        StagingRoot = stagingRoot,
                        MainExecutableName = "MCPanel.exe",
                        TargetVersion = "1.0.1"
                    }));
            }

            Assert.AreEqual("old", File.ReadAllText(Path.Combine(install, "MCPanel.exe")));
            Assert.AreEqual("do-not-delete", File.ReadAllText(lockedFile));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
