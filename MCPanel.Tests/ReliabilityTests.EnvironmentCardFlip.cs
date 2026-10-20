using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace MCPanel.Tests;

public sealed partial class ReliabilityTests
{
    [DataTestMethod]
    [DataRow(600d)]
    [DataRow(760d)]
    [DataRow(1280d)]
    public void TomcatBackContentCannotStretchEnvironmentCards(double width)
    {
        RunEnvironmentCardUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var items = ShowInstalledEnvironmentCards(window, width);
                var cards = EnvironmentCardBorders(items);
                var before = CaptureEnvironmentCardBounds(items);
                var outerScroll = FindVisualChildren<ScrollViewer>((Grid)window.FindName("EnvironmentPage")).First();
                var beforeExtent = outerScroll.ExtentHeight;
                var tomcat = (EnvironmentItem)cards.Single(card => ((EnvironmentItem)card.DataContext).Kind == EnvironmentKind.Tomcat).DataContext;
                var tomcatCard = cards.Single(card => ReferenceEquals(card.DataContext, tomcat));
                for (var index = 0; index < 24; index++)
                {
                    var id = "card-layout-test-" + index;
                    tomcat.TomcatPorts.Add(new TomcatPortItem(new TomcatProductDeploymentInfo(
                        id, "Catalina" + (9000 + index), "Catalina", "webapps", "/" + id,
                        @"C:\MCPanel-layout-fixture", 9000 + index, "http://localhost/" + id)));
                }

                foreach (var flipped in new[] { false, true, false, true })
                {
                    tomcat.IsFlipped = flipped;
                    PumpEnvironmentCardUi(window);
                    AssertEnvironmentCardBounds(before, CaptureEnvironmentCardBounds(items));
                    Assert.AreEqual(beforeExtent, outerScroll.ExtentHeight, 1d,
                        "Back content must not enlarge the outer page's scrolling extent.");
                    if (!flipped) continue;
                    var innerScroll = FindVisualChildren<ScrollViewer>(tomcatCard).Single(scroll =>
                        scroll.IsVisible && scroll.Content is ItemsControl content && ReferenceEquals(content.ItemsSource, tomcat.TomcatPorts));
                    Assert.IsTrue(innerScroll.ViewportHeight > 20d && innerScroll.ScrollableHeight > 0d,
                        "Long port lists must scroll inside the card.");
                    innerScroll.ScrollToBottom();
                    PumpEnvironmentCardUi(window);
                    Assert.AreEqual(innerScroll.ScrollableHeight, innerScroll.VerticalOffset, 1d);
                    var lastEditor = FindVisualChildren<TextBox>(tomcatCard)
                        .Single(editor => ReferenceEquals(editor.DataContext, tomcat.TomcatPorts.Last()));
                    var point = lastEditor.TransformToAncestor(innerScroll).Transform(new Point());
                    Assert.IsTrue(point.Y >= -1d && point.Y + lastEditor.ActualHeight <= innerScroll.ActualHeight + 1d,
                        "The last port must remain reachable within the card's scroll viewport.");
                }

                window.Width = width == 1280d ? 600d : 1280d;
                PumpEnvironmentCardUi(window);
                window.Width = width;
                PumpEnvironmentCardUi(window);
                AssertEnvironmentCardBounds(before, CaptureEnvironmentCardBounds(items));
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(EnvironmentKind.MySql)]
    [DataRow(EnvironmentKind.SqlServer)]
    [DataRow(EnvironmentKind.Tomcat)]
    public void ActualFlipClicksPreserveCardBoundsAndIgnoreReentry(EnvironmentKind kind)
    {
        RunEnvironmentCardUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var items = ShowInstalledEnvironmentCards(window, 1280d);
                var card = EnvironmentCardBorders(items).Single(element => ((EnvironmentItem)element.DataContext).Kind == kind);
                var item = (EnvironmentItem)card.DataContext;
                var before = CaptureEnvironmentCardBounds(items);
                for (var attempt = 0; attempt < 4; attempt++)
                {
                    var initialFace = item.IsFlipped;
                    var flip = FindVisualChildren<Button>(card).Single(button => button.IsVisible && Equals(button.Content, "\uE8AB"));
                    flip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    Assert.IsTrue(item.IsFlipAnimating, "The actual template click must start the flip handler.");
                    flip.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                    WaitForEnvironmentCardFlip(window, item);
                    Assert.AreEqual(!initialFace, item.IsFlipped, "A second click during the animation must be ignored.");
                    AssertEnvironmentCardBounds(before, CaptureEnvironmentCardBounds(items));
                    var scale = (ScaleTransform)card.RenderTransform;
                    Assert.AreEqual(1d, scale.ScaleX, 0.001d);
                    Assert.AreEqual(1d, scale.ScaleY, 0.001d);
                }
            }
            finally { window.Close(); }
        });
    }

    [DataTestMethod]
    [DataRow(EnvironmentKind.MySql)]
    [DataRow(EnvironmentKind.SqlServer)]
    public void CredentialBackFaceKeepsLongValuesWithinCard(EnvironmentKind kind)
    {
        RunEnvironmentCardUiTest(() =>
        {
            var window = CreateUiTestWindow();
            try
            {
                var items = ShowInstalledEnvironmentCards(window, 760d);
                var card = EnvironmentCardBorders(items).Single(element => ((EnvironmentItem)element.DataContext).Kind == kind);
                var item = (EnvironmentItem)card.DataContext;
                var before = CaptureEnvironmentCardBounds(items);
                var fields = FindVisualChildren<ItemsControl>(card).Single(control => control.ItemsSource is IEnumerable<CredentialField>);
                fields.ItemsSource = Enumerable.Range(0, 12).Select(index =>
                    new CredentialField("测试字段 " + index, new string('x', 300), CredentialEditKind.Password)).ToArray();
                item.IsFlipped = true;
                PumpEnvironmentCardUi(window);
                AssertEnvironmentCardBounds(before, CaptureEnvironmentCardBounds(items));
                var scroll = FindVisualChildren<ScrollViewer>(card).Single(viewer => viewer.IsVisible);
                Assert.IsTrue(scroll.ScrollableHeight > 0d && scroll.ViewportWidth > 0d);
                scroll.ScrollToBottom();
                PumpEnvironmentCardUi(window);
                Assert.AreEqual(scroll.ScrollableHeight, scroll.VerticalOffset, 1d);
                var connect = FindVisualChildren<Button>(card).Single(button => button.IsVisible && Equals(button.Content, "连接工具"));
                var point = connect.TransformToAncestor(card).Transform(new Point());
                Assert.IsTrue(point.X >= 0d && point.Y >= 0d &&
                    point.X + connect.ActualWidth <= card.ActualWidth + 1d &&
                    point.Y + connect.ActualHeight <= card.ActualHeight + 1d,
                    "The connection action must remain inside the card.");
            }
            finally { window.Close(); }
        });
    }

    private static ItemsControl ShowInstalledEnvironmentCards(MainWindow window, double width)
    {
        window.Show();
        PumpEnvironmentCardUi(window);
        // SourceInitialized fits the initial window to the monitor; apply the test
        // viewport afterwards so every data row exercises its requested layout.
        window.Width = width;
        window.Height = 640d;
        PumpEnvironmentCardUi(window);
        Assert.AreEqual(width, window.ActualWidth, 1d);
        Assert.AreEqual(640d, window.ActualHeight, 1d);
        Assert.AreEqual(width == 600d ? 1 : width == 760d ? 2 : 3,
            ((MainViewModel)window.DataContext).EnvironmentColumnCount);
        foreach (var item in ((MainViewModel)window.DataContext).EnvironmentItems) item.IsInstalled = true;
        FindVisualChildren<RadioButton>(window).Single(button => Equals(button.CommandParameter, "Environment")).IsChecked = true;
        PumpEnvironmentCardUi(window);
        return (ItemsControl)window.FindName("EnvironmentItemsControl");
    }

    private static Border[] EnvironmentCardBorders(DependencyObject items) =>
        FindVisualChildren<Border>(items).Where(border => border.Name == "EnvironmentCard").ToArray();

    private static Dictionary<EnvironmentKind, Rect> CaptureEnvironmentCardBounds(ItemsControl items) =>
        EnvironmentCardBorders(items).ToDictionary(card => ((EnvironmentItem)card.DataContext).Kind,
            card => new Rect(card.TranslatePoint(new Point(), items), card.RenderSize));

    private static void AssertEnvironmentCardBounds(Dictionary<EnvironmentKind, Rect> expected, Dictionary<EnvironmentKind, Rect> actual)
    {
        Assert.AreEqual(6, actual.Count);
        foreach (var pair in expected)
        {
            var current = actual[pair.Key];
            Assert.AreEqual(pair.Value.Width, current.Width, 1d, pair.Key + " width changed after flipping.");
            Assert.AreEqual(pair.Value.Height, current.Height, 1d, pair.Key + " height changed after flipping.");
            Assert.AreEqual(pair.Value.X, current.X, 1d, pair.Key + " horizontal position changed after flipping.");
            Assert.AreEqual(pair.Value.Y, current.Y, 1d, pair.Key + " vertical position changed after flipping.");
        }
    }

    private static void PumpEnvironmentCardUi(MainWindow window)
    {
        window.UpdateLayout();
        window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
        window.UpdateLayout();
    }

    private static void WaitForEnvironmentCardFlip(MainWindow window, EnvironmentItem item)
    {
        var frame = new DispatcherFrame();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(20) };
        timer.Tick += (_, _) => { if (!item.IsFlipAnimating || DateTime.UtcNow >= deadline) frame.Continue = false; };
        timer.Start();
        try { Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        Assert.IsFalse(item.IsFlipAnimating, "Card animation did not finish.");
        PumpEnvironmentCardUi(window);
    }

    private static void RunEnvironmentCardUiTest(Action action)
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
        Assert.IsTrue(completed.Wait(TimeSpan.FromSeconds(45)), "Environment card UI regression timed out.");
        if (failure is not null) Assert.Fail(failure.ToString());
    }
}
