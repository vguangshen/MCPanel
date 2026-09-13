using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatProductAndSharedServerStartsUseVisibleConsoles()
    {
        var manager = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var templates = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
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

        // Shared Tomcat remains visible, but normal Start/Restart and explicit
        // Catalina diagnostics intentionally use different launch semantics.
        StringAssert.Contains(runtime, "internal static void LaunchTomcatStartConsole(string tomcatRoot)");
        StringAssert.Contains(runtime, "call startup.bat");
        StringAssert.Contains(runtime, "call catalina.bat run");
        StringAssert.Contains(runtime, "ProcessWindowStyle.Normal");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.Start();", StringComparison.Ordinal));

        // Both explicit Catalina entry points must remain available.
        StringAssert.Contains(templates, "Tag=\"CatalinaRun\"");
        StringAssert.Contains(templates, "Tag=\"Catalina\"");
        StringAssert.Contains(templates, "Content=\"以 Catalina 方式启动\"");

        StringAssert.Contains(processRunner,
            "ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden");
    }
}
