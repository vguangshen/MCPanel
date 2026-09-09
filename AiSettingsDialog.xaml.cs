using System.Windows;

namespace MCPanel;

public partial class AiSettingsDialog : PanelModalWindow
{
    private readonly CancellationTokenSource _closed = new();
    private bool _isClosed;
    public AiSettingsDialog()
    {
        InitializeComponent();
        var settings = new AiAnalysisService().LoadSettings();
        EndpointBox.Text = settings.Endpoint; ModelBox.Text = settings.Model;
        ProviderBox.Text = settings.Provider; ApiKeyBox.Password = settings.ApiKey;
        Closed += (_, _) => { _isClosed = true; _closed.Cancel(); };
    }
    private AiProviderSettings Read() => new()
    {
        Provider = ProviderBox.Text.Trim(), Endpoint = EndpointBox.Text.Trim(), Model = ModelBox.Text.Trim(), ApiKey = ApiKeyBox.Password.Trim()
    };
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        SaveButton.IsEnabled = false; TestButton.IsEnabled = false;
        try { var settings = Read(); await Task.Run(() => AiSettingsStore.Save(settings)); if (!_isClosed) DialogResult = true; }
        catch (Exception ex) { if (!_isClosed) StatusText.Text = ex.Message; }
        finally { if (!_isClosed) { SaveButton.IsEnabled = true; TestButton.IsEnabled = true; } }
    }
    private async void Test_Click(object sender, RoutedEventArgs e)
    {
        TestButton.IsEnabled = false; SaveButton.IsEnabled = false; FormPanel.IsEnabled = false;
        StatusText.Text = "正在测试连接…";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_closed.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try { await new AiAnalysisService().TestConnectionAsync(Read(), timeout.Token); if (!_isClosed) StatusText.Text = "连接成功，模型已响应。"; }
        catch (OperationCanceledException) { if (!_isClosed) StatusText.Text = "连接超时，请检查接口地址和网络。"; }
        catch (Exception ex) { if (!_isClosed) StatusText.Text = ex.GetBaseException().Message; }
        finally { if (!_isClosed) { TestButton.IsEnabled = true; SaveButton.IsEnabled = true; FormPanel.IsEnabled = true; } }
    }
}
