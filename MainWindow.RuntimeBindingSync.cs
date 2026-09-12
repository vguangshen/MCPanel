namespace MCPanel;

public partial class MainWindow
{
    private async Task RefreshProductRuntimeBindingsAsync(bool force)
    {
        if (_productRouteSyncInFlight || _isClosed)
        {
            return;
        }

        _productRouteSyncInFlight = true;
        try
        {
            var changed = await Task.Run(() =>
                ProductDeploymentService.ReconcileRuntimeDeploymentState(force));

            // NginxProductProxyService is change-aware: identical effective
            // configuration returns without writing or reloading Nginx.
            await NginxProductProxyService.TrySyncAsync();

            if (_isClosed || changed.Count == 0)
            {
                return;
            }

            foreach (var item in _model.InstalledProducts)
            {
                item.RefreshRuntime();
            }
            _model.RefreshWebsiteFilterForRuntimeChange();
        }
        catch (Exception ex)
        {
            WriteRuntimeRefreshError(ex);
        }
        finally
        {
            var cadence = SitesPage.IsVisible && IsVisible && WindowState != System.Windows.WindowState.Minimized
                ? ProductRouteForegroundSyncInterval
                : ProductRouteBackgroundSyncInterval;
            _nextProductRouteSyncUtc = DateTime.UtcNow + cadence;
            _productRouteSyncInFlight = false;
        }
    }
}