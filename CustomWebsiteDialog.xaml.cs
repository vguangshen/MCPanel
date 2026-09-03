using Microsoft.Win32;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class CustomWebsiteDialog : Window
{
    private readonly CustomWebsiteDefinition _original;

    public CustomWebsiteDialog(CustomWebsiteDefinition? definition = null)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ResponsiveWindowSizing.FitToCurrentMonitor(this, 780, 750, 0.95, 1.1);
        _original = definition ?? new CustomWebsiteDefinition
        {
            Name = string.Empty,
            PhysicalPath = Path.Combine(AppContext.BaseDirectory, "web", "sites", "website")
        };
        TitleText.Text = definition is null ? "新建 IIS 网站" : $"编辑 IIS 网站：{definition.Name}";
        NameBox.Text = _original.Name;
        NameBox.IsReadOnly = definition is not null;
        PhysicalPathBox.Text = _original.PhysicalPath;
        HttpPortBox.Text = _original.HttpPort.ToString(CultureInfo.InvariantCulture);
        DomainsBox.Text = string.Join(Environment.NewLine, _original.Domains);
        RuntimeBox.SelectedValue = _original.ManagedRuntimeVersion;
        if (RuntimeBox.SelectedIndex < 0) RuntimeBox.SelectedIndex = 0;
        Enable32BitBox.IsChecked = _original.Enable32Bit;
        SslBox.IsChecked = _original.SslEnabled;
        HttpsPortBox.Text = _original.HttpsPort.ToString(CultureInfo.InvariantCulture);
        RedirectBox.IsChecked = _original.RedirectHttpToHttps;
        BandwidthBox.Text = _original.MaxBandwidthKbps.ToString(CultureInfo.InvariantCulture);
        MimeBox.Text = CustomWebsiteService.FormatMimeMappings(_original.MimeMappings);
        UpdateSslState();
    }

    public CustomWebsiteDefinition? ResultDefinition { get; private set; }
    public string CertificatePath { get; private set; } = string.Empty;
    public string CertificatePassword { get; private set; } = string.Empty;

    public void ApplyTheme(bool dark)
    {
        PanelThemeService.Apply(dark, Resources);
    }

    private void BrowseRoot_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "选择网站根目录",
            ShowNewFolderButton = true,
            SelectedPath = Directory.Exists(PhysicalPathBox.Text) ? PhysicalPathBox.Text : AppContext.BaseDirectory
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK) PhysicalPathBox.Text = dialog.SelectedPath;
    }

    private void BrowsePfx_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "PFX/P12 证书 (*.pfx;*.p12)|*.pfx;*.p12|所有文件 (*.*)|*.*",
            CheckFileExists = true
        };
        if (dialog.ShowDialog(this) == true) PfxPathBox.Text = dialog.FileName;
    }

    private void SslOptionChanged(object sender, RoutedEventArgs e) => UpdateSslState();

    private void UpdateSslState()
    {
        if (!IsInitialized) return;
        var enabled = SslBox.IsChecked == true;
        PfxPathBox.IsEnabled = enabled;
        PfxPasswordBox.IsEnabled = enabled;
        HttpsPortBox.IsEnabled = enabled;
        RedirectBox.IsEnabled = enabled;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var httpPort = ParseNumber(HttpPortBox.Text, "HTTP 端口", 1, 65535);
            var httpsPort = ParseNumber(HttpsPortBox.Text, "HTTPS 端口", 1, 65535);
            var bandwidth = ParseNumber(BandwidthBox.Text, "带宽上限", 0, 1048576);
            var domains = CustomWebsiteService.NormalizeDomains(new[] { DomainsBox.Text });
            var runtime = (RuntimeBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "v4.0";
            var definition = new CustomWebsiteDefinition
            {
                Id = _original.Id,
                Name = NameBox.Text.Trim(),
                PhysicalPath = PhysicalPathBox.Text.Trim(),
                HttpPort = httpPort,
                Domains = domains,
                ManagedRuntimeVersion = runtime,
                Enable32Bit = Enable32BitBox.IsChecked == true,
                SslEnabled = SslBox.IsChecked == true,
                HttpsPort = httpsPort,
                CertificateThumbprint = _original.CertificateThumbprint,
                RedirectHttpToHttps = SslBox.IsChecked == true && RedirectBox.IsChecked == true,
                MaxBandwidthKbps = bandwidth,
                MimeMappings = CustomWebsiteService.ParseMimeMappings(MimeBox.Text)
            };
            CertificatePath = PfxPathBox.Text.Trim();
            CertificatePassword = PfxPasswordBox.Password;
            CustomWebsiteService.Validate(definition, CertificatePath);
            ResultDefinition = definition;
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "IIS 网站配置", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private static int ParseNumber(string text, string label, int minimum, int maximum)
    {
        if (!int.TryParse(text.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < minimum || value > maximum)
            throw new InvalidOperationException($"{label}必须是 {minimum} 到 {maximum} 之间的整数。");
        return value;
    }

}
