$ErrorActionPreference = 'Stop'

$path = Join-Path $PWD 'tools\Fix-VisibleSharedTomcat-1.3.43.ps1'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))

$oldBlock = @'
$buttonStart = $text.IndexOf('<Button Content="以 Catalina 方式启动"', [StringComparison]::Ordinal)
if ($buttonStart -lt 0) { throw 'Environment Catalina button start not found.' }
$buttonEnd = $text.IndexOf('/>', $buttonStart, [StringComparison]::Ordinal)
if ($buttonEnd -lt 0) { throw 'Environment Catalina button end not found.' }
$text = $text.Remove($buttonStart, $buttonEnd + 2 - $buttonStart)
if ($text.Contains('Tag="CatalinaRun"') -or $text.Contains('以 Catalina 方式启动')) {
    throw 'Redundant environment Catalina button was not removed.'
}
'@
$newBlock = @'
$tagIndex = $text.IndexOf('Tag="CatalinaRun"', [StringComparison]::Ordinal)
if ($tagIndex -lt 0) { throw 'Environment Catalina tag not found.' }
$buttonStart = $text.LastIndexOf('<Button', $tagIndex, [StringComparison]::Ordinal)
if ($buttonStart -lt 0) { throw 'Environment Catalina button start not found.' }
$buttonEnd = $text.IndexOf('/>', $tagIndex, [StringComparison]::Ordinal)
if ($buttonEnd -lt 0) { throw 'Environment Catalina button end not found.' }
$text = $text.Remove($buttonStart, $buttonEnd + 2 - $buttonStart)
if ($text.Contains('Tag="CatalinaRun"')) {
    throw 'Redundant environment Catalina button was not removed.'
}
'@
if (-not $text.Contains($oldBlock.Trim())) {
    throw 'Expected environment Catalina removal block was not found.'
}
$text = $text.Replace($oldBlock.Trim(), $newBlock.Trim())

$oldTest = @'
        Assert.IsFalse(xaml.Contains("以 Catalina 方式启动", StringComparison.Ordinal));
'@
$newTest = @'
        StringAssert.Contains(xaml, "Tag=\"Catalina\"");
'@
if (-not $text.Contains($oldTest.Trim())) {
    throw 'Expected Catalina regression assertion was not found.'
}
$text = $text.Replace($oldTest.Trim(), $newTest.Trim())

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
& $path
