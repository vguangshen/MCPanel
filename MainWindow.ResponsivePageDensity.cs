using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MCPanel;

public partial class MainWindow
{
    private const double IconOnlyNavigationBreakpoint = 1100d;
    private const double IconOnlyNavigationWidth = 72d;
    private const double ShortViewportBreakpoint = 760d;

    private readonly Dictionary<RadioButton, object?> _navigationLabels = new();
    private bool _responsiveDensityHooksAttached;
    private bool _applyingResponsivePageDensity;
    private double _lastResponsiveWidth = ResponsiveWindowSizing.MainDesignWidth;
    private double _lastResponsiveHeight = ResponsiveWindowSizing.MainDesignHeight;

    private void EnsureResponsiveDensityHooks()
    {
        if (_responsiveDensityHooksAttached)
        {
            return;
        }

        _responsiveDensityHooksAttached = true;
        EnvironmentPage.IsVisibleChanged += (_, _) => QueueResponsiveDensityRefresh();
        HomePage.IsVisibleChanged += (_, _) => QueueResponsiveDensityRefresh();
    }

    private void QueueResponsiveDensityRefresh()
    {
        if (Dispatcher.HasShutdownStarted)
        {
            return;
        }

        Dispatcher.BeginInvoke(
            new Action(() => ApplyResponsivePageDensity(_lastResponsiveWidth, _lastResponsiveHeight)),
            DispatcherPriority.Loaded);
    }

    private void ApplyResponsivePageDensity(double width, double height)
    {
        if (_applyingResponsivePageDensity)
        {
            return;
        }

        _lastResponsiveWidth = width;
        _lastResponsiveHeight = height;
        _applyingResponsivePageDensity = true;
        try
        {
            var iconOnlyNavigation = width < IconOnlyNavigationBreakpoint;
            var compactPageDensity = width < CompactShellBreakpoint || height < ShortViewportBreakpoint;

            ApplyIconOnlyNavigation(iconOnlyNavigation);
            ApplyHomePageDensity(compactPageDensity);
            ApplyEnvironmentPageDensity(compactPageDensity);

            // Embedded pages are already responsive grids. Keep them attached directly
            // to the main content host so a synthetic outer ScrollViewer cannot force
            // a full-page scrollbar or constrain their star-sized rows.
            AccountApiPageControl.MinHeight = 0d;
            AccountApiPageControl.VerticalAlignment = VerticalAlignment.Stretch;
            AccountApiPageControl.HorizontalAlignment = HorizontalAlignment.Stretch;
            AiAnalysisPageControl.MinHeight = 0d;
            AiAnalysisPageControl.VerticalAlignment = VerticalAlignment.Stretch;
            AiAnalysisPageControl.HorizontalAlignment = HorizontalAlignment.Stretch;
        }
        finally
        {
            _applyingResponsivePageDensity = false;
        }
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

    private void ApplyHomePageDensity(bool compact)
    {
        if (HomePage.Content is not Grid homeGrid)
        {
            return;
        }

        HomeSystemSummaryCard.Height = compact ? 78d : 92d;
        HomeSystemSummaryCard.Padding = compact
            ? new Thickness(18d, 10d, 18d, 10d)
            : new Thickness(22d, 16d, 22d, 16d);

        var resourceCard = homeGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetRow(border) == 1);
        if (resourceCard is not null)
        {
            resourceCard.Height = compact ? 136d : 162d;
            resourceCard.Padding = compact
                ? new Thickness(14d, 8d, 14d, 8d)
                : new Thickness(16d, 10d, 16d, 10d);
            resourceCard.Margin = compact
                ? new Thickness(0d, 8d, 0d, 0d)
                : new Thickness(0d, 10d, 0d, 0d);
        }

        ApplyCircularMetricDensity(CpuProgress, compact);
        ApplyCircularMetricDensity(MemoryProgress, compact);

        var servicesCard = homeGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetRow(border) == 2);
        if (servicesCard is not null)
        {
            servicesCard.Margin = compact
                ? new Thickness(0d, 8d, 0d, 0d)
                : new Thickness(0d, 10d, 0d, 0d);

            if (servicesCard.Child is DockPanel dock)
            {
                var header = dock.Children.OfType<Border>().FirstOrDefault();
                if (header is not null)
                {
                    header.Padding = compact
                        ? new Thickness(14d, 6d, 14d, 6d)
                        : new Thickness(16d, 8d, 16d, 8d);
                }

                var items = dock.Children.OfType<ItemsControl>().FirstOrDefault();
                if (items is not null)
                {
                    items.Margin = compact ? new Thickness(8d) : new Thickness(10d);
                    items.UpdateLayout();
                    ApplyServiceCardDensity(items, compact);
                }
            }
        }

        // Keep Auto as a safety fallback for exceptionally large dynamic content, but
        // the supported 1024x640 viewport is compacted to a zero-scroll normal state.
        HomePage.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HomePage.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private static void ApplyCircularMetricDensity(CircularProgress progress, bool compact)
    {
        var size = compact ? 72d : 86d;
        progress.Width = size;
        progress.Height = size;

        if (progress.Parent is not Grid circleHost)
        {
            return;
        }

        circleHost.Width = size;
        circleHost.Height = size;
        if (circleHost.Parent is Grid metricHost && metricHost.RowDefinitions.Count >= 2)
        {
            metricHost.RowDefinitions[0].Height = new GridLength(compact ? 74d : 90d);
            metricHost.RowDefinitions[1].Height = new GridLength(compact ? 18d : 20d);
        }
    }

