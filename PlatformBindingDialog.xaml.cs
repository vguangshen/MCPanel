using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class PlatformBindingDialog : PanelModalWindow
{
    private readonly bool _java;
    private readonly ManualPlatformDefinition? _existing;
    private CancellationTokenSource? _operation;
    private bool _closed;
    internal Func<ManualPlatformDefinition, CancellationToken, Task<string>>? BindOperation { get; set; }
    internal string? ResultMessage { get; private set; }
    internal PlatformBindingDialog(bool java, ManualPlatformDefinition? existing = null)
    {
        _java = java;
        _existing = existing;
        InitializeComponent();
        TitleText.Text = (existing is null ? "绑定 " : "编辑 ") + (java ? "Java 平台" : ".NET 平台");
        DotNetOptions.Visibility = java ? Visibility.Collapsed : Visibility.Visible;
        HintText.Text = java
            ? "选择包含 WAR 文件或 WEB-INF 的软件目录，使用 Tomcat 运行。"
            : "选择网站根目录，使用 IIS 运行。ASP.NET Core 网站需预先安装对应的 Hosting Bundle。";
        if (existing is not null)
        {
            NameBox.Text = existing.Name;
            PathBox.Text = existing.Path;
            ArchitectureBox.SelectedValue = existing.Architecture;
            IisRuntimeBox.SelectedValue = existing.IisRuntime ?? "Auto";
        }
        Closing += (_, e) => { if (_operation is not null) { e.Cancel = true; _operation.Cancel(); StatusText.Text = "正在取消，请稍候…"; } };
        Closed += (_, _) => _closed = true;
    }
    internal ManualPlatformDefinition? Definition { get; private set; }
    private void Input_Changed(object sender, TextChangedEventArgs e)
    {
        if (BindButton is null || NameBox is null || PathBox is null) return;
        BindButton.IsEnabled = !string.IsNullOrWhiteSpace(NameBox.Text) && !string.IsNullOrWhiteSpace(PathBox.Text);
        if (StatusText is not null) StatusText.Text = string.Empty;
        if (sender == PathBox && JavaTargetBox is not null)
        {
            JavaTargetBox.ItemsSource = null;
            JavaTargetPanel.Visibility = Visibility.Collapsed;
        }
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
    private async void Bind_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        _operation = new CancellationTokenSource();
        var token = _operation.Token;
        FormPanel.IsEnabled = false;
        BindButton.IsEnabled = false;
        StatusText.Text = "正在检查软件目录…";
        var name = NameBox.Text;
        var path = PathBox.Text;
        var architecture = ArchitectureBox.SelectedValue as string ?? "Auto";
        var runtime = IisRuntimeBox.SelectedValue as string;
        if (runtime == "Auto") runtime = null;
        try
        {
            string? target = JavaTargetBox.SelectedItem as string;
            if (_java && target is null)
            {
                if (JavaTargetBox.Items.Count > 1) { StatusText.Text = "请选择实际部署的 Java 应用，然后确认绑定。"; return; }
                var targets = await Task.Run(() => LocalPlatformInspection.FindJavaTargets(path, token), token);
                if (targets.Count > 1)
                {
                    JavaTargetBox.ItemsSource = targets;
                    JavaTargetPanel.Visibility = Visibility.Visible;
                    StatusText.Text = "发现多个 Java 应用，请选择要绑定的应用。";
                    return;
                }
                target = targets.FirstOrDefault();
            }
            Definition = await Task.Run(() => new ManualPlatformStore().Prepare(name, path, _java,
                _existing?.Id, target, architecture, runtime, token), token);
            token.ThrowIfCancellationRequested();
            if (BindOperation is not null)
            {
                StatusText.Text = "正在创建运行绑定并验证，请稍候…";
                ResultMessage = await BindOperation(Definition, token);
            }
            _operation.Dispose();
            _operation = null;
            DialogResult = true;
        }
        catch (OperationCanceledException) { if (!_closed) StatusText.Text = "操作已取消，可修改后重试。"; }
        catch (Exception ex)
        {
            if (!_closed) StatusText.Text = ex.GetBaseException().Message;
            EnvironmentOperationDiagnostics.RecordFailure("平台绑定", name, ex);
        }
        finally
        {
            _operation?.Dispose(); _operation = null;
            if (!_closed) { FormPanel.IsEnabled = true; BindButton.IsEnabled = true; }
        }
    }
    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) { _operation.Cancel(); StatusText.Text = "正在取消，请稍候…"; }
        else DialogResult = false;
    }
    private sealed class FolderDialogOwner(IntPtr handle) : System.Windows.Forms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }
}
