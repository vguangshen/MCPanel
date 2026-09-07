using System.Globalization;
using System.Windows;

namespace MCPanel;

internal enum PanelInputDialogMode
{
    Port,
    Password
}

public partial class PanelInputDialog : PanelModalWindow
{
    private readonly PanelInputDialogMode _mode;

    private PanelInputDialog(PanelInputDialogMode mode)
    {
        InitializeComponent();
        _mode = mode;
        Loaded += (_, _) =>
        {
            if (_mode == PanelInputDialogMode.Password)
            {
                PasswordInput.Focus();
            }
            else
            {
                ValueTextBox.Focus();
                ValueTextBox.SelectAll();
            }
        };
    }

    public string? ResultText { get; private set; }

    public static PanelInputDialog CreatePortEditor(int currentPort)
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Port);
        dialog.TitleText.Text = "修改 MySQL 端口";
        dialog.SubtitleText.Text = "更新 MySQL 服务监听端口，并同步面板连接配置。";
        dialog.IconText.Text = "\uE8D7";
        dialog.ContextBadgeText.Text = "MySQL";
        dialog.InputLabel.Text = "MySQL 端口";
        dialog.ValueTextBox.Text = currentPort.ToString(CultureInfo.InvariantCulture);
        dialog.TextHint.Text = "修改后会同步更新 MySQL 配置与面板连接信息。";
        dialog.TextPanel.Visibility = Visibility.Visible;
        dialog.PasswordPanel.Visibility = Visibility.Collapsed;
        return dialog;
    }


    public static PanelInputDialog CreateAccountApiPortEditor(int currentPort)
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Port);
        dialog.TitleText.Text = "修改 Account API 监听端口";
        dialog.SubtitleText.Text = "修改内置 Account API 的监听端口，用于避开其他软件的端口占用。";
        dialog.IconText.Text = "\uE8D7";
        dialog.ContextBadgeText.Text = "Account API";
        dialog.InputLabel.Text = "监听端口";
        dialog.ValueTextBox.Text = currentPort.ToString(CultureInfo.InvariantCulture);
        dialog.TextHint.Text = "端口范围 1–65535；API 已启用时确认后会立即切换，失败会自动恢复原端口。";
        dialog.TextPanel.Visibility = Visibility.Visible;
        dialog.PasswordPanel.Visibility = Visibility.Collapsed;
        return dialog;
    }

    public static PanelInputDialog CreatePasswordEditor()
    {
        var dialog = new PanelInputDialog(PanelInputDialogMode.Password);
        dialog.TitleText.Text = "修改 MySQL 密码";
        dialog.SubtitleText.Text = "更新 root 密码，并同步 MCPanel 保存的连接凭据。";
        dialog.IconText.Text = "\uE72E";
        dialog.TextPanel.Visibility = Visibility.Collapsed;
        dialog.PasswordPanel.Visibility = Visibility.Visible;
        return dialog;
    }

    public void ApplyTheme(bool dark) => PanelThemeService.Apply(dark, Resources);

    private void Confirm_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Text = string.Empty;
        if (_mode == PanelInputDialogMode.Port)
        {
            var text = ValueTextBox.Text.Trim();
            if (!int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            {
                StatusText.Text = "请输入 1 到 65535 之间的有效端口号。";
                ValueTextBox.Focus();
                ValueTextBox.SelectAll();
                return;
            }

            ResultText = text;
        }
        else
        {
            var password = PasswordInput.Password;
            if (password.Length is < 1 or > 64)
            {
                StatusText.Text = "密码不能为空，最多 64 个字符；纯数字密码也可以。";
                PasswordInput.Focus();
                PasswordInput.SelectAll();
                return;
            }

            if (password.Any(char.IsControl))
            {
                StatusText.Text = "密码不能包含换行、制表符等控制字符。";
                PasswordInput.Focus();
                PasswordInput.SelectAll();
                return;
            }

            if (!string.Equals(password, PasswordConfirmation.Password, StringComparison.Ordinal))
            {
                StatusText.Text = "两次输入的密码不一致。";
                PasswordConfirmation.Focus();
                PasswordConfirmation.SelectAll();
                return;
            }

            ResultText = password;
        }

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