    private static void ApplyServiceCardDensity(ItemsControl items, bool compact)
    {
        for (var index = 0; index < items.Items.Count; index++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container)
            {
                continue;
            }

            var card = FindFirstVisualChild<Border>(container);
            if (card is null)
            {
                continue;
            }

            card.Height = compact ? 110d : 166d;
            card.Padding = compact ? new Thickness(8d) : new Thickness(10d);
            card.Margin = compact
                ? new Thickness(0d, 0d, 6d, 6d)
                : new Thickness(0d, 0d, 8d, 8d);

            if (card.Child is not Grid cardGrid)
            {
                continue;
            }

            var details = cardGrid.Children
                .OfType<StackPanel>()
                .FirstOrDefault(panel => Grid.GetRow(panel) == 1);
            if (details is null)
            {
                continue;
            }

            details.Margin = compact
                ? new Thickness(0d, 4d, 0d, 4d)
                : new Thickness(0d, 6d, 0d, 6d);
            var lines = details.Children.OfType<TextBlock>().ToList();
            if (lines.Count >= 2)
            {
                lines[0].Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
                lines[1].Margin = compact
                    ? new Thickness(0d)
                    : new Thickness(0d, 2d, 0d, 0d);
            }
        }
    }

    private void ApplyEnvironmentPageDensity(bool compact)
    {
        var header = EnvironmentPage.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetRow(border) == 0);
        if (header is not null)
        {
            header.Padding = compact
                ? new Thickness(16d, 12d, 16d, 12d)
                : new Thickness(22d, 18d, 22d, 18d);
            header.Margin = compact
                ? new Thickness(0d, 0d, 0d, 10d)
                : new Thickness(0d, 0d, 0d, 16d);
        }

        var items = EnvironmentPage.Children
            .OfType<ItemsControl>()
            .FirstOrDefault(control => Grid.GetRow(control) == 1);
        if (items is null || !EnvironmentPage.IsVisible)
        {
            return;
        }

        items.UpdateLayout();
        for (var index = 0; index < items.Items.Count; index++)
        {
            if (items.ItemContainerGenerator.ContainerFromIndex(index) is not DependencyObject container)
            {
                continue;
            }

            var card = FindFirstVisualChild<Border>(container);
            if (card is null)
            {
                continue;
            }

            card.Margin = compact
                ? new Thickness(0d, 0d, 10d, 10d)
                : new Thickness(0d, 0d, 16d, 16d);
            card.Padding = compact
                ? new Thickness(14d, 12d, 14d, 12d)
                : new Thickness(18d, 16d, 18d, 16d);

            foreach (var text in FindVisualChildren<TextBlock>(card))
            {
                if (compact && Math.Abs(text.FontSize - 18d) < 0.1d)
                {
                    text.FontSize = 16d;
                }
                else if (!compact && Math.Abs(text.FontSize - 16d) < 0.1d)
                {
                    // Environment headings are the only 18 DIP text elements in this
                    // template. Restore those headings when the full layout returns.
                    text.FontSize = 18d;
                }

                if (compact && Math.Abs(text.LineHeight - 19d) < 0.1d)
                {
                    text.LineHeight = 17d;
                }
                else if (!compact && Math.Abs(text.LineHeight - 17d) < 0.1d)
                {
                    text.LineHeight = 19d;
                }
            }

            foreach (var button in FindVisualChildren<Button>(card))
            {
                if (Math.Abs(button.Height - 30d) > 0.1d && Math.Abs(button.Height - 28d) > 0.1d)
                {
                    continue;
                }

                if (Math.Abs(button.Width - 172d) < 0.1d || Math.Abs(button.Width - 146d) < 0.1d)
                {
                    button.Width = compact ? 146d : 172d;
                    button.MinWidth = 0d;
                    button.Height = compact ? 28d : 30d;
                    button.Margin = compact
                        ? new Thickness(0d, 0d, 6d, 6d)
                        : new Thickness(0d, 0d, 8d, 8d);
                    continue;
                }

                if (Math.Abs(button.Width - 78d) < 0.1d || Math.Abs(button.Width - 64d) < 0.1d ||
                    Math.Abs(button.MinWidth - 82d) < 0.1d || Math.Abs(button.MinWidth - 64d) < 0.1d)
                {
                    button.Width = compact ? 64d : 78d;
                    button.MinWidth = compact ? 64d : 82d;
                    button.Height = compact ? 28d : 30d;
                    button.Padding = compact
                        ? new Thickness(6d, 0d, 6d, 0d)
                        : new Thickness(8d, 0d, 8d, 0d);
                    button.Margin = compact
                        ? new Thickness(0d, 0d, 6d, 6d)
                        : new Thickness(0d, 0d, 8d, 8d);
                    button.FontSize = compact ? 12d : 13d;
                }
            }

            foreach (var combo in FindVisualChildren<ComboBox>(card))
            {
                if (Math.Abs(combo.Width - 178d) < 0.1d || Math.Abs(combo.Width - 148d) < 0.1d)
                {
                    combo.Width = compact ? 148d : 178d;
                    combo.Height = compact ? 28d : 30d;
                    combo.Padding = compact
                        ? new Thickness(8d, 0d, 8d, 0d)
                        : new Thickness(10d, 0d, 10d, 0d);
                    combo.Margin = compact
                        ? new Thickness(0d, 0d, 6d, 6d)
                        : new Thickness(0d, 0d, 8d, 8d);
                }
            }
        }
    }

    private static T? FindFirstVisualChild<T>(DependencyObject root)
        where T : DependencyObject
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
            {
                return typed;
            }

            var nested = FindFirstVisualChild<T>(child);
            if (nested is not null)
            {
                return nested;
            }
        }

        return null;
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
