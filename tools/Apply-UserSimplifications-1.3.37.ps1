$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$utf8 = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$Path) {
    return [System.IO.File]::ReadAllText((Join-Path (Get-Location) $Path))
}

function Write-RepoText([string]$Path, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path (Get-Location) $Path), $Text, $utf8)
}

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = Read-RepoText $Path
    if (-not $text.Contains($Old)) {
        throw "Expected text was not found in $Path.`n--- expected ---`n$Old"
    }
    Write-RepoText $Path ($text.Replace($Old, $New))
}

function Replace-RegexOne([string]$Path, [string]$Pattern, [string]$Replacement) {
    $text = Read-RepoText $Path
    $regex = [regex]::new($Pattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
    $matches = $regex.Matches($text)
    if ($matches.Count -ne 1) {
        throw "Expected exactly one regex match in $Path, found $($matches.Count). Pattern: $Pattern"
    }
    Write-RepoText $Path ($regex.Replace($text, $Replacement, 1))
}

Write-Host '1/8 Remove duplicate AI model-setting buttons...'
Replace-Exact 'AiAnalysisPage.xaml' @'
                    <Button Content="模型设置" Click="AiSettings_Click" HorizontalAlignment="Left" Margin="0,6,0,0" Padding="12,5" />
'@ @'
                    <TextBlock Text="模型配置读取自 MCPanel.exe.config，修改后重新开始分析即可生效。"
                               FontSize="12" Margin="0,5,0,0"
                               Foreground="{DynamicResource MutedBrush}" />
'@

Replace-RegexOne 'AiAnalysisPage.xaml.cs' '(?m)^    private void AiSettings_Click\(object sender, RoutedEventArgs e\)\s*\{.*?^    \}\r?\n' ''
Replace-Exact 'MainWindow.xaml' @'
                                    <Button Content="AI 模型设置" HorizontalAlignment="Left" Margin="0,12,0,0" Height="32" Style="{StaticResource RoundedRectTonalButton}" Click="AiSettings_Click" />
'@ ''
Replace-RegexOne 'MainWindow.Websites.cs' '(?m)^    private void AiSettings_Click\(object sender, RoutedEventArgs e\)\s*\{.*?^    \}\r?\n' ''
Replace-Exact 'AiAnalysisService.cs' 'throw new InvalidOperationException("尚未配置模型 API Key，请打开“模型设置”填写并保存。");' 'throw new InvalidOperationException("尚未配置模型 API Key，请在 MCPanel.exe.config 的 Ai.ApiKey 中填写。\n配置修改后重新开始分析即可生效。");'

Write-Host '2/8 Restore the website cards and keep only platform/state filters...'
Replace-Exact 'MainWindow.xaml' @'
                            <Grid.ColumnDefinitions><ColumnDefinition Width="132" /><ColumnDefinition Width="142" /><ColumnDefinition Width="Auto" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
'@ @'
                            <Grid.ColumnDefinitions><ColumnDefinition Width="132" /><ColumnDefinition Width="142" /><ColumnDefinition Width="*" /><ColumnDefinition Width="Auto" /></Grid.ColumnDefinitions>
'@
Replace-Exact 'MainWindow.xaml' @'
                            <CheckBox Grid.Column="2" Content="紧凑视图" IsChecked="{Binding CompactWebsites}" VerticalAlignment="Center" />
                            <Button Grid.Column="4" Content="刷新状态" Height="32" Click="RefreshWebsiteStates_Click" ToolTip="批量更新服务状态，不扫描软件目录或访问网页" />
'@ @'
                            <Button Grid.Column="3" Content="刷新状态" Height="32" Click="RefreshWebsiteStates_Click" ToolTip="批量更新服务状态，不扫描软件目录或访问网页" />
'@

$websiteTemplate = @'
    <DataTemplate x:Key="WebsiteRowTemplate">
        <ContentControl Content="{Binding Item}">
            <ContentControl.Style>
                <Style TargetType="ContentControl">
                    <Setter Property="ContentTemplate" Value="{StaticResource InstalledProductCardTemplate}" />
                    <Style.Triggers>
                        <DataTrigger Binding="{Binding IsCustom}" Value="True">
                            <Setter Property="ContentTemplate" Value="{StaticResource CustomWebsiteCardTemplate}" />
                        </DataTrigger>
                    </Style.Triggers>
                </Style>
            </ContentControl.Style>
        </ContentControl>
    </DataTemplate>
    <local:SubtractConverter x:Key="SubtractConverter" />
'@
Replace-RegexOne 'Resources/MainWindowTemplates.xaml' '    <DataTemplate x:Key="WebsiteRowTemplate">.*?</DataTemplate>\r?\n    <local:SubtractConverter x:Key="SubtractConverter" />' $websiteTemplate

$websiteViewModel = @'
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Data;

namespace MCPanel;

public sealed partial class MainViewModel
{
    private string[] _websiteSearchTerms = [];
    private int _websiteTypeFilter;
    private int _websiteStateFilter;
    private bool _updatingWebsiteRows;
    private string _websiteRecordError = string.Empty;
    private string _websiteOperationText = "每 30 秒更新服务状态；仅在此页可见时检测";
    private readonly WebsiteRefreshSchedule _websiteRefreshSchedule = new();
    private readonly WebsiteRowCollection _websiteRows = new();
    public ObservableCollection<WebsiteRow> WebsiteRows => _websiteRows;
    public ICollectionView WebsiteView { get; private set; } = null!;
    public int WebsiteTypeFilter { get => _websiteTypeFilter; set { if (SetProperty(ref _websiteTypeFilter, value)) RefreshWebsiteFilter(); } }
    public int WebsiteStateFilter { get => _websiteStateFilter; set { if (SetProperty(ref _websiteStateFilter, value)) RefreshWebsiteFilter(); } }
    public string WebsiteRecordError { get => _websiteRecordError; private set { SetProperty(ref _websiteRecordError, value); OnPropertyChanged(nameof(WebsiteRecordErrorVisibility)); } }
    public Visibility WebsiteRecordErrorVisibility => string.IsNullOrEmpty(WebsiteRecordError) ? Visibility.Collapsed : Visibility.Visible;
    public string WebsiteOperationText { get => _websiteOperationText; set => SetProperty(ref _websiteOperationText, value); }

    private void InitializeWebsiteFeatures(bool persist)
    {
        _ = persist;
        WebsiteView = new ListCollectionView(WebsiteRows);
        WebsiteView.Filter = item => ((WebsiteRow)item).Item switch
        {
            InstalledProductItem product => MatchesWebsite(product),
            CustomWebsiteItem custom => MatchesWebsite(custom),
            _ => false
        };
        WebsiteView.SortDescriptions.Add(new SortDescription(nameof(WebsiteRow.Order), ListSortDirection.Ascending));
    }

    private bool MatchesWebsiteOptions(bool java, bool running) =>
        (_websiteTypeFilter == 0 || (_websiteTypeFilter == 1 ? java : !java)) &&
        (_websiteStateFilter == 0 || (_websiteStateFilter == 1 ? running : !running));

    private void RefreshWebsiteFilter()
    {
        VisibleInstalledWebsites.Refresh(); VisibleCustomWebsites.Refresh(); WebsiteView.Refresh(); NotifyWebsiteFilter();
    }

    private void SyncWebsiteRows()
    {
        if (_updatingWebsiteRows) return;
        var old = WebsiteRows.ToDictionary(row => row.Id, StringComparer.OrdinalIgnoreCase);
        var rows = new List<WebsiteRow>();
        foreach (var item in CustomWebsites.Cast<object>().Concat(InstalledProducts))
        {
            var id = item is InstalledProductItem product ? "product:" + product.ProductId : "iis:" + ((CustomWebsiteItem)item).Definition.Id;
            var row = old.TryGetValue(id, out var existing) && ReferenceEquals(existing.Item, item)
                ? existing : new WebsiteRow(id, item);
            row.Order = rows.Count;
            rows.Add(row);
        }
        _websiteRows.Replace(rows);
        NotifyWebsiteFilter();
    }

    internal async Task RefreshWebsiteStatesAsync(bool visible, bool minimized, bool force = false)
    {
        if (_disposed) return;
        if (force) _websiteRefreshSchedule.RequestRefresh();
        if (!_websiteRefreshSchedule.TryStart(DateTime.UtcNow, visible, minimized, WebsiteRows.Count > 0)) return;
        var products = InstalledProducts.Where(item => !item.Product.IsBusy).ToArray();
        var custom = CustomWebsites.ToArray();
        var java = products.Where(item => item.IsTomcatDeployment && item.RuntimePort > 0).Select(item => (item.ProductId, item.RuntimePort)).ToArray();
        var hasIis = custom.Length > 0 || products.Any(item => item.IisInfo is not null);
        var directories = products.Select(item => item.InstallPath).Concat(custom.Select(item => item.PhysicalPath)).ToArray();
        var token = _driveCancellation.Token;
        try
        {
            var snapshot = await Task.Run(() => WebsiteRuntimeSnapshot.Capture(hasIis, java, directories, token), token);
            if (_disposed) return;
            WebsiteRuntimeSnapshot.Latest = snapshot;
            var oldStates = products.Select(item => item.CanBrowse).ToArray();
            var oldCustom = custom.Select(item => item.IsRunning).ToArray();
            foreach (var item in products) if (InstalledProducts.Contains(item)) item.ApplyRuntimeSnapshot(snapshot);
            foreach (var item in custom) if (CustomWebsites.Contains(item)) item.RefreshStatus(snapshot.Iis, snapshot.Directories[item.PhysicalPath]);
            if (!oldStates.SequenceEqual(products.Select(item => item.CanBrowse)) || !oldCustom.SequenceEqual(custom.Select(item => item.IsRunning))) RefreshWebsiteFilter();
            WebsiteOperationText = "服务状态更新于 " + DateTime.Now.ToString("HH:mm:ss") + " · 自动间隔 30 秒";
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!_disposed) WebsiteOperationText = "状态暂未更新：" + ex.Message; }
        finally { _websiteRefreshSchedule.Complete(DateTime.UtcNow); }
    }

    private sealed class WebsiteRowCollection : ObservableCollection<WebsiteRow>
    {
        internal void Replace(IEnumerable<WebsiteRow> rows)
        {
            Items.Clear();
            foreach (var row in rows) Items.Add(row);
            OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
            OnCollectionChanged(new System.Collections.Specialized.NotifyCollectionChangedEventArgs(System.Collections.Specialized.NotifyCollectionChangedAction.Reset));
        }
    }
}

