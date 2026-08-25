using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarApp.Data;

/// <summary>
/// Remembers the last main-panel view (calendar/tasks) and per-resolution
/// window geometry. Stored as a tiny JSON file next to settings.json
/// (<c>~/.calendarapp/lastWindow.json</c>).
///
/// Window geometry is keyed by monitor resolution + DPI so that dragging
/// the panel between monitors with different resolutions/DPI restores the
/// last size and position for each configuration independently.
/// </summary>
public sealed class LastViewSettings
{
    public const string DefaultFileName = "lastWindow.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>"calendar" or "tasks". Unknown values fall back to calendar on read.</summary>
    public string View { get; set; } = "calendar";

    /// <summary>Per-resolution window geometry, keyed by e.g. "1920x1080@96".</summary>
    public Dictionary<string, SavedGeometry> Geometry { get; set; } = new();

    /// <summary>True when the saved view is the tasks list (derived, not serialized).</summary>
    [JsonIgnore]
    public bool IsTasksView => string.Equals(View, "tasks", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default location: inside the user-profile .calendarapp directory.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserSettings.DefaultDirectoryName,
            DefaultFileName);

    /// <summary>
    /// Builds the resolution key from a monitor's work-area size and DPI.
    /// </summary>
    public static string MakeKey(int workWidthPx, int workHeightPx, uint dpi) =>
        $"{workWidthPx}x{workHeightPx}@{dpi}";

    /// <summary>
    /// Returns the saved geometry for the given key, or null.
    /// </summary>
    public SavedGeometry? GetGeometry(string key) =>
        Geometry.TryGetValue(key, out var g) ? g : null;

    /// <summary>
    /// Saves (or updates) the geometry for the given key.
    /// </summary>
    public void SetGeometry(string key, int x, int y, double width, double height)
    {
        Geometry[key] = new SavedGeometry { X = x, Y = y, Width = width, Height = height };
    }

    /// <summary>
    /// Removes the geometry entry for the given key, leaving others intact.
    /// </summary>
    public void RemoveGeometry(string key)
    {
        Geometry.Remove(key);
    }

    public static LastViewSettings Load(string? path = null)
    {
        var fullPath = path ?? DefaultPath;
        try
        {
            if (!File.Exists(fullPath))
            {
                return new LastViewSettings();
            }

            return JsonSerializer.Deserialize<LastViewSettings>(File.ReadAllText(fullPath), JsonOptions)
                   ?? new LastViewSettings();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"读取上次视图失败 '{fullPath}': {ex.Message}");
            return new LastViewSettings();
        }
    }

    public void Save(string? path = null)
    {
        var fullPath = path ?? DefaultPath;
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write-then-move keeps the file intact even if the process dies mid-write.
        var tempPath = fullPath + ".tmp";
        File.WriteAllText(tempPath, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(tempPath, fullPath, overwrite: true);
    }
}

/// <summary>
/// A single window-geometry record (position in physical pixels, size in DIPs).
/// </summary>
public sealed class SavedGeometry
{
    public int X { get; set; }
    public int Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
}