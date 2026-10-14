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
    public async Task IndependentTomcatRequiresPortsToRemainStable()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var wait = TomcatRuntimeProbe.EnsureStableAsync(new[] { port }, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            Assert.IsFalse(wait.IsCompleted);
            await wait;
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task IndependentTomcatRejectsPortThatClosesDuringValidation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var wait = TomcatRuntimeProbe.EnsureStableAsync(new[] { port }, TimeSpan.FromMilliseconds(100), CancellationToken.None);
            listener.Stop();
            await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => wait);
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public async Task IndependentTomcatStabilityCheckRejectsClosedPortsAndSupportsCancellation()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await Assert.ThrowsExceptionAsync<InvalidOperationException>(() => TomcatRuntimeProbe.EnsureStableAsync(
            new[] { port }, TimeSpan.FromMilliseconds(100), CancellationToken.None));
        listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            port = ((IPEndPoint)listener.LocalEndpoint).Port;
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            await Assert.ThrowsExceptionAsync<TaskCanceledException>(() => TomcatRuntimeProbe.EnsureStableAsync(
                new[] { port }, TimeSpan.FromSeconds(1), cancellation.Token));
        }
        finally { listener.Stop(); }
    }

    [TestMethod]
    public void SharedTomcatRestartAndCatalinaDoNotDriveEnvironmentProgress()
    {
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(window.Contains("\"Restart\" when item.Kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        StringAssert.Contains(window, "\"Restart\" => _runtimeService.RestartAsync(kind)");
        StringAssert.Contains(window, "\"CatalinaRun\" => _runtimeService.StartTomcatInCatalinaConsoleAsync()");
        Assert.IsFalse(window.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("tomcatProgress: kind == EnvironmentKind.Tomcat", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在验证 Tomcat 端口稳定监听", StringComparison.Ordinal));
    }
}
