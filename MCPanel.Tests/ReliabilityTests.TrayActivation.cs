using System;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [TestMethod]
    public void TrayActivationIgnoresReentryWhileWindowSourceIsCreated()
    {
        RunTraySta(() =>
        {
            var controller = new WindowActivationController();
            var creates = 0;
            var activations = 0;
            Exception? error = null;
            Action? request = null;
            request = () => controller.Show(() =>
            {
                creates++;
                request!(); // Construction can also pump messages before publication.
                var window = CreateUiTestWindow();
                // SourceInitialized runs inside Show, before it has completed.
                window.SourceInitialized += (_, _) => request!();
                return window;
            }, window =>
            {
                activations++;
                App.RestoreAndActivate(window);
            }, window => ((MainWindow)window).CloseAfterActivationFailure(), ex => error = ex);
            try
            {
                request();
                Assert.IsNull(error, error?.ToString());
                Assert.AreEqual(1, creates);
                Assert.AreEqual(1, activations, "不能在首次 Show 尚未结束时再次激活窗口。");
                Assert.IsTrue(controller.CurrentWindow!.IsVisible);
                controller.CurrentWindow.Hide();
                request();
                Assert.AreEqual(1, creates, "托盘恢复应复用原窗口。");
                Assert.AreEqual(2, activations);
                Assert.IsTrue(controller.CurrentWindow.IsVisible);
            }
            finally { controller.CurrentWindow?.Close(); }
            Assert.IsNull(controller.CurrentWindow);
        });
    }

    [TestMethod]
    public void TrayActivationClosesFailedWindowAndAllowsRetryAfterErrorDialog()
    {
        RunTraySta(() =>
        {
            var controller = new WindowActivationController();
            var failedWindow = CreateUiTestWindow();
            var closed = false;
            failedWindow.Closed += (_, _) => closed = true;
            var expected = new ArgumentException("模拟显示阶段异常");
            var reports = 0;
            controller.Show(() => failedWindow, window =>
            {
                window.Show();
                throw expected;
            }, window => ((MainWindow)window).CloseAfterActivationFailure(), error =>
            {
                reports++;
                Assert.AreSame(expected, error);
                Assert.IsTrue(closed);
                Assert.IsFalse(failedWindow.IsVisible);
                Assert.IsNull(controller.CurrentWindow);
                // An error dialog pumps the dispatcher too; activation is still blocked.
                controller.Show(() => throw new AssertFailedException("错误弹窗期间不应创建窗口"),
                    _ => Assert.Fail(), _ => Assert.Fail(), _ => Assert.Fail());
            });
            Assert.AreEqual(1, reports);
            try
            {
                controller.Show(CreateUiTestWindow, App.RestoreAndActivate,
                    window => ((MainWindow)window).CloseAfterActivationFailure(), error => Assert.Fail(error.ToString()));
                Assert.IsNotNull(controller.CurrentWindow);
                Assert.AreNotSame(failedWindow, controller.CurrentWindow);
                Assert.IsTrue(controller.CurrentWindow!.IsVisible);
            }
            finally { controller.CurrentWindow?.Close(); }
        });
    }

    [TestMethod]
    public void TrayRestorePreservesMaximizedStateAndFillsViewport()
    {
        RunTraySta(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                App.RestoreAndActivate(window);
                window.WindowState = WindowState.Maximized;
                window.Hide();
                App.RestoreAndActivate(window);
                Assert.AreEqual(WindowState.Maximized, window.WindowState);
                window.WindowState = WindowState.Normal;
                foreach (var width in new[] { 1024d, 1440d, 1280d })
                {
                    window.Hide();
                    window.Width = width;
                    window.Height = 720;
                    App.RestoreAndActivate(window);
                    window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                    window.UpdateLayout();
                    var viewport = (Viewbox)window.FindName("MainViewbox");
                    var surface = (Grid)window.FindName("DesignSurface");
                    Assert.AreEqual(viewport.ActualWidth, surface.ActualWidth, 1d);
                    Assert.AreEqual(viewport.ActualHeight, surface.ActualHeight, 1d);
                }
                window.WindowState = WindowState.Minimized;
                App.RestoreAndActivate(window);
                Assert.AreEqual(WindowState.Normal, window.WindowState);
            }
            finally { window.Close(); }
        });
    }

    private static void RunTraySta(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { completed.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(45)), "托盘窗口测试超时。");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
