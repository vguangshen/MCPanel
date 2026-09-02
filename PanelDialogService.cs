using System.Linq;
using System.Windows;

namespace MCPanel;

internal static class PanelDialogService
{
    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image)
    {
        var dialog = new PanelMessageDialog(message, caption, buttons, image);
        if (owner is not null)
        {
            dialog.Owner = owner;
        }

        var themeOwner = owner as MainWindow ?? owner?.Owner as MainWindow;
        dialog.ApplyTheme(themeOwner?.IsDarkThemeActive == true);
        dialog.ShowDialog();
        return dialog.Result;
    }

    public static Window? FindOwner()
    {
        return Application.Current?.Windows.OfType<Window>().FirstOrDefault(window => window.IsActive)
            ?? Application.Current?.MainWindow;
    }
}

internal static class MessageBox
{
    public static MessageBoxResult Show(string message) =>
        PanelDialogService.Show(PanelDialogService.FindOwner(), message, "MCPanel", MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(string message, string caption) =>
        PanelDialogService.Show(PanelDialogService.FindOwner(), message, caption, MessageBoxButton.OK, MessageBoxImage.Information);

    public static MessageBoxResult Show(
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        PanelDialogService.Show(PanelDialogService.FindOwner(), message, caption, buttons, image);

    public static MessageBoxResult Show(
        Window owner,
        string message,
        string caption,
        MessageBoxButton buttons,
        MessageBoxImage image) =>
        PanelDialogService.Show(owner, message, caption, buttons, image);
}
