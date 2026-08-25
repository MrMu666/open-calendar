using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CalendarApp.Platform;

/// <summary>
/// Rainmeter-style live blur: <c>DwmEnableBlurBehindWindow</c> with
/// <c>DWM_BB_BLURREGION</c> on a layered window. Mirrors
/// <c>Skin::ShowBlur() / BlurBehindWindow()</c> from
/// <c>rainmeter/rainmeter Library/Skin.cpp</c>.
///
/// * Checks <c>DwmIsCompositionEnabled</c> and <c>DwmGetColorizationColor</c>
///   (opaque → no blur), exactly like Rainmeter.
/// * Creates a full-window <c>HRGN</c> via <c>CreateRectRgn</c> sized to the
///   physical window bounds (DPI-aware).
/// * Re-applies on resize; disables on close.
/// Unlike <c>SetWindowCompositionAttribute(ACCENT_*)</c>, this path does NOT
/// produce an opaque black rectangle over RDP/software compositors — when
/// composition is unavailable it simply leaves the window as a tinted
/// translucent panel (graceful fallback, no black).
/// </summary>
public static class BlurBackdrop
{
    private const uint DWM_BB_ENABLE = 0x00000001;
    private const uint DWM_BB_BLURREGION = 0x00000002;

    // One region handle per window, like Skin::m_BlurRegion.
    private static readonly Dictionary<IntPtr, IntPtr> s_regions = new();

    /// <summary>Enables Rainmeter-style blur on a WPF Window. Call from Loaded / SourceInitialized.</summary>
    public static bool EnableBlur(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return false;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        var size = GetPhysicalSize(window);
        return EnableBlur(hwnd, size.Width, size.Height);
    }

    /// <summary>Enables blur for an HWND with explicit physical size. DPI-aware.</summary>
    public static bool EnableBlur(IntPtr hwnd, int physicalW, int physicalH)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;
        if (physicalW <= 0 || physicalH <= 0) return false;

        // Rainmeter: if DWM composition is disabled or colorization is opaque, skip blur.
        if (DwmIsCompositionEnabled(out var enabled) != 0 || !enabled) return false;
        if (DwmGetColorizationColor(out _, out var opaque) == 0 && opaque) return false;

        // Drop previous region for this window, like Skin::ShowBlur() does for BLURMODE_FULL.
        if (s_regions.TryGetValue(hwnd, out var old) && old != IntPtr.Zero)
        {
            DeleteObject(old);
            s_regions.Remove(hwnd);
        }

        var hRgn = CreateRectRgn(0, 0, physicalW, physicalH);
        if (hRgn == IntPtr.Zero) return false;
        s_regions[hwnd] = hRgn;

        var bb = new DWM_BLURBEHIND
        {
            dwFlags = DWM_BB_ENABLE | DWM_BB_BLURREGION,
            fEnable = true,
            hRgnBlur = hRgn,
            fTransitionOnMaximized = false,
        };
        var hr = DwmEnableBlurBehindWindow(hwnd, ref bb);
        return hr == 0;
    }

    /// <summary>Updates the blur region after a resize. Call from SizeChanged.</summary>
    public static void UpdateBlurRegion(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var size = GetPhysicalSize(window);
        EnableBlur(hwnd, size.Width, size.Height);
    }

    /// <summary>Disables blur and frees the region. Call from Closed.</summary>
    public static void DisableBlur(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        if (s_regions.TryGetValue(hwnd, out var hRgn) && hRgn != IntPtr.Zero)
        {
            DeleteObject(hRgn);
            s_regions.Remove(hwnd);
        }
        var bb = new DWM_BLURBEHIND { dwFlags = DWM_BB_ENABLE, fEnable = false };
        DwmEnableBlurBehindWindow(hwnd, ref bb);
    }

    public static void DisableBlur(Window window)
    {
        if (window is null) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        DisableBlur(hwnd);
    }

    private static (int Width, int Height) GetPhysicalSize(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        double dpiX = 1.0, dpiY = 1.0;
        if (source?.CompositionTarget != null)
        {
            dpiX = source.CompositionTarget.TransformToDevice.M11;
            dpiY = source.CompositionTarget.TransformToDevice.M22;
        }
        var w = (int)Math.Round(window.ActualWidth * dpiX);
        var h = (int)Math.Round(window.ActualHeight * dpiY);
        // Fallback: use window Width/Height if not yet rendered.
        if (w <= 0) w = (int)Math.Round(window.Width * dpiX);
        if (h <= 0) h = (int)Math.Round(window.Height * dpiY);
        if (w <= 0) w = 340;
        if (h <= 0) h = 500;
        return (w, h);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DWM_BLURBEHIND
    {
        public uint dwFlags;
        public bool fEnable;
        public IntPtr hRgnBlur;
        public bool fTransitionOnMaximized;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmIsCompositionEnabled(out bool pfEnabled);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetColorizationColor(out uint pcrColorization, out bool pfOpaqueBlend);

    [DllImport("dwmapi.dll")]
    private static extern int DwmEnableBlurBehindWindow(IntPtr hWnd, ref DWM_BLURBEHIND pBlurBehind);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int x1, int y1, int x2, int y2);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    // Legacy acrylic path kept for reference but NOT used by the Rainmeter flow.
    // If you need to re-enable it, call the ACCT path below instead of DWM blur.
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENTPOLICY { public int AccentState; public int AccentFlags; public int GradientColor; public int AnimationId; }
    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA { public int Attribute; public IntPtr Data; public int SizeOfData; }
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
