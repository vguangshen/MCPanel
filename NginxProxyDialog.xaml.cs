using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class NginxProxyDialog : Window
{
    private readonly EnvironmentRuntimeService _runtimeService;
    private readonly ObservableCollection<NginxProxyRuleRow> _rules = [];

    public NginxProxyDialog(EnvironmentRuntimeService runtimeService)
    {
        InitializeComponent();
        _runtimeService = runtimeService;
        SourceInitialized += (_, _) => ResponsiveWindowSizing.FitToCurrentMonitor(this, 980, 640, 0.92, 1.2);

        var options = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        foreach (var rule in options.Rules)
        {
            _rules.Add(NginxProxyRuleRow.FromRule(rule));
        }

        if (_rules.Count == 0)
        {
            _rules.Add(NginxProxyRuleRow.FromRule(NginxRuntimeManager.CreateDefaultRule()));
        }

        RulesGrid.ItemsSource = _rules;
        RulesGrid.SelectedIndex = 0;
        UpdateRuleCount();
        StatusText.Text = "保存后会自动校验 nginx.conf；如果 Nginx 正在运行，会立即热重载生效。";
    }

    public string ResultMessage { get; private set; } = string.Empty;

    public void ApplyTheme(bool dark)
    {
        Background = Brush(dark ? "#101419" : "#F5F7FB");
        SetBrush("PageBrush", dark ? "#101419" : "#F5F7FB");
        SetBrush("PrimaryBrush", dark ? "#4C8DFF" : "#1A73E8");
        SetBrush("PrimaryDarkBrush", dark ? "#6EA3FF" : "#1765CC");
        SetBrush("SurfaceBrush", dark ? "#1B2027" : "#FFFFFF");
        SetBrush("SurfaceAltBrush", dark ? "#222A33" : "#F8FAFD");
        SetBrush("TextBrush", dark ? "#F8FAFC" : "#202124");
        SetBrush("MutedBrush", dark ? "#D0D6DD" : "#5F6368");
        SetBrush("LineBrush", dark ? "#4B5561" : "#DADCE0");
        SetBrush("InputBrush", dark ? "#151A20" : "#FFFFFF");
        SetBrush("TonalBrush", dark ? "#243D63" : "#E8F0FE");
        SetBrush("TonalTextBrush", dark ? "#D7E3FF" : "#174EA6");
        SetBrush("DangerBrush", dark ? "#F28B82" : "#D93025");
    }

    private void AddRule_Click(object sender, RoutedEventArgs e)
    {
        var port = _rules.Count == 0
            ? NginxRuntimeManager.DefaultListenPort
            : _rules.Select(rule => rule.TryGetPort()).Where(port => port > 0).DefaultIfEmpty(NginxRuntimeManager.DefaultListenPort).Max() + 1;
        var row = NginxProxyRuleRow.FromRule(NginxRuntimeManager.CreateDefaultRule(port));
        row.Name = $"本地服务 {_rules.Count + 1}";
        _rules.Add(row);
        RulesGrid.SelectedItem = row;
        UpdateRuleCount();
    }

    private void DuplicateRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not NginxProxyRuleRow selected)
        {
            StatusText.Text = "请先选择一条规则。";
            return;
        }

        var copy = selected.Clone();
        copy.Name = $"{selected.Name} 副本";
        copy.ListenPort = (selected.TryGetPort() + 1).ToString(CultureInfo.InvariantCulture);
        copy.ManagedProductId = null;
        copy.ManagedWebsiteId = null;
        _rules.Add(copy);
        RulesGrid.SelectedItem = copy;
        UpdateRuleCount();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesGrid.SelectedItem is not NginxProxyRuleRow selected)
        {
            StatusText.Text = "请先选择一条规则。";
            return;
        }

        if (_rules.Count == 1)
        {
            StatusText.Text = "至少需要保留一条规则。";
            return;
        }

        var index = _rules.IndexOf(selected);
        _rules.Remove(selected);
        RulesGrid.SelectedIndex = Math.Min(index, _rules.Count - 1);
        UpdateRuleCount();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            RulesGrid.CommitEdit(DataGridEditingUnit.Cell, true);
            RulesGrid.CommitEdit(DataGridEditingUnit.Row, true);

            var rules = _rules.Select(row => row.ToRule()).ToList();
            var firstEnabled = rules.FirstOrDefault(rule => rule.Enabled) ?? rules.First();
            var options = new NginxRuntimeOptions
            {
                ListenPort = firstEnabled.ListenPort,
                ProxyEnabled = rules.Any(rule => rule.Enabled),
                ProxyTarget = firstEnabled.ProxyTarget,
                Rules = rules
            };

            NginxRuntimeManager.ValidateOptions(options);
            IsEnabled = false;
            StatusText.Text = "正在保存并校验 Nginx 配置...";
            ResultMessage = await _runtimeService.SaveNginxOptionsAsync(options);
            await NginxProductProxyService.TrySyncAsync();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            IsEnabled = true;
            StatusText.Text = ex.Message;
            MessageBox.Show(ex.Message, "Nginx 反向代理管理", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void UpdateRuleCount()
    {
        RuleCountText.Text = $"{_rules.Count} 条规则";
    }

    private void SetBrush(string key, string hex)
    {
        var color = (Color)ColorConverter.ConvertFromString(hex);
        if (Resources[key] is SolidColorBrush brush && !brush.IsFrozen && !brush.IsSealed)
        {
            brush.Color = color;
        }
        else
        {
            Resources[key] = new SolidColorBrush(color);
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
    }

    public sealed class NginxProxyRuleRow
    {
        public bool Enabled { get; set; }
        public string Name { get; set; } = string.Empty;
        public string ListenPort { get; set; } = string.Empty;
        public string ServerName { get; set; } = string.Empty;
        public string LocationPath { get; set; } = string.Empty;
        public string ProxyTarget { get; set; } = string.Empty;
        public bool WebSocket { get; set; }
        public string? ManagedProductId { get; set; }
        public string? ManagedWebsiteId { get; set; }
        public bool SslEnabled { get; set; }
        public int HttpsPort { get; set; }
        public string SslCertificatePath { get; set; } = string.Empty;
        public string SslCertificateKeyPath { get; set; } = string.Empty;
        public bool RedirectHttpToHttps { get; set; }
        public int MaxRateKbps { get; set; }

        public static NginxProxyRuleRow FromRule(NginxProxyRule rule)
        {
            var normalized = NginxRuntimeManager.NormalizeRule(rule);
            return new NginxProxyRuleRow
            {
                Enabled = normalized.Enabled,
                Name = normalized.Name,
                ListenPort = normalized.ListenPort.ToString(CultureInfo.InvariantCulture),
                ServerName = normalized.ServerName,
                LocationPath = normalized.LocationPath,
                ProxyTarget = normalized.ProxyTarget,
                WebSocket = normalized.WebSocket,
                ManagedProductId = normalized.ManagedProductId,
                ManagedWebsiteId = normalized.ManagedWebsiteId,
                SslEnabled = normalized.SslEnabled,
                HttpsPort = normalized.HttpsPort,
                SslCertificatePath = normalized.SslCertificatePath,
                SslCertificateKeyPath = normalized.SslCertificateKeyPath,
                RedirectHttpToHttps = normalized.RedirectHttpToHttps,
                MaxRateKbps = normalized.MaxRateKbps
            };
        }

        public NginxProxyRule ToRule()
        {
            if (!int.TryParse(ListenPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                throw new InvalidOperationException($"规则“{Name}”的监听端口必须是数字。");
            }

            return NginxRuntimeManager.NormalizeRule(new NginxProxyRule
            {
                Enabled = Enabled,
                Name = Name,
                ListenPort = port,
                ServerName = ServerName,
                LocationPath = LocationPath,
                ProxyTarget = ProxyTarget,
                WebSocket = WebSocket,
                ManagedProductId = ManagedProductId,
                ManagedWebsiteId = ManagedWebsiteId,
                SslEnabled = SslEnabled,
                HttpsPort = HttpsPort,
                SslCertificatePath = SslCertificatePath,
                SslCertificateKeyPath = SslCertificateKeyPath,
                RedirectHttpToHttps = RedirectHttpToHttps,
                MaxRateKbps = MaxRateKbps
            });
        }

        public int TryGetPort()
        {
            return int.TryParse(ListenPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port)
                ? port
                : NginxRuntimeManager.DefaultListenPort;
        }

        public NginxProxyRuleRow Clone()
        {
            return new NginxProxyRuleRow
            {
                Enabled = Enabled,
                Name = Name,
                ListenPort = ListenPort,
                ServerName = ServerName,
                LocationPath = LocationPath,
                ProxyTarget = ProxyTarget,
                WebSocket = WebSocket,
                ManagedProductId = ManagedProductId,
                ManagedWebsiteId = ManagedWebsiteId,
                SslEnabled = SslEnabled,
                HttpsPort = HttpsPort,
                SslCertificatePath = SslCertificatePath,
                SslCertificateKeyPath = SslCertificateKeyPath,
                RedirectHttpToHttps = RedirectHttpToHttps,
                MaxRateKbps = MaxRateKbps
            };
        }
    }
}
