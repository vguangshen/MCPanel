from pathlib import Path

v2_path = Path('.github/scripts/apply-inwindow-download-queue-v2.py')
exec(compile(v2_path.read_text(encoding='utf-8-sig'), str(v2_path), 'exec'))


def read(path):
    return Path(path).read_text(encoding='utf-8-sig').replace('\r\n', '\n')


def write(path, text):
    Path(path).write_text(text, encoding='utf-8', newline='\n')


xaml = read('MainWindow.xaml')
old = '''                    <Border x:Name="ProductsHeader"
                            Style="{StaticResource Card}"
                            Padding="20,16"
                            SizeChanged="ProductsHeader_SizeChanged">'''
new = '''                    <Border x:Name="ProductsHeader"
                            Style="{StaticResource Card}"
                            Padding="20,16">'''
if xaml.count(old) != 1:
    raise RuntimeError(f'Expected one ProductsHeader SizeChanged hook, found {xaml.count(old)}')
xaml = xaml.replace(old, new, 1)
write('MainWindow.xaml', xaml)

templates = read('MainWindowTemplates.xaml.cs')
templates = templates.replace(
    '''        // Normal DataTemplate content is attached to the main window, but a
        // Popup is hosted in a separate PopupRoot window. Window.GetWindow()
        // therefore returns null for queue-row buttons in the download popup.
        // Fall back to the visible MCPanel window so popup actions still reach
        // the queue service.
''',
    '''        // Queue rows now live in the MainWindow visual tree. Keep a visible-window
        // fallback for templates that are temporarily detached during layout changes.
''',
    1)
templates = templates.replace(
    '.Where(window => window.IsVisible || window.DownloadQueuePopup.IsOpen)',
    '.Where(window => window.IsVisible)',
    1)
if 'DownloadQueuePopup' in templates:
    raise RuntimeError('MainWindowTemplates.xaml.cs still references the removed WPF Popup')
write('MainWindowTemplates.xaml.cs', templates)
