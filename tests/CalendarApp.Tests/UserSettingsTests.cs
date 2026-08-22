using System.IO;
using CalendarApp.Data;
using Xunit;

namespace CalendarApp.Tests;

public class UserSettingsTests : IDisposable
{
    private readonly string _dir;
    private readonly string _settingsPath;

    public UserSettingsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "settings-tests-" + Guid.NewGuid().ToString("N"));
        _settingsPath = Path.Combine(_dir, "settings.json");
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_dir))
            {
                Directory.Delete(_dir, recursive: true);
            }
        }
        catch (IOException)
        {
            // best effort cleanup
        }
    }

    [Fact]
    public void LoadReturnsEmptyWhenNoFileExists()
    {
        var settings = UserSettings.Load(_settingsPath);
        Assert.Null(settings.LastOpenedFile);
    }

    [Fact]
    public void SaveAndLoadRoundTripsLastOpenedFile()
    {
        var expected = Path.GetFullPath(Path.Combine(_dir, "data", "my-schedule.db"));

        var before = new UserSettings { LastOpenedFile = expected };
        before.Save(_settingsPath);
        Assert.True(File.Exists(_settingsPath));

        var after = UserSettings.Load(_settingsPath);
        Assert.Equal(expected, after.LastOpenedFile);
    }

    [Fact]
    public void SaveAndLoadRoundTripsLaunchAtStartup()
    {
        var before = new UserSettings { LaunchAtStartup = true };
        before.Save(_settingsPath);

        var after = UserSettings.Load(_settingsPath);
        Assert.True(after.LaunchAtStartup);

        // Default is off.
        var fresh = UserSettings.Load(Path.Combine(_dir, "missing.json"));
        Assert.False(fresh.LaunchAtStartup);
    }

    [Fact]
    public void LoadToleratesCorruptFile()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_settingsPath, "this is { not valid json at all");

        var settings = UserSettings.Load(_settingsPath);
        Assert.Null(settings.LastOpenedFile);
    }

    [Fact]
    public void LastViewSettingsRoundTrip()
    {
        var path = Path.Combine(_dir, "lastWindow.json");

        var saved = new LastViewSettings { View = "tasks" };
        saved.Save(path);
        Assert.True(File.Exists(path));

        var loaded = LastViewSettings.Load(path);
        Assert.True(loaded.IsTasksView);

        // Missing file defaults to calendar.
        var fresh = LastViewSettings.Load(Path.Combine(_dir, "missing.json"));
        Assert.False(fresh.IsTasksView);

        // Unknown/corrupt values fall back to calendar.
        Directory.CreateDirectory(_dir);
        File.WriteAllText(path, "this is { not valid json");
        Assert.False(LastViewSettings.Load(path).IsTasksView);
    }
}
