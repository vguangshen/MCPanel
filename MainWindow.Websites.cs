using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace MCPanel;

public partial class MainWindow
{

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
}