public sealed class WebsiteRow(string id, object item) : ObservableObject
{
    public string Id { get; } = id;
    public object Item { get; } = item;
    public bool IsCustom => Item is CustomWebsiteItem;
    public string Name => Item is InstalledProductItem product ? product.DisplayName : ((CustomWebsiteItem)Item).Name;
    public string Path => Item is InstalledProductItem product ? product.InstallPath : ((CustomWebsiteItem)Item).PhysicalPath;
    public int Order { get; set; }
}
'@
Write-RepoText 'ViewModels/MainViewModel.Websites.cs' $websiteViewModel
Replace-RegexOne 'MainWindow.Websites.cs' '(?m)^    internal async void PinWebsite_Click\(object sender, RoutedEventArgs e\)\s*\{.*?^    \}\r?\n\r?\n    internal void ExpandWebsite_Click\(object sender, RoutedEventArgs e\)\s*\{.*?^    \}\r?\n' ''

Write-Host '3/8 Stop using Tomcat service-status waits...'
$servicesPath = 'ManagedComponentWindowsServices.cs'
$services = Read-RepoText $servicesPath
$insertMarker = '    public static void Start(string serviceName, string displayName)'
if (-not $services.Contains('StartWithoutStatusWait')) {
    $helpers = @'
    public static void StartWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            throw new InvalidOperationException($"{displayName} Windows 服务尚未注册，请先重新安装该组件。");
        }

        var result = RunSc(BuildScArguments("start", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1056)
        {
            ThrowCommandFailure($"启动 {displayName} Windows 服务", result);
        }
    }

    public static void StopWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("stop", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1062)
        {
            ThrowCommandFailure($"停止 {displayName} Windows 服务", result);
        }
    }

    public static void DeleteWithoutStatusWait(string serviceName, string displayName)
    {
        if (!IsInstalled(serviceName))
        {
            return;
        }

        var result = RunSc(BuildScArguments("delete", serviceName), elevated: true);
        if (result.ExitCode != 0 && result.ExitCode != 1060)
        {
            ThrowCommandFailure($"删除 {displayName} Windows 服务", result);
        }
    }

