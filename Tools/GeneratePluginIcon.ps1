Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Drawing

$outputPath = Join-Path $PSScriptRoot '..\Dalamud-Plugin\InstantEdit\icon.png'
$outputPath = [System.IO.Path]::GetFullPath($outputPath)
$outputDirectory = Split-Path -Parent $outputPath
if (-not (Test-Path -LiteralPath $outputDirectory)) {
    New-Item -ItemType Directory -Path $outputDirectory | Out-Null
}

$size = 512
$bitmap = [System.Drawing.Bitmap]::new($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$graphics.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
$graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality

function New-Points([object[]] $pairs) {
    Write-Output -NoEnumerate ([System.Drawing.PointF[]]@($pairs | ForEach-Object { [System.Drawing.PointF]::new($_[0], $_[1]) }))
}

try {
    # Deep navy badge.
    $bounds = [System.Drawing.Rectangle]::new(8, 8, 496, 496)
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        $bounds,
        [System.Drawing.Color]::FromArgb(255, 8, 18, 43),
        [System.Drawing.Color]::FromArgb(255, 20, 42, 74),
        45)
    $graphics.FillEllipse($background, $bounds)
    $background.Dispose()

    $ringPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(180, 92, 224, 238), 7)
    $graphics.DrawEllipse($ringPen, 15, 15, 482, 482)
    $ringPen.Dispose()

    # Subtle orbital arcs keep the background alive without losing small-size clarity.
    $arcPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(70, 109, 227, 242), 4)
    $graphics.DrawArc($arcPen, 57, 68, 398, 355, 198, 112)
    $graphics.DrawArc($arcPen, 75, 95, 360, 320, 18, 105)
    $arcPen.Dispose()

    # Isometric model cube: top, left, and right faces.
    $top = New-Points @(@(256, 103), @(398, 181), @(256, 259), @(114, 181))
    $left = New-Points @(@(114, 181), @(256, 259), @(256, 413), @(114, 335))
    $right = New-Points @(@(398, 181), @(256, 259), @(256, 413), @(398, 335))

    $topBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 55, 206, 226))
    $leftBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 16, 111, 157))
    $rightBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 25, 154, 188))
    $graphics.FillPolygon($topBrush, $top)
    $graphics.FillPolygon($leftBrush, $left)
    $graphics.FillPolygon($rightBrush, $right)
    $topBrush.Dispose(); $leftBrush.Dispose(); $rightBrush.Dispose()

    $edgePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 170, 249, 255), 7)
    $edgePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPolygon($edgePen, $top)
    $graphics.DrawPolygon($edgePen, $left)
    $graphics.DrawPolygon($edgePen, $right)
    $graphics.DrawLine($edgePen, 256, 259, 256, 413)
    $edgePen.Dispose()

    # Wireframe construction lines signal editable 3D geometry.
    $wirePen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(160, 213, 254, 255), 3)
    $wirePen.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
    $graphics.DrawLine($wirePen, 114, 181, 256, 336)
    $graphics.DrawLine($wirePen, 398, 181, 256, 336)
    $graphics.DrawLine($wirePen, 256, 103, 256, 259)
    $wirePen.Dispose()

    # Gold instant-action bolt/arrow over the model.
    $boltShadow = New-Points @(@(289, 128), @(181, 267), @(247, 267), @(221, 389), @(342, 228), @(276, 228))
    $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(180, 0, 10, 28))
    $graphics.FillPolygon($shadowBrush, $boltShadow)
    $shadowBrush.Dispose()

    $bolt = New-Points @(@(280, 113), @(171, 252), @(238, 252), @(212, 374), @(341, 213), @(271, 213))
    $boltBrush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(170, 112, 172, 263),
        [System.Drawing.Color]::FromArgb(255, 255, 241, 143),
        [System.Drawing.Color]::FromArgb(255, 239, 156, 43),
        90)
    $graphics.FillPolygon($boltBrush, $bolt)
    $boltBrush.Dispose()
    $boltPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 203, 91), 5)
    $boltPen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $graphics.DrawPolygon($boltPen, $bolt)
    $boltPen.Dispose()

    # Four tiny corner nodes reinforce the editing/transform motif.
    foreach ($point in @(@(114, 181), @(398, 181), @(114, 335), @(398, 335))) {
        $nodeBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 234, 255, 255))
        $graphics.FillEllipse($nodeBrush, $point[0] - 8, $point[1] - 8, 16, 16)
        $nodeBrush.Dispose()
    }

    $bitmap.Save($outputPath, [System.Drawing.Imaging.ImageFormat]::Png)
}
finally {
    $graphics.Dispose()
    $bitmap.Dispose()
}

Write-Output "Generated $outputPath"
