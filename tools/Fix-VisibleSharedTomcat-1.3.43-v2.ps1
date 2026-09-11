$ErrorActionPreference = 'Stop'

$path = Join-Path $PWD 'tools\Fix-VisibleSharedTomcat-1.3.43.ps1'
$text = [IO.File]::ReadAllText($path, [Text.UTF8Encoding]::new($false))

$old = @'
if ($text.Contains('Tag="CatalinaRun"') -or $text.Contains('以 Catalina 方式启动')) {
'@
$new = @'
if ($text.Contains('Tag="CatalinaRun"')) {
'@
if (-not $text.Contains($old.Trim())) {
    throw 'Expected Catalina postcondition was not found in the migration script.'
}
$text = $text.Replace($old.Trim(), $new.Trim())

$old = @'
        Assert.IsFalse(xaml.Contains("以 Catalina 方式启动", StringComparison.Ordinal));
'@
$new = @'
        StringAssert.Contains(xaml, "Tag=\"Catalina\"");
'@
if (-not $text.Contains($old.Trim())) {
    throw 'Expected Catalina regression assertion was not found in the migration script.'
}
$text = $text.Replace($old.Trim(), $new.Trim())

[IO.File]::WriteAllText($path, $text, [Text.UTF8Encoding]::new($false))
& $path
