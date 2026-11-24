using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class MainWindow
{
    private const double IconOnlyNavigationBreakpoint = 1100d;
    private const double IconOnlyNavigationWidth = 72d;
    private const double ShortViewportBreakpoint = 760d;
    private const double NarrowHomeContentBreakpoint = 620d;

    private readonly Dictionary<RadioButton, object?> _navigationLabels = new();

    /// <summary>
    /// Derives the density flags and column counts from the window size. The
    /// pages and templates react to <see cref="ResponsiveDensity"/> in XAML.
    /// </summary>
    private void ApplyResponsivePageDensity(double width, double height)
    {
        var iconOnlyNavigation = width < IconOnlyNavigationBreakpoint;
        var compactPageDensity = width < CompactShellBreakpoint || height < ShortViewportBreakpoint;

        ApplyIconOnlyNavigation(iconOnlyNavigation);
        var contentWidth = width - (iconOnlyNavigation ? IconOnlyNavigationWidth : width < CompactShellBreakpoint ? 190d : 226d) - (width < CompactShellBreakpoint ? 32d : 48d);
        _model.SetProductColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 580d ? 2 : 1);
        _model.SetEnvironmentColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 560d ? 2 : 1);
        _model.SetHomeServiceColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 620d ? 2 : 1);

        ResponsiveDensity.SetIsCompact(this, compactPageDensity);
        ResponsiveDensity.SetIsNarrow(this, contentWidth < NarrowHomeContentBreakpoint);

        AccountApiPageControl.MinHeight = 0d;
        AccountApiPageControl.VerticalAlignment = VerticalAlignment.Stretch;
        AccountApiPageControl.HorizontalAlignment = HorizontalAlignment.Stretch;
        AiAnalysisPageControl.MinHeight = 0d;
        AiAnalysisPageControl.VerticalAlignment = VerticalAlignment.Stretch;
        AiAnalysisPageControl.HorizontalAlignment = HorizontalAlignment.Stretch;
    }

    private void ApplyIconOnlyNavigation(bool iconOnly)
    {
        if (NavRail.Parent is DockPanel navDock &&
            navDock.Parent is Border navBorder &&
            navBorder.Parent is Grid shellGrid &&
            shellGrid.ColumnDefinitions.Count >= 2 &&
            iconOnly)
        {
            shellGrid.ColumnDefinitions[0].Width = new GridLength(IconOnlyNavigationWidth);
        }

        foreach (var button in NavItemsPanel.Children.OfType<RadioButton>())
        {
            if (!_navigationLabels.TryGetValue(button, out var originalContent))
            {
                originalContent = button.Content;
                _navigationLabels[button] = originalContent;
            }

            var label = originalContent?.ToString() ?? string.Empty;
            AutomationProperties.SetName(button, label);

            if (iconOnly)
            {
                button.Content = string.Empty;
                button.ToolTip = label;
                button.Width = 56d;
                button.HorizontalAlignment = HorizontalAlignment.Center;
                button.Margin = new Thickness(8d, 3d, 8d, 3d);
                button.Padding = new Thickness(11d, 0d, 11d, 0d);
            }
            else
            {
                button.Content = originalContent;
                button.ToolTip = null;
                button.Width = double.NaN;
                button.HorizontalAlignment = HorizontalAlignment.Stretch;
                button.Margin = new Thickness(10d, 3d, 10d, 3d);
                button.Padding = new Thickness(14d, 0d, 14d, 0d);
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                yield return typed;
            }

            foreach (var nested in FindVisualChildren<T>(child))
            {
                yield return nested;
            }
        }
    }
}
