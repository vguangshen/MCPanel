using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

[TestClass]
public sealed class FrpLatestReleaseTests
{
    private const string Mirror = "https://mirrors.nju.edu.cn/github-release/fatedier/frp/LatestRelease/";

    [TestMethod]
    public async Task ResolvesAgainAndMigratesPreservedVersionedConfiguration()
    {
        var version = "0.71.1";
        using var handler = new FakeHandler(request =>
        {
            Assert.AreEqual(FrpReleaseResolver.LatestReleaseApi, request.RequestUri!.AbsoluteUri);
            Assert.IsTrue(request.Headers.CacheControl!.NoCache);
            return Metadata(version);
        });
        using var client = new HttpClient(handler);
        var first = await FrpReleaseResolver.ResolveAsync(client, Mirror + "frp_0.71.0_windows_amd64.zip", default);
        version = "0.72.0";
        var second = await FrpReleaseResolver.ResolveAsync(client, Mirror + "frp_0.71.0_windows_amd64.zip", default);

        Assert.AreEqual("0.71.1", first.Version);
        Assert.AreEqual(Mirror + "frp_0.72.0_windows_amd64.zip", second.PackageUrl);
        Assert.AreEqual("frp_0.72.0_windows_amd64.zip", second.PackageFileName);
        Assert.AreEqual(2, handler.Urls.Count);
    }

    [TestMethod]
    public async Task MirrorDiscoveryHandlesApiRateLimitsAndSortsVersionsNumerically()
    {
        using var handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri == FrpReleaseResolver.LatestReleaseApi
            ? new HttpResponseMessage(HttpStatusCode.Forbidden)
            : Text("""
                <a href="frp_0.9.9_windows_amd64.zip">old</a>
                <a href='frp_0.10.0_windows_amd64.zip'>new</a>
                <a href="frp_9.0.0_windows_arm64.zip">wrong architecture</a>
                <a href="frp_9.0.0_linux_amd64.tar.gz">wrong OS</a>
                <a href="frp_9.0.0-rc1_windows_amd64.zip">preview</a>
                frp_99.0.0_windows_amd64.zip
                """));
        using var client = new HttpClient(handler);
        var release = await FrpReleaseResolver.ResolveAsync(client, Mirror + "frp_0.71.0_windows_amd64.zip", default);

