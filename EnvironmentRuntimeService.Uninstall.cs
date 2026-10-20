using System.IO;
using System.Text;

namespace MCPanel;

public sealed partial class EnvironmentRuntimeService
{
    private static async Task RunRuntimeUninstallAsync(
        string component, Func<string, string> buildScript, CancellationToken cancellationToken)
    {
        var operation = $"uninstall-{component}-{Guid.NewGuid():N}";
        var script = Path.Combine(ComponentPaths.WorkRoot, operation + ".ps1");
        var log = Path.Combine(ComponentPaths.WorkRoot, operation + ".log");
        await ElevatedActionLock.WaitAsync(cancellationToken);
        try
        {
            Directory.CreateDirectory(ComponentPaths.WorkRoot);
            await FileCompat.WriteAllTextAsync(script, buildScript(Path.GetFileName(log)),
                new UTF8Encoding(true), cancellationToken);
            await RunElevatedPowerShellFileAsync(script, cancellationToken, log);
        }
        finally
        {
            TryDeleteFile(script);
            RollingLogWriter.EnsureCapacity(log);
            PruneGeneratedLogs(ComponentPaths.WorkRoot, $"uninstall-{component}-*.log*", log);
            ElevatedActionLock.Release();
        }
    }

    private static string PowerShellPaths(IEnumerable<string> paths) => string.Join(", ", paths
        .Distinct(StringComparer.OrdinalIgnoreCase).Select(path => $"'{EscapePowerShellPath(path)}'"));

    private static string BuildRuntimeUninstallScript(string body, string logFileName) =>
        BuildElevatedPowerShellScript(RuntimeUninstallFunctions + Environment.NewLine + body,
            Path.Combine(ComponentPaths.WorkRoot, logFileName));

    private static IEnumerable<string> ProtectedUninstallRoots => new[]
    {
        ComponentPaths.ApplicationRoot, ComponentPaths.StoreDataRoot,
        ComponentPaths.RuntimeRoot, ComponentPaths.LegacyRuntimeRoot
    };

    internal static string BuildNginxUninstallScript(string nginxRoot, string logFileName = "uninstall-nginx.log")
    {
        var imagePath = NginxWindowsServiceManager.BuildServiceImagePath(
            Path.Combine(ComponentPaths.ApplicationRoot, "MCPanel.exe"), nginxRoot);
        return BuildRuntimeUninstallScript($$"""
            $targetRoot = Assert-ComponentRoot '{{EscapePowerShellPath(nginxRoot)}}' @({{PowerShellPaths(ComponentPaths.NginxSearchRoots)}}) @({{PowerShellPaths(ProtectedUninstallRoots)}})
            $nginxExe = Join-Path $targetRoot 'nginx.exe'
            if (-not (Test-Path -LiteralPath $nginxExe)) { throw ('未找到组件 nginx.exe：' + $targetRoot) }
            $service = Get-ComponentService 'nginx'
            if ($service) {
                $serviceExe = Get-ServiceExecutable $service.ImagePath
                $panelWrapper = $service.ImagePath.Trim().Equals('{{EscapePowerShellPath(imagePath)}}', [StringComparison]::OrdinalIgnoreCase)
                $legacyWrapper = (Test-PathUnderRoot $serviceExe $targetRoot) -and ([IO.Path]::GetFileName($serviceExe) -eq 'nginx-server.exe')
                if (-not $panelWrapper -and -not $legacyWrapper) {
                    throw '检测到非当前 MCPanel 组件的 nginx 服务，已停止卸载以保护现有服务。'
                }
                Write-Output 'Stopping Nginx Windows service and watchdog.'
                Stop-ComponentService 'nginx'
            }

            $processNames = @('nginx.exe')
            if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) {
                # nginx documents -s quit (graceful), then -s stop (fast).
                # The explicit prefix works regardless of the elevated process CWD.
                foreach ($signal in @('quit', 'stop')) {
                    Write-Output ('Sending nginx -s ' + $signal)
                    try { & $nginxExe -p ($targetRoot.Replace('\', '/') + '/') -s $signal 2>&1 | Write-Output }
                    catch { Write-Output $_.Exception.Message }
                    $global:LASTEXITCODE = 0
                    if (Wait-ComponentProcesses $targetRoot $processNames 10) { break }
                }
                if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) {
                    Get-ComponentProcesses $targetRoot $processNames | ForEach-Object {
                        Stop-Process -Id $_.ProcessId -Force -ErrorAction Stop
                    }
                    if (-not (Wait-ComponentProcesses $targetRoot $processNames 5)) {
                        throw 'Nginx 进程仍在运行，已停止删除组件文件。'
                    }
                }
            }
            if ($service) { Remove-ComponentService 'nginx' }
            if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) { throw 'Nginx 进程重新出现，已停止删除。' }
            Remove-ComponentDirectory $targetRoot
            if (Test-Path -LiteralPath $targetRoot) { throw ('Nginx 目录仍然存在：' + $targetRoot) }
            """, logFileName);
    }

