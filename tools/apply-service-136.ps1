$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) {
        throw "Expected block was not found in $Path"
    }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

# App service dispatch: system services must be handled before normal WPF UI startup.
$path = 'App.xaml.cs'
$old = @'
            if (NginxWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = NginxWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (EnvironmentInstallWorker.IsWorkerRequest(e.Args))
'@
$new = @'
            if (NginxWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = NginxWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (TomcatWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = TomcatWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (FrpWindowsServiceHost.IsServiceRequest(e.Args))
            {
                ShutdownMode = ShutdownMode.OnExplicitShutdown;
                var exitCode = FrpWindowsServiceHost.Run(e.Args);
                Shutdown(exitCode);
                return;
            }

            if (EnvironmentInstallWorker.IsWorkerRequest(e.Args))
'@
Replace-Exact $path $old $new

# Shared Tomcat environment installation becomes an Automatic Windows service.
$path = 'EnvironmentInstaller.cs'
$old = @'
    private async Task InstallTomcatAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var locator = new ComponentLocator();
        var root = SelectTomcatInstallRoot(locator);
        Directory.CreateDirectory(root);
        var tomcatRoot = locator.FindTomcatRoot(root);
        if (tomcatRoot is null || !File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat")))
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.TomcatPackageUrl, GetPackageDirectory(), "apache-tomcat-8.5.57.zip", progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Tomcat 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            tomcatRoot = new ComponentLocator().FindTomcatRoot(root) ?? Path.Combine(root, "apache-tomcat-8.5.57");
        }
        else
        {
            progress(InstallingProgress(70, "已找到现有 Tomcat，正在检查并修复配置...", 0));
        }
        ConfigureTomcat(tomcatRoot);

        var startup = Path.Combine(tomcatRoot, "bin", "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("Tomcat 解压完成，但未找到启动脚本。", startup);
        }

        if (File.Exists(startup))
        {
            progress(InstallingProgress(88, "正在启动 Tomcat...", 75));
            await RunProcessAsync(startup, string.Empty, Path.GetDirectoryName(startup)!, false, cancellationToken);
            var ports = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot);
            await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, ports, cancellationToken);
        }

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}。"));
    }
'@
$new = @'
    private async Task InstallTomcatAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var locator = new ComponentLocator();
        var root = SelectTomcatInstallRoot(locator);
        Directory.CreateDirectory(root);
        var tomcatRoot = locator.FindTomcatRoot(root);
        if (tomcatRoot is null || !File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat")))
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.TomcatPackageUrl, GetPackageDirectory(), "apache-tomcat-8.5.57.zip", progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Tomcat 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            tomcatRoot = new ComponentLocator().FindTomcatRoot(root) ?? Path.Combine(root, "apache-tomcat-8.5.57");
        }
        else
        {
            progress(InstallingProgress(70, "已找到现有 Tomcat，正在检查并修复配置...", 0));
        }
        ConfigureTomcat(tomcatRoot);

        var startup = Path.Combine(tomcatRoot, "bin", "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("Tomcat 解压完成，但未找到启动脚本。", startup);
        }

        var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(serviceExecutable) || !File.Exists(serviceExecutable))
        {
            throw new InvalidOperationException("无法定位 MCPanel 主程序，不能注册 Tomcat Windows 服务。");
        }

        progress(InstallingProgress(84, "正在注册 Tomcat Server Windows 服务...", 58));
        TomcatProductStartupManager.RemoveRegistration();
        await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);
        TomcatWindowsServiceManager.EnsureRegistered(serviceExecutable!, tomcatRoot);

        progress(InstallingProgress(92, "正在启动 Tomcat Server Windows 服务...", 78));
        TomcatWindowsServiceManager.Start();
        var ports = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
        await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, ports, cancellationToken);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并注册为自动启动的 Windows 服务 {TomcatWindowsServiceManager.ServiceName}。"));
    }
'@
Replace-Exact $path $old $new

