using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CalendarApp.Platform;

/// <summary>
/// DWM backdrop blur for the main glass panel (Windows 11).
///
/// Uses DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW (acrylic).
/// Falls back to SetWindowCompositionAttribute ACCENT_ENABLE_ACRYLICBLURBEHIND
/// on builds where the system-backdrop attribute is not supported.
///
/// IMPORTANT: this requires a NON-layered WPF window (AllowsTransparency
/// must be false) with WindowChrome GlassFrameThickness = -1. The window's
/// own background brushes carry the user tint (color + opacity); the DWM
/// backdrop supplies the blur behind that tint.
/// </summary>
public static class BlurBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic
    private const int DWMWCP_ROUND = 2;

    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    /// <summary>Applies acrylic backdrop blur + dark mode to the window. Returns true when the DWM attribute path succeeded.</summary>
    public static bool EnableAcrylic(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        return EnableAcrylic(hwnd);
    }

    /// <summary>Applies acrylic backdrop blur + dark mode to an existing HWND. Returns true when the DWM attribute path succeeded.</summary>
    public static bool EnableAcrylic(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;

        var dark = 1;
        var res = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        _ = res; // dark mode hint is best-effort; acrylic is what matters

        var backdrop = DWMSBT_TRANSIENTWINDOW;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));

        if (hr == 0)
        {
            return true;
        }

        // Fallback: pre-Win11-22621 builds / attribute rejected
        return TrySetAcrylicFallback(hwnd);
    }

    /// <summary>Requests DWM-native rounded corners for this HWND (Windows 11).</summary>
    public static void EnableRoundedCorners(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;

        var pref = DWMWCP_ROUND;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    private static bool TrySetAcrylicFallback(IntPtr hwnd)
    {
        try
        {
            var accent = new ACCENTPOLICY
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                GradientColor = 0x01000000, // near-transparent tint, ABGR
            };
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = 19, // WCA_ACCENT_POLICY
                SizeOfData = Marshal.SizeOf<ACCENTPOLICY>(),
            };
            var handle = GCHandle.Alloc(accent, GCHandleType.Pinned);
            try
            {
                data.Data = handle.AddrOfPinnedObject();
                return SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                handle.Free();
            }
        }
        catch
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENTPOLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);
}
