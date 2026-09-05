$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $repoRoot $Path), $Text, $utf8NoBom)
}

function Replace-RegexOnce([string]$Text, [string]$Pattern, [string]$Replacement, [string]$Description) {
    $matches = [regex]::Matches($Text, $Pattern)
    if ($matches.Count -ne 1) {
        throw "Expected one match while $Description, found $($matches.Count)."
    }
    return [regex]::Replace($Text, $Pattern, $Replacement)
}

Write-Host '1/6 Adding the shared Tomcat startup progress contract and runtime reporting...'
$runtimePath = 'EnvironmentRuntimeService.cs'
$runtime = Read-RepoText $runtimePath

if (-not $runtime.Contains('public sealed record TomcatStartupProgress(')) {
    $snapshotPattern = '(?ms)(public sealed record EnvironmentRuntimeSnapshot\(\r?\n    IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> States,\r?\n    IReadOnlyDictionary<EnvironmentKind, string\?> InstallDirectories\);\r?\n)'
    $snapshotReplacement = @'
public sealed record EnvironmentRuntimeSnapshot(
    IReadOnlyDictionary<EnvironmentKind, EnvironmentRuntimeState> States,
    IReadOnlyDictionary<EnvironmentKind, string?> InstallDirectories);

public sealed record TomcatStartupProgress(
    double Percent,
    string Message,
    int ReadyApplications,
    int TotalApplications);
'@
    $runtime = Replace-RegexOnce $runtime $snapshotPattern ($snapshotReplacement.TrimEnd("`r", "`n") + "`r`n") 'adding TomcatStartupProgress'
}

$runtime = $runtime.Replace(
    'public async Task<string> StartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default)',
    'public async Task<string> StartAsync(EnvironmentKind kind, CancellationToken cancellationToken = default, Action<TomcatStartupProgress>? tomcatProgress = null)')