# Environment card now represents only the shared Tomcat Server; single-app instances remain independent.
$path = 'EnvironmentRuntimeService.cs'
$old = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);
                var tomcatPorts = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
                if (tomcatPorts.Length == 0)
                {
                    throw new InvalidDataException("Tomcat server.xml 中没有可用的 HTTP 端口。请先修复产品绑定。");
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    if (TomcatRuntimeProbe.ArePortsListening(tomcatPorts))
                    {
                        return $"Tomcat 已在全部应用模式运行。日志目录：{Path.Combine(tomcatRoot, "logs")}";
                    }

                    // A Java process can remain after Tomcat failed during startup. Clear
                    // that stale process before trying to start a healthy instance again.
                    await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
                }

                EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(tomcatRoot);
                StartDetached(Path.Combine(tomcatRoot, "bin", "startup.bat"), Path.Combine(tomcatRoot, "bin"));
                try
                {
                    await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, tomcatPorts, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(
                        CancellationToken.None,
                        throwOnFailure: false);
                    throw;
                }
                catch (Exception error)
                {
                    await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(
                        CancellationToken.None,
                        throwOnFailure: false);
                    throw new InvalidOperationException($"Tomcat 启动失败：{error.Message}", error);
                }
                return $"Tomcat 全部应用已启动，{tomcatPorts.Length} 个端口已确认监听。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@
$new = @'
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
                await TomcatRuntimeProbe.WaitForStartupAsync(tomcatRoot, tomcatPorts, cancellationToken);
                return $"Tomcat Server Windows 服务已启动，{tomcatPorts.Length} 个配置端口已确认监听。日志目录：{Path.Combine(tomcatRoot, "logs")}";
'@
Replace-Exact $path $old $new

$old = @'
            case EnvironmentKind.Tomcat:
                _ = RequireTomcatRoot();
                await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken);
                return "Tomcat 及其全部单应用进程已停止。";
'@
$new = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    TomcatWindowsServiceManager.Stop();
                    return "Tomcat Server Windows 服务已停止；单应用 Tomcat 实例不受影响。";
                }

                if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                {
                    var shutdown = Path.Combine(tomcatRoot, "bin", "shutdown.bat");
                    if (File.Exists(shutdown))
                    {
                        await RunFileAsync(shutdown, string.Empty, Path.Combine(tomcatRoot, "bin"), false, cancellationToken);
                    }
                    for (var attempt = 0; attempt < 80 && TomcatProductInstanceManager.IsSharedTomcatRunning(); attempt++)
                    {
                        await Task.Delay(250, cancellationToken);
                    }
                    if (TomcatProductInstanceManager.IsSharedTomcatRunning())
                    {
                        throw new InvalidOperationException("Tomcat Server 共享进程未能停止；为避免影响单应用实例，已取消强制结束全部 Java 进程。");
                    }
                }
                return "Tomcat Server 已停止；单应用 Tomcat 实例不受影响。";
'@
Replace-Exact $path $old $new

$old = @'
            case EnvironmentKind.Tomcat:
                await TryStopAsync(kind, cancellationToken);
                DeleteDirectory(RequireTomcatRoot());
                TomcatProductStartupManager.RemoveRegistration();
                return "Tomcat 已卸载。";
'@
$new = @'
            case EnvironmentKind.Tomcat:
                var tomcatRoot = RequireTomcatRoot();
                await TryStopAsync(kind, cancellationToken);
                await TomcatProductInstanceManager.StopAllProductInstancesAsync(cancellationToken);
                if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
                {
                    TomcatWindowsServiceManager.Delete();
                }
                DeleteDirectory(tomcatRoot);
                TomcatProductStartupManager.RemoveRegistration();
                return "Tomcat Windows 服务、共享运行环境及实例运行进程已卸载；产品文件保持由产品管理单独处理。";
'@
Replace-Exact $path $old $new

$old = @'
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
        var status = anyRunning ? RuntimeStatusKind.Running : RuntimeStatusKind.Stopped;
        var detail = sharedRunning
            ? "全部应用模式正在运行。"
            : managedRunning
                ? "一个或多个应用正在单独运行。"
                : sharedProcessRunning
                    ? "检测到 Tomcat Java 进程，但配置端口未全部监听，已判定为启动异常。"
                : "当前没有健康的 Tomcat 监听。";
        return Installed(status, $"Tomcat 已安装到 {tomcatRoot}。{detail}");
    }
