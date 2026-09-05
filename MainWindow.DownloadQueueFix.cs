using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class MainWindow
{
    internal const double DownloadQueueToolbarHostHeight = 52d;
    internal const double DownloadQueueProgressRingSize = 50d;
    internal const double DownloadQueueProgressRingStroke = 2d;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => NormalizeDownloadQueueToolbarVisuals();
    }

    internal static bool ShouldCloseDownloadQueueFlyout(
        bool isOpen,
        bool toolbarButtonHovered,
        bool flyoutHovered) =>
        isOpen && !toolbarButtonHovered && !flyoutHovered;

    private void NormalizeDownloadQueueToolbarVisuals()
    {
        if (DownloadQueueButton is null)
        {
            return;
        }

        // ProductToolbarIconButton intentionally uses Segoe MDL2 Assets for
        // icon-only buttons. Keep the nested numeric badge on the normal UI font.
        DownloadQueueButton.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        if (VisualTreeHelper.GetParent(DownloadQueueButton) is not Grid host)
        {
            return;
        }

        host.Height = DownloadQueueToolbarHostHeight;
        host.ClipToBounds = false;

        foreach (UIElement child in host.Children)
        {
            if (child is not CircularProgress ring)
            {
                continue;
            }

            ring.Width = DownloadQueueProgressRingSize;
            ring.Height = DownloadQueueProgressRingSize;
            ring.StrokeThickness = DownloadQueueProgressRingStroke;
            ring.SnapsToDevicePixels = true;
            ring.UseLayoutRounding = true;
        }
    }
}
