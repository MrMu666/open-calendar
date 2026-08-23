using System.Windows;
using System.Windows.Media;

namespace CalendarApp;

/// <summary>
/// Shared accent brush factory used by the calendar selected day, the 新增事项
/// button and the date-jump button. All consumers reference the brushes via
/// application resources; <see cref="ApplyAccent"/> REPLACES the registered
/// brushes with freshly-created instances (WPF freezes Freezables stored in
/// resource dictionaries, so in-place mutation is impossible — replacement
/// keeps every DynamicResource reference alive automatically).
/// </summary>
public static class Theme
{
    private static readonly Brush _fallback = CreateBrush("#4CAF50");

    /// <summary>Accent color — default #4CAF50, same as the calendar selected day.</summary>
    public static Brush Accent { get; private set; } = _fallback;

    /// <summary>Slightly darker shade used for button hover.</summary>
    public static Brush AccentHover { get; private set; } = CreateBrush("#43A047");

    /// <summary>Darker shade used for button pressed state.</summary>
    public static Brush AccentPressed { get; private set; } = CreateBrush("#3E8E41");

    /// <summary>Re-applies the accent color by replacing all three brushes.</summary>
    public static void ApplyAccent(string hex)
    {
        if (!TryParseHexColor(hex, out var color))
        {
            return;
        }

        var accent = CreateBrush(color);
        var hover = CreateBrush(Scale(color, 0.88));
        var pressed = CreateBrush(Scale(color, 0.78));

        var app = Application.Current;
        if (app is not null)
        {
            app.Resources["AccentBrush"] = accent;
            app.Resources["AccentHoverBrush"] = hover;
            app.Resources["AccentPressedBrush"] = pressed;
        }

        Accent = accent;
        AccentHover = hover;
        AccentPressed = pressed;
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

    private static Brush CreateBrush(string hex) =>
        CreateBrush((Color)ColorConverter.ConvertFromString(hex));

    private static Brush CreateBrush(Color color) => new SolidColorBrush(color);

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
