using System;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow("安装失败，请检查日志")]
    [DataRow("监听端口 80 已被占用")]
    [DataRow("触发器")]
    [DataRow("服务器")]
    public void MojibakeDetectionFindsUtf8ReadAsGbkButNotGenuineChinese(string original)
    {
        var gbk = Encoding.GetEncoding(936);
        var garbled = gbk.GetString(Encoding.UTF8.GetBytes(original));

        Assert.IsTrue(TextFiles.LooksLikeMojibake(garbled), $"“{garbled}” is UTF-8 “{original}” decoded as GBK.");
        Assert.IsFalse(TextFiles.LooksLikeMojibake(original), $"Genuine Chinese “{original}” must not be flagged.");
    }

    [TestMethod]
    public void MojibakeDetectionNeverFlagsTheApplicationsOwnChineseText()
    {
        var literal = new System.Text.RegularExpressions.Regex("\"([^\"\\\\\\r\\n]*[\\u4E00-\\u9FFF][^\"\\\\\\r\\n]*)\"");
        var checkedCount = 0;
        var flagged = new System.Collections.Generic.List<string>();
        foreach (var file in RepositorySource.ApplicationFiles("*.cs"))
        {
            foreach (System.Text.RegularExpressions.Match match in literal.Matches(System.IO.File.ReadAllText(file)))
            {
                checkedCount++;
                if (TextFiles.LooksLikeMojibake(match.Groups[1].Value))
                {
                    flagged.Add(System.IO.Path.GetFileName(file) + ": " + match.Groups[1].Value);
                }
            }
        }

        Assert.IsTrue(checkedCount > 500, $"Expected the panel's Chinese strings to be scanned, found {checkedCount}.");
        Assert.AreEqual(0, flagged.Count, "Genuine Chinese flagged as mojibake:" + Environment.NewLine + string.Join(Environment.NewLine, flagged));
    }

    [TestMethod]
    public void MojibakeDetectionHandlesAsciiReplacementCharactersAndEmptyText()
    {
        Assert.IsFalse(TextFiles.LooksLikeMojibake("nginx: [emerg] bind() to 0.0.0.0:80 failed"));
        Assert.IsFalse(TextFiles.LooksLikeMojibake(null));
        Assert.IsFalse(TextFiles.LooksLikeMojibake(string.Empty));
        Assert.IsTrue(TextFiles.LooksLikeMojibake("服务��"), "U+FFFD means the bytes were already lost.");
    }

    [TestMethod]
    public void ProtectedCredentialFileEncryptsOnSaveAndReportsPlainTextFiles()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(root, "nested", "mysql.json");
            var credentials = new MySqlDefaultCredentials("127.0.0.1", 3380, "root", "s3cret!");
            ProtectedCredentialFile.Save(path, credentials, c => c.Password, (c, p) => c with { Password = p });
            StringAssert.DoesNotMatch(System.IO.File.ReadAllText(path), new System.Text.RegularExpressions.Regex("s3cret!"),
                "The password must never be written in plain text.");

            var (read, wasProtected) = ProtectedCredentialFile.Read<MySqlDefaultCredentials>(path, c => c.Password, (c, p) => c with { Password = p }, "invalid", "empty");
            Assert.AreEqual(credentials, read);
            Assert.IsTrue(wasProtected);

            System.IO.File.WriteAllText(path, "{\"Host\":\"127.0.0.1\",\"Port\":3380,\"UserName\":\"root\",\"Password\":\"legacy\"}");
            var (legacy, legacyProtected) = ProtectedCredentialFile.Read<MySqlDefaultCredentials>(path, c => c.Password, (c, p) => c with { Password = p }, "invalid", "empty");
            Assert.AreEqual("legacy", legacy.Password);
            Assert.IsFalse(legacyProtected, "An older plain-text file is reported so the store re-saves it encrypted.");

            System.IO.File.WriteAllText(path, "{\"Host\":\"127.0.0.1\",\"Port\":3380,\"UserName\":\"root\",\"Password\":\"\"}");
            var error = Assert.ThrowsException<System.IO.InvalidDataException>(() =>
                ProtectedCredentialFile.Read<MySqlDefaultCredentials>(path, c => c.Password, (c, p) => c with { Password = p }, "invalid", "empty"));
            Assert.AreEqual("invalid", error.Message);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void JsonStateFileToleratesMissingAndHalfWrittenFilesAndThrottleDropsRepeats()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var path = System.IO.Path.Combine(root, "progress.json");
            Assert.IsFalse(JsonStateFile.TryRead<EnvironmentInstallWorkerProgress>(path, out _));
            System.IO.File.WriteAllText(path, "{\"State\":\"runn");
            Assert.IsFalse(JsonStateFile.TryRead<EnvironmentInstallWorkerProgress>(path, out _), "A half-written file is not an error.");
            JsonStateFile.Write(path, new EnvironmentInstallWorkerProgress("running", 42, "安装中"));
            Assert.IsTrue(JsonStateFile.TryRead<EnvironmentInstallWorkerProgress>(path, out var progress));
            Assert.AreEqual(42d, progress.Percent);

            var throttle = new ProgressWriteThrottle(TimeSpan.FromMinutes(1));
            Assert.IsTrue(throttle.ShouldWrite("下载中", force: false));
            Assert.IsFalse(throttle.ShouldWrite("下载中", force: false), "The same message within the interval is dropped.");
            Assert.IsTrue(throttle.ShouldWrite("解压中", force: false), "A new message always goes through.");
            Assert.IsTrue(throttle.ShouldWrite("解压中", force: true), "A forced write always goes through.");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void HttpUtilFollowsOnlyRealRedirectsAndNormalisesDirectoryUris()
    {
        foreach (var code in new[] { 301, 302, 303, 307, 308 })
        {
            Assert.IsTrue(HttpUtil.IsRedirect((HttpStatusCode)code), code.ToString());
        }

        foreach (var code in new[] { 200, 300, 304, 305, 404 })
        {
            Assert.IsFalse(HttpUtil.IsRedirect((HttpStatusCode)code), code + " carries no Location to follow.");
        }

        Assert.AreEqual("https://svn.example.com/repo/", HttpUtil.EnsureDirectoryUri(new Uri("https://svn.example.com/repo")).AbsoluteUri);
        Assert.AreEqual("https://svn.example.com/repo/", HttpUtil.EnsureDirectoryUri(new Uri("https://svn.example.com/repo/")).AbsoluteUri);
    }

    [TestMethod]
    public void PortAvailabilityRejectsAPortWithAListener()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            Assert.IsTrue(SystemProbe.IsTcpPortListening(port));
            Assert.IsFalse(SystemProbe.IsPortAvailable(port), "A loopback-only listener must still count as taken.");
        }
        finally
        {
            listener.Stop();
        }
    }
}
