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
    private async Task InstallTomcatAsync(EnvironmentDownloadSettings downloads, Action<InstallProgress> progress, CancellationToken cancellationToken)
    {
        var locator = new ComponentLocator();
        var root = SelectTomcatInstallRoot(locator);
        Directory.CreateDirectory(root);
        var tomcatRoot = locator.FindTomcatRoot(root);
        if (tomcatRoot is null || !File.Exists(Path.Combine(tomcatRoot, "bin", "startup.bat")))
        {
            var archive = await DownloadAbsoluteFileAsync(downloads.TomcatPackageUrl, GetPackageDirectory(), EnvironmentComponentCatalog.TomcatPackageFileName, progress, 0, 65, cancellationToken);
            progress(InstallingProgress(70, "正在解压 Tomcat 定制包...", 8));
            await ExtractZipAsync(archive, root, cancellationToken);
            tomcatRoot = new ComponentLocator().FindTomcatRoot(root) ?? Path.Combine(root, EnvironmentComponentCatalog.TomcatDirectoryName);
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

        progress(InstallingProgress(84, "正在清理旧 Tomcat 后台服务并准备可见控制台...", 58));
        TomcatProductStartupManager.RemoveRegistration();
        await TomcatProductInstanceManager.StopAllTomcatProcessesAsync(cancellationToken, throwOnFailure: false);

        if (TomcatWindowsServiceManager.IsRegisteredForRoot(tomcatRoot))
        {
            try { TomcatWindowsServiceManager.Stop(); } catch { }
            try { TomcatWindowsServiceManager.Delete(); } catch { }
        }

        progress(InstallingProgress(92, "正在以标准 start 模式打开 Tomcat Server CMD 控制台...", 78));
        EnvironmentRuntimeService.LaunchTomcatStartConsole(tomcatRoot);

        progress(InstallingProgress(100, $"Tomcat 已安装到 {tomcatRoot}，并已按标准 start 模式在可见 CMD 控制台中启动；后续启动与重启不再通过 Windows Service 隐藏运行。"));
    }
    private static void ConfigureTomcat(string tomcatRoot)
    {
        var serverXml = Path.Combine(tomcatRoot, "conf", "server.xml");
        if (!File.Exists(serverXml))
        {
            return;
        }

        var text = File.ReadAllText(serverXml, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
            .TrimStart('\uFEFF');
        text = text.Replace("$[ServerPort]", "8005", StringComparison.OrdinalIgnoreCase)
            .Replace("$[ConnectorPort]", "8080", StringComparison.OrdinalIgnoreCase);
        AtomicFile.WriteAllText(serverXml, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        var jvmProperties = Path.Combine(tomcatRoot, "conf", "jvm.properties");
        if (File.Exists(jvmProperties))
        {
            var jvmText = NormalizeTomcatJvmProperties(
                File.ReadAllText(jvmProperties, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)));
            if (jvmText.IndexOf("-XX:ErrorFile=", StringComparison.OrdinalIgnoreCase) < 0)
            {
                var lines = jvmText.Replace("\r\n", "\n", StringComparison.Ordinal)
                    .Replace('\r', '\n')
                    .Split('\n');
                if (lines.Length > 0 && !string.IsNullOrWhiteSpace(lines[0]))
                {
                    var errorFile = Path.Combine(tomcatRoot, "logs", "hs_err_pid%p.log")
                        .Replace("\\", "/");
                    lines[0] = lines[0].TrimEnd() + " -XX:ErrorFile=\"" + errorFile + "\"";
                    jvmText = string.Join(Environment.NewLine, lines);
                }
            }

            // catalina.bat reads this file with `for /F`; a UTF-8 BOM becomes
            // part of the first JVM option (for example `﻿-Xms2048m`).
            // Always rewrite it as BOM-free UTF-8, including already-customized
            // packages that do not enter the branch above.
            AtomicFile.WriteAllText(jvmProperties, NormalizeTomcatJvmProperties(jvmText), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        }
    }

    internal static string NormalizeTomcatJvmProperties(string text) =>
        text.TrimStart('\uFEFF');

    internal static void NormalizeTomcatJvmPropertiesFile(
        string tomcatRoot,
        bool useInstanceLocalErrorFile = false)
    {
        var path = Path.Combine(tomcatRoot, "conf", "jvm.properties");
        if (!File.Exists(path))
        {
            return;
        }

        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        var text = NormalizeTomcatJvmProperties(File.ReadAllText(path, encoding));
        if (useInstanceLocalErrorFile)
        {
            var lines = text.Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace('\r', '\n')
                .Split('\n');
            var firstOptionLine = Array.FindIndex(lines, line =>
                !string.IsNullOrWhiteSpace(line) &&
                !line.TrimStart().StartsWith("#", StringComparison.Ordinal));
            if (firstOptionLine >= 0)
            {
                var errorFile = Path.Combine(tomcatRoot, "logs", "hs_err_pid%p.log")
                    .Replace("\\", "/", StringComparison.Ordinal);
                var replacement = $"-XX:ErrorFile=\"{errorFile}\"";
                var pattern = @"-XX:ErrorFile=(?:""[^""]*""|\S+)";
                lines[firstOptionLine] = Regex.IsMatch(
                        lines[firstOptionLine],
                        pattern,
                        RegexOptions.IgnoreCase)
                    ? Regex.Replace(
                        lines[firstOptionLine],
                        pattern,
                        replacement,
                        RegexOptions.IgnoreCase)
                    : lines[firstOptionLine].TrimEnd() + " " + replacement;
                text = string.Join(Environment.NewLine, lines);
            }
        }

        AtomicFile.WriteAllText(path, text, encoding);
    }

    private static string SelectTomcatInstallRoot(ComponentLocator locator)
    {
        foreach (var root in ComponentPaths.TomcatSearchRoots)
        {
            if (locator.FindTomcatRoot(root) is not null)
            {
                return root;
            }
        }

        return ComponentPaths.TomcatRoot;
    }
}
