# Renders docs/mini-player.png for the README: the player showing a sample song, with an original
# abstract cover drawn here (not real album art). Run build.ps1 first so .\bin\YTMusicMini.exe exists.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'bin\YTMusicMini.exe'
if (-not (Test-Path $exe)) { throw 'Build first: .\build.ps1 -NoInstall' }

# An original abstract cover: deep violet with a warm gold sun, a rose disc and thin cream rings.
$art = Join-Path $env:TEMP 'ytmini-demo-art.png'
$s = 600
$bmp = New-Object System.Drawing.Bitmap $s, $s
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::FromArgb(255, 74, 40, 122))
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 58, 30, 98))), -120, 260, 520, 520)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 232, 176, 75))), 330, 70, 190, 190)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(225, 200, 67, 94))), 120, 300, 230, 230)
$ring = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(170, 243, 227, 195)), 5
foreach ($r in 150, 210, 270) { $g.DrawEllipse($ring, 425 - $r, 165 - $r, 2 * $r, 2 * $r) }
$dot = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 243, 227, 195))
foreach ($p in @(@(80, 90), @(150, 160), @(60, 230), @(520, 420), @(470, 520), @(540, 330))) { $g.FillEllipse($dot, $p[0], $p[1], 10, 10) }
$g.Dispose()
$bmp.Save($art, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$out = Join-Path $PSScriptRoot 'mini-player.png'
$p = Start-Process $exe -PassThru -Wait -ArgumentList @(
    '--preview', '--title', '"Bohemian Rhapsody"', '--artist', '"Queen"',
    '--art', "`"$art`"", '--position', '2:07', '--duration', '5:55', '--snapshot', "`"$out`"")
Remove-Item $art
if (-not (Test-Path $out)) { throw 'Snapshot was not written' }
"Wrote $out"
