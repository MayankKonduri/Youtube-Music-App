# YT Music Mini

A floating mini player for the YouTube Music desktop app on Windows. When you minimize YouTube Music,
a small always-on-top player appears in the corner of the screen; when you open YouTube Music again,
it disappears. Works on Windows 10 and 11.

The mini player shows:
- the song's artwork, title and artist (long titles and artist names slowly slide back and forth)
- previous, play/pause and next buttons
- a red time bar you can click or drag to jump within the song
- a button to reopen YouTube Music, and one to hide the player until the next minimize

Little touches:
- **Colors from the artwork:** the background is a vivid pastel of each song's main artwork color, built in OKLCH (a color model where equal lightness looks equally bright), so every color gets the same light-but-colorful look. It fades to the next song's color.
- **Smooth animations:** it fades and slides in when you minimize, and the artwork crossfades between songs.
- **Sharp on every monitor:** it renders at each monitor's own display scaling, with pixel-snapped text that stays crisp while it scrolls.
- **Snaps to the bottom:** drop it near the bottom edge or a bottom corner and it glides into place. Anywhere else, it stays where you drop it. It remembers the spot, and a snapped corner stays that corner even if monitors or scaling change.
- **Out of the way:** it never takes keyboard focus and stays out of Alt+Tab.

## How it works

- **The app:** YouTube Music installed from the browser ("Open in app" on music.youtube.com in Chrome, Edge or Brave).
- **Detecting minimize:** the mini player listens for Windows minimize/restore events on that window.
- **Song info and controls:** these come from Windows' own media controls (the same source as the volume flyout), so nothing inside YouTube Music is changed and you stay signed in as usual.
- **Choosing the right session:** the session reported by the installed web app is matched against the song title in the YouTube Music window, so a video playing in an ordinary browser tab is ignored. Once found, the app's session id is remembered, so a paused song (whose window title is just "YouTube Music") is still recognized.

## Build and install

No SDK is needed. It's built with the C# compiler that ships with Windows (.NET Framework 4.x).

```powershell
.\build.ps1             # builds, installs to %LOCALAPPDATA%\Programs\YT Music Mini and starts it
.\build.ps1 -NoInstall  # only builds into .\bin
```

The first run adds a "Start with Windows" entry for your user account. To turn it off, right-click the tray icon (in the hidden-icons area by the clock). The tray menu also has **Exit**.

To check the design without touching any media:

```powershell
.\bin\YTMusicMini.exe --preview        # neutral colors
.\bin\YTMusicMini.exe --preview 140    # tinted as if the artwork were green (any hue 0-360)
```

The background style lives in `Theme.BgLightness` and `Theme.BgChroma` in `MiniPlayer.cs`.

## Files

| File | Purpose |
|---|---|
| `MiniPlayer.cs` | The whole app (C# 5, WPF) |
| `build.ps1` | Compile, install and start |
| `make-icon.ps1` | Draws `app.ico` (run automatically by the build) |
| `app.manifest` | Tells Windows the app handles per-monitor display scaling |

Settings and a small log are kept in `%LOCALAPPDATA%\YT Music Mini`.

## Uninstall

1. Right-click the tray icon, untick **Start with Windows**, then choose **Exit**.
2. Delete `%LOCALAPPDATA%\Programs\YT Music Mini` and `%LOCALAPPDATA%\YT Music Mini`.
