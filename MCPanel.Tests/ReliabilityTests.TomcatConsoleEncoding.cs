using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatLaunchersAreBomFreeAndPutJavaConsoleOutputInUtf8()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.Tomcat.cs");
        foreach (var method in new[] { "internal static void LaunchTomcatStartConsole", "internal static void LaunchTomcatConsole" })
        {
            var start = runtime.IndexOf(method, StringComparison.Ordinal);
            var end = runtime.IndexOf("ProcessRunner.StartFile(", start, StringComparison.Ordinal);
            Assert.IsTrue(start >= 0 && end > start, method);
            var section = runtime.Substring(start, end - start);
            StringAssert.Contains(section, "WriteTomcatLauncher(", method + " must write its launcher without a BOM.");
            Assert.IsTrue(
                section.IndexOf("chcp 65001 >nul", StringComparison.Ordinal) <
                section.IndexOf("set \"JAVA_TOOL_OPTIONS=%JAVA_TOOL_OPTIONS% {TomcatConsoleUtf8JavaOptions}\"", StringComparison.Ordinal),
                method + " must switch the console to UTF-8 and pass the UTF-8 console options to Java.");
        }

        Assert.IsFalse(runtime.Contains("encoderShouldEmitUTF8Identifier: true"), "No Tomcat launcher may be written with a BOM.");
        foreach (var option in new[] { "-Dsun.stdout.encoding=UTF-8", "-Dsun.stderr.encoding=UTF-8", "-Dstdout.encoding=UTF-8", "-Dstderr.encoding=UTF-8" })
        {
            StringAssert.Contains(EnvironmentRuntimeService.TomcatConsoleUtf8JavaOptions, option);
        }

        Assert.IsFalse(EnvironmentRuntimeService.TomcatConsoleUtf8JavaOptions.Contains("file.encoding"),
            "Only console output may change; the products' default file encoding must stay as it was.");
    }

    [TestMethod]
    public void TomcatLauncherRunsCleanlyFromAGbkConsoleIntoAChineseFolder()
    {
        var root = CreateTemporaryDirectory();
        try
        {
            // A Tomcat folder with a Chinese name, and a startup.bat that reports what Java would receive.
            var tomcat = Path.Combine(root, "中文 Tomcat");
            var bin = Path.Combine(tomcat, "bin");
            Directory.CreateDirectory(bin);
            var report = Path.Combine(root, "report.txt");
            File.WriteAllText(Path.Combine(bin, "startup.bat"),
                "@echo off\r\n> \"" + report + "\" echo CD=%CD%\r\n>> \"" + report + "\" echo JTO=%JAVA_TOOL_OPTIONS%\r\n",
                new UTF8Encoding(false));

            var launcher = Path.Combine(root, "start-tomcat-server.cmd");
            EnvironmentRuntimeService.WriteTomcatLauncher(launcher,
                "@echo off\n" +
                "chcp 65001 >nul\n" +
                "set \"JAVA_TOOL_OPTIONS=%JAVA_TOOL_OPTIONS% " + EnvironmentRuntimeService.TomcatConsoleUtf8JavaOptions + "\"\n" +
                "title MCPanel Tomcat Startup\n" +
                "cd /d \"" + bin + "\"\n" +
                "call \".\\startup.bat\"\n");
            var bytes = File.ReadAllBytes(launcher);
            Assert.AreEqual((byte)'@', bytes[0], "The launcher must start with @echo off, not a BOM.");

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
            Assert.IsFalse(console.Contains("chcp 65001"), "Commands must not be echoed: " + console);

            var lines = File.ReadAllText(report, Encoding.UTF8);
            StringAssert.Contains(lines, "CD=" + bin, "Chinese folder names must resolve after chcp 65001.");
            StringAssert.Contains(lines, "-Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8");
        }
        finally
        {
            DeleteTemporaryTree(root);
        }
    }
}
