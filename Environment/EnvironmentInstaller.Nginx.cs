using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;

namespace MCPanel;

public sealed partial class EnvironmentInstaller
{
    private async Task InstallNginxAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var root = ComponentPaths.SelectNginxInstallRoot();
        Directory.CreateDirectory(root);

        var locator = new ComponentLocator();
        var nginxExe = locator.FindNginxExecutable(root);
        if (nginxExe is null)
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.NginxPackageUrl, GetPackageDirectory(), EnvironmentComponentCatalog.NginxPackageFileName, progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Nginx 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            var nginxRoot = FindDirectory(root, NginxRuntimeManager.VersionDirectoryName) ?? Path.Combine(root, NginxRuntimeManager.VersionDirectoryName);
            nginxExe = Path.Combine(nginxRoot, "nginx.exe");
        }
        else
        {
            progress(InstallingProgress(70, "已找到现有 Nginx，正在检查并修复配置...", 0));
        }
        if (!File.Exists(nginxExe))
        {
            nginxExe = Directory.GetFiles(root, "nginx.exe", SearchOption.AllDirectories).FirstOrDefault() ?? nginxExe;
        }

        if (!File.Exists(nginxExe))
        {
            throw new FileNotFoundException("Nginx 解压完成，但未找到 nginx.exe。", nginxExe);
        }

        var nginxWorkDirectory = Path.GetDirectoryName(nginxExe)!;
        progress(InstallingProgress(76, "正在停止旧 Nginx 进程，准备注册 Windows 服务...", 20));
        if (NginxWindowsServiceManager.IsInstalled())
        {
            if (!NginxWindowsServiceManager.IsRegisteredForRoot(nginxWorkDirectory))
            {
                throw new InvalidOperationException(
                    $"Windows 服务 {NginxWindowsServiceManager.ServiceName} 已存在，但未指向当前 Nginx 目录，已停止安装以保护现有服务。");
            }

            NginxWindowsServiceManager.Stop();
        }

        NginxRuntimeManager.KillProcessesUnderRoot(nginxWorkDirectory);
        progress(InstallingProgress(82, "正在生成 Nginx 监听配置...", 32));

        progress(InstallingProgress(88, "正在校验 Nginx 配置和端口...", 48));
        var listenPort = await ConfigureAndTestNginxAsync(nginxExe, nginxWorkDirectory, cancellationToken);

        var serviceExecutable = Process.GetCurrentProcess().MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(serviceExecutable) || !File.Exists(serviceExecutable))
        {
            throw new InvalidOperationException("无法定位 MCPanel 主程序，不能注册 Nginx Windows 服务。");
        }

        progress(InstallingProgress(92, "正在注册 Nginx Windows 服务...", 64));
        NginxWindowsServiceManager.EnsureRegistered(serviceExecutable!, nginxWorkDirectory);
        progress(InstallingProgress(95, $"正在启动 Nginx Windows 服务，监听端口 {listenPort}...", 78));
        NginxWindowsServiceManager.Start();
        await Task.Delay(1000, cancellationToken);
        if (!IsNginxOperational(nginxWorkDirectory))
        {
            var log = NginxRuntimeManager.ReadRecentErrorLog(nginxWorkDirectory);
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(log)
                ? "Nginx 启动失败，未检测到运行中的 nginx.exe。"
                : $"Nginx 启动失败。最近日志：{log}");
        }

        progress(InstallingProgress(97, "正在同步已安装产品的 Nginx 反向代理规则...", 90));
        var proxySyncSucceeded = await NginxProductProxyService.TrySyncAsync(cancellationToken);
        progress(InstallingProgress(100, proxySyncSucceeded
            ? $"Nginx 已注册为 Windows 服务并安装到 {Path.GetDirectoryName(nginxExe)}，监听端口 {listenPort}。"
            : $"Nginx 已注册为 Windows 服务并安装到 {Path.GetDirectoryName(nginxExe)}，监听端口 {listenPort}；产品反向代理规则同步失败，请查看日志后重试。"));
    }

    private static async Task<int> ConfigureAndTestNginxAsync(string nginxExe, string nginxRoot, CancellationToken cancellationToken)
    {
        var savedOptions = NginxRuntimeManager.LoadOptions();
        if (savedOptions is { Rules.Count: > 1 })
        {
            var normalized = NginxRuntimeManager.NormalizeOptions(savedOptions);
            var configuredPorts = NginxRuntimeManager.GetEffectiveListenPorts(normalized).ToArray();
            if (configuredPorts.Length == 0)
            {
                configuredPorts = [normalized.ListenPort];
            }
            foreach (var port in configuredPorts)
            {
                if (!NginxRuntimeManager.IsPortAvailable(port))
                {
                    throw new InvalidOperationException(
                        $"Nginx 多规则配置的端口 {port} 已被占用，请先在“管理”中调整端口后再安装。");
                }
            }

            try
            {
                NginxRuntimeManager.CleanConfigFiles(nginxRoot);
                NginxRuntimeManager.WriteManagedConfig(nginxRoot, normalized);
                await RunProcessWithOutputAsync(nginxExe, "-t", nginxRoot, cancellationToken);
                NginxRuntimeManager.SaveOptions(normalized);
                return normalized.ListenPort;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 多规则配置校验失败：{ex.Message}", ex);
            }
        }

        Exception? lastError = null;
        foreach (var port in NginxRuntimeManager.BuildPortCandidates())
        {
            if (!NginxRuntimeManager.IsPortAvailable(port))
            {
                continue;
            }

            NginxRuntimeManager.CleanConfigFiles(nginxRoot);
            NginxRuntimeManager.WriteListenPort(nginxRoot, port);

            try
            {
                await RunProcessWithOutputAsync(nginxExe, "-t", nginxRoot, cancellationToken);
                NginxRuntimeManager.SaveOptions(port);
                return port;
            }
            catch (Exception ex) when (NginxRuntimeManager.IsPortBindFailure(ex.Message))
            {
                lastError = ex;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Nginx 配置校验失败：{ex.Message}", ex);
            }
        }

        throw new InvalidOperationException("Nginx 无法找到可用监听端口。请检查 72、80、8088、8080、8090、8099、18080 是否被占用。", lastError);
    }
    private static bool IsNginxOperational(string nginxRoot) =>
        NginxRuntimeManager.IsRunningUnderRoot(nginxRoot) ||
        NginxWindowsServiceManager.IsRunningForRoot(nginxRoot);
}
