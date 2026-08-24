using System.IO;
using CalendarApp.Data;

namespace CalendarApp;

/// <summary>
/// Composition root: the singleton services shared by the main and settings
/// windows. No DI container — plain hand-wired singletons.
/// </summary>
public sealed class AppServices
{
    /// <summary>Cross-session preferences written to ~/.scheduleapp/settings.json.</summary>
    public UserSettings Settings { get; }

    /// <summary>Primary store: a folder of markdown item files, live-watched.</summary>
    public FolderItemStore Items { get; }

    private AppServices(UserSettings settings, FolderItemStore items)
    {
        Settings = settings;
        Items = items;
    }

    /// <summary>
    /// Builds the service graph. Re-tries opening the last-used storage folder
    /// if one is recorded and still exists.
    /// </summary>
    public static AppServices Create()
    {
        var settings = UserSettings.Load();
        var items = new FolderItemStore();

        var last = settings.LastOpenedFolder;
        if (!string.IsNullOrWhiteSpace(last) && Directory.Exists(last))
        {
            try
            {
                items.Open(last);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"打开上次的日程文件夹失败 '{last}': {ex.Message}");
            }
        }

        return new AppServices(settings, items);
    }
}
