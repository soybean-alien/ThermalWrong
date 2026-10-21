# ThermalWrong by soybean_alien
# https://github.com/soybean-alien/ThermalWrong · MIT License
#
# Draws assets\ThermalWrong.ico: a little LCD with an upside-down thermometer on it (the "wrong" in
# ThermalWrong). Writes PNG-compressed icon entries at 256, 48, 32 and 16 px.
#   powershell -File tools\make-icon.ps1
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $root 'assets\ThermalWrong.ico'
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null

function New-RoundRect([single]$x, [single]$y, [single]$w, [single]$h, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $y, $r * 2, $r * 2, 180, 90)
    $p.AddArc($x + $w - $r * 2, $y, $r * 2, $r * 2, 270, 90)
    $p.AddArc($x + $w - $r * 2, $y + $h - $r * 2, $r * 2, $r * 2, 0, 90)
    $p.AddArc($x, $y + $h - $r * 2, $r * 2, $r * 2, 90, 90)
    $p.CloseFigure()
    $p
}

function Draw-Icon([int]$size) {
    $s = $size / 256.0
    $bmp = New-Object System.Drawing.Bitmap $size, $size
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'
    $g.Clear([System.Drawing.Color]::Transparent)

    # The screen: deep-sea gradient with an icy cyan bezel.
    $screen = New-RoundRect (12 * $s) (40 * $s) (232 * $s) (176 * $s) (30 * $s)
    $fill = New-Object System.Drawing.Drawing2D.LinearGradientBrush ([System.Drawing.PointF]::new(0, 40 * $s)), ([System.Drawing.PointF]::new(0, 216 * $s)), ([System.Drawing.Color]::FromArgb(6, 18, 52)), ([System.Drawing.Color]::FromArgb(0, 96, 140))
    $g.FillPath($fill, $screen)
    $bezel = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(110, 235, 255)), ([Math]::Max(1.5, 12 * $s))
    $g.DrawPath($bezel, $screen)

    # The thermometer, upside down: bulb at the top, stem hanging down.
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cx = 128 * $s
    $bulb = 30 * $s
    $g.FillEllipse($white, $cx - $bulb, 66 * $s, $bulb * 2, $bulb * 2)
    $stemW = 26 * $s
    $stem = New-RoundRect ($cx - $stemW / 2) (100 * $s) $stemW (96 * $s) ($stemW / 2)
    $g.FillPath($white, $stem)
    # Mercury: a warm red inside the bulb and part of the stem.
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 72, 72))
    $g.FillEllipse($red, $cx - $bulb * 0.62, 66 * $s + $bulb * 0.38, $bulb * 1.24, $bulb * 1.24)
    if ($size -ge 32) { $g.FillRectangle($red, $cx - $stemW * 0.22, 100 * $s, $stemW * 0.44, 46 * $s) }
    $g.Dispose()
    $bmp
}

$sizes = 256, 48, 32, 16
$pngs = foreach ($size in $sizes) {
    $bmp = Draw-Icon $size
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    , $ms.ToArray()
}

# ICO container: header, one directory entry per image, then the PNG data.
$fs = [System.IO.File]::Create($out)
$w = New-Object System.IO.BinaryWriter $fs
$w.Write([uint16]0); $w.Write([uint16]1); $w.Write([uint16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $dim = if ($sizes[$i] -ge 256) { 0 } else { $sizes[$i] }
    $w.Write([byte]$dim); $w.Write([byte]$dim); $w.Write([byte]0); $w.Write([byte]0)
    $w.Write([uint16]1); $w.Write([uint16]32)
    $w.Write([uint32]$pngs[$i].Length); $w.Write([uint32]$offset)
    $offset += $pngs[$i].Length
}
foreach ($png in $pngs) { $w.Write($png) }
$w.Dispose()
"Wrote $out"
