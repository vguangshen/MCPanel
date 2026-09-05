using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatServerStartup_ReportsRealApplicationReadinessOnEnvironmentProgressBar()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
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