# Builds YT Music Mini with the C# compiler that ships with Windows (no SDK needed), installs it to
# %LOCALAPPDATA%\Programs\YT Music Mini and starts it. Use -NoInstall to only compile into .\bin.
param([switch]$NoInstall)
$ErrorActionPreference = 'Stop'
$src = $PSScriptRoot
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$wm = 'C:\Windows\System32\WinMetadata'

$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) { & (Join-Path $src 'make-icon.ps1') -Out $ico }

$bin = Join-Path $src 'bin'
New-Item -ItemType Directory -Force $bin | Out-Null
$exe = Join-Path $bin 'YTMusicMini.exe'
$refs = "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll", "$fw\WPF\WindowsBase.dll", "$fw\System.Xaml.dll",
        "$fw\System.Windows.Forms.dll", "$fw\System.Drawing.dll", "$fw\System.Runtime.dll", "$fw\System.Runtime.WindowsRuntime.dll",
        "$fw\System.Runtime.InteropServices.WindowsRuntime.dll", "$wm\Windows.Media.winmd", "$wm\Windows.Foundation.winmd", "$wm\Windows.Storage.winmd"
$cscArgs = @('-nologo', '-target:winexe', '-optimize+', "-out:$exe", "-win32icon:$ico", "-resource:$ico,app.ico") +
           ($refs | ForEach-Object { "-r:$_" }) + (Join-Path $src 'MiniPlayer.cs')
& "$fw\csc.exe" @cscArgs | Where-Object { $_ -notmatch 'only supports language versions up to C# 5|newer versions of the C# programming language|go.microsoft.com/fwlink/\?LinkID=533240|^\s*$' }
if ($LASTEXITCODE -ne 0) { throw 'Build failed' }
"Built $exe"
if ($NoInstall) { return }

$dest = Join-Path $env:LOCALAPPDATA 'Programs\YT Music Mini'
$running = Get-Process YTMusicMini -ErrorAction SilentlyContinue
if ($running) { $running | Stop-Process -Force; $running | Wait-Process -Timeout 5 -ErrorAction SilentlyContinue }
New-Item -ItemType Directory -Force $dest | Out-Null
Copy-Item $exe (Join-Path $dest 'YTMusicMini.exe') -Force
Start-Process (Join-Path $dest 'YTMusicMini.exe')
"Installed and started: $dest\YTMusicMini.exe"
