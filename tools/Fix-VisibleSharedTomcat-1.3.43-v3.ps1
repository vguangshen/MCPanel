$ErrorActionPreference = 'Stop'

$path = Join-Path $PWD 'tools\Fix-VisibleSharedTomcat-1.3.43.ps1'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))

# The normal shared-Tomcat Start/Restart path is being moved to a visible
# Catalina CMD console in 1.3.43, but the dedicated environment-card
# "以 Catalina 方式启动" action is still a supported explicit entry point.
# Keep both that CatalinaRun button and the per-product Tag="Catalina" action.
$sectionStartMarker = '# 3) Normal Start is now the visible Catalina experience, so remove the duplicate'
$sectionEndMarker = '# 4) Replace old regression contracts that described the retired hidden-service path.'
$sectionStart = $text.IndexOf($sectionStartMarker, [StringComparison]::Ordinal)
$sectionEnd = $text.IndexOf($sectionEndMarker, $sectionStart, [StringComparison]::Ordinal)
if ($sectionStart -lt 0 -or $sectionEnd -le $sectionStart) {
    throw 'Expected environment Catalina migration section was not found.'
}

$newSection = @'
# 3) Keep the dedicated environment-card Catalina start action.
#    Normal Start/Restart also uses the visible Catalina console from 1.3.43,
#    but the explicit "以 Catalina 方式启动" button remains available.
$path = 'Resources/MainWindowTemplates.xaml'
$text = Read-Utf8 $path
if (-not $text.Contains('Tag="CatalinaRun"') -or -not $text.Contains('以 Catalina 方式启动')) {
    throw 'Dedicated environment Catalina start button must be preserved.'
}
if (-not $text.Contains('Tag="Catalina"')) {
    throw 'Per-product Catalina action must be preserved.'
}
Write-Utf8 $path $text

'@
$text = $text.Substring(0, $sectionStart) + $newSection + $text.Substring($sectionEnd)

$oldTest = @'
    [TestMethod]
    public void EnvironmentCardHasNoDuplicateCatalinaStartButton()
    {
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        Assert.IsFalse(xaml.Contains("Tag=\"CatalinaRun\"", StringComparison.Ordinal));
        Assert.IsFalse(xaml.Contains("以 Catalina 方式启动", StringComparison.Ordinal));
    }
'@
$newTest = @'
    [TestMethod]
    public void EnvironmentCardKeepsDedicatedCatalinaStartButton()
    {
        var xaml = ReadRepositoryFile("Resources/MainWindowTemplates.xaml");
        StringAssert.Contains(xaml, "Tag=\"CatalinaRun\"");
        StringAssert.Contains(xaml, "以 Catalina 方式启动");
        StringAssert.Contains(xaml, "Tag=\"Catalina\"");
    }
'@
if (-not $text.Contains($oldTest.Trim())) {
    throw 'Expected Catalina regression test block was not found.'
}
$text = $text.Replace($oldTest.Trim(), $newTest.Trim())

$oldNote = '- 删除环境卡片上重复的“以 Catalina 方式启动”按钮；普通“启动”现在就是可见 Catalina 控制台模式。'
$newNote = '- 保留环境卡片上的“以 Catalina 方式启动”按钮和产品管理中的 Catalina 启动入口；普通“启动”与“重启”同时改为可见 Catalina CMD 控制台模式。'
if (-not $text.Contains($oldNote)) {
    throw 'Expected Catalina release-note line was not found.'
}
$text = $text.Replace($oldNote, $newNote)

# Guard the generated migration script itself so a future edit cannot silently
# reintroduce the removal contract before it is executed.
if ($text.Contains('EnvironmentCardHasNoDuplicateCatalinaStartButton', [StringComparison]::Ordinal) -or
    $text.Contains('Redundant environment Catalina button was not removed.', [StringComparison]::Ordinal)) {
    throw 'Catalina button removal contract is still present after patching.'
}

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
& $path
