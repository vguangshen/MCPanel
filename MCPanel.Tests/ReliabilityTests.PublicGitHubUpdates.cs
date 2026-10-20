using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    private const string PublicReleaseApi = "https://api.github.com/repos/vguangshen/MCPanel/releases/";

    [TestMethod]
    public void PublicUpdatesDefaultToOfficialRepositoryWithoutSetup()
    {
        var settings = new NameValueCollection();
        Assert.AreEqual("vguangshen/MCPanel", ApplicationUpdateService.ResolveGitHubUpdateRepository(settings));
        settings[ApplicationUpdateService.GitHubUpdateRepositoryConfigKey] = " ";
        Assert.AreEqual("vguangshen/MCPanel", ApplicationUpdateService.ResolveGitHubUpdateRepository(settings));
        settings["UpdateManifestUrl"] = "https://updates.example.test/manifest.json";
        Assert.AreEqual(string.Empty, ApplicationUpdateService.ResolveGitHubUpdateRepository(settings));
        settings[ApplicationUpdateService.GitHubUpdateRepositoryConfigKey] = " example/public-panel ";
        Assert.AreEqual("example/public-panel", ApplicationUpdateService.ResolveGitHubUpdateRepository(settings));
    }

    [TestMethod]
    public async Task PublicReleaseChecksAndPreparesPackageWithoutCredentialsEvenWithLockedLegacyFile()
    {
        var root = CreateTemporaryDirectory();
        PreparedApplicationUpdate? prepared = null;
        ApplicationUpdateService? service = null;
        try
        {
            var package = CreatePublicUpdateFixture();
            var hash = PublicUpdateHash(package);
            var version = ApplicationUpdateService.CurrentVersionText;
            using var handler = new PublicUpdateHandler(request =>
            {
                var url = request.RequestUri!.AbsoluteUri;
                if (url == PublicReleaseApi + "latest") return PublicReleaseMetadata(version, hash);
                if (url == PublicReleaseApi + "assets/1") return PublicUpdateRedirect("https://release-assets.example.test/manifest");
                if (url == "https://release-assets.example.test/manifest") return PublicReleaseManifest(version, hash);
                if (url == PublicReleaseApi + "assets/2") return PublicUpdateRedirect("https://release-assets.example.test/package");
                Assert.AreEqual("https://release-assets.example.test/package", url);
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(package) };
            });
            using var client = new HttpClient(handler);
            service = new ApplicationUpdateService(client, root);
            var oldCredential = Path.Combine(root, "github-update-credential.json");
            File.WriteAllText(oldCredential, "unreadable legacy credential");
            using var lockedCredential = File.Open(oldCredential, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

            var source = service.LoadGitHubReleaseUpdateSource();
            Assert.AreEqual("vguangshen/MCPanel", source.Repository);
            var update = await service.CheckGitHubReleaseAsync(source, default);
            prepared = await service.PrepareGitHubReleaseAsync(update, null, null, default);

            Assert.AreEqual(version, prepared.Version);
            Assert.IsTrue(File.Exists(Path.Combine(prepared.PayloadDirectory, "MCPanel.exe")));
            Assert.IsTrue(File.Exists(Path.Combine(prepared.PayloadDirectory, "MCPanel.exe.config")));
            Assert.AreEqual(hash, PublicUpdateHash(File.ReadAllBytes(prepared.PackageFile)));
            Assert.AreEqual(5, handler.Urls.Count);
            Assert.IsFalse(Directory.GetFiles(root, "*.part", SearchOption.AllDirectories).Any());
        }
        finally
        {
            service?.DiscardPreparedUpdate(prepared);
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public async Task PublicReleaseStillRejectsDownloadedPackageWithWrongSha256()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var hash = new string('a', 64);
            using var handler = new PublicUpdateHandler(request => request.RequestUri!.AbsoluteUri switch
            {
                PublicReleaseApi + "latest" => PublicReleaseMetadata("99.0.0", hash),
                PublicReleaseApi + "assets/1" => PublicReleaseManifest("99.0.0", hash),
                _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("wrong package") }
            });
            using var client = new HttpClient(handler);
            var service = new ApplicationUpdateService(client, root);
            var update = await service.CheckGitHubReleaseAsync(new("vguangshen/MCPanel"), default);
            await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
                service.PrepareGitHubReleaseAsync(update, null, null, default));
            Assert.IsFalse(Directory.GetFiles(root, "*.zip", SearchOption.AllDirectories).Any());
        }
        finally { DeleteTemporaryTree(root); }
    }

    [DataTestMethod]
    [DataRow(401)]
    [DataRow(403)]
    [DataRow(429)]
    public async Task PublicUpdateAccessFailuresNeverAskForAToken(int status)
    {
        using var handler = new PublicUpdateHandler(_ => new HttpResponseMessage((HttpStatusCode)status));
        using var client = new HttpClient(handler);
        var error = await Assert.ThrowsExceptionAsync<InvalidOperationException>(() =>
            new ApplicationUpdateService(client).CheckGitHubReleaseAsync(new("vguangshen/MCPanel"), default));
        StringAssert.Contains(error.Message, "稍后重试");
        Assert.IsFalse(error.Message.Contains("令牌"));
        Assert.IsFalse(error.Message.Contains("配置"));
    }

    [DataTestMethod]
    [DataRow("http")]
    [DataRow("missing")]
    [DataRow("loop")]
    public async Task AnonymousAssetDownloadsStillRejectInvalidRedirects(string kind)
    {
        using var handler = new PublicUpdateHandler(request =>
        {
            if (request.RequestUri!.AbsoluteUri == PublicReleaseApi + "latest")
                return PublicReleaseMetadata("99.0.0", new string('a', 64));
            if (kind == "missing") return new HttpResponseMessage(HttpStatusCode.Redirect);
            return PublicUpdateRedirect(kind == "http" ? "http://example.test/manifest" : PublicReleaseApi + "assets/1");
        });
        using var client = new HttpClient(handler);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            new ApplicationUpdateService(client).CheckGitHubReleaseAsync(new("vguangshen/MCPanel"), default));
    }

    [DataTestMethod]
    [DataRow("tag")]
    [DataRow("digest")]
    public async Task PublicReleaseStillBindsManifestToTagAndAssetDigest(string mismatch)
    {
        var hash = new string('a', 64);
        using var handler = new PublicUpdateHandler(request => request.RequestUri!.AbsoluteUri == PublicReleaseApi + "latest"
            ? PublicReleaseMetadata("99.0.0", mismatch == "digest" ? new string('b', 64) : hash)
            : PublicReleaseManifest(mismatch == "tag" ? "99.0.1" : "99.0.0", hash));
        using var client = new HttpClient(handler);
        await Assert.ThrowsExceptionAsync<InvalidDataException>(() =>
            new ApplicationUpdateService(client).CheckGitHubReleaseAsync(new("vguangshen/MCPanel"), default));
    }

    [TestMethod]
    public void SoftwareUpdateCardOffersDirectUpdateWithoutGitHubConfiguration()
    {
        RunWebsiteUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                window.Show();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                ((FrameworkElement)window.FindName("HomePage")).Visibility = Visibility.Collapsed;
                ((FrameworkElement)window.FindName("SettingsPage")).Visibility = Visibility.Visible;
                window.UpdateLayout();
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                var card = (Border)window.FindName("SoftwareUpdateCard");
                var buttons = FindVisualChildren<Button>(card).Select(button => button.Content as string).ToArray();
                CollectionAssert.Contains(buttons, "检查并更新");
                CollectionAssert.Contains(buttons, "选择更新包");
                Assert.IsFalse(buttons.Any(text => text?.Contains("GitHub") == true));
                Assert.IsTrue(((Button)window.FindName("CheckOnlineUpdateButton")).IsEnabled);
                Assert.IsFalse(FindVisualChildren<TextBlock>(card).Any(text => text.Text.Contains("私有")));
                Assert.IsTrue(card.ActualWidth > 0 && card.ActualHeight > 0);
                SaveVectorIconPreview(card, (int)Math.Ceiling(card.ActualWidth),
                    (int)Math.Ceiling(card.ActualHeight), "public-update-settings");
            }
            finally { window.Close(); }
        });
    }

    private static byte[] CreatePublicUpdateFixture()
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "MCPanel.exe"), "MCPanel.exe");
            archive.CreateEntryFromFile(Path.Combine(AppContext.BaseDirectory, "MCPanel.exe.config"), "MCPanel.exe.config");
        }
        return stream.ToArray();
    }
    private static string PublicUpdateHash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }
    private static HttpResponseMessage PublicUpdateJson(object value) =>
        new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    private static HttpResponseMessage PublicReleaseMetadata(string version, string hash) => PublicUpdateJson(new
    {
        tag_name = "v" + version, draft = false, prerelease = false,
        assets = new[] { new { id = 1, name = "update-manifest.json", digest = "" },
            new { id = 2, name = $"MCPanel-{version}.zip", digest = "sha256:" + hash } }
    });
    private static HttpResponseMessage PublicReleaseManifest(string version, string hash) => PublicUpdateJson(new
    {
        format = "mcpanel-github-release-v1", version, packageName = $"MCPanel-{version}.zip", sha256 = hash
    });
    private static HttpResponseMessage PublicUpdateRedirect(string location) =>
        new(HttpStatusCode.Redirect) { Headers = { Location = new Uri(location) } };
    private sealed class PublicUpdateHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsNull(request.Headers.Authorization, "Public updates must never send credentials, including on redirects.");
            Assert.AreEqual("https", request.RequestUri!.Scheme);
            Assert.IsTrue(request.Headers.UserAgent.Any());
            Urls.Add(request.RequestUri.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }
}
