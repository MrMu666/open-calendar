using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CalendarApp.Data;

namespace CalendarApp.Platform;

/// <summary>
/// Persists and restores the main panel's geometry across sessions.
///
/// Geometry is now stored in LastViewSettings (lastWindow.json) keyed by the
/// current monitor's work-area resolution + DPI. This means:
/// - Dragging between monitors saves/restores independent positions per config
/// - DPI changes are detected and switch to that configuration's saved size
/// - Regular settings.json (UserSettings) no longer carries window geometry
///
/// Position is stored in PHYSICAL pixels (Win32 GetWindowRect/SetWindowPos),
/// size in DIPs (WPF ActualWidth/ActualHeight).
/// </summary>
public static class WindowGeometry
{
    private const int SWP_NOSIZE = 0x0001;
    private const int SWP_NOZORDER = 0x0004;
    private const int SWP_NOACTIVATE = 0x0010;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int DefaultWidth = 340;
    private const int DefaultHeight = 500;
    private const int DockMarginPx = 16;
    private const double MinSize = 200;
    private const double MaxSize = 4000;

    /// <summary>Last known resolution key; used to detect monitor/DPI changes.</summary>
    private static string? _lastKey;

    /// <summary>
    /// Called on every LocationChanged or SizeChanged. Detects resolution/DPI
    /// changes and auto-restores the saved geometry for the new monitor.
    /// Returns true if the geometry was restored (caller should skip save).
    /// </summary>
    public static bool OnMovedOrResized(Window w, LastViewSettings lastView)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var key = GetCurrentKey(hwnd);
        if (key is null) return false;

        if (_lastKey != key)
        {
            ApplyKeyChange(w, lastView, key);
            return true;
        }

