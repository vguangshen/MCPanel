using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow(1920d, 1040d, 1.0d, DisplayName = "1080p at 100%")]
    [DataRow(1536d, 824d, 1.0d, DisplayName = "1080p at 125% stays readable")]
    [DataRow(1366d, 728d, 1.0d, DisplayName = "1366x768")]
    [DataRow(2560d, 1400d, 1.30d, DisplayName = "1440p at 100%")]
    [DataRow(2560d, 1392d, 1.30d, DisplayName = "4K at 150%")]
    [DataRow(2048d, 1112d, 1.05d, DisplayName = "1440p at 125%")]
    [DataRow(3440d, 1400d, 1.55d, DisplayName = "3440x1440 ultrawide")]
    [DataRow(3840d, 2120d, 2.0d, DisplayName = "4K at 100%")]
    [DataRow(5120d, 2840d, 2.0d, DisplayName = "5K capped")]
    public void InterfaceScaleFollowsTheScreenLikeTheGoldenWindow(double workWidth, double workHeight, double expected)
    {
        var workArea = new Rect(0, 0, workWidth, workHeight);
        var scale = ResponsiveWindowSizing.UiScale(workArea);
        Assert.AreEqual(expected, scale, 0.001d);

        // Where the screen is large enough to scale, the scaled window shows the
        // same logical size as the golden window on the reference screen.
        if (scale > 1d && scale < ResponsiveWindowSizing.MaximumUiScale)
        {
            var reference = ResponsiveWindowSizing.GoldenBounds(new Rect(new Point(), ResponsiveWindowSizing.ReferenceWorkArea));
            var bounds = ResponsiveWindowSizing.GoldenBounds(workArea);
            Assert.AreEqual(reference.Width, bounds.Width / scale, reference.Width * 0.05d,
                "Scaled content must keep the reference proportions inside the golden window.");
        }
    }

    [TestMethod]
    public void ScaledWindowUsesTheLayoutOfItsLogicalSizeAndFillsTheWindow()
    {
        RunWebsiteUiTest(() =>
        {
            var scaled = CreateUiTestWindow();
            var plain = CreateUiTestWindow();
            try
            {
                const double scale = 1.3d;
                const double logicalWidth = 740d; // 962 x 624 scaled: fits a 1024x768 runner screen
                const double logicalHeight = 480d;
                scaled.Show();
                scaled.ApplyUiScale(scale);
                scaled.Width = logicalWidth * scale;
                scaled.Height = logicalHeight * scale;
                scaled.UpdateLayout();
                scaled.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                scaled.UpdateLayout();

                // A small screen (CI runners) can clamp the scaled window; compare with
                // a plain window of the logical size the scaled window really got.
                plain.Show();
                plain.Width = scaled.ActualWidth / scale;
                plain.Height = scaled.ActualHeight / scale;
                plain.UpdateLayout();
                plain.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                plain.UpdateLayout();

                var surface = (FrameworkElement)scaled.FindName("DesignSurface");
                var viewbox = (FrameworkElement)scaled.FindName("MainViewbox");
                Assert.AreEqual(scale, ((ScaleTransform)surface.LayoutTransform).ScaleX, 0.001d);
                Assert.AreEqual(viewbox.ActualWidth / scale, surface.ActualWidth, 1d, "The surface is laid out at the logical width.");
                Assert.AreEqual(viewbox.ActualHeight / scale, surface.ActualHeight, 1d, "The surface is laid out at the logical height.");

                var scaledModel = (MainViewModel)scaled.DataContext;
                var plainModel = (MainViewModel)plain.DataContext;
                Assert.AreEqual(plainModel.EnvironmentColumnCount, scaledModel.EnvironmentColumnCount);
                Assert.AreEqual(plainModel.HomeServiceColumnCount, scaledModel.HomeServiceColumnCount);
                Assert.AreEqual(ResponsiveDensity.GetIsCompact(plain), ResponsiveDensity.GetIsCompact(scaled));
                Assert.AreEqual(ResponsiveDensity.GetIsNarrow(plain), ResponsiveDensity.GetIsNarrow(scaled));
                bool IconOnly(Window window) => ((StackPanel)window.FindName("NavItemsPanel")).Children
                    .OfType<RadioButton>().All(button => string.IsNullOrEmpty(button.Content as string));
                Assert.AreEqual(IconOnly(plain), IconOnly(scaled), "Navigation must collapse at the same logical width.");

                var home = (ScrollViewer)scaled.FindName("HomePage");
                Assert.IsTrue(home.ExtentWidth <= home.ViewportWidth + 2d, "A scaled page must not overflow horizontally.");

                scaled.ApplyUiScale(1d);
                scaled.UpdateLayout();
                Assert.AreEqual(viewbox.ActualWidth, surface.ActualWidth, 1d, "Returning to scale 1 restores the plain layout.");
            }
            finally
            {
                scaled.Close();
                plain.Close();
                PanelUiScale.Set(1d);
            }
        });
    }
}
