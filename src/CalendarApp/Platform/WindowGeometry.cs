using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using CalendarApp.Data;

namespace CalendarApp.Platform;

/// <summary>
/// Persists and restores the main panel's geometry across sessions.
///
/// Position is stored in PHYSICAL pixels (Win32 GetWindowRect/SetWindowPos),
/// size in DIPs (WPF ActualWidth/ActualHeight). This keeps correct placement
/// when dragging between monitors with different DPI — a pixel position is
/// monitor-agnostic; the size is a logical size that WPF re-scales per monitor.
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

    /// <summary>
    /// Saves current geometry: X/Y as physical pixels (GetWindowRect), size as
    /// DIPs. No-op unless the window is in the Normal state.
    /// </summary>
    public static void SaveNow(Window w, UserSettings settings)
    {
        if (w.WindowState != WindowState.Normal)
        {
            return;
        }

        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd != IntPtr.Zero && GetWindowRect(hwnd, out var rect))
        {
            settings.WindowX = rect.Left;
            settings.WindowY = rect.Top;
        }

        settings.WindowWidth = w.Width;
        settings.WindowHeight = w.Height;
        settings.Save();
    }

    /// <summary>
    /// Applies persisted geometry before the window is shown. Returns true if a
    /// valid record was applied; false when the caller should fall back to the
    /// default bottom-right docking. Values outside [200,4000] are rejected.
    /// </summary>
    public static bool TryRestore(Window w, UserSettings settings)
    {
        if (settings.WindowX is not int x ||
            settings.WindowY is not int y ||
            settings.WindowWidth is not double width ||
            settings.WindowHeight is not double height)
        {
            return false;
        }

        if (width < MinSize || width > MaxSize || height < MinSize || height > MaxSize)
        {
            return false;
        }

        w.Width = width;
        w.Height = height;

        // Apply the pixel position once the HWND exists.
        void Apply()
        {
            var hwnd = new WindowInteropHelper(w).Handle;
            if (hwnd != IntPtr.Zero)
            {
                SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
                    SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
            }
        }

        if (new WindowInteropHelper(w).Handle != IntPtr.Zero)
        {
            Apply();
        }
        else
        {
            w.SourceInitialized += (_, _) => Apply();
        }

        return true;
    }

    /// <summary>
    /// Docks the window at the bottom-right of its monitor's work area with a
    /// 16px physical-pixel margin, using the default 340x500 DIP size.
    /// </summary>
    public static void DockBottomRight(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return;
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }

        double scale = dpi / 96.0;
        int widthPx = (int)(DefaultWidth * scale);
        int heightPx = (int)(DefaultHeight * scale);

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        GetMonitorInfo(monitor, out var mi);

        int x = mi.rcWork.Right - widthPx - DockMarginPx;
        int y = mi.rcWork.Bottom - heightPx - DockMarginPx;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>Clears persisted geometry and re-docks to the default position.</summary>
    public static void ResetPosition(Window w, UserSettings settings)
    {
        settings.WindowX = null;
        settings.WindowY = null;
        settings.WindowWidth = null;
        settings.WindowHeight = null;
        settings.Save();

        w.Width = DefaultWidth;
        w.Height = DefaultHeight;
        DockBottomRight(w);
    }

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

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
