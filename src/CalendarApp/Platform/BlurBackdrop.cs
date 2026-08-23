using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace CalendarApp.Platform;

/// <summary>
/// Background blur for the main glass panel.
///
/// Primary path: SetWindowCompositionAttribute with ACCENT_ENABLE_ACRYLICBLURBEHIND —
/// DWM composites a real gaussian blur of everything behind the window and works
/// with layered (AllowsTransparency) WPF windows. GradientColor alpha stays ~0 so
/// the app's own PanelBackground brush provides the user tint on top of the blur.
///
/// Fallback path: DWMWA_SYSTEMBACKDROP_TYPE = DWMSBT_TRANSIENTWINDOW (Win11 22H2+,
/// non-layered windows only).
/// </summary>
public static class BlurBackdrop
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_TRANSIENTWINDOW = 3; // acrylic

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;
    private const int ACCENT_FLAG_DRAW_ALL_BORDERS = 2;

    /// <summary>Applies background blur + dark mode to the window. Returns true when the acrylic policy was applied.</summary>
    public static bool EnableAcrylic(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return false;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;

        return EnableAcrylic(hwnd);
    }

    /// <summary>Applies background blur + dark mode to an existing HWND.</summary>
    public static bool EnableAcrylic(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return false;

        EnableDarkMode(hwnd);

        if (TrySetAcrylicAccent(hwnd))
        {
            return true;
        }

        // Fallback: Win11 system backdrop (requires a non-layered window).
        var backdrop = DWMSBT_TRANSIENTWINDOW;
        var hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        return hr == 0;
    }

    /// <summary>Dark titlebar/backdrop blending hint (best-effort).</summary>
    public static void EnableDarkMode(IntPtr hwnd)
    {
        if (!OperatingSystem.IsWindows() || hwnd == IntPtr.Zero) return;
        var dark = 1;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
    }

    private static bool TrySetAcrylicAccent(IntPtr hwnd)
    {
        try
        {
            var accent = new ACCENTPOLICY
            {
                AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
                AccentFlags = ACCENT_FLAG_DRAW_ALL_BORDERS,
                GradientColor = 0x01000000, // ABGR, alpha=1: no tint of its own
            };
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
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