'@
$new = @'
    private static EnvironmentRuntimeState GetTomcatState(ComponentLocator locator)
    {
        var tomcatRoot = locator.FindTomcatRoot();
        if (tomcatRoot is null)
        {
            return NotInstalled();
        }

        var ports = TomcatRuntimeProbe.ReadHttpPorts(tomcatRoot).ToArray();
        var serviceRegistered = TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot);
        var serviceRunning = serviceRegistered && TomcatWindowsServiceManager.IsRunningForRoot(tomcatRoot);
        var sharedProcessRunning = TomcatProductInstanceManager.IsSharedTomcatRunning();
        var sharedHealthy = sharedProcessRunning && ports.Length > 0 && TomcatRuntimeProbe.ArePortsListening(ports);
        var singleApplicationRunning = !sharedHealthy && TomcatProductInstanceManager.IsAnyManagedTomcatHealthy();

        var status = sharedHealthy
            ? RuntimeStatusKind.Running
            : serviceRunning
                ? RuntimeStatusKind.Starting
                : RuntimeStatusKind.Stopped;
        var serviceText = serviceRegistered
            ? $"Windows 服务 {TomcatWindowsServiceManager.ServiceName} 已注册为自动启动。"
            : "尚未注册 Windows 服务；下次点击启动或重新安装时会自动迁移。";
        var healthText = sharedHealthy
            ? "共享 Tomcat Server 正在运行。"
            : serviceRunning
                ? "Windows 服务正在运行，但配置端口尚未全部监听。"
                : sharedProcessRunning
                    ? "检测到旧版共享 Tomcat Java 进程，但服务未运行。"
                    : "共享 Tomcat Server 当前未运行。";
        var singleText = singleApplicationRunning
            ? "另有一个或多个产品正在单应用模式运行；这些实例不会随 Windows 自动启动。"
            : "单应用实例保持按需启动，不参与系统自动启动。";
        return Installed(status, $"Tomcat 已安装到 {tomcatRoot}。{serviceText}{healthText}{singleText}");
    }
'@
Replace-Exact $path $old $new

# Starting an individual product explicitly switches away from the shared automatic service.
$path = 'TomcatProductInstanceManager.cs'
$old = @'
            // The shared process owns the same product ports. Stop only that process before
            // switching to single-application mode; other single-app processes remain running.
            await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));
'@
$new = @'
            // The shared Windows service owns the same product ports. An explicit
            // single-application start switches the server into manual product mode;
            // the shared service will return automatically on the next Windows boot.
            if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatHome))
            {
                TomcatWindowsServiceManager.Stop();
            }
            await StopCatalinaBaseAsync(tomcatHome, cancellationToken, GetTomcatHttpPorts(tomcatHome));
'@
Replace-Exact $path $old $new

# Product installation/configuration no longer auto-starts a single-app Tomcat instance.
$path = 'ProductDeploymentService.cs'
$old = @'
        TomcatProductStartupManager.RefreshRegistration();

        var instanceManager = new TomcatProductInstanceManager();
        await instanceManager.StartAsync(contextName, catalinaMode: false, cancellationToken);

        return new ProductDeploymentResult(
            $"产品已安装到 {appRoot}，并分配 Tomcat 独立端口 {deployment.Port}。访问地址：{deployment.Url}",
            appRoot);
'@
$new = @'
        TomcatProductStartupManager.RefreshRegistration();

        var sharedServiceRestarted = false;
        if (TomcatWindowsServiceManager.IsRunningForRoot(tomcatRoot))
        {
            TomcatWindowsServiceManager.Stop();
            TomcatWindowsServiceManager.Start();
            sharedServiceRestarted = true;
        }

        var modeText = sharedServiceRestarted
            ? "共享 Tomcat Server 已重启并加载新应用。"
            : "未自动启动单应用实例；如需单独运行，请在产品管理中手动启动。";
        return new ProductDeploymentResult(
            $"产品已安装到 {appRoot}，并分配 Tomcat 端口 {deployment.Port}。访问地址：{deployment.Url}。{modeText}",
            appRoot);
'@
Replace-Exact $path $old $new

