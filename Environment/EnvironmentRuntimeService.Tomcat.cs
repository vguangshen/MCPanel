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
    /// <summary>
    /// Puts the JVM's console output in UTF-8 to match the launcher's
    /// <c>chcp 65001</c>: <c>sun.std*.encoding</c> for Java 8-17 and
    /// <c>std*.encoding</c> for Java 18+. Without it Java 8 prints
    /// application output in the ANSI code page (GBK), which shows as
    /// mojibake. Only the console streams change; file.encoding and so the
    /// products' own file handling stay as they were. Set through
    /// JAVA_TOOL_OPTIONS because the JVM reads it directly, whatever the
    /// customised catalina.bat does with JAVA_OPTS.
    /// </summary>
    internal const string TomcatConsoleUtf8JavaOptions =
        "-Dsun.stdout.encoding=UTF-8 -Dsun.stderr.encoding=UTF-8 -Dstdout.encoding=UTF-8 -Dstderr.encoding=UTF-8";

    /// <summary>
    /// Writes a launcher as BOM-free UTF-8 with CRLF line ends. cmd.exe does
    /// not understand a BOM: on a GBK console it read the first line as a
    /// garbled, unknown command, so the window showed an error and, with
    /// echo never turned off, printed every following command.
    /// </summary>
    internal static void WriteTomcatLauncher(string path, string script) =>
        AtomicFile.WriteAllText(
            path,
            script.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

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
        WriteTomcatLauncher(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            set "JAVA_TOOL_OPTIONS=%JAVA_TOOL_OPTIONS% {TomcatConsoleUtf8JavaOptions}"
            title MCPanel Tomcat Startup
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            set "TITLE=MCPanel Tomcat Server"
            cd /d "{binDirectory}"
            call ".\startup.bat"
            """);

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
        WriteTomcatLauncher(
            launcher,
            $"""
            @echo off
            chcp 65001 >nul
            set "JAVA_TOOL_OPTIONS=%JAVA_TOOL_OPTIONS% {TomcatConsoleUtf8JavaOptions}"
            title MCPanel Tomcat Server
            set "CATALINA_HOME={root}"
            set "CATALINA_BASE={root}"
            cd /d "{binDirectory}"
            call ".\catalina.bat" run
            echo.
            echo Tomcat has exited. Review the Catalina output above.
            echo Press any key to close this window.
            pause >nul
            """);

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
