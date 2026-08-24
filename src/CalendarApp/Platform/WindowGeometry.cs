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
    /// Computes the default panel size (DIPs) sized to the current desktop
    /// resolution: roughly 18% of the work-area width x 50% of its height,
    /// scaled back to DIPs via the window DPI and clamped to the allowed range.
    /// Returns the 340x500 constants when the window, monitor or DPI cannot be
    /// determined. Higher-resolution screens get a proportionally larger panel.
    /// </summary>
    public static (double Width, double Height) ComputeAdaptiveSize(Window w)
    {
        var hwnd = new WindowInteropHelper(w).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return (DefaultWidth, DefaultHeight);
        }

        var dpi = GetDpiForWindow(hwnd);
        if (dpi == 0)
        {
            dpi = 96;
        }
        double scale = dpi / 96.0;

        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!GetMonitorInfo(monitor, out var mi))
        {
            return (DefaultWidth, DefaultHeight);
        }

        int workW = mi.rcWork.Right - mi.rcWork.Left;
        int workH = mi.rcWork.Bottom - mi.rcWork.Top;
        if (workW <= 0 || workH <= 0)
        {
            return (DefaultWidth, DefaultHeight);
        }

        double width = Math.Round(workW * 0.18) / scale;
        double height = Math.Round(workH * 0.5) / scale;

        // Respect the window's own XAML minimums so sizing never fights layout.
        return (Math.Max(w.MinWidth, Clamp(width, MinSize, MaxSize)),
                Math.Max(w.MinHeight, Clamp(height, MinSize, MaxSize)));
    }

    /// <summary>
    /// Sizes the panel to the current desktop resolution and docks it at the
    /// bottom-right of its monitor's work area with a 16px margin. Used by the
    /// tray "重置窗口位置" action and by the default placement on a fresh setup
    /// where there is no saved geometry to restore.
    /// </summary>
    public static void ApplyDefaultPlacement(Window w)
    {
        var (width, height) = ComputeAdaptiveSize(w);
        w.Width = width;
        w.Height = height;

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
        DockBottomRight(hwnd, (int)Math.Round(width * scale), (int)Math.Round(height * scale));
    }

    /// <summary>
    /// Docks the window at the bottom-right of its monitor's work area with a
    /// 16px physical-pixel margin, given the window's intended physical size.
    /// </summary>
    private static void DockBottomRight(IntPtr hwnd, int widthPx, int heightPx)
    {
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (!GetMonitorInfo(monitor, out var mi))
        {
            return;
        }

        int x = mi.rcWork.Right - widthPx - DockMarginPx;
        int y = mi.rcWork.Bottom - heightPx - DockMarginPx;

        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    private static double Clamp(double value, double min, double max) =>
        Math.Clamp(value, min, max);

    /// <summary>Clears persisted geometry, sizes to the current resolution, and docks it bottom-right.</summary>
    public static void ResetPosition(Window w, UserSettings settings)
    {
        settings.WindowX = null;
        settings.WindowY = null;
        settings.WindowWidth = null;
        settings.WindowHeight = null;
        settings.Save();

        ApplyDefaultPlacement(w);
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
