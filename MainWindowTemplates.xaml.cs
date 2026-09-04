using System.Linq;
using System.Windows;
using System.Windows.Input;

namespace MCPanel;

public partial class MainWindowTemplates : ResourceDictionary
{
    public MainWindowTemplates()
    {
        InitializeComponent();
    }

    private static MainWindow? GetOwner(object sender)
    {
        if (sender is not DependencyObject element)
        {
            return null;
        }

        // Normal DataTemplate content is attached to the main window, but a
        // Popup is hosted in a separate PopupRoot window. Window.GetWindow()
        // therefore returns null for queue-row buttons in the download popup.
        // Fall back to the visible MCPanel window so popup actions still reach
        // the queue service.
        if (Window.GetWindow(element) is MainWindow owner)
        {
            return owner;
        }

        return Application.Current?.Windows
            .OfType<MainWindow>()
            .Where(window => window.IsVisible || window.DownloadQueuePopup.IsOpen)
            .OrderByDescending(window => window.IsActive)
            .FirstOrDefault();
    }

    private void ProductInstall_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ProductInstall_Click(sender, e);

    private void ProductCategory_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ProductCategory_Click(sender, e);

    private void ServiceAction_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ServiceAction_Click(sender, e);

    private void ToggleQueuedProductPause_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ToggleQueuedProductPause_Click(sender, e);

    private void RemoveQueuedProduct_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.RemoveQueuedProduct_Click(sender, e);

    private void EnvironmentOpenDirectory_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.EnvironmentOpenDirectory_Click(sender, e);

    private void EnvironmentCardFlip_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.EnvironmentCardFlip_Click(sender, e);

    private void EnvironmentInstall_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.EnvironmentInstall_Click(sender, e);

    private void EnvironmentRuntime_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.EnvironmentRuntime_Click(sender, e);

    private void ConnectMySql_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ConnectMySql_Click(sender, e);

    private void ConnectSqlServer_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ConnectSqlServer_Click(sender, e);

    private void NginxOpenManagement_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.NginxOpenManagement_Click(sender, e);

    private void FrpOpenManagement_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.FrpOpenManagement_Click(sender, e);

    private void CredentialEdit_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CredentialEdit_Click(sender, e);

    private void CredentialCopy_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CredentialCopy_Click(sender, e);

    private void CredentialConnect_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CredentialConnect_Click(sender, e);

    private void TomcatPortSave_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.TomcatPortSave_Click(sender, e);

    private void DatabaseToolPath_Click(object sender, MouseButtonEventArgs e) =>
        GetOwner(sender)?.DatabaseToolPath_Click(sender, e);

    private void DatabaseToolAction_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.DatabaseToolAction_Click(sender, e);

    private void ProductUninstall_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.ProductUninstall_Click(sender, e);

    private void InstalledProductUrl_Click(object sender, MouseButtonEventArgs e) =>
        GetOwner(sender)?.InstalledProductUrl_Click(sender, e);

    private void InstalledProductOpen_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductOpen_Click(sender, e);

    private void InstalledProductManageToggle_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductManageToggle_Click(sender, e);

    private void InstalledProductTomcatAction_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductTomcatAction_Click(sender, e);

    private void InstalledProductIis_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductIis_Click(sender, e);

    private void InstalledProductRepair_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductRepair_Click(sender, e);

    private void InstalledProductDomain_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductDomain_Click(sender, e);

    private void InstalledProductUninstall_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.InstalledProductUninstall_Click(sender, e);

    private void CustomWebsiteBrowse_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CustomWebsiteBrowse_Click(sender, e);

    private void CustomWebsiteOpenRoot_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CustomWebsiteOpenRoot_Click(sender, e);

    private void CustomWebsiteEdit_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CustomWebsiteEdit_Click(sender, e);

    private void CustomWebsiteDelete_Click(object sender, RoutedEventArgs e) =>
        GetOwner(sender)?.CustomWebsiteDelete_Click(sender, e);
}
