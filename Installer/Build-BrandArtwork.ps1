param([string]$OutputDirectory)
Add-Type -AssemblyName System.Drawing
$bitmap = New-Object Drawing.Bitmap(162, 162)
$graphics = [Drawing.Graphics]::FromImage($bitmap)
$logo = [Drawing.Image]::FromFile((Join-Path $PSScriptRoot '..\Assets\AppIcon\mcpanel-logo.png'))
try {
    $graphics.Clear([Drawing.ColorTranslator]::FromHtml('#102D55'))
    $graphics.InterpolationMode = 'HighQualityBicubic'
    $graphics.DrawImage($logo, 0, 0, 162, 162)
    $bitmap.Save((Join-Path $OutputDirectory 'Brand.bmp'), [Drawing.Imaging.ImageFormat]::Bmp)
} finally { $logo.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
