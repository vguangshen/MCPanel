$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$path = 'MainWindowTemplates.xaml.cs'
$text = [System.IO.File]::ReadAllText((Join-Path (Get-Location) $path))
$old = @'
    private void PinWebsite_Click(object sender, RoutedEventArgs e) => GetOwner(sender)?.PinWebsite_Click(sender, e);
    private void ExpandWebsite_Click(object sender, RoutedEventArgs e) => GetOwner(sender)?.ExpandWebsite_Click(sender, e);
'@
if (-not $text.Contains($old)) {
    throw 'Unable to locate obsolete website pin/expand proxy methods.'
}
$text = $text.Replace($old, '')
[System.IO.File]::WriteAllText((Join-Path (Get-Location) $path), $text, (New-Object System.Text.UTF8Encoding($false)))
Write-Host 'Removed obsolete WebsiteRowTemplate proxy handlers.'
