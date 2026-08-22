using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CalendarApp.Data;

/// <summary>
/// Remembers which main-panel view (calendar or tasks) the user last had open,
/// stored as a tiny JSON file (<c>~/.scheduleapp/lastWindow.json</c>) next to
/// settings.json. Read at startup to restore the previous view.
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

    /// <summary>True when the saved view is the tasks list (derived, not serialized).</summary>
    [JsonIgnore]
    public bool IsTasksView => string.Equals(View, "tasks", StringComparison.OrdinalIgnoreCase);

    /// <summary>Default location: inside the user-profile .scheduleapp directory.</summary>
    public static string DefaultPath =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            UserSettings.DefaultDirectoryName,
            DefaultFileName);

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
