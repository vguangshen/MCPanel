using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using MarchCenter.AccountApi;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void AccountApiHostReportsRealListenerLifecycle()
    {
        Directory.CreateDirectory(AccountApiStorage.RootPath);
        var port = ReserveFreeTcpPort();
        var options = CreateAccountApiTestOptions(port);
        var host = new AccountApiHost(options);
        try
        {
            host.Start();
            Assert.IsTrue(host.IsRunning, "HttpListener 启动后应报告真实监听状态。");
        }
        finally
        {
            host.Dispose();
        }

        Assert.IsFalse(host.IsRunning, "Dispose 返回后 Account API 不得继续报告运行中。");
    }

    [TestMethod]
    public void EmbeddedAccountApiRuntimeCanStopAndImmediatelyRestartSamePort()
    {
        Directory.CreateDirectory(AccountApiStorage.RootPath);
        var root = CreateTemporaryDirectory();
        var configPath = Path.Combine(root, "config.ini");
        var port = ReserveFreeTcpPort();
        File.WriteAllText(configPath, BuildAccountApiTestConfig(port), new UTF8Encoding(false));

        EmbeddedAccountApiRuntime.Stop();
        try
        {
            EmbeddedAccountApiRuntime.Start(configPath);
            Assert.IsTrue(EmbeddedAccountApiRuntime.IsRunning);

            EmbeddedAccountApiRuntime.Stop();
            Assert.IsFalse(EmbeddedAccountApiRuntime.IsRunning,
                "Stop 返回时必须已经释放旧 HttpListener，而不是先把 _host 置空。 ");

            EmbeddedAccountApiRuntime.Start(configPath);
            Assert.IsTrue(EmbeddedAccountApiRuntime.IsRunning,
                "关闭后应能立即重新监听同一个端口，不应误报为其他程序占用。");
        }
        finally
        {
            EmbeddedAccountApiRuntime.Stop();
            DeleteTemporaryTree(root);
        }
    }

    [TestMethod]
    public void AccountApiHostFailsOnlyWhenThePortIsActuallyBound()
    {
        Directory.CreateDirectory(AccountApiStorage.RootPath);
        var blocker = new TcpListener(IPAddress.Loopback, 0);
        blocker.Start();
        var port = ((IPEndPoint)blocker.LocalEndpoint).Port;
        var host = new AccountApiHost(CreateAccountApiTestOptions(port));
        try
        {
            Assert.ThrowsException<HttpListenerException>(() => host.Start(),
                "真实端口冲突应由 HttpListener.Start 返回，而不是由预先 /health 探测猜测。");
        }
        finally
        {
            host.Dispose();
            blocker.Stop();
        }
    }

    private static int ReserveFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static AccountApiOptions CreateAccountApiTestOptions(int port)
    {
        return new AccountApiOptions
        {
            Server = new ServerOptions
            {
                BindAddress = "127.0.0.1",
                Port = port,
                RequestBodyLimitBytes = 65536,
                RequestsPerMinute = 120
            },
            Authentication = new AuthenticationOptions
            {
                Mode = "Hmac",
                SigningSecret = new string('A', 48),
                ClockSkewSeconds = 300
            }
        };
    }

    private static string BuildAccountApiTestConfig(int port)
    {
        return
            "[AccountApi:Server]\r\n" +
            "BindAddress=127.0.0.1\r\n" +
            "Port=" + port + "\r\n" +
            "RequestBodyLimitBytes=65536\r\n" +
            "RequestsPerMinute=120\r\n\r\n" +
            "[AccountApi:Authentication]\r\n" +
            "Mode=Hmac\r\n" +
            "SigningSecret=" + new string('A', 48) + "\r\n" +
            "ClockSkewSeconds=300\r\n\r\n" +
            "[AccountApi]\r\n" +
            "AuditLogDirectory=logs\r\n";
    }
}