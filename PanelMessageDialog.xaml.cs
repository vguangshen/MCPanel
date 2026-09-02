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
        SetBrush("DialogSurfaceBrush", dark ? "#20262F" : "#FFFFFF");
        SetBrush("DialogLineBrush", dark ? "#3A4655" : "#DADCE0");
        SetBrush("DialogTextBrush", dark ? "#F3F6FA" : "#202124");
        SetBrush("DialogMutedBrush", dark ? "#B7C1CF" : "#5F6368");
        SetBrush("DialogTonalBrush", dark ? "#263A5A" : "#E8F0FE");
        SetBrush("DialogTonalTextBrush", dark ? "#A9C7FF" : "#174EA6");
        SetBrush("DialogPrimaryBrush", dark ? "#6EA3FF" : "#1A73E8");
        SetBrush("DialogPrimaryHoverBrush", dark ? "#8AB5FF" : "#1765CC");
        SetBrush("DialogDangerBrush", dark ? "#FF8A80" : "#D93025");
        SetBrush("ScrollThumbBrush", dark ? "#46515E" : "#B7C0CC");
        SetBrush("ScrollThumbHoverBrush", dark ? "#748092" : "#7E8A99");
        SetBrush("PrimaryBrush", dark ? "#6EA3FF" : "#1A73E8");
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

    private void SetBrush(string key, string hex)
    {
        Resources[key] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }
}
