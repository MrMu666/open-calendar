using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CalendarApp.Platform;

/// <summary>
/// Fences-style blurred background: instead of relying on the DWM blur
/// compositor (which renders as an opaque black rectangle over RDP / software
/// compositors), we read the desktop wallpaper bitmap itself, blur it in image
/// space (downscale + upscale = cheap gaussian approximation), and reuse it as
/// the panel's own background. Pure client-side content: identical output on
/// local sessions, RDP, and software rendering.
/// </summary>
public static class WallpaperBackdrop
{
    private const int SPI_GETDESKWALLPAPER = 0x0073;

    /// <summary>
    /// Builds an ImageBrush of the current wallpaper, blurred via
    /// downscale/upscale. Returns an empty brush (no image) when no wallpaper
    /// is set or the file cannot be loaded — the panel then falls back to the
    /// tint-only look.
    /// </summary>
    public static ImageBrush CreateBlurredWallpaperBrush()
    {
        var path = GetWallpaperPath();
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new ImageBrush();
        }

        try
        {
            var source = new BitmapImage();
            source.BeginInit();
            source.UriSource = new Uri(path, UriKind.Absolute);
            source.CacheOption = BitmapCacheOption.OnLoad;
            source.EndInit();
            source.Freeze();

            // Downscale to ~1/8 then upscale back: bilinear smoothing doubles as
            // a strong gaussian-like blur and works on any renderer (no GPU).
            const double scale = 0.125;
            var small = new TransformedBitmap(source, new ScaleTransform(scale, scale));
            small.Freeze();
            var blurred = new TransformedBitmap(small, new ScaleTransform(1.0 / scale, 1.0 / scale));
            blurred.Freeze();

            var brush = new ImageBrush(blurred)
            {
                Stretch = Stretch.UniformToFill,
            };
            brush.Freeze();
            return brush;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WallpaperBackdrop] 壁纸加载失败: {ex.Message}");
            return new ImageBrush();
        }
    }

    /// <summary>Reads the current wallpaper file path from the shell.</summary>
    public static string GetWallpaperPath()
    {
        var sb = new StringBuilder(512);
        _ = SystemParametersInfo(SPI_GETDESKWALLPAPER, (uint)sb.Capacity, sb, 0);
        var path = sb.ToString();
        return string.IsNullOrWhiteSpace(path) ? string.Empty : path;
    }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SystemParametersInfo(
        int uiAction, uint uiParam, StringBuilder pvParam, int fWinIni);
}
