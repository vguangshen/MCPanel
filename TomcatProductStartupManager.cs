using Microsoft.Win32;
using System.IO;
using System.Text;

namespace MCPanel;

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
            if (ProductDeploymentService.LoadTomcatDeploymentInfos().Count > 0)
            {
                EnsureRegistered();
            }
            else
            {
                RemoveRegistration();
            }
        }
        catch (Exception ex)
        {
            WriteLog("刷新 Tomcat 产品开机恢复项失败。", ex);
        }
    }

    public static void EnsureRegistered()
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "MCPanel.exe");
        if (!File.Exists(executable))
        {
            WriteLog($"暂未创建开机恢复项，主程序不存在：{executable}", null);
            return;
        }

        using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true)
            ?? throw new InvalidOperationException("无法打开当前用户的 Windows 启动项。");
        var command = BuildStartupCommand(executable);
        if (!string.Equals(key.GetValue(RunValueName) as string, command, StringComparison.OrdinalIgnoreCase))
        {
            key.SetValue(RunValueName, command, RegistryValueKind.String);
            WriteLog("已创建 Tomcat 产品开机自恢复项。", null);
        }
    }

    public static void RemoveRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
        if (key?.GetValue(RunValueName) is not null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            WriteLog("已移除 Tomcat 产品开机自恢复项。", null);
        }
    }

    internal static string BuildStartupCommand(string executable) =>
        $"\"{Path.GetFullPath(executable)}\" {RestoreArgument}";

    public static async Task<int> RestoreAsync(CancellationToken cancellationToken = default)
    {
        using var mutex = new Mutex(true, "Local\\MCPanel.TomcatProductRestore.v1", out var isPrimary);
        if (!isPrimary)
        {
            return 0;
        }

        var deployments = ProductDeploymentService.LoadTomcatDeploymentInfos();
        if (deployments.Count == 0)
        {
            RemoveRegistration();
            return 0;
        }

        var manager = new TomcatProductInstanceManager();
        var failures = 0;
        WriteLog($"开始恢复 {deployments.Count} 个 Tomcat 产品。", null);
        foreach (var deployment in deployments)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                if (manager.IsRunning(deployment.ProductId))
                {
                    WriteLog($"{deployment.ProductId} 已运行，跳过重复启动。", null);
                    continue;
                }

                var message = await manager.StartAsync(deployment.ProductId, catalinaMode: false, cancellationToken);
                WriteLog(message, null);
            }
            catch (Exception ex)
            {
                failures++;
                WriteLog($"恢复 {deployment.ProductId} 失败。", ex);
            }
        }

        WriteLog(failures == 0
            ? "Tomcat 产品开机自恢复完成。"
            : $"Tomcat 产品开机自恢复完成，其中 {failures} 个启动失败。", null);
        return failures == 0 ? 0 : 1;
    }

    internal static void WriteLog(string message, Exception? exception)
    {
        try
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "StoreData", "Work");
            var detail = exception is null ? string.Empty : $"{Environment.NewLine}{exception}";
            RollingLogWriter.Append(
                Path.Combine(directory, "tomcat-autostart.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}{detail}{Environment.NewLine}{Environment.NewLine}",
                Encoding.UTF8);
        }
        catch
        {
            // Startup recovery diagnostics must never break the Windows logon path.
        }
    }
}
