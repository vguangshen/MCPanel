using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace MCPanel;

public sealed class VerticalMeter : Control
{
    public static readonly DependencyProperty TitleProperty =
        DependencyProperty.Register(nameof(Title), typeof(string), typeof(VerticalMeter), new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(VerticalMeter), new PropertyMetadata(0d));

    public string Title
    {
        get => (string)GetValue(TitleProperty);
        set => SetValue(TitleProperty, value);
    }

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    static VerticalMeter()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(VerticalMeter), new FrameworkPropertyMetadata(typeof(VerticalMeter)));
    }

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Template = BuildTemplate();
    }

    private static ControlTemplate BuildTemplate()
    {
        const string template = """
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Control}">
                <Border BorderBrush="#D7DEE8" BorderThickness="1" CornerRadius="6" Width="74" Padding="8">
                    <Grid>
                        <Grid.RowDefinitions>
                            <RowDefinition Height="28" />
                            <RowDefinition Height="*" />
                            <RowDefinition Height="28" />
                        </Grid.RowDefinitions>
                        <TextBlock Text="{Binding Title, RelativeSource={RelativeSource TemplatedParent}}" FontWeight="SemiBold" FontSize="15" HorizontalAlignment="Center" Background="White" Padding="8,0" />
                        <Grid Grid.Row="1" Margin="10,4" ClipToBounds="True">
                            <Border Background="#E2E8F0" />
                            <Border Background="#1296DB" VerticalAlignment="Bottom">
                                <Border.Height>
                                    <MultiBinding Converter="{x:Static local:MeterHeightConverter.Instance}" xmlns:local="clr-namespace:MCPanel">
                                        <Binding Path="ActualHeight" RelativeSource="{RelativeSource AncestorType=Grid}" />
                                        <Binding Path="Value" RelativeSource="{RelativeSource TemplatedParent}" />
                                    </MultiBinding>
                                </Border.Height>
                            </Border>
                        </Grid>
                        <TextBlock Grid.Row="2" Text="{Binding Value, RelativeSource={RelativeSource TemplatedParent}, StringFormat={}{0:N1}%}" FontSize="13" HorizontalAlignment="Center" VerticalAlignment="Bottom" />
                    </Grid>
                </Border>
            </ControlTemplate>
            """;

        return (ControlTemplate)System.Windows.Markup.XamlReader.Parse(template);
    }
}