'@
    if (-not $services.Contains($insertMarker)) { throw 'Unable to locate ManagedWindowsServiceController.Start.' }
    $services = $services.Replace($insertMarker, $helpers + $insertMarker)
}
$oldTomcatManager = @'
    public static void Start()
    {
        // Clear recovery on every start as well, so machines upgraded from older
        // builds cannot retain an SCM restart policy that masks a Tomcat crash.
        ManagedWindowsServiceController.DisableRecovery(ServiceName, ServiceDisplayName);
        ManagedWindowsServiceController.Start(ServiceName, ServiceDisplayName);
    }

    public static void Stop() => ManagedWindowsServiceController.Stop(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.Delete(ServiceName, ServiceDisplayName);
'@
$newTomcatManager = @'
    public static void Start()
    {
        // Tomcat is launched through SCM, but MCPanel deliberately does not wait
        // for SCM status transitions. The Java process/ports are the runtime truth.
        ManagedWindowsServiceController.DisableRecovery(ServiceName, ServiceDisplayName);
        ManagedWindowsServiceController.StartWithoutStatusWait(ServiceName, ServiceDisplayName);
    }

    public static void Stop() => ManagedWindowsServiceController.StopWithoutStatusWait(ServiceName, ServiceDisplayName);
    public static void Delete() => ManagedWindowsServiceController.DeleteWithoutStatusWait(ServiceName, ServiceDisplayName);
'@
if (-not $services.Contains($oldTomcatManager)) { throw 'Unable to locate TomcatWindowsServiceManager control block.' }
$services = $services.Replace($oldTomcatManager, $newTomcatManager)
$oldServiceProbe = '        TomcatRuntimeProbe.WaitForStartupAsync(_tomcatRoot, ports, cancellationToken).GetAwaiter().GetResult();'
if (-not $services.Contains($oldServiceProbe)) { throw 'Unable to locate Tomcat service readiness wait.' }
$services = $services.Replace($oldServiceProbe, '        // Do not block the Windows service on connector/application readiness; startup.bat returning successfully is sufficient here.')
Write-RepoText $servicesPath $services

Write-Host '4/8 Remove shared-Tomcat progress polling and service-state probing...'
$runtimePath = 'EnvironmentRuntimeService.cs'
$runtime = Read-RepoText $runtimePath
$startPattern = '(?s)            case EnvironmentKind\.Tomcat:\r?\n                var tomcatRoot = RequireTomcatRoot\(\);.*?                return \$"Tomcat Server Windows 服务已启动，\{tomcatPorts\.Length\} 个配置端口已确认监听。日志目录：\{Path\.Combine\(tomcatRoot, "logs"\)\}";\r?\n            case EnvironmentKind\.Nginx:'
$startReplacement = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                var tomcatPorts = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
                if (tomcatPorts.Length == 0)
                {
                    throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。请先修复产品绑定。");
                }

                if (!TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    var serviceExecutable = Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe");
                    if (!File.Exists(serviceExecutable))
                    {
                        throw new FileNotFoundException("无法定位 MCPanel.exe，不能注册 Tomcat Windows 服务。", serviceExecutable);
                    }
                    TomcatWindowsServiceManager.EnsureRegistered(serviceExecutable, tomcatRoot);
                }

                TomcatProductStartupManager.RemoveRegistration();
                TomcatWindowsServiceManager.Start();
                await Task.Delay(500, cancellationToken);
                return $"Tomcat Server 启动请求已发送；不再等待 Windows 服务状态或读取启动进度。日志目录：{Path.Combine(tomcatRoot, "logs")}";
            case EnvironmentKind.Nginx:
'@
$regex = [regex]::new($startPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($regex.Matches($runtime).Count -ne 1) { throw 'Unable to replace Tomcat StartAsync block.' }
$runtime = $regex.Replace($runtime, $startReplacement, 1)

$oldRestart = @'
        if (kind == EnvironmentKind.Tomcat)
            tomcatProgress?.Invoke(new TomcatStartupProgress(5, "正在停止 Tomcat Server...", 0, 0));
        try
'@
if (-not $runtime.Contains($oldRestart)) { throw 'Unable to locate Tomcat restart progress prelude.' }
$runtime = $runtime.Replace($oldRestart, '        try' + [Environment]::NewLine)
$oldStartCall = @'
        await StartAsync(kind, cancellationToken, tomcatProgress: kind == EnvironmentKind.Tomcat
            ? update => tomcatProgress?.Invoke(update with { Percent = 10d + update.Percent * 0.9d })
            : null);
'@
if (-not $runtime.Contains($oldStartCall)) { throw 'Unable to locate restart StartAsync progress forwarding.' }
$runtime = $runtime.Replace($oldStartCall, '        await StartAsync(kind, cancellationToken);' + [Environment]::NewLine)

$oldUninstall = @'
                await TryStopAsync(kind, cancellationToken);
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
'@
$newUninstall = @'
                await TryStopAsync(kind, cancellationToken);
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);
                await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
'@
if (-not $runtime.Contains($oldUninstall)) { throw 'Unable to locate Tomcat uninstall stop sequence.' }
$runtime = $runtime.Replace($oldUninstall, $newUninstall)

$statePattern = '(?s)    private static EnvironmentRuntimeState GetTomcatState\(ComponentLocator locator\)\r?\n    \{.*?\r?\n    \}\r?\n\r?\n    private static EnvironmentRuntimeState GetSqlServerState'
$stateReplacement = @'
    private static EnvironmentRuntimeState GetTomcatState(ComponentLocator locator)
    {
        var tomcatRoot = locator.FindTomcatRoot();
        if (tomcatRoot is null)
        {
            return NotInstalled();
        }

        var sharedProcessRunning = TomcatProductInstanceManager.IsSharedTomcatRunning();
        var sharedRunning = sharedProcessRunning &&
            TomcatRuntimeProbe.ArePortsListening(TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot));
        var managedRunning = TomcatProductInstanceManager.IsAnyManagedTomcatHealthy();
        var anyRunning = sharedRunning || managedRunning;
        var detail = sharedRunning
            ? "全部应用模式正在运行。"
            : managedRunning
                ? "一个或多个应用正在单独运行。"
                : sharedProcessRunning
                    ? "检测到 Tomcat Java 进程，但配置端口未全部监听。"
                    : "当前没有运行中的 Tomcat。";
        return Installed(anyRunning, $"Tomcat 已安装到 {tomcatRoot}。{detail}");
    }

    private static EnvironmentRuntimeState GetSqlServerState
