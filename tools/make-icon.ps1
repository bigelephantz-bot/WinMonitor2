<#
.SYNOPSIS
    Generates src\WinMonitor\app.ico: a flat red rounded square with a white thermometer.

.DESCRIPTION
    Renders 16/20/24/32/40/48/64/128/256 px frames with System.Drawing (anti-aliased,
    32bpp ARGB) and stores each frame PNG-compressed inside a single multi-image .ico.
    Windows PowerShell 5.1 compatible. Run from the repo root:

        powershell.exe -NoProfile -ExecutionPolicy Bypass -File tools\make-icon.ps1
#>

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$repoRoot = Split-Path -Parent $PSScriptRoot
$outPath  = Join-Path $repoRoot 'src\WinMonitor\app.ico'
$sizes    = 16, 20, 24, 32, 40, 48, 64, 128, 256
$red      = [System.Drawing.Color]::FromArgb(255, 0xD9, 0x30, 0x25)  # flat red #D93025

function New-RoundedRectPath {
    param([single]$X, [single]$Y, [single]$W, [single]$H, [single]$Radius)
    $d = $Radius * 2
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($X, $Y, $d, $d, 180, 90)
    $p.AddArc($X + $W - $d, $Y, $d, $d, 270, 90)
    $p.AddArc($X + $W - $d, $Y + $H - $d, $d, $d, 0, 90)
    $p.AddArc($X, $Y + $H - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

function New-CapsulePath {
    # Vertical capsule (rounded at both ends) centered at $Cx, spanning $Top..$Bottom.
    param([single]$Cx, [single]$Top, [single]$Bottom, [single]$W)
    $x = $Cx - ($W / 2)
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $p.AddArc($x, $Top, $W, $W, 180, 180)
    $p.AddArc($x, $Bottom - $W, $W, $W, 0, 180)
    $p.CloseFigure()
    return $p
}

function New-IconFramePng {
    param([int]$S)
    $bmp        = New-Object System.Drawing.Bitmap($S, $S, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g          = [System.Drawing.Graphics]::FromImage($bmp)
    $redBrush   = New-Object System.Drawing.SolidBrush($red)
    $whiteBrush = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
    try {
        $g.SmoothingMode   = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
        $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
        $g.Clear([System.Drawing.Color]::Transparent)

        # Background: flat red rounded square (corner radius ~22%), transparent corners, no border.
        $bg = New-RoundedRectPath -X 0 -Y 0 -W $S -H $S -Radius ($S * 0.22)
        $g.FillPath($redBrush, $bg)
        $bg.Dispose()

        # Thermometer geometry (fractions of tile size), centered horizontally.
        $cx      = $S / 2.0
        $bulbD   = $S * 0.30      # bulb diameter ~30%
        $bulbCY  = $S * 0.68      # bulb center Y
        $stemW   = $S * 0.18      # stem width ~18%
        $stemTop = $S * 0.16

        # White stem capsule (rounded cap on top; bottom end hidden under the bulb).
        $stem = New-CapsulePath -Cx $cx -Top $stemTop -Bottom $bulbCY -W $stemW
        $g.FillPath($whiteBrush, $stem)
        $stem.Dispose()

        # Red 'mercury' channel in the lower half of the stem, drawn before the bulb
        # so the solid white bulb caps its bottom. Dropped below 24 px: at tiny sizes
        # the plain capsule + bulb silhouette stays readable.
        if ($S -ge 24) {
            $ch = New-CapsulePath -Cx $cx -Top ($S * 0.36) -Bottom $bulbCY -W ($S * 0.08)
            $g.FillPath($redBrush, $ch)
            $ch.Dispose()
        }

        # Solid white bulb overlapping the stem bottom.
        $g.FillEllipse($whiteBrush, $cx - ($bulbD / 2), $bulbCY - ($bulbD / 2), $bulbD, $bulbD)

        $msPng = New-Object System.IO.MemoryStream
        $bmp.Save($msPng, [System.Drawing.Imaging.ImageFormat]::Png)
        $bytes = $msPng.ToArray()
        $msPng.Dispose()
        return ,$bytes
    }
    finally {
        $whiteBrush.Dispose()
        $redBrush.Dispose()
        $g.Dispose()
        $bmp.Dispose()
    }
}

# Render every frame.
$frames = New-Object 'System.Collections.Generic.List[byte[]]'
foreach ($s in $sizes) {
    $frames.Add((New-IconFramePng -S $s))
}

# Assemble the .ico: ICONDIR + ICONDIRENTRY table + PNG frame data.
# PNG-compressed entries are valid in .ico; a width/height byte of 0 means 256.
$ms = New-Object System.IO.MemoryStream
$bw = New-Object System.IO.BinaryWriter($ms)
$bw.Write([UInt16]0)               # reserved, must be 0
$bw.Write([UInt16]1)               # type 1 = icon
$bw.Write([UInt16]$sizes.Count)    # image count
$offset = 6 + (16 * $sizes.Count)  # first frame starts after the directory
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim)                  # width  (0 = 256)
    $bw.Write([byte]$dim)                  # height (0 = 256)
    $bw.Write([byte]0)                     # palette color count (0 = no palette)
    $bw.Write([byte]0)                     # reserved
    $bw.Write([UInt16]1)                   # color planes
    $bw.Write([UInt16]32)                  # bits per pixel
    $bw.Write([UInt32]$frames[$i].Length)  # bytes in resource
    $bw.Write([UInt32]$offset)             # offset of frame data from file start
    $offset += $frames[$i].Length
}
foreach ($png in $frames) {
    $bw.Write($png)
}
$bw.Flush()
[System.IO.File]::WriteAllBytes($outPath, $ms.ToArray())
$bw.Dispose()
$ms.Dispose()

# Validate: sane size, and the .ico must load through GDI+ (both default and 16 px).
$file = Get-Item -LiteralPath $outPath
if ($file.Length -lt 5KB) {
    throw "app.ico is suspiciously small ($($file.Length) bytes)."
}
$icon = New-Object System.Drawing.Icon($outPath)
$icon.Dispose()
$icon16 = New-Object System.Drawing.Icon($outPath, 16, 16)
$icon16.Dispose()
Write-Host ("OK: {0} ({1} bytes, {2} frames: {3})" -f $outPath, $file.Length, $sizes.Count, ($sizes -join ', '))
