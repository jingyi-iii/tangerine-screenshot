# Generates Assets\icon.png (256x256 tile) and Assets\app.ico.
#
# app.ico carries two treatments of the same scissors:
#   16/20/24/28/32  - transparent background, tangerine glyph, drawn wider and
#                     bolder so it survives the tray and reads on both light and
#                     dark taskbars. Encoded as classic BMP/DIB frames (widest
#                     compatibility; the tray asks for these sizes).
#   48/64/128/256   - the gradient rounded-square tile (PNG frames) used by
#                     Explorer, Alt-Tab, the taskbar and the exe icon.
#
# Style: light, high-end minimal - soft tangerine gradient rounded square + white
# minimal scissors. The tangerine is the app's one accent colour; keep the values
# below in step with AccentColor in Themes\LightTheme.xaml.
Add-Type -AssemblyName System.Drawing

$assets = Join-Path $PSScriptRoot '..\screenshot\Assets'
New-Item -ItemType Directory -Force -Path $assets | Out-Null

# Tangerine ramp — one accent, three stops.
$peel   = [System.Drawing.Color]::FromArgb(255, 0xFB, 0x92, 0x3C)   # #FB923C  top of the gradient
$accent = [System.Drawing.Color]::FromArgb(255, 0xEA, 0x58, 0x0C)   # #EA580C  base (tray glyph, bottom stop)
$flesh  = [System.Drawing.Color]::FromArgb(255, 0xF9, 0x73, 0x16)   # #F97316  mid stop

function New-RoundedRectPath([float]$x, [float]$y, [float]$w, [float]$h, [float]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $p.AddArc($x, $y, $d, $d, 180, 90)
    $p.AddArc($x + $w - $d, $y, $d, $d, 270, 90)
    $p.AddArc($x + $w - $d, $y + $h - $d, $d, $d, 0, 90)
    $p.AddArc($x, $y + $h - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}

# ── Tile artwork (large sizes + app icon): gradient square + white scissors ──
function New-IconPngBytes([int]$size) {
    $s = [double]$size / 256.0
    $bmp = New-Object System.Drawing.Bitmap($size, $size)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

    # Background: vertical gradient rounded square (peel → flesh → accent at the base,
    # interpolated in sRGB the same way the two-stop version was)
    $rect = New-Object System.Drawing.Rectangle(0, 0, $size, $size)
    $path = New-RoundedRectPath 0 0 $size $size (56 * $s)
    $brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, $peel, $accent, 90)
    # Assign rather than pass arrays to New-Object: PowerShell would unroll them
    # again and fail to bind the (int, Color[], float[]) overload.
    $blend = New-Object System.Drawing.Drawing2D.ColorBlend
    $blend.Colors = [System.Drawing.Color[]]@($peel, $flesh, $accent)
    $blend.Positions = [single[]]@(0.0, 0.5, 1.0)
    $brush.InterpolationColors = $blend
    $g.FillPath($brush, $path)
    $brush.Dispose(); $path.Dispose()

    # Scissors: white strokes
    $stroke = [Math]::Max(2.0, 15.0 * $s)
    $pen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, $stroke)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round

    # Blades (from ring centers to opposite tips, crossing in the middle)
    $g.DrawLine($pen, [single](97 * $s), [single](173 * $s), [single](202 * $s), [single](54 * $s))
    $g.DrawLine($pen, [single](159 * $s), [single](173 * $s), [single](54 * $s), [single](54 * $s))

    # Finger rings
    $r = 27 * $s
    $g.DrawEllipse($pen, [single](86 * $s - $r), [single](184 * $s - $r), [single](2 * $r), [single](2 * $r))
    $g.DrawEllipse($pen, [single](170 * $s - $r), [single](184 * $s - $r), [single](2 * $r), [single](2 * $r))

    $pen.Dispose(); $g.Dispose()

    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes   # comma: emit the byte[] as ONE object (PowerShell would else unroll it)
}

# ── Tray glyph: the scissors alone, accent blue on transparent ──
# Geometry lives in a 24x24 space, deliberately wider and with heavier rings than
# the tile so the mark stays legible at 16px. Bounds incl. stroke: x 2..21, y 2.2..21.6.
function New-GlyphBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [Math]::Max(1.0, $size * 0.07)
    $markW = 19.0; $markH = 19.4
    $s = [Math]::Min(($size - 2 * $pad) / $markW, ($size - 2 * $pad) / $markH)
    $tx = ($size - $markW * $s) / 2 - 2.0 * $s
    $ty = ($size - $markH * $s) / 2 - 2.2 * $s

    # Small sizes get a slightly bolder stroke so the glyph survives the tray.
    $penW = [Math]::Max(2.0, $s * 2.0 * $(if ($size -le 20) { 1.15 } else { 1.0 }))
    $pen = New-Object System.Drawing.Pen($accent, [single]$penW)
    $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    function PX([double]$v) { return [single]($tx + $v * $s) }
    function PY([double]$v) { return [single]($ty + $v * $s) }

    $g.DrawLine($pen, (PX 6.5), (PY 16.0), (PX 20.0), (PY 3.2))
    $g.DrawLine($pen, (PX 16.5), (PY 16.0), (PX 3.0), (PY 3.2))
    $g.DrawEllipse($pen, (PX 3.0), (PY 13.6), [single](7.0 * $s), [single](7.0 * $s))
    $g.DrawEllipse($pen, (PX 13.0), (PY 13.6), [single](7.0 * $s), [single](7.0 * $s))

    $pen.Dispose(); $g.Dispose()
    return $bmp
}

