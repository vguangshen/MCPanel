using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;

namespace MCPanel;

public partial class MainWindow
{
    private const double ResponsiveLayoutEpsilon = 0.5d;
    private const double CompactShellBreakpoint = 1180d;
    private const double CompactSettingsBreakpoint = 900d;
    private const double StandardNavWidth = 226d;
    private const double CompactNavWidth = 190d;
    private const double EmbeddedPageMinHeight = 630d;
    private const double StartupCardResponsiveMinHeight = 84d;

    private bool _applyingResponsiveLayout;
    private bool _nativeTextRenderingConfigured;
    private bool _settingsAlignmentHooked;
    private bool _aligningSettingsCards;
    private ScrollViewer? _accountApiScrollHost;
    private ScrollViewer? _aiAnalysisScrollHost;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureScrollableEmbeddedPages();
        EnsureSettingsAlignmentHook();
        ApplyResponsiveLayout();

        // OnContentRendered is the first point where all responsive settings cards
        // have real geometry. Prime the stability/alignment rules immediately rather
        // than waiting for another LayoutUpdated pass that may never be scheduled.
        StabilizeSoftwareUpdateCardHeight();
        AlignSettingsCardBottoms();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ResetSettingsCardAlignment();
        ApplyResponsiveLayout(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

        // A requested Window.Width/Height change can be observed before the hosted
        // render target catches up (notably on RDP and headless Windows runners).
        // React to the requested size as well as the rendered size so responsive
        // breakpoints never get stuck in the previous compact state.
        if ((e.Property == WidthProperty || e.Property == HeightProperty) &&
            IsInitialized &&
            !_applyingResponsiveLayout)
        {
            var requestedWidth = e.Property == WidthProperty && e.NewValue is double width
                ? width
                : Width;
            var requestedHeight = e.Property == HeightProperty && e.NewValue is double height
                ? height
                : Height;

            ResetSettingsCardAlignment();
            ApplyResponsiveLayout(requestedWidth, requestedHeight);
        }
    }

    private void ApplyResponsiveLayout(double requestedWidth = double.NaN, double requestedHeight = double.NaN)
    {
        if (_applyingResponsiveLayout || MainViewbox is null || DesignSurface is null)
        {
            return;
        }

        _applyingResponsiveLayout = true;
        try
        {
            // Never scale the complete visual tree. Whole-window Viewbox scaling makes
            // WPF text land on fractional pixels and causes small fonts to look soft.
            // The Viewbox is retained only as a host so existing XAML names and tests
            // remain stable; Stretch=None guarantees a 1:1 device-independent layout.
            MainViewbox.Stretch = Stretch.None;
            MainViewbox.StretchDirection = StretchDirection.Both;
            MainViewbox.HorizontalAlignment = HorizontalAlignment.Stretch;
            MainViewbox.VerticalAlignment = VerticalAlignment.Stretch;

            var viewportWidth = IsUsableDimension(requestedWidth)
                ? requestedWidth
                : MainViewbox.ActualWidth > 1d
                    ? MainViewbox.ActualWidth
                    : Math.Max(1d, ActualWidth);
            var viewportHeight = IsUsableDimension(requestedHeight)
                ? requestedHeight
                : MainViewbox.ActualHeight > 1d
                    ? MainViewbox.ActualHeight
                    : Math.Max(1d, ActualHeight);

            if (IsUsableDimension(viewportWidth) &&
                Math.Abs(DesignSurface.Width - viewportWidth) > ResponsiveLayoutEpsilon)
            {
                DesignSurface.Width = viewportWidth;
            }

            if (IsUsableDimension(viewportHeight) &&
                Math.Abs(DesignSurface.Height - viewportHeight) > ResponsiveLayoutEpsilon)
            {
                DesignSurface.Height = viewportHeight;
            }

            DesignSurface.MinWidth = 0d;
            DesignSurface.MinHeight = 0d;

            ConfigureNativeTextRendering();

            // Use the explicit requested width when one is available. This preserves
            // the user's requested responsive state even if the compositor or a remote
            // session temporarily reports an older constrained render size.
            var responsiveWidth = IsUsableDimension(requestedWidth)
                ? requestedWidth
                : IsUsableDimension(Width)
                    ? Width
                    : viewportWidth;
            var compactShell = responsiveWidth < CompactShellBreakpoint;
            ApplyShellDensity(compactShell);
            ApplyProductsHeaderDensity(compactShell);
            ApplySettingsResponsiveColumns(responsiveWidth < CompactSettingsBreakpoint);

            // The dashboard used to rely on the Viewbox to make all three sections fit
            // vertically. Native-size text must scroll instead of being scaled down.
            HomePage.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HomePage.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        }
        finally
        {
            _applyingResponsiveLayout = false;
        }
    }

