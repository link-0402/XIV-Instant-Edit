Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$outputDirectory = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\Dalamud-Plugin\InstantEdit'))

function New-Points([object[]] $pairs) {
    Write-Output -NoEnumerate ([System.Drawing.PointF[]]@($pairs | ForEach-Object { [System.Drawing.PointF]::new($_[0], $_[1]) }))
}

function Draw-Bolt($g, [int] $x, [int] $y, [double] $scale) {
    $bolt = New-Points @(
        @(($x + (36 * $scale)), ($y + (0 * $scale))),
        @(($x - (58 * $scale)), ($y + (123 * $scale))),
        @(($x + (0 * $scale)), ($y + (123 * $scale))),
        @(($x - (22 * $scale)), ($y + (235 * $scale))),
        @(($x + (92 * $scale)), ($y + (91 * $scale))),
        @(($x + (35 * $scale)), ($y + (91 * $scale)))
    )
    $shadow = [System.Drawing.PointF[]]@($bolt | ForEach-Object { [System.Drawing.PointF]::new($_.X + 9, $_.Y + 11) })
    $shadowBrush = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(150, 0, 5, 18))
    $g.FillPolygon($shadowBrush, $shadow)
    $shadowBrush.Dispose()
    $brush = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new($x - 60, $y, 160, 240),
        [System.Drawing.Color]::FromArgb(255, 255, 241, 148),
        [System.Drawing.Color]::FromArgb(255, 238, 151, 37),
        90)
    $g.FillPolygon($brush, $bolt)
    $brush.Dispose()
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 207, 100), [float](5 * $scale))
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $g.DrawPolygon($pen, $bolt)
    $pen.Dispose()
}

function New-Canvas {
    $bitmap = [System.Drawing.Bitmap]::new(512, 512, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bitmap)
    $null = $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $null = $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $null = $g.CompositingQuality = [System.Drawing.Drawing2D.CompositingQuality]::HighQuality
    $null = $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $background = [System.Drawing.Drawing2D.LinearGradientBrush]::new(
        [System.Drawing.Rectangle]::new(0, 0, 512, 512),
        [System.Drawing.Color]::FromArgb(255, 7, 15, 35),
        [System.Drawing.Color]::FromArgb(255, 18, 48, 72),
        35)
    $g.FillRectangle($background, 0, 0, 512, 512)
    $background.Dispose()
    return [pscustomobject]@{ Bitmap = $bitmap; Graphics = $g }
}

function Save-Variant([string] $name, [scriptblock] $draw) {
    $canvas = New-Canvas
    $bitmap = $canvas.Bitmap; $g = $canvas.Graphics
    try {
        & $draw $g
        $bitmap.Save((Join-Path $outputDirectory $name), [System.Drawing.Imaging.ImageFormat]::Png)
    } finally {
        $g.Dispose(); $bitmap.Dispose()
    }
}

# Variant 1: lightning bolt and an edit cursor, communicating “instant edit”.
Save-Variant 'icon-iteration-cursor.png' {
    param($g)
    $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 79, 226, 235), 13)
    $cyan.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round
    $cursor = New-Points @(@(332, 288), @(385, 417), @(414, 393), @(366, 265), @(416, 270), @(355, 209), @(339, 294))
    $g.DrawPolygon($cyan, $cursor)
    $cyan.Dispose()
    Draw-Bolt -g $g -x 226 -y 105 -scale 1.35
    $spark = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 132, 250, 255))
    foreach ($p in @(@(105, 130), @(402, 111), @(414, 355))) { $g.FillEllipse($spark, $p[0] - 8, $p[1] - 8, 16, 16) }
    $spark.Dispose()
}

# Variant 2: bolt between two directional arrows, communicating send and return.
Save-Variant 'icon-iteration-transfer.png' {
    param($g)
    $cyan = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 73, 218, 232), 18)
    $cyan.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $cyan.EndCap = [System.Drawing.Drawing2D.LineCap]::Triangle
    $g.DrawLine($cyan, 65, 172, 172, 172)
    $g.DrawLine($cyan, 340, 340, 447, 340)
    $cyan.Dispose()
    Draw-Bolt -g $g -x 256 -y 116 -scale 1.45
    $gold = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 255, 187, 67), 10)
    $gold.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $gold.EndCap = [System.Drawing.Drawing2D.LineCap]::Triangle
    $g.DrawLine($gold, 340, 172, 447, 172)
    $g.DrawLine($gold, 65, 340, 172, 340)
    $gold.Dispose()
}

# Variant 3: bolt with a simple pencil/edit stroke and sparks.
Save-Variant 'icon-iteration-pencil.png' {
    param($g)
    $stroke = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 79, 224, 237), 17)
    $stroke.StartCap = [System.Drawing.Drawing2D.LineCap]::Round; $stroke.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $g.DrawLine($stroke, 93, 382, 148, 437)
    $g.DrawLine($stroke, 148, 437, 412, 173)
    $stroke.Dispose()
    $eraser = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(255, 21, 110, 148))
    $g.FillPolygon($eraser, (New-Points @(@(82, 372), @(103, 351), @(171, 419), @(149, 441))))
    $eraser.Dispose()
    Draw-Bolt -g $g -x 265 -y 87 -scale 1.38
    $sparkPen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb(255, 140, 247, 255), 9)
    $g.DrawLine($sparkPen, 109, 107, 109, 147); $g.DrawLine($sparkPen, 89, 127, 129, 127)
    $g.DrawLine($sparkPen, 416, 352, 416, 386); $g.DrawLine($sparkPen, 399, 369, 433, 369)
    $sparkPen.Dispose()
}

Write-Output "Generated three icon iterations in $outputDirectory"
