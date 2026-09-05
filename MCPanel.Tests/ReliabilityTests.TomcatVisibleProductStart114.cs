using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatProductStart_IndependentAndCatalinaUseVisibleConsole_WhileSharedServerStaysHidden()
    {
        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var processRunner = ReadRepositoryFile("ProcessRunner.cs");

        StringAssert.Contains(manager,
            "await StartTomcatAsync(tomcatHome, instanceRoot, productId, cancellationToken);");
        StringAssert.Contains(manager,
            "BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: false)");
        StringAssert.Contains(manager,
            "BuildTomcatJavaStartInfo(tomcatHome, instanceRoot, redirectOutput: false)");
        StringAssert.Contains(manager, "CreateNoWindow = redirectOutput");
        StringAssert.Contains(manager,
            "WindowStyle = redirectOutput ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal");

        Assert.IsFalse(manager.Contains(
            "BuildTomcatJavaStartInfo(tomcatHome, catalinaBase, redirectOutput: true)",
            StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("CaptureTomcatOutputAsync(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("PumpTomcatReaderAsync(", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("DisposeProcessAfterCaptureAsync(", StringComparison.Ordinal));

        // Shared Tomcat remains the opposite: managed by the background Windows service.
        StringAssert.Contains(runtime, "TomcatWindowsServiceManager.Start");
        StringAssert.Contains(processRunner,
            "ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden");
    }
}