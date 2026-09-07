using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void MainWindowResponsiveSurfaceFillsArbitraryAspectRatiosWithoutLetterbox()
    {
        Exception? failure = null;
        var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            MainWindow? window = null;
            try
            {
                window = CreateUiTestWindow();
                window.Show();

                var timer = typeof(MainWindow)
                    .GetField("_timer", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)?
                    .GetValue(window) as DispatcherTimer;
                timer?.Stop();

                var sizes = new[]
                {
                    new Size(1088d, 643d),
                    new Size(1024d, 640d),
                    new Size(1440d, 900d),
                    new Size(1600d, 820d),
                    new Size(1280d, 1000d)
                };

                foreach (var size in sizes)
                {
                    window.WindowState = WindowState.Normal;
                    window.Width = size.Width;
                    window.Height = size.Height;
                    window.UpdateLayout();
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();

                    var viewbox = (Viewbox)window.FindName("MainViewbox");
                    var surface = (Grid)window.FindName("DesignSurface");

                    Assert.AreEqual(HorizontalAlignment.Stretch, viewbox.HorizontalAlignment,
                        "主视口必须横向占满客户区，不能继续居中留下纯色边带。");
                    Assert.AreEqual(VerticalAlignment.Stretch, viewbox.VerticalAlignment,
                        "主视口必须纵向占满客户区，不能继续居中留下纯色边带。");
                    Assert.AreEqual(Stretch.Uniform, viewbox.Stretch,
                        "响应式布局仍须保持统一缩放，避免圆形控件和文字被非等比拉伸。");
                    Assert.AreEqual(StretchDirection.DownOnly, viewbox.StretchDirection,
                        "大窗口应扩展真实布局面积，而不是把固定画布继续整体放大。");

                    Assert.IsTrue(viewbox.ActualWidth > 0d && viewbox.ActualHeight > 0d,
                        "主视口必须完成布局。 ");
                    Assert.IsTrue(surface.ActualWidth >= ResponsiveWindowSizing.MainDesignWidth - 0.5d,
                        "逻辑布局宽度不能小于基准设计宽度。 ");
                    Assert.IsTrue(surface.ActualHeight >= ResponsiveWindowSizing.MainDesignHeight - 0.5d,
                        "逻辑布局高度不能小于基准设计高度。 ");

                    var horizontalScale = viewbox.ActualWidth / surface.ActualWidth;
                    var verticalScale = viewbox.ActualHeight / surface.ActualHeight;
                    Assert.AreEqual(horizontalScale, verticalScale, 0.005d,
                        $"{size.Width}x{size.Height} 下横纵缩放必须一致，否则控件会变形。 ");
                    Assert.IsTrue(horizontalScale <= 1.005d,
                        "响应式布局不应在大窗口整体放大固定画布。 ");

                    var viewportRatio = viewbox.ActualWidth / viewbox.ActualHeight;
                    var surfaceRatio = surface.ActualWidth / surface.ActualHeight;
                    Assert.AreEqual(viewportRatio, surfaceRatio, 0.005d,
                        $"{size.Width}x{size.Height} 下设计面必须匹配视口宽高比，避免出现 letterbox 纯色边带。 ");
                }
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                window?.Close();
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(20)), "响应式主窗口渲染测试超时。 ");
        if (failure is not null)
        {
            Assert.Fail($"响应式主窗口布局验证失败：{failure}");
        }
    }
}
