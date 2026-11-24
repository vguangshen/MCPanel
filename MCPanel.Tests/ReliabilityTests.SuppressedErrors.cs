using System;
using System.IO;
using System.Linq;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void SuppressedErrorLogWritesOneLinePerDistinctErrorAndThrottlesRepeats()
    {
        SuppressedErrorLog.ResetForTest();
        var marker = Guid.NewGuid().ToString("N");
        var subject = @"C:\state\" + marker + ".json";
        var before = ReadSuppressedLog();

        SuppressedErrorLog.Record("测试读取", new InvalidDataException("bad\r\njson " + marker), subject);
        SuppressedErrorLog.Record("测试读取", new InvalidDataException("bad\r\njson " + marker), subject);
        SuppressedErrorLog.Record("测试读取", new IOException("locked " + marker), subject);

        var added = ReadSuppressedLog().Skip(before.Length).Where(line => line.Contains(marker)).ToArray();
        Assert.AreEqual(2, added.Length, string.Join(Environment.NewLine, added));
        StringAssert.Contains(added[0], "测试读取：" + subject);
        StringAssert.Contains(added[0], "InvalidDataException: bad  json " + marker);
        StringAssert.Contains(added[1], "IOException: locked " + marker);
    }

    [TestMethod]
    public void UnreadableDeploymentStateIsRecordedInsteadOfSilentlySkipped()
    {
        SuppressedErrorLog.ResetForTest();
        Directory.CreateDirectory(ComponentPaths.ProductStateRoot);
        var corrupt = Path.Combine(ComponentPaths.ProductStateRoot, "corrupt-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(corrupt, "{ not json");
        try
        {
            var deployments = ProductDeploymentService.LoadIisDeploymentInfos();
            Assert.IsFalse(deployments.Any(info => info.PhysicalPath.Contains("corrupt-")));
            Assert.IsTrue(
                ReadSuppressedLog().Any(line => line.Contains("读取 IIS 部署记录：" + corrupt)),
                "The unreadable state file must be named in " + SuppressedErrorLog.LogPath);
        }
        finally
        {
            File.Delete(corrupt);
        }
    }

    private static string[] ReadSuppressedLog() =>
        File.Exists(SuppressedErrorLog.LogPath) ? File.ReadAllLines(SuppressedErrorLog.LogPath) : Array.Empty<string>();
}
