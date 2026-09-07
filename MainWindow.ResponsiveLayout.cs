using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class MainWindow
{
    private const double ResponsiveLayoutEpsilon = 0.5d;

    protected override void OnContentRendered(EventArgs e)
    {
        ApplyResponsiveLayout();
        base.OnContentRendered(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyResponsiveLayout();
    }

    private void ApplyResponsiveLayout()
    {
        if (MainViewbox is null || DesignSurface is null)
        {
            return;
        }

        // The old fixed 1280 x 820 surface was centered inside a Uniform Viewbox.
        // Any window aspect ratio that differed from 1280:820 therefore produced
        // letterbox bands. Keep uniform scaling for small displays, but make the
        // logical design surface follow the viewport aspect ratio so the real WPF
        // layout receives the extra width/height and can reflow instead of leaving
        // unused background around the edges.
        MainViewbox.Stretch = Stretch.Uniform;
        MainViewbox.StretchDirection = StretchDirection.DownOnly;
        MainViewbox.HorizontalAlignment = HorizontalAlignment.Stretch;
        MainViewbox.VerticalAlignment = VerticalAlignment.Stretch;

        var viewportWidth = Math.Max(1d, ActualWidth);
        var viewportHeight = Math.Max(1d, ActualHeight);
        var scale = Math.Min(
            1d,
            Math.Min(
                viewportWidth / ResponsiveWindowSizing.MainDesignWidth,
                viewportHeight / ResponsiveWindowSizing.MainDesignHeight));

        if (double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0d)
        {
            return;
        }

        // At the minimum supported window size this preserves the current compact
        // visual density. On larger windows scale stays at 1 and the design surface
        // itself expands, which makes the content genuinely responsive rather than
        // scaling a fixed screenshot-like canvas.
        var targetWidth = Math.Max(
            ResponsiveWindowSizing.MainDesignWidth,
            viewportWidth / scale);
        var targetHeight = Math.Max(
            ResponsiveWindowSizing.MainDesignHeight,
            viewportHeight / scale);

        if (double.IsNaN(DesignSurface.Width) ||
            Math.Abs(DesignSurface.Width - targetWidth) > ResponsiveLayoutEpsilon)
        {
            DesignSurface.Width = targetWidth;
        }

        if (double.IsNaN(DesignSurface.Height) ||
            Math.Abs(DesignSurface.Height - targetHeight) > ResponsiveLayoutEpsilon)
        {
            DesignSurface.Height = targetHeight;
        }
    }
}
