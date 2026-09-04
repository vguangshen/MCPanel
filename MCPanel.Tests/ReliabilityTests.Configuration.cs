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
    public void EnvironmentInstallElevationPolicyPromptsOnlyForPrivilegedComponents()
    {
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Iis));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Nginx));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.MySql));
        Assert.IsTrue(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.SqlServer));
        Assert.IsFalse(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.Tomcat));
        Assert.IsFalse(EnvironmentInstallWorker.RequiresImmediateElevation(EnvironmentKind.FrpTunnel));
    }

    [TestMethod]
    public void EnvironmentInstallProgressShowsDownloadSpeedThenResetsForInstall()
    {
        var item = new EnvironmentItem(EnvironmentKind.SqlServer, "SQL Server", "版本", "测试环境组件");
        item.SetBusyState("安装中", "正在准备安装...", 0);

        item.ApplyInstallProgress(new InstallProgress(
            35,
            "正在下载 SQLServer-SSEI-EntDev.exe：350 MB / 1 GB",
            InstallProgressStage.Downloading,
            35,
            "12.5 MB/s"));

        Assert.AreEqual("状态", item.ProgressStageText);
        Assert.AreEqual(35, item.Progress, 0.001);
        Assert.AreEqual("速度 12.5 MB/s", item.DownloadSpeedText);
        Assert.AreEqual(Visibility.Visible, item.DownloadSpeedVisibility);
        Assert.IsFalse(item.IsProgressIndeterminate);

        item.ApplyInstallProgress(new InstallProgress(
            84,
            "正在通过官方安装包安装 SQL Server",
            InstallProgressStage.Installing,
            18));

        Assert.AreEqual("状态", item.ProgressStageText);
        Assert.AreEqual(18, item.Progress, 0.001);
        Assert.AreEqual(string.Empty, item.DownloadSpeedText);
        Assert.AreEqual(Visibility.Collapsed, item.DownloadSpeedVisibility);
        Assert.IsFalse(item.IsProgressIndeterminate);
    }

    [TestMethod]
    public void ProductDownloadProgressCarriesTransferSizeAndSpeed()
    {
        var progress = new ProductDownloadProgress(
            42,
            "正在下载 product.zip",
            1572864,
            10485760,
            "8.5 MB/s");

        Assert.AreEqual(42, progress.Percent, 0.001);
        Assert.AreEqual("已下载 1.5 MB / 10 MB", progress.TransferText);
        Assert.AreEqual("8.5 MB/s", progress.SpeedText);
    }

    [TestMethod]
    public void ProductDownloadProgressCarriesScannedAndDownloadedSize()
    {
        var progress = new ProductDownloadProgress(
            42,
            "正在扫描并下载 product.jar",
            524288,
            10485760,
            "3.2 MB/s",
            7,
            1572864);

        Assert.AreEqual("已扫描 7 项 · 1.5 MB · 已下载 512 KB / 10 MB", progress.TransferText);
    }

    [TestMethod]
    public void AtomicWriteReplacesCompleteFile()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(root, "settings.json");
            AtomicFile.WriteAllText(path, "{\"value\":1}");
            AtomicFile.WriteAllText(path, "{\"value\":2}");
            Assert.AreEqual("{\"value\":2}", File.ReadAllText(path));
            Assert.AreEqual(0, Directory.GetFiles(root, "*.tmp").Length);
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void LocalSecretRoundTripsForCurrentUser()
    {
        const string secret = "test-secret-123";
        var protectedValue = LocalSecretProtector.Protect(secret);
        Assert.IsTrue(LocalSecretProtector.IsProtected(protectedValue));
        Assert.AreEqual(secret, LocalSecretProtector.Unprotect(protectedValue));
    }

    [TestMethod]
    public void GitHubUpdateTokenIsDpapiProtectedAndCanBeCleared()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var credentialFile = Path.Combine(root, "github-update-credential.json");
            var store = new GitHubUpdateCredentialStore(credentialFile);
            const string token = "mcpanel_test_token_0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";

            store.SaveAccessToken(token);

            Assert.IsTrue(store.HasStoredCredential());
            Assert.AreEqual(-1, File.ReadAllText(credentialFile).IndexOf(token, StringComparison.Ordinal));
            Assert.AreEqual(token, store.LoadAccessToken());

            store.ClearAccessToken();
            Assert.IsFalse(store.HasStoredCredential());
            Assert.AreEqual(string.Empty, store.LoadAccessToken());
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void GitHubReleaseManifestRequiresBoundVersionAndSha256()
    {
        const string hash = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var manifest = ApplicationUpdateService.ValidateGitHubReleaseManifest(new GitHubReleaseManifest
        {
            Format = ApplicationUpdateService.GitHubReleaseManifestFormat,
            Version = "v1.1.20",
            PackageName = "MCPanel-1.1.20.zip",
            Sha256 = "sha256:" + hash,
            ReleaseNotes = "测试更新"
        });

        Assert.AreEqual("1.1.20", manifest.Version);
        Assert.AreEqual(hash, manifest.Sha256);
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.ValidateGitHubReleaseManifest(new GitHubReleaseManifest
            {
                Format = ApplicationUpdateService.GitHubReleaseManifestFormat,
                Version = "1.1.20",
                PackageName = "other.zip",
                Sha256 = hash
            }));
    }

    [TestMethod]
    public void GitHubRepositoryValidationRejectsTraversal()
    {
        Assert.AreEqual("vguangshen/MCPanel", ApplicationUpdateService.NormalizeGitHubRepository(" vguangshen/MCPanel "));
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.NormalizeGitHubRepository("vguangshen/../../other"));
        Assert.ThrowsException<InvalidDataException>(() =>
            ApplicationUpdateService.NormalizeGitHubRepository("https://github.com/vguangshen/MCPanel"));
    }

    [TestMethod]
    public void StorageSizeUsesReadableUnits()
    {
        Assert.AreEqual("0 B", PanelSettingsService.FormatStorageSize(0));
        Assert.AreEqual("1.5 MB", PanelSettingsService.FormatStorageSize(1572864));
        Assert.AreEqual("2 GB", PanelSettingsService.FormatStorageSize(2147483648));
    }

    [TestMethod]
    public void AccountApiStateLivesBesideStoreData()
    {
        var accountApiRoot = Path.GetFullPath(AccountApiStorage.RootPath).TrimEnd(Path.DirectorySeparatorChar);
        var storeDataAccountApi = Path.Combine(
            Path.GetDirectoryName(accountApiRoot) ?? string.Empty,
            "StoreData",
            "AccountApi");

        StringAssert.EndsWith(accountApiRoot, Path.Combine("AccountApi"));
        Assert.AreNotEqual(
            Path.GetFullPath(storeDataAccountApi).TrimEnd(Path.DirectorySeparatorChar),
            accountApiRoot,
            true,
            "Account API 不应再以内嵌目录形式写入 StoreData。");
    }

    [TestMethod]
    public void SplitComponentRootsStayOutsideStoreData()
    {
        var storeDataRoot = Path.GetFullPath(ComponentPaths.StoreDataRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;

        foreach (var componentRoot in new[]
        {
            ComponentPaths.RuntimeRoot,
            ComponentPaths.DownloadsRoot,
            ComponentPaths.ToolsRoot,
            ComponentPaths.ProductIconsRoot
        })
        {
            var normalizedRoot = Path.GetFullPath(componentRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                + Path.DirectorySeparatorChar;

            Assert.IsFalse(
                normalizedRoot.StartsWith(storeDataRoot, StringComparison.OrdinalIgnoreCase),
                $"拆分后的组件目录不应位于 StoreData 下: {componentRoot}");
        }
    }

    [TestMethod]
    public void ComponentStorageMigrationMovesLegacyDirectoriesWithoutOverwritingCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var storeData = Path.Combine(root, "StoreData");
            var legacyRuntimeFile = Path.Combine(storeData, "Runtime", "TomcatProductRuns", "demo", "conf", "server.xml");
            var legacyDownloadFile = Path.Combine(storeData, "Downloads", "Environment", "tomcat.zip");
            var legacyToolFile = Path.Combine(storeData, "Tools", "Installers", "SSMS-Setup.exe");
            var legacyIconFile = Path.Combine(storeData, "ProductIcons", "demo.png");
            foreach (var file in new[] { legacyRuntimeFile, legacyDownloadFile, legacyToolFile, legacyIconFile })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                File.WriteAllText(file, file);
            }

            var existingCacheFile = Path.Combine(root, "Cache", "products-cache.json");
            Directory.CreateDirectory(Path.GetDirectoryName(existingCacheFile)!);
            File.WriteAllText(existingCacheFile, "catalog");

            ComponentStorageMigration.MigrateForTest(root);
            ComponentStorageMigration.MigrateForTest(root);

            Assert.IsTrue(File.Exists(Path.Combine(root, "Runtime", "TomcatProductRuns", "demo", "conf", "server.xml")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Downloads", "Environment", "tomcat.zip")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Tools", "Installers", "SSMS-Setup.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(root, "Cache", "ProductIcons", "demo.png")));
            Assert.AreEqual("catalog", File.ReadAllText(existingCacheFile));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Runtime")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Downloads")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "Tools")));
            Assert.IsFalse(Directory.Exists(Path.Combine(storeData, "ProductIcons")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void NavicatOfficialDownloadMetadataIsStrictlyParsed()
    {
        var html = "<script>var product = \"navicat17_premium_lite_cs_x64.exe\";</script>";
        var product = NavicatPremiumLiteInstaller.ExtractProductFileName(html);

        Assert.AreEqual("navicat17_premium_lite_cs_x64.exe", product);
        Assert.AreEqual(
            NavicatPremiumLiteInstaller.DefaultProductFileName,
            NavicatPremiumLiteInstaller.ExtractProductFileName("<html>missing</html>"));

        var uri = NavicatPremiumLiteInstaller.ParseDownloadUri(
            "{\"download_link\":\"dn.navicat.com.cn/download/navicat17_premium_lite_cs_x64.exe\"}",
            product);
        Assert.AreEqual(Uri.UriSchemeHttps, uri.Scheme);
        Assert.AreEqual("dn.navicat.com.cn", uri.Host);
    }

    [TestMethod]
    public void NavicatDownloadRejectsUntrustedHostsAndProductNames()
    {
        const string product = "navicat17_premium_lite_cs_x64.exe";
        Assert.IsTrue(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("https://navicat-installers.oss-cn-shanghai.aliyuncs.com/download/navicat17_premium_lite_cs_x64.exe?signature=test"),
            product));
        Assert.IsFalse(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("https://example.com/download/navicat17_premium_lite_cs_x64.exe"),
            product));
        Assert.IsFalse(NavicatPremiumLiteInstaller.IsAllowedDownloadUri(
            new Uri("http://dn.navicat.com.cn/download/navicat17_premium_lite_cs_x64.exe"),
            product));
        Assert.ThrowsException<InvalidDataException>(() =>
            NavicatPremiumLiteInstaller.ParseDownloadUri(
                "{\"download_link\":\"evil.example/navicat17_premium_lite_cs_x64.exe\"}",
                product));
    }

    [TestMethod]
    public void NavicatInstallerTargetsDedicatedFolderBesideApplication()
    {
        var arguments = NavicatPremiumLiteInstaller.BuildInstallerArguments(
            @"D:\MCPanel\Navicat Premium Lite",
            @"D:\MCPanel\StoreData\Work\navicat-install.log");

        StringAssert.Contains(arguments, "/VERYSILENT");
        StringAssert.Contains(arguments, "/SUPPRESSMSGBOXES");
        StringAssert.Contains(arguments, "/NORESTART");
        StringAssert.Contains(arguments, "/DIR=\"D:\\MCPanel\\Navicat Premium Lite\"");
        StringAssert.Contains(arguments, "/LOG=");
        StringAssert.Contains(arguments, @"D:\MCPanel\StoreData\Work\navicat-install.log");
    }

    [TestMethod]
    public void DatabaseToolDefaultDetectionUsesOnlyKnownBoundedLayouts()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var programFiles = Path.Combine(root, "Program Files");
            var programFilesX86 = Path.Combine(root, "Program Files (x86)");
            var navicat = Path.Combine(programFiles, "PremiumSoft", "Navicat Premium 12", "navicat.exe");
            var modernSsms = Path.Combine(
                programFiles,
                "Microsoft SQL Server Management Studio 22",
                "Release",
                "Common7",
                "IDE",
                "Ssms.exe");
            var legacySsms = Path.Combine(
                programFilesX86,
                "Microsoft SQL Server",
                "100",
                "Tools",
                "Binn",
                "VSShell",
                "Common7",
                "IDE",
                "Ssms.exe");
            var unrelated = Path.Combine(programFiles, "Unrelated", "Nested", "navicat.exe");
            foreach (var path in new[] { navicat, modernSsms, legacySsms, unrelated })
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllBytes(path, [0]);
            }

            var roots = new[] { programFiles, programFilesX86 };
            var navicatCandidates = DatabaseToolLocator.EnumerateNavicatDefaultCandidates(roots).ToArray();
            var ssmsCandidates = DatabaseToolLocator.EnumerateSsmsDefaultCandidates(roots).ToArray();

            CollectionAssert.Contains(navicatCandidates, navicat);
            CollectionAssert.DoesNotContain(navicatCandidates, unrelated);
            CollectionAssert.Contains(ssmsCandidates, modernSsms);
            CollectionAssert.Contains(ssmsCandidates, legacySsms);
            Assert.IsFalse(navicatCandidates.Any(path => path.IndexOf("Nested", StringComparison.OrdinalIgnoreCase) >= 0));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    [TestMethod]
    public void DesktopDetectionDoesNotRecurseIntoSubdirectories()
    {
        var desktop = CreateTemporaryDirectory();
        try
        {
            var navicat = Path.Combine(desktop, "navicat.exe");
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), navicat);
            var nestedSsms = Path.Combine(desktop, "Nested", "Ssms.exe");
            Directory.CreateDirectory(Path.GetDirectoryName(nestedSsms)!);
            File.Copy(Path.Combine(Environment.SystemDirectory, "notepad.exe"), nestedSsms);

            var detectedNavicat = DatabaseToolLocator.FindDesktop(DatabaseToolKind.Navicat, [desktop]);
            var detectedSsms = DatabaseToolLocator.FindDesktop(DatabaseToolKind.SqlServerManagementStudio, [desktop]);

            Assert.IsNotNull(detectedNavicat);
            Assert.AreEqual(navicat, detectedNavicat.ExecutablePath, true);
            Assert.IsNull(detectedSsms, "桌面检测不得递归进入子目录。");
        }
        finally
        {
            Directory.Delete(desktop, true);
        }
    }

    [TestMethod]
    public void RegisteredDatabaseToolUninstallCommandsAreParsedWithoutCmdShell()
    {
        var quoted = DatabaseToolUninstaller.ParseRegisteredCommand(
            "\"C:\\Program Files\\PremiumSoft\\Navicat Premium 17\\unins000.exe\" /SILENT");
        Assert.IsNotNull(quoted);
        Assert.AreEqual(@"C:\Program Files\PremiumSoft\Navicat Premium 17\unins000.exe", quoted.FileName);
        Assert.AreEqual("/SILENT", quoted.Arguments);

        var msi = DatabaseToolUninstaller.ParseRegisteredCommand("MsiExec.exe /I{12345678-1234-1234-1234-1234567890AB}");
        Assert.IsNotNull(msi);
        Assert.AreEqual("MsiExec.exe", msi.FileName);
        Assert.AreEqual(
            "/X{12345678-1234-1234-1234-1234567890AB}",
            DatabaseToolUninstaller.ConvertMsiInstallToUninstall(msi.Arguments));
    }

    [TestMethod]
    public void EnvironmentDownloadSettingsAcceptsOverridesAndRejectsInvalidUrls()
    {
        var settings = new NameValueCollection
        {
            [EnvironmentDownloadSettings.FrpPackageKey] = "https://mirror.example.com/frp.zip",
            [EnvironmentDownloadSettings.NginxPackageKey] = "not-a-url"
        };

        var downloads = EnvironmentDownloadSettings.Load(settings);

        Assert.AreEqual("https://mirror.example.com/frp.zip", downloads.FrpPackageUrl);
        Assert.AreEqual(EnvironmentDownloadSettings.DefaultNginxPackageUrl, downloads.NginxPackageUrl);
        Assert.AreEqual(
            "https://aka.ms/sql2025EnterpriseDev",
            downloads.SqlServer2025EnterpriseDeveloperUrl);
    }

    [TestMethod]
    public void MySqlReleaseCatalogUsesOfficialWindowsArchives()
    {
        Assert.AreEqual(MySqlReleaseCatalog.MySql8411Id, MySqlReleaseCatalog.Default.Id);
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
            MySqlReleaseCatalog.MySql8411.PackageUrl);
        Assert.AreEqual("mysql-8.4.11-winx64.zip", MySqlReleaseCatalog.MySql8411.PackageFileName);
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-9.7/mysql-9.7.2-winx64.zip",
            MySqlReleaseCatalog.MySql972.PackageUrl);
        Assert.AreEqual("mysql-9.7.2-winx64.zip", MySqlReleaseCatalog.MySql972.PackageFileName);
        Assert.AreEqual("MySQL 8.4.11 LTS", MySqlReleaseCatalog.MySql8411.DisplayName);
        Assert.AreEqual("MySQL 5.6.31", MySqlReleaseCatalog.LegacyMySql56.DisplayName);
        Assert.AreEqual(
            MySqlReleaseCatalog.MySql972Id,
            MySqlReleaseCatalog.FindByServerVersion("mysqld Ver 9.7.2 for Win64 on x86_64")?.Id);

        var legacy = MySqlReleaseCatalog.Resolve(
            MySqlReleaseCatalog.LegacyMySql56Id,
            "https://mirror.example.com/mysql.zip");
        Assert.IsTrue(legacy.IsLegacy);
        Assert.AreEqual("https://mirror.example.com/mysql.zip", legacy.PackageUrl);
    }

    [TestMethod]
    public void SupplierDownloadPreservesLegacyHttpButRejectsUntrustedPlainHttp()
    {
        var legacyUri = new Uri("http://regservice.itmc.cn/down/tomcat/nginx-1.14.2.zip");

        Assert.AreEqual(legacyUri, McPanelStoreClient.ValidateDownloadUri(legacyUri));
        Assert.AreEqual(
            "https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip",
            McPanelStoreClient.ValidateDownloadUri(new Uri(MySqlReleaseCatalog.MySql8411.PackageUrl)).AbsoluteUri);
        Assert.ThrowsException<InvalidOperationException>(() =>
            McPanelStoreClient.ValidateDownloadUri(new Uri("http://mirror.example.com/package.zip")));
    }

    [TestMethod]
    public void SupplierCatalogSvnSourcesMatchOriginalUpdateClientRouting()
    {
        Assert.IsTrue(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://update.itmc.org.cn/svn/Shop/Shop/")));
        Assert.IsTrue(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://update.product.itmc.cn:9443/udp/itmc_baike_kuajing/")));
        Assert.IsFalse(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("http://regservice.itmc.cn/down/tomcat/nginx-1.14.2.zip")));
        Assert.IsFalse(
            McPanelStoreClient.IsVendorSvnRepository(
                new Uri("https://cdn.mysql.com/Downloads/MySQL-8.4/mysql-8.4.11-winx64.zip")));
    }

    [TestMethod]
    public void MySqlEnvironmentItemDefaultsToLtsAndPreservesSelection()
    {
        var item = new EnvironmentItem(EnvironmentKind.MySql, "MySQL", "", "test");

        Assert.AreEqual(MySqlReleaseCatalog.MySql8411Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual(Visibility.Visible, item.MySqlVersionSelectorVisibility);

        item.SelectedMySqlReleaseId = MySqlReleaseCatalog.MySql972Id;
        Assert.AreEqual(MySqlReleaseCatalog.MySql972Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual("MySQL 9.7.2 LTS", item.SelectedMySqlRelease.DisplayName);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "MySQL 已安装",
            RuntimeStatusKind.Stopped,
            MySqlReleaseCatalog.MySql972Id,
            "9.7.2"));
        Assert.AreEqual(MySqlReleaseCatalog.MySql972Id, item.SelectedMySqlReleaseId);
        Assert.AreEqual("版本  MySQL 9.7.2 LTS", item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "MySQL 已安装",
            RuntimeStatusKind.Stopped,
            DetectedMySqlVersion: "8.0.36"));
        Assert.AreEqual("版本  MySQL 8.0.36", item.OptionLabel);
    }

    [TestMethod]
    public void SqlServerEnvironmentItemDefaultsToRecommendedReleaseAndPreservesSelection()
    {
        var item = new EnvironmentItem(EnvironmentKind.SqlServer, "SQLServer", "", "test");

        Assert.AreEqual(SqlServerReleaseCatalog.Recommended.Id, item.SelectedSqlServerReleaseId);
        Assert.AreEqual(Visibility.Visible, item.SqlServerVersionSelectorVisibility);
        Assert.AreEqual(
            SqlServerReleaseCatalog.Recommended.DisplayName,
            item.SelectedSqlServerRelease.DisplayName);
        Assert.IsTrue(SqlServerReleaseCatalog.Contains(SqlServerReleaseCatalog.SqlServer2012Id));
        Assert.AreEqual("SQL Server 2012 Express SP4", SqlServerReleaseCatalog.SqlServer2012.DisplayName);
        Assert.IsTrue(SqlServerReleaseCatalog.Contains(SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId));
        Assert.AreEqual(
            "SQL Server 2025 Enterprise Developer",
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloper.DisplayName);
        Assert.AreEqual(
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
            SqlServerReleaseCatalog.FindByInstallation(17, "Enterprise Developer Edition")?.Id);
        Assert.AreEqual(
            SqlServerReleaseCatalog.SqlServer2025Id,
            SqlServerReleaseCatalog.FindByInstallation(17, "Express Edition")?.Id);

        item.SelectedSqlServerReleaseId = SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId;
        Assert.AreEqual(
            "版本  SQL Server 2025 Enterprise Developer",
            item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "SQL Server 已安装",
            RuntimeStatusKind.Stopped,
            DetectedSqlServerReleaseId: SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId,
            DetectedSqlServerDisplayName: "SQL Server 2025 Enterprise Developer"));
        Assert.AreEqual(SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId, item.SelectedSqlServerReleaseId);
        Assert.AreEqual("版本  SQL Server 2025 Enterprise Developer", item.OptionLabel);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "SQL Server 已安装",
            RuntimeStatusKind.Stopped,
            DetectedSqlServerDisplayName: "SQL Server 2019 Standard Edition"));
        Assert.AreEqual("版本  SQL Server 2019 Standard Edition", item.OptionLabel);

        var developerPlan = EnvironmentInstaller.GetSqlServerInstallPlan(
            EnvironmentDownloadSettings.Load(new NameValueCollection()),
            SqlServerReleaseCatalog.SqlServer2025EnterpriseDeveloperId);
        Assert.AreEqual("SQL2025-SSEI-EntDev.exe", developerPlan.InstallerFileName);
        Assert.AreEqual(
            EnvironmentDownloadSettings.DefaultSqlServer2025EnterpriseDeveloperUrl,
            developerPlan.DownloadUrl);
        Assert.IsTrue(developerPlan.UsesSseiInstaller);
        Assert.AreEqual("*", developerPlan.MediaPackageSearchPattern);
        Assert.AreEqual("ISO", developerPlan.MediaType);

        var developerScript = EnvironmentInstaller.BuildModernSqlServerScript(
            "D:\\MCPanel\\Downloads\\SQL2025-SSEI-EntDev.exe",
            "D:\\MCPanel\\MSSQL",
            developerPlan,
            "test-password");
        StringAssert.Contains(developerScript, "FileSystem -eq 'CDFS'");
        StringAssert.Contains(developerScript, "$_.Size -eq $image.Size");
        StringAssert.Contains(developerScript, "Get-ChildItem -LiteralPath $isoRoot -Filter 'setup.exe'");
        StringAssert.Contains(developerScript, "Get-CurrentWindowsUser");
        StringAssert.Contains(developerScript, "Get-SqlAdminAccounts");
        StringAssert.Contains(developerScript, "Ensure-CurrentWindowsSqlLogin");
        StringAssert.Contains(developerScript, "SQLSYSADMINACCOUNTS=' + $adminArgument");

        var legacyScript = EnvironmentInstaller.BuildSqlServer2008Script(
            "D:\\MCPanel\\Downloads\\SQLEXPR_2008_x64.exe",
            "D:\\MCPanel\\MSSQL",
            "test-password",
            "SQL Server 2008 Express");
        StringAssert.Contains(legacyScript, "Get-CurrentWindowsUser");
        StringAssert.Contains(legacyScript, "Get-SqlAdminAccounts");
        StringAssert.Contains(legacyScript, "Ensure-CurrentWindowsSqlLogin");
        StringAssert.Contains(legacyScript, "SQLSYSADMINACCOUNTS=' + $adminArgument");

        var expressPlan = EnvironmentInstaller.GetSqlServerInstallPlan(
            EnvironmentDownloadSettings.Load(new NameValueCollection()),
            SqlServerReleaseCatalog.SqlServer2025Id);
        Assert.AreEqual("Core", expressPlan.MediaType);

        item.SelectedSqlServerReleaseId = SqlServerReleaseCatalog.SqlServer2022Id;
        Assert.AreEqual(SqlServerReleaseCatalog.SqlServer2022Id, item.SelectedSqlServerReleaseId);
        Assert.AreEqual("SQL Server 2022 Express", item.SelectedSqlServerRelease.ToString());
    }

    [TestMethod]
    public void FrpEnvironmentCardSeparatesInstallationFromRuntimeControls()
    {
        var item = new EnvironmentItem(
            EnvironmentKind.FrpTunnel,
            "FRP 内网穿透",
            "frp 0.71.0",
            "test");

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            false,
            false,
            "FRP 尚未安装",
            RuntimeStatusKind.NotInstalled));
        Assert.AreEqual(System.Windows.Visibility.Visible, item.InstallButtonVisibility);
        Assert.AreEqual(System.Windows.Visibility.Collapsed, item.ServiceControlsVisibility);
        Assert.AreEqual("安装", item.ActionText);
        Assert.IsFalse(item.CanStart);
        Assert.IsFalse(item.CanUninstall);

        item.ApplyRuntimeState(new EnvironmentRuntimeState(
            true,
            false,
            "FRP 已安装但未运行",
            RuntimeStatusKind.Stopped));
        Assert.AreEqual(System.Windows.Visibility.Collapsed, item.InstallButtonVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, item.ServiceControlsVisibility);
        Assert.AreEqual(System.Windows.Visibility.Visible, item.FrpManagementVisibility);
        Assert.IsTrue(item.CanStart);
        Assert.IsTrue(item.CanUninstall);
        Assert.IsFalse(item.CanStop);
    }

    [TestMethod]
    public void FrpStartRefusesToInstallOrCreateFilesImplicitly()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            using var manager = new FrpManager(root);

            var exception = Assert.ThrowsException<InvalidOperationException>(() =>
                manager.StartAsync().GetAwaiter().GetResult());

            StringAssert.Contains(exception.Message, "请先在“环境管理”中点击 FRP 的“安装”按钮");
            Assert.IsFalse(manager.IsInstalled);
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Frp")));
            Assert.IsFalse(Directory.Exists(Path.Combine(root, "Downloads", "Frp")));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void FrpUninstallRemovesClientConfigurationAndDownloadCache()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var clientRoot = Path.Combine(root, "Frp");
            var downloadRoot = Path.Combine(root, "Downloads", "Frp");
            Directory.CreateDirectory(clientRoot);
            Directory.CreateDirectory(downloadRoot);
            File.WriteAllText(Path.Combine(clientRoot, "frpc.exe"), "test");
            File.WriteAllText(Path.Combine(clientRoot, "frpc.toml"), "serverAddr = \"127.0.0.1\"");
            File.WriteAllText(Path.Combine(downloadRoot, "frp.zip"), "test");

            using var manager = new FrpManager(root);
            Assert.IsTrue(manager.IsInstalled);

            var message = manager.UninstallAsync().GetAwaiter().GetResult();

            StringAssert.Contains(message, "已卸载");
            Assert.IsFalse(manager.IsInstalled);
            Assert.IsFalse(Directory.Exists(clientRoot));
            Assert.IsFalse(Directory.Exists(downloadRoot));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ElevatedScriptFailureShowsNativeLogTailAndFullPath()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var logPath = Path.Combine(root, "uninstall-test.log");
            File.WriteAllText(logPath, "step started\r\nAccess is denied by the service manager\r\n", Encoding.UTF8);

            var exception = EnvironmentOperationDiagnostics.CreateScriptFailure(
                "测试卸载 ",
                1,
                logPath);

            Assert.AreEqual(1, exception.ExitCode);
            Assert.AreEqual(Path.GetFullPath(logPath), exception.LogPath);
            StringAssert.Contains(exception.Message, "退出码：1");
            StringAssert.Contains(exception.Message, "Access is denied by the service manager");
            StringAssert.Contains(exception.Message, Path.GetFullPath(logPath));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void EnvironmentUninstallScriptsParseAndIncludeFailurePostconditions()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var scripts = new[]
            {
                (Name: "iis", Text: EnvironmentRuntimeService.BuildIisUninstallScript()),
                (Name: "sqlserver", Text: EnvironmentRuntimeService.BuildSqlServerUninstallScript()),
                (Name: "mysql", Text: EnvironmentRuntimeService.BuildMySqlUninstallScript(null)),
                (Name: "mysql-action", Text: EnvironmentRuntimeService.BuildMySqlServiceActionScript(
                    "start",
                    "mysql-service-action.log",
                    "mysql-service-action.result")),
                (Name: "mysql-preparation", Text: EnvironmentInstaller.BuildMySqlInstallPreparationScript()),
                (Name: "mysql-install", Text: EnvironmentInstaller.BuildMySqlScript(
                    @"D:\MCPanel\MySQL",
                    @"D:\MCPanel\MySQL\bin\mysqld.exe",
                    @"D:\MCPanel\MySQL\bin\mysql.exe",
                    "test-password")),
                (Name: "mysql-cleanup", Text: EnvironmentInstaller.BuildMySqlInstallCleanupScript(@"D:\MCPanel\MySQL")),
                (Name: "iis-install", Text: EnvironmentInstaller.BuildIisScript(
                    @"D:\MCPanel\Downloads\URLRewrite.msi")),
                (Name: "action", Text: EnvironmentRuntimeService.BuildElevatedPowerShellScript(
                    "& whoami.exe",
                    Path.Combine(root, "environment-action.log")))
            };
            var parserScript = Path.Combine(root, "parse.ps1");
            File.WriteAllText(parserScript,
                "param([string]$Target)\n$tokens=$null; $errors=$null\n" +
                "[System.Management.Automation.Language.Parser]::ParseFile($Target,[ref]$tokens,[ref]$errors) | Out-Null\n" +
                "if ($errors.Count -gt 0) { $errors | ForEach-Object { Write-Error $_.Message }; exit 1 }\nexit 0\n",
                Encoding.UTF8);

            foreach (var script in scripts)
            {
                var scriptPath = Path.Combine(root, "uninstall-" + script.Name + ".ps1");
                File.WriteAllText(scriptPath, script.Text, Encoding.UTF8);
                using var parser = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{parserScript}\" -Target \"{scriptPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                });
                Assert.IsNotNull(parser);
                parser!.WaitForExit();
                Assert.AreEqual(0, parser.ExitCode, script.Name + ": " + parser.StandardError.ReadToEnd());
                StringAssert.Contains(script.Text, "Start-Transcript");
            }

            StringAssert.Contains(scripts[1].Text, "SQL Server 服务仍然存在");
            StringAssert.Contains(scripts[1].Text, "$migratedDataRoot");
            StringAssert.Contains(scripts[1].Text, "exit 1");
            StringAssert.Contains(scripts[2].Text, "MySQL80 服务仍然存在");
            StringAssert.Contains(scripts[2].Text, "exit 1");
            StringAssert.Contains(scripts[2].Text, "$mysqlInstallationRoots");
            StringAssert.Contains(scripts[2].Text, "MySQL 安装目录与共享 Runtime 根目录重叠");
            StringAssert.Contains(scripts[2].Text, "Removing MySQL installation directory");
            StringAssert.Contains(scripts[2].Text, "非 MCPanel 管理的 MySQL80 服务");
            StringAssert.Contains(scripts[2].Text, "Is-ManagedMySqlPath");
            StringAssert.Contains(scripts[2].Text, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
            Assert.IsFalse(scripts[2].Text.Contains("$deleteTargets", StringComparison.Ordinal));
            StringAssert.Contains(scripts[3].Text, "--bind-address=127.0.0.1");
            Assert.IsFalse(scripts[3].Text.Contains("--skip-networking=0", StringComparison.OrdinalIgnoreCase));
            StringAssert.Contains(scripts[4].Text, "Name -in @('mysqld.exe', 'mysqld-itmc.exe')");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void IisInstallScriptChecksMsiAndIisResetExitCodes()
    {
        var script = EnvironmentInstaller.BuildIisScript(Path.Combine(Path.GetTempPath(), "URLRewrite.msi"));

        StringAssert.Contains(script, "$rewriteProcess = Start-Process");
        StringAssert.Contains(script, "$rewriteProcess.ExitCode -notin @(0, 3010)");
        StringAssert.Contains(script, "if ($LASTEXITCODE -ne 0)");
        StringAssert.Contains(script, "Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -All -NoRestart -ErrorAction Stop");
    }

    [TestMethod]
    public void NginxUninstallRefusesSharedRuntimeRoot()
    {
        Assert.ThrowsException<InvalidOperationException>(
            () => NginxRuntimeManager.EnsureSafeDeleteRoot(ComponentPaths.RuntimeRoot));
        Assert.ThrowsException<InvalidOperationException>(
            () => NginxRuntimeManager.EnsureSafeDeleteRoot(ComponentPaths.LegacyRuntimeRoot));

        var dedicatedRoot = CreateTemporaryDirectory();
        try
        {
            NginxRuntimeManager.EnsureSafeDeleteRoot(dedicatedRoot);
        }
        finally
        {
            DeleteTemporaryTree(dedicatedRoot);
        }
    }

    [TestMethod]
    public void AiProviderSettingsReadsConfigurationOverrides()
    {
        var appSettings = new NameValueCollection
        {
            [AiProviderSettings.ProviderConfigKey] = "OpenAI",
            [AiProviderSettings.EndpointConfigKey] = "https://api.example.com/v1/chat/completions",
            [AiProviderSettings.ModelConfigKey] = "ops-model",
            [AiProviderSettings.ApiKeyConfigKey] = "test-api-key"
        };

        var settings = AiProviderSettings.FromAppSettings(appSettings);

        Assert.AreEqual("OpenAI", settings.Provider);
        Assert.AreEqual("https://api.example.com/v1/chat/completions", settings.Endpoint);
        Assert.AreEqual("ops-model", settings.Model);
        Assert.AreEqual("test-api-key", settings.ApiKey);
    }

    [TestMethod]
    public void SqlServerNewPasswordMatchesOriginalStoreFormat()
    {
        var credentials = SqlServerCredentialStore.CreateNew();

        StringAssert.Matches(credentials.Password, new Regex(@"^it[0-9A-Z]{8}8$"));
        Assert.AreEqual(11, credentials.Password.Length);
    }

    [TestMethod]
    public void SsmsArgumentsUseParametersSharedByOldAndCurrentVersions()
    {
        var arguments = PanelSettingsService.BuildSqlServerArguments(
            new SqlServerDefaultCredentials("localhost", 1433, "sa", "secret", "MSSQLSERVER"));

        StringAssert.Contains(arguments, "-nosplash");
        StringAssert.Contains(arguments, "-S");
        Assert.IsFalse(arguments.Contains("-E"));
        Assert.IsFalse(arguments.Contains("-U"));
        Assert.IsFalse(arguments.Contains("-P"));
    }

    [TestMethod]
    public void SsmsUninstallUsesProductAndChannelInsteadOfInstallPathOrWait()
    {
        var arguments = DatabaseToolUninstaller.BuildModernSsmsUninstallArguments(
            "Microsoft.VisualStudio.Product.Ssms",
            "SSMS.22.SSMS.Release");

        StringAssert.StartsWith(arguments, "uninstall");
        StringAssert.Contains(arguments, "--productId");
        StringAssert.Contains(arguments, "Microsoft.VisualStudio.Product.Ssms");
        StringAssert.Contains(arguments, "--channelId");
        StringAssert.Contains(arguments, "SSMS.22.SSMS.Release");
        Assert.IsFalse(arguments.Contains("--installPath"));
        Assert.IsFalse(arguments.Contains("--wait"));
    }

    [TestMethod]
    public void SsmsReleaseMatchesLegacyAndCurrentSqlServerGenerations()
    {
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(10).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(11).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(12).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(13).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(14).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(15).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(16).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(17).Id);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms22Id, SsmsReleaseCatalog.ResolveForSqlServerMajor(null).Id);
        Assert.AreEqual(11, SqlServerInstallationDetector.TryParseMajorVersion("11.0.7001.0"));
        Assert.AreEqual(17, SqlServerInstallationDetector.TryParseMajorVersion("MSSQL17.MSSQLSERVER"));

        var sqlServer2008R2 = new SqlServerInstallationInfo(
            "MSSQLSERVER",
            "MSSQL10_50.MSSQLSERVER",
            10,
            "10.50.6000.34",
            "Express Edition");
        var recommendation = SsmsReleaseCatalog.ResolveForInstalledSqlServer(sqlServer2008R2);
        Assert.AreEqual(SsmsReleaseCatalog.Ssms18121Id, recommendation.Release.Id);
        StringAssert.Contains(recommendation.Summary, "SQL Server 2008 R2");
        Assert.AreEqual("SQL Server 2008 R2 Express Edition", sqlServer2008R2.GenerationDisplayName);
    }

    [TestMethod]
    public void DatabaseToolViewModelSeparatesSsmsMatchFromInstallStatus()
    {
        var tool = new DatabaseToolViewModel("SQL Server 连接工具", "SSMS");

        tool.Apply(null, @"D:\MCPanel\SSMS", "SSMS 22");

        Assert.AreEqual("未安装", tool.StatusText);
        Assert.AreEqual("（SSMS 22）", tool.MatchedVersionText);

        tool.Apply(
            new DatabaseToolInstallation(
                DatabaseToolKind.SqlServerManagementStudio,
                @"D:\MCPanel\SSMS\Common7\IDE\Ssms.exe",
                DatabaseToolSource.ManagedFolder,
                @"D:\MCPanel\SSMS\Common7\IDE\Ssms.exe",
                "Microsoft SQL Server Management Studio",
                "22.0"),
            @"D:\MCPanel\SSMS",
            "SSMS 22");

        Assert.AreEqual("已安装", tool.StatusText);
        Assert.AreEqual("（SSMS 22）", tool.MatchedVersionText);
    }

    [TestMethod]
    public void DatabaseToolViewModelBuildsStableActionTags()
    {
        var tool = new DatabaseToolViewModel("MySQL 连接工具", "Navicat", "Navicat");

        Assert.AreEqual("NavicatPath", tool.PathActionTag);
        Assert.AreEqual("NavicatInstall", tool.InstallActionTag);
        Assert.AreEqual("NavicatUninstall", tool.UninstallActionTag);
        Assert.AreEqual("NavicatConnect", tool.ConnectActionTag);
        Assert.AreEqual("NavicatConfigure", tool.ConfigureActionTag);
    }

    [TestMethod]
    public void SsmsInstallArgumentsKeepLegacyBootstrapperCompatible()
    {
        var legacy = PanelSettingsService.BuildSsmsInstallArguments(SsmsReleaseCatalog.Ssms18121);
        StringAssert.Contains(legacy, "/quiet");
        StringAssert.Contains(legacy, "/norestart");
        StringAssert.Contains(legacy, "SSMSInstallRoot=");
        Assert.IsFalse(legacy.Contains("--installPath"));

        var current = PanelSettingsService.BuildSsmsInstallArguments(SsmsReleaseCatalog.Ssms22);
        StringAssert.Contains(current, "--installPath");
        StringAssert.Contains(current, "--path");
        Assert.IsFalse(current.Contains("SSMSInstallRoot="));
    }

    [TestMethod]
    public void LegacyRuntimeConfigTextStripsUtf8Bom()
    {
        Assert.AreEqual(
            "serverAddr = \"127.0.0.1\"",
            FrpManager.NormalizeTomlForProcess("\uFEFFserverAddr = \"127.0.0.1\""));
        Assert.AreEqual(
            "-Xms128m",
            EnvironmentInstaller.NormalizeTomcatJvmProperties("\uFEFF-Xms128m"));
    }
}
