using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class PlatformBindingDialog : PanelModalWindow
{
    private readonly bool _java;
    internal PlatformBindingDialog(bool java)
    {
        _java = java;
        InitializeComponent();
        TitleText.Text = java ? "绑定 Java 平台" : "绑定 .NET 平台";
        HintText.Text = java
            ? "选择包含 WAR 文件或 WEB-INF 的软件目录，使用 Tomcat 运行。"
            : "选择网站根目录，使用 IIS 运行。ASP.NET Core 网站需预先安装对应的 Hosting Bundle。";
    }
    internal ManualPlatformDefinition? Definition { get; private set; }
    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (BindButton is null || NameBox is null || PathBox is null) return;
        BindButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(PathBox.Text);
        if (StatusText is not null) StatusText.Text = string.Empty;
    }
    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择本地软件的网站根目录",
            ShowNewFolderButton = false,
            SelectedPath = Directory.Exists(PathBox.Text) ? PathBox.Text : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };
        var owner = new FolderDialogOwner(new System.Windows.Interop.WindowInteropHelper(this).Handle);
        if (dialog.ShowDialog(owner) != System.Windows.Forms.DialogResult.OK) return;
        PathBox.Text = dialog.SelectedPath;
        if (string.IsNullOrWhiteSpace(NameBox.Text)) NameBox.Text = new DirectoryInfo(dialog.SelectedPath).Name;
    }
    private void Bind_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Definition = new ManualPlatformStore().Prepare(NameBox.Text, PathBox.Text, _java);
            DialogResult = true;
        }
        catch (Exception ex) { StatusText.Text = ex.Message; }
    }
    private sealed class FolderDialogOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
