using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public async Task TomcatProgressProbeReportsReadinessAndWaitsForStablePorts()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var reports = new List<int>();
            var wait = TomcatRuntimeProbe.WaitForStartupAsync("unused", new[] { port }, CancellationToken.None,
                progress: (ready, total) => { Assert.AreEqual(1, total); reports.Add(ready); });
            Assert.IsFalse(wait.IsCompleted, "检测到监听后仍须等待稳定性验证。");
            await wait;
            CollectionAssert.Contains(reports, 1);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task TomcatProgressProbeRejectsPortThatClosesDuringValidation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var wait = TomcatRuntimeProbe.WaitForStartupAsync("unused", new[] { port }, CancellationToken.None,
                progress: (ready, _) => { if (ready == 1) listener.Stop(); });
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => wait);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task TomcatProgressProbeTimesOutAndSupportsCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var reports = new List<int>();
        await Assert.ThrowsExceptionAsync<TimeoutException>(() => TomcatRuntimeProbe.WaitForStartupAsync(
            "unused", new[] { port }, CancellationToken.None, TimeSpan.FromMilliseconds(50),
            (ready, _) => reports.Add(ready)));
        CollectionAssert.Contains(reports, 0);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        try
        {
            await TomcatRuntimeProbe.WaitForStartupAsync("unused", new[] { port }, cancellation.Token);
            Assert.Fail("取消后不能报告启动成功。");
        }
        catch (OperationCanceledException) { }
    }

    [TestMethod]
    public void SharedTomcatRestartAndCatalinaDoNotDriveEnvironmentProgress()
    {
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(window.Contains("\"Restart\" when item.Kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        StringAssert.Contains(window, "\"Restart\" => await _runtimeService.RestartAsync(item.Kind)");
        StringAssert.Contains(window, "\"CatalinaRun\" => await _runtimeService.StartTomcatInCatalinaConsoleAsync()");
        Assert.IsFalse(window.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("tomcatProgress: kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在验证 Tomcat 端口稳定监听", StringComparison.Ordinal));
    }
}
