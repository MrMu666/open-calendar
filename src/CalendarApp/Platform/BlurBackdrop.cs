using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace CalendarApp.Platform;

/// <summary>
/// Manages the blurred-wallpaper backdrop without DWM (RDP-safe).
///
/// The panel uses three stacked layers:
///   1. Wallpaper crop layer — an Image that shows the desktop wallpaper
///      cropped to the window region (WallpaperBackdrop.CropForWindow), with
///      a WPF BlurEffect applied.
///   2. Tint layer — a SolidColorBrush with user-configurable colour and
///      opacity, rendered on top of the blurred wallpaper.
///   3. Hit-test floor (1/255 alpha Border, managed by the XAML).
///
/// Call Attach once from Loaded, ScheduleRefresh from LocationChanged /
/// SizeChanged (100 ms debounce), Detach from Closed.
/// </summary>
public static class BlurBackdrop
{
    private static readonly Dictionary<IntPtr, BackdropState> s_states = new();

    private sealed class BackdropState
    {
        public required Image WallpaperImage { get; init; }
        public required BlurEffect BlurEffect { get; init; }
        public DispatcherTimer? DebounceTimer { get; set; }
        /// <summary>Retry counter for transient file-lock failures.
        /// Reset to 0 on each successful crop.</summary>
        public int RetryCount { get; set; }
    }

    /// <summary>
    /// Attaches the backdrop system to a window. The window must have named
    /// children: x:Name="WallpaperImage" (Image) and x:Name="WallpaperBlur"
    /// (BlurEffect).
    /// </summary>
    public static void Attach(Window window, Image wallpaperImage, BlurEffect blurEffect)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        if (s_states.ContainsKey(hwnd)) Detach(window);

        var state = new BackdropState
        {
            WallpaperImage = wallpaperImage,
            BlurEffect = blurEffect,
        };
        s_states[hwnd] = state;

        WallpaperBackdrop.WallpaperChanged += OnWallpaperChanged;
        RefreshBlur(window);
    }

    /// <summary>Removes the backdrop from this window. Call from Closed.</summary>
    public static void Detach(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        WallpaperBackdrop.WallpaperChanged -= OnWallpaperChanged;

        if (s_states.TryGetValue(hwnd, out var state))
        {
            state.DebounceTimer?.Stop();
            state.DebounceTimer = null;
            state.WallpaperImage.Source = null;
            s_states.Remove(hwnd);
        }
    }

    /// <summary>
    /// Captures the wallpaper under the window immediately and applies the blur.
    /// Transient file-lock failures (TranscodedWallpaper mid-write during
    /// slideshow transitions) trigger up to 3 automatic retries.
    /// </summary>
    public static void RefreshBlur(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !s_states.TryGetValue(hwnd, out var state)) return;

        var crop = WallpaperBackdrop.CropForWindow(window);
        if (crop is not null)
        {
            state.RetryCount = 0;
            state.WallpaperImage.Source = crop;
        }
        else if (state.RetryCount < 3)
        {
            state.RetryCount++;
            // File may be mid-write — retry after a short quiet period.
            ScheduleRefresh(window);
        }
    }

    /// <summary>
    /// Schedules a RefreshBlur after 100 ms of quiet. Call from
    /// LocationChanged / SizeChanged so dragging/resizing the window does not
    /// capture on every pixel.
    /// </summary>
    public static void ScheduleRefresh(Window window)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !s_states.TryGetValue(hwnd, out var state)) return;

        if (state.DebounceTimer is null)
        {
            var timer = new DispatcherTimer(
                TimeSpan.FromMilliseconds(100),
                DispatcherPriority.Background,
                (_, _) =>
                {
                    state.DebounceTimer?.Stop();
                    RefreshBlur(window);
                },
                window.Dispatcher);
            state.DebounceTimer = timer;
        }

        state.DebounceTimer.Stop();
        state.DebounceTimer.Start();
    }

    /// <summary>Sets the BlurEffect radius directly on the WPF effect.</summary>
    public static void SetBlurRadius(Window window, double radius)
    {
        if (!OperatingSystem.IsWindows() || window is null) return;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero || !s_states.TryGetValue(hwnd, out var state)) return;

        state.BlurEffect.Radius = Math.Max(0, radius);
    }

    private static void OnWallpaperChanged()
    {
        // Walk all attached windows and refresh.
        foreach (var (hwnd, _) in s_states)
        {
            foreach (Window w in Application.Current.Windows)
            {
                var wh = new System.Windows.Interop.WindowInteropHelper(w).Handle;
                if (wh == hwnd)
                {
                    RefreshBlur(w);
                    break;
                }
            }
        }
    }
}