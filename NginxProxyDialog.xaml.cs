using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class NginxProxyDialog : PanelModalWindow
{
    private readonly EnvironmentRuntimeService _runtimeService;
    private readonly string _loadedRevision;
    private readonly ObservableCollection<NginxProxyRuleRow> _rules = [];
    private readonly List<NginxProxyRule> _suppressedRules = [];

    public NginxProxyDialog(EnvironmentRuntimeService runtimeService)
    {
        InitializeComponent();
        _runtimeService = runtimeService;

        var options = NginxRuntimeManager.NormalizeOptions(runtimeService.GetNginxOptions());
        _loadedRevision = NginxConfigurationCoordinator.Revision(options);
        foreach (var rule in options.Rules.Select(NginxRuntimeManager.NormalizeRule))
        {
            if (NginxProductProxyService.IsSuppressedManagedProductId(rule.ManagedProductId))
            {
                _suppressedRules.Add(rule);
                continue;
            }

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
        PanelThemeService.Apply(dark, Resources);
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

        if (NginxProductProxyService.TryParseManagedProductId(
                selected.ManagedProductId,
                out var productId,
                out _))
        {
            var tombstone = selected.ToRule();
            tombstone.Enabled = false;
            tombstone.ManagedProductId = NginxProductProxyService.CreateSuppressedId(productId);
            tombstone = NginxRuntimeManager.NormalizeRule(tombstone);
            _suppressedRules.RemoveAll(rule =>
                NginxProductProxyService.TryParseManagedProductId(rule.ManagedProductId, out var existingId, out _) &&
                string.Equals(existingId, productId, StringComparison.OrdinalIgnoreCase));
            _suppressedRules.Add(tombstone);
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
            var visibleProductIds = rules
                .Select(rule => NginxProductProxyService.TryParseManagedProductId(
                    rule.ManagedProductId,
                    out var productId,
                    out _)
                    ? productId
                    : null)
                .Where(productId => !string.IsNullOrWhiteSpace(productId))
                .Cast<string>()
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            rules.AddRange(_suppressedRules
                .Where(rule => !NginxProductProxyService.TryParseManagedProductId(
                                   rule.ManagedProductId,
                                   out var productId,
                                   out _) ||
                               !visibleProductIds.Contains(productId))
                .Select(NginxRuntimeManager.NormalizeRule));

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
            ResultMessage = await _runtimeService.SaveNginxOptionsAsync(options, expectedRevision: _loadedRevision);
            var synced = await NginxProductProxyService.TrySyncAsync();
            if (!synced)
            {
                ResultMessage += " 自动关联产品规则未完全同步，请重新打开本窗口确认配置。";
            }
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

    public sealed class NginxProxyRuleRow
    {
        private NginxProxyRule? _originalRule;

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
            var row = new NginxProxyRuleRow
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
            row._originalRule = normalized;
            return row;
        }

        public NginxProxyRule ToRule()
        {
            if (!int.TryParse(ListenPort.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var port))
            {
                throw new InvalidOperationException($"规则“{Name}”的监听端口必须是数字。");
            }

            var normalized = NginxRuntimeManager.NormalizeRule(new NginxProxyRule
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

            if (_originalRule is not null &&
                NginxProductProxyService.TryParseManagedProductId(
                    _originalRule.ManagedProductId,
                    out var productId,
                    out var ownership) &&
                ownership == NginxProductProxyService.ManagedProductRuleKind.Automatic &&
                !HasSameEditableConfiguration(normalized, _originalRule))
            {
                normalized.ManagedProductId = NginxProductProxyService.CreateUserOverrideId(productId);
            }

            return normalized;
        }

        private static bool HasSameEditableConfiguration(NginxProxyRule left, NginxProxyRule right)
        {
            var a = NginxRuntimeManager.NormalizeRule(left);
            var b = NginxRuntimeManager.NormalizeRule(right);
            return a.Enabled == b.Enabled &&
                   a.ListenPort == b.ListenPort &&
                   a.WebSocket == b.WebSocket &&
                   string.Equals(a.Name, b.Name, StringComparison.Ordinal) &&
                   string.Equals(a.ServerName, b.ServerName, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.LocationPath, b.LocationPath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.ProxyTarget, b.ProxyTarget, StringComparison.OrdinalIgnoreCase) &&
                   a.SslEnabled == b.SslEnabled &&
                   a.HttpsPort == b.HttpsPort &&
                   string.Equals(a.SslCertificatePath, b.SslCertificatePath, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(a.SslCertificateKeyPath, b.SslCertificateKeyPath, StringComparison.OrdinalIgnoreCase) &&
                   a.RedirectHttpToHttps == b.RedirectHttpToHttps &&
                   a.MaxRateKbps == b.MaxRateKbps;
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
