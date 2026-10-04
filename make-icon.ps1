# Draws the app icon (red rounded square, white play triangle, white progress bar) at several sizes
# and writes them into one .ico file.
param([string]$Out = (Join-Path $PSScriptRoot 'app.ico'))
Add-Type -AssemblyName System.Drawing

function Draw-Icon([int]$s) {
    $bmp = New-Object System.Drawing.Bitmap $s, $s, ([System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = 'AntiAlias'; $g.PixelOffsetMode = 'HighQuality'; $g.Clear([System.Drawing.Color]::Transparent)
    $r = [Math]::Max(2, $s * 0.22); $d = $r * 2; $w = $s - 1
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $path.AddArc(0, 0, $d, $d, 180, 90); $path.AddArc($w - $d, 0, $d, $d, 270, 90)
    $path.AddArc($w - $d, $w - $d, $d, $d, 0, 90); $path.AddArc(0, $w - $d, $d, $d, 90, 90); $path.CloseFigure()
    $g.FillPath((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 255, 0, 51))), $path)
    $white = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::White)
    $cx = $s * 0.52; $cy = $s * 0.42; $h = $s * 0.40
    $tri = [System.Drawing.PointF[]]@(
        (New-Object System.Drawing.PointF ($cx - $h * 0.42), ($cy - $h / 2)),
        (New-Object System.Drawing.PointF ($cx - $h * 0.42), ($cy + $h / 2)),
        (New-Object System.Drawing.PointF ($cx + $h * 0.50), $cy))
    $g.FillPolygon($white, $tri)
    $barH = [Math]::Max(1.5, $s * 0.075); $barY = $s * 0.76; $barX = $s * 0.2
    $g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(110, 255, 255, 255))), $barX, $barY, ($s * 0.6), $barH)
    $g.FillRectangle($white, $barX, $barY, ($s * 0.34), $barH)
    $g.Dispose()
    return $bmp
}

# Classic DIB entry (BITMAPINFOHEADER + bottom-up BGRA + empty AND mask) so every Windows API can read it.
function Dib-Bytes($bmp) {
    $s = $bmp.Width; $ms = New-Object System.IO.MemoryStream; $bw = New-Object System.IO.BinaryWriter $ms
    $bw.Write([int]40); $bw.Write([int]$s); $bw.Write([int]($s * 2)); $bw.Write([int16]1); $bw.Write([int16]32)
    $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0); $bw.Write([int]0)
    for ($y = $s - 1; $y -ge 0; $y--) { for ($x = 0; $x -lt $s; $x++) { $c = $bmp.GetPixel($x, $y); $bw.Write([byte]$c.B); $bw.Write([byte]$c.G); $bw.Write([byte]$c.R); $bw.Write([byte]$c.A) } }
    $maskRow = [int]([Math]::Ceiling($s / 32.0) * 4)
    $bw.Write((New-Object byte[] ($maskRow * $s)))
    $bw.Flush(); return , $ms.ToArray()
}

$sizes = 16, 20, 24, 32, 40, 48, 64, 256
$images = foreach ($s in $sizes) {
    $bmp = Draw-Icon $s
    , [byte[]](Dib-Bytes $bmp)
    $bmp.Dispose()
}

$fs = [System.IO.File]::Create($Out); $bw = New-Object System.IO.BinaryWriter $fs
$bw.Write([int16]0); $bw.Write([int16]1); $bw.Write([int16]$sizes.Count)
$offset = 6 + 16 * $sizes.Count
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]; $dim = if ($s -ge 256) { 0 } else { $s }
    $bw.Write([byte]$dim); $bw.Write([byte]$dim); $bw.Write([byte]0); $bw.Write([byte]0)
    $bw.Write([int16]1); $bw.Write([int16]32); $bw.Write([int]$images[$i].Length); $bw.Write([int]$offset)
    $offset += $images[$i].Length
}
foreach ($img in $images) { $bw.Write([byte[]]$img) }
$bw.Close()
"Icon written: $Out"

