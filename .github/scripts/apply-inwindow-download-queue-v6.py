from pathlib import Path

v5_path = Path('.github/scripts/apply-inwindow-download-queue-v5.py')
exec(compile(v5_path.read_text(encoding='utf-8-sig'), str(v5_path), 'exec'))

path = Path('MCPanel.Tests/ReliabilityTests.DialogUi.cs')
text = path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
old = '        StringAssert.Contains(main, "<Popup x:Name=\\\"DownloadQueuePopup\\\"");\n'
new = '''        StringAssert.Contains(main, "x:Name=\\\"DownloadQueueFlyoutLayer\\\"");
        Assert.IsFalse(main.Contains("<Popup x:Name=\\\"DownloadQueuePopup\\\"", StringComparison.Ordinal),
            "下载队列已改为主窗口内浮层，不应再创建独立 Popup HWND。");
'''
if text.count(old) != 1:
    raise RuntimeError(f'Expected one legacy dialog Popup assertion, found {text.count(old)}')
text = text.replace(old, new, 1)
path.write_text(text, encoding='utf-8', newline='\n')
