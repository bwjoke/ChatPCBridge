[CmdletBinding()]
param([string]$OutputDirectory = (Join-Path $PSScriptRoot '..\artifacts\generated-assets'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$null = New-Item -ItemType Directory -Force -Path $OutputDirectory

function Write-BridgeIcon([int]$Size, [string]$Name) {
    $bitmap = [System.Drawing.Bitmap]::new($Size, $Size)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $background = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#145D60'))
    $foreground = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::White)
    $accent = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#A8F0C5'))
    $path = [System.Drawing.Drawing2D.GraphicsPath]::new()
    $margin = [single]($Size * 0.04)
    $diameter = [single]($Size * 0.30)
    $edge = [single]($Size - $margin * 2)
    $path.AddArc($margin, $margin, $diameter, $diameter, 180, 90)
    $path.AddArc($margin + $edge - $diameter, $margin, $diameter, $diameter, 270, 90)
    $path.AddArc($margin + $edge - $diameter, $margin + $edge - $diameter, $diameter, $diameter, 0, 90)
    $path.AddArc($margin, $margin + $edge - $diameter, $diameter, $diameter, 90, 90)
    $path.CloseFigure()
    $graphics.FillPath($background, $path)
    # An archive sheet with an arrow: no Tencent/OpenAI logo or trademark artwork.
    $graphics.FillRectangle($foreground, [single]($Size*.24), [single]($Size*.20), [single]($Size*.40), [single]($Size*.59))
    for ($row = 0; $row -lt 5; $row++) {
        $graphics.FillRectangle($background, [single]($Size*(.395 + .04*($row % 2))), [single]($Size*(.25 + .067*$row)), [single]($Size*.055), [single]($Size*.048))
    }
    $points = [System.Drawing.PointF[]]@(
        [System.Drawing.PointF]::new($Size*.56, $Size*.59),
        [System.Drawing.PointF]::new($Size*.73, $Size*.59),
        [System.Drawing.PointF]::new($Size*.73, $Size*.48),
        [System.Drawing.PointF]::new($Size*.88, $Size*.66),
        [System.Drawing.PointF]::new($Size*.73, $Size*.84),
        [System.Drawing.PointF]::new($Size*.73, $Size*.73),
        [System.Drawing.PointF]::new($Size*.56, $Size*.73)
    )
    $graphics.FillPolygon($accent, $points)
    try { $bitmap.Save((Join-Path $OutputDirectory $Name), [System.Drawing.Imaging.ImageFormat]::Png) }
    finally { $path.Dispose(); $accent.Dispose(); $foreground.Dispose(); $background.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
}
Write-BridgeIcon 50 'StoreLogo.png'
Write-BridgeIcon 44 'Square44x44Logo.png'
Write-BridgeIcon 150 'Square150x150Logo.png'
Write-BridgeIcon 256 'Icon256.png'
Write-Host "Icons written to $OutputDirectory"