# FRP becomes an Automatic Windows service while the local management web UI stays in the panel process.
$path = 'FrpManager.cs'
$old = @'
    public bool IsRunning
    {
        get
        {
            if (_frpcProcess is { HasExited: false })
            {
                return true;
            }

            using var process = FindManagedFrpcProcess();
            return process is not null;
        }
    }
'@
$new = @'
    public bool IsRunning
    {
        get
        {
            if (FrpWindowsServiceManager.IsRunningForRoot(_applicationRoot))
            {
                return true;
            }
            if (_frpcProcess is { HasExited: false })
            {
                return true;
            }

            using var process = FindManagedFrpcProcess();
            return process is not null;
        }
    }
'@
Replace-Exact $path $old $new

$old = '                    ? "FRP 客户端运行中，可打开网页管理页面查看或修改配置。"`r`n                    : "FRP 客户端已安装但未运行，可点击“启动”或打开“管理”编辑配置。",'
$new = '                    ? "FRP Windows 服务运行中，可打开网页管理页面查看或修改配置。"`r`n                    : "FRP 客户端已安装但服务未运行，可点击“启动”恢复 Windows 服务。",'
Replace-Exact $path $old $new

$old = @'
            progress?.Invoke(new InstallProgress(5, "正在准备 FRP 安装目录..."));
            await EnsureFrpFilesAsync(progress, cancellationToken);
            progress?.Invoke(new InstallProgress(100, "FRP 客户端已安装；本次安装不会自动启动。"));
'@
$new = @'
            progress?.Invoke(new InstallProgress(5, "正在准备 FRP 安装目录..."));
            await EnsureFrpFilesAsync(progress, cancellationToken);
            NormalizeConfigFileEncoding(ConfigPath);
            await VerifyConfigAsync(ConfigPath);
            var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrWhiteSpace(serviceExecutable) || !File.Exists(serviceExecutable))
            {
                throw new InvalidOperationException("无法定位 MCPanel 主程序，不能注册 FRP Windows 服务。");
            }
            progress?.Invoke(new InstallProgress(94, "正在注册并启动 FRP Windows 服务..."));
            FrpWindowsServiceManager.EnsureRegistered(serviceExecutable!, _applicationRoot);
            FrpWindowsServiceManager.Start();
            progress?.Invoke(new InstallProgress(100, "FRP 客户端已安装并注册为自动启动的 Windows 服务。"));
'@
Replace-Exact $path $old $new

$old = @'
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await EnsureManagementServerCoreAsync();
            await StartCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }
'@
$new = @'
    public async Task StartAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await EnsureManagementServerCoreAsync();
            RequireInstalled();
            NormalizeConfigFileEncoding(ConfigPath);
            await VerifyConfigAsync(ConfigPath);

            if (!FrpWindowsServiceManager.IsRegisteredForRoot(_applicationRoot))
            {
                await StopCoreAsync();
                var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;
                if (string.IsNullOrWhiteSpace(serviceExecutable) || !File.Exists(serviceExecutable))
                {
                    throw new InvalidOperationException("无法定位 MCPanel 主程序，不能注册 FRP Windows 服务。");
                }
                FrpWindowsServiceManager.EnsureRegistered(serviceExecutable!, _applicationRoot);
            }

            FrpWindowsServiceManager.Start();
        }
        finally
        {
            _operationLock.Release();
        }
    }
'@
Replace-Exact $path $old $new

$old = @'
    public async Task StopAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            await StopCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }
'@
$new = @'
    public async Task StopAsync()
    {
        ThrowIfDisposed();
        await _operationLock.WaitAsync();
        try
        {
            if (FrpWindowsServiceManager.IsRegisteredForRoot(_applicationRoot))
            {
                FrpWindowsServiceManager.Stop();
            }
            await StopCoreAsync();
        }
        finally
        {
            _operationLock.Release();
        }
    }
'@
Replace-Exact $path $old $new

