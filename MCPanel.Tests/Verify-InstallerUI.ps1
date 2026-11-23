param([string]$InstallerFile, [string]$InstallDirectory, [string]$UninstallRoot, [switch]$CompleteUninstall)

# Drives the real MCPanel-Setup.exe window through UI Automation (the WPF
# window has no Win32 child controls) and saves screenshots for review.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') {
    # UIAutomationClient is most reliable in Windows PowerShell 5.1.
    $arguments = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', $PSCommandPath)
    foreach ($entry in $PSBoundParameters.GetEnumerator()) {
        if ($entry.Value -is [switch]) { if ($entry.Value) { $arguments += ('-' + $entry.Key) } }
        else { $arguments += @(('-' + $entry.Key), [string]$entry.Value) }
    }
    & (Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe') @arguments
    if ($LASTEXITCODE -ne 0) { throw ('Installer UI verification failed: ' + $LASTEXITCODE) }
    return
}

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing
if (!('SetupWindowCapture' -as [type])) { Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class SetupWindowCapture {
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr window, out RECT rect);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
}
'@ }

$output = Join-Path $(if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }) 'MCPanelInstallerPreview'
[IO.Directory]::CreateDirectory($output) | Out-Null

function Get-Texts([Windows.Automation.AutomationElement]$Root) {
    $values = New-Object Collections.Generic.List[string]
    $walker = [Windows.Automation.TreeWalker]::RawViewWalker
    $stack = New-Object Collections.Stack
    $stack.Push($Root)
    while ($stack.Count -gt 0) {
        $node = $stack.Pop()
        try { if ($node.Current.Name) { $values.Add($node.Current.Name) } } catch { }
        $child = $walker.GetFirstChild($node)
        while ($child) { $stack.Push($child); $child = $walker.GetNextSibling($child) }
    }
    return ($values -join "`n")
}

function Invoke-Button([Windows.Automation.AutomationElement]$Root, [string]$AutomationId) {
    $condition = New-Object Windows.Automation.PropertyCondition([Windows.Automation.AutomationElement]::AutomationIdProperty, $AutomationId)
    $button = $Root.FindFirst([Windows.Automation.TreeScope]::Descendants, $condition)
    if (!$button) { return $false }
    $button.GetCurrentPattern([Windows.Automation.InvokePattern]::Pattern).Invoke()
    return $true
}

function Save-Screenshot([IntPtr]$Handle, [string]$Name) {
    $rect = New-Object SetupWindowCapture+RECT
    $null = [SetupWindowCapture]::GetWindowRect($Handle, [ref]$rect)
    $bitmap = New-Object Drawing.Bitmap(($rect.Right - $rect.Left), ($rect.Bottom - $rect.Top))
    $graphics = [Drawing.Graphics]::FromImage($bitmap)
    $dc = $graphics.GetHdc()
    try { $null = [SetupWindowCapture]::PrintWindow($Handle, $dc, 2) }
    finally { $graphics.ReleaseHdc($dc); $graphics.Dispose() }
    try { $bitmap.Save((Join-Path $output $Name), [Drawing.Imaging.ImageFormat]::Png) } finally { $bitmap.Dispose() }
}

if ($UninstallRoot) {
    $setup = Join-Path $env:ProgramData 'MCPanel.Setup\MCPanel-Setup.exe'
    if (!(Test-Path -LiteralPath $setup)) { throw 'The installed uninstaller is missing: ' + $setup }
    $process = Start-Process -FilePath $setup -ArgumentList @('/UNINSTALL', ('"' + $UninstallRoot + '"')) -PassThru
} else {
    # Render every designed page for the preview artifact.
    $pages = Join-Path $output 'pages'
    $render = Start-Process -FilePath $InstallerFile -ArgumentList @('--render-preview', ('"' + $pages + '"')) -PassThru
    if (!$render.WaitForExit(60000)) { $render.Kill(); throw 'Design preview rendering timed out.' }
    if ($render.ExitCode -ne 0 -or @(Get-ChildItem -LiteralPath $pages -Filter '*.png').Count -lt 7) { throw 'Design preview rendering failed.' }
    Write-Host 'Passed: every installer and uninstaller page renders from vector XAML.'
    $process = Start-Process -FilePath $InstallerFile -ArgumentList @(('/DIR="' + $InstallDirectory + '"')) -PassThru
}

try {
    $handle = [IntPtr]::Zero
    for ($attempt = 0; $attempt -lt 80 -and $handle -eq [IntPtr]::Zero; $attempt++) {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) { throw ('Setup window exited early: ' + $process.ExitCode) }
        $handle = $process.MainWindowHandle
    }
    if ($handle -eq [IntPtr]::Zero) { throw 'Setup window was not created.' }
    Start-Sleep -Milliseconds 1200
    $window = [Windows.Automation.AutomationElement]::FromHandle($handle)
    $text = Get-Texts $window
    if ($UninstallRoot) {
        if ($text -notmatch '完整卸载 MCPanel' -or $text -notmatch '确认并完整卸载' -or $text -notmatch '数据库、站点') { throw ('Complete uninstall controls are missing: ' + $text) }
    } elseif ($text -notmatch '准备你的部署工作台' -or $text -notmatch '创建桌面快捷方式' -or $text -notmatch 'VC\+\+ 2010' -or
              $text -notmatch '网站部署' -or $text -notmatch '服务管理' -or $text -notmatch '开始安装|更新 MCPanel') {
        throw ('Branded MCPanel controls are missing: ' + $text)
    }

    Save-Screenshot $handle $(if ($CompleteUninstall) { 'uninstaller.png' } elseif ($UninstallRoot) { 'uninstaller.png' } else { 'installer.png' })
    Write-Host 'Passed: the MCPanel vector deployment/maintenance interface renders with Chinese controls.'

    if ($CompleteUninstall) {
        if (!(Invoke-Button $window 'ConfirmUninstallButton')) { throw 'Unable to activate the complete uninstall button.' }
        $complete = $false
        for ($attempt = 0; $attempt -lt 480; $attempt++) {
            Start-Sleep -Milliseconds 250
            $text = Get-Texts $window
            if ($text -match 'MCPanel 已完整卸载') { $complete = $true; break }
            if ($text -match '重试完整卸载') { throw ('Interactive uninstall failed: ' + $text) }
        }
        if (!$complete) { throw 'Interactive uninstall did not show its completion state.' }
        Start-Sleep -Milliseconds 1200
        Save-Screenshot $handle 'uninstaller-complete.png'
        if (!(Invoke-Button $window 'FinishButton')) { throw 'Unable to activate the finish button.' }
        if (!$process.WaitForExit(10000) -or $process.ExitCode -ne 0) { throw 'Interactive complete uninstall did not exit successfully.' }
        Write-Host 'Passed: the branded uninstall confirmation completes file/registration cleanup and exits successfully.'
    }
} finally {
    # This is an isolated CI-owned preview process; installation is never clicked.
    if (!$process.HasExited) { & taskkill.exe /PID $process.Id /T /F 2>&1 | Out-Null }
    $process.Dispose()
}
