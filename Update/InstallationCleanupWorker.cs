using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace MCPanel;

internal static class InstallationCleanupWorker
{
    internal const string Argument = "--uninstall-components";

    internal static bool IsUnderRoot(string? path, string root) => !string.IsNullOrWhiteSpace(path) &&
        (Path.GetFullPath(path!).TrimEnd('\\').Equals(Path.GetFullPath(root).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ||
         Path.GetFullPath(path!).StartsWith(Path.GetFullPath(root).TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase));

    internal static string? ReadServiceImage(string name)
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + name);
        var command = Environment.ExpandEnvironmentVariables(key?.GetValue("ImagePath") as string ?? string.Empty).Trim();
        if (command.StartsWith("\""))
        {
            var end = command.IndexOf('"', 1);
            return end > 1 ? command.Substring(1, end - 1) : null;
        }
        var exe = command.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        return exe >= 0 ? command.Substring(0, exe + 4) : null;
    }

    internal static bool OwnsMySql(string? executable, string root, IEnumerable<string> nativeRoots) =>
        !string.IsNullOrWhiteSpace(executable) &&
        (Path.GetFileName(executable).Equals("mysqld.exe", StringComparison.OrdinalIgnoreCase) ||
         Path.GetFileName(executable).Equals("mysqld-itmc.exe", StringComparison.OrdinalIgnoreCase)) &&
        (IsUnderRoot(executable, root) || nativeRoots.Any(path => IsUnderRoot(executable, path)));

    internal static bool OwnsSqlServer(IEnumerable<string> startupParameters, string root) => startupParameters.Any(value =>
        value.StartsWith("-d", StringComparison.OrdinalIgnoreCase) && IsUnderRoot(value.Substring(2).Trim('"'), root));

    internal static async Task<int> RunAsync(IReadOnlyList<string> arguments)
    {
        var index = arguments.ToList().FindIndex(value => value.Equals(Argument, StringComparison.OrdinalIgnoreCase));
        var resultFile = index >= 0 && index + 1 < arguments.Count ? arguments[index + 1] : string.Empty;
        if (!IsUnderRoot(resultFile, Path.GetTempPath()) ||
            !Path.GetFileName(resultFile).StartsWith("MCPanel-CompleteUninstall-", StringComparison.Ordinal)) return 2;
        void Report(string phase, bool success = false, string? error = null) =>
            AtomicFile.WriteAllText(resultFile, JsonSerializer.Serialize(new { phase, success, error }), new UTF8Encoding(false));
        try
        {
            if (!ProcessRunner.IsAdministrator()) throw new UnauthorizedAccessException("完整卸载需要管理员权限。");
            var root = ComponentPaths.ApplicationRoot.TrimEnd('\\');
            if (InstallerRegistration.FindRegistration(root) is null) throw new InvalidOperationException("此目录不是已注册的 MCPanel 安装，已取消清理。");
            Report("正在移除 MCPanel 托管的网站与服务...");
            await RemoveOwnedWebsitesAsync(root);
            var runtime = new EnvironmentRuntimeService();
            var locator = new ComponentLocator();
            var nginx = locator.FindNginxExecutable();
            var nginxService = ReadServiceImage("nginx");
            if (nginx is not null && (nginxService is null || IsUnderRoot(nginxService, root) ||
                IsUnderRoot(nginxService, Path.GetDirectoryName(nginx)!)))
            {
                Report("正在停止并卸载 Nginx...");
                await runtime.UninstallAsync(EnvironmentKind.Nginx);
            }
            var mysqlService = ReadServiceImage("MySQL80");
            if (OwnsMySql(mysqlService, root, new[] { ComponentPaths.MySqlRoot }) ||
                (mysqlService is null && locator.FindMySqlExecutable() is not null))
            {
                Report("正在正常关闭并卸载 MySQL 数据库...");
                await runtime.UninstallAsync(EnvironmentKind.MySql);
            }
            if (locator.FindTomcatRoot() is not null)
            {
                Report("正在停止 Tomcat 及产品实例...");
                await runtime.UninstallAsync(EnvironmentKind.Tomcat);
            }
            using (var frp = new FrpManager())
            {
                if (frp.IsInstalled || FrpWindowsServiceManager.IsRegisteredForRoot(root))
                {
                    Report("正在停止并移除 FRP 服务...");
                    await frp.UninstallAsync();
                }
            }
            using (var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64))
            using (var key = machine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL"))
            {
                if (key?.GetValue("MSSQLSERVER") is string instance)
                {
                    using var parameters = machine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\" + instance + @"\MSSQLServer\Parameters");
                    var values = parameters?.GetValueNames().Select(name => parameters.GetValue(name) as string ?? string.Empty) ?? [];
                    if (OwnsSqlServer(values, root))
                    {
                        Report("正在调用 SQL Server 官方卸载流程...");
                        await runtime.UninstallAsync(EnvironmentKind.SqlServer);
                        using var remaining = machine.OpenSubKey(@"SOFTWARE\Microsoft\Microsoft SQL Server\Instance Names\SQL");
                        if (remaining?.GetValue("MSSQLSERVER") is not null)
                            throw new InvalidOperationException("SQL Server 官方卸载尚未完成，已保留 MCPanel 卸载入口。请先修复数据库卸载或重启后重试。");
                    }
                }
            }
            var settings = new PanelSettingsService();
            foreach (var tool in new[] { settings.GetNavicatInstallation(), settings.GetSqlManagementStudioInstallation() })
            {
                if (tool is null || !IsUnderRoot(tool.ExecutablePath, root)) continue;
                Report("正在调用数据库管理工具的官方卸载程序...");
                await DatabaseToolUninstaller.UninstallAsync(tool, ComponentPaths.WorkRoot);
            }
            Report("托管组件已停止，正在清理文件和系统注册信息...", success: true);
            return 0;
        }
        catch (Exception error)
        {
            Report("组件清理未完成，面板卸载已停止。", error: error.Message);
            return 1;
        }
    }

    private static Task RemoveOwnedWebsitesAsync(string root)
    {
        var websites = new CustomWebsiteService();
        var definitions = websites.LoadAllForUninstall();
        var library = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), @"System32\inetsrv\Microsoft.Web.Administration.dll");
        if (!File.Exists(library)) return Task.CompletedTask;
        var assembly = Assembly.LoadFrom(library);
        dynamic manager = Activator.CreateInstance(assembly.GetType("Microsoft.Web.Administration.ServerManager", throwOnError: true)!)!;
        try
        {
            var pools = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var definition in definitions)
            {
                dynamic existing = manager.Sites[definition.Name];
                if (existing is null) continue;
                dynamic app = existing.Applications["/"];
                var physical = Environment.ExpandEnvironmentVariables((string)app.VirtualDirectories["/"].PhysicalPath);
                if (!InstallerRegistration.IsSameRoot(physical, Environment.ExpandEnvironmentVariables(definition.PhysicalPath)) ||
                    !string.Equals((string)app.ApplicationPoolName, definition.ApplicationPoolName, StringComparison.OrdinalIgnoreCase)) continue;
                pools.Add((string)app.ApplicationPoolName);
                manager.Sites.Remove(existing);
            }
            var sites = ((System.Collections.IEnumerable)manager.Sites).Cast<object>().ToArray();
            foreach (dynamic site in sites)
            {
                var apps = ((System.Collections.IEnumerable)site.Applications).Cast<object>().ToArray();
                foreach (dynamic app in apps)
                {
                    var physical = Environment.ExpandEnvironmentVariables((string)app.VirtualDirectories["/"].PhysicalPath);
                    if (!IsUnderRoot(physical, root)) continue;
                    pools.Add((string)app.ApplicationPoolName);
                    if ((string)app.Path == "/") { manager.Sites.Remove(site); break; }
                    site.Applications.Remove(app);
                }
            }
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (dynamic site in manager.Sites)
                foreach (dynamic app in site.Applications) used.Add((string)app.ApplicationPoolName);
            foreach (var pool in pools.Where(name => name.StartsWith("MCPanel", StringComparison.OrdinalIgnoreCase) && !used.Contains(name)))
            {
                dynamic value = manager.ApplicationPools[pool];
                if (value is not null) manager.ApplicationPools.Remove(value);
            }
            manager.CommitChanges();
        }
        finally { ((IDisposable)manager).Dispose(); }
        return Task.CompletedTask;
    }
}
