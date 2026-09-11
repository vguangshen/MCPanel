$ErrorActionPreference = 'Stop'

$scriptPath = 'tools/Fix-WebsiteCatalinaBlank-1.3.40.ps1'
$raw = Get-Content -LiteralPath $scriptPath -Raw
$pattern = '(?s)# 5\. Keep virtualization.*?# 6\. Add a regression test'
$replacement = @'
# 5. Keep virtualization for large lists, but avoid Recycling containers for the dynamic full-card template.
# Patch only the WebsiteListScroll declaration so the product repository virtualization policy is unchanged.
$xamlPath = 'MainWindow.xaml'
$xaml = Get-Content -LiteralPath $xamlPath -Raw
$anchor = 'x:Name="WebsiteListScroll"'
$start = $xaml.IndexOf($anchor, [StringComparison]::Ordinal)
if ($start -lt 0) { throw 'WebsiteListScroll anchor not found.' }
$end = $xaml.IndexOf('</ListBox>', $start, [StringComparison]::Ordinal)
if ($end -le $start) { throw 'WebsiteListScroll closing tag not found.' }
$segment = $xaml.Substring($start, $end - $start)
$oldMode = 'VirtualizingPanel.VirtualizationMode="Recycling"'
$newMode = 'VirtualizingPanel.VirtualizationMode="Standard"'
if (([regex]::Matches($segment, [regex]::Escape($oldMode))).Count -ne 1) {
    throw 'WebsiteListScroll must contain exactly one Recycling virtualization mode marker.'
}
$segment = $segment.Replace($oldMode, $newMode)
$xaml = $xaml.Substring(0, $start) + $segment + $xaml.Substring($end)
Set-Content -LiteralPath $xamlPath -Value $xaml -Encoding UTF8

# 6. Add a regression test
'@
$updated = [regex]::Replace($raw, $pattern, $replacement, 1)
if ($updated -eq $raw) { throw 'Unable to patch the XAML replacement block in the original fix script.' }
Set-Content -LiteralPath $scriptPath -Value $updated -Encoding UTF8

& .\tools\Fix-WebsiteCatalinaBlank-1.3.40.ps1
