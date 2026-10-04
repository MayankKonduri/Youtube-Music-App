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
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Shell;
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
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public const uint EVENT_SYSTEM_MINIMIZESTART = 0x0016, EVENT_SYSTEM_MINIMIZEEND = 0x0017;
        public const uint WINEVENT_OUTOFCONTEXT = 0x0000, WINEVENT_SKIPOWNPROCESS = 0x0002;
        public const int SW_RESTORE = 9, GWL_EXSTYLE = -20, WS_EX_TOOLWINDOW = 0x80, WS_EX_NOACTIVATE = 0x08000000;
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

    // Now-playing state for the YouTube Music session, read from Windows' media controls.
    class Media
    {
        GlobalSystemMediaTransportControlsSessionManager manager;
        GlobalSystemMediaTransportControlsSession session;
        string artKey = "";

        public string Title = "", Artist = "";
        public bool Playing, CanSeek, CanNext, CanPrev;
        public TimeSpan Position, Duration;
        public DateTimeOffset UpdatedAt;
        public ImageSource Art;
        public bool HasSession { get { return session != null; } }

        public async Task Init()
        {
            manager = await WinRt.Run(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
        }

        static bool IsBrowser(string appId)
        {
            appId = (appId ?? "").ToLowerInvariant();
            return appId.Contains("chrome") || appId.Contains("edge") || appId.Contains("brave") || appId.Contains("_crx_");
        }

        // Picks the YouTube Music session. Installed web apps report an id like "Chrome._crx_<app>", while
        // ordinary tabs report just "Chrome", so: the app's own session whose song title is in the window
        // title wins; a plain tab only counts if its title matches the window title.
        string appId = "";

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
            if (key != artKey && bestProps.Thumbnail != null)
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
                    artKey = key;
                }
                catch (Exception ex) { Log.Write("Artwork failed: " + ex.Message); }
            }
            else if (key != artKey) Art = null;
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
        x:Name='Root' Background='#202020' Padding='10' TextOptions.TextFormattingMode='Display'>
  <Border.Resources>
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
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='#26FFFFFF'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bg' Property='Background' Value='#40FFFFFF'/></Trigger>
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
      <Setter Property='Foreground' Value='#B3B3B3'/>
    </Style>
    <Style x:Key='Play' TargetType='Button' BasedOn='{StaticResource Icon}'>
      <Setter Property='Foreground' Value='#111111'/>
      <Setter Property='Width' Value='30'/>
      <Setter Property='Height' Value='30'/>
      <Setter Property='Template'>
        <Setter.Value>
          <ControlTemplate TargetType='Button'>
            <Border x:Name='Bg' Background='#FFFFFF' CornerRadius='15'>
              <ContentPresenter HorizontalAlignment='Center' VerticalAlignment='Center'/>
            </Border>
            <ControlTemplate.Triggers>
              <Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Bg' Property='Background' Value='#E3E3E3'/></Trigger>
              <Trigger Property='IsPressed' Value='True'><Setter TargetName='Bg' Property='Background' Value='#C8C8C8'/></Trigger>
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
    <Border x:Name='Art' Width='70' Height='70' CornerRadius='6' Background='#333333' Cursor='Hand' ToolTip='Open YouTube Music'>
      <TextBlock x:Name='ArtGlyph' Text='&#xE8D6;' FontFamily='Segoe Fluent Icons, Segoe MDL2 Assets' FontSize='24' Foreground='#7A7A7A'
                 HorizontalAlignment='Center' VerticalAlignment='Center'/>
    </Border>
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
        <StackPanel Margin='0,1,6,0'>
          <TextBlock x:Name='TitleText' Text='Nothing playing' Foreground='#FFFFFF' FontFamily='Segoe UI Variable Text, Segoe UI'
                     FontSize='13' FontWeight='SemiBold' TextTrimming='CharacterEllipsis'/>
          <TextBlock x:Name='ArtistText' Foreground='#AAAAAA' FontFamily='Segoe UI Variable Text, Segoe UI' FontSize='12'
                     TextTrimming='CharacterEllipsis' Margin='0,1,0,0'/>
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
        <TextBlock x:Name='CurText' Grid.Column='4' Text='0:00' Foreground='#AAAAAA' FontSize='10' FontFamily='Segoe UI'
                   VerticalAlignment='Center' Margin='0,0,6,0'/>
        <Grid x:Name='Seek' Grid.Column='5' Height='16' Background='Transparent' Cursor='Hand' VerticalAlignment='Center'>
          <Border x:Name='Track' Height='3' CornerRadius='1.5' Background='#4D4D4D' VerticalAlignment='Center'/>
          <Border x:Name='Fill' Height='3' CornerRadius='1.5' Background='#FF0033' HorizontalAlignment='Left' Width='0' VerticalAlignment='Center'/>
          <Ellipse x:Name='Knob' Width='10' Height='10' Fill='#FF0033' HorizontalAlignment='Left' VerticalAlignment='Center' Visibility='Hidden'/>
        </Grid>
        <TextBlock x:Name='DurText' Grid.Column='6' Text='0:00' Foreground='#AAAAAA' FontSize='10' FontFamily='Segoe UI'
                   VerticalAlignment='Center' Margin='6,0,0,0'/>
      </Grid>
    </Grid>
  </Grid>
