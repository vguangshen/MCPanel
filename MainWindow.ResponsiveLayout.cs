using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace MCPanel;

public partial class MainWindow
{
    private const double ResponsiveLayoutEpsilon = 0.5d;
    private const double CompactShellBreakpoint = 1180d;
    private const double CompactSettingsBreakpoint = 1180d;
    private const double CompactShellHeightBreakpoint = 760d;
    private const double StandardNavWidth = 226d;
    private const double CompactNavWidth = 190d;
    private const double StartupCardResponsiveMinHeight = 84d;
    private const double SoftwareUpdateStableMinHeight = 228d;

    private static readonly object LowTierShadowHandlerLock = new();
    private static bool _lowTierShadowHandlerRegistered;

    private bool _applyingResponsiveLayout;
    private bool _nativeTextRenderingConfigured;
    private bool _renderingPerformanceConfigured;
    private bool _settingsAlignmentHooked;
    private bool _aligningSettingsCards;
    private bool _settingsMaintenanceQueued;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        EnsureResponsiveDensityHooks();
        EnsureSettingsAlignmentHook();
        ApplyResponsiveLayout();
        StabilizeSoftwareUpdateCardHeight();
        AlignSettingsCardBottoms();
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        ApplyResponsiveLayout(sizeInfo.NewSize.Width, sizeInfo.NewSize.Height);
        StabilizeSoftwareUpdateCardHeight();
        AlignSettingsCardBottoms();
    }

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);

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

            ApplyResponsiveLayout(requestedWidth, requestedHeight);
            StabilizeSoftwareUpdateCardHeight();
            AlignSettingsCardBottoms();
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
            ConfigureRenderingPerformancePolicy();

            var responsiveWidth = IsUsableDimension(requestedWidth)
                ? requestedWidth
                : IsUsableDimension(Width)
                    ? Width
                    : viewportWidth;
            var responsiveHeight = IsUsableDimension(requestedHeight)
                ? requestedHeight
                : IsUsableDimension(Height)
                    ? Height
                    : viewportHeight;

            var compactWidth = responsiveWidth < CompactShellBreakpoint;
            var compactShell = compactWidth || responsiveHeight < CompactShellHeightBreakpoint;
            ApplyShellDensity(compactShell, compactWidth);
            ApplyProductsHeaderDensity(responsiveWidth);
            ApplySitesHeaderDensity(responsiveWidth);
            ApplyResponsivePageDensity(responsiveWidth, responsiveHeight);
            ApplySettingsResponsiveColumns(responsiveWidth < CompactSettingsBreakpoint);
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

    private void ConfigureRenderingPerformancePolicy()
    {
        if (_renderingPerformanceConfigured)
        {
            return;
        }

        _renderingPerformanceConfigured = true;
        var renderingTier = RenderCapability.Tier >> 16;
        var lowTierOrRemoteSession = renderingTier < 2 ||
                                     System.Windows.Forms.SystemInformation.TerminalServerSession;
        if (!lowTierOrRemoteSession)
        {
            return;
        }

        // WPF DropShadowEffect is disproportionately expensive when the desktop is
        // rendered through RDP/cloud-PC software composition. Remove decorative
        // shadows in that environment while keeping borders, spacing and colors.
        foreach (var border in FindVisualChildren<Border>(DesignSurface))
        {
            if (border.Effect is DropShadowEffect)
            {
                border.Effect = null;
            }
        }

        lock (LowTierShadowHandlerLock)
        {
            if (_lowTierShadowHandlerRegistered)
            {
                return;
            }

            EventManager.RegisterClassHandler(
                typeof(Border),
                FrameworkElement.LoadedEvent,
                new RoutedEventHandler(RemoveLowTierDropShadowOnLoaded));
            _lowTierShadowHandlerRegistered = true;
        }
    }

    private static void RemoveLowTierDropShadowOnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is Border { Effect: DropShadowEffect } border)
        {
            border.Effect = null;
        }
    }

    private void ApplyShellDensity(bool compactShell, bool compactWidth)
    {
        if (DesignSurface.RowDefinitions.Count >= 3)
        {
            DesignSurface.RowDefinitions[0].Height = new GridLength(compactShell ? 56d : 64d);
            DesignSurface.RowDefinitions[2].Height = new GridLength(compactShell ? 30d : 32d);
        }

        NavRail.Margin = compactShell
            ? new Thickness(0d, 10d, 0d, 0d)
            : new Thickness(0d, 16d, 0d, 0d);

        if (NavRail.Parent is DockPanel navDock &&
            navDock.Parent is Border navBorder &&
            navBorder.Parent is Grid shellGrid &&
            shellGrid.ColumnDefinitions.Count >= 2)
        {
            shellGrid.ColumnDefinitions[0].Width = new GridLength(compactWidth ? CompactNavWidth : StandardNavWidth);
        }

        if (PageFocusSentinel.Parent is Grid contentHost)
        {
            contentHost.Margin = compactShell ? new Thickness(16d) : new Thickness(24d);
        }
    }

    private void ApplyProductsHeaderDensity(double width)
    {
        if (ProductsHeader.Child is not Grid headerGrid || headerGrid.ColumnDefinitions.Count < 3)
        {
            return;
        }

        var stacked = width < 950d;
        headerGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0d : width < CompactShellBreakpoint ? 300d : 420d);
        headerGrid.ColumnDefinitions[2].Width = new GridLength(132d);
        Grid.SetRow(ProductsSearchHost, stacked ? 1 : 0);
        Grid.SetColumn(ProductsSearchHost, stacked ? 0 : 1);
        Grid.SetColumnSpan(ProductsSearchHost, stacked ? 3 : 1);
        ProductsSearchHost.Margin = stacked ? new Thickness(0d, 12d, 0d, 0d) : new Thickness(18d, 0d, 0d, 0d);
        ProductsHeader.Padding = stacked ? new Thickness(16d, 12d, 16d, 12d) : new Thickness(20d, 16d, 20d, 16d);
    }

    private void ApplySitesHeaderDensity(double width)
    {
        var stacked = width < 1000d;
        SitesHeaderGrid.ColumnDefinitions[1].Width = new GridLength(stacked ? 0d : 320d);
        Grid.SetRow(WebsiteSearchHost, stacked ? 1 : 0);
        Grid.SetColumn(WebsiteSearchHost, stacked ? 0 : 1);
        Grid.SetColumnSpan(WebsiteSearchHost, stacked ? 3 : 1);
        WebsiteSearchHost.Margin = stacked ? new Thickness(0d, 12d, 0d, 0d) : new Thickness(18d, 0d, 0d, 0d);
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

        BindingOperations.ClearBinding(StartupSettingsCard, FrameworkElement.HeightProperty);
        StartupSettingsCard.Height = double.NaN;
        StartupSettingsCard.MinHeight = Math.Max(StartupSettingsCard.MinHeight, StartupCardResponsiveMinHeight);
        SoftwareUpdateCard.MinHeight = Math.Max(SoftwareUpdateCard.MinHeight, SoftwareUpdateStableMinHeight);

        AppearanceSettingsCard.Loaded += (_, _) => QueueSettingsCardMaintenance();
        StartupSettingsCard.Loaded += (_, _) => QueueSettingsCardMaintenance();
        SoftwareUpdateCard.Loaded += (_, _) => QueueSettingsCardMaintenance();
        SettingsPage.IsVisibleChanged += (_, _) => QueueSettingsCardMaintenance();
        _model.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(MainViewModel.IsUpdateBusy) ||
                args.PropertyName == nameof(MainViewModel.UpdateStatus))
            {
                QueueSettingsCardMaintenance();
            }
        };
    }

    private void QueueSettingsCardMaintenance()
    {
        if (_settingsMaintenanceQueued ||
            Dispatcher.HasShutdownStarted ||
            SettingsPage is null ||
            !SettingsPage.IsVisible)
        {
            return;
        }

        _settingsMaintenanceQueued = true;
        Dispatcher.BeginInvoke(
            new Action(() =>
            {
                _settingsMaintenanceQueued = false;
                StabilizeSoftwareUpdateCardHeight();
                AlignSettingsCardBottoms();
            }),
            DispatcherPriority.Loaded);
    }

    private void StabilizeSoftwareUpdateCardHeight()
    {
        if (SoftwareUpdateCard is null)
        {
            return;
        }

        SoftwareUpdateCard.MinHeight = SoftwareUpdateStableMinHeight;
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
            Grid.GetColumn(leftColumn) == Grid.GetColumn(rightColumn) ||
            AppearanceSettingsCard.ActualHeight <= 1d ||
            StartupSettingsCard.ActualHeight <= 1d)
        {
            return;
        }

        try
        {
            _aligningSettingsCards = true;

            columnsGrid.UpdateLayout();

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
}
