using System.IO;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class MainWindow
{
    private static readonly HttpClient WebsiteCheckClient = new(new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    private readonly HashSet<string> _websiteChecks = new(StringComparer.OrdinalIgnoreCase);
    private void AiSettings_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new AiSettingsDialog { Owner = this };
        PanelThemeService.Apply(_isDarkThemeActive, dialog.Resources);
        if (dialog.ShowDialog() == true) _model.SettingsStatus = "AI 模型设置已保存，下次分析立即生效。";
    }

    private async void RefreshWebsiteStates_Click(object sender, RoutedEventArgs e)
        => await _model.RefreshWebsiteStatesAsync(true, false, force: true);

    private async void RestorePlatformRecords_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Task.Run(() => new ManualPlatformStore().RestoreBackup());
            await _model.RefreshInstalledProductsAsync();
            _model.WebsiteOperationText = "已恢复最近有效的平台记录备份。";
        }
        catch (Exception ex) { MessageBox.Show("无法恢复平台记录：" + ex.Message, "平台记录", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal async void EditLocalPlatform_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledProductItem item || !item.Product.IsExternalPlatform) return;
        try
        {
            var existing = await Task.Run(() => new ManualPlatformStore().Load().FirstOrDefault(record => record.Id == item.ProductId));
            if (existing is not null) await BindPlatformAsync(existing.Java, existing);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "编辑平台", MessageBoxButton.OK, MessageBoxImage.Warning); }
    }

    internal async void PinWebsite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is WebsiteRow row) await _model.ToggleWebsitePinAsync(row);
    }

    internal void ExpandWebsite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not WebsiteRow row) return;
        row.IsExpanded = !row.IsExpanded;
        if (row.Item is InstalledProductItem product) product.IsManagementExpanded = row.IsExpanded;
    }

    internal async void ProbeWebsite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not InstalledProductItem item || string.IsNullOrWhiteSpace(item.Url)) return;
        if (_websiteChecks.Count >= 2 || !_websiteChecks.Add(item.ProductId)) return;
        var button = sender as Button;
        if (button is not null) button.IsEnabled = false;
        _model.WebsiteOperationText = "正在检测访问：" + item.DisplayName;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
            using var response = await WebsiteCheckClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            var code = (int)response.StatusCode;
            _model.WebsiteOperationText = $"{item.DisplayName} · HTTP {code} · " +
                (code < 400 ? "网页可访问" : code is 401 or 403 ? "服务已响应，需要登录或访问权限" : "应用返回错误，请查看日志");
        }
        catch (Exception ex) { _model.WebsiteOperationText = item.DisplayName + " · 访问失败：" + ex.GetBaseException().Message; }
        finally { _websiteChecks.Remove(item.ProductId); if (button is not null) button.IsEnabled = true; }
    }
}
