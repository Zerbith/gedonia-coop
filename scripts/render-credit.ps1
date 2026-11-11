# Renders the main-menu credit "Coop Mod by Zerbith" in the look of the game's title logo
# (gold gradient sampled from LogoPng3, dark brown rim, warm glow) to src\GedoniaCoop\res\credit.png.
# The PNG is embedded in the plugin dll by build.ps1. Font: Almendra Bold (SIL OFL, google/fonts),
# only needed here, not shipped: downloads\fonts\Almendra-Bold.ttf (-Font picks another file there).
param([string]$Text = 'Coop Mod by Zerbith', [float]$Em = 120, [string]$Font = 'Almendra-Bold.ttf', [string]$Out)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Drawing
$fonts = New-Object System.Drawing.Text.PrivateFontCollection
$fonts.AddFontFile("$root\downloads\fonts\$Font")
$family = $fonts.Families[0]

# Text outline as a path, measured to size the canvas.
$path = New-Object System.Drawing.Drawing2D.GraphicsPath
$fmt = [System.Drawing.StringFormat]::GenericTypographic
$path.AddString($Text, $family, [int][System.Drawing.FontStyle]::Regular, $Em, (New-Object System.Drawing.PointF(0, 0)), $fmt)
$r = $path.GetBounds()
$pad = [int]($Em * 0.32)
$w = [int][Math]::Ceiling($r.Width) + 2 * $pad
$h = [int][Math]::Ceiling($r.Height) + 2 * $pad
$m = New-Object System.Drawing.Drawing2D.Matrix
$m.Translate($pad - $r.X, $pad - $r.Y)
$path.Transform($m)
$r = $path.GetBounds()

$bmp = New-Object System.Drawing.Bitmap($w, $h, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.CompositingQuality = 'HighQuality'
$g.Clear([System.Drawing.Color]::Transparent)

function Pen($argb, $width) {
    $p = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb($argb), $width)
    $p.LineJoin = 'Round'; $p.StartCap = 'Round'; $p.EndCap = 'Round'
    return $p
}

# Warm glow: wide soft strokes, faint and orange outside, stronger and yellow inside.
$steps = 14
for ($i = $steps; $i -ge 1; $i--) {
    $t = $i / $steps
    $a = [int](10 + 14 * (1 - $t))
    $col = ($a -shl 24) -bor 0xFFB030
    $g.DrawPath((Pen $col ($Em * 0.05 + $Em * 0.42 * $t)), $path)
}
# Dark rim (logo: #221D1A), then a thinner deep-brown edge inside it.
$g.DrawPath((Pen 0xFF1E1813 ($Em * 0.10)), $path)
$g.DrawPath((Pen 0xFF5A3208 ($Em * 0.035)), $path)

# Gold fill, top to bottom as on the logo's letters.
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush((New-Object System.Drawing.PointF(0, $r.Top)), (New-Object System.Drawing.PointF(0, $r.Bottom)), [System.Drawing.Color]::White, [System.Drawing.Color]::Black)
$blend = New-Object System.Drawing.Drawing2D.ColorBlend(5)
$blend.Colors = @([System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xFC, 0xDB), [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xE8, 0x66),
    [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xCC, 0x3A), [System.Drawing.Color]::FromArgb(0xFF, 0xFF, 0xB8, 0x25), [System.Drawing.Color]::FromArgb(0xFF, 0xF7, 0x96, 0x00))
$blend.Positions = [float[]]@(0, 0.28, 0.52, 0.74, 1)
$brush.InterpolationColors = $blend
$brush.WrapMode = 'TileFlipX'
$g.FillPath($brush, $path)
# A light top edge, like the shine on the logo's letters.
$g.SetClip($path)
$shine = New-Object System.Drawing.Drawing2D.Matrix
$shine.Translate(0, $Em * 0.025)
$under = $path.Clone(); $under.Transform($shine)
$g.DrawPath((Pen 0x66FFFFF0 ($Em * 0.03)), $path)
$g.ResetClip()

$out = if ($Out) { $Out } else { "$root\src\GedoniaCoop\res\credit.png" }
New-Item -ItemType Directory -Force (Split-Path $out) | Out-Null
$bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
"rendered $out ${w}x${h}"
