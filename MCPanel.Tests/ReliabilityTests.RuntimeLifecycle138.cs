using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MySqlRestartSeparatesServiceHealthFromCredentialVerification()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        StringAssert.Contains(runtime, "--get-server-public-key");
        StringAssert.Contains(runtime, "Wait-RootPasswordReady");
        StringAssert.Contains(runtime, "服务运行正常，但保存的 root 凭据验证未通过");
        StringAssert.Contains(runtime, "private static async Task<string> RunMySqlServiceActionAsync");
        Assert.IsFalse(runtime.Contains("if (!(Test-RootPassword $paths)) { Fail 'MySQL 已启动，但保存的 root 凭据无法验证", StringComparison.Ordinal));
    }

    [TestMethod]
    public void TomcatRestartCannotBeKilledByLateServiceStopCleanup()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var stopStart = services.IndexOf("protected override void OnStop()", StringComparison.Ordinal);
        var shutdownStart = services.IndexOf("protected override void OnShutdown()", stopStart, StringComparison.Ordinal);
        Assert.IsTrue(stopStart >= 0 && shutdownStart > stopStart);
        var normalStop = services.Substring(stopStart, shutdownStart - stopStart);
        StringAssert.Contains(normalStop, "runtime cleanup delegated to MCPanel controller");
        Assert.IsFalse(normalStop.Contains("StopTomcat", StringComparison.Ordinal));
        Assert.IsFalse(normalStop.Contains("shutdown.bat", StringComparison.Ordinal));

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        StringAssert.Contains(runtime, "RetireLegacyTomcatWindowsService(tomcatRoot)");
        StringAssert.Contains(runtime, "LaunchTomcatConsole(tomcatRoot)");
        Assert.IsFalse(runtime.Contains("controlAttempt < 3", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("startup.bat 已返回成功", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "StopSharedTomcatAsync(cancellationToken)");

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        StringAssert.Contains(instances, "public static async Task StopSharedTomcatAsync");
    }
}