using System;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void OperationDiagnosticsReadLegacyCodePageLogsWithoutMojibake()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var ansi = Encoding.Default;
            if (ansi.CodePage == 65001)
            {
                Assert.Inconclusive("The system ANSI code page is UTF-8; there is no legacy encoding to fall back to.");
            }

            const string message = "错误：服务启动失败，端口 3306 已被占用。";
            var log = Path.Combine(root, "install.log");
            File.WriteAllBytes(log, ansi.GetBytes("line one" + Environment.NewLine + message + Environment.NewLine));
            var expected = ansi.GetString(ansi.GetBytes(message));

            StringAssert.EndsWith(EnvironmentOperationDiagnostics.ReadLogTail(log), expected,
                "Diagnostics must show legacy-code-page logs the same way the installer does.");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void FileUtilDeletesReadOnlyTreesAndCopiesWithoutFollowingLinks()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var source = Path.Combine(root, "source");
            Directory.CreateDirectory(Path.Combine(source, "conf"));
            File.WriteAllText(Path.Combine(source, "server.xml"), "skip me");
            File.WriteAllText(Path.Combine(source, "conf", "web.xml"), "keep me");
            File.SetAttributes(Path.Combine(source, "conf", "web.xml"), FileAttributes.ReadOnly);

            var copy = Path.Combine(root, "copy");
            FileUtil.CopyDirectory(source, copy, skipTopLevelFile: "server.xml");
            Assert.IsFalse(File.Exists(Path.Combine(copy, "server.xml")));
            Assert.AreEqual("keep me", File.ReadAllText(Path.Combine(copy, "conf", "web.xml")));

            FileUtil.DeleteDirectory(source);
            Assert.IsFalse(Directory.Exists(source), "Read-only files must not block deletion.");
            FileUtil.DeleteDirectory(source);
            Assert.IsTrue(FileUtil.TryDeleteDirectory(copy));
            Assert.IsTrue(FileUtil.TryDeleteDirectory(null));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void FileUtilHashesMatchTheStandardSha256Digest()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var file = Path.Combine(root, "abc.txt");
            File.WriteAllText(file, "abc", new UTF8Encoding(false));
            const string abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
            Assert.AreEqual(abc, FileUtil.ComputeSha256(file));
            Assert.AreEqual(abc, FileUtil.Sha256Hex("abc"));
            Assert.IsTrue(TextFiles.ContainsAny("Apache Tomcat Server", "iis", "TOMCAT"));
            Assert.IsFalse(TextFiles.ContainsAny("nginx", "iis", "tomcat"));
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }

    [DataTestMethod]
    [DataRow("file:///C:/Windows/System32/calc.exe")]
    [DataRow(@"C:\Windows\System32\calc.exe")]
    [DataRow("javascript:alert(1)")]
    [DataRow("")]
    [DataRow(null)]
    public void ShellOpenRefusesAddressesThatAreNotWebPages(string? url)
    {
        Assert.IsFalse(ShellOpen.TryUrl(url), $"{url} must not be launched.");
    }

    [TestMethod]
    public void PanelArgumentsQuoteTrailingBackslashesCorrectly()
    {
        Assert.AreEqual("\"C:\\My Tools\\\\\"", Compat.QuoteCommandLineArgument(@"C:\My Tools\"));
        Assert.AreEqual(@"localhost\SQLEXPRESS", Compat.QuoteCommandLineArgument(@"localhost\SQLEXPRESS"));
        StringAssert.EndsWith(PanelSettingsService.BuildSqlServerArguments(
            new SqlServerDefaultCredentials("localhost", 1433, "sa", "secret", "MSSQLSERVER")), "-S localhost,1433");
    }
}
