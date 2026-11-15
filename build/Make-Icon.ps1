<#
.SYNOPSIS
  Draws the UCR-Next icon (a white gamepad on a blue rounded square) at every Windows icon size
  and writes src\UcrNext.App\Assets\UCR-Next.ico (plus a 256 px PNG preview next to it).
  Run it again after changing the drawing; the .ico is committed, so a normal build doesn't need this.
#>
param([string]$Out = (Join-Path (Split-Path $PSScriptRoot -Parent) 'src\UcrNext.App\Assets\UCR-Next.ico'))
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

function RoundRect([System.Drawing.Drawing2D.GraphicsPath]$p, [float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $d = 2 * $r
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
}

function Draw([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap $size, $size, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.PixelOffsetMode = 'HighQuality'
    $g.Clear([System.Drawing.Color]::Transparent)
    $s = $size / 256.0
    $g.ScaleTransform($s, $s)

    # Background: blue rounded square with a soft top-to-bottom gradient.
    $bg = New-Object System.Drawing.Drawing2D.GraphicsPath
    RoundRect $bg 8 8 240 240 56
    $grad = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 8)), ([System.Drawing.PointF]::new(0, 248)),
        ([System.Drawing.Color]::FromArgb(255, 0x3B, 0x82, 0xF6)), ([System.Drawing.Color]::FromArgb(255, 0x1D, 0x4E, 0xD8))
    $g.FillPath($grad, $bg)

    # Gamepad silhouette: a body with two grips.
    $pad = New-Object System.Drawing.Drawing2D.GraphicsPath
    $pad.FillMode = 'Winding'
    RoundRect $pad 34 84 188 84 40
    $pad.AddEllipse(30, 104, 84, 104)
    $pad.AddEllipse(142, 104, 84, 104)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $g.FillPath($white, $pad)

    $ink = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 0x1E, 0x3A, 0x8A))
    # D-pad (left)
    $g.FillRectangle($ink, 74, 104, 22, 64)
    $g.FillRectangle($ink, 53, 125, 64, 22)
    if ($size -ge 24) {
        # Four face buttons (right); too small to read below 24 px, so one bigger dot there instead.
        foreach ($c in @(@(172, 108), @(194, 130), @(172, 152), @(150, 130))) { $g.FillEllipse($ink, $c[0] - 12, $c[1] - 12, 24, 24) }
    } else {
        $g.FillEllipse($ink, 152, 112, 40, 40)
    }
    $g.Dispose()
    return $bmp
}

# ICO: 16-48 px as 32-bit BMP frames (every Windows API reads those), 64+ as PNG frames.
New-Item -ItemType Directory -Force (Split-Path $Out) | Out-Null
$sizes = 16, 20, 24, 32, 40, 48, 64, 128, 256
$frames = foreach ($sz in $sizes) {
    $bmp = Draw $sz
    if ($sz -ge 64) {
        $ms = New-Object System.IO.MemoryStream
        $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
        $data = $ms.ToArray()
    } else {
        $ms = New-Object System.IO.MemoryStream
        $w = New-Object System.IO.BinaryWriter $ms
        # BITMAPINFOHEADER (height doubled: XOR image + AND mask)
        $w.Write([int]40); $w.Write([int]$sz); $w.Write([int]($sz * 2)); $w.Write([int16]1); $w.Write([int16]32)
        $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0); $w.Write([int]0)
        for ($y = $sz - 1; $y -ge 0; $y--) {          # bottom-up BGRA
            for ($x = 0; $x -lt $sz; $x++) {
                $c = $bmp.GetPixel($x, $y)
                $w.Write([byte]$c.B); $w.Write([byte]$c.G); $w.Write([byte]$c.R); $w.Write([byte]$c.A)
            }
        }
        $maskRow = [int]([math]::Ceiling($sz / 32.0) * 4)  # AND mask: all 0 (alpha decides)
        $w.Write((New-Object byte[] ($maskRow * $sz)))
        $w.Flush()
        $data = $ms.ToArray()
    }
    if ($sz -eq 256) { $bmp.Save([System.IO.Path]::ChangeExtension($Out, '.png'), [System.Drawing.Imaging.ImageFormat]::Png) }
    $bmp.Dispose()
    [pscustomobject]@{ Size = $sz; Data = $data }
}

$fs = [System.IO.File]::Create($Out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([int16]0); $w.Write([int16]1); $w.Write([int16]$frames.Count)   # ICONDIR
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {                                                  # ICONDIRENTRY
    $dim = if ($f.Size -ge 256) { 0 } else { $f.Size }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([int16]1); $w.Write([int16]32); $w.Write([int]$f.Data.Length); $w.Write([int]$offset)
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $w.Write($f.Data) }
$w.Dispose()
Write-Host "icon: $Out ($((Get-Item $Out).Length) bytes, sizes $($sizes -join ', '))"
