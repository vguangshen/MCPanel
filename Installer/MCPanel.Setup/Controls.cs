using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MCPanel.Setup
{
    /// <summary>A circular arc from 12 o'clock, clockwise; animatable via <see cref="Fraction"/>.</summary>
    public sealed class ArcShape : Shape
    {
        public static readonly DependencyProperty FractionProperty = DependencyProperty.Register(
            nameof(Fraction), typeof(double), typeof(ArcShape),
            new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

        public double Fraction
        {
            get => (double)GetValue(FractionProperty);
            set => SetValue(FractionProperty, value);
        }

        protected override Geometry DefiningGeometry
        {
            get
            {
                var fraction = Math.Max(0, Math.Min(0.9999, Fraction));
                if (fraction <= 0.0001 || double.IsNaN(Width))
                {
                    return Geometry.Empty;
                }

                var radius = (Math.Min(Width, Height) - StrokeThickness) / 2;
                var center = new Point(Width / 2, Height / 2);
                var start = new Point(center.X, center.Y - radius);
                var angle = fraction * 2 * Math.PI;
                var end = new Point(center.X + radius * Math.Sin(angle), center.Y - radius * Math.Cos(angle));
                var figure = new PathFigure { StartPoint = start, IsClosed = false };
                figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
                var geometry = new PathGeometry();
                geometry.Figures.Add(figure);
                return geometry;
            }
        }
    }

    internal enum StepState
    {
        Pending,
        Active,
        Done,
        Failed
    }

    /// <summary>One row of the vertical stage timeline.</summary>
    internal sealed class StepRow : Grid
    {
        private static readonly Brush PendingRing = Frozen("#CFD8E6");
        private static readonly Brush ActiveRing = Frozen("#1A73E8");
        private static readonly Brush DoneFill = Frozen("#1A73E8");
        private static readonly Brush FailedFill = Frozen("#D93025");
        private static readonly Brush TitleBrush = Frozen("#0F1B2D");
        private static readonly Brush PendingTitle = Frozen("#8A98AD");

        private readonly Ellipse _ring;
        private readonly Ellipse _fill;
        private readonly Ellipse _spinner;
        private readonly RotateTransform _spin;
        private readonly Path _check;
        private readonly TextBlock _title;
        private readonly TextBlock _hint;
        private readonly Rectangle _connector;
        private readonly Rectangle _connectorFill;
        private StepState _state = StepState.Pending;

        public StepRow(string title, string hint, bool last)
        {
            Height = last ? 34 : 50;
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
            ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var node = new Grid { Width = 22, Height = 22, VerticalAlignment = VerticalAlignment.Top, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 1, 0, 0) };
            _ring = new Ellipse { Stroke = PendingRing, StrokeThickness = 2 };
            _fill = new Ellipse { Fill = DoneFill, Opacity = 0, RenderTransformOrigin = new Point(0.5, 0.5), RenderTransform = new ScaleTransform(0.4, 0.4) };
            _spin = new RotateTransform();
            _spinner = new Ellipse
            {
                Stroke = ActiveRing,
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 7, 30 },
                Opacity = 0,
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = _spin
            };
            _check = new Path
            {
                Data = (Geometry)Application.Current.FindResource("CheckGlyph"),
                Stroke = Brushes.White,
                StrokeThickness = 2.4,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round,
                Width = 12,
                Height = 12,
                Stretch = Stretch.Uniform,
                StrokeDashArray = new DoubleCollection { 12 },
                StrokeDashOffset = 12,
                Opacity = 0
            };
            node.Children.Add(_ring);
            node.Children.Add(_spinner);
            node.Children.Add(_fill);
            node.Children.Add(_check);
            Children.Add(node);

            if (!last)
            {
                _connector = new Rectangle { Width = 2, Fill = PendingRing, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(10, 26, 0, 2), RadiusX = 1, RadiusY = 1 };
                _connectorFill = new Rectangle
                {
                    Width = 2,
                    Fill = DoneFill,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    Margin = new Thickness(10, 26, 0, 2),
                    RadiusX = 1,
                    RadiusY = 1,
                    RenderTransformOrigin = new Point(0.5, 0),
                    RenderTransform = new ScaleTransform(1, 0)
                };
                Children.Add(_connector);
                Children.Add(_connectorFill);
            }

            var text = new StackPanel();
            SetColumn(text, 1);
            _title = new TextBlock { Text = title, FontSize = 13.5, FontWeight = FontWeights.SemiBold, Foreground = PendingTitle };
            _hint = new TextBlock { Text = hint, FontSize = 11.5, Foreground = PendingTitle, Margin = new Thickness(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
            text.Children.Add(_title);
            text.Children.Add(_hint);
            Children.Add(text);
        }

        public StepState State => _state;

        public void SetHint(string hint) => _hint.Text = hint;

        public void SetState(StepState state, bool animate = true)
        {
            if (_state == state)
            {
                return;
            }

            _state = state;
            var duration = animate ? 0.32 : 0.0;
            switch (state)
            {
                case StepState.Pending:
                    _title.Foreground = PendingTitle;
                    _ring.Stroke = PendingRing;
                    Fade(_spinner, 0, duration);
                    Fade(_fill, 0, duration);
                    Fade(_check, 0, duration);
                    _spin.BeginAnimation(RotateTransform.AngleProperty, null);
                    break;
                case StepState.Active:
                    _title.Foreground = TitleBrush;
                    _ring.Stroke = Frozen("#D7E5FB");
                    Fade(_spinner, 1, duration);
                    _spin.BeginAnimation(RotateTransform.AngleProperty,
                        new DoubleAnimation(0, 360, TimeSpan.FromSeconds(1.1)) { RepeatBehavior = RepeatBehavior.Forever });
                    break;
                case StepState.Done:
                case StepState.Failed:
                    _title.Foreground = TitleBrush;
                    _fill.Fill = state == StepState.Done ? DoneFill : FailedFill;
                    _ring.Stroke = _fill.Fill;
                    _spin.BeginAnimation(RotateTransform.AngleProperty, null);
                    Fade(_spinner, 0, duration);
                    Fade(_fill, 1, duration);
                    var scale = (ScaleTransform)_fill.RenderTransform;
                    var pop = new DoubleAnimation(0.4, 1, TimeSpan.FromSeconds(duration)) { EasingFunction = new BackEase { Amplitude = 0.5, EasingMode = EasingMode.EaseOut } };
                    scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
                    scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
                    if (state == StepState.Done)
                    {
                        _check.Opacity = 1;
                        _check.BeginAnimation(Shape.StrokeDashOffsetProperty,
                            new DoubleAnimation(12, 0, TimeSpan.FromSeconds(animate ? 0.35 : 0)) { BeginTime = TimeSpan.FromSeconds(duration * 0.6) });
                        if (_connectorFill != null)
                        {
                            ((ScaleTransform)_connectorFill.RenderTransform).BeginAnimation(ScaleTransform.ScaleYProperty,
                                new DoubleAnimation(0, 1, TimeSpan.FromSeconds(animate ? 0.45 : 0)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut } });
                        }
                    }

                    break;
            }
        }

        private static void Fade(UIElement element, double to, double seconds) =>
            element.BeginAnimation(OpacityProperty, new DoubleAnimation(to, TimeSpan.FromSeconds(seconds)));

        internal static Brush Frozen(string color)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
            brush.Freeze();
            return brush;
        }
    }
}
