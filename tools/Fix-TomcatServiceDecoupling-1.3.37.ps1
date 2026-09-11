$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-Text([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path (Get-Location) $Path))
}

function Write-Text([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $Path), $Text, $utf8)
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-Text $Path
    if (-not $text.Contains($Old)) {
        throw "Expected text was not found in $Path.`n--- expected ---`n$Old"
    }
    Write-Text $Path ($text.Replace($Old, $New))
}

Write-Host '1/6 Remove remaining Tomcat SCM-running-state dependency from product runtime detection...'
Replace-Exact 'TomcatProductInstanceManager.cs' @'
        if (tomcatHome is not null && portListening &&
            (TomcatWindowsServiceManager.IsRunningForRoot(tomcatHome) ||
             (IsSharedTomcatRunning() && instanceProcessIds.Length == 0)))
'@ @'
        if (tomcatHome is not null && portListening &&
            IsSharedTomcatRunning() &&
            instanceProcessIds.Length == 0)
'@

Replace-Exact 'TomcatProductInstanceManager.cs' @'
        var shared = home is not null && (TomcatWindowsServiceManager.IsRunningForRoot(home) ||
            java.Any(process => ContainsJavaOptionPath(process.CommandLine, "-Dcatalina.base", home)));
'@ @'
        var shared = home is not null &&
            java.Any(process => ContainsJavaOptionPath(process.CommandLine, "-Dcatalina.base", home));
'@

Replace-Exact 'TomcatProductInstanceManager.cs' @'
            if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatHome))
            {
                TomcatWindowsServiceManager.Stop();
            }
'@ @'
            if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatHome))
            {
                // Ask SCM to stop the wrapper if it owns this Tomcat root, but do
                // not use SCM Running/Stopped as runtime truth. The CATALINA_BASE
                // process check below is authoritative.
                TomcatWindowsServiceManager.Stop();
            }
'@

Write-Host '2/6 Make shared Tomcat stop wait on the actual Java process instead of SCM status...'
Replace-Exact 'EnvironmentRuntimeService.cs' @'
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    await Task.Run(() => TomcatWindowsServiceManager.Stop(), cancellationToken);
                    return "Tomcat Server Windows 服务已停止；单应用 Tomcat 实例不受影响。";
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
'@ @'
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    await Task.Run(() => TomcatWindowsServiceManager.Stop(), cancellationToken);
                    // sc.exe only acknowledges the stop request. Verify the real
                    // shared CATALINA_BASE process, never the SCM status value.
                    for (var attempt = 0; attempt < 80 && TomcatProductInstanceManager.IsSharedTomcatRunning(); attempt++)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                    if (!TomcatProductInstanceManager.IsSharedTomcatRunning())
                    {
                        return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
                    }
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
'@

Write-Host '3/6 Remove obsolete Tomcat IsRunning/IsRunningForRoot SCM helpers...'
Replace-Exact 'ManagedComponentWindowsServices.cs' @'
    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRunning() => ManagedWindowsServiceController.IsRunning(ServiceName);
    public static bool IsRunningForRoot(
        string tomcatRoot,
        [System.Runtime.CompilerServices.CallerMemberName] string callerMemberName = "") =>
        ShouldTreatAsRunningForRoot(callerMemberName) &&
        IsRunning() &&
        ManagedWindowsServiceController.IsRegisteredForRoot(ServiceName, TomcatWindowsServiceHost.ServiceArgument, tomcatRoot);

    // DeployToTomcatAsync writes the new product Service into server.xml. If the
    // shared Tomcat service is already running, restarting it here would make the
    // freshly installed Java product start immediately. Product deployment is
    // intentionally configuration-only: the user starts the product (or the
    // shared all-applications mode) explicitly afterwards.
    internal static bool ShouldTreatAsRunningForRoot(string callerMemberName) =>
        !string.Equals(callerMemberName, "DeployToTomcatAsync", StringComparison.Ordinal);
    public static bool IsRegisteredForRoot(string tomcatRoot) =>
