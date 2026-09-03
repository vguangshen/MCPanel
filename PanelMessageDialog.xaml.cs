using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace MCPanel;

public sealed partial class PanelMessageDialog : Window
{
    private readonly MessageBoxButton _buttons;

    public PanelMessageDialog(string message, string caption, MessageBoxButton buttons, MessageBoxImage image)
    {
        InitializeComponent();
        if (TryFindResource("DialogDangerBrush") is null)
        {
            // Tests and host integrations can construct the dialog without
            // running App.xaml first. Use the shared palette as a local
            // fallback only in that non-standard path.
            PanelThemeService.Apply(false, Resources);
        }
        _buttons = buttons;
        Title = caption;
        TitleText.Text = string.IsNullOrWhiteSpace(caption) ? "MCPanel" : caption;
        MessageText.Text = message;
        IconText.Text = image switch
        {
            MessageBoxImage.Warning => "\uE7BA",
            MessageBoxImage.Error => "\uEA39",
            MessageBoxImage.Question => "\uE9CE",
            _ => "\uE946"
        };
        IconText.Foreground = image switch
        {
            MessageBoxImage.Warning => (Brush)FindResource("DialogDangerBrush"),
            MessageBoxImage.Error => (Brush)FindResource("DialogDangerBrush"),
            _ => (Brush)FindResource("DialogPrimaryBrush")
        };

        BuildButtons(buttons);
    }

    public MessageBoxResult Result { get; private set; } = MessageBoxResult.Cancel;

    public void ApplyTheme(bool dark)
    {
        PanelThemeService.Apply(dark, Resources);
    }

    private void BuildButtons(MessageBoxButton buttons)
    {
        switch (buttons)
        {
            case MessageBoxButton.OK:
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true);
                break;
            case MessageBoxButton.OKCancel:
                AddButton("确定", MessageBoxResult.OK, primary: true, isDefault: true);
                AddButton("取消", MessageBoxResult.Cancel, primary: false, isCancel: true);
                break;
            case MessageBoxButton.YesNo:
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("否", MessageBoxResult.No, primary: false, isCancel: true);
                break;
            case MessageBoxButton.YesNoCancel:
                AddButton("是", MessageBoxResult.Yes, primary: true, isDefault: true);
                AddButton("否", MessageBoxResult.No, primary: false);
                AddButton("取消", MessageBoxResult.Cancel, primary: false, isCancel: true);
                break;
        }
    }

    private void AddButton(string text, MessageBoxResult result, bool primary, bool isDefault = false, bool isCancel = false)
    {
        var button = new Button
        {
            Content = text,
            Style = (Style)FindResource(primary ? "DialogPrimaryButton" : "DialogButton"),
            Margin = new Thickness(8, 0, 0, 0),
            IsDefault = isDefault,
            IsCancel = isCancel,
            Tag = result
        };
        button.Click += DialogButton_Click;
        ButtonsPanel.Children.Add(button);
    }

    private void DialogButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: MessageBoxResult result })
        {
            Complete(result);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
        {
            return;
        }

        var result = _buttons switch
        {
            MessageBoxButton.OK => MessageBoxResult.OK,
            MessageBoxButton.YesNo => MessageBoxResult.No,
            _ => MessageBoxResult.Cancel
        };
        Complete(result);
        e.Handled = true;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Complete(MessageBoxResult result)
    {
        Result = result;
        DialogResult = true;
        Close();
    }

}
