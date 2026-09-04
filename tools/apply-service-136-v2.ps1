$ErrorActionPreference = 'Stop'

function Replace-Exact([string]$Path, [string]$Old, [string]$New) {
    $text = [IO.File]::ReadAllText($Path)
    if (-not $text.Contains($Old)) {
        throw "Expected continuation block was not found in $Path"
    }
    $text = $text.Replace($Old, $New)
    [IO.File]::WriteAllText($Path, $text, [Text.UTF8Encoding]::new($false))
}

# The original patch script deliberately stops at the FRP status-text cosmetic
# replacement on LF-only runners. Everything before that point is valid and is
# kept in the working tree; continue with the functional service changes here.
try {
    .\tools\apply-service-136.ps1
}
catch {
    if ($_.Exception.Message -notlike '*Expected block was not found in FrpManager.cs*') {
        throw
    }
    Write-Host 'Base patch reached the known FRP status-text boundary; continuing with v2.'
}

$path = 'FrpManager.cs'

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

$path = 'MCPanel.csproj'
$text = [IO.File]::ReadAllText($path)
$text = $text.Replace('<Version>1.3.5</Version>', '<Version>1.3.6</Version>')
$text = $text.Replace('<FileVersion>1.3.5.0</FileVersion>', '<FileVersion>1.3.6.0</FileVersion>')
$text = $text.Replace('<AssemblyVersion>1.3.5.0</AssemblyVersion>', '<AssemblyVersion>1.3.6.0</AssemblyVersion>')
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

$path = '.github/workflows/release.yml'
$text = [IO.File]::ReadAllText($path)
$text = $text.Replace(
    '修复 Nginx Windows 服务只启动不监管 nginx.exe 的问题，增加进程健康监控、自动恢复和 Windows 服务故障恢复策略。',
    '将共享 Tomcat Server 与 FRP 客户端改为自动启动的 Windows 服务，并取消单应用 Tomcat 的登录后自动恢复。')
$text = $text.Replace(
    '本版本修复 Nginx 长时间运行后子进程退出但 Windows 服务仍显示正在运行的问题，不改变原有产品下载和部署链路。',
    '本版本完善服务器重启后的无人值守恢复：共享 Tomcat Server 与 FRP 改为 Windows 服务，单应用 Tomcat 保持用户按需启动；不改变原有产品下载链路。')
$oldNotes = @'
          - Nginx Windows 服务增加 watchdog，持续检查 nginx.exe 与实际配置端口的健康状态。
          - nginx.exe 意外退出时自动按 1 秒、3 秒、10 秒、30 秒、60 秒退避恢复。
          - 短时间内反复异常达到上限时退出服务进程，交由 Windows Service Control Manager 继续恢复，避免无限快速重启。
          - Windows nginx 服务配置 SCM recovery：wrapper 自身异常退出时自动重启。
          - 停止服务时先停止 watchdog，再优雅退出 nginx.exe，避免停止过程中被 watchdog 重新拉起。
          - 新增 logs\\mcpanel-service.log，记录服务启动、异常检测、自动恢复和停止过程。
          - 发布前执行完整可靠性测试。
'@
$newNotes = @'
          - 环境页“Tomcat Server”注册为 Automatic Windows 服务，服务器重启后无需用户登录即可启动共享 Tomcat。
          - Tomcat 服务带进程/端口健康监控和 SCM recovery；用户手动启动单应用实例时会停止共享服务，单应用实例本身不自动启动。
          - 移除旧版 MCPanelTomcatProducts 当前用户 Run 恢复项，升级后会自动清理旧启动项。
          - FRP 客户端注册为 Automatic Windows 服务，关闭 MCPanel UI 不再终止 frpc.exe，服务器重启后无需登录即可恢复。
          - FRP 服务带子进程 watchdog 与 SCM recovery，配置仍由原有 FRP 管理页面维护。
          - Nginx、MySQL、SQL Server、IIS 和原有产品下载链路保持不变。
          - 发布前执行完整可靠性测试。
'@
if ($text.Contains($oldNotes)) { $text = $text.Replace($oldNotes, $newNotes) }
[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))

Write-Host 'MCPanel 1.3.6 service migration v2 patch applied.'
