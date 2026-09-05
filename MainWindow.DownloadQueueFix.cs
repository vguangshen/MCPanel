using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
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

        // MainWindow.xaml historically closed the queue popup on every preview
        // click that was not over the toolbar button. A WPF Popup lives in its
        // own presentation source, but mouse input from its child controls can
        // still participate in the owner's input path. Replace that broad
        // handler with one that explicitly treats the popup surface as inside.
        PreviewMouseLeftButtonDown -= MainWindow_PreviewMouseLeftButtonDown;
        PreviewMouseLeftButtonDown += DownloadQueueSafePreviewMouseLeftButtonDown;

        Loaded += (_, _) => NormalizeDownloadQueueToolbarVisuals();
        ProductsHeader.SizeChanged += (_, _) => NormalizeDownloadQueueToolbarVisuals();
    }

    private void DownloadQueueSafePreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        var popupHovered = DownloadQueuePopup.Child is UIElement popupChild && popupChild.IsMouseOver;
        if (ShouldCloseDownloadQueuePopup(
                DownloadQueuePopup.IsOpen,
                DownloadQueueButton.IsMouseOver,
                popupHovered))
        {
            DownloadQueuePopup.IsOpen = false;
        }
    }

    internal static bool ShouldCloseDownloadQueuePopup(
        bool isOpen,
        bool toolbarButtonHovered,
        bool popupHovered) =>
        isOpen && !toolbarButtonHovered && !popupHovered;

    private void NormalizeDownloadQueueToolbarVisuals()
    {
        if (DownloadQueueButton is null)
        {
            return;
        }

        // ProductToolbarIconButton intentionally uses Segoe MDL2 Assets for
        // icon-only buttons. The active-queue badge is a nested TextBlock and
        // previously inherited that icon font, so a numeric count such as "1"
        // could render as an unrelated symbol. The download glyph itself has an
        // explicit MDL2 font in XAML, so making the button's inherited font a
        // normal UI font fixes only the badge text.
        DownloadQueueButton.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        if (VisualTreeHelper.GetParent(DownloadQueueButton) is not Grid host)
        {
            return;
        }

        // The XAML host was 40 px high while its progress ring was 48 px. That
        // constrained/clipped the ring and left almost no visual separation from
        // the 40 px button. Give the ring its own breathing room without changing
        // the toolbar button's hit target or the progress calculation.
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