        Assert.AreEqual("0.10.0", release.Version);
        Assert.AreEqual(Mirror + "frp_0.10.0_windows_amd64.zip", release.PackageUrl);
        Assert.AreEqual(Mirror, handler.Urls[1]);
    }

    [TestMethod]
    public async Task KnownLatestWithoutWindowsAssetDoesNotDowngradeToMirror()
    {
        using var handler = new FakeHandler(_ => Text("""
            {"tag_name":"v0.99.0","draft":false,"prerelease":false,"assets":[]}
            """));
        using var client = new HttpClient(handler);
        await Assert.ThrowsExceptionAsync<NotSupportedException>(() =>
            FrpReleaseResolver.ResolveAsync(client, Mirror, default));
        Assert.AreEqual(1, handler.Urls.Count);
    }

    [TestMethod]
    public async Task LookupFailureDoesNotFallBackToOldInstallationOrCache()
    {
        using var tree = new TemporaryTree();
        tree.InstallOldVersion();
        tree.Write("Downloads/Frp/frp_0.71.0_windows_amd64.zip", Archive("0.71.0"));
        using var handler = new FakeHandler(_ => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => manager.EnsureFrpFilesAsync(null, default));

        Assert.AreEqual("0.71.0", tree.Read("Frp/frp-version.txt"));
        Assert.AreEqual("old client", tree.Read("Frp/frpc.exe"));
        Assert.AreEqual(2, handler.Urls.Count);
    }

    [TestMethod]
    public async Task InstallsResolvedVersionRejectsMislabelledCacheAndPreservesConfiguration()
    {
        using var tree = new TemporaryTree();
        tree.InstallOldVersion();
        tree.Write("Downloads/Frp/frp_0.71.1_windows_amd64.zip", Archive("0.71.0"));
        var config = tree.Read("Frp/frpc.toml");
        var version = "0.71.1";
        using var handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri == FrpReleaseResolver.LatestReleaseApi
            ? Metadata(version)
            : Bytes(Archive(version)));
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror + "frp_0.71.0_windows_amd64.zip");

        await manager.EnsureFrpFilesAsync(null, default);
        Assert.AreEqual("0.71.1", tree.Read("Frp/frp-version.txt"));
        Assert.AreEqual("client 0.71.1", tree.Read("Frp/frpc.exe"));
        Assert.AreEqual(config, tree.Read("Frp/frpc.toml"));

        version = "0.72.0";
        await manager.EnsureFrpFilesAsync(null, default);
        Assert.AreEqual("0.72.0", tree.Read("Frp/frp-version.txt"));
        Assert.AreEqual("client 0.72.0", tree.Read("Frp/frpc.exe"));
        Assert.AreEqual(config, tree.Read("Frp/frpc.toml"));
        CollectionAssert.AreEqual(new[]
        {
            FrpReleaseResolver.LatestReleaseApi, Mirror + "frp_0.71.1_windows_amd64.zip",
            FrpReleaseResolver.LatestReleaseApi, Mirror + "frp_0.72.0_windows_amd64.zip"
        }, handler.Urls);
    }

    [TestMethod]
    public async Task MatchingCacheIsReusedOnlyAfterLatestVersionLookupAndHashValidation()
    {
        using var tree = new TemporaryTree();
        var archive = Archive("0.71.1");
        tree.Write("Downloads/Frp/frp_0.71.1_windows_amd64.zip", archive);
        using var handler = new FakeHandler(_ => Metadata("0.71.1", Hash(archive)));
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror);

        await manager.EnsureFrpFilesAsync(null, default);
        await manager.EnsureFrpFilesAsync(null, default);

        Assert.AreEqual("client 0.71.1", tree.Read("Frp/frpc.exe"));
        Assert.IsTrue(handler.Urls.All(url => url == FrpReleaseResolver.LatestReleaseApi));
        Assert.AreEqual(2, handler.Urls.Count);
    }

    [DataTestMethod]
    [DataRow("missing")]
    [DataRow("old-version")]
    [DataRow("wrong-hash")]
    public async Task MissingOrInvalidMirrorPackageFallsBackToSameOfficialVersion(string failure)
    {
        using var tree = new TemporaryTree();
        var archive = Archive("0.71.1");
        using var handler = new FakeHandler(request =>
        {
            var url = request.RequestUri!.AbsoluteUri;
            if (url == FrpReleaseResolver.LatestReleaseApi) return Metadata("0.71.1", Hash(archive));
            if (!url.StartsWith(Mirror, StringComparison.Ordinal)) return Bytes(archive);
            if (failure == "missing") return new HttpResponseMessage(HttpStatusCode.NotFound);
            return Bytes(Archive(failure == "old-version" ? "0.71.0" : "0.71.1", "tampered client"));
        });
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror);

        await manager.EnsureFrpFilesAsync(null, default);

        Assert.AreEqual("0.71.1", tree.Read("Frp/frp-version.txt"));
        Assert.AreEqual("client 0.71.1", tree.Read("Frp/frpc.exe"));
        Assert.AreEqual("https://github.com/fatedier/frp/releases/download/v0.71.1/frp_0.71.1_windows_amd64.zip", handler.Urls.Last());
        Assert.IsFalse(Directory.EnumerateFiles(tree.Root, "*.part", SearchOption.AllDirectories).Any());
    }

    [TestMethod]
    public async Task FailedDownloadsLeaveInstalledFilesAndMarkerUntouched()
    {
        using var tree = new TemporaryTree();
        tree.InstallOldVersion();
        using var handler = new FakeHandler(request => request.RequestUri!.AbsoluteUri == FrpReleaseResolver.LatestReleaseApi
            ? Metadata("0.71.1")
            : Bytes(Archive("0.71.0")));
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror);

        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => manager.EnsureFrpFilesAsync(null, default));

        Assert.AreEqual("0.71.0", tree.Read("Frp/frp-version.txt"));
        Assert.AreEqual("old client", tree.Read("Frp/frpc.exe"));
        Assert.IsFalse(Directory.EnumerateFiles(tree.Root, "*.part", SearchOption.AllDirectories).Any());
        Assert.IsFalse(File.Exists(tree.PathFor("Downloads/Frp/frp_0.71.1_windows_amd64.zip")));
    }

    [DataTestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CancellationDuringLookupOrDownloadDoesNotTryFallback(bool duringLookup)
    {
        using var tree = new TemporaryTree();
        using var cancellation = new CancellationTokenSource();
        using var handler = new FakeHandler(request =>
        {
            if (!duringLookup && request.RequestUri!.AbsoluteUri == FrpReleaseResolver.LatestReleaseApi)
                return Metadata("0.71.1");
            cancellation.Cancel();
            throw new OperationCanceledException(cancellation.Token);
        });
        using var client = new HttpClient(handler);
        using var manager = new FrpManager(tree.Root, client, Mirror);
        try
        {
            await manager.EnsureFrpFilesAsync(null, cancellation.Token);
            Assert.Fail("Installation must honor cancellation.");
        }
        catch (OperationCanceledException)
        {
        }

        Assert.AreEqual(duringLookup ? 1 : 2, handler.Urls.Count);
        Assert.IsFalse(File.Exists(tree.PathFor("Frp/frp-version.txt")));
    }

    [TestMethod]
    public void ArchiveValidationRequiresResolvedVersionArchitectureAndHash()
    {
        using var tree = new TemporaryTree();
        var archive = Archive("0.71.1");
        var path = tree.Write("package.zip", archive);
        var release = new FrpRelease("0.71.1", "https://example.test/frp.zip", "https://example.test/frp.zip", Hash(archive));

        Assert.IsTrue(FrpManager.IsUsableFrpArchive(path, release));
        Assert.IsFalse(FrpManager.IsUsableFrpArchive(path, release with { Version = "0.72.0", Sha256 = null }));
        Assert.IsFalse(FrpManager.IsUsableFrpArchive(path, release with { Sha256 = new string('0', 64) }));
        tree.Write("package.zip", Archive("0.71.1", architecture: "arm64"));
        Assert.IsFalse(FrpManager.IsUsableFrpArchive(path, release with { Sha256 = null }));
    }

    private static HttpResponseMessage Metadata(string version, string? hash = null) => Text(
        "{\"tag_name\":\"v" + version + "\",\"draft\":false,\"prerelease\":false,\"assets\":[{\"name\":\"frp_" + version +
        "_windows_amd64.zip\",\"digest\":" + (hash is null ? "null" : "\"sha256:" + hash + "\"") + "}]}");

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpResponseMessage Bytes(byte[] bytes) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) };
    private static string Hash(byte[] bytes)
    {
        using var sha256 = SHA256.Create();
        return BitConverter.ToString(sha256.ComputeHash(bytes)).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static byte[] Archive(string version, string? executable = null, string architecture = "amd64")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Add(string name, string text)
            {
                using var writer = new StreamWriter(archive.CreateEntry($"frp_{version}_windows_{architecture}/{name}").Open());
                writer.Write(text);
            }
            Add("frpc.exe", executable ?? "client " + version);
            Add("frpc.toml", "serverAddr = \"127.0.0.1\"\nserverPort = 7000\n");
        }
        return stream.ToArray();
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<string> Urls { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.AbsoluteUri);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class TemporaryTree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "MCPanel.FrpTests", Guid.NewGuid().ToString("N"));
        public string PathFor(string relative) => Path.Combine(Root, relative.Replace('/', Path.DirectorySeparatorChar));
        public string Read(string relative) => File.ReadAllText(PathFor(relative), Encoding.UTF8);
        public string Write(string relative, byte[] bytes)
        {
            var path = PathFor(relative);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
            return path;
        }
        public void InstallOldVersion()
        {
            Write("Frp/frpc.exe", Encoding.UTF8.GetBytes("old client"));
            Write("Frp/frp-version.txt", Encoding.UTF8.GetBytes("0.71.0"));
            Write("Frp/frpc.toml", Encoding.UTF8.GetBytes("serverAddr = \"custom.test\"\nserverPort = 7777\nloginFailExit = false\n"));
        }
        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }
}
