from pathlib import Path


def read(path):
    return Path(path).read_text(encoding="utf-8-sig").replace("\r\n", "\n")


def write(path, text):
    Path(path).write_text(text, encoding="utf-8", newline="\n")


def replace_once(text, old, new, label):
    count = text.count(old)
    if count != 1:
        raise RuntimeError(f"{label}: expected one occurrence, found {count}")
    return text.replace(old, new, 1)


# MainWindow.xaml: move the queue surface out of WPF Popup and into ProductsPage.
xaml = read("MainWindow.xaml")
xaml = replace_once(
    xaml,
    '''                <Grid x:Name="ProductsPage" Visibility="Collapsed"
                      Focusable="True"
                      KeyboardNavigation.TabNavigation="Local">''',
    '''                <Grid x:Name="ProductsPage" Visibility="Collapsed"
                      Focusable="True"
                      ClipToBounds="True"
                      IsVisibleChanged="ProductsPage_IsVisibleChanged"
                      PreviewKeyDown="ProductsPage_PreviewKeyDown"
                      KeyboardNavigation.TabNavigation="Local">''',
    "ProductsPage declaration")

popup_start = xaml.find('        <Popup x:Name="DownloadQueuePopup"')
if popup_start < 0:
    raise RuntimeError("DownloadQueuePopup block not found")
popup_open_end = xaml.find(">\n", popup_start)
popup_close_start = xaml.find("        </Popup>", popup_open_end)
if popup_open_end < 0 or popup_close_start < 0:
    raise RuntimeError("DownloadQueuePopup boundaries not found")
popup_open_end += 2
popup_end = popup_close_start + len("        </Popup>")
if popup_end < len(xaml) and xaml[popup_end] == "\n":
    popup_end += 1

inner = xaml[popup_open_end:popup_close_start]
inner = inner.replace("DownloadQueuePopupHost", "DownloadQueueFlyoutHost")
inner = inner.replace("DownloadQueuePopupCard", "DownloadQueueFlyoutCard")
inner = inner.replace('MaxHeight="560"', 'MaxHeight="540"', 1)
inner = inner.replace('RenderTransformOrigin="0.5,0"', 'RenderTransformOrigin="0.66,0"', 1)
xaml = xaml[:popup_start] + xaml[popup_end:]

flyout = '''                    <Grid x:Name="DownloadQueueFlyoutLayer"
                          Grid.RowSpan="3"
                          Panel.ZIndex="200"
                          Width="460"
                          HorizontalAlignment="Right"
                          VerticalAlignment="Top"
                          Margin="0,86,16,0"
                          Visibility="Collapsed"
                          Focusable="False">
''' + inner + '''                    </Grid>
'''
insert_marker = '''                   </ListBox>
                </Grid>

                <Grid x:Name="EnvironmentPage"'''
replacement = '''                   </ListBox>
''' + flyout + '''                </Grid>

                <Grid x:Name="EnvironmentPage"'''
xaml = replace_once(xaml, insert_marker, replacement, "ProductsPage flyout insertion")
if '<Popup x:Name="DownloadQueuePopup"' in xaml:
    raise RuntimeError("Native queue Popup still present after refactor")
write("MainWindow.xaml", xaml)


# MainWindow.Products.cs: replace native-popup positioning with an in-window visibility/animation policy.
products = read("MainWindow.Products.cs")
start = products.find("    private void DownloadQueueButton_Click")
end = products.find("    private void HideInstallationProgress_Click", start)
if start < 0 or end < 0:
    raise RuntimeError("Queue popup methods block not found in MainWindow.Products.cs")
