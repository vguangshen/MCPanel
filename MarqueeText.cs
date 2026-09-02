using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace MCPanel;

public sealed class MarqueeText : Control
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(MarqueeText),
        new FrameworkPropertyMetadata(
            string.Empty,
            FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender,
            OnTextChanged));

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _animationTimer;
    private bool _textOverflows;

    static MarqueeText()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(MarqueeText),
            new FrameworkPropertyMetadata(typeof(MarqueeText)));
    }

    public MarqueeText()
    {
        ClipToBounds = true;
        _animationTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(80)
        };
        _animationTimer.Tick += OnAnimationTick;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        IsVisibleChanged += (_, _) => UpdateAnimationState();
        SizeChanged += (_, _) => UpdateAnimationState();
    }

    public string Text
    {
        get => (string)GetValue(TextProperty);
        set => SetValue(TextProperty, value);
    }

    protected override Size MeasureOverride(Size constraint)
    {
        var text = CreateFormattedText();
        var width = double.IsInfinity(constraint.Width) ? text.WidthIncludingTrailingWhitespace : constraint.Width;
        return new Size(Math.Max(0, width), Math.Max(text.Height, FontSize * 1.35));
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);
        if (string.IsNullOrEmpty(Text) || ActualWidth <= 0 || ActualHeight <= 0)
        {
            return;
        }

        var text = CreateFormattedText();
        var textWidth = Math.Ceiling(text.WidthIncludingTrailingWhitespace) + 2;
        var y = Math.Max(0, (ActualHeight - text.Height) / 2);
        if (textWidth <= ActualWidth)
        {
            drawingContext.DrawText(text, new Point(0, y));
            return;
        }

        const double speed = 36;
        const double initialDelaySeconds = 0.8;
        const double blankGap = 48;
        var movingSeconds = Math.Max(0, _clock.Elapsed.TotalSeconds - initialDelaySeconds);
        var cycleDistance = ActualWidth + textWidth + blankGap;
        var offset = movingSeconds * speed % cycleDistance;
        var x = ActualWidth - offset;
        drawingContext.DrawText(text, new Point(x, y));
    }

    private FormattedText CreateFormattedText()
    {
        return new FormattedText(
            Text ?? string.Empty,
            CultureInfo.CurrentUICulture,
            FlowDirection,
            new Typeface(FontFamily, FontStyle, FontWeight, FontStretch),
            FontSize,
            Foreground,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        UpdateAnimationState();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _animationTimer.Stop();
    }

    private static void OnTextChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs e)
    {
        ((MarqueeText)dependencyObject).UpdateAnimationState();
    }

    private void UpdateAnimationState()
    {
        if (!IsLoaded || !IsVisible || ActualWidth <= 0 || string.IsNullOrEmpty(Text))
        {
            _textOverflows = false;
            _animationTimer.Stop();
            return;
        }

        _textOverflows = Math.Ceiling(CreateFormattedText().WidthIncludingTrailingWhitespace) + 2 > ActualWidth;
        if (_textOverflows)
        {
            if (!_animationTimer.IsEnabled)
            {
                _clock.Restart();
                _animationTimer.Start();
            }
        }
        else
        {
            _animationTimer.Stop();
            _clock.Reset();
        }

        InvalidateVisual();
    }

    private void OnAnimationTick(object? sender, EventArgs e)
    {
        if (!_textOverflows || !IsVisible)
        {
            UpdateAnimationState();
            return;
        }

        InvalidateVisual();
    }
}