    private static bool IsUsableDimension(double value) =>
        value > 1d && !double.IsNaN(value) && !double.IsInfinity(value);

    private void ConfigureNativeTextRendering()
    {
        if (_nativeTextRenderingConfigured)
        {
            return;
        }

        _nativeTextRenderingConfigured = true;
        TextOptions.SetTextFormattingMode(DesignSurface, TextFormattingMode.Display);
        TextOptions.SetTextRenderingMode(DesignSurface, TextRenderingMode.ClearType);
        TextOptions.SetTextHintingMode(DesignSurface, TextHintingMode.Fixed);
        RenderOptions.SetClearTypeHint(DesignSurface, ClearTypeHint.Enabled);
    }

    private void ApplyShellDensity(bool compact)
    {
        if (DesignSurface.RowDefinitions.Count >= 3)
        {
            DesignSurface.RowDefinitions[0].Height = new GridLength(compact ? 56d : 64d);
            DesignSurface.RowDefinitions[2].Height = new GridLength(compact ? 30d : 32d);
        }

        NavRail.Margin = compact
            ? new Thickness(0d, 10d, 0d, 0d)
            : new Thickness(0d, 16d, 0d, 0d);

        if (NavRail.Parent is DockPanel navDock &&
            navDock.Parent is Border navBorder &&
            navBorder.Parent is Grid shellGrid &&
            shellGrid.ColumnDefinitions.Count >= 2)
        {
            shellGrid.ColumnDefinitions[0].Width = new GridLength(compact ? CompactNavWidth : StandardNavWidth);
        }

        if (PageFocusSentinel.Parent is Grid contentHost)
        {
            contentHost.Margin = compact ? new Thickness(16d) : new Thickness(24d);
        }
    }

