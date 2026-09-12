using Microsoft.Win32;

namespace MCPanel;

internal static class StartupRegistrationBootstrapper
{
    private const string StateKeyPath = @"Software\MCPanel";
    private const string DefaultAppliedValueName = "StartupDefaultApplied";

    internal static bool IsDefaultAlreadyApplied(object? value) =>
        value is int intValue && intValue == 1 ||
        value is long longValue && longValue == 1;

    internal static void EnsureDefaultRegistration()
    {
        using (var key = Registry.CurrentUser.OpenSubKey(StateKeyPath, writable: false))
        {
            if (IsDefaultAlreadyApplied(key?.GetValue(DefaultAppliedValueName)))
            {
                return;
            }
        }

        // The original ITMCStore installer registers the application under the
        // current user's Windows Run key. MCPanel is distributed as a portable
        // package, so apply the same default on the first normal UI launch.
        // PanelSettingsService keeps the actual Run entry in one place and adds
        // --tray so Windows logon starts MCPanel quietly in the notification area.
        var settings = new PanelSettingsService();
        if (!settings.IsStartupEnabled())
        {
            settings.SetStartupEnabled(true);
        }

        using var writable = Registry.CurrentUser.CreateSubKey(StateKeyPath, writable: true) ??
                             throw new InvalidOperationException("无法创建 MCPanel 当前用户配置注册表项。");
        writable.SetValue(DefaultAppliedValueName, 1, RegistryValueKind.DWord);
    }
}