$startTomcatPattern = '(?ms)            case EnvironmentKind\.Tomcat:\r?\n                var tomcatRoot = RequireTomcatRoot\(\);.*?\r?\n                return \$"Tomcat Server Windows 服务已启动，\{tomcatPorts\.Length\} 个配置端口已确认监听。日志目录：\{Path\.Combine\(tomcatRoot, "logs"\)\}";\r?\n            case EnvironmentKind\.Nginx:'
$startTomcatReplacement = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                var tomcatPorts = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
                if (tomcatPorts.Length == 0)
                {
                    throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。请先修复产品绑定。");
                }

                var productPorts = ProductDeploymentService.LoadTomcatDeploymentInfos()
                    .Select(info => info.Port)
                    .Where(port => tomcatPorts.Contains(port))
                    .Distinct()
                    .ToArray();
                var progressPorts = productPorts.Length > 0 ? productPorts : tomcatPorts;
                var reportsApplications = productPorts.Length > 0;
                tomcatProgress?.Invoke(new TomcatStartupProgress(
                    5,
                    "正在准备共享 Tomcat Server...",
                    0,
                    reportsApplications ? productPorts.Length : 0));
                await Task.Yield();

                if (!TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    var serviceExecutable = Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe");
                    if (!File.Exists(serviceExecutable))
                    {
                        throw new FileNotFoundException("无法定位 MCPanel.exe，不能注册 Tomcat Windows 服务。", serviceExecutable);
                    }
                    TomcatWindowsServiceManager.EnsureRegistered(serviceExecutable, tomcatRoot);
                }

                tomcatProgress?.Invoke(new TomcatStartupProgress(
                    10,
                    "正在切换到后台 Tomcat Windows Service...",
                    0,
                    reportsApplications ? productPorts.Length : 0));
                TomcatProductStartupManager.RemoveRegistration();
                tomcatProgress?.Invoke(new TomcatStartupProgress(
                    15,
                    "正在启动 Tomcat Server Windows 服务...",
                    0,
                    reportsApplications ? productPorts.Length : 0));

                // The Windows service intentionally starts Tomcat without a console window. Run the
                // synchronous SCM wait on a worker thread so the WPF UI can poll the real connector
                // ports while Tomcat loads each configured product.
                var serviceStartTask = Task.Run(() => TomcatWindowsServiceManager.Start());
                while (!serviceStartTask.IsCompleted)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var ready = progressPorts.Count(TomcatRuntimeProbe.IsPortListening);
                    var total = progressPorts.Length;
                    var ratio = total == 0 ? 0d : ready / (double)total;
                    var percent = ready >= total && total > 0
                        ? 92d
                        : 15d + ratio * 75d;
                    var message = reportsApplications
                        ? ready >= total && total > 0
                            ? $"应用已就绪：{ready}/{total}，正在等待 Tomcat Server 完成启动..."
                            : $"正在加载应用：{ready}/{total}"
                        : ready >= total && total > 0
                            ? "Tomcat 端口已就绪，正在等待 Windows 服务完成启动..."
                            : $"正在等待 Tomcat 端口：{ready}/{total}";
                    tomcatProgress?.Invoke(new TomcatStartupProgress(
                        percent,
                        message,
                        reportsApplications ? ready : 0,
                        reportsApplications ? total : 0));
                    await Task.Delay(250, cancellationToken);
                }

                await serviceStartTask;
                tomcatProgress?.Invoke(new TomcatStartupProgress(
                    96,
                    "正在验证 Tomcat 端口稳定监听...",
                    productPorts.Length,
                    productPorts.Length));
                await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, tomcatPorts, cancellationToken);
                tomcatProgress?.Invoke(new TomcatStartupProgress(
                    100,
                    reportsApplications
                        ? $"Tomcat Server 已启动，{productPorts.Length}/{productPorts.Length} 个应用已就绪。"
                        : "Tomcat Server 已启动。",
                    productPorts.Length,
                    productPorts.Length));
                return $"Tomcat Server Windows 服务已启动，{tomcatPorts.Length} 个配置端口已确认监听。日志目录：{Path.Combine(tomcatRoot, "logs")}";
            case EnvironmentKind.Nginx:
'@
$runtime = Replace-RegexOnce $runtime $startTomcatPattern $startTomcatReplacement.TrimEnd("`r", "`n") 'replacing shared Tomcat startup with progress-aware startup'
Write-RepoText $runtimePath $runtime

Write-Host '2/6 Binding runtime progress to the existing Tomcat Server progress bar...'
$windowPath = 'MainWindow.Environment.cs'
$window = Read-RepoText $windowPath
$busyOld = '            item.SetBusyState(RuntimeBusyBadgeText(action), $"{item.Title} 正在{RuntimeActionText(action)}...");'
$busyNew = @'
            var isTomcatStartup = item.Kind == EnvironmentKind.Tomcat && action == "Start";
            item.SetBusyState(
                RuntimeBusyBadgeText(action),
                $"{item.Title} 正在{RuntimeActionText(action)}...",
                isTomcatStartup ? (double?)5 : null);
'@
if (-not $window.Contains($busyOld)) {
    throw 'Unable to locate EnvironmentRuntime_Click busy-state assignment.'
}
$window = $window.Replace($busyOld, $busyNew.TrimEnd("`r", "`n"))

$startDispatchOld = '                    "Start" => await _runtimeService.StartAsync(item.Kind),'
$startDispatchNew = @'
                    "Start" when item.Kind == EnvironmentKind.Tomcat => await _runtimeService.StartAsync(
                        item.Kind,
                        tomcatProgress: update => Dispatcher.Invoke(() => item.ApplyTomcatStartupProgress(update))),
                    "Start" => await _runtimeService.StartAsync(item.Kind),
'@
if (-not $window.Contains($startDispatchOld)) {
    throw 'Unable to locate environment Start dispatch.'
}
$window = $window.Replace($startDispatchOld, $startDispatchNew.TrimEnd("`r", "`n"))
Write-RepoText $windowPath $window