public sealed class CircularProgress : Control
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(
            nameof(Value),
            typeof(double),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(
                0d,
                FrameworkPropertyMetadataOptions.AffectsRender,
                OnValueChanged));

    public static readonly DependencyProperty AnimatedValueProperty =
        DependencyProperty.Register(
            nameof(AnimatedValue),
            typeof(double),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty IsIndeterminateProperty =
        DependencyProperty.Register(
            nameof(IsIndeterminate),
            typeof(bool),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender, OnIsIndeterminateChanged));

    private static readonly DependencyProperty IndeterminateAngleProperty =
        DependencyProperty.Register(
            "IndeterminateAngle",
            typeof(double),
            typeof(CircularProgress),
            new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    private static readonly Duration ValueAnimationDuration =
        new(TimeSpan.FromMilliseconds(700));
    private static readonly Duration IndeterminateAnimationDuration =
        new(TimeSpan.FromMilliseconds(1050));

    public CircularProgress()
    {
        Loaded += (_, _) =>
        {
            if (IsIndeterminate && IsVisible)
            {
                StartIndeterminateAnimation();
            }
        };
        Unloaded += (_, _) => StopIndeterminateAnimation();
        IsVisibleChanged += (_, _) =>
        {
            if (!IsVisible)
            {
                StopIndeterminateAnimation();
            }
            else if (IsIndeterminate && IsLoaded)
            {
                StartIndeterminateAnimation();
            }
        };
    }

    public static readonly DependencyProperty ProgressBrushProperty =
        DependencyProperty.Register(nameof(ProgressBrush), typeof(Brush), typeof(CircularProgress), new FrameworkPropertyMetadata(Brushes.DodgerBlue, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty =
        DependencyProperty.Register(nameof(TrackBrush), typeof(Brush), typeof(CircularProgress), new FrameworkPropertyMetadata(new SolidColorBrush(Color.FromRgb(232, 234, 237)), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty StrokeThicknessProperty =
        DependencyProperty.Register(nameof(StrokeThickness), typeof(double), typeof(CircularProgress), new FrameworkPropertyMetadata(8d, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double AnimatedValue => (double)GetValue(AnimatedValueProperty);

    public bool IsIndeterminate
    {
        get => (bool)GetValue(IsIndeterminateProperty);
        set => SetValue(IsIndeterminateProperty, value);
    }

    private double IndeterminateAngle => (double)GetValue(IndeterminateAngleProperty);

    public Brush ProgressBrush
    {
        get => (Brush)GetValue(ProgressBrushProperty);
        set => SetValue(ProgressBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double StrokeThickness
    {
        get => (double)GetValue(StrokeThicknessProperty);
        set => SetValue(StrokeThicknessProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var thickness = Math.Max(1, StrokeThickness);
        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        var radius = Math.Max(1, size / 2 - thickness / 2 - 2);
        var trackPen = CreatePen(TrackBrush, thickness);
        var progressPen = CreatePen(ProgressBrush, thickness);

        drawingContext.DrawEllipse(null, trackPen, center, radius, radius);

        if (IsIndeterminate)
        {
            var activityStartAngle = IndeterminateAngle - 90d;
            var activityEndAngle = activityStartAngle + 96d;
            var activityStart = PointOnCircle(center, radius, activityStartAngle);
            var activityEnd = PointOnCircle(center, radius, activityEndAngle);
            var activityGeometry = new StreamGeometry();
            using (var activityContext = activityGeometry.Open())
            {
                activityContext.BeginFigure(activityStart, isFilled: false, isClosed: false);
                activityContext.ArcTo(activityEnd, new Size(radius, radius), 0, false, SweepDirection.Clockwise, true, false);
            }

            activityGeometry.Freeze();
            drawingContext.DrawGeometry(null, progressPen, activityGeometry);
            return;
        }

        var percent = Compat.Clamp(AnimatedValue, 0, 100);
        if (percent <= 0)
        {
            return;
        }

        if (percent >= 99.95)
        {
            drawingContext.DrawEllipse(null, progressPen, center, radius, radius);
            return;
        }

        var startAngle = -90d;
        var endAngle = startAngle + percent * 3.6d;
        var start = PointOnCircle(center, radius, startAngle);
        var end = PointOnCircle(center, radius, endAngle);
        var geometry = new StreamGeometry();
        using (var context = geometry.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, percent >= 50, SweepDirection.Clockwise, true, false);
        }

        geometry.Freeze();
        drawingContext.DrawGeometry(null, progressPen, geometry);
    }

    private static void OnIsIndeterminateChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (CircularProgress)dependencyObject;
        if ((bool)args.NewValue && control.IsLoaded && control.IsVisible)
        {
            control.StartIndeterminateAnimation();
        }
        else
        {
            control.StopIndeterminateAnimation();
        }
    }

    private void StartIndeterminateAnimation()
    {
        if (!IsVisible)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = 0d,
            To = 360d,
            Duration = IndeterminateAnimationDuration,
            RepeatBehavior = RepeatBehavior.Forever,
            FillBehavior = FillBehavior.HoldEnd
        };
        BeginAnimation(IndeterminateAngleProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private void StopIndeterminateAnimation()
    {
        BeginAnimation(IndeterminateAngleProperty, null);
        SetValue(IndeterminateAngleProperty, 0d);
    }

    private static void OnValueChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var control = (CircularProgress)dependencyObject;
        var target = NormalizeValue((double)args.NewValue);

        // Smooth interpolation is useful on a local hardware-composited desktop,
        // but it becomes expensive under RDP/cloud-PC software composition. Hidden
        // controls also do not need compositor work at all.
        if (!control.IsLoaded ||
            !control.IsVisible ||
            control.ActualWidth <= 0 ||
            control.ActualHeight <= 0 ||
            !ShouldAnimateValueChanges())
        {
            control.BeginAnimation(AnimatedValueProperty, null);
            control.SetValue(AnimatedValueProperty, target);
            return;
        }

        var current = control.AnimatedValue;
        if (Math.Abs(current - target) < 0.01)
        {
            control.BeginAnimation(AnimatedValueProperty, null);
            control.SetValue(AnimatedValueProperty, target);
            return;
        }

        var animation = new DoubleAnimation
        {
            From = current,
            To = target,
            Duration = ValueAnimationDuration,
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd
        };
        control.BeginAnimation(AnimatedValueProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }

    private static bool ShouldAnimateValueChanges() =>
        (RenderCapability.Tier >> 16) >= 2 &&
        !System.Windows.Forms.SystemInformation.TerminalServerSession;

    private static double NormalizeValue(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
        {
            return 0;
        }

        return Compat.Clamp(value, 0, 100);
    }

    private static Pen CreatePen(Brush brush, double thickness) => new(brush, thickness)
    {
        StartLineCap = PenLineCap.Round,
        EndLineCap = PenLineCap.Round
    };

    private static Point PointOnCircle(Point center, double radius, double angleDegrees)
    {
        var radians = angleDegrees * Math.PI / 180d;
        return new Point(center.X + radius * Math.Cos(radians), center.Y + radius * Math.Sin(radians));
    }
}
