function Get-PreservedAppSettings([string]$ConfigFile, [string[]]$Keys) {
    $settings = @{}
    if (!(Test-Path -LiteralPath $ConfigFile)) { return $settings }
    # Fail before replacing installed files if their configuration is unreadable.
    [xml]$document = Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8
    foreach ($entry in @($document.configuration.appSettings.add)) {
        if ($null -ne $entry -and $Keys -contains [string]$entry.key -and $entry.HasAttribute('value')) {
            $settings[[string]$entry.key] = [string]$entry.value
        }
    }
    return $settings
}

function Set-PreservedAppSettings([string]$ConfigFile, [hashtable]$Settings) {
    if ($Settings.Count -eq 0) { return }
    if (!(Test-Path -LiteralPath $ConfigFile)) { throw "Published configuration is missing: $ConfigFile" }
    # Windows PowerShell 5.1 returns a JSON array as a single pipeline object;
    # avoid @() wrapping it in another array and hiding its individual entries.
    $migrations = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Installer\download-url-migrations.json') -Raw -Encoding UTF8 | ConvertFrom-Json
    [xml]$document = Get-Content -LiteralPath $ConfigFile -Raw -Encoding UTF8
    $appSettings = $document.configuration.appSettings
    if ($null -eq $appSettings) {
        $appSettings = $document.CreateElement('appSettings')
        [void]$document.configuration.AppendChild($appSettings)
    }
    foreach ($key in $Settings.Keys) {
        $entry = @($appSettings.add) | Where-Object { $null -ne $_ -and [string]$_.key -ieq $key } | Select-Object -First 1
        if ($null -eq $entry) {
            $entry = $document.CreateElement('add')
            $entry.SetAttribute('key', $key)
            [void]$appSettings.AppendChild($entry)
        }
        $value = [string]$Settings[$key]
        foreach ($migration in $migrations) {
            if ($key -ieq $migration.Key -and $value.Trim() -ieq $migration.PreviousUrl) { $value = $migration.CurrentUrl }
        }
        $entry.SetAttribute('value', $value)
    }
    $document.Save($ConfigFile)
}