Write-Host '3/6 Adding the Tomcat-specific runtime progress view-model state...'
$viewModelPath = 'ViewModels/EnvironmentViewModels.cs'
$viewModel = Read-RepoText $viewModelPath
if (-not $viewModel.Contains('public void ApplyTomcatStartupProgress(TomcatStartupProgress update)')) {
    $applyRuntimeMarker = '    public void ApplyRuntimeState(EnvironmentRuntimeState state)'
    if (-not $viewModel.Contains($applyRuntimeMarker)) {
        throw 'Unable to locate EnvironmentItem.ApplyRuntimeState.'
    }
    $progressMethod = @'
    public void ApplyTomcatStartupProgress(TomcatStartupProgress update)
    {
        if (update is null || !IsTomcatModule)
        {
            return;
        }

        _statusKind = update.Percent >= 100
            ? RuntimeStatusKind.Running
            : RuntimeStatusKind.Starting;
        _progressStage = InstallProgressStage.Preparing;
        _stagePercent = Compat.Clamp(update.Percent, 0, 100);
        Progress = _stagePercent.Value;
        ProgressStageText = "启动进度";
        DownloadSpeedText = string.Empty;
        StatusText = update.Message;
        BadgeText = update.Percent >= 100 ? "运行中" : "启动中";
        BadgeBrush = RuntimeStatusVisuals.Brush(_statusKind);
        OnPropertyChanged(nameof(IsProgressIndeterminate));
    }

'@
    $viewModel = $viewModel.Replace($applyRuntimeMarker, $progressMethod + $applyRuntimeMarker)
}
Write-RepoText $viewModelPath $viewModel

Write-Host '4/6 Adding regression coverage for hidden startup and real application readiness progress...'
$testPath = 'MCPanel.Tests/ReliabilityTests.TomcatServerProgress113.cs'
$testContent = @'
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
'@
Write-RepoText $testPath ($testContent.TrimStart("`r", "`n"))

Write-Host '5/6 Bumping MCPanel to 1.3.13...'
$projectPath = 'MCPanel.csproj'
$project = Read-RepoText $projectPath
$project = $project.Replace('<Version>1.3.12</Version>', '<Version>1.3.13</Version>')
$project = $project.Replace('<FileVersion>1.3.12.0</FileVersion>', '<FileVersion>1.3.13.0</FileVersion>')
$project = $project.Replace('<AssemblyVersion>1.3.12.0</AssemblyVersion>', '<AssemblyVersion>1.3.13.0</AssemblyVersion>')
if (-not $project.Contains('<Version>1.3.13</Version>')) {
    throw 'Version bump to 1.3.13 failed.'
}
Write-RepoText $projectPath $project

Write-Host '6/6 Auditing the final source before build...'
$runtime = Read-RepoText $runtimePath
$window = Read-RepoText $windowPath
$viewModel = Read-RepoText $viewModelPath
foreach ($token in @(
    'TomcatStartupProgress',
    'Task.Run(() => TomcatWindowsServiceManager.Start())',
    '正在加载应用：{ready}/{total}',
    '正在验证 Tomcat 端口稳定监听'
)) {
    if (-not $runtime.Contains($token)) { throw "Missing runtime progress token: $token" }
}
foreach ($token in @('ApplyTomcatStartupProgress', 'tomcatProgress: update =>')) {
    if (-not $window.Contains($token) -and -not $viewModel.Contains($token)) { throw "Missing UI progress token: $token" }
}
if (-not (Read-RepoText 'ProcessRunner.cs').Contains('ProcessWindowStyle windowStyle = ProcessWindowStyle.Hidden')) {
    throw 'ProcessRunner no longer defaults background commands to a hidden window.'
}

Write-Host 'Tomcat Server startup progress source migration completed successfully.'