'@ @'
    public static bool IsInstalled() => ManagedWindowsServiceController.IsInstalled(ServiceName);
    public static bool IsRegisteredForRoot(string tomcatRoot) =>
'@

Write-Host '4/6 Update stale Java deployment regression...'
$javaDeploymentTest = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void JavaProductDeploymentDoesNotInspectOrReloadSharedTomcatService()
    {
        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(
            deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal),
            "Java 产品安装不应再根据 Windows Service Running 状态决定是否重载共享 Tomcat。");
        Assert.IsFalse(
            deployment.Contains("sharedServiceRestarted", StringComparison.Ordinal),
            "Java 产品安装不应保留共享服务自动重启分支。");
        StringAssert.Contains(deployment, "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。");
    }
}
'@
Write-Text 'MCPanel.Tests/ReliabilityTests.JavaDeployNoAutostart.cs' $javaDeploymentTest

Write-Host '5/6 Add whole-path regression coverage for Tomcat service decoupling...'
$decouplingTest = @'
using System;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatRuntimePathsUseProcessesAndPortsInsteadOfScmRunningState()
    {
        var services = ReadRepositoryFile("ManagedComponentWindowsServices.cs");
        var managerStart = services.IndexOf("internal static class TomcatWindowsServiceManager", StringComparison.Ordinal);
        var managerEnd = services.IndexOf("internal static class FrpWindowsServiceManager", managerStart, StringComparison.Ordinal);
        Assert.IsTrue(managerStart >= 0 && managerEnd > managerStart);
        var manager = services.Substring(managerStart, managerEnd - managerStart);
        Assert.IsFalse(manager.Contains("IsRunningForRoot", StringComparison.Ordinal));
        Assert.IsFalse(manager.Contains("public static bool IsRunning()", StringComparison.Ordinal));
        StringAssert.Contains(manager, "StartWithoutStatusWait");
        StringAssert.Contains(manager, "StopWithoutStatusWait");
        StringAssert.Contains(manager, "DeleteWithoutStatusWait");

        var serviceStart = services.IndexOf("internal sealed class TomcatWindowsService", StringComparison.Ordinal);
        var serviceEnd = services.IndexOf("internal sealed class FrpWindowsService", serviceStart, StringComparison.Ordinal);
        Assert.IsTrue(serviceStart >= 0 && serviceEnd > serviceStart);
        var service = services.Substring(serviceStart, serviceEnd - serviceStart);
        Assert.IsFalse(service.Contains("WaitForStartupAsync", StringComparison.Ordinal));

        var instances = ReadRepositoryFile("TomcatProductInstanceManager.cs");
        Assert.IsFalse(instances.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(instances, "IsSharedTomcatRunning()");
        StringAssert.Contains(instances, "ContainsJavaOptionPath(process.CommandLine, \"-Dcatalina.base\", home)");

        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        Assert.IsFalse(runtime.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(runtime, "不再等待 Windows 服务状态或读取启动进度");

        var deployment = ReadRepositoryFile("ProductDeploymentService.cs");
        Assert.IsFalse(deployment.Contains("TomcatWindowsServiceManager.IsRunningForRoot", StringComparison.Ordinal));
    }
}
'@
Write-Text 'MCPanel.Tests/ReliabilityTests.TomcatServiceDecoupling137.cs' $decouplingTest

Write-Host '6/6 Verify no Tomcat SCM-running-state callsites remain in production sources...'
$productionFiles = @(
    'EnvironmentRuntimeService.cs',
    'EnvironmentInstaller.cs',
    'ProductDeploymentService.cs',
    'TomcatProductInstanceManager.cs',
    'ManagedComponentWindowsServices.cs'
)
foreach ($file in $productionFiles) {
    $text = Read-Text $file
    if ($text.Contains('TomcatWindowsServiceManager.IsRunningForRoot')) {
        throw "Tomcat SCM running-state dependency still remains in $file."
    }
}

Write-Host 'Tomcat service decoupling audit fixes applied.'
