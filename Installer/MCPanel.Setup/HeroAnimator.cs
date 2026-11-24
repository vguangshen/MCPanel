using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace MCPanel.Setup
{
    /// <summary>
    /// Drives the vector box illustration on the brand stage:
    /// idle (arrow hovers over the open box), working (arrow drops into the box
    /// in a loop while the ring tracks progress and modules pop out),
    /// success (box settles, badge draws), failure (shake + alert) and the
    /// reverse uninstall sequence (modules return, arrow lifts out).
    /// </summary>
    internal sealed class HeroAnimator
    {
        private readonly MainWindow _w;
        private readonly Random _random = new Random(7);

        public HeroAnimator(MainWindow window)
        {
            _w = window;
        }

        public void BuildBackdrop()
        {
            // Faint isometric-style grid.
            var lines = new GeometryGroup();
            for (var x = -400; x < 800; x += 34)
            {
                lines.Children.Add(new LineGeometry(new Point(x, 0), new Point(x + 360, 600)));
                lines.Children.Add(new LineGeometry(new Point(x + 360, 0), new Point(x, 600)));
            }

            _w.GridLayer.Children.Add(new Path { Data = lines, Stroke = Brushes.White, StrokeThickness = 1 });

            // Slow floating particles.
            for (var index = 0; index < 18; index++)
            {
                var size = 2 + _random.NextDouble() * 3.5;
                var dot = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = index % 3 == 0 ? StepRow.Frozen("#7FE9FF") : Brushes.White,
                    Opacity = 0.12 + _random.NextDouble() * 0.35
                };
                var shift = new TranslateTransform();
                dot.RenderTransform = shift;
                Canvas.SetLeft(dot, _random.NextDouble() * 370);
                Canvas.SetTop(dot, 60 + _random.NextDouble() * 500);
                _w.ParticleLayer.Children.Add(dot);
                var rise = new DoubleAnimation(0, -40 - _random.NextDouble() * 60, TimeSpan.FromSeconds(6 + _random.NextDouble() * 6))
                {
                    AutoReverse = true,
                    RepeatBehavior = RepeatBehavior.Forever,
                    BeginTime = TimeSpan.FromSeconds(-_random.NextDouble() * 6),
                    EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
                };
                shift.BeginAnimation(TranslateTransform.YProperty, rise);
            }

            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 0, 360, 18, autoReverse: false, ease: false);
            Loop(_w.HaloScale, ScaleTransform.ScaleXProperty, 0.94, 1.04, 2.6);
            Loop(_w.HaloScale, ScaleTransform.ScaleYProperty, 0.94, 1.04, 2.6);
        }

        public void Idle()
        {
            HideBadges();
            StopArrow();
            Loop(_w.ArrowShift, TranslateTransform.YProperty, -7, 0, 1.4);
            Loop(_w.FlapLeftRotation, RotateTransform.AngleProperty, 0, -4, 1.4);
            Loop(_w.FlapRightRotation, RotateTransform.AngleProperty, 0, 4, 1.4);
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, To(1, 0.3));
            SetProgress(0, animate: false);
        }

        public void Working()
        {
            HideBadges();
            StopArrow();
            // The arrow drops behind the lid, i.e. into the box, then reappears above.
            var drop = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            drop.KeyFrames.Add(new DiscreteDoubleKeyFrame(-34, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            drop.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.45)), new CubicEase { EasingMode = EasingMode.EaseOut }));
            drop.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.75))));
            drop.KeyFrames.Add(new EasingDoubleKeyFrame(30, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2)), new QuadraticEase { EasingMode = EasingMode.EaseIn }));
            drop.KeyFrames.Add(new DiscreteDoubleKeyFrame(30, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.35))));
            _w.ArrowShift.BeginAnimation(TranslateTransform.YProperty, drop);

            var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.25))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.35))));
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, fade);

            Loop(_w.FlapLeftRotation, RotateTransform.AngleProperty, -2, -12, 0.675);
            Loop(_w.FlapRightRotation, RotateTransform.AngleProperty, 2, 12, 0.675);
            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 0, 360, 4.5, autoReverse: false, ease: false);
        }

        public void SetProgress(double fraction, bool animate = true)
        {
            fraction = Math.Max(0, Math.Min(1, fraction));
            _w.ProgressArc.BeginAnimation(ArcShape.FractionProperty, animate
                ? new DoubleAnimation(fraction, TimeSpan.FromSeconds(0.6)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } }
                : new DoubleAnimation(fraction, TimeSpan.Zero));
        }

        /// <summary>Pops a deployed module out of the box (0 runtime, 1 web, 2 service).</summary>
        public void ShowModule(int index, bool visible = true, bool animate = true)
        {
            var module = index == 0 ? _w.ModuleRuntime : index == 1 ? _w.ModuleWeb : _w.ModuleService;
            var scale = (ScaleTransform)module.RenderTransform;
            var seconds = animate ? 0.55 : 0;
            var target = visible ? 1.0 : 0.2;
            var ease = visible ? (IEasingFunction)new BackEase { Amplitude = 0.6, EasingMode = EasingMode.EaseOut } : new CubicEase { EasingMode = EasingMode.EaseIn };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(target, TimeSpan.FromSeconds(seconds)) { EasingFunction = ease });
            module.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(visible ? 1 : 0, TimeSpan.FromSeconds(seconds * 0.7)));
        }

        public void Success()
        {
            StopArrow();
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, To(0, 0.3));
            _w.FlapLeftRotation.BeginAnimation(RotateTransform.AngleProperty, To(-3, 0.4));
            _w.FlapRightRotation.BeginAnimation(RotateTransform.AngleProperty, To(3, 0.4));
            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 0, 360, 18, autoReverse: false, ease: false);
            SetProgress(1);
            for (var index = 0; index < 3; index++)
            {
                ShowModule(index);
            }

            Bounce();
            PopBadge(_w.SuccessBadge);
            _w.SuccessCheck.BeginAnimation(Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.4)) { BeginTime = TimeSpan.FromSeconds(0.35) });
            Burst();
        }

        public void Failure()
        {
            StopArrow();
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, To(1, 0.2));
            _w.ArrowShift.BeginAnimation(TranslateTransform.YProperty, To(-8, 0.3));
            _w.FlapLeftRotation.BeginAnimation(RotateTransform.AngleProperty, To(0, 0.3));
            _w.FlapRightRotation.BeginAnimation(RotateTransform.AngleProperty, To(0, 0.3));
            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 0, 360, 18, autoReverse: false, ease: false);
            var shake = new DoubleAnimationUsingKeyFrames();
            var offsets = new[] { -7.0, 6, -5, 4, -2, 0 };
            for (var index = 0; index < offsets.Length; index++)
            {
                shake.KeyFrames.Add(new LinearDoubleKeyFrame(offsets[index], KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.06 * (index + 1)))));
            }

            _w.BoxShift.BeginAnimation(TranslateTransform.XProperty, shake);
            HideBadges();
            PopBadge(_w.ErrorBadge);
        }

        /// <summary>Uninstall: modules sink back, the arrow lifts out of the box.</summary>
        public void Removing()
        {
            HideBadges();
            StopArrow();
            for (var index = 0; index < 3; index++)
            {
                ShowModule(index, visible: true, animate: false);
            }

            var lift = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            lift.KeyFrames.Add(new DiscreteDoubleKeyFrame(30, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            lift.KeyFrames.Add(new EasingDoubleKeyFrame(-44, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.1)), new CubicEase { EasingMode = EasingMode.EaseInOut }));
            lift.KeyFrames.Add(new DiscreteDoubleKeyFrame(-44, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.35))));
            _w.ArrowShift.BeginAnimation(TranslateTransform.YProperty, lift);
            var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
            fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.8))));
            fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.1))));
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, fade);
            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 360, 0, 4.5, autoReverse: false, ease: false);
            Loop(_w.FlapLeftRotation, RotateTransform.AngleProperty, -2, -12, 0.675);
            Loop(_w.FlapRightRotation, RotateTransform.AngleProperty, 2, 12, 0.675);
        }

        public void Removed()
        {
            StopArrow();
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, To(0, 0.3));
            for (var index = 0; index < 3; index++)
            {
                ShowModule(index, visible: false);
            }

            _w.FlapLeftRotation.BeginAnimation(RotateTransform.AngleProperty, To(0, 0.4));
            _w.FlapRightRotation.BeginAnimation(RotateTransform.AngleProperty, To(0, 0.4));
            Loop(_w.OrbitRotation, RotateTransform.AngleProperty, 0, 360, 18, autoReverse: false, ease: false);
            SetProgress(1);
            Bounce();
            PopBadge(_w.SuccessBadge);
            _w.SuccessCheck.BeginAnimation(Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromSeconds(0.4)) { BeginTime = TimeSpan.FromSeconds(0.35) });
        }

        private void Bounce()
        {
            var bounce = new DoubleAnimationUsingKeyFrames();
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.18)), new QuadraticEase { EasingMode = EasingMode.EaseOut }));
            bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(0.6)), new BounceEase { Bounces = 2, Bounciness = 3, EasingMode = EasingMode.EaseOut }));
            _w.BoxShift.BeginAnimation(TranslateTransform.YProperty, bounce);
        }

        private void PopBadge(FrameworkElement badge)
        {
            var scale = (ScaleTransform)badge.RenderTransform;
            var pop = new DoubleAnimation(0.3, 1, TimeSpan.FromSeconds(0.5))
            {
                BeginTime = TimeSpan.FromSeconds(0.15),
                EasingFunction = new BackEase { Amplitude = 0.7, EasingMode = EasingMode.EaseOut }
            };
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, pop);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, pop);
            badge.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1, TimeSpan.FromSeconds(0.25)) { BeginTime = TimeSpan.FromSeconds(0.15) });
        }

        private void HideBadges()
        {
            _w.SuccessBadge.BeginAnimation(UIElement.OpacityProperty, To(0, 0.15));
            _w.ErrorBadge.BeginAnimation(UIElement.OpacityProperty, To(0, 0.15));
            _w.SuccessCheck.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
            _w.SuccessCheck.StrokeDashOffset = 10;
        }

        /// <summary>Celebration sparks around the box.</summary>
        private void Burst()
        {
            for (var index = 0; index < 14; index++)
            {
                var angle = index * Math.PI * 2 / 14 + _random.NextDouble() * 0.3;
                var size = 4 + _random.NextDouble() * 4;
                var spark = new Ellipse
                {
                    Width = size,
                    Height = size,
                    Fill = index % 2 == 0 ? StepRow.Frozen("#7FE9FF") : StepRow.Frozen("#FFD66B"),
                    IsHitTestVisible = false
                };
                var shift = new TranslateTransform();
                spark.RenderTransform = shift;
                Canvas.SetLeft(spark, 150 - size / 2);
                Canvas.SetTop(spark, 150 - size / 2);
                _w.Hero.Children.Add(spark);
                var distance = 95 + _random.NextDouble() * 45;
                var duration = TimeSpan.FromSeconds(0.9 + _random.NextDouble() * 0.4);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                shift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(0, Math.Cos(angle) * distance, duration) { EasingFunction = ease });
                shift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, Math.Sin(angle) * distance, duration) { EasingFunction = ease });
                var fade = new DoubleAnimation(1, 0, duration) { BeginTime = TimeSpan.FromSeconds(0.1) };
                fade.Completed += (sender, args) => _w.Hero.Children.Remove(spark);
                spark.BeginAnimation(UIElement.OpacityProperty, fade);
            }
        }

        private void StopArrow()
        {
            _w.ArrowShift.BeginAnimation(TranslateTransform.YProperty, null);
            _w.Arrow.BeginAnimation(UIElement.OpacityProperty, null);
            _w.FlapLeftRotation.BeginAnimation(RotateTransform.AngleProperty, null);
            _w.FlapRightRotation.BeginAnimation(RotateTransform.AngleProperty, null);
        }

        private static DoubleAnimation To(double value, double seconds) =>
            new DoubleAnimation(value, TimeSpan.FromSeconds(seconds)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };

        private static void Loop(Animatable target, DependencyProperty property, double from, double to, double seconds, bool autoReverse = true, bool ease = true)
        {
            var animation = new DoubleAnimation(from, to, TimeSpan.FromSeconds(seconds))
            {
                AutoReverse = autoReverse,
                RepeatBehavior = RepeatBehavior.Forever
            };
            if (ease)
            {
                animation.EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut };
            }

            target.BeginAnimation(property, animation);
        }
    }
}
