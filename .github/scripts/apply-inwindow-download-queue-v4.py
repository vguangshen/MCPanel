from pathlib import Path

v3_path = Path('.github/scripts/apply-inwindow-download-queue-v3.py')
exec(compile(v3_path.read_text(encoding='utf-8-sig'), str(v3_path), 'exec'))


def read(path):
    return Path(path).read_text(encoding='utf-8-sig').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


# Fix named arguments in the close-policy regression.
ui_fix = read('MCPanel.Tests/ReliabilityTests.DownloadQueueUiFix.cs')
ui_fix = ui_fix.replace('popupHovered:', 'flyoutHovered:')
write('MCPanel.Tests/ReliabilityTests.DownloadQueueUiFix.cs', ui_fix)

# Migrate older Popup rendering tests to the in-window flyout surface.
ui = read('MCPanel.Tests/ReliabilityTests.Ui.cs')

old_alignment = '''    [TestMethod]
    public void DownloadQueuePopupRightEdgeRemainsAlignedAfterViewboxScaling()
    {
        const double targetWidth = 1060d;
        const double popupWidth = 460d;
        const double scaleX = 0.8d;

        var left = MainWindow.CalculateDownloadQueuePopupLeft(
            targetWidth,
            popupWidth,
            scaleX);

        var popupRightOnScreen = left * scaleX + popupWidth;
        var targetRightOnScreen = targetWidth * scaleX;
        Assert.AreEqual(targetRightOnScreen, popupRightOnScreen, 0.001d);
    }
'''
new_alignment = '''    [TestMethod]
    public void DownloadQueueFlyoutIsHostedInsideProductsPage()
    {
        var xaml = ReadRepositoryFile("MainWindow.xaml");
        StringAssert.Contains(xaml, "x:Name=\\\"DownloadQueueFlyoutLayer\\\"");
        StringAssert.Contains(xaml, "Grid.RowSpan=\\\"3\\\"");
        StringAssert.Contains(xaml, "HorizontalAlignment=\\\"Right\\\"");
        StringAssert.Contains(xaml, "ClipToBounds=\\\"True\\\"");
        Assert.IsFalse(xaml.Contains("<Popup x:Name=\\\"DownloadQueuePopup\\\""));
    }
'''
if ui.count(old_alignment) != 1:
    raise RuntimeError(f'Expected one obsolete popup alignment test, found {ui.count(old_alignment)}')
ui = ui.replace(old_alignment, new_alignment, 1)

ui = ui.replace('public void DownloadQueuePopupTemplateRendersReadOnlyQueueRows()',
                'public void DownloadQueueFlyoutTemplateRendersReadOnlyQueueRows()', 1)
ui = ui.replace('public void DownloadQueuePopupDeleteActionReachesMainWindowQueueService()',
                'public void DownloadQueueFlyoutDeleteActionReachesMainWindowQueueService()', 1)

# There are exactly two legacy Popup locals in these queue UI tests.
if ui.count('            Popup? popup = null;') != 2:
    raise RuntimeError(f'Expected two Popup locals, found {ui.count("            Popup? popup = null;")}')
ui = ui.replace('            Popup? popup = null;', '            FrameworkElement? flyout = null;')

first_open = '''                var button = (Button)window.FindName("DownloadQueueButton");
                popup = (Popup)window.FindName("DownloadQueuePopup");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.IsTrue(popup.IsOpen, "下载队列按钮必须打开弹窗。");
                var host = (FrameworkElement)window.FindName("DownloadQueuePopupHost");
'''
first_open_new = '''                var button = (Button)window.FindName("DownloadQueueButton");
                flyout = (FrameworkElement)window.FindName("DownloadQueueFlyoutLayer");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                Assert.AreEqual(Visibility.Visible, flyout.Visibility, "下载队列按钮必须打开窗口内浮层。");
                var host = (FrameworkElement)window.FindName("DownloadQueueFlyoutHost");
'''
if ui.count(first_open) != 1:
    raise RuntimeError(f'Expected first legacy queue open block once, found {ui.count(first_open)}')
ui = ui.replace(first_open, first_open_new, 1)

ui = ui.replace('Assert.AreEqual(true, activeTab.IsChecked, "弹窗每次打开必须默认停留在“下载中”。");',
                'Assert.AreEqual(true, activeTab.IsChecked, "队列浮层每次打开必须默认停留在“下载中”。");', 1)
ui = ui.replace('Assert.IsFalse(popup.IsOpen, "再次点击下载队列按钮必须关闭弹窗。");',
                'Assert.AreEqual(Visibility.Collapsed, flyout.Visibility, "再次点击下载队列按钮必须关闭窗口内浮层。");', 1)

second_open = '''                var button = (Button)window.FindName("DownloadQueueButton");
                popup = (Popup)window.FindName("DownloadQueuePopup");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

                var completedTab = (RadioButton)window.FindName("DownloadQueueCompletedTab");
'''
second_open_new = '''                var button = (Button)window.FindName("DownloadQueueButton");
                flyout = (FrameworkElement)window.FindName("DownloadQueueFlyoutLayer");
                button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                window.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
                Assert.AreEqual(Visibility.Visible, flyout.Visibility);

                var completedTab = (RadioButton)window.FindName("DownloadQueueCompletedTab");
'''
if ui.count(second_open) != 1:
    raise RuntimeError(f'Expected second legacy queue open block once, found {ui.count(second_open)}')
ui = ui.replace(second_open, second_open_new, 1)

legacy_finally = '''                if (popup is not null)
                {
                    popup.IsOpen = false;
                }
                window?.Close();
'''
if ui.count(legacy_finally) != 2:
    raise RuntimeError(f'Expected two legacy Popup finally blocks, found {ui.count(legacy_finally)}')
ui = ui.replace(legacy_finally, '''                if (flyout is not null)
                {
                    flyout.Visibility = Visibility.Collapsed;
                }
                window?.Close();
''')

ui = ui.replace('"弹窗渲染测试超时。"', '"队列浮层渲染测试超时。"', 1)
ui = ui.replace('$"下载队列弹窗无法渲染：{failure}"', '$"下载队列浮层无法渲染：{failure}"', 1)
ui = ui.replace('"弹窗删除路由测试超时。"', '"队列浮层删除路由测试超时。"', 1)
ui = ui.replace('$"弹窗删除按钮未能到达队列服务：{failure}"', '$"队列浮层删除按钮未能到达队列服务：{failure}"', 1)

# Native Popup type may still be used elsewhere in this broad UI test file; only
# queue-specific names must be gone.
if 'DownloadQueuePopup' in ui or 'CalculateDownloadQueuePopupLeft' in ui:
    raise RuntimeError('ReliabilityTests.Ui.cs still contains obsolete download queue Popup references')
write('MCPanel.Tests/ReliabilityTests.Ui.cs', ui)
