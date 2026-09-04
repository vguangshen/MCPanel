using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

[TestClass]
public sealed class ComponentLocatorAndDownloadTests
{
    [TestMethod]
    public void ComponentLocatorUsesExplicitTomcatCapabilities()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var tomcat = Path.Combine(root, "apache-tomcat-9.0.0");
            Directory.CreateDirectory(Path.Combine(tomcat, "bin"));
            Directory.CreateDirectory(Path.Combine(tomcat, "conf"));
            Directory.CreateDirectory(Path.Combine(tomcat, "webapps"));
            File.WriteAllText(Path.Combine(tomcat, "bin", "startup.bat"), "startup");
            File.WriteAllText(Path.Combine(tomcat, "conf", "server.xml"), "<Server />");

            var locator = new ComponentLocator();
            Assert.AreEqual(
                Path.GetFullPath(tomcat),
                locator.FindTomcatRoot(root));
            Assert.IsNull(
                locator.FindTomcatRoot(
                    root,
                    TomcatComponentRequirements.CatalinaScript));

            File.WriteAllText(Path.Combine(tomcat, "bin", "catalina.bat"), "catalina");
            locator = new ComponentLocator();
            Assert.AreEqual(
                Path.GetFullPath(tomcat),
                locator.FindTomcatRoot(
                    root,
                    TomcatComponentRequirements.CatalinaScript));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void ComponentLocatorSkipsExcludedMySqlStagingDirectory()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var staging = Path.Combine(root, "staging");
            var existing = Path.Combine(root, "existing");
            Directory.CreateDirectory(Path.Combine(staging, "bin"));
            Directory.CreateDirectory(Path.Combine(existing, "bin"));
            File.WriteAllText(Path.Combine(staging, "bin", "mysql.exe"), "staging");
            File.WriteAllText(Path.Combine(existing, "bin", "mysql.exe"), "existing");

            var executable = new ComponentLocator().FindMySqlExecutable(root, staging);

            Assert.AreEqual(
                Path.GetFullPath(Path.Combine(existing, "bin", "mysql.exe")),
                executable);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task DownloadServiceMovesOnlyAfterLengthValidation()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(root, "downloads", "package.zip");
            var payload = Encoding.UTF8.GetBytes("MCPanel download payload");
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            var progress = new List<DownloadProgressSnapshot>();
            var validatedPartial = string.Empty;

            var result = await DownloadService.SaveResponseAsync(
                response,
                target,
                progress.Add,
                temporarySuffix: ".part",
                validatePartial: path =>
                {
                    validatedPartial = path;
                    Assert.IsTrue(File.Exists(path));
                });

