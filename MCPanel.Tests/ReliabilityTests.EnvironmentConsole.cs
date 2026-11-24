using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

/// <summary>
/// Environment page in the ops-console layout: a component list, a status
/// summary and a detail panel for the selected component.
/// </summary>
public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow(600d)]
    [DataRow(760d)]
    [DataRow(1280d)]
    public void EnvironmentConsoleSummarisesStatesAndShowsTheSelectedComponent(double width)
    {
        RunEnvironmentConsoleUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var model = ShowEnvironmentConsole(window, width);
                Assert.AreEqual(3, model.EnvironmentRunningCount);
                Assert.AreEqual(1, model.EnvironmentStoppedCount);
                Assert.AreEqual(2, model.EnvironmentNotInstalledCount);
                Assert.AreEqual(0, model.EnvironmentBusyCount);

                var list = (ListBox)window.FindName("EnvironmentItemsControl");
                var host = (ContentControl)window.FindName("EnvironmentDetailHost");
                var page = (FrameworkElement)window.FindName("EnvironmentPage");
                foreach (var item in model.EnvironmentItems)
                {
                    list.SelectedItem = item;
                    PumpEnvironmentConsoleUi(window);
                    Assert.AreSame(item, model.SelectedEnvironmentItem);
                    Assert.AreSame(item, host.Content);
                    var detail = EnvironmentDetail(window);
                    AssertInside(detail, page, $"{item.Title} 的详情面板超出了页面（{width} DIP）。");

                    var credentialSection = item.IsInstalled && item.IsCredentialModule;
                    Assert.AreEqual(credentialSection, FindVisualChildren<ItemsControl>(detail)
                        .Any(control => control.IsVisible && control.ItemsSource is IEnumerable<CredentialField>));
                    var portSection = item.IsInstalled && item.IsTomcatModule;
                    Assert.AreEqual(portSection, FindVisualChildren<ItemsControl>(detail)
                        .Any(control => control.IsVisible && ReferenceEquals(control.ItemsSource, item.TomcatPorts)));
                }

                list.SelectedItem = model.EnvironmentItems.Single(item => item.Kind == EnvironmentKind.MySql);
                PumpEnvironmentConsoleUi(window);
                SaveEnvironmentConsoleScreenshot(window, $"environment-console-{width:0}.png");
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(760d)]
    [DataRow(1280d)]
    public void LongTomcatPortListScrollsInsideTheDetailPanel(double width)
    {
        RunEnvironmentConsoleUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var model = ShowEnvironmentConsole(window, width);
                var page = (FrameworkElement)window.FindName("EnvironmentPage");
                var pageHeight = page.ActualHeight;
                var tomcat = model.EnvironmentItems.Single(item => item.Kind == EnvironmentKind.Tomcat);
                model.SelectedEnvironmentItem = tomcat;
                for (var index = 0; index < 24; index++)
                {
                    var id = "console-layout-test-" + index;
                    tomcat.TomcatPorts.Add(new TomcatPortItem(new TomcatProductDeploymentInfo(
                        id, "Catalina" + (9000 + index), "Catalina", "webapps", "/" + id,
                        @"C:\MCPanel-layout-fixture", 9000 + index, "http://localhost/" + id)));
                }

                PumpEnvironmentConsoleUi(window);
                Assert.AreEqual(pageHeight, page.ActualHeight, 1d, "A long port list must not stretch the page.");
                var detail = EnvironmentDetail(window);
                var scroll = FindVisualChildren<ScrollViewer>(detail).First(viewer => viewer.IsVisible);
                Assert.IsTrue(scroll.ScrollableHeight > 0d, "Long port lists must scroll inside the detail panel.");
                scroll.ScrollToBottom();
                PumpEnvironmentConsoleUi(window);
                var lastEditor = FindVisualChildren<TextBox>(detail)
                    .Single(editor => ReferenceEquals(editor.DataContext, tomcat.TomcatPorts.Last()));
                var point = lastEditor.TransformToAncestor(scroll).Transform(new Point());
                Assert.IsTrue(point.Y >= -1d && point.Y + lastEditor.ActualHeight <= scroll.ActualHeight + 1d,
                    "The last port must be reachable inside the detail panel.");
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(EnvironmentKind.MySql)]
    [DataRow(EnvironmentKind.SqlServer)]
    public void LongConnectionValuesStayInsideTheDetailPanel(EnvironmentKind kind)
    {
        RunEnvironmentConsoleUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var model = ShowEnvironmentConsole(window, 760d);
                var component = model.EnvironmentItems.Single(item => item.Kind == kind);
                component.ApplyRuntimeState(new EnvironmentRuntimeState(true, true, "服务运行中", RuntimeStatusKind.Running));
                model.SelectedEnvironmentItem = component;
                PumpEnvironmentConsoleUi(window);
                var detail = EnvironmentDetail(window);
                var fields = FindVisualChildren<ItemsControl>(detail).Single(control => control.ItemsSource is IEnumerable<CredentialField>);
                fields.ItemsSource = Enumerable.Range(0, 12).Select(index =>
                    new CredentialField("测试字段 " + index, new string('x', 300), CredentialEditKind.Password)).ToArray();
                PumpEnvironmentConsoleUi(window);
                var scroll = FindVisualChildren<ScrollViewer>(detail).First(viewer => viewer.IsVisible);
                Assert.IsTrue(scroll.ScrollableHeight > 0d, $"Long connection values must scroll vertically (extent {scroll.ExtentHeight}, viewport {scroll.ViewportHeight}).");
                Assert.IsTrue(scroll.ExtentWidth <= scroll.ViewportWidth + 1d, $"Long connection values must not widen the panel ({scroll.ExtentWidth} > {scroll.ViewportWidth}).");
                foreach (var copy in FindVisualChildren<Button>(fields).Where(button => button.IsVisible && Equals(button.Content, "\uE8C8")).Take(2))
                {
                    var point = copy.TransformToAncestor(fields).Transform(new Point());
                    Assert.IsTrue(point.X + copy.ActualWidth <= fields.ActualWidth + 1d, "The copy action must stay visible.");
                }
            }
            finally { window.Close(); }
        });
    }

    /// <summary>
    /// Shows the environment page with IIS, Nginx and MySQL running, Tomcat
    /// stopped, and SQL Server and FRP not installed.
    /// </summary>
    private static MainViewModel ShowEnvironmentConsole(MainWindow window, double width)
    {
        window.Show();
        PumpEnvironmentConsoleUi(window);
        window.MinWidth = width;
        window.Width = width;
        window.Height = 640d;
        var model = (MainViewModel)window.DataContext;
        foreach (var item in model.EnvironmentItems)
        {
            var state = item.Kind switch
            {
                EnvironmentKind.Iis or EnvironmentKind.Nginx or EnvironmentKind.MySql =>
                    new EnvironmentRuntimeState(true, true, "服务运行中", RuntimeStatusKind.Running),
                EnvironmentKind.Tomcat => new EnvironmentRuntimeState(true, false, "服务未运行", RuntimeStatusKind.Stopped),
                _ => new EnvironmentRuntimeState(false, false, "尚未安装", RuntimeStatusKind.NotInstalled)
            };
            item.ApplyRuntimeState(state);
        }

        FindVisualChildren<RadioButton>(window).Single(button => Equals(button.CommandParameter, "Environment")).IsChecked = true;
        PumpEnvironmentConsoleUi(window);
        Assert.AreEqual(width, window.ActualWidth, 1d);
        return model;
    }

    private static FrameworkElement EnvironmentDetail(MainWindow window) =>
        FindVisualChildren<Border>((ContentControl)window.FindName("EnvironmentDetailHost"))
            .Single(border => border.Name == "EnvironmentDetail");

    private static void AssertInside(FrameworkElement element, FrameworkElement container, string message)
    {
        var point = element.TransformToAncestor(container).Transform(new Point());
        Assert.IsTrue(element.ActualWidth > 100d && element.ActualHeight > 100d, message + " (尺寸过小)");
        Assert.IsTrue(point.X >= -1d && point.Y >= -1d &&
            point.X + element.ActualWidth <= container.ActualWidth + 1d &&
            point.Y + element.ActualHeight <= container.ActualHeight + 1d, message);
    }

    private static void SaveEnvironmentConsoleScreenshot(MainWindow window, string name)
    {
        var bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "artifacts", "audit"));
        Directory.CreateDirectory(output);
        using var stream = File.Create(Path.Combine(output, name));
        encoder.Save(stream);
    }

    private static void PumpEnvironmentConsoleUi(MainWindow window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void RunEnvironmentConsoleUiTest(Action action)
    {
        Exception? failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext());
            try { action(); }
            catch (Exception error) { failure = error; }
            finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); completed.Set(); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(45)), "Environment console UI regression timed out.");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
