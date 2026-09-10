using System;
using System.IO;
using System.Net;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatServerStartup_ReportsRealApplicationReadinessOnEnvironmentProgressBar()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var probe = ReadRepositoryFile("TomcatRuntimeProbe.cs");
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "EnvironmentViewModels.cs"));
        var xaml = ReadRepositoryFile(Path.Combine("Resources", "MainWindowTemplates.xaml"));
        var processRunner = ReadRepositoryFile("ProcessRunner.cs");

        StringAssert.Contains(runtime, "public sealed record TomcatStartupProgress(");
        StringAssert.Contains(runtime, "Task.Run(() => TomcatWindowsServiceManager.Start())");
        StringAssert.Contains(runtime, "正在加载应用：{ready}/{total}");
        StringAssert.Contains(runtime, "15d + ratio * 75d");
        StringAssert.Contains(runtime, "正在验证 Tomcat 端口稳定监听");
        StringAssert.Contains(runtime, "TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, tomcatPorts, cancellationToken)");

        // The progress counter must not equate an open Connector socket with a
        // successfully initialized web application. Managed product ports are
        // probed through the real Context URL, and startup requires two stable
        // HTTP-ready passes before it can reach success.
        StringAssert.Contains(probe, "ProbeManagedApplicationAsync");
        StringAssert.Contains(probe, "WaitForApplicationsReadyAsync");
        StringAssert.Contains(probe, "BuildProbeUri");
        StringAssert.Contains(probe, "A connector that");
        Assert.IsTrue(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.OK));
        Assert.IsTrue(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.Redirect));
        Assert.IsTrue(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.Unauthorized));
        Assert.IsTrue(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.Forbidden));
        Assert.IsFalse(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.NotFound));
        Assert.IsFalse(TomcatRuntimeProbe.IsApplicationHttpStatusReady(HttpStatusCode.InternalServerError));

        StringAssert.Contains(window, "tomcatProgress: update => Dispatcher.Invoke(() => item.ApplyTomcatStartupProgress(update))");
        StringAssert.Contains(viewModel, "public void ApplyTomcatStartupProgress(TomcatStartupProgress update)");
        StringAssert.Contains(viewModel, "ProgressStageText = \"启动进度\"");
        StringAssert.Contains(xaml, "Value=\"{Binding Progress, Mode=OneWay}\"");

        // Normal shared startup remains console-free. Catalina diagnostic mode is the explicit
        // foreground-console path, while ProcessRunner defaults ordinary commands to Hidden.
        StringAssert.Contains(processRunner, "ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden");
        Assert.IsFalse(runtime.Contains("startup.bat", StringComparison.Ordinal) &&
                       runtime.Contains("windowStyle: ProcessWindowStyle.Normal", StringComparison.Ordinal),
            "Shared EnvironmentRuntimeService startup must not directly open startup.bat in a visible console.");
    }
}
