using Microsoft.Win32;
using System.IO;
using System.Text;

namespace MCPanel;

/// <summary>
/// Compatibility cleanup for the legacy per-product logon startup entry.
/// Individual Tomcat product instances are intentionally user-controlled in
/// 1.3.6; only the shared Tomcat Server is registered as an automatic Windows
/// service. Keeping this recognizer lets an already queued legacy Run command
/// remove itself harmlessly after an upgrade.
/// </summary>
internal static class TomcatProductStartupManager
{
    internal const string RestoreArgument = "--restore-tomcat-products";
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "MCPanelTomcatProducts";

    public static bool IsRestoreRequest(IReadOnlyList<string> arguments) =>
        arguments.Any(argument => argument.Equals(RestoreArgument, StringComparison.OrdinalIgnoreCase));

    public static void RefreshRegistration()
    {
        try
        {
            RemoveRegistration();
        }
        catch (Exception ex)
        {
            WriteLog("移除旧版 Tomcat 产品开机恢复项失败。", ex);
        }
    }

    public static void EnsureRegistered()
    {
        // Kept for source compatibility with older call sites. 1.3.6 no longer
        // registers individual product instances for logon startup.
        RemoveRegistration();
    }

    public static void RemoveRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(RunValueName) is not null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            WriteLog("已移除旧版 Tomcat 产品开机自恢复项；单应用实例改为按需启动。", null);
        }
    }

    internal static string BuildStartupCommand(string executable) =>
        $"\"{Path.GetFullPath(executable)}\" {RestoreArgument}";

    public static Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RemoveRegistration();
        WriteLog("检测到旧版 Tomcat 产品恢复命令，已跳过自动启动并清理启动项。", null);
        return Task.FromResult(0);
    }

    internal static void WriteLog(string message, Exception? exception)
    {
        try
        {
            var directory = ComponentPaths.WorkRoot;
            var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            RollingLogWriter.Append(
                Path.Combine(directory, "tomcat-autostart.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{detail}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Startup migration diagnostics must never break Windows logon.
        }
    }
}
