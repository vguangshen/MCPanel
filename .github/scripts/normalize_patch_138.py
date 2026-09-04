from pathlib import Path

path = Path('.github/scripts/patch_sql_iis_138.py')
text = path.read_text(encoding='utf-8')
text = text.replace('NT AUTHORITY' + chr(92) + 'NETWORK SERVICE', 'NT AUTHORITY' + chr(92) * 2 + 'NETWORK SERVICE')
text = text.replace(
    'updated, count = re.subn(pattern, replacement, text, count=1, flags=re.S)',
    'updated, count = re.subn(pattern, lambda _match: replacement, text, count=1, flags=re.S)')
lines = text.splitlines(keepends=True)
for index, line in enumerate(lines):
    if '$rewriteProcess = Start-Process msiexec.exe' in line:
        lines[index] = line.replace(chr(92) * 2 + '"', chr(92) + '"')
text = ''.join(lines)
path.write_text(text, encoding='utf-8')
