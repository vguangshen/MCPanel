$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Split-Path -Parent $PSScriptRoot
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)

function Read-RepoText([string]$RelativePath) {
    [System.IO.File]::ReadAllText((Join-Path $repoRoot $RelativePath))
}

function Write-RepoText([string]$RelativePath, [string]$Text) {
    [System.IO.File]::WriteAllText((Join-Path $repoRoot $RelativePath), $Text, $utf8NoBom)
}

function Assert-Xml([string]$RelativePath) {
    $text = Read-RepoText $RelativePath
    try {
        [xml]$null = $text
    }
    catch {
        throw "Generated XML is invalid: $RelativePath. $($_.Exception.Message)"
    }
    Write-Host "Validated generated XML: $RelativePath"
}

# Two interpolated replacement strings in the migration can leave literal
# backslashes immediately before XML quotes. Normalize only those known files.
foreach ($relativePath in @('MainWindow.xaml', 'Resources/PanelDialogControls.xaml')) {
    $fixed = Read-RepoText $relativePath
    while ($fixed.Contains('\"')) {
        $fixed = $fixed.Replace('\"', '"')
    }

    # XML 1.0 permits TAB/LF/CR and U+0020+, but not the remaining C0 controls.
    # PowerShell replacement expansion can materialize a vertical-tab (U+000B)
    # while rebuilding MainWindow.xaml. Remove only XML-illegal control chars;
    # ordinary whitespace and all printable content are preserved.
    $fixed = [regex]::Replace($fixed, '[\x00-\x08\x0B\x0C\x0E-\x1F]', '')
    Write-RepoText $relativePath $fixed
}

# Give the destructive product-domain removal action its own visual hierarchy.
$controlsPath = 'Resources/PanelDialogControls.xaml'
$controls = Read-RepoText $controlsPath
if (-not $controls.Contains('x:Key="DangerDialogButton"')) {
    $anchor = @'
    <Style x:Key="PrimaryDialogButton" TargetType="Button" BasedOn="{StaticResource DialogPrimaryButton}">
        <Setter Property="MinWidth" Value="118" />
    </Style>
'@
    $danger = @'
    <Style x:Key="PrimaryDialogButton" TargetType="Button" BasedOn="{StaticResource DialogPrimaryButton}">
        <Setter Property="MinWidth" Value="118" />
    </Style>

    <Style x:Key="DangerDialogButton" TargetType="Button" BasedOn="{StaticResource DialogButton}">
        <Setter Property="MinWidth" Value="118" />
        <Setter Property="Margin" Value="0" />
        <Setter Property="Foreground" Value="#C42B1C" />
        <Setter Property="Background" Value="#14C42B1C" />
        <Setter Property="BorderBrush" Value="#35C42B1C" />
    </Style>
'@
    if (-not $controls.Contains($anchor.TrimStart("`r", "`n"))) {
        throw 'Unable to locate PrimaryDialogButton style while adding DangerDialogButton.'
    }
    $controls = $controls.Replace($anchor.TrimStart("`r", "`n"), $danger.TrimStart("`r", "`n"))
    Write-RepoText $controlsPath $controls
}

$productPath = 'ProductWebsiteDialog.xaml'
$product = Read-RepoText $productPath
$oldRemove = '<Button x:Name="RemoveButton" Content="移除域名配置" HorizontalAlignment="Left" Margin="0" Click="Remove_Click" />'
$newRemove = '<Button x:Name="RemoveButton" Content="移除域名配置" HorizontalAlignment="Left" Style="{StaticResource DangerDialogButton}" Click="Remove_Click" />'
if ($product.Contains($oldRemove)) {
    $product = $product.Replace($oldRemove, $newRemove)
    Write-RepoText $productPath $product
}
elseif (-not $product.Contains('Style="{StaticResource DangerDialogButton}"')) {
    throw 'Unable to locate the product domain removal action for danger styling.'
}

# Validate every XAML file introduced or rewritten by the modal migration,
# including the main installation/deployment overlay host.
foreach ($relativePath in @(
    'MainWindow.xaml',
    'Resources/PanelDialogControls.xaml',
    'Resources/PanelModalChrome.xaml',
    'PanelMessageDialog.xaml',
    'CustomWebsiteDialog.xaml',
    'ProductWebsiteDialog.xaml',
    'NginxProxyDialog.xaml',
    'PanelInputDialog.xaml'
)) {
    Assert-Xml $relativePath
}

Write-Host 'Normalized, sanitized, styled, and validated unified modal UI XAML.'
