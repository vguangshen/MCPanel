using System;
using System.Windows;

namespace MCPanel;

public partial class MainWindow
{
    private double _uiScale = 1d;

    /// <summary>
    /// The interface scale: the whole design surface (text, buttons, cards,
    /// spacing) is drawn this much larger. Responsive breakpoints work on
    /// the logical size, the window size divided by this scale, so the
    /// layout chosen for a window is the same on every monitor.
    /// </summary>
    internal double UiScale => _uiScale;

    /// <summary>Applies an interface scale and re-runs the responsive layout.</summary>
    internal void ApplyUiScale(double scale)
    {
        scale = Math.Max(1d, Math.Min(ResponsiveWindowSizing.MaximumUiScale, scale));
        if (Math.Abs(scale - _uiScale) < 0.001d && DesignSurface.LayoutTransform == PanelUiScale.Transform)
        {
            return;
        }

        _uiScale = scale;
        var transform = PanelUiScale.Set(scale);
        DesignSurface.LayoutTransform = transform;
        Resources[PanelUiScale.TransformResourceKey] = transform;
        ApplyResponsiveLayout();
        StabilizeSoftwareUpdateCardHeight();
        AlignSettingsCardBottoms();
    }

    /// <summary>
    /// Moving the window to a monitor of another size changes the scale;
    /// the window keeps its size, its content adapts.
    /// </summary>
    private void UpdateUiScaleForMonitor()
    {
        if (_monitorAwareScaling)
        {
            ApplyUiScale(ResponsiveWindowSizing.UiScaleForCurrentMonitor(this));
        }
    }
}