</Border>";

        public Border Art, Fill, Track;
        public TextBlock ArtGlyph, TitleText, ArtistText, CurText, DurText;
        public Button PlayBtn, PrevBtn, NextBtn, RestoreBtn, CloseBtn;
        public Grid Seek;
        public Ellipse Knob;
        public bool Dragging;
        public double DragFraction;
        public event Action<double> SeekRequested;
        public event Action Moved;

        public MiniWindow()
        {
            Title = "YT Music Mini";
            Width = 340;
            Height = 90;
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            Topmost = true;
            ShowActivated = false;
            Background = new SolidColorBrush(Color.FromRgb(0x20, 0x20, 0x20));
            WindowChrome.SetWindowChrome(this, new WindowChrome
            {
                CaptionHeight = 0,
                GlassFrameThickness = new Thickness(0, 0, 0, 1),
                ResizeBorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false
            });

            var root = (Border)XamlReader.Parse(Xaml);
            Content = root;
            Art = (Border)root.FindName("Art");
            ArtGlyph = (TextBlock)root.FindName("ArtGlyph");
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

            // Drag the player by any empty area.
            root.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e)
            {
                if (e.ButtonState != MouseButtonState.Pressed) return;
                try { DragMove(); } catch { }
                if (Moved != null) Moved();
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
            // Keep it out of Alt+Tab and don't steal focus from whatever you're typing in.
            int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
            Native.SetWindowLong(hwnd, Native.GWL_EXSTYLE, ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE);
            // Windows 11 rounded corners and a subtle dark border (ignored on Windows 10).
            int round = 2;
            Native.DwmSetWindowAttribute(hwnd, 33, ref round, 4);
            int border = 0x003A3A3A;
            Native.DwmSetWindowAttribute(hwnd, 34, ref border, 4);
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
        int ticks;
        string shownKey = "";

        public async void Start()
        {
            Log.Write("Started");
            win = new MiniWindow();
            win.PlayBtn.Click += async delegate { await media.TogglePlay(); Render(); SoonRefresh(); };
            win.NextBtn.Click += async delegate { await media.Next(); SoonRefresh(); };
            win.PrevBtn.Click += async delegate { await media.Previous(); SoonRefresh(); };
            win.RestoreBtn.Click += delegate { RestoreYt(); };
            win.CloseBtn.Click += delegate { dismissed = true; HidePlayer(); };
            win.Art.MouseLeftButtonUp += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; RestoreYt(); };
            win.Art.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs e) { e.Handled = true; };
            win.SeekRequested += async delegate(double f)
            {
                if (media.Duration > TimeSpan.Zero) await media.SeekTo(TimeSpan.FromTicks((long)(media.Duration.Ticks * f)));
                Render();
                SoonRefresh();
            };
            win.Moved += delegate { settings.Left = win.Left; settings.Top = win.Top; settings.Save(); };

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
        public void Preview()
        {
            win = new MiniWindow();
            win.CloseBtn.Click += delegate { Application.Current.Shutdown(); };
            win.TitleText.Text = "Sample song title that is fairly long";
            win.ArtistText.Text = "Sample artist";
            win.CurText.Text = "1:32";
            win.DurText.Text = "3:48";
            PlaceWindow();
            win.Show();
            win.UpdateLayout();
            win.ShowFraction(0.4);
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
            if (ok && !dismissed) { Render(); ShowPlayer(); }
            else if (!ok) HidePlayer();
        }

        void ShowPlayer()
        {
            if (win.IsVisible) return;
            PlaceWindow();
            win.Show();
            win.UpdateLayout();
            RenderPosition();
        }

        void HidePlayer()
        {
            if (win.IsVisible) win.Hide();
        }

        void PlaceWindow()
        {
            double l = settings.Left, t = settings.Top;
            bool onScreen = !double.IsNaN(l) && !double.IsNaN(t)
                && l >= SystemParameters.VirtualScreenLeft - 20 && t >= SystemParameters.VirtualScreenTop - 20
                && l + win.Width <= SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth + 20
                && t + win.Height <= SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight + 20;
            if (!onScreen)
            {
                Rect wa = SystemParameters.WorkArea;
                l = wa.Right - win.Width - 14;
                t = wa.Bottom - win.Height - 14;
            }
            win.Left = l;
            win.Top = t;
        }

        void Render()
        {
            string key = media.Title + "\n" + media.Artist;
            win.TitleText.Text = media.Title.Length > 0 ? media.Title : "Nothing playing";
            win.ArtistText.Text = media.Artist;
            win.PlayBtn.Content = media.Playing ? "" : "";
            win.NextBtn.IsEnabled = media.CanNext;
            win.PrevBtn.IsEnabled = media.CanPrev;
            win.Seek.IsHitTestVisible = media.CanSeek;
            if (media.Art != null)
            {
                win.Art.Background = new ImageBrush(media.Art) { Stretch = Stretch.UniformToFill };
                win.ArtGlyph.Visibility = Visibility.Collapsed;
            }
            else if (key != shownKey)
            {
                win.Art.Background = new SolidColorBrush(Color.FromRgb(0x33, 0x33, 0x33));
                win.ArtGlyph.Visibility = Visibility.Visible;
            }
            shownKey = key;
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
            if (Array.IndexOf(args, "--preview") >= 0)
            {
                var previewApp = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                previewApp.Startup += delegate { new Controller().Preview(); };
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