            Assert.AreEqual(payload.Length, result.BytesReceived);
            CollectionAssert.AreEqual(payload, File.ReadAllBytes(target));
            Assert.AreEqual(target + ".part", validatedPartial);
            Assert.IsFalse(File.Exists(target + ".part"));
            Assert.IsTrue(progress.Count > 0);
            Assert.AreEqual(payload.Length, progress[progress.Count - 1].BytesReceived);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task DownloadServiceDeletesPartialFileWhenLengthDoesNotMatch()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(root, "package.zip");
            var content = new ByteArrayContent(Encoding.UTF8.GetBytes("short"));
            content.Headers.ContentLength = 100;
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = content
            };

            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                DownloadService.SaveResponseAsync(response, target));

            Assert.IsFalse(File.Exists(target));
            Assert.IsFalse(File.Exists(target + ".download"));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task DownloadServiceHonorsPauseControllerBeforeReading()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var target = Path.Combine(root, "package.zip");
            using var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes("paused payload"))
            };
            using var pauseController = new DownloadPauseController();
            Assert.IsTrue(pauseController.Pause());

            var download = DownloadService.SaveResponseAsync(
                response,
                target,
                pauseController: pauseController);
            await Task.Delay(100);

            Assert.IsFalse(download.IsCompleted);
            Assert.IsFalse(File.Exists(target));

            Assert.IsTrue(pauseController.Resume());
            await download;
            Assert.IsTrue(File.Exists(target));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void DeploymentLayoutManifestIsShippedWithTheApplication()
    {
        var manifestPath = Path.Combine(AppContext.BaseDirectory, "deployment-layout.json");

        Assert.IsTrue(File.Exists(manifestPath));
        Assert.AreEqual(20, DeploymentLayoutManifest.PreservedTopLevelNames.Count);
        Assert.IsTrue(File.ReadAllText(manifestPath).Contains("\"web\"", StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task ProcessRunnerCapturesOutputAndExitCode()
    {
        var result = await ProcessRunner.RunAsync(
            "cmd.exe",
            "/d /c echo mcpanel-runner",
            ComponentPaths.ApplicationRoot,
            elevated: false,
            cancellationToken: default,
            captureOutput: true,
            timeout: TimeSpan.FromSeconds(5));

        Assert.AreEqual(0, result.ExitCode);
        Assert.IsTrue(result.StandardOutput.Contains("mcpanel-runner", StringComparison.OrdinalIgnoreCase));
    }

    [TestMethod]
    public void ProcessRunnerStartsWithoutOutputRedirection()
    {
        using var process = ProcessRunner.Start(
            "cmd.exe",
            "/d /c exit 0",
            ComponentPaths.ApplicationRoot,
            elevated: false,
            captureOutput: false);

        process.WaitForExit();
        Assert.AreEqual(0, process.ExitCode);
    }

    [TestMethod]
    public void EnvironmentDownloadCatalogMatchesTheSettingsContract()
    {
        Assert.AreEqual(13, EnvironmentDownloadCatalog.All.Count);
        Assert.AreEqual(EnvironmentDownloadCatalog.All.Count, EnvironmentDownloadSettings.ConfigKeys.Count);

        for (var index = 0; index < EnvironmentDownloadCatalog.All.Count; index++)
        {
            var definition = EnvironmentDownloadCatalog.All[index];
            Assert.AreEqual(definition.Key, EnvironmentDownloadSettings.ConfigKeys[index]);
            Assert.IsTrue(
                Uri.TryCreate(definition.DefaultUrl, UriKind.Absolute, out var uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps),
                definition.Key);
        }

        Assert.IsNotNull(EnvironmentDownloadCatalog.Find(EnvironmentDownloadCatalog.SqlServer2025EnterpriseDeveloperKey));
        Assert.IsNotNull(EnvironmentDownloadCatalog.Find(EnvironmentDownloadCatalog.SqlServer2012ExpressX64Key));
        Assert.IsNotNull(EnvironmentDownloadCatalog.Find(EnvironmentDownloadCatalog.SqlServer2012ExpressX86Key));
    }

    [TestMethod]
    public void EnvironmentDownloadValidationReportsMissingAndInvalidEntries()
    {
        var settings = new NameValueCollection
        {
            [EnvironmentDownloadCatalog.IisUrlRewriteKey] = "ftp://mirror.example.com/urlrewrite.msi",
            [EnvironmentDownloadCatalog.SqlServer2025EnterpriseDeveloperKey] = "https://mirror.example.com/sql2025-dev.exe"
        };

        var validation = EnvironmentDownloadSettings.Validate(settings);

        Assert.IsFalse(validation.IsValid);
        Assert.IsTrue(validation.Issues.Any(issue =>
            issue.Key == EnvironmentDownloadCatalog.IisUrlRewriteKey &&
            issue.Reason.Contains("http://", StringComparison.Ordinal)));
        Assert.IsTrue(validation.Issues.Any(issue =>
            issue.Key == EnvironmentDownloadCatalog.SqlServer2012ExpressX64Key &&
            issue.Reason == "缺少配置值"));
        Assert.IsFalse(validation.Issues.Any(issue =>
            issue.Key == EnvironmentDownloadCatalog.SqlServer2025EnterpriseDeveloperKey));
        StringAssert.Contains(
            validation.ToDisplayMessage(),
            "SQL Server 2012 Express (x64)");
    }

    private static string CreateTemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "MCPanel.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void DeleteTemporaryTree(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
        {
            File.SetAttributes(file, FileAttributes.Normal);
        }

        Directory.Delete(path, true);
    }
}
