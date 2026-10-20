param([string]$InstallerFile, [string]$InstallDirectory, [string]$UninstallRoot)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
if (!('InstallerWindow' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class InstallerWindow {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  public delegate bool EnumChild(IntPtr window, IntPtr parameter);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
  [DllImport("user32.dll")] static extern bool EnumChildWindows(IntPtr parent, EnumChild callback, IntPtr parameter);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] static extern int GetWindowText(IntPtr window, StringBuilder text, int count);
  public static string Texts(IntPtr parent) {
    var values = new List<string>();
    EnumChildWindows(parent, (window, parameter) => { var text = new StringBuilder(4096); GetWindowText(window, text, text.Capacity); values.Add(text.ToString()); return true; }, IntPtr.Zero);
    return string.Join("\n", values);
  }
}
'@
}
if ($UninstallRoot) {
    $process = Start-Process -FilePath (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') -ArgumentList ('-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + (Join-Path $UninstallRoot 'Uninstall-MCPanel.ps1') + '" -InstallRoot "' + $UninstallRoot + '"') -PassThru
} else {
    $process = Start-Process -FilePath $InstallerFile -ArgumentList @('/SP-', ('/DIR="' + $InstallDirectory + '"')) -PassThru
}
try {
    $window = $null
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        $process.Refresh()
        if ($UninstallRoot -and $process.MainWindowHandle -ne [IntPtr]::Zero) { $window = $process; break }
        $children = @(Get-CimInstance Win32_Process -Filter ('ParentProcessId=' + $process.Id))
        foreach ($child in $children) {
            try {
                $candidate = [Diagnostics.Process]::GetProcessById([int]$child.ProcessId)
                $candidate.Refresh()
                if ($candidate.MainWindowHandle -ne [IntPtr]::Zero) { $window = $candidate; break }
            } catch { }
        }
        if ($null -ne $window) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($null -eq $window) { throw 'Installer selection window was not created.' }
    $text = [InstallerWindow]::Texts($window.MainWindowHandle)
    if ($UninstallRoot) {
        if ($text -notmatch '完整卸载 MCPanel' -or $text -notmatch '确认并完整卸载' -or $text -notmatch '数据库、站点') { throw ('Complete uninstall controls are missing: ' + $text) }
    } elseif ($text -notmatch '准备你的部署工作台' -or $text -notmatch '创建桌面快捷方式' -or $text -notmatch 'VC\+\+ 2010' -or $text -notmatch '网站部署' -or $text -notmatch '服务管理') {
        throw ('Branded MCPanel controls are missing: ' + $text)
    }
    $rect = [InstallerWindow+RECT]::new()
    $null = [InstallerWindow]::GetWindowRect($window.MainWindowHandle, [ref]$rect)
    $bitmap = [Drawing.Bitmap]::new(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $null = [InstallerWindow]::PrintWindow($window.MainWindowHandle, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    $output = Join-Path $env:RUNNER_TEMP 'MCPanelInstallerPreview'
    [IO.Directory]::CreateDirectory($output) | Out-Null
    try { $bitmap.Save((Join-Path $output $(if ($UninstallRoot) { 'uninstaller.png' } else { 'installer.png' })), [Drawing.Imaging.ImageFormat]::Png) }
    finally { $bitmap.Dispose() }
    Write-Host 'Passed: custom MCPanel deployment/maintenance interface and Chinese controls render.'
} finally {
    # This is an isolated CI-owned preview process; installation is never clicked.
    & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null
    $process.Dispose()
}