new_block = r'''    private bool IsDownloadQueueFlyoutOpen =>
        DownloadQueueFlyoutLayer.Visibility == Visibility.Visible;

    private void DownloadQueueButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsDownloadQueueFlyoutOpen)
        {
            CloseDownloadQueueFlyout();
            return;
        }

        _model.InstallationProgress.ShowCompletedQueue = false;
        OpenDownloadQueueFlyout();
    }

    private void DownloadQueueButton_PreviewMouseLeftButtonDown(
        object sender,
        MouseButtonEventArgs e)
    {
        // Treat a physical double-click as one toggle. This avoids WPF raising
        // two Click events (open, then immediately close), which is especially
        // easy to perceive as "no response" over a remote desktop connection.
        if (e.ClickCount > 1)
        {
            e.Handled = true;
        }
    }

    private void MainWindow_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (ShouldCloseDownloadQueueFlyout(
                IsDownloadQueueFlyoutOpen,
                DownloadQueueButton.IsMouseOver,
                DownloadQueueFlyoutCard.IsMouseOver))
        {
            CloseDownloadQueueFlyout();
        }
    }

    private void ProductsPage_IsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!ProductsPage.IsVisible)
        {
            CloseDownloadQueueFlyout();
        }
    }

    private void ProductsPage_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && IsDownloadQueueFlyoutOpen)
        {
            CloseDownloadQueueFlyout();
            e.Handled = true;
        }
    }

    private void OpenDownloadQueueFlyout()
    {
        DownloadQueueFlyoutLayer.Visibility = Visibility.Visible;
        DownloadQueueFlyoutCard.BeginAnimation(UIElement.OpacityProperty, null);
        DownloadQueueFlyoutCard.Opacity = 0d;

        if (DownloadQueueFlyoutCard.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 0.94d;
            scale.ScaleY = 0.94d;

            var duration = TimeSpan.FromMilliseconds(170);
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
            scale.BeginAnimation(
                ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94d, 1d, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
            scale.BeginAnimation(
                ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94d, 1d, duration)
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
            DownloadQueueFlyoutCard.BeginAnimation(
                UIElement.OpacityProperty,
                new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(130))
                {
                    EasingFunction = easing,
                    FillBehavior = FillBehavior.HoldEnd
                });
        }
        else
        {
            DownloadQueueFlyoutCard.Opacity = 1d;
        }
    }

    private void CloseDownloadQueueFlyout()
    {
        if (DownloadQueueFlyoutLayer.Visibility != Visibility.Visible)
        {
            return;
        }

        DownloadQueueFlyoutCard.BeginAnimation(UIElement.OpacityProperty, null);
        DownloadQueueFlyoutCard.Opacity = 1d;
        if (DownloadQueueFlyoutCard.RenderTransform is ScaleTransform scale)
        {
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
            scale.ScaleX = 1d;
            scale.ScaleY = 1d;
        }

        DownloadQueueFlyoutLayer.Visibility = Visibility.Collapsed;
    }

'''
products = products[:start] + new_block + products[end:]
if "DownloadQueuePopup" in products:
    raise RuntimeError("MainWindow.Products.cs still contains native popup references")
write("MainWindow.Products.cs", products)


# MainWindow.xaml.cs: remove native Popup callback/reposition lifecycle.
main = read("MainWindow.xaml.cs")
main = main.replace("    private bool _downloadQueuePopupPlacementRefreshPending;\n", "", 1)
main = main.replace("        DownloadQueuePopup.CustomPopupPlacementCallback = PlaceDownloadQueuePopup;\n", "", 1)
main = replace_once(
    main,
    '''        StateChanged += (_, _) =>
        {
            UpdateWindowStateChrome();
            RequestDownloadQueuePopupPlacementRefresh();
        };
        SizeChanged += (_, _) => RequestDownloadQueuePopupPlacementRefresh();
        LocationChanged += (_, _) => RequestDownloadQueuePopupPlacementRefresh();
''',
    '''        StateChanged += (_, _) => UpdateWindowStateChrome();
''',
    "popup reposition window events")
main = main.replace("            DownloadQueuePopup.IsOpen = false;\n", "            DownloadQueueFlyoutLayer.Visibility = Visibility.Collapsed;\n", 1)
placement_start = main.find("    private CustomPopupPlacement[] PlaceDownloadQueuePopup")
placement_end = main.find("    private static void WriteRuntimeRefreshError", placement_start)
if placement_start < 0 or placement_end < 0:
    raise RuntimeError("Popup placement helper block not found in MainWindow.xaml.cs")
main = main[:placement_start] + main[placement_end:]
if "DownloadQueuePopup" in main or "PlaceDownloadQueuePopup" in main or "CustomPopupPlacementCallback" in main:
    raise RuntimeError("MainWindow.xaml.cs still contains popup placement references")
write("MainWindow.xaml.cs", main)


