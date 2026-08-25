using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace CalendarApp.Platform;

/// <summary>
/// Captures the portion of the desktop wallpaper that lies under the window.
///
/// Key design decisions (v4):
///   - Source image: always loads from %AppData%\Microsoft\Windows\Themes\
///     TranscodedWallpaper — the live desktop bitmap that Windows rewrites on
///     every wallpaper change (manual, slideshow, theme). This avoids the
///     IDesktopWallpaper COM race where GetWallpaper can return a stale path
///     for slideshows.
///   - Monitor region: still resolved via IDesktopWallpaper COM for accurate
///     per-monitor RECT (needed for crop origin calculation).
///   - Layout mode: read from registry HKCU\Control Panel\Desktop (
///     WallpaperStyle / TileWallpaper).
///   - FileSystemWatcher on TranscodedWallpaper catches slideshow rotations
///     that UserPreferenceChanged.Desktop misses. Callbacks are marshalled
///     to the UI thread and debounced (300ms).
/// </summary>
public static class WallpaperBackdrop
{
    private const string DesktopKey = @"Control Panel\Desktop";

    private static readonly string s_transcodedPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        @"Microsoft\Windows\Themes\TranscodedWallpaper");

    private static FileSystemWatcher? s_transcodedWatcher;
    private static DispatcherTimer? s_changeDebounce;

    /// <summary>
    /// Raised when the desktop wallpaper changes. Always raised on the
    /// application's UI thread. Debounced: rapid slideshow transitions
    /// fire only once every 300ms.
    /// </summary>
    public static event Action? WallpaperChanged;

    static WallpaperBackdrop()
    {
        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
        StartTranscodedWatcher();
    }

    /// <summary>
    /// Returns a BitmapSource of the current wallpaper cropped to the
    /// window's screen region. The returned bitmap has the same pixel
    /// dimensions as the window's physical size, so placing it in an
    /// Image with Stretch="Fill" produces a 1:1 screen-pixel mapping
    /// at any DPI (no distortion, no misalignment).
    ///
    /// Returns null when no wallpaper is set or the file is unreadable.
    /// </summary>
    public static BitmapSource? CropForWindow(Window window)
    {
        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;

        // Always load from the live TranscodedWallpaper — it is rewritten on
        // every wallpaper change (manual, slideshow, theme) and is always the
        // current desktop image.  Avoids the IDesktopWallpaper.GetWallpaper COM
        // race where slideshow transitions leave the COM path stale.
        if (!File.Exists(s_transcodedPath)) return null;

        try
        {
            var wallpaper = LoadWallpaper(s_transcodedPath);
            if (wallpaper is null) return null;

            var (mode, tile) = GetLayout();

            if (!GetMonitorRectForWindow(hwnd, out int monLeft, out int monTop, out int monW, out int monH))
                return null;

            if (!GetWindowRect(hwnd, out var wr))
                return null;
            int winW = wr.Right - wr.Left;
            int winH = wr.Bottom - wr.Top;
            if (winW <= 0 || winH <= 0) return null;

            double wplW = wallpaper.PixelWidth;
            double wplH = wallpaper.PixelHeight;
            (double imgLeft, double imgTop, double imgW, double imgH) = LayoutWallpaper(
                mode, tile, monW, monH, wplW, wplH);

            double relX = wr.Left - monLeft;
            double relY = wr.Top - monTop;

            double srcX = (relX - imgLeft) / imgW * wplW;
            double srcY = (relY - imgTop) / imgH * wplH;
            double srcW = winW / imgW * wplW;
            double srcH = winH / imgH * wplH;

            srcX = Math.Max(0, srcX);
            srcY = Math.Max(0, srcY);
            srcW = Math.Min(srcW, wallpaper.PixelWidth - srcX);
            srcH = Math.Min(srcH, wallpaper.PixelHeight - srcY);

            if (srcW <= 0 || srcH <= 0) return null;

            int sx = (int)Math.Floor(srcX);
            int sy = (int)Math.Floor(srcY);
            int sw = (int)Math.Ceiling(srcW);
            int sh = (int)Math.Ceiling(srcH);
            sw = Math.Min(sw, wallpaper.PixelWidth - sx);
            sh = Math.Min(sh, wallpaper.PixelHeight - sy);
            if (sw <= 0 || sh <= 0) return null;

            var crop = new CroppedBitmap(wallpaper, new Int32Rect(sx, sy, sw, sh));
            crop.Freeze();
            return crop;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WallpaperBackdrop] crop failed: {ex.Message}");
            return null;
        }
    }

    internal static (double left, double top, double w, double h) LayoutWallpaper(
        int mode, bool tile, int monW, int monH, double wplW, double wplH)
    {
        if (tile)
            return (0, 0, monW, monH);

        double imgW, imgH;
        switch (mode)
        {
            case 10: // Fill
                { double s = Math.Max(monW / wplW, monH / wplH); imgW = wplW * s; imgH = wplH * s; break; }
            case 6:  // Fit
                { double s = Math.Min(monW / wplW, monH / wplH); imgW = wplW * s; imgH = wplH * s; break; }
            case 2:  // Stretch
                imgW = monW; imgH = monH; break;
            default: // Center
                imgW = wplW; imgH = wplH; break;
        }

        double left = (monW - imgW) / 2.0;
        double top = (monH - imgH) / 2.0;
        return (left, top, imgW, imgH);
    }

    /// <summary>
    /// Returns the monitor RECT that contains the window's center point,
    /// using IDesktopWallpaper COM (Win8+) or MonitorFromWindow fallback.
    /// </summary>
    private static bool GetMonitorRectForWindow(IntPtr hwnd,
        out int left, out int top, out int w, out int h)
    {
        left = top = w = h = 0;

        if (!GetWindowRect(hwnd, out var wr))
            return GetMonitorRect(hwnd, out left, out top, out w, out h);

        int cx = (wr.Left + wr.Right) / 2;
        int cy = (wr.Top + wr.Bottom) / 2;

        try
        {
            var dw = CreateDesktopWallpaper();
            if (dw is not null)
            {
                dw.GetMonitorDevicePathCount(out uint count);
                for (uint i = 0; i < count; i++)
                {
                    dw.GetMonitorDevicePathAt(i, out string monId);
                    dw.GetMonitorRECT(monId, out RECT r);
                    if (cx >= r.Left && cx < r.Right && cy >= r.Top && cy < r.Bottom)
                    {
                        left = r.Left;
                        top = r.Top;
                        w = r.Right - r.Left;
                        h = r.Bottom - r.Top;
                        return true;
                    }
                }
            }
        }
        catch { }

        return GetMonitorRect(hwnd, out left, out top, out w, out h);
    }

    private static BitmapSource? LoadWallpaper(string path)
    {
        // Load via FileStream instead of UriSource to bypass WPF's internal
        // URI-based bitmap cache — even with CacheOption=OnLoad, WPF can
        // return a cached instance when the same URI is loaded repeatedly.
        // TranscodedWallpaper is rewritten in-place by Windows, so we must
        // read the file bytes fresh every time.
        var bytes = File.ReadAllBytes(path);
        using var ms = new MemoryStream(bytes);
        var source = new BitmapImage();
        source.BeginInit();
        source.StreamSource = ms;
        source.CacheOption = BitmapCacheOption.OnLoad;
        source.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        source.EndInit();
        source.Freeze();
        return source;
    }

    private static (int mode, bool tile) GetLayout()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(DesktopKey);
            var style = key?.GetValue("WallpaperStyle") as string ?? "10";
            var tile = key?.GetValue("TileWallpaper") as string ?? "0";
            return (int.TryParse(style, out var m) ? m : 10, tile == "1");
        }
        catch { return (10, false); }
    }

    private static void StartTranscodedWatcher()
    {
        try
        {
            var themesDir = Path.GetDirectoryName(s_transcodedPath);
            if (string.IsNullOrEmpty(themesDir) || !Directory.Exists(themesDir)) return;

            var fileName = Path.GetFileName(s_transcodedPath);
            s_transcodedWatcher = new FileSystemWatcher(themesDir, fileName)
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size,
                EnableRaisingEvents = true,
            };
            s_transcodedWatcher.Changed += OnTranscodedChanged;
            s_transcodedWatcher.Created += OnTranscodedChanged;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("[WallpaperBackdrop] watcher: " + ex.Message);
        }
    }

    private static void OnTranscodedChanged(object sender, FileSystemEventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(
            DispatcherPriority.Background, new Action(ScheduleWallpaperChanged));
    }

    private static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category == UserPreferenceCategory.Desktop)
            ScheduleWallpaperChanged();
    }

    private static void ScheduleWallpaperChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null) return;

        if (s_changeDebounce is null)
        {
            s_changeDebounce = new DispatcherTimer(
                TimeSpan.FromMilliseconds(300),
                DispatcherPriority.Background,
                (_, _) => { s_changeDebounce?.Stop(); WallpaperChanged?.Invoke(); },
                dispatcher);
        }
        s_changeDebounce.Stop();
        s_changeDebounce.Start();
    }

    // ── IDesktopWallpaper COM ─────────────────────────────────────────

    private static IDesktopWallpaper? CreateDesktopWallpaper()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(
                new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"), throwOnError: false);
            if (type is null) return null;
            return (IDesktopWallpaper)Activator.CreateInstance(type)!;
        }
        catch { return null; }
    }

    [ComImport]
    [Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        [PreserveSig] int SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string m, [MarshalAs(UnmanagedType.LPWStr)] string w);
        [PreserveSig] int GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string m, [MarshalAs(UnmanagedType.LPWStr)] out string w);
        [PreserveSig] int GetMonitorDevicePathAt(uint i, [MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetMonitorDevicePathCount(out uint c);
        [PreserveSig] int GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string m, out RECT r);
        [PreserveSig] int _SetBackgroundColor(uint c);
        [PreserveSig] int _GetBackgroundColor(out uint c);
        [PreserveSig] int _SetPosition(int p);
        [PreserveSig] int _GetPosition(out int p);
        [PreserveSig] int _SetSlideshow([MarshalAs(UnmanagedType.LPWStr)] string items);
        [PreserveSig] int _GetSlideshow([MarshalAs(UnmanagedType.LPWStr)] out string items);
        [PreserveSig] int _SetSlideshowOptions(int o, uint t);
        [PreserveSig] int _GetSlideshowOptions(out int o, out uint t);
        [PreserveSig] int _AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string m, int d);
        [PreserveSig] int _GetStatus(out int s);
        [PreserveSig] int _Enable([MarshalAs(UnmanagedType.Bool)] bool e);
    }

    // ── native interop ─────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    private static bool GetMonitorRect(IntPtr hwnd, out int left, out int top, out int w, out int h)
    {
        left = top = w = h = 0;
        var monitor = MonitorFromWindow(hwnd, 2);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monitor, ref mi)) return false;
        left = mi.rcMonitor.Left;
        top = mi.rcMonitor.Top;
        w = mi.rcMonitor.Right - mi.rcMonitor.Left;
        h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
        return true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
