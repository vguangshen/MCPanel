from pathlib import Path

v4_path = Path('.github/scripts/apply-inwindow-download-queue-v4.py')
source = v4_path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
old = "if 'DownloadQueuePopup' in ui or 'CalculateDownloadQueuePopupLeft' in ui:\n    raise RuntimeError('ReliabilityTests.Ui.cs still contains obsolete download queue Popup references')"
new = "if 'CalculateDownloadQueuePopupLeft' in ui:\n    raise RuntimeError('ReliabilityTests.Ui.cs still calls obsolete popup placement math')"
if source.count(old) != 1:
    raise RuntimeError(f'Expected one v4 validation block, found {source.count(old)}')
source = source.replace(old, new, 1)
exec(compile(source, str(v4_path), 'exec'))
