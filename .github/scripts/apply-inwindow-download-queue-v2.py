from pathlib import Path

source_path = Path('.github/scripts/apply-inwindow-download-queue.py')
source = source_path.read_text(encoding='utf-8-sig').replace('\r\n', '\n')
lines = source.splitlines()
updated = []
replaced = False
for line in lines:
    if line.startswith('main = main.replace("            DownloadQueuePopup.IsOpen = false;'):
        updated.append('main = main.replace("DownloadQueuePopup.IsOpen = false;", "CloseDownloadQueueFlyout();")')
        replaced = True
    else:
        updated.append(line)
if not replaced:
    raise RuntimeError('Could not patch legacy Popup close replacement in v1 script')
patched_source = '\n'.join(updated) + '\n'
exec(compile(patched_source, str(source_path), 'exec'))
