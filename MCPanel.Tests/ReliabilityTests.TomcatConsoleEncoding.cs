using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatLaunchersKeepTheSystemConsoleEncoding()
    {
        var runtime = ReadRepositoryFile("Environment/EnvironmentRuntimeService.Tomcat.cs");
        foreach (var method in new[] { "internal static void LaunchTomcatStartConsole", "internal static void LaunchTomcatConsole" })
        {
            var start = runtime.IndexOf(method, StringComparison.Ordinal);
            var end = runtime.IndexOf("ProcessRunner.StartFile(", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, method);
            var section = runtime.Substring(start, end - start);
            StringAssert.Contains(section, "WriteTomcatLauncher(", method + " must write its launcher through the shared writer.");
            Assert.IsFalse(section.Contains("chcp 65001"), method + " must not force the console to UTF-8.");
            Assert.IsFalse(section.Contains("JAVA_TOOL_OPTIONS"), method + " must not change Java's console encoding.");
        }

        Assert.IsFalse(runtime.Contains("encoderShouldEmitUTF8Identifier: true"), "No Tomcat launcher may be written with a BOM.");
        Assert.IsFalse(runtime.Contains("stdout.encoding"), "Java console encoding stays Tomcat's default.");
    }

    [TestMethod]
    public void TomcatLauncherIsWrittenInTheConsoleCodePageAndFallsBackOnlyWhenItMust()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            var launcher = Path.Combine(root, "start.cmd");
            const string script = "@echo off\ntitle MCPanel Tomcat Startup\ncd /d \"C:\\MCPanel\\中文\\Tomcat\\bin\"\ncall \".\\startup.bat\"\n";

            EnvironmentRuntimeService.WriteTomcatLauncher(launcher, script, 936);
            var gbk = File.ReadAllBytes(launcher);
            Assert.AreEqual((byte)'@', gbk[0], "No BOM: cmd would run it as part of the first command.");
            var text = Encoding.GetEncoding(936).GetString(gbk);
            StringAssert.Contains(text, "中文", "The path is written in the console code page (GBK).");
            Assert.IsFalse(text.Contains("chcp"), "A path the code page can hold needs no code-page switch.");
            StringAssert.Contains(text, "\r\n", "cmd needs CRLF line ends.");

            // Characters the code page cannot hold: UTF-8 that switches to 65001 first.
            EnvironmentRuntimeService.WriteTomcatLauncher(launcher, script.Replace("中文", "日本語ｱ€"), 437);
            var utf8 = File.ReadAllBytes(launcher);
            Assert.AreEqual((byte)'@', utf8[0]);
            var fallback = new UTF8Encoding(false).GetString(utf8);
            Assert.IsTrue(fallback.StartsWith("@echo off\r\nchcp 65001 >nul\r\n", StringComparison.Ordinal), fallback);
            StringAssert.Contains(fallback, "日本語ｱ€");
        }
        finally { DeleteTemporaryTree(root); }
    }

    [TestMethod]
    public void TomcatLauncherRunsCleanlyFromAGbkConsoleIntoAChineseFolder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // A Tomcat folder with a Chinese name, and a startup.bat that reports where it ran.
            var tomcat = Path.Combine(root, "中文 Tomcat");
            var bin = Path.Combine(tomcat, "bin");
            Directory.CreateDirectory(bin);
            var report = Path.Combine(root, "report.txt");
            File.WriteAllText(Path.Combine(bin, "startup.bat"),
                "@echo off\r\n> \"" + report + "\" echo CD=%CD%\r\n", Encoding.GetEncoding(936));

            var launcher = Path.Combine(root, "start-tomcat-server.cmd");
            EnvironmentRuntimeService.WriteTomcatLauncher(launcher,
                "@echo off\n" +
                "title MCPanel Tomcat Startup\n" +
                "cd /d \"" + bin + "\"\n" +
                "call \".\\startup.bat\"\n");

            // Start from the console code page a Chinese Windows uses by default.
            var wrapper = Path.Combine(root, "wrapper.bat");
            var output = Path.Combine(root, "console.txt");
            File.WriteAllText(wrapper, "@chcp 936 >nul\r\n@call \"" + launcher + "\" > \"" + output + "\" 2>&1\r\n", Encoding.ASCII);
            using (var process = Process.Start(new ProcessStartInfo("cmd.exe", "/d /c \"" + wrapper + "\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = root
            })!)
            {
                Assert.IsTrue(process.WaitForExit(30000));
            }

            var console = Encoding.GetEncoding(936).GetString(File.ReadAllBytes(output));
            Assert.IsFalse(console.Contains("不是内部或外部命令") || console.Contains("is not recognized"),
                "The launcher printed a command error: " + console);
            Assert.IsFalse(console.Contains("title MCPanel"), "Commands must not be echoed: " + console);

            var lines = Encoding.GetEncoding(936).GetString(File.ReadAllBytes(report));
            var utf8Lines = File.ReadAllText(report, Encoding.UTF8);
            Assert.IsTrue(lines.Contains("CD=" + bin) || utf8Lines.Contains("CD=" + bin),
                "Chinese folder names must resolve: " + lines);
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
