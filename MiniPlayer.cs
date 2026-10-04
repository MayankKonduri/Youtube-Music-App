// YT Music Mini: a floating mini player that appears whenever the YouTube Music app (the Chrome/Edge
// "Open in app" window) is minimized, and hides again when it is restored.
// Now-playing info and the controls come from Windows' media system (the same source as the volume
// flyout), so nothing inside YouTube Music is modified.
// Built with the C# compiler that ships with Windows (.NET Framework 4.x, C# 5) - see build.ps1.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using Microsoft.Win32;
using Windows.Media.Control;
using Windows.Storage.Streams;
using WinForms = System.Windows.Forms;
using AsyncStatus = Windows.Foundation.AsyncStatus;

namespace YTMusicMini
{
    static class Native
    {
        public delegate void WinEventProc(IntPtr hook, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);
        public delegate bool EnumProc(IntPtr hwnd, IntPtr lParam);

        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc proc, uint pid, uint tid, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);
        [DllImport("user32.dll")] public static extern int GetWindowTextLength(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc proc, IntPtr lParam);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);
        [DllImport("user32.dll")] public static extern int GetWindowLong(IntPtr hwnd, int index);
        [DllImport("user32.dll")] public static extern int SetWindowLong(IntPtr hwnd, int index, int value);

        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int SW_RESTORE = 9, GWL_EXSTYLE = -20;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
        public const uint GW_OWNER = 4;

        public static string Title(IntPtr hwnd)
        {
            int n = GetWindowTextLength(hwnd);
            if (n <= 0) return "";
            var sb = new StringBuilder(n + 1);
            GetWindowText(hwnd, sb, sb.Capacity);
            return sb.ToString();
        }
    }

    // Finds the YouTube Music app window: a top-level Chrome/Edge/Brave window whose title mentions
    // YouTube Music but which is not an ordinary browser window (those end in "- Google Chrome" etc.).
    static class YtWindow
    {
        static readonly string[] Browsers = { "chrome", "msedge", "brave" };

        public static bool Is(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero || !Native.IsWindow(hwnd)) return false;
            if (Native.GetWindow(hwnd, Native.GW_OWNER) != IntPtr.Zero) return false;
            string t = Native.Title(hwnd);
            if (t.IndexOf("YouTube Music", StringComparison.OrdinalIgnoreCase) < 0) return false;
            if (t.EndsWith("Google Chrome") || t.EndsWith("Edge") || t.EndsWith("Brave")) return false;
            uint pid;
            Native.GetWindowThreadProcessId(hwnd, out pid);
            try
            {
                string name = Process.GetProcessById((int)pid).ProcessName.ToLowerInvariant();
                return Array.IndexOf(Browsers, name) >= 0;
            }
            catch { return false; }
        }

        public static IntPtr Find()
        {
            IntPtr found = IntPtr.Zero;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (Native.IsWindowVisible(h) && Is(h)) { found = h; return false; }
                return true;
            }, IntPtr.Zero);
            return found;
        }
    }

    static class Log
    {
        static readonly object Gate = new object();
        public static string Dir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YT Music Mini");

        public static void Write(string line)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Dir);
                    string path = System.IO.Path.Combine(Dir, "log.txt");
                    if (File.Exists(path) && new FileInfo(path).Length > 200000) File.Delete(path);
                    File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + line + Environment.NewLine);
                }
            }
            catch { }
        }
    }

    static class WinRt
    {
        // Waits for a WinRT async operation on the calling (UI) thread, so every WinRT object stays on one thread.
        public static async Task<T> Run<T>(global::Windows.Foundation.IAsyncOperation<T> op)
        {
            while (op.Status == AsyncStatus.Started) await Task.Delay(8);
            if (op.Status == AsyncStatus.Completed) return op.GetResults();
            if (op.Status == AsyncStatus.Error) throw op.ErrorCode;
            throw new TaskCanceledException();
        }
    }

    // A small frame-synced tween: calls step(0..1, eased out) every frame for the given duration.
    class Anim
    {
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly double ms;
        readonly Action<double> step;
        readonly Action done;
        bool running = true;

        Anim(double ms, Action<double> step, Action done) { this.ms = ms; this.step = step; this.done = done; }

        public static Anim Run(double ms, Action<double> step, Action done)
        {
            var a = new Anim(ms, step, done);
            CompositionTarget.Rendering += a.Frame;
            return a;
        }

        void Frame(object sender, EventArgs e)
        {
            if (!running) return;
            double p = ms <= 0 ? 1 : Math.Min(1, clock.Elapsed.TotalMilliseconds / ms);
            step(1 - Math.Pow(1 - p, 3));
            if (p >= 1)
            {
                Stop();
                if (done != null) done();
            }
        }

        public void Stop()
        {
            if (!running) return;
            running = false;
            CompositionTarget.Rendering -= Frame;
        }

        public static void Stop(Anim a) { if (a != null) a.Stop(); }
    }

    // Finds the main hue of the artwork, ignoring dark, washed-out and gray pixels.
    static class ArtColor
    {
        public static double? DominantHue(BitmapSource src)
        {
            try
            {
                double scale = 40.0 / Math.Max(src.PixelWidth, src.PixelHeight);
                var small = new FormatConvertedBitmap(new TransformedBitmap(src, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
                int w = small.PixelWidth, h = small.PixelHeight;
                var px = new byte[w * h * 4];
                small.CopyPixels(px, w * 4, 0);

                var weight = new double[36];
                var cos = new double[36];
                var sin = new double[36];
                int colorful = 0;
                for (int i = 0; i < px.Length; i += 4)
                {
                    double b = px[i] / 255.0, g = px[i + 1] / 255.0, r = px[i + 2] / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double v = max, s = max <= 0 ? 0 : (max - min) / max;
                    if (v < 0.18 || s < 0.18) continue;
                    double hue;
                    double d = max - min;
                    if (max == r) hue = 60 * (((g - b) / d) % 6);
                    else if (max == g) hue = 60 * ((b - r) / d + 2);
                    else hue = 60 * ((r - g) / d + 4);
                    if (hue < 0) hue += 360;
                    double wgt = s * s * v;
                    int bin = (int)(hue / 10) % 36;
                    weight[bin] += wgt;
                    cos[bin] += Math.Cos(hue * Math.PI / 180) * wgt;
                    sin[bin] += Math.Sin(hue * Math.PI / 180) * wgt;
                    colorful++;
                }
                if (colorful < 0.06 * (w * h)) return null;

                int best = 0;
                double bestScore = -1;
                for (int i = 0; i < 36; i++)
                {
                    double score = weight[i] + 0.5 * (weight[(i + 35) % 36] + weight[(i + 1) % 36]);
                    if (score > bestScore) { bestScore = score; best = i; }
                }
                double c = 0, sn = 0;
                for (int k = -1; k <= 1; k++) { int i = (best + k + 36) % 36; c += cos[i]; sn += sin[i]; }
                double result = Math.Atan2(sn, c) * 180 / Math.PI;
                return result < 0 ? result + 360 : result;
            }
            catch { return null; }
        }
    }

    // All colors of the player, derived from the artwork's hue. The time bar always stays red.
    class Theme
    {
        // The background style: saturation and lightness applied to the artwork's main hue.
        // Lightness under 0.5 gives a dark player with light text; above gives a light one with dark text.
        public const double Saturation = 0.38, Lightness = 0.86;

        public Color Bg, Border, Title, Sub, Icon, PlayBg, PlayBgHover, PlayFg, Track, Time, Hover, Press, Placeholder;

        static Color Hsl(double h, double s, double l)
        {
            l = Math.Max(0, Math.Min(1, l));
            double c = (1 - Math.Abs(2 * l - 1)) * s, hp = (h % 360) / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1)), m = l - c / 2;
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            return Color.FromRgb((byte)Math.Round((r + m) * 255), (byte)Math.Round((g + m) * 255), (byte)Math.Round((b + m) * 255));
        }

        public static Theme For(double? hue)
        {
            double h = hue.HasValue ? hue.Value : 0;
            double tint = hue.HasValue ? 1 : 0;
            double s = Saturation * tint, l = Lightness;
            bool dark = l < 0.5;
            var t = new Theme();
            t.Bg = Hsl(h, s, l);
            t.Border = dark ? Hsl(h, s, l + 0.12) : Hsl(h, s, l - 0.09);
            t.Title = dark ? Colors.White : Hsl(h, 0.30 * tint, 0.13);
            t.Sub = dark ? Hsl(h, 0.12 * tint, 0.74) : Hsl(h, 0.14 * tint, 0.36);
            t.Icon = dark ? Colors.White : Hsl(h, 0.30 * tint, 0.16);
            t.PlayBg = dark ? Colors.White : Hsl(h, 0.30 * tint, 0.16);
            t.PlayBgHover = dark ? Color.FromRgb(0xE3, 0xE3, 0xE3) : Hsl(h, 0.30 * tint, 0.28);
            t.PlayFg = dark ? Color.FromRgb(0x11, 0x11, 0x11) : Colors.White;
            t.Track = dark ? Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x29, 0, 0, 0);
            t.Time = dark ? Hsl(h, 0.10 * tint, 0.70) : Hsl(h, 0.12 * tint, 0.38);
            t.Hover = dark ? Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x1A, 0, 0, 0);
            t.Press = dark ? Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x30, 0, 0, 0);
            t.Placeholder = dark ? Hsl(h, s, l + 0.08) : Hsl(h, s, l - 0.08);
            return t;
        }

        public static Theme Default()
        {
            // The original dark look, used until the first artwork arrives.
            var t = new Theme();
            t.Bg = Color.FromRgb(0x20, 0x20, 0x20);
            t.Border = Color.FromRgb(0x3A, 0x3A, 0x3A);
            t.Title = Colors.White;
            t.Sub = Color.FromRgb(0xAA, 0xAA, 0xAA);
            t.Icon = Colors.White;
            t.PlayBg = Colors.White;
            t.PlayBgHover = Color.FromRgb(0xE3, 0xE3, 0xE3);
            t.PlayFg = Color.FromRgb(0x11, 0x11, 0x11);
            t.Track = Color.FromRgb(0x4D, 0x4D, 0x4D);
            t.Time = Color.FromRgb(0xAA, 0xAA, 0xAA);
            t.Hover = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
            t.Press = Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF);
            t.Placeholder = Color.FromRgb(0x33, 0x33, 0x33);
            return t;
        }

        static Color Mix(Color a, Color b, double p)
        {
            return Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * p), (byte)Math.Round(a.R + (b.R - a.R) * p),
                                  (byte)Math.Round(a.G + (b.G - a.G) * p), (byte)Math.Round(a.B + (b.B - a.B) * p));
        }

        public static Theme Lerp(Theme a, Theme b, double p)
        {
            var t = new Theme();
            t.Bg = Mix(a.Bg, b.Bg, p); t.Border = Mix(a.Border, b.Border, p); t.Title = Mix(a.Title, b.Title, p);
            t.Sub = Mix(a.Sub, b.Sub, p); t.Icon = Mix(a.Icon, b.Icon, p); t.PlayBg = Mix(a.PlayBg, b.PlayBg, p);
            t.PlayBgHover = Mix(a.PlayBgHover, b.PlayBgHover, p); t.PlayFg = Mix(a.PlayFg, b.PlayFg, p);
            t.Track = Mix(a.Track, b.Track, p); t.Time = Mix(a.Time, b.Time, p); t.Hover = Mix(a.Hover, b.Hover, p);
            t.Press = Mix(a.Press, b.Press, p); t.Placeholder = Mix(a.Placeholder, b.Placeholder, p);
            return t;
        }
    }

    // Now-playing state for the YouTube Music session, read from Windows' media controls.
    class Media
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession session;
        string artKey = "";
        string appId = "";

        public string Title = "", Artist = "";
        public bool Playing, CanSeek, CanNext, CanPrev;
        public TimeSpan Position, Duration;
        public DateTimeOffset UpdatedAt;
        public ImageSource Art;
        public double? Hue;
        public int ArtVersion;

        public async Task Init()
        {
            manager = await WinRt.Run(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        }

        static bool IsBrowser(string id)
        {
            id = (id ?? "").ToLowerInvariant();
            return id.Contains("chrome") || id.Contains("edge") || id.Contains("brave") || id.Contains("_crx_");
        }

        // Picks the YouTube Music session. Installed web apps report an id like "Chrome._crx_<app>", while
        // ordinary tabs report just "Chrome", so: the app's own session whose song title is in the window
        // title wins; a plain tab only counts if its title matches the window title.
        public async Task<bool> Refresh(string windowTitle)
        {
            if (manager == null) return false;
            GlobalSystemMediaTransportControlsSession best = null;
            GlobalSystemMediaTransportControlsSessionMediaProperties bestProps = null;
            int bestScore = 0;
            var seen = new List<string>();
            foreach (var s in manager.GetSessions())
            {
                string id = s.SourceAppUserModelId ?? "";
                if (!IsBrowser(id)) continue;
                GlobalSystemMediaTransportControlsSessionMediaProperties p;
                try { p = await WinRt.Run(s.TryGetMediaPropertiesAsync()); }
                catch { continue; }
                string t = p.Title ?? "";
                seen.Add(id + ":" + t);
                bool titleMatch = t.Length > 1 && windowTitle.IndexOf(t, StringComparison.OrdinalIgnoreCase) >= 0;
                bool webApp = id.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) >= 0;
                int score = (titleMatch ? 10 : 0) + (webApp ? 4 : 0) + (id == appId && appId.Length > 0 ? 4 : 0);
                if (!titleMatch && id != appId) score = 0;
                if (score > bestScore) { best = s; bestProps = p; bestScore = score; }
            }
            if (best != null && bestScore >= 10 && best.SourceAppUserModelId.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) >= 0)
                appId = best.SourceAppUserModelId;
            if (best == null)
            {
                if (session != null) Log.Write("No session matches window '" + windowTitle + "'. Sessions: " + string.Join(" | ", seen));
                session = null;
                return false;
            }
            if (session == null) Log.Write("Using session '" + bestProps.Title + "' for window '" + windowTitle + "'");
            session = best;

            Title = bestProps.Title ?? "";
            Artist = bestProps.Artist ?? "";
            if (Artist.Length == 0) Artist = bestProps.AlbumTitle ?? "";
            var info = best.GetPlaybackInfo();
            Playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            CanSeek = info.Controls.IsPlaybackPositionEnabled;
            CanNext = info.Controls.IsNextEnabled;
            CanPrev = info.Controls.IsPreviousEnabled;
            var tl = best.GetTimelineProperties();
            Position = tl.Position;
            Duration = tl.EndTime - tl.StartTime;
            UpdatedAt = tl.LastUpdatedTime;

            string key = Title + "\n" + Artist;
            if (key != artKey)
            {
                if (bestProps.Thumbnail != null)
                {
                    try
                    {
                        var stream = await WinRt.Run(bestProps.Thumbnail.OpenReadAsync());
                        var reader = new DataReader(stream);
                        uint n = await WinRt.Run<uint>(reader.LoadAsync((uint)stream.Size));
                        var bytes = new byte[n];
                        reader.ReadBytes(bytes);
                        var bmp = new BitmapImage();
                        bmp.BeginInit();
                        bmp.CacheOption = BitmapCacheOption.OnLoad;
                        bmp.StreamSource = new MemoryStream(bytes);
                        bmp.DecodePixelHeight = 160;
                        bmp.EndInit();
                        bmp.Freeze();
                        Art = bmp;
                        Hue = ArtColor.DominantHue(bmp);
                        artKey = key;
                        ArtVersion++;
                    }
                    catch (Exception ex) { Log.Write("Artwork failed: " + ex.Message); }
                }
                else if (Art != null)
                {
                    Art = null;
                    ArtVersion++;
                }
            }
            return true;
        }

        // Where the song is right now: the last reported position plus time elapsed since, while playing.
        public TimeSpan Now()
        {
            TimeSpan p = Position;
            if (Playing && UpdatedAt.Year > 2000)
            {
                TimeSpan since = DateTimeOffset.Now - UpdatedAt;
                if (since > TimeSpan.Zero && since < TimeSpan.FromHours(6)) p += since;
            }
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            if (p < TimeSpan.Zero) p = TimeSpan.Zero;
            return p;
        }

        public async Task TogglePlay()
        {
            if (session == null) return;
            Position = Now(); UpdatedAt = DateTimeOffset.Now; Playing = !Playing;
            try { await WinRt.Run(session.TryTogglePlayPauseAsync()); } catch { }
        }

        public async Task Next() { if (session != null) try { await WinRt.Run(session.TrySkipNextAsync()); } catch { } }
        public async Task Previous() { if (session != null) try { await WinRt.Run(session.TrySkipPreviousAsync()); } catch { } }

        public async Task SeekTo(TimeSpan target)
        {
            if (session == null || !CanSeek) return;
            Position = target; UpdatedAt = DateTimeOffset.Now;
            try { await WinRt.Run(session.TryChangePlaybackPositionAsync(target.Ticks)); } catch { }
        }
    }

    class MiniWindow : Window
    {
        const string Xaml = @"
<Border xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
        x:Name='Root' Padding='9' CornerRadius='8' BorderThickness='1' TextOptions.TextFormattingMode='Display'>
  <Border.Resources>
    <SolidColorBrush x:Key='HoverBrush' Color='#26FFFFFF'/>
    <SolidColorBrush x:Key='PressBrush' Color='#40FFFFFF'/>
    <SolidColorBrush x:Key='PlayBgBrush' Color='#FFFFFF'/>
    <SolidColorBrush x:Key='PlayBgHoverBrush' Color='#E3E3E3'/>
    <Style x:Key='Icon' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='FontSize' Value='14'/>
      <Setter Property='Width' Value='28'/>
      <Setter Property='Height' Value='28'/>
      <Setter Property='Cursor' Value='Hand'/>
      <Setter Property='Focusable' Value='False'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bg' Background='Transparent' CornerRadius='14'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='{DynamicResource HoverBrush}'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bg' Property='Background' Value='{DynamicResource PressBrush}'/></Trigger>
              <Trigger Property='IsEnabled' Value='False'><Setter Property='Opacity' Value='0.35'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
    <Style x:Key='Small' TargetType='Button' BasedOn='{StaticResource Icon}'>
      <Setter Property='Width' Value='22'/>
      <Setter Property='Height' Value='22'/>
      <Setter Property='FontSize' Value='10'/>
      <Setter Property='Foreground' Value='#AAAAAA'/>
    </Style>
    <Style x:Key='Play' TargetType='Button' BasedOn='{StaticResource Icon}'>
      <Setter Property='Foreground' Value='#111111'/>
      <Setter Property='Width' Value='30'/>
      <Setter Property='Height' Value='30'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bg' Background='{DynamicResource PlayBgBrush}' CornerRadius='15'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='{DynamicResource PlayBgHoverBrush}'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bg' Property='Opacity' Value='0.85'/></Trigger>
            </ControlTemplate.Triggers>
          </ControlTemplate>
        </Setter.Value>
      </Setter>
    </Style>
  </Border.Resources>
  <Grid>
    <Grid.ColumnDefinitions>
      <ColumnDefinition Width='70'/>
      <ColumnDefinition Width='10'/>
      <ColumnDefinition Width='*'/>
    </Grid.ColumnDefinitions>
    <Grid x:Name='ArtHost' Width='70' Height='70' Cursor='Hand' ToolTip='Open YouTube Music'>
      <Border x:Name='ArtBack' CornerRadius='6'/>
      <Border x:Name='ArtFront' CornerRadius='6'/>
      <TextBlock x:Name='ArtGlyph' Text='&#xE8D6;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24'
                 HorizontalAlignment='Center' VerticalAlignment='Center'/>
    </Grid>
    <Grid Grid.Column='2'>
      <Grid.RowDefinitions>
        <RowDefinition Height='Auto'/>
        <RowDefinition Height='*'/>
      </Grid.RowDefinitions>
      <Grid>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <StackPanel Margin='0,0,6,0'>
          <Canvas x:Name='TitleBox' Height='18' ClipToBounds='True'>
            <TextBlock x:Name='TitleText' Text='Nothing playing' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13' FontWeight='SemiBold'
                       TextOptions.TextFormattingMode='Ideal'>
              <TextBlock.RenderTransform><TranslateTransform/></TextBlock.RenderTransform>
            </TextBlock>
          </Canvas>
          <Canvas x:Name='ArtistBox' Height='16' ClipToBounds='True' Margin='0,1,0,0'>
            <TextBlock x:Name='ArtistText' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='12' TextOptions.TextFormattingMode='Ideal'>
              <TextBlock.RenderTransform><TranslateTransform/></TextBlock.RenderTransform>
            </TextBlock>
          </Canvas>
        </StackPanel>
        <StackPanel Grid.Column='1' Orientation='Horizontal' VerticalAlignment='Top' Margin='0,-4,-4,0'>
          <Button x:Name='RestoreBtn' Style='{StaticResource Small}' Content='&#xE8A7;' ToolTip='Open YouTube Music'/>
          <Button x:Name='CloseBtn' Style='{StaticResource Small}' Content='&#xE711;' ToolTip='Hide until the next minimize'/>
        </StackPanel>
      </Grid>
      <Grid Grid.Row='1' VerticalAlignment='Bottom'>
        <Grid.ColumnDefinitions>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='8'/>
          <ColumnDefinition Width='Auto'/>
          <ColumnDefinition Width='*'/>
          <ColumnDefinition Width='Auto'/>
        </Grid.ColumnDefinitions>
        <Button x:Name='PrevBtn' Style='{StaticResource Icon}' Content='&#xE892;' ToolTip='Previous'/>
        <Button x:Name='PlayBtn' Grid.Column='1' Style='{StaticResource Play}' Content='&#xE769;' Margin='2,0' ToolTip='Play / pause'/>
        <Button x:Name='NextBtn' Grid.Column='2' Style='{StaticResource Icon}' Content='&#xE893;' ToolTip='Next'/>
        <TextBlock x:Name='CurText' Grid.Column='4' Text='0:00' FontSize='10' FontFamily='Segoe UI' VerticalAlignment='Center' Margin='0,0,6,0'/>
        <Grid x:Name='Seek' Grid.Column='5' Height='16' Background='Transparent' Cursor='Hand' VerticalAlignment='Center'>
          <Border x:Name='Track' Height='3' CornerRadius='1.5' VerticalAlignment='Center'/>
          <Border x:Name='Fill' Height='3' CornerRadius='1.5' Background='#FF0033' HorizontalAlignment='Left' Width='0' VerticalAlignment='Center'/>
          <Ellipse x:Name='Knob' Width='10' Height='10' Fill='#FF0033' HorizontalAlignment='Left' VerticalAlignment='Center' Visibility='Hidden'/>
        </Grid>
        <TextBlock x:Name='DurText' Grid.Column='6' Text='0:00' FontSize='10' FontFamily='Segoe UI' VerticalAlignment='Center' Margin='6,0,0,0'/>
      </Grid>
    </Grid>
  </Grid>
</Border>";

        // Scrolling text: speed in pixels per second and how long it rests at each end.
        const double MarqueeSpeed = 16, MarqueeHold = 2.4, MarqueeEndPad = 14;
        // Opacity while the mouse isn't over the player.
        const double IdleAlpha = 0.90;

        public Border ArtBack, ArtFront, Fill, Track;
        public Grid ArtHost, Seek;
        public Canvas TitleBox, ArtistBox;
        public TextBlock ArtGlyph, TitleText, ArtistText, CurText, DurText;
        public Button PlayBtn, PrevBtn, NextBtn, RestoreBtn, CloseBtn;
        public Ellipse Knob;
        public bool Dragging, Hiding;
        public double DragFraction;
        public event Action<double> SeekRequested;
        public event Action DragFinished;

        readonly Border root;
        Brush placeholder = Brushes.Transparent;
        double alpha;
        Anim fadeAnim, moveAnim, themeAnim;
        Theme shown;
        ImageSource currentArt;

        public MiniWindow()
        {
            Title = "YT Music Mini";
            Width = 340;
            Height = 90;
            // A transparent window that draws its own rounded card, so the whole player can fade.
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Opacity = 0;

            root = (Border)XamlReader.Parse(Xaml);
            Content = root;
            ArtHost = (Grid)root.FindName("ArtHost");
            ArtBack = (Border)root.FindName("ArtBack");
            ArtFront = (Border)root.FindName("ArtFront");
            ArtGlyph = (TextBlock)root.FindName("ArtGlyph");
            TitleBox = (Canvas)root.FindName("TitleBox");
            ArtistBox = (Canvas)root.FindName("ArtistBox");
            TitleText = (TextBlock)root.FindName("TitleText");
            ArtistText = (TextBlock)root.FindName("ArtistText");
            CurText = (TextBlock)root.FindName("CurText");
            DurText = (TextBlock)root.FindName("DurText");
            PlayBtn = (Button)root.FindName("PlayBtn");
            PrevBtn = (Button)root.FindName("PrevBtn");
            NextBtn = (Button)root.FindName("NextBtn");
            RestoreBtn = (Button)root.FindName("RestoreBtn");
            CloseBtn = (Button)root.FindName("CloseBtn");
            Seek = (Grid)root.FindName("Seek");
            Fill = (Border)root.FindName("Fill");
            Track = (Border)root.FindName("Track");
            Knob = (Ellipse)root.FindName("Knob");

            // Drag the player by any empty area; it snaps when let go near the bottom of the screen.
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                Anim.Stop(moveAnim);
                try { DragMove(); } catch { }
                if (DragFinished != null) DragFinished();
            };

            // Slightly see-through until the mouse is over it.
            MouseEnter += delegate { if (IsVisible && !Hiding) FadeTo(1, 140, null); };
            MouseLeave += delegate { if (IsVisible && !Hiding && !Dragging) FadeTo(IdleAlpha, 280, null); };

            Seek.MouseEnter += delegate { Track.Height = 4; Fill.Height = 4; Knob.Visibility = Visibility.Visible; };
            Seek.MouseLeave += delegate { if (!Dragging) { Track.Height = 3; Fill.Height = 3; Knob.Visibility = Visibility.Hidden; } };
            Seek.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                Dragging = true;
                Seek.CaptureMouse();
                DragFraction = FractionAt(e.GetPosition(Seek).X);
                ShowFraction(DragFraction);
                e.Handled = true;
            };
            Seek.MouseMove += delegate(object s, MouseEventArgs e)
            {
                if (!Dragging) return;
                DragFraction = FractionAt(e.GetPosition(Seek).X);
                ShowFraction(DragFraction);
            };
            Seek.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e)
            {
                if (!Dragging) return;
                Dragging = false;
                Seek.ReleaseMouseCapture();
                if (!Seek.IsMouseOver) { Track.Height = 3; Fill.Height = 3; Knob.Visibility = Visibility.Hidden; }
                if (SeekRequested != null) SeekRequested(DragFraction);
                e.Handled = true;
            };

            ApplyTheme(Theme.Default(), false);
        }

        double FractionAt(double x)
        {
            double w = Seek.ActualWidth;
            if (w <= 0) return 0;
            return Math.Max(0, Math.Min(1, x / w));
        }

        public void ShowFraction(double f)
        {
            double w = Seek.ActualWidth * f;
            Fill.Width = w;
            Knob.Margin = new Thickness(Math.Max(0, w - 5), 0, 0, 0);
        }

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            // Out of Alt+Tab and never steals focus from what you're typing in.
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
        }

        void SetAlpha(double a)
        {
            alpha = Math.Max(0, Math.Min(1, a));
            Opacity = alpha;
        }

        void FadeTo(double target, double ms, Action done)
        {
            Anim.Stop(fadeAnim);
            double from = alpha;
            fadeAnim = Anim.Run(ms, p => SetAlpha(from + (target - from) * p), done);
        }

        // Fades in while sliding up a little into place.
        public void AnimateIn(double left, double top)
        {
            Anim.Stop(fadeAnim);
            Anim.Stop(moveAnim);
            Hiding = false;
            Left = left;
            Top = top + 14;
            if (!IsVisible) { SetAlpha(0); Show(); }
            double fromTop = Top;
            FadeTo(IsMouseOver ? 1 : IdleAlpha, 230, null);
            moveAnim = Anim.Run(280, p => Top = fromTop + (top - fromTop) * p, null);
            RefreshMarquees();
        }

        // Fades out while dipping down a little, then hides.
        public void AnimateOut()
        {
            if (!IsVisible || Hiding) return;
            Hiding = true;
            Anim.Stop(moveAnim);
            double fromTop = Top;
            moveAnim = Anim.Run(170, p => Top = fromTop + 10 * p, null);
            FadeTo(0, 170, delegate
            {
                Hide();
                Top = fromTop;
                Hiding = false;
                StopMarquees();
            });
        }

        public void GlideTo(double left, double top, Action done)
        {
            Anim.Stop(moveAnim);
            double fromL = Left, fromT = Top;
            moveAnim = Anim.Run(180, p => { Left = fromL + (left - fromL) * p; Top = fromT + (top - fromT) * p; }, done);
        }

        public void ApplyTheme(Theme target, bool animate)
        {
            Anim.Stop(themeAnim);
            Theme from = shown ?? target;
            if (!animate) { SetColors(target); return; }
            themeAnim = Anim.Run(450, p => SetColors(Theme.Lerp(from, target, p)), null);
        }

        static Brush B(Color c)
        {
            var b = new SolidColorBrush(c);
            b.Freeze();
            return b;
        }

        // Fresh brushes every time (WPF locks brushes that styles and templates have used, so they
        // can't be recolored in place). Called once per frame while a theme change animates.
        void SetColors(Theme t)
        {
            shown = t;
            root.Background = B(t.Bg);
            root.BorderBrush = B(t.Border);
            TitleText.Foreground = B(t.Title);
            Brush sub = B(t.Sub), icon = B(t.Icon), time = B(t.Time);
            ArtistText.Foreground = sub;
            RestoreBtn.Foreground = sub;
            CloseBtn.Foreground = sub;
            ArtGlyph.Foreground = sub;
            PrevBtn.Foreground = icon;
            NextBtn.Foreground = icon;
            PlayBtn.Foreground = B(t.PlayFg);
            CurText.Foreground = time;
            DurText.Foreground = time;
            Track.Background = B(t.Track);
            root.Resources["HoverBrush"] = B(t.Hover);
            root.Resources["PressBrush"] = B(t.Press);
            root.Resources["PlayBgBrush"] = B(t.PlayBg);
            root.Resources["PlayBgHoverBrush"] = B(t.PlayBgHover);
            Brush newPlaceholder = B(t.Placeholder);
            if (ArtFront.Background == placeholder || ArtFront.Background == null) ArtFront.Background = newPlaceholder;
            if (ArtBack.Background == placeholder) ArtBack.Background = newPlaceholder;
            placeholder = newPlaceholder;
        }

        // Crossfades from the previous artwork to the new one.
        public void SetArt(ImageSource img)
        {
            if (img == currentArt) return;
            currentArt = img;
            ArtGlyph.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
            ArtBack.Background = ArtFront.Background;
            ArtFront.Background = img != null ? (Brush)new ImageBrush(img) { Stretch = Stretch.UniformToFill } : placeholder;
            ArtFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
        }

        public void SetText(string titleText, string artistText, bool animate)
        {
            if (TitleText.Text == titleText && ArtistText.Text == artistText) return;
            TitleText.Text = titleText;
            ArtistText.Text = artistText;
            if (animate)
            {
                var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260));
                TitleText.BeginAnimation(OpacityProperty, fade);
                ArtistText.BeginAnimation(OpacityProperty, fade);
            }
            RefreshMarquees();
        }

        public void RefreshMarquees()
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                if (!IsVisible) return;
                Marquee(TitleBox, TitleText, 0);
                Marquee(ArtistBox, ArtistText, 1.1);
            }));
        }

        public void StopMarquees()
        {
            foreach (var tb in new[] { TitleText, ArtistText })
            {
                var tt = (TranslateTransform)tb.RenderTransform;
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = 0;
            }
        }

        // Text that doesn't fit slides to its end, rests, slides back, rests, and repeats.
        // Text that fits stays still.
        void Marquee(Canvas box, TextBlock tb, double delaySeconds)
        {
            var tt = (TranslateTransform)tb.RenderTransform;
            tt.BeginAnimation(TranslateTransform.XProperty, null);
            tt.X = 0;
            tb.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = tb.DesiredSize.Width - box.ActualWidth;
            if (box.ActualWidth <= 0 || over <= 1)
            {
                box.OpacityMask = null;
                return;
            }
            // Fade the right edge so the cut-off text looks intentional.
            double fadeStart = Math.Max(0, 1 - MarqueeEndPad / box.ActualWidth);
            var mask = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 0) };
            mask.GradientStops.Add(new GradientStop(Colors.Black, 0));
            mask.GradientStops.Add(new GradientStop(Colors.Black, fadeStart));
            mask.GradientStops.Add(new GradientStop(Colors.Transparent, 1));
            box.OpacityMask = mask;

            double distance = over + MarqueeEndPad;
            double move = Math.Max(1.6, distance / MarqueeSpeed), hold = MarqueeHold;
            var ease = new SineEase { EasingMode = EasingMode.EaseInOut };
            var a = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever, BeginTime = TimeSpan.FromSeconds(delaySeconds) };
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(hold + move)), ease));
            a.KeyFrames.Add(new LinearDoubleKeyFrame(-distance, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * hold + move))));
            a.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(2 * hold + 2 * move)), ease));
            tt.BeginAnimation(TranslateTransform.XProperty, a);
        }
    }

    class Settings
    {
        static string FilePath { get { return System.IO.Path.Combine(Log.Dir, "settings.txt"); } }
        public double Left = double.NaN, Top = double.NaN;
        public bool StartupConfigured;

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                if (!File.Exists(FilePath)) return s;
                foreach (string line in File.ReadAllLines(FilePath))
                {
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq).Trim(), v = line.Substring(eq + 1).Trim();
                    double d;
                    if (k == "left" && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Left = d;
                    if (k == "top" && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Top = d;
                    if (k == "startupConfigured") s.StartupConfigured = v == "1";
                }
            }
            catch { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Log.Dir);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                File.WriteAllText(FilePath,
                    "left=" + (double.IsNaN(Left) ? "" : Left.ToString(ci)) + "\r\n" +
                    "top=" + (double.IsNaN(Top) ? "" : Top.ToString(ci)) + "\r\n" +
                    "startupConfigured=" + (StartupConfigured ? "1" : "0") + "\r\n");
            }
            catch { }
        }
    }

    class Controller
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string RunName = "YT Music Mini";
        // Snapping: distance from the screen edge when snapped, and how close counts as "near".
        const double SnapMargin = 14, SnapReach = 40;

        readonly Media media = new Media();
        readonly Settings settings = Settings.Load();
        MiniWindow win;
        WinForms.NotifyIcon tray;
        WinForms.ToolStripMenuItem startupItem;
        Native.WinEventProc hookProc;
        IntPtr hook;
        IntPtr yt = IntPtr.Zero;
        bool watching, dismissed, refreshing;
        DispatcherTimer timer;
        int ticks, misses;
        string shownKey = "";
        int shownArtVersion;

        void CreateWindow()
        {
            win = new MiniWindow();
            new WindowInteropHelper(win).EnsureHandle();
        }

        public async void Start()
        {
            Log.Write("Started");
            CreateWindow();
            win.PlayBtn.Click += async delegate { await media.TogglePlay(); Render(); SoonRefresh(); };
            win.NextBtn.Click += async delegate { await media.Next(); SoonRefresh(); };
            win.PrevBtn.Click += async delegate { await media.Previous(); SoonRefresh(); };
            win.RestoreBtn.Click += delegate { RestoreYt(); };
            win.CloseBtn.Click += delegate { dismissed = true; HidePlayer(); };
            win.ArtHost.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            win.ArtHost.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; RestoreYt(); };
            win.SeekRequested += async delegate(double f)
            {
                if (media.Duration > TimeSpan.Zero) await media.SeekTo(TimeSpan.FromTicks((long)(media.Duration.Ticks * f)));
                Render();
                SoonRefresh();
            };
            win.DragFinished += OnDragFinished;

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate { Tick(); };

            SetupTray();
            if (!settings.StartupConfigured)
            {
                SetStartup(true);
                settings.StartupConfigured = true;
                settings.Save();
            }

            try { await media.Init(); }
            catch (Exception ex) { Log.Write("Media init failed: " + ex.Message); }

            hookProc = OnWinEvent;
            hook = Native.SetWinEventHook(Native.EVENT_SYSTEM_MINIMIZESTART, Native.EVENT_SYSTEM_MINIMIZEEND, IntPtr.Zero, hookProc, 0, 0,
                Native.WINEVENT_OUTOFCONTEXT | Native.WINEVENT_SKIPOWNPROCESS);

            // YouTube Music may already be minimized when this starts.
            IntPtr existing = YtWindow.Find();
            if (existing != IntPtr.Zero && Native.IsIconic(existing)) BeginWatching(existing);
        }

        // "--preview": shows the player with sample data (no media access), to check the look.
        public void Preview(double? hue)
        {
            CreateWindow();
            win.CloseBtn.Click += delegate { Application.Current.Shutdown(); };
            win.DragFinished += OnDragFinished;
            win.SetText("Evadu Evadu (From the Original Motion Picture Soundtrack)", "Harvey Spector • Premam (Original Motion Picture Soundtrack)", false);
            win.CurText.Text = "1:32";
            win.DurText.Text = "3:48";
            win.ApplyTheme(Theme.For(hue), false);
            double l, t;
            TargetPosition(out l, out t);
            win.AnimateIn(l, t);
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { win.ShowFraction(0.4); }));
        }

        void OnWinEvent(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0) return;
            if (evt == Native.EVENT_SYSTEM_MINIMIZESTART && YtWindow.Is(hwnd)) BeginWatching(hwnd);
            else if (evt == Native.EVENT_SYSTEM_MINIMIZEEND && hwnd == yt) StopWatching();
        }

        void BeginWatching(IntPtr hwnd)
        {
            yt = hwnd;
            watching = true;
            dismissed = false;
            ticks = 0;
            misses = 0;
            timer.Start();
            Refresh();
        }

        void StopWatching()
        {
            watching = false;
            timer.Stop();
            HidePlayer();
        }

        void Tick()
        {
            if (!watching) return;
            if (!Native.IsWindow(yt) || !Native.IsIconic(yt)) { StopWatching(); return; }
            ticks++;
            if (ticks % 4 == 0) Refresh();
            else if (win.IsVisible) RenderPosition();
        }

        void SoonRefresh()
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            t.Tick += delegate { t.Stop(); Refresh(); };
            t.Start();
        }

        async void Refresh()
        {
            if (refreshing || !watching) return;
            refreshing = true;
            bool ok = false;
            try { ok = await media.Refresh(Native.Title(yt)); }
            catch (Exception ex) { Log.Write("Refresh failed: " + ex.Message); }
            refreshing = false;
            if (!watching) return;
            if (ok)
            {
                misses = 0;
                if (!dismissed) { Render(); ShowPlayer(); }
            }
            // Between songs YouTube Music's media session briefly disappears; only hide if it stays gone.
            else if (++misses >= 4 || !win.IsVisible) HidePlayer();
        }

        void ShowPlayer()
        {
            if (win.IsVisible && !win.Hiding) return;
            double l, t;
            TargetPosition(out l, out t);
            win.AnimateIn(l, t);
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(RenderPosition));
        }

        void HidePlayer()
        {
            win.AnimateOut();
        }

        // The saved spot if it's still on a screen, otherwise the bottom-right corner.
        void TargetPosition(out double l, out double t)
        {
            l = settings.Left;
            t = settings.Top;
            bool onScreen = !double.IsNaN(l) && !double.IsNaN(t)
                && l >= SystemParameters.VirtualScreenLeft - 20 && t >= SystemParameters.VirtualScreenTop - 20
                && l + win.Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 20
                && t + win.Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 20;
            if (!onScreen)
            {
                Rect wa = SystemParameters.WorkArea;
                l = wa.Right - win.Width - SnapMargin;
                t = wa.Bottom - win.Height - SnapMargin;
            }
        }

        Rect WorkArea()
        {
            var src = PresentationSource.FromVisual(win);
            if (src == null || src.CompositionTarget == null) return SystemParameters.WorkArea;
            var wa = WinForms.Screen.FromHandle(new WindowInteropHelper(win).Handle).WorkingArea;
            Matrix m = src.CompositionTarget.TransformFromDevice;
            return new Rect(m.Transform(new Point(wa.Left, wa.Top)), m.Transform(new Point(wa.Right, wa.Bottom)));
        }

        // Let go near the bottom edge and it settles onto it; near a bottom corner and it settles into
        // the corner. Anywhere else it stays where it was dropped.
        void OnDragFinished()
        {
            Rect wa = WorkArea();
            double l = win.Left, t = win.Top;
            double bottom = wa.Bottom - SnapMargin - win.Height;
            double leftEdge = wa.Left + SnapMargin, rightEdge = wa.Right - SnapMargin - win.Width;
            if (t >= bottom - SnapReach)
            {
                t = bottom;
                if (l <= leftEdge + SnapReach) l = leftEdge;
                else if (l >= rightEdge - SnapReach) l = rightEdge;
            }
            win.GlideTo(l, t, delegate
            {
                settings.Left = l;
                settings.Top = t;
                settings.Save();
            });
        }

        void Render()
        {
            string key = media.Title + "\n" + media.Artist;
            bool changed = key != shownKey;
            win.SetText(media.Title.Length > 0 ? media.Title : "Nothing playing", media.Artist, changed && win.IsVisible && shownKey.Length > 0);
            shownKey = key;
            win.PlayBtn.Content = media.Playing ? "" : "";
            win.NextBtn.IsEnabled = media.CanNext;
            win.PrevBtn.IsEnabled = media.CanPrev;
            win.Seek.IsHitTestVisible = media.CanSeek;
            if (media.ArtVersion != shownArtVersion)
            {
                shownArtVersion = media.ArtVersion;
                win.SetArt(media.Art);
                if (media.Art != null) win.ApplyTheme(Theme.For(media.Hue), win.IsVisible);
            }
            RenderPosition();
        }

        void RenderPosition()
        {
            TimeSpan now = media.Now(), dur = media.Duration;
            win.DurText.Text = Format(dur);
            if (win.Dragging)
            {
                win.CurText.Text = Format(TimeSpan.FromTicks((long)(dur.Ticks * win.DragFraction)));
                return;
            }
            win.CurText.Text = Format(now);
            win.ShowFraction(dur.Ticks > 0 ? Math.Min(1.0, (double)now.Ticks / dur.Ticks) : 0);
        }

        static string Format(TimeSpan t)
        {
            if (t < TimeSpan.Zero) t = TimeSpan.Zero;
            if (t.TotalHours >= 1) return string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds);
            return string.Format("{0}:{1:00}", (int)t.TotalMinutes, t.Seconds);
        }

        void RestoreYt()
        {
            IntPtr h = yt != IntPtr.Zero && Native.IsWindow(yt) ? yt : YtWindow.Find();
            if (h != IntPtr.Zero)
            {
                Native.ShowWindow(h, Native.SW_RESTORE);
                Native.SetForegroundWindow(h);
            }
            else LaunchYt();
        }

        static void LaunchYt()
        {
            string[] candidates =
            {
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), @"Chrome Apps\YouTube Music.lnk"),
                System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "YouTube Music.lnk")
            };
            foreach (string c in candidates)
            {
                if (File.Exists(c)) { try { Process.Start(c); } catch { } return; }
            }
            try { Process.Start("https://music.youtube.com"); } catch { }
        }

        void SetupTray()
        {
            System.Drawing.Icon icon;
            using (var s = typeof(Controller).Assembly.GetManifestResourceStream("app.ico"))
                icon = s != null ? new System.Drawing.Icon(s, WinForms.SystemInformation.SmallIconSize) : System.Drawing.SystemIcons.Application;
            tray = new WinForms.NotifyIcon { Icon = icon, Text = "YT Music Mini", Visible = true };
            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add("Open YouTube Music", null, delegate { RestoreYt(); });
            startupItem = new WinForms.ToolStripMenuItem("Start with Windows") { Checked = IsStartupEnabled() };
            startupItem.Click += delegate { SetStartup(!IsStartupEnabled()); startupItem.Checked = IsStartupEnabled(); };
            menu.Items.Add(startupItem);
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add("Exit", null, delegate { Exit(); });
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { RestoreYt(); };
        }

        static bool IsStartupEnabled()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetStartup(bool on)
        {
            try
            {
                using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
                {
                    if (on) k.SetValue(RunName, "\"" + Process.GetCurrentProcess().MainModule.FileName + "\"");
                    else if (k.GetValue(RunName) != null) k.DeleteValue(RunName);
                }
            }
            catch (Exception ex) { Log.Write("Startup setting failed: " + ex.Message); }
        }

        void Exit()
        {
            if (hook != IntPtr.Zero) Native.UnhookWinEvent(hook);
            tray.Visible = false;
            tray.Dispose();
            Application.Current.Shutdown();
        }
    }

    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            int previewAt = Array.IndexOf(args, "--preview");
            if (previewAt >= 0)
            {
                // Optional hue after the flag, e.g. "--preview 140" for a green album.
                double? hue = null;
                double h;
                if (previewAt + 1 < args.Length && double.TryParse(args[previewAt + 1], System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture, out h)) hue = h;
                var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                previewApp.Startup += delegate { new Controller().Preview(hue); };
                previewApp.Run();
                return;
            }
            bool created;
            using (var mutex = new Mutex(true, "YTMusicMini_SingleInstance", out created))
            {
                if (!created) return;
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                var controller = new Controller();
                app.Startup += delegate { controller.Start(); };
                app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
                {
                    Log.Write("Unhandled: " + e.Exception);
                    e.Handled = true;
                };
                app.Run();
            }
        }
    }
}
