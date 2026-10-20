param([string]$InstallRoot = $PSScriptRoot, [switch]$Silent)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$script:InstallRoot = [IO.Path]::GetFullPath($InstallRoot).TrimEnd('\')
$script:ExitCode = 1
$script:Busy = $false
$script:Form = $null
$script:ResultFile = Join-Path ([IO.Path]::GetTempPath()) ('MCPanel-CompleteUninstall-' + [Guid]::NewGuid().ToString('N') + '.json')
$script:UninstallKey = $null
$script:UninstallId = $null
$script:ProfileShortcutFolders = @()
$registryBase = 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\'
$powerShell = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = New-Object Security.Principal.WindowsPrincipal($identity)
if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $arguments = '-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File "' + $PSCommandPath + '" -InstallRoot "' + $script:InstallRoot + '"'
    if ($Silent) { $arguments += ' -Silent' }
    try { $elevated = Start-Process -FilePath $powerShell -ArgumentList $arguments -Verb RunAs -PassThru -Wait; exit $elevated.ExitCode }
    catch { exit 1 }
}
# Never keep the installation directory as this process's working directory.
Set-Location -LiteralPath ([IO.Path]::GetTempPath())
[Environment]::CurrentDirectory = [IO.Path]::GetTempPath()

function Set-Status([string]$Text) {
    if ($script:LastStatus -eq $Text) { return }
    $script:LastStatus = $Text
    Write-Host $Text
    if ($script:Form) { $script:Status.Text = $Text; [Windows.Forms.Application]::DoEvents() }
}
function Assert-Installation {
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    try {
        foreach ($id in @('{4877D128-D808-4B6B-B4CB-D4D0476F5275}_is1', 'MCPanel.Setup.IntegrationTest_is1')) {
            $key = $machine.OpenSubKey($registryBase + $id)
            try {
                if ($key) {
                    $location = [string]$key.GetValue('InstallLocation')
                    if ($location -and [IO.Path]::GetFullPath($location).TrimEnd('\').Equals($script:InstallRoot, [StringComparison]::OrdinalIgnoreCase)) {
                        $script:UninstallId = $id; break
                    }
                }
            } finally { if ($key) { $key.Dispose() } }
        }
    } finally { $machine.Dispose() }
    if (!$script:UninstallId -or !(Test-Path -LiteralPath (Join-Path $script:InstallRoot 'Maintenance\installer.marker'))) {
        throw '没有找到与此目录匹配的 MCPanel 安装记录，请使用新版安装器修复后再卸载。'
    }
    $support = Join-Path $script:InstallRoot 'InstallerAssets\RuntimeCleanup.ps1'
    if (!(Test-Path -LiteralPath $support)) { throw '卸载支持文件缺失，请使用新版安装器修复。' }
    # Dot-sourced into script scope, not a transient function scope.
    . $support
    $protected = @($env:WINDIR, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:PUBLIC, $env:USERPROFILE, [IO.Path]::GetTempPath()) | Where-Object { $_ }
    $null = Assert-ComponentRoot $script:InstallRoot @($script:InstallRoot) $protected
}
# Load cleanup definitions only after proving registration and before deleting files.
Assert-Installation
. (Join-Path $script:InstallRoot 'InstallerAssets\RuntimeCleanup.ps1')

Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Text;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
public static class MCPanelDeleteProbe {
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern uint GetLongPathName(string path, StringBuilder result, uint capacity);
    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    class ShellLinkObject { }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    interface IShellLinkUnicode {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder path, int capacity, IntPtr findData, uint flags);
    }
    public static string ShortcutTarget(string path) {
        var link = new ShellLinkObject();
        try {
            ((System.Runtime.InteropServices.ComTypes.IPersistFile)link).Load(path, 0);
            var buffer = new StringBuilder(32768);
            ((IShellLinkUnicode)link).GetPath(buffer, buffer.Capacity, IntPtr.Zero, 4);
            return Environment.ExpandEnvironmentVariables(buffer.ToString());
        } catch (COMException) { return String.Empty; }
        finally { Marshal.FinalReleaseComObject(link); }
    }
    public static string LongPath(string path) {
        var full = Path.GetFullPath(path);
        var result = new StringBuilder(32768);
        var length = GetLongPathName(full, result, (uint)result.Capacity);
        return length > 0 && length < result.Capacity ? result.ToString() : full;
    }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static void Check(string path) {
        using (var handle = CreateFile(path, 0x10000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero)) {
            if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), path);
        }
    }
}
'@
function Assert-Deletable([string]$Path) {
    for ($attempt = 0; $attempt -lt 2; $attempt++) {
        try {
            $item = Get-Item -LiteralPath $Path -Force -ErrorAction Stop
            if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0) {
                [IO.File]::SetAttributes($Path, ($item.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly)))
            }
            [MCPanelDeleteProbe]::Check($Path)
            if ($item.PSIsContainer -and (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0)) {
                $children = @(Get-ChildItem -LiteralPath $Path -Force -ErrorAction Stop)
            } else { $children = @() }
            break
        } catch {
            if ($attempt -eq 0 -and (Test-ComponentAccessDenied $_)) { Repair-ComponentEntryAccess $Path | Out-Null }
            else { throw ('文件被占用或无法清理，请关闭占用程序后重试：' + $Path + '。' + $_.Exception.Message) }
        }
    }
    foreach ($child in $children) { Assert-Deletable $child.FullName }
}
function Get-OwnedServices {
    $services = [Microsoft.Win32.Registry]::LocalMachine.OpenSubKey('SYSTEM\CurrentControlSet\Services')
    try {
        foreach ($name in $services.GetSubKeyNames()) {
            $key = $services.OpenSubKey($name)
            try {
                $image = [string]$key.GetValue('ImagePath')
                if (!$image -or $image -notmatch '(?i)\.exe') { continue }
                $executable = Get-ServiceExecutable ([Environment]::ExpandEnvironmentVariables($image))
                if (Test-PathUnderRoot $executable $script:InstallRoot) { $name }
            } finally { if ($key) { $key.Dispose() } }
        }
    } finally { $services.Dispose() }
}
function Test-OwnedPath([string]$Path) {
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    # Windows stores shell/AUMID and special application tokens alongside paths.
    try {
        $full = [MCPanelDeleteProbe]::LongPath($Path.Trim('"'))
        $base = [MCPanelDeleteProbe]::LongPath($script:InstallRoot)
        return Test-PathUnderRoot $full $base
    }
    catch { return $false }
}
function Remove-UserRegistry([string]$HiveName) {
    $run = [Microsoft.Win32.Registry]::Users.OpenSubKey($HiveName + '\Software\Microsoft\Windows\CurrentVersion\Run', $true)
    try {
        foreach ($name in @('MCPanel', 'MCPanelTomcatProducts')) {
            if (!$run) { continue }
            $command = [string]$run.GetValue($name)
            if ($command -and $command.IndexOf($script:InstallRoot + '\', [StringComparison]::OrdinalIgnoreCase) -ge 0) { $run.DeleteValue($name, $false) }
        }
    } finally { if ($run) { $run.Dispose() } }
    if ($script:UninstallId -ne 'MCPanel.Setup.IntegrationTest_is1') {
        $hive = [Microsoft.Win32.Registry]::Users.OpenSubKey($HiveName, $true)
        try { if ($hive) { $hive.DeleteSubKeyTree('Software\MCPanel', $false) } }
        finally { if ($hive) { $hive.Dispose() } }
    }
}
function Remove-OwnedUserState {
    $loaded = @([Microsoft.Win32.Registry]::Users.GetSubKeyNames() | Where-Object { $_ -match '^S-\d+(?:-\d+)+$' })
    foreach ($sid in $loaded) { Remove-UserRegistry $sid }
    if ($script:UninstallId -eq 'MCPanel.Setup.IntegrationTest_is1') { return }
    $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
    $profiles = $machine.OpenSubKey('SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList')
    try {
        foreach ($sid in $profiles.GetSubKeyNames()) {
            if ($sid -notmatch '^S-\d+(?:-\d+)+$') { continue }
            $key = $profiles.OpenSubKey($sid)
            try { $profile = [Environment]::ExpandEnvironmentVariables([string]$key.GetValue('ProfileImagePath')) }
            finally { $key.Dispose() }
            if (!$profile -or !(Test-Path -LiteralPath $profile)) { continue }
            $script:ProfileShortcutFolders += @( (Join-Path $profile 'Desktop'), (Join-Path $profile 'AppData\Roaming\Microsoft\Windows\Start Menu\Programs') )
            $fallback = Join-Path $profile 'AppData\Local\MCPanel'
            if ((Test-Path -LiteralPath $fallback) -and !(Test-PathUnderRoot $script:InstallRoot $fallback)) { Assert-NoComponentLink $fallback; Assert-Deletable $fallback; Remove-ComponentDirectory $fallback | Out-Null }
            # Logged-out profiles can also retain a per-user startup entry.
            # Load only real user hives, closing every handle before unloading.
            if ($sid -in $loaded -or $sid -in @('S-1-5-18', 'S-1-5-19', 'S-1-5-20')) { continue }
            $hiveFile = Join-Path $profile 'NTUSER.DAT'
            if (!(Test-Path -LiteralPath $hiveFile)) { continue }
            $temporaryHive = 'MCPanel.Uninstall.' + [Guid]::NewGuid().ToString('N')
            & reg.exe load ('HKU\' + $temporaryHive) $hiveFile | Out-Null
            if ($LASTEXITCODE -ne 0) { throw ('无法清理此用户的 MCPanel 启动配置，请退出该用户会话后重试：' + $profile) }
            $global:LASTEXITCODE = 0
            try { Remove-UserRegistry $temporaryHive }
            finally {
                & reg.exe unload ('HKU\' + $temporaryHive) | Out-Null
                if ($LASTEXITCODE -ne 0) { throw '临时用户配置单元未能释放，请重启后再次卸载。' }
                $global:LASTEXITCODE = 0
            }
        }
    } finally { if ($profiles) { $profiles.Dispose() }; $machine.Dispose() }
}
function Remove-OwnedStartupAndShortcuts {
    Remove-OwnedUserState
    $folders = @([Environment]::GetFolderPath('CommonPrograms'), [Environment]::GetFolderPath('Programs'), [Environment]::GetFolderPath('CommonDesktopDirectory'), [Environment]::GetFolderPath('DesktopDirectory')) + $script:ProfileShortcutFolders | Select-Object -Unique
    foreach ($folder in $folders) {
        if (!$folder -or !(Test-Path -LiteralPath $folder)) { continue }
        foreach ($file in @(Get-ChildItem -LiteralPath $folder -Filter '*.lnk' -File -Recurse -Force -ErrorAction Stop)) {
            # WScript.Shell can convert a Unicode target through the system ANSI
            # code page. Read IShellLinkW directly so Chinese install paths survive.
            $target = [MCPanelDeleteProbe]::ShortcutTarget($file.FullName)
            if ($target -and (Test-OwnedPath $target)) { [IO.File]::Delete($file.FullName) }
        }
        $group = Join-Path $folder 'MCPanel'
        if ((Test-Path -LiteralPath $group) -and @(Get-ChildItem -LiteralPath $group -Force).Count -eq 0) { [IO.Directory]::Delete($group) }
    }
}

function Remove-OwnedFirewallRules {
    $policy = New-Object -ComObject HNetCfg.FwPolicy2
    try {
        $rules = @($policy.Rules)
        foreach ($rule in $rules) {
            $ownedProgram = $rule.ApplicationName -and (Test-OwnedPath ([Environment]::ExpandEnvironmentVariables($rule.ApplicationName)))
            if ($ownedProgram -or ($script:UninstallId -ne 'MCPanel.Setup.IntegrationTest_is1' -and $rule.Name -like 'MCPanel *')) { $policy.Rules.Remove($rule.Name) }
        }
    } finally { [void][Runtime.InteropServices.Marshal]::ReleaseComObject($policy) }
}
function Get-NativeRuntimeCleanupPaths([string]$NativeParent) {
    if (!(Test-Path -LiteralPath $NativeParent)) { return }
    Assert-NoComponentLink $NativeParent
    foreach ($name in @('Nginx', 'MySQL')) {
        $path = Join-Path $NativeParent $name
        if (Test-Path -LiteralPath $path) { $path }
    }
    # Failed MySQL swaps deliberately retain their recovery data beside the
    # live tree. Full uninstall also owns these exact generated directories.
    foreach ($entry in @(Get-ChildItem -LiteralPath $NativeParent -Directory -Force -ErrorAction Stop)) {
        if ($entry.Name -match '^\.mcpanel-mysql-[0-9a-f]{32}$') { $entry.FullName }
    }
}
function Invoke-CompleteUninstall {
    $mutex = New-Object Threading.Mutex($false, 'Local\MCPanel.Uninstall.v2')
    $locked = $false
    try {
        $locked = $mutex.WaitOne(0)
        if (!$locked) { throw '另一个 MCPanel 卸载正在进行。' }
        try { $panel = [Threading.Mutex]::OpenExisting('Local\MCPanel.MainWindow.v1') }
        catch [Threading.WaitHandleCannotBeOpenedException] { $panel = $null }
        if ($panel) { $panel.Dispose(); throw '请先在系统托盘中完全退出 MCPanel，然后重新点击完整卸载。' }
        Set-Status '正在检查文件占用和安装目录...'
        # Running component binaries and databases are expected to be locked.
        # Probe panel settings first, then stop components before the full probe.
        foreach ($name in @('MCPanel.exe.config', 'Uninstall-MCPanel.ps1')) {
            $path = Join-Path $script:InstallRoot $name
            if (Test-Path -LiteralPath $path) { Assert-Deletable $path }
        }
        $stateRoot = Join-Path $script:InstallRoot 'StoreData'
        if (Test-Path -LiteralPath $stateRoot) {
            foreach ($file in @(Get-ChildItem -LiteralPath $stateRoot -File -Force)) { Assert-Deletable $file.FullName }
        }
        $exe = Join-Path $script:InstallRoot 'MCPanel.exe'
        if (!(Test-Path -LiteralPath $exe)) { throw 'MCPanel.exe 缺失，请运行新版安装器修复后再次卸载。' }
        Set-Status '正在正常停止数据库、网站和托管服务...'
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $exe
        $info.Arguments = '--uninstall-components "' + $script:ResultFile + '"'
        $info.WorkingDirectory = [IO.Path]::GetTempPath()
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $worker = [Diagnostics.Process]::Start($info)
        try {
            while (!$worker.WaitForExit(200)) {
                if (Test-Path -LiteralPath $script:ResultFile) {
                    try { $state = [IO.File]::ReadAllText($script:ResultFile) | ConvertFrom-Json; Set-Status $state.phase } catch { }
                }
                if ($script:Form) { [Windows.Forms.Application]::DoEvents() }
            }
            $state = if (Test-Path -LiteralPath $script:ResultFile) { [IO.File]::ReadAllText($script:ResultFile) | ConvertFrom-Json } else { $null }
            if ($worker.ExitCode -ne 0 -or !$state -or !$state.success) {
                throw ('组件清理未完成，卸载入口已保留。' + $(if ($state) { $state.error } else { '请检查组件或使用新版安装器修复。' }))
            }
        } finally { $worker.Dispose() }
        foreach ($name in @(Get-OwnedServices)) {
            Set-Status ('正在停止并移除服务：' + $name)
            Stop-ComponentService $name | Out-Null
            Remove-ComponentService $name | Out-Null
        }
        if (@(Get-OwnedServices).Count -gt 0) { throw '仍有服务引用安装目录，已停止清理。' }
        Set-Status '正在清理启动项、快捷方式和防火墙规则...'
        Remove-OwnedStartupAndShortcuts
        Remove-OwnedFirewallRules
        # Native runtime paths exist only for non-ASCII installation directories.
        if ($script:UninstallId -ne 'MCPanel.Setup.IntegrationTest_is1') {
            if ($script:InstallRoot -match '[^\x00-\x7F]') {
                $nativeParent = Join-Path $env:ProgramData 'MCPanel'
                if ($nativeParent -match '[^\x00-\x7F]') { $nativeParent = Join-Path ($env:SystemDrive + '\') 'MCPanelData' }
                foreach ($path in @(Get-NativeRuntimeCleanupPaths $nativeParent)) {
                    Assert-NoComponentLink $path; Assert-Deletable $path; Remove-ComponentDirectory $path | Out-Null
                }
                if ((Test-Path -LiteralPath $nativeParent) -and @(Get-ChildItem -LiteralPath $nativeParent -Force).Count -eq 0) { [IO.Directory]::Delete($nativeParent) }
            }

        }
        Set-Status '正在删除程序、配置、数据库、站点和缓存...'
        Assert-Deletable $script:InstallRoot
        # Keep the retry entry and registration marker until every other file is gone.
        $last = @('InstallerAssets', 'MCPanel.exe', 'Maintenance', 'Uninstall-MCPanel.ps1')
        foreach ($entry in @(Get-ChildItem -LiteralPath $script:InstallRoot -Force | Where-Object { $_.Name -notin $last })) {
            Remove-ComponentEntry $entry.FullName $false | Out-Null
        }
        foreach ($name in $last) {
            $path = Join-Path $script:InstallRoot $name
            if (Test-Path -LiteralPath $path) { Remove-ComponentEntry $path $false | Out-Null }
        }
        [IO.Directory]::Delete($script:InstallRoot, $false)
        if (Test-Path -LiteralPath $script:InstallRoot) { throw '安装目录仍然存在，卸载尚未完成。' }
        $machine = [Microsoft.Win32.RegistryKey]::OpenBaseKey('LocalMachine', 'Registry64')
        try { $machine.DeleteSubKeyTree($registryBase + $script:UninstallId, $false) }
        finally { $machine.Dispose() }
        $script:ExitCode = 0
        Set-Status 'MCPanel 已完整卸载，安装目录及其管理的系统注册项已清理。'
    } finally {
        if (Test-Path -LiteralPath $script:ResultFile) { [IO.File]::Delete($script:ResultFile) }
        if ($locked) { $mutex.ReleaseMutex() }
        $mutex.Dispose()
    }
}

if ($Silent) {
    try { Invoke-CompleteUninstall } catch { Write-Host $_.ScriptStackTrace; Write-Error $_ -ErrorAction Continue }
    exit $script:ExitCode
}
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
[Windows.Forms.Application]::EnableVisualStyles()
$script:Form = New-Object Windows.Forms.Form
$script:Form.Text = 'MCPanel · 卸载工作台'
$script:Form.ClientSize = New-Object Drawing.Size(900, 560)
$script:Form.AutoScaleMode = 'Dpi'
$script:Form.FormBorderStyle = 'FixedDialog'
$script:Form.MaximizeBox = $false
$script:Form.StartPosition = 'CenterScreen'
$script:Form.BackColor = [Drawing.Color]::White
$script:Form.Font = New-Object Drawing.Font('Microsoft YaHei UI', 10)
function Add-Text($Parent, $Text, $X, $Y, $W, $H, $Size, $Color, [switch]$Bold) {
    $label = New-Object Windows.Forms.Label
    $label.Text = $Text; $label.SetBounds($X, $Y, $W, $H)
    $label.ForeColor = [Drawing.ColorTranslator]::FromHtml($Color)
    $style = if ($Bold) { [Drawing.FontStyle]::Bold } else { [Drawing.FontStyle]::Regular }
    $label.Font = New-Object Drawing.Font('Microsoft YaHei UI', $Size, $style)
    $Parent.Controls.Add($label)
    return $label
}
$side = New-Object Windows.Forms.Panel
$side.SetBounds(0, 0, 270, 560)
$side.BackColor = [Drawing.ColorTranslator]::FromHtml('#102D55')
$script:Form.Controls.Add($side)
$logo = New-Object Windows.Forms.PictureBox
$logo.SetBounds(28, 30, 54, 54); $logo.SizeMode = 'Zoom'
$bytes = [IO.File]::ReadAllBytes((Join-Path $script:InstallRoot 'InstallerAssets\Brand.png'))
$stream = New-Object IO.MemoryStream(,$bytes)
try { $image = [Drawing.Image]::FromStream($stream); $logo.Image = $image.Clone(); $image.Dispose() } finally { $stream.Dispose() }
$side.Controls.Add($logo)
$null = Add-Text $side 'MCPanel' 96 38 160 40 22 '#FFFFFF' -Bold
$null = Add-Text $side "部署与运维控制台" 28 112 218 36 17 '#FFFFFF' -Bold
$null = Add-Text $side "运行环境 · 网站部署 · 服务管理" 28 157 218 64 10 '#BCD1EB'
$null = Add-Text $side '01  正常停止托管组件' 28 278 218 32 11 '#FFFFFF'
$null = Add-Text $side '02  清理站点与运行数据' 28 326 218 32 11 '#FFFFFF'
$null = Add-Text $side '03  移除文件与系统注册' 28 374 218 32 11 '#FFFFFF'
$null = Add-Text $side 'DEPLOY. MANAGE. SIMPLIFY.' 28 507 232 22 8 '#BCD1EB'
$null = Add-Text $script:Form '完整卸载 MCPanel' 310 38 540 48 23 '#172E4D' -Bold
$null = Add-Text $script:Form '清理此工作台创建和管理的内容' 312 98 535 30 11 '#64758C'
$warning = New-Object Windows.Forms.Panel
$warning.SetBounds(310, 155, 545, 152); $warning.BackColor = [Drawing.ColorTranslator]::FromHtml('#FFF3E8')
$script:Form.Controls.Add($warning)
$null = Add-Text $warning '卸载前请备份需要保留的业务数据' 20 18 505 30 12 '#9D4317' -Bold
$null = Add-Text $warning "将删除安装目录内的配置、数据库、站点、组件和缓存，`r`n并移除 MCPanel 管理的服务、快捷方式及启动项。`r`n此操作无法撤销。" 20 57 505 84 10 '#814B30'
$null = Add-Text $script:Form ('安装位置：' + $script:InstallRoot) 312 327 540 52 10 '#64758C'
$script:Status = Add-Text $script:Form '准备就绪。请先从系统托盘完全退出 MCPanel。' 312 391 535 78 10 '#334D6F'
$button = New-Object Windows.Forms.Button
$button.Text = '确认并完整卸载'; $button.SetBounds(648, 492, 207, 42)
$button.FlatStyle = 'Flat'; $button.FlatAppearance.BorderSize = 0
$button.BackColor = [Drawing.ColorTranslator]::FromHtml('#205ECF'); $button.ForeColor = [Drawing.Color]::White
$button.Add_Click({
    if ($script:ExitCode -eq 0) { $script:Form.Close(); return }
    $script:Busy = $true; $button.Enabled = $false; $cancel.Enabled = $false
    try {
        Invoke-CompleteUninstall
        $button.Text = '完成'; $script:Status.ForeColor = [Drawing.ColorTranslator]::FromHtml('#14734A')
    } catch {
        $script:Status.ForeColor = [Drawing.ColorTranslator]::FromHtml('#B12B2B'); Set-Status $_.Exception.Message
        $button.Text = '重试完整卸载'
    } finally { $script:Busy = $false; $button.Enabled = $true; $cancel.Enabled = $true }
})
$script:Form.Controls.Add($button)
$cancel = New-Object Windows.Forms.Button
$cancel.Text = '取消'; $cancel.SetBounds(522, 492, 110, 42)
$cancel.FlatStyle = 'Flat'; $cancel.FlatAppearance.BorderColor = [Drawing.ColorTranslator]::FromHtml('#D9E2EE')
$cancel.Add_Click({ $script:Form.Close() }); $script:Form.Controls.Add($cancel)
$script:Form.Add_FormClosing({ if ($script:Busy) { $_.Cancel = $true } })
try { [void]$script:Form.ShowDialog() }
finally { $logo.Image.Dispose(); $script:Form.Dispose() }
exit $script:ExitCode
