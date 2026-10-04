# Renders the README images:
#   docs/desktop.png         the player in the corner of a simulated Windows 11 desktop
#   docs/mini-player.png     a close-up of the player
#   docs/compact-player.png  a close-up of the compact player
# The song is "Clair de Lune" by Claude Debussy (public domain), and the cover is an original moonlit
# picture drawn here, not real album art. Run build.ps1 first so .\bin\YTMusicMini.exe exists.
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$root = Split-Path $PSScriptRoot -Parent
$exe = Join-Path $root 'bin\YTMusicMini.exe'
if (-not (Test-Path $exe)) { throw 'Build first: .\build.ps1 -NoInstall' }

# Original cover: a pale moon over a night-blue sky and its reflection on water, with a few stars.
$art = Join-Path $env:TEMP 'ytmini-demo-art.png'
$s = 600
$bmp = New-Object System.Drawing.Bitmap $s, $s
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.Clear([System.Drawing.Color]::FromArgb(255, 28, 40, 96))
$g.FillRectangle((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 20, 30, 74))), 0, 390, $s, 210)
$halo = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(40, 242, 230, 184))
foreach ($r in 150, 120, 100) { $g.FillEllipse($halo, 300 - $r, 200 - $r, 2 * $r, 2 * $r) }
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 242, 230, 184))), 225, 125, 150, 150)
$g.FillEllipse((New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(255, 226, 212, 160))), 270, 160, 34, 34)
$shine = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(150, 242, 230, 184))
$y = 410; $w = 170
while ($y -lt 590) { $g.FillRectangle($shine, 300 - $w / 2, $y, $w, 6); $y += 18; $w = [Math]::Max(30, $w - 22) }
$star = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(230, 240, 240, 255))
foreach ($p in @(@(70, 60), @(140, 140), @(520, 90), @(470, 300), @(90, 300), @(540, 220), @(200, 50), @(420, 40))) { $g.FillEllipse($star, $p[0], $p[1], 7, 7) }
$g.Dispose()
$bmp.Save($art, [System.Drawing.Imaging.ImageFormat]::Png)
$bmp.Dispose()

$song = @('--preview', '--title', '"Clair de Lune"', '--artist', '"Claude Debussy"', '--art', "`"$art`"", '--position', '1:47', '--duration', '5:02')
$shots = @(
    @{ File = 'desktop.png';     Extra = @('--desktop') },
    @{ File = 'mini-player.png'; Extra = @() },
    @{ File = 'compact-player.png'; Extra = @('--compact') }
)
foreach ($shot in $shots) {
    $out = Join-Path $PSScriptRoot $shot.File
    if (Test-Path $out) { Remove-Item $out }
    Start-Process $exe -Wait -ArgumentList ($song + $shot.Extra + @('--snapshot', "`"$out`""))
    if (-not (Test-Path $out)) { throw "Snapshot was not written: $out" }
    "Wrote $out"
}
Remove-Item $art
