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

// Targeting 4.8 turns on WPF's modern behavior, including re-rendering at each monitor's own scaling
// (together with the per-monitor DPI setting in app.manifest).
[assembly: System.Runtime.Versioning.TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
// Shown in the file's Properties and in Task Manager.
[assembly: System.Reflection.AssemblyTitle("YT Music Mini")]
[assembly: System.Reflection.AssemblyDescription("Floating mini player for the YouTube Music app")]
[assembly: System.Reflection.AssemblyProduct("YT Music Mini")]
[assembly: System.Reflection.AssemblyVersion("1.0.1.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.0.1.0")]

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

        [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] public static extern uint GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] public static extern IntPtr MonitorFromPoint(POINT pt, uint flags);
        [DllImport("shcore.dll")] public static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);

        public struct RECT { public int Left, Top, Right, Bottom; }
        public struct POINT { public int X, Y; public POINT(int x, int y) { X = x; Y = y; } }
        public const uint SWP_NOSIZE = 0x1, SWP_NOZORDER = 0x4, SWP_NOACTIVATE = 0x10, MONITOR_DEFAULTTONEAREST = 2;

        // Display scaling of the monitor at a screen point (1.0 = 100%, 1.5 = 150%).
        public static double ScaleAt(int x, int y)
        {
            try
            {
                uint dx, dy;
                IntPtr mon = MonitorFromPoint(new POINT(x, y), MONITOR_DEFAULTTONEAREST);
                if (GetDpiForMonitor(mon, 0, out dx, out dy) == 0 && dx > 0) return dx / 96.0;
            }
            catch { }
            return 1;
        }

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
        // Album covers are solid; a browser's own icon (offered as a stand-in before the real cover
        // loads) has see-through corners.
        public static bool LooksLikeIcon(BitmapSource src)
        {
            try
            {
                double scale = 32.0 / Math.Max(src.PixelWidth, src.PixelHeight);
                var small = new FormatConvertedBitmap(new TransformedBitmap(src, new ScaleTransform(scale, scale)), PixelFormats.Bgra32, null, 0);
                int w = small.PixelWidth, h = small.PixelHeight;
                var px = new byte[w * h * 4];
                small.CopyPixels(px, w * 4, 0);
                int clear = 0;
                for (int i = 3; i < px.Length; i += 4) if (px[i] < 200) clear++;
                return clear > 0.05 * w * h;
            }
            catch { return false; }
        }

        public static double? DominantHue(BitmapSource src)
        {
            double? second;
            return DominantHues(src, out second);
        }

        // The artwork's main hue and, if it has one, a clearly different second hue (at least 60 degrees
        // around the color wheel away, and covering at least 15% as much of the picture).
        public static double? DominantHues(BitmapSource src, out double? second)
        {
            second = null;
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

                var scores = new double[36];
                int best = 0;
                for (int i = 0; i < 36; i++)
                {
                    scores[i] = weight[i] + 0.5 * (weight[(i + 35) % 36] + weight[(i + 1) % 36]);
                    if (scores[i] > scores[best]) best = i;
                }
                int next = -1;
                for (int i = 0; i < 36; i++)
                {
                    int apart = Math.Abs(i - best);
                    if (Math.Min(apart, 36 - apart) < 6 || scores[i] < 0.15 * scores[best]) continue;
                    if (next < 0 || scores[i] > scores[next]) next = i;
                }
                if (next >= 0) second = BinHue(next, cos, sin);
                return BinHue(best, cos, sin);
            }
            catch { return null; }
        }

        // The average hue of a bin and its two neighbours.
        static double BinHue(int bin, double[] cos, double[] sin)
        {
            double c = 0, sn = 0;
            for (int k = -1; k <= 1; k++) { int i = (bin + k + 36) % 36; c += cos[i]; sn += sin[i]; }
            double result = Math.Atan2(sn, c) * 180 / Math.PI;
            return result < 0 ? result + 360 : result;
        }
    }

    // All colors of the player, derived from the artwork. The time bar always stays red.
    // Colors are built in OKLCH (lightness, chroma = colorfulness, hue): equal lightness there looks
    // equally bright to the eye, so every artwork color gets the same light-but-colorful look.
    class Theme
    {
        // The background: a gradient from the artwork's main color (left) to its second color (right,
        // a little deeper). With only one main color, the right side uses a nearby, shifted hue.
        public const double BgLightness = 0.81, Bg2Lightness = 0.76, BgChroma = 0.19, SecondHueShift = 35;
        // Lightness and colorfulness of the artwork placeholder and border, from the main color.
        const double ShadeLightness = 0.79, ShadeChroma = 0.085;

        public Color Bg, Bg2, Border, Title, Sub, Icon, PlayBg, PlayBgHover, PlayFg, Track, Time, Hover, Press, Placeholder;

        // The artwork hue is an HSV hue; take a typical color of that hue and find its OKLCH hue.
        static double ToOkHue(double artHue)
        {
            double[] rgb = Hsv(artHue, 0.75, 0.85);
            return OkHue(rgb[0], rgb[1], rgb[2]);
        }

        public static Theme For(double? artHue, double? secondHue)
        {
            double h = 0, h2 = 0, c = 0;
            if (artHue.HasValue)
            {
                h = ToOkHue(artHue.Value);
                h2 = secondHue.HasValue ? ToOkHue(secondHue.Value) : (h + SecondHueShift) % 360;
                c = 1;
            }
            var t = new Theme();
            t.Bg = Ok(BgLightness, BgChroma * c, h);
            t.Bg2 = Ok(Bg2Lightness, BgChroma * c, h2);
            t.Border = Ok(ShadeLightness - 0.06, ShadeChroma * c, h);
            t.Title = Ok(0.25, 0.04 * c, h);
            t.Sub = Ok(0.42, 0.04 * c, h);
            t.Icon = Ok(0.27, 0.04 * c, h);
            t.PlayBg = Ok(0.27, 0.05 * c, h);
            t.PlayBgHover = Ok(0.36, 0.05 * c, h);
            t.PlayFg = Colors.White;
            t.Track = Color.FromArgb(0x29, 0, 0, 0);
            t.Time = Ok(0.42, 0.04 * c, h);
            t.Hover = Color.FromArgb(0x1A, 0, 0, 0);
            t.Press = Color.FromArgb(0x30, 0, 0, 0);
            t.Placeholder = Ok(ShadeLightness, ShadeChroma * c, h);
            return t;
        }

        static double[] Hsv(double h, double s, double v)
        {
            double c = v * s, hp = (h % 360) / 60.0, x = c * (1 - Math.Abs(hp % 2 - 1)), m = v - c;
            double r = 0, g = 0, b = 0;
            if (hp < 1) { r = c; g = x; }
            else if (hp < 2) { r = x; g = c; }
            else if (hp < 3) { g = c; b = x; }
            else if (hp < 4) { g = x; b = c; }
            else if (hp < 5) { r = x; b = c; }
            else { r = c; b = x; }
            return new[] { r + m, g + m, b + m };
        }

        static double ToLinear(double c) { return c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4); }
        static double ToGamma(double c) { return c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055; }
        static byte ToByte(double v) { return (byte)Math.Round(Math.Max(0, Math.Min(1, v)) * 255); }

        // OKLCH hue (degrees) of an sRGB color (channels 0..1).
        static double OkHue(double r, double g, double b)
        {
            r = ToLinear(r); g = ToLinear(g); b = ToLinear(b);
            double l = Math.Pow(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b, 1.0 / 3);
            double m = Math.Pow(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b, 1.0 / 3);
            double s = Math.Pow(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b, 1.0 / 3);
            double A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
            double B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
            double h = Math.Atan2(B, A) * 180 / Math.PI;
            return h < 0 ? h + 360 : h;
        }

        // The screen color for an OKLCH value, easing off the colorfulness if the screen can't show it.
        static Color Ok(double L, double C, double h)
        {
            double R = 0, G = 0, Bl = 0;
            for (int i = 0; i < 25; i++)
            {
                double a = C * Math.Cos(h * Math.PI / 180), b = C * Math.Sin(h * Math.PI / 180);
                double l_ = L + 0.3963377774 * a + 0.2158037573 * b;
                double m_ = L - 0.1055613458 * a - 0.0638541728 * b;
                double s_ = L - 0.0894841775 * a - 1.2914855480 * b;
                double l = l_ * l_ * l_, m = m_ * m_ * m_, s = s_ * s_ * s_;
                R = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
                G = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
                Bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
                if (R >= -0.001 && R <= 1.001 && G >= -0.001 && G <= 1.001 && Bl >= -0.001 && Bl <= 1.001) break;
                C *= 0.9;
            }
            return Color.FromRgb(ToByte(ToGamma(Math.Max(0, R))), ToByte(ToGamma(Math.Max(0, G))), ToByte(ToGamma(Math.Max(0, Bl))));
        }

        public static Theme Default()
        {
            // The original dark look, used until the first artwork arrives.
            var t = new Theme();
            t.Bg = Color.FromRgb(0x20, 0x20, 0x20);
            t.Bg2 = Color.FromRgb(0x20, 0x20, 0x20);
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

        public static Color Mix(Color a, Color b, double p)
        {
            return Color.FromArgb((byte)Math.Round(a.A + (b.A - a.A) * p), (byte)Math.Round(a.R + (b.R - a.R) * p),
                                  (byte)Math.Round(a.G + (b.G - a.G) * p), (byte)Math.Round(a.B + (b.B - a.B) * p));
        }

        public static Theme Lerp(Theme a, Theme b, double p)
        {
            var t = new Theme();
            t.Bg = Mix(a.Bg, b.Bg, p); t.Bg2 = Mix(a.Bg2, b.Bg2, p); t.Border = Mix(a.Border, b.Border, p); t.Title = Mix(a.Title, b.Title, p);
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
        // YouTube Music's media-session id once identified (saved in settings, so it's known even when
        // a paused song leaves the window title as just "YouTube Music").
        public string AppId = "";
        public event Action AppIdLearned;
        string lastMissLog = "";

        public string Title = "", Artist = "";
        public bool Playing, CanSeek, CanNext, CanPrev;
        public TimeSpan Position, Duration;
        public DateTimeOffset UpdatedAt;
        public ImageSource Art;
        public double? Hue, Hue2;
        public int ArtVersion;

        public async Task Init()
        {
            manager = await WinRt.Run(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
            onProps = delegate { propsDirty = true; RaiseChanged(); };
            onPlayback = delegate { RaiseChanged(); };
            onTimeline = delegate { RaiseChanged(); };
            try
            {
                manager.SessionsChanged += delegate { RaiseChanged(); };
                manager.CurrentSessionChanged += delegate { RaiseChanged(); };
            }
            catch (Exception ex) { Log.Write("Session events unavailable: " + ex.Message); }
        }

        // Windows reports song, play/pause and position changes as they happen (on a background thread);
        // listeners re-read the state right away instead of waiting for the next periodic check.
        public event Action Changed;
        GlobalSystemMediaTransportControlsSession watched;
        global::Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> onProps;
        global::Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> onPlayback;
        global::Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, TimelinePropertiesChangedEventArgs> onTimeline;

        void RaiseChanged()
        {
            var handler = Changed;
            if (handler != null) handler();
        }

        void Watch(GlobalSystemMediaTransportControlsSession s)
        {
            if (s == watched || onProps == null) return;
            try
            {
                if (watched != null)
                {
                    watched.MediaPropertiesChanged -= onProps;
                    watched.PlaybackInfoChanged -= onPlayback;
                    watched.TimelinePropertiesChanged -= onTimeline;
                }
            }
            catch { }
            watched = s;
            try
            {
                s.MediaPropertiesChanged += onProps;
                s.PlaybackInfoChanged += onPlayback;
                s.TimelinePropertiesChanged += onTimeline;
            }
            catch (Exception ex) { Log.Write("Song events unavailable: " + ex.Message); }
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
            int bestScore = 0, webApps = 0;
            GlobalSystemMediaTransportControlsSession onlyWebApp = null;
            GlobalSystemMediaTransportControlsSessionMediaProperties onlyWebAppProps = null;
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
                if (webApp) { webApps++; onlyWebApp = s; onlyWebAppProps = p; }
                int score = (titleMatch ? 10 : 0) + (webApp ? 4 : 0) + (id == AppId && AppId.Length > 0 ? 8 : 0);
                if (!titleMatch && id != AppId) score = 0;
                if (score > bestScore) { best = s; bestProps = p; bestScore = score; }
            }
            // Nothing matched by title or saved id: if exactly one installed web app has media, it's ours.
            if (best == null && webApps == 1) { best = onlyWebApp; bestProps = onlyWebAppProps; bestScore = 4; }
            if (best != null && best.SourceAppUserModelId.IndexOf("_crx_", StringComparison.OrdinalIgnoreCase) >= 0
                && best.SourceAppUserModelId != AppId && (bestScore >= 10 || AppId.Length == 0))
            {
                AppId = best.SourceAppUserModelId;
                if (AppIdLearned != null) AppIdLearned();
            }
            if (best == null)
            {
                string summary = windowTitle + " / " + string.Join(" | ", seen);
                if (summary != lastMissLog) Log.Write("No session matches window '" + windowTitle + "'. Sessions: " + string.Join(" | ", seen));
                lastMissLog = summary;
                session = null;
                return false;
            }
            if (session == null) Log.Write("Using session '" + bestProps.Title + "' for window '" + windowTitle + "'");
            session = best;
            Watch(best);

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

            // Artwork. Right after a song change the browser may still offer the previous song's picture,
            // or its own icon as a stand-in until the real cover loads. So for a while after each change
            // (and whenever the song info is updated) re-read it, and only accept real, solid artwork.
            string key = Title + "\n" + Artist;
            if (key != songKey) { songKey = key; songChangedAt = DateTime.Now; }
            bool recheck = propsDirty || DateTime.Now - songChangedAt < TimeSpan.FromSeconds(20);
            propsDirty = false;
            if (recheck)
            {
                byte[] bytes = null;
                if (bestProps.Thumbnail != null)
                {
                    try { bytes = await ReadAll(bestProps.Thumbnail); }
                    catch (Exception ex) { Log.Write("Artwork failed: " + ex.Message); }
                }
                bool same = bytes != null && artBytes != null && SameBytes(bytes, artBytes);
                if (bytes != null && !same)
                {
                    BitmapImage bmp = Decode(bytes);
                    if (bmp != null && !ArtColor.LooksLikeIcon(bmp))
                    {
                        artBytes = bytes;
                        Art = bmp;
                        Hue = ArtColor.DominantHues(bmp, out Hue2);
                        artSongKey = key;
                        ArtVersion++;
                    }
                }
                if (artSongKey != key && DateTime.Now - songChangedAt > TimeSpan.FromSeconds(3))
                {
                    // Still the same picture a few seconds in: same album, same cover. Otherwise there's no
                    // real artwork yet, so show the music-note placeholder rather than the last song's cover.
                    if (same) artSongKey = key;
                    else if (Art != null) { Art = null; artBytes = null; ArtVersion++; }
                }
            }
            return true;
        }

        string songKey = "", artSongKey = "";
        DateTime songChangedAt = DateTime.MinValue;
        volatile bool propsDirty;
        byte[] artBytes;

        static async Task<byte[]> ReadAll(IRandomAccessStreamReference source)
        {
            var stream = await WinRt.Run(source.OpenReadAsync());
            var reader = new DataReader(stream);
            uint n = await WinRt.Run<uint>(reader.LoadAsync((uint)stream.Size));
            var bytes = new byte[n];
            reader.ReadBytes(bytes);
            return bytes;
        }

        static BitmapImage Decode(byte[] bytes)
        {
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.StreamSource = new MemoryStream(bytes);
                bmp.DecodePixelHeight = 240;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch { return null; }
        }

        static bool SameBytes(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
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
        x:Name='Root' Padding='9' CornerRadius='8' BorderThickness='1' UseLayoutRounding='True' SnapsToDevicePixels='True'
        TextOptions.TextFormattingMode='Display' TextOptions.TextHintingMode='Fixed' RenderOptions.ClearTypeHint='Enabled'>
  <Border.Resources>
    <SolidColorBrush x:Key='HoverBrush' Color='#26FFFFFF'/>
    <SolidColorBrush x:Key='PressBrush' Color='#40FFFFFF'/>
    <SolidColorBrush x:Key='PlayBgBrush' Color='#FFFFFF'/>
    <SolidColorBrush x:Key='PlayBgHoverBrush' Color='#E3E3E3'/>
    <Style x:Key='Icon' TargetType='Button'>
      <Setter Property='Foreground' Value='#FFFFFF'/>
      <Setter Property='FontFamily' Value='Segoe Fluent Icons, Segoe MDL2 Assets'/>
      <Setter Property='TextOptions.TextRenderingMode' Value='Grayscale'/>
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
    <Grid x:Name='ArtHost' Width='70' Height='70' Cursor='Hand' ToolTip='Open YouTube Music' RenderOptions.BitmapScalingMode='HighQuality'>
      <Border x:Name='ArtBack' CornerRadius='6'/>
      <Border x:Name='ArtFront' CornerRadius='6'/>
      <TextBlock x:Name='ArtGlyph' Text='&#xE8D6;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24' TextOptions.TextRenderingMode='Grayscale'
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
            <TextBlock x:Name='TitleText' Text='Nothing playing' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='13' FontWeight='SemiBold'/>
            <Rectangle x:Name='TitleFade' Width='18' Height='18' Canvas.Right='0' Canvas.Top='0' Visibility='Collapsed'/>
          </Canvas>
          <Canvas x:Name='ArtistBox' Height='16' ClipToBounds='True' Margin='0,1,0,0'>
            <TextBlock x:Name='ArtistText' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='12'/>
            <Rectangle x:Name='ArtistFade' Width='18' Height='16' Canvas.Right='0' Canvas.Top='0' Visibility='Collapsed'/>
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

        public Border ArtBack, ArtFront, Fill, Track;
        public Grid ArtHost, Seek;
        public Canvas TitleBox, ArtistBox;
        public Rectangle TitleFade, ArtistFade;
        public TextBlock ArtGlyph, TitleText, ArtistText, CurText, DurText;
        public Button PlayBtn, PrevBtn, NextBtn, RestoreBtn, CloseBtn;
        public Ellipse Knob;
        public bool Dragging, Hiding;
        public double DragFraction;
        public event Action<double> SeekRequested;
        public event Action DragFinished;

        readonly Border root;
        Brush placeholder = Brushes.Transparent;
        readonly Stopwatch gradientClock = Stopwatch.StartNew();
        readonly DispatcherTimer gradientTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(33) };
        WriteableBitmap backdrop;
        int[] backdropPixels;
        Color bgLeft, bgRight;
        double gradientTime;
        IntPtr hwnd;
        double alpha;
        Anim fadeAnim, moveAnim, themeAnim;
        Theme shown;
        ImageSource currentArt;
        Marquee titleMarquee, artistMarquee;
        readonly Stopwatch marqueeClock = new Stopwatch();
        double marqueeSlide;
        readonly DispatcherTimer marqueeTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(16) };

        public MiniWindow()
        {
            Title = "YT Music Mini";
            Width = 340;
            Height = 90;
            // A transparent window that draws its own rounded card, so the player can fade in and out.
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
            TitleFade = (Rectangle)root.FindName("TitleFade");
            ArtistFade = (Rectangle)root.FindName("ArtistFade");
            gradientTimer.Tick += delegate { UpdateGradient(gradientClock.Elapsed.TotalSeconds * GradientSpeed); };
            titleMarquee = new Marquee(TitleBox, TitleText, TitleFade);
            artistMarquee = new Marquee(ArtistBox, ArtistText, ArtistFade);
            marqueeTimer.Tick += delegate
            {
                double ppd = VisualTreeHelper.GetDpi(this).PixelsPerDip, t = marqueeClock.Elapsed.TotalSeconds;
                titleMarquee.Step(t, marqueeSlide, ppd);
                artistMarquee.Step(t, marqueeSlide, ppd);
            };

            // Drag the player by any empty area; it snaps when let go near the bottom of the screen.
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                Anim.Stop(moveAnim);
                try { DragMove(); } catch { }
                if (DragFinished != null) DragFinished();
            };

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
            hwnd = new WindowInteropHelper(this).Handle;
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

        // Positions are in real screen pixels (not WPF's scaled units), so placement stays exact on
        // monitors with different display scaling.
        public Native.RECT PixelRect()
        {
            Native.RECT r;
            Native.GetWindowRect(hwnd, out r);
            return r;
        }

        public double Scale
        {
            get
            {
                uint dpi = hwnd != IntPtr.Zero ? Native.GetDpiForWindow(hwnd) : 0;
                return dpi > 0 ? dpi / 96.0 : 1;
            }
        }

        public void MoveTo(double x, double y)
        {
            Native.SetWindowPos(hwnd, IntPtr.Zero, (int)Math.Round(x), (int)Math.Round(y), 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }

        // Fades in while sliding up a little into place.
        public void AnimateIn(double x, double y)
        {
            Anim.Stop(fadeAnim);
            Anim.Stop(moveAnim);
            Hiding = false;
            if (!IsVisible)
            {
                SetAlpha(0);
                MoveTo(x, y + 14 * Native.ScaleAt((int)x + 10, (int)y + 10));
                Show();
            }
            var r = PixelRect();
            double fromX = r.Left, fromY = r.Top;
            FadeTo(1, 230, null);
            moveAnim = Anim.Run(280, p => MoveTo(fromX + (x - fromX) * p, fromY + (y - fromY) * p), null);
            RefreshMarquees();
            UpdateGradient(gradientClock.Elapsed.TotalSeconds * GradientSpeed);
            gradientTimer.Start();
        }

        // Fades out while dipping down a little, then hides.
        public void AnimateOut()
        {
            if (!IsVisible || Hiding) return;
            Hiding = true;
            Anim.Stop(moveAnim);
            var r = PixelRect();
            double x = r.Left, y = r.Top, drop = 10 * Scale;
            moveAnim = Anim.Run(170, p => MoveTo(x, y + drop * p), null);
            FadeTo(0, 170, delegate
            {
                Hide();
                MoveTo(x, y);
                Hiding = false;
                StopMarquees();
                gradientTimer.Stop();
            });
        }

        public void GlideTo(double x, double y, Action done)
        {
            Anim.Stop(moveAnim);
            var r = PixelRect();
            double fromX = r.Left, fromY = r.Top;
            moveAnim = Anim.Run(180, p => MoveTo(fromX + (x - fromX) * p, fromY + (y - fromY) * p), done);
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

        // The moving background: the artwork's main color on the left and its second color on the right,
        // meeting at a crisp, gently wavy edge that drifts and tilts slowly around the middle.
        // Edge: half-width of the blend between the colors, as a fraction of the player's width.
        const double GradientMovement = 3.0, GradientSpeed = 2.0, GradientEdge = 0.09;

        // Where the edge is (fraction of the width) at height v (0 = top, 1 = bottom), t seconds in.
        static double EdgeAt(double v, double t, double aspect)
        {
            double mid = 0.5 + 0.04 * GradientMovement * Math.Sin(t * 0.5 + 1);
            double tilt = Math.Tan(7 * GradientMovement * Math.Sin(t * 0.35) * Math.PI / 180);
            double wave = 0.03 * Math.Min(1.5, GradientMovement) * Math.Sin(2 * Math.PI * v / 1.4 + t * 1.1);
            return mid + (v - 0.5) * aspect * tilt + wave;
        }

        // Paints the background into a small bitmap at the screen's pixel density.
        void UpdateGradient(double t)
        {
            gradientTime = t;
            double dipW = root.ActualWidth > 0 ? root.ActualWidth : Width, dipH = root.ActualHeight > 0 ? root.ActualHeight : Height;
            double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
            int w = Math.Max(1, (int)Math.Round(dipW * scale)), h = Math.Max(1, (int)Math.Round(dipH * scale));
            if (backdrop == null || backdrop.PixelWidth != w || backdrop.PixelHeight != h)
            {
                backdrop = new WriteableBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Bgr32, null);
                backdropPixels = new int[w * h];
                root.Background = new ImageBrush(backdrop) { Stretch = Stretch.Fill };
            }
            int left = (bgLeft.R << 16) | (bgLeft.G << 8) | bgLeft.B, right = (bgRight.R << 16) | (bgRight.G << 8) | bgRight.B;
            double band = GradientEdge * w;
            for (int y = 0; y < h; y++)
            {
                double edge = EdgeAt((y + 0.5) / h, t, dipH / dipW) * w;
                int start = (int)Math.Floor(edge - band), end = (int)Math.Ceiling(edge + band), row = y * w;
                for (int x = 0; x < w; x++)
                {
                    if (x < start) backdropPixels[row + x] = left;
                    else if (x > end) backdropPixels[row + x] = right;
                    else
                    {
                        double f = Math.Max(0, Math.Min(1, (x + 0.5 - (edge - band)) / (2 * band)));
                        Color c = Theme.Mix(bgLeft, bgRight, f);
                        backdropPixels[row + x] = (c.R << 16) | (c.G << 8) | c.B;
                    }
                }
            }
            backdrop.WritePixels(new Int32Rect(0, 0, w, h), backdropPixels, w * 4, 0);
            UpdateFades();
        }

        // The background color at a point on the player.
        Color BackgroundAt(Point p)
        {
            double dipW = root.ActualWidth > 0 ? root.ActualWidth : Width, dipH = root.ActualHeight > 0 ? root.ActualHeight : Height;
            double edge = EdgeAt(p.Y / dipH, gradientTime, dipH / dipW);
            double f = Math.Max(0, Math.Min(1, (p.X / dipW - (edge - GradientEdge)) / (2 * GradientEdge)));
            return Theme.Mix(bgLeft, bgRight, f);
        }

        // The soft fade at the right edge of scrolling text is painted in whatever color is behind it.
        void UpdateFades()
        {
            foreach (var fade in new[] { TitleFade, ArtistFade })
            {
                if (fade.Visibility != Visibility.Visible || fade.ActualWidth <= 0) continue;
                Color c = BackgroundAt(fade.TranslatePoint(new Point(fade.ActualWidth / 2, fade.ActualHeight / 2), root));
                var edge = new LinearGradientBrush(Color.FromArgb(0, c.R, c.G, c.B), c, 0);
                edge.Freeze();
                fade.Fill = edge;
            }
        }

        // Fresh brushes every time (WPF locks brushes that styles and templates have used, so they
        // can't be recolored in place). Called once per frame while a theme change animates.
        void SetColors(Theme t)
        {
            shown = t;
            bgLeft = t.Bg;
            bgRight = t.Bg2;
            UpdateGradient(gradientTime);
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
        public void SetArt(ImageSource img) { SetArt(img, true); }

        public void SetArt(ImageSource img, bool animate)
        {
            if (img == currentArt) return;
            currentArt = img;
            ArtGlyph.Visibility = img == null ? Visibility.Visible : Visibility.Collapsed;
            ArtBack.Background = ArtFront.Background;
            ArtFront.Background = img != null ? (Brush)new ImageBrush(img) { Stretch = Stretch.UniformToFill } : placeholder;
            if (animate) ArtFront.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(380)));
            else { ArtFront.BeginAnimation(OpacityProperty, null); ArtFront.Opacity = 1; }
        }

        // Draws the player (with a soft shadow) to a PNG at twice the normal resolution, for screenshots.
        public void Snapshot(string path, double fraction)
        {
            const double pad = 24;
            var stage = new Grid { Width = Width + 2 * pad, Height = Height + 2 * pad };
            stage.Children.Add(DetachWithShadow(pad, pad));
            Render(stage, path, 2, fraction);
        }

        // Draws the player in the corner of a simulated Windows 11 desktop (original wallpaper, generic
        // taskbar icons, this app's own tray icon), for the README.
        public void SnapshotDesktop(string path, double fraction)
        {
            const double W = 1280, H = 720, bar = 48, gap = 14;
            var stage = new Grid { Width = W, Height = H, ClipToBounds = true };

            var wall = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
            wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x17, 0x1E, 0x4A), 0));
            wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x2B, 0x57, 0x8C), 0.55));
            wall.GradientStops.Add(new GradientStop(Color.FromRgb(0x76, 0x58, 0xA6), 1));
            stage.Background = wall;
            var glows = new Canvas();
            AddGlow(glows, 930, 150, 430, Color.FromArgb(0x60, 0x9E, 0xC9, 0xFF));
            AddGlow(glows, 260, 560, 400, Color.FromArgb(0x50, 0xC7, 0x8B, 0xE8));
            AddGlow(glows, 600, 330, 260, Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF));
            stage.Children.Add(glows);

            // Taskbar: generic app icons in the middle; tray icons, this app's icon and the clock on the right.
            var taskbar = new Grid { Height = bar, VerticalAlignment = VerticalAlignment.Bottom, Background = B(Color.FromArgb(0xEB, 0x1C, 0x1C, 0x1F)) };
            taskbar.Children.Add(new Border { BorderBrush = B(Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)), BorderThickness = new Thickness(0, 1, 0, 0) });
            var apps = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
            apps.Children.Add(TaskbarIcon("", Color.FromRgb(0x6C, 0xB8, 0xF6), null, false));
            apps.Children.Add(TaskbarIcon("", Colors.White, null, false));
            apps.Children.Add(TaskbarIcon("", Color.FromRgb(0xF7, 0xC9, 0x48), null, false));
            apps.Children.Add(TaskbarIcon("", Color.FromRgb(0x4F, 0xA3, 0xF7), null, false));
            apps.Children.Add(TaskbarIcon("", Color.FromRgb(0x5A, 0xB0, 0xF2), null, false));
            apps.Children.Add(TaskbarIcon("", Colors.White, Color.FromRgb(0xE5, 0x2D, 0x3A), true));   // the minimized music app
            apps.Children.Add(TaskbarIcon("", Color.FromRgb(0xC8, 0xC8, 0xC8), null, false));
            taskbar.Children.Add(apps);
            var tray = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 14, 0) };
            tray.Children.Add(TrayGlyph(""));
            var appIcon = AppIcon();
            if (appIcon != null)
                tray.Children.Add(new Border
                {
                    Width = 28, Height = 28, CornerRadius = new CornerRadius(4), Margin = new Thickness(2, 0, 6, 0),
                    Background = B(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF)),
                    Child = new Image { Source = appIcon, Width = 16, Height = 16 }
                });
            tray.Children.Add(TrayGlyph(""));
            tray.Children.Add(TrayGlyph(""));
            tray.Children.Add(TrayGlyph(""));
            var clock = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            clock.Children.Add(new TextBlock { Text = "9:41 AM", Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Right });
            clock.Children.Add(new TextBlock { Text = "10/3/2026", Foreground = Brushes.White, FontSize = 12, FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"), HorizontalAlignment = HorizontalAlignment.Right });
            tray.Children.Add(clock);
            taskbar.Children.Add(tray);
            stage.Children.Add(taskbar);

            stage.Children.Add(DetachWithShadow(W - gap - Width, H - bar - gap - Height));
            Render(stage, path, 1.5, fraction);
        }

        // Takes the player out of this window and puts it, with a soft shadow, at (x, y) on a layer.
        Canvas DetachWithShadow(double x, double y)
        {
            Content = null;
            TextOptions.SetTextFormattingMode(root, TextFormattingMode.Ideal);
            var layer = new Canvas();
            var shadow = new Border
            {
                Width = Width, Height = Height,
                CornerRadius = new CornerRadius(8),
                Background = root.Background,
                Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 28, ShadowDepth = 6, Direction = 270, Opacity = 0.3, Color = Colors.Black }
            };
            root.Width = Width;
            root.Height = Height;
            foreach (UIElement e in new UIElement[] { shadow, root })
            {
                Canvas.SetLeft(e, x);
                Canvas.SetTop(e, y);
                layer.Children.Add(e);
            }
            return layer;
        }

        void Render(FrameworkElement stage, string path, double scale, double fraction)
        {
            stage.Measure(new Size(stage.Width, stage.Height));
            stage.Arrange(new Rect(0, 0, stage.Width, stage.Height));
            stage.UpdateLayout();
            ShowFraction(fraction);
            UpdateGradient(0);
            stage.UpdateLayout();
            var image = new RenderTargetBitmap((int)(stage.Width * scale), (int)(stage.Height * scale), 96 * scale, 96 * scale, PixelFormats.Pbgra32);
            image.Render(stage);
            var png = new PngBitmapEncoder();
            png.Frames.Add(BitmapFrame.Create(image));
            using (var file = File.Create(path)) png.Save(file);
        }

        static void AddGlow(Canvas c, double cx, double cy, double r, Color color)
        {
            var glow = new System.Windows.Shapes.Ellipse { Width = 2 * r, Height = 2 * r, Fill = new RadialGradientBrush(color, Color.FromArgb(0, color.R, color.G, color.B)) };
            Canvas.SetLeft(glow, cx - r);
            Canvas.SetTop(glow, cy - r);
            c.Children.Add(glow);
        }

        static FrameworkElement TaskbarIcon(string glyph, Color color, Color? circle, bool running)
        {
            var cell = new Grid { Width = 44, Height = 40, Margin = new Thickness(2, 0, 2, 0) };
            var icon = new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = circle.HasValue ? 11 : 20,
                Foreground = B(color), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
            };
            if (circle.HasValue)
                cell.Children.Add(new Border { Width = 24, Height = 24, CornerRadius = new CornerRadius(12), Background = B(circle.Value), Child = icon });
            else
                cell.Children.Add(icon);
            if (running)
                cell.Children.Add(new Border { Width = 6, Height = 3, CornerRadius = new CornerRadius(1.5), Background = B(Color.FromRgb(0x9A, 0x9A, 0x9A)), VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 0, 0, 1) });
            return cell;
        }

        static FrameworkElement TrayGlyph(string glyph)
        {
            return new TextBlock
            {
                Text = glyph, FontFamily = new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"), FontSize = 14, Foreground = Brushes.White,
                VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(6, 0, 6, 0)
            };
        }

        static ImageSource AppIcon()
        {
            try
            {
                using (var s = typeof(MiniWindow).Assembly.GetManifestResourceStream("app.ico"))
                {
                    var frames = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames;
                    BitmapFrame best = frames[0];
                    foreach (var f in frames) if (f.PixelWidth == 32) best = f;
                    return best;
                }
            }
            catch { return null; }
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
                titleMarquee.Start();
                artistMarquee.Start();
                marqueeSlide = Math.Max(titleMarquee.Travel, artistMarquee.Travel);
                marqueeClock.Restart();
                if (titleMarquee.Active || artistMarquee.Active) marqueeTimer.Start();
                else marqueeTimer.Stop();
            }));
        }

        public void StopMarquees()
        {
            marqueeTimer.Stop();
            titleMarquee.Stop();
            artistMarquee.Stop();
        }
    }

    // Text that doesn't fit slides to its end at a constant speed, rests, slides back, rests, and
    // repeats; text that fits stays still. The title and artist share one schedule: both set off from
    // each side at the same moment, and the shorter one simply arrives first and waits. It moves in
    // whole-pixel steps so the letters stay sharp the whole time.
    class Marquee
    {
        // Pixels per second: 70% of the original motion's top speed (about 25 px/s).
        public const double Speed = 0.70 * (16 * Math.PI / 2);
        // Seconds resting at each side, and extra pixels to slide so the last letter clears the edge fade.
        public const double Hold = 2.4;
        const double EndPad = 14;

        readonly Canvas box;
        readonly TextBlock text;
        readonly Rectangle fade;
        readonly TranslateTransform shift = new TranslateTransform();
        double distance;
        public bool Active;

        public Marquee(Canvas box, TextBlock text, Rectangle fade)
        {
            this.box = box;
            this.text = text;
            this.fade = fade;
            text.RenderTransform = shift;
        }

        // Seconds this text needs to slide from one side to the other.
        public double Travel { get { return Active ? distance / Speed : 0; } }

        public void Start()
        {
            Stop();
            text.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double over = text.DesiredSize.Width - box.ActualWidth;
            if (box.ActualWidth <= 0 || over <= 1) return;
            distance = Math.Ceiling(over + EndPad);
            fade.Visibility = Visibility.Visible;
            Active = true;
        }

        public void Stop()
        {
            Active = false;
            shift.X = 0;
            fade.Visibility = Visibility.Collapsed;
        }

        // t: seconds since both started; slide: the longest travel time of the two, which sets when
        // both turn around.
        public void Step(double t, double slide, double pixelsPerDip)
        {
            if (!Active) return;
            double x;
            t %= 2 * Hold + 2 * slide;
            if (t < Hold) x = 0;
            else if (t < Hold + slide) x = -Math.Min(distance, Speed * (t - Hold));
            else if (t < 2 * Hold + slide) x = -distance;
            else x = -Math.Max(0, distance - Speed * (t - 2 * Hold - slide));
            x = Math.Round(x * pixelsPerDip) / pixelsPerDip;
            if (x != shift.X) shift.X = x;
        }
    }

    class Settings
    {
        static string FilePath { get { return System.IO.Path.Combine(Log.Dir, "settings.txt"); } }
        // Where the player was left, in screen pixels, and what it's snapped to:
        // "" (nowhere), "bottom", "bottomleft" or "bottomright".
        public double X = double.NaN, Y = double.NaN;
        public string Snap = "";
        public string AppId = "";
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
                    if (k == "x" && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.X = d;
                    if (k == "y" && double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out d)) s.Y = d;
                    if (k == "snap") s.Snap = v;
                    if (k == "appId") s.AppId = v;
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
                    "x=" + (double.IsNaN(X) ? "" : X.ToString(ci)) + "\r\n" +
                    "y=" + (double.IsNaN(Y) ? "" : Y.ToString(ci)) + "\r\n" +
                    "snap=" + Snap + "\r\n" +
                    "appId=" + AppId + "\r\n" +
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
        bool watching, dismissed, refreshing, refreshAgain;
        DateTime missingSince = DateTime.MinValue;
        DispatcherTimer timer;
        int ticks;
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
            win.PlayBtn.Click += async delegate { await media.TogglePlay(); Render(); SoonRefresh(300); };
            win.NextBtn.Click += async delegate { await media.Next(); Refresh(); SoonRefresh(300); };
            win.PrevBtn.Click += async delegate { await media.Previous(); Refresh(); SoonRefresh(300); };
            win.RestoreBtn.Click += delegate { RestoreYt(); };
            win.CloseBtn.Click += delegate { dismissed = true; HidePlayer(); };
            win.ArtHost.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            win.ArtHost.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; RestoreYt(); };
            win.SeekRequested += async delegate(double f)
            {
                if (media.Duration > TimeSpan.Zero) await media.SeekTo(TimeSpan.FromTicks((long)(media.Duration.Ticks * f)));
                Render();
                SoonRefresh(300);
            };
            win.DragFinished += OnDragFinished;

            timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            timer.Tick += delegate { Tick(); };

            SetupTray();
            if (!settings.StartupConfigured)
            {
                // First run: start with Windows, and say it's running (otherwise nothing visible happens
                // until YouTube Music is minimized).
                SetStartup(true);
                settings.StartupConfigured = true;
                settings.Save();
                tray.ShowBalloonTip(8000, "YT Music Mini is running",
                    "Minimize YouTube Music to see the mini player. Right-click this tray icon for options.", WinForms.ToolTipIcon.None);
            }
            // If the app was moved since, point "Start with Windows" at its new location.
            else if (IsStartupEnabled()) SetStartup(true);

            media.AppId = settings.AppId;
            media.AppIdLearned += delegate { settings.AppId = media.AppId; settings.Save(); };
            // Song changes arrive on a background thread; re-check on the UI thread straight away.
            media.Changed += delegate { win.Dispatcher.BeginInvoke(new Action(Refresh)); };
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
        // With "--snapshot <file.png>" it draws the player to an image instead and exits.
        public void Preview(PreviewOptions o)
        {
            CreateWindow();
            win.CloseBtn.Click += delegate { Application.Current.Shutdown(); };
            win.SetText(o.Title ?? "Evadu Evadu (From the Original Motion Picture Soundtrack)",
                        o.Artist ?? "Harvey Spector • Premam (Original Motion Picture Soundtrack)", false);
            win.CurText.Text = o.Position ?? "1:32";
            win.DurText.Text = o.Duration ?? "3:48";
            double? hue = o.Hue, hue2 = o.Hue2;
            if (o.ArtPath != null)
            {
                var art = new BitmapImage();
                art.BeginInit();
                art.CacheOption = BitmapCacheOption.OnLoad;
                art.UriSource = new Uri(System.IO.Path.GetFullPath(o.ArtPath));
                art.DecodePixelHeight = 240;
                art.EndInit();
                art.Freeze();
                win.SetArt(art, false);
                hue = ArtColor.DominantHues(art, out hue2);
            }
            win.ApplyTheme(Theme.For(hue, hue2), false);
            double fraction = Fraction(win.CurText.Text, win.DurText.Text);
            if (o.SnapshotPath != null)
            {
                if (o.Desktop) win.SnapshotDesktop(o.SnapshotPath, fraction);
                else win.Snapshot(o.SnapshotPath, fraction);
                Application.Current.Shutdown();
                return;
            }
            double l, t;
            TargetPosition(out l, out t);
            win.AnimateIn(l, t);
            win.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate { win.ShowFraction(fraction); }));
        }

        static double Fraction(string position, string duration)
        {
            TimeSpan p, d;
            if (TimeSpan.TryParseExact(position, @"m\:ss", null, out p) && TimeSpan.TryParseExact(duration, @"m\:ss", null, out d) && d.Ticks > 0)
                return Math.Min(1, (double)p.Ticks / d.Ticks);
            return 0.4;
        }

        void OnWinEvent(IntPtr h, uint evt, IntPtr hwnd, int idObject, int idChild, uint thread, uint time)
        {
            if (idObject != 0 || idChild != 0) return;
            if (evt == Native.EVENT_SYSTEM_MINIMIZESTART && YtWindow.Is(hwnd)) BeginWatching(hwnd);
            else if (evt == Native.EVENT_SYSTEM_MINIMIZEEND && hwnd == yt) StopWatching();
        }

        void BeginWatching(IntPtr hwnd)
        {
            if (hwnd != yt || !watching) Log.Write("YouTube Music minimized: '" + Native.Title(hwnd) + "'");
            yt = hwnd;
            watching = true;
            dismissed = false;
            ticks = 0;
            missingSince = DateTime.MinValue;
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

        // A follow-up check shortly after a button press, in case the change event comes late.
        void SoonRefresh(int ms)
        {
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += delegate { t.Stop(); Refresh(); };
            t.Start();
        }

        async void Refresh()
        {
            if (!watching) return;
            if (refreshing) { refreshAgain = true; return; }
            refreshing = true;
            bool ok = false;
            try { ok = await media.Refresh(Native.Title(yt)); }
            catch (Exception ex) { Log.Write("Refresh failed: " + ex.Message); }
            refreshing = false;
            if (!watching) return;
            if (ok)
            {
                missingSince = DateTime.MinValue;
                if (!dismissed) { Render(); ShowPlayer(); }
            }
            else
            {
                // Between songs YouTube Music's media session briefly disappears; only hide if it stays gone.
                if (missingSince == DateTime.MinValue) missingSince = DateTime.Now;
                if (!win.IsVisible || DateTime.Now - missingSince > TimeSpan.FromSeconds(3)) HidePlayer();
            }
            // Something changed while this check was running: check again so the newest state shows.
            if (refreshAgain) { refreshAgain = false; Refresh(); }
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

        // Where to show the player, in screen pixels. Snapped spots are recomputed on the screen the
        // player was left on (or the nearest one if that monitor is gone), so a bottom-left player stays
        // bottom-left even if monitors or scaling change. Free spots are kept fully on a screen.
        void TargetPosition(out double x, out double y)
        {
            bool saved = !double.IsNaN(settings.X) && !double.IsNaN(settings.Y);
            var screen = saved ? WinForms.Screen.FromPoint(new System.Drawing.Point((int)settings.X + 1, (int)settings.Y + 1))
                               : WinForms.Screen.PrimaryScreen;
            var wa = screen.WorkingArea;
            double scale = Native.ScaleAt(wa.Left + wa.Width / 2, wa.Top + wa.Height / 2);
            double w = Math.Round(win.Width * scale), h = Math.Round(win.Height * scale), m = SnapMargin * scale;
            string snap = saved ? settings.Snap : "bottomright";
            x = saved ? settings.X : 0;
            y = saved ? settings.Y : 0;
            if (snap == "bottomright") { x = wa.Right - m - w; y = wa.Bottom - m - h; }
            else if (snap == "bottomleft") { x = wa.Left + m; y = wa.Bottom - m - h; }
            else if (snap == "bottom") y = wa.Bottom - m - h;
            x = Math.Max(wa.Left, Math.Min(x, wa.Right - w));
            y = Math.Max(wa.Top, Math.Min(y, wa.Bottom - h));
        }

        // Let go near the bottom edge and it settles onto it; near a bottom corner and it settles into
        // the corner. Anywhere else it stays where it was dropped.
        void OnDragFinished()
        {
            var r = win.PixelRect();
            double w = r.Right - r.Left, h = r.Bottom - r.Top, x = r.Left, y = r.Top;
            var wa = WinForms.Screen.FromRectangle(new System.Drawing.Rectangle(r.Left, r.Top, (int)w, (int)h)).WorkingArea;
            double scale = win.Scale, m = SnapMargin * scale, reach = SnapReach * scale;
            double bottom = wa.Bottom - m - h, leftEdge = wa.Left + m, rightEdge = wa.Right - m - w;
            string snap = "";
            if (y >= bottom - reach)
            {
                y = bottom;
                snap = "bottom";
                if (x <= leftEdge + reach) { x = leftEdge; snap = "bottomleft"; }
                else if (x >= rightEdge - reach) { x = rightEdge; snap = "bottomright"; }
            }
            win.GlideTo(x, y, delegate
            {
                settings.X = x;
                settings.Y = y;
                settings.Snap = snap;
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
                if (media.Art != null) win.ApplyTheme(Theme.For(media.Hue, media.Hue2), win.IsVisible);
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

    // Sample data for "--preview": an optional hue right after the flag ("--preview 140" looks like a
    // green album), plus --hue2 <second hue>, --title, --artist, --art <image>, --position m:ss,
    // --duration m:ss, --snapshot <file.png> and --desktop (snapshot on a simulated Windows desktop).
    class PreviewOptions
    {
        public double? Hue, Hue2;
        public string Title, Artist, ArtPath, Position, Duration, SnapshotPath;
        public bool Desktop;

        public static PreviewOptions Parse(string[] args, int at)
        {
            var o = new PreviewOptions();
            double h;
            if (at + 1 < args.Length && double.TryParse(args[at + 1], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out h)) o.Hue = h;
            o.Title = Value(args, "--title");
            o.Artist = Value(args, "--artist");
            o.ArtPath = Value(args, "--art");
            o.Position = Value(args, "--position");
            o.Duration = Value(args, "--duration");
            o.SnapshotPath = Value(args, "--snapshot");
            o.Desktop = Array.IndexOf(args, "--desktop") >= 0;
            string second = Value(args, "--hue2");
            if (second != null && double.TryParse(second, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out h)) o.Hue2 = h;
            return o;
        }

        static string Value(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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
                var options = PreviewOptions.Parse(args, previewAt);
                var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                previewApp.Startup += delegate { new Controller().Preview(options); };
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