'@
$stateRegex = [regex]::new($statePattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($stateRegex.Matches($runtime).Count -ne 1) { throw 'Unable to replace GetTomcatState.' }
$runtime = $stateRegex.Replace($runtime, $stateReplacement, 1)
Write-RepoText $runtimePath $runtime

Write-Host '5/8 Stop the environment page from binding Tomcat startup to the progress bar...'
$windowEnv = Read-RepoText 'MainWindow.Environment.cs'
$busyOld = @'
            var isTomcatStartup = item.Kind == EnvironmentKind.Tomcat &&
                                  (action is "Start" or "Restart" or "CatalinaRun");
            item.SetBusyState(
                RuntimeBusyBadgeText(action),
                $"{item.Title} 正在{RuntimeActionText(action)}...",
                isTomcatStartup ? (double?)5 : null);
'@
$busyNew = @'
            item.SetBusyState(
                RuntimeBusyBadgeText(action),
                $"{item.Title} 正在{RuntimeActionText(action)}...");
'@
if (-not $windowEnv.Contains($busyOld)) { throw 'Unable to locate Tomcat runtime busy/progress setup.' }
$windowEnv = $windowEnv.Replace($busyOld, $busyNew)
$dispatchOld = @'
                    "Start" when item.Kind == EnvironmentKind.Tomcat => await _runtimeService.StartAsync(
                        item.Kind,
                        tomcatProgress: update => Dispatcher.Invoke(() => item.ApplyTomcatStartupProgress(update))),
                    "Start" => await _runtimeService.StartAsync(item.Kind),
                    "Stop" => await _runtimeService.StopAsync(item.Kind),
                    "Restart" when item.Kind == EnvironmentKind.Tomcat => await _runtimeService.RestartAsync(
                        item.Kind,
                        tomcatProgress: update => Dispatcher.Invoke(() => item.ApplyTomcatStartupProgress(update))),
                    "Restart" => await _runtimeService.RestartAsync(item.Kind),
                    "Uninstall" => await _runtimeService.UninstallAsync(item.Kind),
                    "OpenIis" => OpenIisManager(),
                    "CatalinaRun" => await _runtimeService.StartTomcatInCatalinaConsoleAsync(
                        tomcatProgress: update => Dispatcher.Invoke(() => item.ApplyTomcatStartupProgress(update))),
'@
$dispatchNew = @'
                    "Start" => await _runtimeService.StartAsync(item.Kind),
                    "Stop" => await _runtimeService.StopAsync(item.Kind),
                    "Restart" => await _runtimeService.RestartAsync(item.Kind),
                    "Uninstall" => await _runtimeService.UninstallAsync(item.Kind),
                    "OpenIis" => OpenIisManager(),
                    "CatalinaRun" => await _runtimeService.StartTomcatInCatalinaConsoleAsync(),
'@
if (-not $windowEnv.Contains($dispatchOld)) { throw 'Unable to locate Tomcat progress action dispatch.' }
$windowEnv = $windowEnv.Replace($dispatchOld, $dispatchNew)
Write-RepoText 'MainWindow.Environment.cs' $windowEnv

$environmentVm = Read-RepoText 'ViewModels/EnvironmentViewModels.cs'
$progressMethodPattern = '(?s)\r?\n    public void ApplyTomcatStartupProgress\(TomcatStartupProgress update\)\r?\n    \{.*?\r?\n    \}\r?\n    public void ApplyRuntimeState'
$progressMethodRegex = [regex]::new($progressMethodPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($progressMethodRegex.Matches($environmentVm).Count -ne 1) { throw 'Unable to locate ApplyTomcatStartupProgress.' }
$environmentVm = $progressMethodRegex.Replace($environmentVm, [Environment]::NewLine + '    public void ApplyRuntimeState', 1)
Write-RepoText 'ViewModels/EnvironmentViewModels.cs' $environmentVm

Write-Host '6/8 Remove service-readiness waits from Tomcat installation/deployment...'
$installer = Read-RepoText 'EnvironmentInstaller.cs'
$installWait = @'
        TomcatWindowsServiceManager.Start();
        var ports = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
        await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, ports, cancellationToken);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并注册为自动启动的 Windows 服务 {TomcatWindowsServiceManager.ServiceName}。"));
'@
$installNoWait = @'
        TomcatWindowsServiceManager.Start();
        await Task.Delay(500, cancellationToken);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，Windows 服务启动请求已发送；不再等待服务状态或读取启动进度。"));
'@
if (-not $installer.Contains($installWait)) { throw 'Unable to locate Tomcat install readiness wait.' }
$installer = $installer.Replace($installWait, $installNoWait)
Write-RepoText 'EnvironmentInstaller.cs' $installer

$deployment = Read-RepoText 'ProductDeploymentService.cs'
$restartPattern = '(?s)        var sharedServiceRestarted = false;\r?\n        if \(TomcatWindowsServiceManager\.IsRunningForRoot\(tomcatRoot\)\)\r?\n        \{.*?\r?\n        \}\r?\n\r?\n        var modeText = sharedServiceRestarted\r?\n            \? "共享 Tomcat Server 已重启并加载新应用。"\r?\n            : "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。";'
$restartReplacement = '        var modeText = "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。";'
$restartRegex = [regex]::new($restartPattern, [System.Text.RegularExpressions.RegexOptions]::Singleline)
if ($restartRegex.Matches($deployment).Count -ne 1) { throw 'Unable to locate shared Tomcat service restart block in product deployment.' }
$deployment = $restartRegex.Replace($deployment, $restartReplacement, 1)
Write-RepoText 'ProductDeploymentService.cs' $deployment

Write-Host '7/8 Update regression coverage for the simplified behavior...'
$progressTest = @'
using System;
using System.IO;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public partial class ReliabilityTests
{
    [TestMethod]
    public void TomcatServerStartup_DoesNotPollApplicationReadinessIntoEnvironmentProgress()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var window = ReadRepositoryFile("MainWindow.Environment.cs");
        var viewModel = ReadRepositoryFile(Path.Combine("ViewModels", "EnvironmentViewModels.cs"));

        Assert.IsFalse(runtime.Contains("progressPorts.Count(TomcatRuntimeProbe.IsPortListening)", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在加载应用：{ready}/{total}", StringComparison.Ordinal));
        Assert.IsFalse(runtime.Contains("正在验证 Tomcat 端口稳定监听", StringComparison.Ordinal));
        Assert.IsFalse(window.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        Assert.IsFalse(viewModel.Contains("ApplyTomcatStartupProgress", StringComparison.Ordinal));
        StringAssert.Contains(runtime, "TomcatWindowsServiceManager.Start();");
        StringAssert.Contains(runtime, "不再等待 Windows 服务状态或读取启动进度");
    }

    [TestMethod]
    public void TomcatRuntimeState_UsesProcessAndPortsInsteadOfScmStatus()
    {
        var runtime = ReadRepositoryFile("EnvironmentRuntimeService.cs");
        var methodStart = runtime.IndexOf("private static EnvironmentRuntimeState GetTomcatState", StringComparison.Ordinal);
        var methodEnd = runtime.IndexOf("private static EnvironmentRuntimeState GetSqlServerState", methodStart, StringComparison.Ordinal);
        Assert.IsTrue(methodStart >= 0 && methodEnd > methodStart);
        var method = runtime.Substring(methodStart, methodEnd - methodStart);

        StringAssert.Contains(method, "TomcatProductInstanceManager.IsSharedTomcatRunning()");
        StringAssert.Contains(method, "TomcatRuntimeProbe.ArePortsListening");
        Assert.IsFalse(method.Contains("TomcatWindowsServiceManager.IsRunning", StringComparison.Ordinal));
        Assert.IsFalse(method.Contains("serviceRunning", StringComparison.Ordinal));
    }
}
'@
Write-RepoText 'MCPanel.Tests/ReliabilityTests.TomcatServerProgress113.cs' $progressTest

Write-Host '8/8 Bump version and release notes...'
$project = Read-RepoText 'MCPanel.csproj'
$project = $project.Replace('<Version>1.3.36</Version>', '<Version>1.3.37</Version>')
$project = $project.Replace('<FileVersion>1.3.36.0</FileVersion>', '<FileVersion>1.3.37.0</FileVersion>')
$project = $project.Replace('<AssemblyVersion>1.3.36.0</AssemblyVersion>', '<AssemblyVersion>1.3.37.0</AssemblyVersion>')
if (-not $project.Contains('<Version>1.3.37</Version>')) { throw 'Version bump failed.' }
Write-RepoText 'MCPanel.csproj' $project

$notes = @'
# MCPanel 1.3.37

- 简化 Tomcat Server 启动：不再等待 Windows Service 状态切换，也不再将应用/端口探测结果绑定到启动进度条，避免 Tomcat 已正常运行却被误报“未按时启动”。
- Tomcat 环境状态改回以实际 Java 进程和配置端口为准；产品部署不再根据 Tomcat Windows 服务状态自动重启共享服务。
- Tomcat 安装、启动、停止和卸载保留原有后台服务机制，但服务控制改为发送命令后立即返回，卸载前额外清理 Tomcat 进程，避免 SCM 等待造成误报。
- AI 日志分析与面板设置移除“模型设置”按钮，模型参数继续仅从 MCPanel.exe.config 读取。
- 网站页移除“紧凑视图”和星标置顶，恢复直接显示完整网站卡片；保留“全部平台/Java/.NET-IIS”和“全部状态/运行中/停止”筛选。
'@
Write-RepoText 'RELEASE-NOTES.md' $notes

Write-Host 'Patch application completed.'
