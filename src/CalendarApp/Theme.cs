using System.Windows;
using System.Windows.Media;

namespace CalendarApp;

/// <summary>
/// Shared accent brushes used by the calendar selected day, the 新增事项
/// button and the date-jump button. All three places reference the same
/// brush instances, and <see cref="ApplyAccent"/> mutates their colors in
/// place, so a settings change repaints every consumer live without
/// rebuilding windows. Hover/pressed are auto-derived darker shades.
/// </summary>
public static class Theme
{
    /// <summary>Accent color — default #4CAF50, same as the calendar selected day.</summary>
    public static SolidColorBrush Accent { get; } = MakeBrush("#4CAF50");

    /// <summary>Slightly darker shade used for button hover.</summary>
    public static SolidColorBrush AccentHover { get; } = MakeBrush("#43A047");

    /// <summary>Darker shade used for button pressed state.</summary>
    public static SolidColorBrush AccentPressed { get; } = MakeBrush("#3E8E41");

    /// <summary>Re-applies the accent color (and derived hover/pressed shades) from a hex string.</summary>
    public static void ApplyAccent(string hex)
    {
        if (!TryParseHexColor(hex, out var color))
        {
            return;
        }

        Accent.Color = color;
        AccentHover.Color = Scale(color, 0.88);
        AccentPressed.Color = Scale(color, 0.78);
    }

    /// <summary>Puts the shared accent brushes into application resources so XAML can reference them.</summary>
    public static void RegisterResources()
    {
        var app = Application.Current;
        if (app is null)
        {
            return;
        }

        app.Resources["AccentBrush"] = Accent;
        app.Resources["AccentHoverBrush"] = AccentHover;
        app.Resources["AccentPressedBrush"] = AccentPressed;
    }

    private static SolidColorBrush MakeBrush(string hex)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        return brush;
    }

    private static bool TryParseHexColor(string hex, out Color color)
    {
        try
        {
            color = (Color)ColorConverter.ConvertFromString(hex);
            return true;
        }
        catch
        {
            color = default;
            return false;
        }
    }

    private static Color Scale(Color c, double factor) => Color.FromArgb(
        c.A,
        (byte)Math.Clamp((int)Math.Round(c.R * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.G * factor), 0, 255),
        (byte)Math.Clamp((int)Math.Round(c.B * factor), 0, 255));
}