    internal static string BuildMySqlUninstallScript(string? mysqlRoot, string logFileName = "uninstall-mysql.log") =>
        BuildRuntimeUninstallScript($$"""
            $mysqlInstallationRoots = @({{PowerShellPaths(ComponentPaths.MySqlInstallationRoots)}})
            $protectedRoots = @({{PowerShellPaths(ProtectedUninstallRoots)}})
            $targetRoot = '{{EscapePowerShellPath(mysqlRoot ?? string.Empty)}}'
            $service = Get-ComponentService 'MySQL80'
            if ($service) {
                $serviceExe = Get-ServiceExecutable $service.ImagePath
                $serviceRoot = [IO.Path]::GetDirectoryName([IO.Path]::GetDirectoryName($serviceExe))
                $serverName = [IO.Path]::GetFileName($serviceExe)
                $legacyParents = @({{PowerShellPaths(new[] { ComponentPaths.RuntimeRoot, ComponentPaths.LegacyRuntimeRoot })}})
                $legacyMySql = [IO.Path]::GetFileName($serviceRoot).StartsWith('mysql', [StringComparison]::OrdinalIgnoreCase) -and
                    @($legacyParents | Where-Object { [IO.Path]::GetFullPath($_).TrimEnd('\').Equals([IO.Path]::GetDirectoryName($serviceRoot), [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0
                if ($serverName -notin @('mysqld.exe', 'mysqld-itmc.exe') -or
                    [IO.Path]::GetFileName([IO.Path]::GetDirectoryName($serviceExe)) -ne 'bin' -or
                    (-not $legacyMySql -and @($mysqlInstallationRoots | Where-Object { Test-PathUnderRoot $serviceRoot $_ }).Count -eq 0)) {
                    throw ('检测到非 MCPanel 管理的 MySQL80 服务，已停止卸载以保护现有实例：' + $serviceExe)
                }
                if ($legacyMySql) { $mysqlInstallationRoots += $serviceRoot }
                # The registered service identifies the actual installation,
                # rather than whichever mysql.exe a broad search finds first.
                $targetRoot = $serviceRoot
            }
            if ([string]::IsNullOrWhiteSpace($targetRoot)) {
                foreach ($root in $mysqlInstallationRoots) {
                    if (Test-Path -LiteralPath (Join-Path $root 'bin\mysql.exe')) { $targetRoot = $root; break }
                    if (-not (Test-Path -LiteralPath $root)) { continue }
                    $candidate = Get-ChildItem -LiteralPath $root -Directory -Force -ErrorAction Stop |
                        Where-Object { ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 -and (Test-Path -LiteralPath (Join-Path $_.FullName 'bin\mysql.exe')) } |
                        Select-Object -First 1
                    if ($candidate) { $targetRoot = $candidate.FullName; break }
                }
            }
            if ([string]::IsNullOrWhiteSpace($targetRoot)) {
                Write-Output 'MySQL 组件和服务均不存在，无需卸载。'
            }
            else {
                # Validate the directory before stopping or removing any service.
                $targetRoot = Assert-ComponentRoot $targetRoot $mysqlInstallationRoots $protectedRoots
                if (-not $service -and (Test-Path -LiteralPath $targetRoot) -and
                    -not (Test-Path -LiteralPath (Join-Path $targetRoot 'bin\mysql.exe'))) {
                    throw ('未在待删除目录中找到 bin\mysql.exe，已停止删除：' + $targetRoot)
                }
                if ($service) {
                    Write-Output 'Stopping MySQL80 using the Windows Service Control Manager.'
                    Stop-ComponentService 'MySQL80'
                }
                $processNames = @('mysqld.exe', 'mysqld-itmc.exe')
                if (-not (Wait-ComponentProcesses $targetRoot $processNames 10)) {
                    # Do not force-kill a database and risk an unclean shutdown.
                    throw 'MySQL 进程仍在运行，请先正常关闭数据库后再次卸载；服务和数据目录尚未删除。'
                }
                if ($service) { Remove-ComponentService 'MySQL80' }
                if (@(Get-ComponentProcesses $targetRoot $processNames).Count -gt 0) { throw 'MySQL 进程重新出现，已停止删除。' }
                Write-Output ('Removing MySQL installation directory: ' + $targetRoot)
                Remove-ComponentDirectory $targetRoot
                if (Test-Path -LiteralPath $targetRoot) { throw ('MySQL 目录仍然存在：' + $targetRoot) }
            }
            """, logFileName);

    // Windows Server 2012 ships PowerShell 3.0. Avoid newer cmdlet parameters,
    // recursive Remove-Item and recursive ACL changes, particularly at junctions.
    private static readonly Lazy<string> RuntimeCleanupScript = new(() =>
    {
        using var stream = typeof(EnvironmentRuntimeService).Assembly.GetManifestResourceStream("MCPanel.RuntimeCleanup.ps1")
            ?? throw new InvalidOperationException("Runtime cleanup support is missing.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    });

    internal static string RuntimeUninstallFunctions => RuntimeCleanupScript.Value;
}
