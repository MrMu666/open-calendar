using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarApp.Data;

/// <summary>
/// The only state the app keeps between sessions in the user profile: the path
/// of the last opened sqlite data file plus app-level preferences (appearance,
/// window geometry). Stored as a small JSON file in the current user's profile
/// directory (e.g. <c>~/.scheduleapp/settings.json</c>). The data file itself
/// only holds schedule items; nothing else is ever written outside these two.
/// </summary>
public sealed class UserSettings
{
    public const string DefaultDirectoryName = ".scheduleapp";
    public const string DefaultFileName = "settings.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Absolute path of the last sqlite data file the user opened, if any.</summary>
    public string? LastOpenedFile { get; set; }

    /// <summary>Glass panel background color as hex (e.g. "#0D1117").</summary>
    public string BackgroundColor { get; set; } = "#0D1117";

    /// <summary>Glass panel background opacity, 0..1 (default 60%).</summary>
    public double BackgroundOpacity { get; set; } = 0.6;

    /// <summary>
    /// Accent color as hex (e.g. "#4CAF50"): calendar selected day, 新增事项
    /// button and date-jump button share this one color.
    /// </summary>
    public string AccentColor { get; set; } = "#4CAF50";

    /// <summary>Last main-window position (physical pixels). Null = never saved.</summary>
    public int? WindowX { get; set; }

    /// <summary>Last main-window position (physical pixels). Null = never saved.</summary>
    public int? WindowY { get; set; }

    /// <summary>Last main-window size (DIPs). Null = never saved.</summary>
    public double? WindowWidth { get; set; }

    /// <summary>Last main-window size (DIPs). Null = never saved.</summary>
    public double? WindowHeight { get; set; }

    /// <summary>Whether the app should auto-launch at user sign-in.</summary>
    public bool LaunchAtStartup { get; set; }

    /// <summary>Default settings location inside the current user's profile directory.</summary>
    public static string DefaultSettingsPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            DefaultDirectoryName,
            DefaultFileName);

    public static UserSettings Load(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        try
        {
            if (!File.Exists(path))
            {
                return new UserSettings();
            }

            return JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonOptions)
                   ?? new UserSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取用户设置失败 '{path}': {ex.Message}");
            return new UserSettings();
        }
    }

    public void Save(string? settingsPath = null)
    {
        var path = settingsPath ?? DefaultSettingsPath;
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-move keeps the file intact even if the process dies mid-write.
        var tempPath = path + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, path, overwrite: true);
    }
}
