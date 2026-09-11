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

# Make the narrow-view regression token deterministic. CustomWebsiteDefinition generates other
# searchable fields, so a plain numeric token such as "43" can accidentally match those fields.
$testPath = 'MCPanel.Tests/ReliabilityTests.WebsiteCatalinaBlank140.cs'
$test = Get-Content -LiteralPath $testPath -Raw
$oldName = '                        Name = $"平台 {index:D2}",'
$newName = '                        Name = index == 43 ? "ONLY_TARGET_43" : $"平台 {index:D2}",'
if (-not $test.Contains($oldName)) { throw 'Unable to find deterministic test-name marker.' }
$test = $test.Replace($oldName, $newName)
$test = $test.Replace('                model.WebsiteSearchKeyword = "平台 43";', '                model.WebsiteSearchKeyword = "ONLY_TARGET_43";')
Set-Content -LiteralPath $testPath -Value $test -Encoding UTF8