# Keep toolbar visual normalization, but remove legacy Popup-specific input rerouting.
fix_source = r'''using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace MCPanel;

public partial class MainWindow
{
    internal const double DownloadQueueToolbarHostHeight = 52d;
    internal const double DownloadQueueProgressRingSize = 50d;
    internal const double DownloadQueueProgressRingStroke = 2d;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += (_, _) => NormalizeDownloadQueueToolbarVisuals();
    }

    internal static bool ShouldCloseDownloadQueueFlyout(
        bool isOpen,
        bool toolbarButtonHovered,
        bool flyoutHovered) =>
        isOpen && !toolbarButtonHovered && !flyoutHovered;

    private void NormalizeDownloadQueueToolbarVisuals()
    {
        if (DownloadQueueButton is null)
        {
            return;
        }

        // ProductToolbarIconButton intentionally uses Segoe MDL2 Assets for
        // icon-only buttons. Keep the nested numeric badge on the normal UI font.
        DownloadQueueButton.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

        if (VisualTreeHelper.GetParent(DownloadQueueButton) is not Grid host)
        {
            return;
        }

        host.Height = DownloadQueueToolbarHostHeight;
        host.ClipToBounds = false;

        foreach (UIElement child in host.Children)
        {
            if (child is not CircularProgress ring)
            {
                continue;
            }

            ring.Width = DownloadQueueProgressRingSize;
            ring.Height = DownloadQueueProgressRingSize;
            ring.StrokeThickness = DownloadQueueProgressRingStroke;
            ring.SnapsToDevicePixels = true;
            ring.UseLayoutRounding = true;
        }
    }
}
'''
write("MainWindow.DownloadQueueFix.cs", fix_source)


# Update/add regression coverage for the new in-window flyout semantics.
tests = read("MCPanel.Tests/ReliabilityTests.DownloadQueueUiFix.cs")
tests = tests.replace(
    "DownloadQueuePopupClosePolicyKeepsToolbarAndPopupClicksInside",
    "DownloadQueueFlyoutClosePolicyKeepsToolbarAndFlyoutClicksInside")
tests = tests.replace("ShouldCloseDownloadQueuePopup", "ShouldCloseDownloadQueueFlyout")
tests = tests.replace(
    '        StringAssert.Contains(source, "PreviewMouseLeftButtonDown -= MainWindow_PreviewMouseLeftButtonDown;");\n        StringAssert.Contains(source, "popupChild.IsMouseOver");\n',
    '        Assert.IsFalse(source.Contains("DownloadQueuePopup"), "工具栏修复层不应再依赖独立 WPF Popup。");\n        StringAssert.Contains(source, "ShouldCloseDownloadQueueFlyout");\n')
new_test = r'''

    [TestMethod]
    public void DownloadQueueUsesInWindowFlyoutInsteadOfNativePopup()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        var products = ReadRepositoryFile("MainWindow.Products.cs");
        var main = ReadRepositoryFile("MainWindow.xaml.cs");

        StringAssert.Contains(xaml, "x:Name=\"DownloadQueueFlyoutLayer\"");
        StringAssert.Contains(xaml, "x:Name=\"DownloadQueueFlyoutCard\"");
        StringAssert.Contains(xaml, "Panel.ZIndex=\"200\"");
        StringAssert.Contains(xaml, "ClipToBounds=\"True\"");
        Assert.IsFalse(xaml.Contains("<Popup x:Name=\"DownloadQueuePopup\""),
            "下载队列必须留在 MainWindow 视觉树内，不能再创建独立 HWND Popup。");
        StringAssert.Contains(products, "DownloadQueueFlyoutLayer.Visibility = Visibility.Visible;");
        StringAssert.Contains(products, "DownloadQueueFlyoutCard.IsMouseOver");
        StringAssert.Contains(products, "e.Key == Key.Escape");
        Assert.IsFalse(products.Contains("DownloadQueuePopup"));
        Assert.IsFalse(main.Contains("CustomPopupPlacementCallback"));
        Assert.IsFalse(main.Contains("PlaceDownloadQueuePopup"));
    }
'''
insert_at = tests.rfind("\n}")
if insert_at < 0:
    raise RuntimeError("Could not append flyout regression test")
tests = tests[:insert_at] + new_test + tests[insert_at:]
write("MCPanel.Tests/ReliabilityTests.DownloadQueueUiFix.cs", tests)