# ── Encode an ARGB bitmap as a classic BMP/DIB ico frame (32bpp + AND mask) ──
function ConvertTo-DibFrame([System.Drawing.Bitmap]$bmp) {
    $w = $bmp.Width; $h = $bmp.Height
    $rect = New-Object System.Drawing.Rectangle(0, 0, $w, $h)
    $locked = $bmp.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::ReadOnly, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $stride = $locked.Stride
    $buf = New-Object byte[] ($stride * $h)
    [System.Runtime.InteropServices.Marshal]::Copy($locked.Scan0, $buf, 0, $buf.Length)
    $bmp.UnlockBits($locked)

    $ms = New-Object System.IO.MemoryStream
    $bw = New-Object System.IO.BinaryWriter($ms)
    # BITMAPINFOHEADER (height doubled: XOR image + AND mask)
    $bw.Write([int]40); $bw.Write([int]$w); $bw.Write([int]($h * 2))
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]0)
    $bw.Write([int]($w * $h * 4)); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)

    # XOR pixels, bottom-up, BGRA
    for ($y = $h - 1; $y -ge 0; $y--) { $bw.Write($buf, $y * $stride, $w * 4) }

    # AND mask (1 = transparent), rows padded to 4 bytes
    $maskRow = [int]([Math]::Floor(($w + 31) / 32) * 4)
    $mask = New-Object byte[] ($maskRow * $h)
    for ($y = 0; $y -lt $h; $y++) {
        $srcY = $h - 1 - $y
        for ($x = 0; $x -lt $w; $x++) {
            $a = $buf[$srcY * $stride + $x * 4 + 3]
            if ($a -lt 128) {
                $idx = $y * $maskRow + [int][Math]::Floor($x / 8)
                $mask[$idx] = [byte]($mask[$idx] -bor ([byte](0x80 -shr ($x % 8))))
            }
        }
    }
    $bw.Write($mask, 0, $mask.Length)
    $bw.Flush()
    $bytes = $ms.ToArray()
    $ms.Dispose()
    return ,$bytes
}

# 1) icon.png (256x256 tile)
[byte[]]$png256 = New-IconPngBytes 256
[System.IO.File]::WriteAllBytes((Join-Path $assets 'icon.png'), $png256)

# 2) app.ico - glyph DIB frames (tray) + tile PNG frames (everything else)
$glyphSizes = @(16, 20, 24, 28, 32)
$tileSizes  = @(48, 64, 128, 256)

$frames = @()
foreach ($sz in $glyphSizes) {
    $bmp = New-GlyphBitmap $sz
    $frames += [pscustomobject]@{ W = $sz; H = $sz; Data = (ConvertTo-DibFrame $bmp) }
    $bmp.Dispose()
}
foreach ($sz in $tileSizes) {
    [byte[]]$data = New-IconPngBytes $sz
    $frames += [pscustomobject]@{ W = $sz; H = $sz; Data = $data }
}

$icoPath = Join-Path $assets 'app.ico'
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)
$bw.Write([uint16]0)                 # reserved
$bw.Write([uint16]1)                 # type: icon
$bw.Write([uint16]$frames.Count)     # entry count
$offset = 6 + 16 * $frames.Count
foreach ($f in $frames) {
    [byte]$b = 0; if ($f.W -lt 256) { $b = [byte]$f.W }
    $bw.Write($b)                    # width  (0 = 256)
    $bw.Write($b)                    # height (0 = 256)
    $bw.Write([byte]0)               # color count
    $bw.Write([byte]0)               # reserved
    $bw.Write([uint16]1)             # planes
    $bw.Write([uint16]32)            # bit count
    $bw.Write([uint32]$f.Data.Length)  # bytes in this image
    $bw.Write([uint32]$offset)         # file offset of this image
    $offset += $f.Data.Length
}
foreach ($f in $frames) { $bw.Write($f.Data) }
$bw.Flush()
$bw.Close()

# Validate: System.Drawing.Icon must be able to load it
$icon = New-Object System.Drawing.Icon($icoPath)
Write-Host ("Generated OK: icon.png ({0} bytes), app.ico ({1} entries - {2} glyph DIB + {3} tile PNG)" -f `
    $png256.Length, $frames.Count, $glyphSizes.Count, $tileSizes.Count)
$icon.Dispose()
