using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;

namespace MCPanel;

public partial class ProductWebsiteDialog : PanelModalWindow
{
    private readonly bool _hadExistingConfiguration;

    internal ProductWebsiteDialog(ProductWebsiteSettings settings)
    {
        InitializeComponent();
        _hadExistingConfiguration = settings.Enabled;
        EnabledBox.IsChecked = settings.Enabled;
        DomainsBox.Text = settings.Domains;
        HttpPortBox.Text = settings.HttpPort.ToString(CultureInfo.InvariantCulture);
        SslBox.IsChecked = settings.SslEnabled;
        HttpsPortBox.Text = settings.HttpsPort.ToString(CultureInfo.InvariantCulture);
        CertificateBox.Text = settings.CertificatePath;
        CertificateKeyBox.Text = settings.CertificateKeyPath;
        RedirectBox.IsChecked = settings.RedirectHttpToHttps;
        BandwidthBox.Text = settings.MaxRateKbps.ToString(CultureInfo.InvariantCulture);
        RemoveButton.IsEnabled = settings.Enabled;
        UpdateEnabledState();
    }

    internal ProductWebsiteSettings? ResultSettings { get; private set; }

    public void ApplyTheme(bool dark)
    {
        PanelThemeService.Apply(dark, Resources);
    }

    private void OptionChanged(object sender, RoutedEventArgs e) => UpdateEnabledState();

    private void UpdateEnabledState()
    {
        if (!IsInitialized)
        {
            return;
        }

        var enabled = EnabledBox.IsChecked == true;
        DomainsBox.IsEnabled = enabled;
        HttpPortBox.IsEnabled = enabled;
        SslBox.IsEnabled = enabled;
        BandwidthBox.IsEnabled = enabled;
        var ssl = enabled && SslBox.IsChecked == true;
        HttpsPortBox.IsEnabled = ssl;
        CertificateBox.IsEnabled = ssl;
        CertificateKeyBox.IsEnabled = ssl;
        RedirectBox.IsEnabled = ssl;
    }

    private void BrowseCertificate_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFile("证书文件 (*.pem;*.crt;*.cer)|*.pem;*.crt;*.cer|所有文件 (*.*)|*.*");
        if (path is not null) CertificateBox.Text = path;
    }

    private void BrowseKey_Click(object sender, RoutedEventArgs e)
    {
        var path = BrowseFile("私钥文件 (*.key;*.pem)|*.key;*.pem|所有文件 (*.*)|*.*");
        if (path is not null) CertificateKeyBox.Text = path;
    }

    private string? BrowseFile(string filter)
    {
        var dialog = new OpenFileDialog { Filter = filter, CheckFileExists = true, Multiselect = false };
        return dialog.ShowDialog(this) == true ? dialog.FileName : null;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var enabled = EnabledBox.IsChecked == true;
            var httpPort = ParsePort(HttpPortBox.Text, "HTTP");
            var httpsPort = ParsePort(HttpsPortBox.Text, "HTTPS");
            if (!int.TryParse(BandwidthBox.Text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var bandwidth) ||
                bandwidth is < 0 or > 1048576)
            {
                throw new InvalidOperationException("单连接限速必须是 0 到 1048576 之间的整数。");
            }

            if (enabled)
            {
                ProductWebsiteService.NormalizeDomains(DomainsBox.Text);
            }

            var ssl = enabled && SslBox.IsChecked == true;
            if (ssl && (!File.Exists(CertificateBox.Text.Trim()) || !File.Exists(CertificateKeyBox.Text.Trim())))
            {
                throw new InvalidOperationException("启用 HTTPS 时必须选择有效的证书和私钥文件。");
            }

            ResultSettings = new ProductWebsiteSettings(
                enabled,
                DomainsBox.Text.Trim(),
                httpPort,
                ssl,
                httpsPort,
                CertificateBox.Text.Trim(),
                CertificateKeyBox.Text.Trim(),
                ssl && RedirectBox.IsChecked == true,
                bandwidth);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "产品域名与 SSL", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        if (!_hadExistingConfiguration || MessageBox.Show(
                "确定移除当前产品的独立域名和 SSL 配置吗？产品原有的本地访问地址不受影响。",
                "产品域名与 SSL", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        ResultSettings = ProductWebsiteSettings.Default;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ParsePort(string text, string label)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port) ||
            port is <= 0 or > 65535)
        {
            throw new InvalidOperationException($"{label} 端口必须是 1 到 65535 之间的整数。");
        }

        return port;
    }

}