        return false;
    }

    /// <summary>
    /// Re-checks the current monitor key and, when it changed (RDP reconnect,
    /// monitor hot-plug, resolution/DPI change), restores that configuration's
    /// saved geometry or applies the default bottom-right placement. Safe to
    /// call from SystemEvents.DisplaySettingsChanged / SessionSwitch even when
    /// the window was not moved or resized (those events don't fire WPF's
    /// LocationChanged/SizeChanged).
    /// </summary>
    public static void Reconcile(Window w, LastViewSettings lastView)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        var key = GetCurrentKey(hwnd);
        if (key is null) return;

        if (_lastKey == key) return;

        ApplyKeyChange(w, lastView, key);
    }

    /// <summary>
    /// Switches to a new monitor key: restores the saved geometry for that key,
    /// or docks bottom-right at an adaptive size when no entry exists yet.
    /// </summary>
    private static void ApplyKeyChange(Window w, LastViewSettings lastView, string key)
    {
        _lastKey = key;
        var hwnd = new WindowInteropHelper(w).Handle;
        var saved = lastView.GetGeometry(key);
        if (saved is not null && saved.Width >= MinSize && saved.Width <= MaxSize
                               && saved.Height >= MinSize && saved.Height <= MaxSize)
        {
            w.Width = saved.Width;
            w.Height = saved.Height;
            if (hwnd != IntPtr.Zero)
            {
                var clamped = ClampToWorkArea(hwnd, saved.X, saved.Y, saved.Width, saved.Height);
                SetWindowPos(hwnd, IntPtr.Zero, clamped.X, clamped.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
            return;
        }

        // No saved entry for the new monitor — apply default placement.
        ApplyDefaultPlacement(w);
    }

    /// <summary>
    /// Saves the window's current geometry into LastViewSettings under the
    /// current monitor's resolution key.
    /// </summary>
    public static void SaveNow(Window w, LastViewSettings lastView)
    {
        if (w.WindowState != WindowState.Normal) return;

        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        var key = GetCurrentKey(hwnd);
        if (key is null) return;

        int x = 0, y = 0;
        if (GetWindowRect(hwnd, out var rect))
        {
            x = rect.Left;
            y = rect.Top;
        }

        lastView.SetGeometry(key, x, y, w.Width, w.Height);
        lastView.Save();
    }

    /// <summary>
    /// Tries to restore the window geometry from LastViewSettings for the
    /// current monitor resolution. Returns true if a valid saved entry exists;
    /// false when the caller should fall back to default bottom-right docking.
    /// </summary>
    public static bool TryRestore(Window w, LastViewSettings lastView)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return false;

        var key = GetCurrentKey(hwnd);
        if (key is null) return false;

        var saved = lastView.GetGeometry(key);
        if (saved is null) return false;

        if (saved.Width < MinSize || saved.Width > MaxSize ||
            saved.Height < MinSize || saved.Height > MaxSize)
        {
            return false;
        }

        w.Width = saved.Width;
        w.Height = saved.Height;

        void Apply()
        {
            var h = new WindowInteropHelper(w).Handle;
            if (h != IntPtr.Zero)
            {
                var clamped = ClampToWorkArea(h, saved.X, saved.Y, saved.Width, saved.Height);
                SetWindowPos(h, IntPtr.Zero, clamped.X, clamped.Y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        if (hwnd != IntPtr.Zero) Apply();
        else w.SourceInitialized += (_, _) => Apply();

        return true;
    }

    /// <summary>
    /// Reset ONLY the current monitor's geometry record, then dock the window
    /// at the bottom-right corner of the current monitor. Other monitor entries
    /// are untouched.
    /// </summary>
    public static void ResetPosition(Window w, LastViewSettings lastView)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        var key = GetCurrentKey(hwnd);
        if (key is not null)
        {
            lastView.RemoveGeometry(key);
            lastView.Save();
        }

        ApplyDefaultPlacement(w);
    }

    /// <summary>
    /// Computes the default panel size (DIPs) sized to the current desktop
    /// resolution: roughly 18% of the work-area width x 50% of its height,
    /// scaled back to DIPs via the window DPI and clamped to the allowed range.
    /// </summary>
    public static (double Width, double Height) ComputeAdaptiveSize(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return (DefaultWidth, DefaultHeight);

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorInfo(monitor, out var mi))
            return (DefaultWidth, DefaultHeight);

        int workW = mi.rcWork.Right - mi.rcWork.Left;
        int workH = mi.rcWork.Bottom - mi.rcWork.Top;
        if (workW <= 0 || workH <= 0)
            return (DefaultWidth, DefaultHeight);

        double width = Math.Round(workW * 0.18) / scale;
        double height = Math.Round(workH * 0.5) / scale;

        return (Math.Max(w.MinWidth, Clamp(width, MinSize, MaxSize)),
                Math.Max(w.MinHeight, Clamp(height, MinSize, MaxSize)));
    }

    /// <summary>
    /// Sizes the panel to the current desktop resolution and docks it at the
    /// bottom-right of its monitor's work area with a 16px margin.
    /// </summary>
    public static void ApplyDefaultPlacement(Window w)
    {
        var (width, height) = ComputeAdaptiveSize(w);
        w.Width = width;
        w.Height = height;

        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero) return;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;
        DockBottomRight(hwnd, (int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>
    /// Returns the persistence key for the monitor the given HWND is on,
    /// or null when the monitor info cannot be queried.
    /// </summary>
    private static string? GetCurrentKey(IntPtr hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorInfo(monitor, out var mi)) return null;

        int workW = mi.rcWork.Right - mi.rcWork.Left;
        int workH = mi.rcWork.Bottom - mi.rcWork.Top;
        if (workW <= 0 || workH <= 0) return null;

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;

        return LastViewSettings.MakeKey(workW, workH, dpi);
    }

    /// <summary>
    /// Clamps the window position so no part of the client area is off-screen,
    /// keeping at least 100px of the title bar visible even at minimum.
    /// </summary>
    private static (int X, int Y) ClampToWorkArea(IntPtr hwnd, int x, int y, double widthDip, double heightDip)
    {
        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        double scale = dpi / 96.0;

        int wPx = (int)Math.Round(widthDip * scale);
        int hPx = (int)Math.Round(heightDip * scale);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorInfo(monitor, out var mi)) return (x, y);

        int minVisiblePx = 80; // at least 80px visible on each axis

        if (x < mi.rcWork.Left - wPx + minVisiblePx) x = mi.rcWork.Left - wPx + minVisiblePx;
        if (x > mi.rcWork.Right - minVisiblePx) x = mi.rcWork.Right - minVisiblePx;
        if (y < mi.rcWork.Top - hPx + minVisiblePx) y = mi.rcWork.Top - hPx + minVisiblePx;
        if (y > mi.rcWork.Bottom - minVisiblePx) y = mi.rcWork.Bottom - minVisiblePx;

        return (x, y);
    }

    /// <summary>
    /// Docks the window at the bottom-right of its monitor's work area.
    /// </summary>
    private static void DockBottomRight(IntPtr hwnd, int widthPx, int heightPx)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!TryGetMonitorInfo(monitor, out var mi)) return;

        int x = mi.rcWork.Right - widthPx - DockMarginPx;
        int y = mi.rcWork.Bottom - heightPx - DockMarginPx;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Clamp(value, min, max);

    // ── P/Invoke ──────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, int uFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, out MONITORINFO lpmi);

    /// <summary>
    /// GetMonitorInfo requires cbSize to be set before the call; otherwise it
    /// fails and every geometry lookup silently bails. This wrapper always
    /// initialises the struct so callers can rely on a correct result.
    /// </summary>
    private static bool TryGetMonitorInfo(IntPtr hMonitor, out MONITORINFO mi)
    {
        mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(hMonitor, out mi);
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}