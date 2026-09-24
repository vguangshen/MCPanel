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
            var contentWidth = width - (iconOnlyNavigation ? IconOnlyNavigationWidth : width < CompactShellBreakpoint ? 190d : 226d) - (width < CompactShellBreakpoint ? 32d : 48d);
            _model.SetProductColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 580d ? 2 : 1);
            _model.SetEnvironmentColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 560d ? 2 : 1);
            _model.SetHomeServiceColumnCount(contentWidth >= 850d ? 3 : contentWidth >= 620d ? 2 : 1);
            ApplyHomePageDensity(compactPageDensity, contentWidth);
            ApplyEnvironmentPageDensity(compactPageDensity);

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

    private void ApplyHomePageDensity(bool compact, double contentWidth)
    {
        if (HomePage.Content is not Grid homeGrid)
        {
            return;
        }

        // Keep the overview card at its proven 92 DIP height so the three hardware
        // lines never crowd one another. Recover compact-height space from the much
        // more flexible resource/service sections instead.
        var narrow = contentWidth < 620d;
        HomeSystemSummaryCard.Height = narrow ? double.NaN : 92d;
        HomeSystemSummaryCard.Padding = compact
            ? new Thickness(18d, 10d, 18d, 10d)
            : new Thickness(22d, 16d, 22d, 16d);
        if (HomeSystemSummaryCard.Child is Grid summaryGrid && summaryGrid.Children.Count >= 2)
        {
            summaryGrid.RowDefinitions.Clear();
            summaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summaryGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            summaryGrid.ColumnDefinitions[1].Width = narrow ? new GridLength(0d) : new GridLength(1.55d, GridUnitType.Star);
            var heading = summaryGrid.Children[0];
            var hardware = summaryGrid.Children[1];
            Grid.SetRow(hardware, narrow ? 1 : 0);
            Grid.SetColumn(hardware, narrow ? 0 : 1);
            if (hardware is StackPanel hardwarePanel)
                hardwarePanel.Margin = narrow ? new Thickness(0d, 10d, 0d, 0d) : new Thickness(24d, 0d, 0d, 0d);
            if (heading is StackPanel headingPanel)
                headingPanel.Margin = narrow ? new Thickness(0d) : new Thickness(0d, 0d, 24d, 0d);
        }

        var resourceCard = homeGrid.Children
            .OfType<Border>()
            .FirstOrDefault(border => Grid.GetRow(border) == 1);
        if (resourceCard is not null)
        {
            resourceCard.Height = narrow ? double.NaN : compact ? 132d : 162d;
            resourceCard.Padding = compact
                ? new Thickness(14d, 7d, 14d, 7d)
                : new Thickness(16d, 10d, 16d, 10d);
            resourceCard.Margin = compact
                ? new Thickness(0d, 8d, 0d, 0d)
                : new Thickness(0d, 10d, 0d, 0d);
            if (resourceCard.Child is Grid resourceGrid && resourceGrid.Children.Count >= 3)
            {
                resourceGrid.RowDefinitions.Clear();
                resourceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                resourceGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                resourceGrid.ColumnDefinitions[0].Width = narrow ? new GridLength(1d, GridUnitType.Star) : new GridLength(260d);
                resourceGrid.ColumnDefinitions[1].Width = new GridLength(narrow ? 0d : 1d);
                Grid.SetRow(resourceGrid.Children[2], narrow ? 1 : 0);
                Grid.SetColumn(resourceGrid.Children[2], narrow ? 0 : 2);
                resourceGrid.Children[1].Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
                if (resourceGrid.Children[2] is StackPanel diskPanel)
                    diskPanel.Margin = narrow ? new Thickness(0d, 16d, 0d, 0d) : new Thickness(0d);
            }
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
                        ? new Thickness(14d, 5d, 14d, 5d)
                        : new Thickness(16d, 8d, 16d, 8d);
                }

                var items = dock.Children.OfType<ItemsControl>().FirstOrDefault();
                if (items is not null)
                {
                    items.Margin = compact ? new Thickness(6d) : new Thickness(10d);
                    items.UpdateLayout();
                    ApplyServiceCardDensity(items, compact);
                }
            }
        }

        HomePage.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        HomePage.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }

    private static void ApplyCircularMetricDensity(CircularProgress progress, bool compact)
    {
        var size = compact ? 70d : 86d;
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
            metricHost.RowDefinitions[0].Height = new GridLength(compact ? 72d : 90d);
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

            card.Height = compact ? 92d : 166d;
            card.Padding = compact ? new Thickness(6d) : new Thickness(10d);
            card.Margin = compact
                ? new Thickness(0d, 0d, 5d, 5d)
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
                ? new Thickness(0d, 2d, 0d, 2d)
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

        var items = EnvironmentItemsControl;
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

                if (Math.Abs(button.Width - 172d) < 0.1d || Math.Abs(button.Width - 134d) < 0.1d)
                {
                    button.Width = compact ? 134d : 172d;
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