$old = @'
        try
        {
            StopManagementServer();
            await StopCoreAsync();
            await _frpFilesLock.WaitAsync(cancellationToken);
'@
$new = @'
        try
        {
            StopManagementServer();
            if (FrpWindowsServiceManager.IsRegisteredForRoot(_applicationRoot))
            {
                FrpWindowsServiceManager.Stop();
                FrpWindowsServiceManager.Delete();
            }
            await StopCoreAsync();
            await _frpFilesLock.WaitAsync(cancellationToken);
'@
Replace-Exact $path $old $new

$old = @'
        foreach (var process in Process.GetProcessesByName("frpc"))
        {
            try
            {
                var executable = process.MainModule?.FileName;
                if (!string.IsNullOrWhiteSpace(executable) &&
                    Path.GetFullPath(executable).Equals(Path.GetFullPath(FrpcPath), StringComparison.OrdinalIgnoreCase))
                {
                    ProcessLifecycle.TryKill(process);
                    process.WaitForExit(5000);
                }
            }
            catch
            {
                // A protected process may deny path inspection or termination.
            }
            finally
            {
                process.Dispose();
            }
        }
'@
$new = @'
        // The Windows service owns frpc.exe after 1.3.6. Disposing a page or
        // closing MCPanel must not stop the server-level FRP service. Only a
        // legacy child process owned by this manager instance is cleaned above.
'@
Replace-Exact $path $old $new

# Version bump.
$path = 'MCPanel.csproj'
$text = [IO.File]::ReadAllText($path)
$text = $text.Replace('<Version>1.3.5</Version>', '<Version>1.3.6</Version>')
$text = $text.Replace('<FileVersion>1.3.5.0</FileVersion>', '<FileVersion>1.3.6.0</FileVersion>')
$text = $text.Replace('<AssemblyVersion>1.3.5.0</AssemblyVersion>', '<AssemblyVersion>1.3.6.0</AssemblyVersion>')
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

# Release notes.
$path = '.github/workflows/release.yml'
$text = [IO.File]::ReadAllText($path)
$text = $text.Replace(
    "修复 Nginx Windows 服务只启动不监管 nginx.exe 的问题，增加进程健康监控、自动恢复和 Windows 服务故障恢复策略。",
    "将共享 Tomcat Server 与 FRP 客户端改为自动启动的 Windows 服务，并取消单应用 Tomcat 的登录后自动恢复。")
$text = $text.Replace(
    "本版本修复 Nginx 长时间运行后子进程退出但 Windows 服务仍显示正在运行的问题，不改变原有产品下载和部署链路。",
    "本版本完善服务器重启后的无人值守恢复：共享 Tomcat Server 与 FRP 改为 Windows 服务，单应用 Tomcat 保持用户按需启动；不改变原有产品下载链路。")
$text = $text.Replace(
@'
          - Nginx Windows 服务增加 watchdog，持续检查 nginx.exe 与实际配置端口的健康状态。
          - nginx.exe 意外退出时自动按 1 秒、3 秒、10 秒、30 秒、60 秒退避恢复。
          - 短时间内反复异常达到上限时退出服务进程，交由 Windows Service Control Manager 继续恢复，避免无限快速重启。
          - Windows nginx 服务配置 SCM recovery：wrapper 自身异常退出时自动重启。
          - 停止服务时先停止 watchdog，再优雅退出 nginx.exe，避免停止过程中被 watchdog 重新拉起。
          - 新增 logs\\mcpanel-service.log，记录服务启动、异常检测、自动恢复和停止过程。
          - 发布前执行完整可靠性测试。
'@,
@'
          - 环境页“Tomcat Server”注册为 Automatic Windows 服务，服务器重启后无需用户登录即可启动共享 Tomcat。
          - Tomcat 服务带进程/端口健康监控和 SCM recovery；用户手动启动单应用实例时会停止共享服务，单应用实例本身不自动启动。
          - 移除旧版 MCPanelTomcatProducts 当前用户 Run 恢复项，升级后会自动清理旧启动项。
          - FRP 客户端注册为 Automatic Windows 服务，关闭 MCPanel UI 不再终止 frpc.exe，服务器重启后无需登录即可恢复。
          - FRP 服务带子进程 watchdog 与 SCM recovery，配置仍由原有 FRP 管理页面维护。
          - Nginx、MySQL、SQL Server、IIS 和原有产品下载链路保持不变。
          - 发布前执行完整可靠性测试。
'@)
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

Write-Host 'MCPanel 1.3.6 Tomcat/FRP service migration patch applied.'
