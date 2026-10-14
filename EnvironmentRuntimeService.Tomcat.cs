using System.Diagnostics;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;
using ServiceControllerStatus = System.ServiceProcess.ServiceControllerStatus;

namespace MCPanel;

public sealed partial class EnvironmentRuntimeService
{
    internal static void LaunchTomcatStartConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var startup = Path.Combine(binDirectory, "startup.bat");
        if (!File.Exists(startup))
        {
            throw new FileNotFoundException("未找到 Tomcat 普通启动脚本。", startup);
        }

        // Normal Start/Restart uses Tomcat's standard `start` semantics. Keep
        // the explicit Catalina entry point separate so only that operation is
        // tied to `catalina.bat run` and its diagnostic console lifecycle.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "start-tomcat-server.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Startup
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            set "TITLE=MCPanel Tomcat Server"
            cd /d "{binDirectory}"
            call startup.bat
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var tomcatWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);
    }
    internal static void LaunchTomcatConsole(string tomcatRoot)
    {
        var root = Path.GetFullPath(tomcatRoot);
        var binDirectory = Path.Combine(root, "bin");
        var catalina = Path.Combine(binDirectory, "catalina.bat");
        if (!File.Exists(catalina))
        {
            throw new FileNotFoundException("未找到 Tomcat Catalina 启动脚本。", catalina);
        }

        // A previous Catalina console can remain at the post-run pause after
        // Tomcat has stopped. Close only MCPanel-owned launcher windows before
        // opening a fresh shared-server console so repeated starts/restarts do
        // not accumulate stale CMD windows.
        TomcatConsoleWindowManager.CloseExistingConsoleWindows();
        EnvironmentInstaller.NormalizeTomcatJvmPropertiesFile(root);
        var workDirectory = ComponentPaths.WorkRoot;
        Directory.CreateDirectory(workDirectory);
        var launcher = Path.Combine(workDirectory, "run-tomcat-server.cmd");
        AtomicFile.WriteAllText(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            title MCPanel Tomcat Server
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            cd /d "{binDirectory}"
            call catalina.bat run
            echo.
            echo Tomcat has exited. Review the Catalina output above.
            echo Press any key to close this window.
            pause >nul
            """,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        using var tomcatWindow = ProcessRunner.StartFile(
            launcher,
            string.Empty,
            binDirectory,
            windowStyle: ProcessWindowStyle.Normal);
    }

    private static void RetireLegacyTomcatWindowsService(string tomcatRoot)
    {
        if (!TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
        {
            return;
        }

        try
        {
            TomcatWindowsServiceManager.Stop();
        }
        catch (Exception serviceError)
        {
            EnvironmentOperationDiagnostics.RecordFailure(
                "环境管理",
                "停止旧 Tomcat Windows Service 包装器",
                serviceError);
        }

        try
        {
            TomcatWindowsServiceManager.Delete();
        }
        catch (Exception serviceError)
        {
            EnvironmentOperationDiagnostics.RecordFailure(
                "环境管理",
                "删除旧 Tomcat Windows Service 包装器",
                serviceError);
        }
    }

    public Task<string> StartTomcatInCatalinaConsoleAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var tomcatRoot = RequireTomcatRoot();
        if (TomcatProductInstanceManager.IsSharedTomcatRunning())
        {
            throw new InvalidOperationException("Tomcat Server 已经在运行。请先停止后再重新打开控制台。");
        }

        RetireLegacyTomcatWindowsService(tomcatRoot);
        LaunchTomcatConsole(tomcatRoot);
        return Task.FromResult(
            "Tomcat Server CMD 控制台已打开。MCPanel 不隐藏启动，也不等待端口或执行 HTTP 就绪诊断；请直接查看窗口中的 Catalina 输出。");
    }
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
        // The Environment card controls the shared server only. Independent/Catalina
        // product instances are managed from the Websites page and must not make the
        // shared Tomcat Server badge/button look running when no shared JVM exists.
        var anyRunning = sharedProcessRunning;
        var detail = sharedRunning
            ? "全部应用模式正在运行。"
            : sharedProcessRunning
                ? "检测到共享 Tomcat Java 进程，但配置端口未全部监听。"
                : managedRunning
                    ? "总 Tomcat Server 未运行；一个或多个应用正在单独运行，请在“网站”页面管理。"
                    : "当前没有运行中的总 Tomcat Server。";
        return Installed(anyRunning, $"Tomcat 已安装到 {tomcatRoot}。{detail}");
    }

    private static string RequireTomcatRoot() => FindTomcatRoot() ?? throw new DirectoryNotFoundException("未找到 Tomcat 安装目录。");
    private static string? FindTomcatRoot() => new ComponentLocator().FindTomcatRoot();
}