    private void ApplyProductsHeaderDensity(bool compact)
    {
        if (ProductsHeader.Child is not Grid headerGrid || headerGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        headerGrid.ColumnDefinitions[1].Width = new GridLength(compact ? 300d : 420d);
        headerGrid.ColumnDefinitions[2].Width = new GridLength(132d);
    }

    private void ApplySettingsResponsiveColumns(bool singleColumn)
    {
        if (AppearanceSettingsCard.Parent is not Grid leftColumn ||
            SoftwareUpdateCard.Parent is not Grid rightColumn ||
            leftColumn.Parent is not Grid columnsGrid ||
            !ReferenceEquals(rightColumn.Parent, columnsGrid) ||
            columnsGrid.ColumnDefinitions.Count < 2)
        {
            return;
        }

        if (columnsGrid.RowDefinitions.Count < 2)
        {
            columnsGrid.RowDefinitions.Clear();
            columnsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            columnsGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        }

        if (singleColumn)
        {
            columnsGrid.ColumnDefinitions[0].Width = new GridLength(1d, GridUnitType.Star);
            columnsGrid.ColumnDefinitions[1].Width = new GridLength(0d);
            Grid.SetColumn(leftColumn, 0);
            Grid.SetRow(leftColumn, 0);
            Grid.SetColumn(rightColumn, 0);
            Grid.SetRow(rightColumn, 1);
            leftColumn.Margin = new Thickness(0d);
            rightColumn.Margin = new Thickness(0d, 10d, 0d, 0d);
            return;
        }

        columnsGrid.ColumnDefinitions[0].Width = new GridLength(0.94d, GridUnitType.Star);
        columnsGrid.ColumnDefinitions[1].Width = new GridLength(1.06d, GridUnitType.Star);
        Grid.SetColumn(leftColumn, 0);
        Grid.SetRow(leftColumn, 0);
        Grid.SetColumn(rightColumn, 1);
        Grid.SetRow(rightColumn, 0);
        leftColumn.Margin = new Thickness(0d, 0d, 7d, 0d);
        rightColumn.Margin = new Thickness(7d, 0d, 0d, 0d);
    }

    private void EnsureSettingsAlignmentHook()
    {
        if (_settingsAlignmentHooked)
        {
            return;
        }

        _settingsAlignmentHooked = true;

        // The legacy fixed-height binding tied the startup card to the appearance
        // card. With native-size responsive text that creates a circular height
        // dependency and prevents the shorter column from being aligned correctly.
        BindingOperations.ClearBinding(StartupSettingsCard, FrameworkElement.HeightProperty);
        StartupSettingsCard.Height = double.NaN;
        StartupSettingsCard.MinHeight = Math.Max(StartupSettingsCard.MinHeight, StartupCardResponsiveMinHeight);

        LayoutUpdated += (_, _) =>
        {
            StabilizeSoftwareUpdateCardHeight();
            AlignSettingsCardBottoms();
        };
    }

    private void ResetSettingsCardAlignment()
    {
        if (!_settingsAlignmentHooked || AppearanceSettingsCard is null || StartupSettingsCard is null)
        {
            return;
        }

        AppearanceSettingsCard.Height = double.NaN;
        StartupSettingsCard.Height = double.NaN;
    }

    private void StabilizeSoftwareUpdateCardHeight()
    {
        if (SoftwareUpdateCard is null || SoftwareUpdateCard.ActualHeight <= 1d)
        {
            return;
        }

        // The cancel button switches layout visibility when an update starts. On a
        // rounded WPF layout this can otherwise make the card shrink by one device
        // pixel even though the progress row itself is fixed. Preserve the largest
        // idle/native height so starting an update never makes the card jump.
        if (DataContext is MainViewModel model && model.IsUpdateBusy)
        {
            return;
        }

        if (SoftwareUpdateCard.ActualHeight > SoftwareUpdateCard.MinHeight + ResponsiveLayoutEpsilon)
        {
            SoftwareUpdateCard.MinHeight = SoftwareUpdateCard.ActualHeight;
        }
    }

    private void AlignSettingsCardBottoms()
    {
        if (_aligningSettingsCards ||
            AppearanceSettingsCard is null ||
            StartupSettingsCard is null ||
            AppearanceSettingsCard.Parent is not Grid leftColumn ||
            StartupSettingsCard.Parent is not Grid rightColumn ||
            leftColumn.Parent is not Grid columnsGrid ||
            !ReferenceEquals(rightColumn.Parent, columnsGrid) ||
            Grid.GetColumn(leftColumn) == Grid.GetColumn(rightColumn))
        {
            return;
        }

        try
        {
            _aligningSettingsCards = true;

            var appearanceTop = AppearanceSettingsCard.TransformToAncestor(columnsGrid).Transform(new Point(0d, 0d)).Y;
            var startupTop = StartupSettingsCard.TransformToAncestor(columnsGrid).Transform(new Point(0d, 0d)).Y;
            var appearanceBottom = appearanceTop + AppearanceSettingsCard.ActualHeight;
            var startupBottom = startupTop + StartupSettingsCard.ActualHeight;
            var delta = startupBottom - appearanceBottom;

            if (Math.Abs(delta) <= ResponsiveLayoutEpsilon)
            {
                return;
            }

            if (delta > 0d)
            {
                // The right column is taller. Grow the appearance card instead of
                // trying to shrink the startup card below its readable minimum.
                var targetHeight = AppearanceSettingsCard.ActualHeight + delta;
                if (IsUsableDimension(targetHeight))
                {
                    AppearanceSettingsCard.Height = targetHeight;
                }
            }
            else
            {
                var targetHeight = StartupSettingsCard.ActualHeight - delta;
                if (IsUsableDimension(targetHeight))
                {
                    StartupSettingsCard.Height = Math.Max(StartupSettingsCard.MinHeight, targetHeight);
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Layout may be between visual-tree states during navigation or shutdown.
        }
        finally
        {
            _aligningSettingsCards = false;
        }
    }

    private void EnsureScrollableEmbeddedPages()
    {
        _accountApiScrollHost ??= WrapEmbeddedPage(AccountApiPageControl);
        _aiAnalysisScrollHost ??= WrapEmbeddedPage(AiAnalysisPageControl);
    }

    private static ScrollViewer? WrapEmbeddedPage(FrameworkElement page)
    {
        if (page.Parent is not Grid parent)
        {
            return null;
        }

        var row = Grid.GetRow(page);
        var column = Grid.GetColumn(page);
        var rowSpan = Grid.GetRowSpan(page);
        var columnSpan = Grid.GetColumnSpan(page);
        var zIndex = Panel.GetZIndex(page);

        parent.Children.Remove(page);
        page.MinHeight = EmbeddedPageMinHeight;

        var host = new ScrollViewer
        {
            Content = page,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Focusable = false
        };

        Grid.SetRow(host, row);
        Grid.SetColumn(host, column);
        Grid.SetRowSpan(host, rowSpan);
        Grid.SetColumnSpan(host, columnSpan);
        Panel.SetZIndex(host, zIndex);

        BindingOperations.SetBinding(
            host,
            VisibilityProperty,
            new Binding(nameof(Visibility))
            {
                Source = page,
                Mode = BindingMode.OneWay
            });

        parent.Children.Add(host);
        return host;
    }
}
