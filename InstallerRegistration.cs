using System.IO;
using Microsoft.Win32;

namespace MCPanel;

internal static class InstallerRegistration
{
    internal const string RegisterArgument = "--register-uninstaller";
    internal const string UninstallScriptName = "Uninstall-MCPanel.ps1";
    internal const string ProductionId = "{4877D128-D808-4B6B-B4CB-D4D0476F5275}_is1";
    internal const string TestId = "MCPanel.Setup.IntegrationTest_is1";
    private const string RegistryPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\";

    internal const string UninstallMutexName = "Local\\MCPanel.Uninstall.v2";

    internal static bool IsUninstallInProgress()
    {
        try { using var mutex = Mutex.OpenExisting(UninstallMutexName); return true; }
        catch (WaitHandleCannotBeOpenedException) { return false; }
    }

    private static string QuotePath(string path) => "\"" + path + "\"";

    internal static string BuildCommand(string root, bool silent = false) =>
        $"{QuotePath(ProcessRunner.NativeWindowsPowerShellPath())} -NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File " +
        $"{QuotePath(Path.Combine(root, UninstallScriptName))} -InstallRoot {QuotePath(root.TrimEnd('\\'))}" +
        (silent ? " -Silent" : string.Empty);

    internal static bool IsSameRoot(string? left, string right) => !string.IsNullOrWhiteSpace(left) &&
        Path.GetFullPath(left!).TrimEnd('\\').Equals(Path.GetFullPath(right).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    internal static string? FindRegistration(string root)
    {
        if (!File.Exists(Path.Combine(root, "Maintenance", "installer.marker"))) return null;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        foreach (var id in new[] { ProductionId, TestId })
        {
            using var key = machine.OpenSubKey(RegistryPath + id);
            if (key is not null && IsSameRoot(key.GetValue("InstallLocation") as string, root)) return id;
        }
        return null;
    }

    internal static bool NeedsRefresh(string root)
    {
        if (!File.Exists(Path.Combine(root, UninstallScriptName))) return false;
        var id = FindRegistration(root);
        if (id is null) return false;
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RegistryPath + id);
        return !string.Equals(key?.GetValue("UninstallString") as string, BuildCommand(root), StringComparison.OrdinalIgnoreCase);
    }

    internal static void Refresh(string root)
    {
        var id = FindRegistration(root) ?? throw new InvalidOperationException("未找到此目录的 MCPanel 安装记录。");
        if (!File.Exists(Path.Combine(root, UninstallScriptName))) throw new FileNotFoundException("完整卸载组件缺失，请运行新版安装器修复。");
        using var machine = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = machine.OpenSubKey(RegistryPath + id, writable: true)
            ?? throw new InvalidOperationException("无法更新 MCPanel 卸载入口。");
        key.SetValue("UninstallString", BuildCommand(root));
        key.SetValue("QuietUninstallString", BuildCommand(root, silent: true));
        key.SetValue("DisplayVersion", ApplicationUpdateService.CurrentVersionText);
        key.SetValue("MCPanelUninstallProtocol", 2, RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
    }

    internal static async Task EnsureCurrentAsync()
    {
        var root = ComponentPaths.ApplicationRoot.TrimEnd('\\');
        if (!NeedsRefresh(root)) return;
        if (ProcessRunner.IsAdministrator()) { Refresh(root); return; }
        var result = await ProcessRunner.RunAsync(Path.Combine(root, "MCPanel.exe"), RegisterArgument,
            root, elevated: true, CancellationToken.None, timeout: TimeSpan.FromMinutes(2));
        if (result.ExitCode != 0) throw new InvalidOperationException("完整卸载入口尚未更新，请重新运行新版安装器完成维护组件升级。");
    }
}
