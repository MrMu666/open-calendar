using System.Collections.Concurrent;
using System.IO;
using Microsoft.Data.Sqlite;
using CalendarApp.Data;
using CalendarApp.Models;
using Xunit;

namespace CalendarApp.Tests;

public class ScheduleDatabaseTests : IDisposable
{
    private readonly string _dir;
    private readonly string _dbPath;

    public ScheduleDatabaseTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "schedule-tests-" + Guid.NewGuid().ToString("N"));
        _dbPath = Path.Combine(_dir, "schedule.db");
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
    public void OpenCreatesDataFileInWalMode()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);

        Assert.True(File.Exists(_dbPath));
        Assert.Equal(Path.GetFullPath(_dbPath), db.CurrentPath);

        using var connection = db.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode;";
            Assert.Equal("wal", (string?)command.ExecuteScalar(), ignoreCase: true);
        }
    }

    [Fact]
    public void RepositoryCrudLifecycle()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);
        var repo = new ScheduleRepository(db);

        var today = DateTimeOffset.Now;
        var mornings = new ScheduleEvent
        {
            Title = "晨会",
            StartsAt = today.AddHours(-2),
            Notes = "讨论本周计划",
            Location = "线上",
        };
        var evening = new ScheduleEvent
        {
            Title = "夜跑",
            StartsAt = today.AddHours(2),
        };
        var tomorrow = new ScheduleEvent
        {
            Title = "明天的安排",
            StartsAt = today.AddHours(26),
            AllDay = true,
        };

        var id1 = repo.Insert(mornings);
        var id2 = repo.Insert(evening);
        repo.Insert(tomorrow);
        Assert.True(id1 > 0 && id2 > 0);
        Assert.Equal(3, repo.Count());

        // Only the two events starting today.
        Assert.Equal(2, repo.GetEventsOnDay(today).Count);

        // Oldest first, limited.
        var upcoming = repo.GetUpcoming(today.AddDays(-1), limit: 10);
        Assert.Equal("晨会", upcoming[0].Title);
        Assert.Equal(3, upcoming.Count);

        // Update.
        var loaded = repo.GetUpcoming(today.AddDays(-1)).First(e => e.Id == id1);
        loaded.Title = "晨会（改期）";
        repo.Update(loaded);
        Assert.Equal("晨会（改期）", repo.GetUpcoming(today.AddDays(-1)).First(e => e.Id == id1).Title);

        // Soft delete removes it from queries but keeps the row.
        repo.Delete(id1);
        Assert.DoesNotContain(repo.GetUpcoming(today.AddDays(-1)), e => e.Id == id1);
        Assert.Equal(2, repo.Count());
    }

    [Fact]
    public async Task ManyConcurrentReadersWhileWriterIsActive()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);
        var repo = new ScheduleRepository(db);

        var start = DateTimeOffset.Now.AddMonths(-1);
        for (var i = 0; i < 40; i++)
        {
            repo.Insert(new ScheduleEvent { Title = $"seed-{i}", StartsAt = start.AddMinutes(i) });
        }

        const int readerCount = 4;
        var failures = new ConcurrentBag<string>();
        var begin = new ManualResetEventSlim(false);

        async Task Reader()
        {
            begin.Wait();
            try
            {
                for (var round = 0; round < 25; round++)
                {
                    var events = repo.GetUpcoming(start);
                    _ = repo.Count();
                    if (events.Count < 0 || events.Count > 500)
                    {
                        failures.Add("unexpected event count");
                    }
                }
            }
            catch (Exception ex)
            {
                failures.Add(ex.ToString());
            }
        }

        var readers = Enumerable.Range(0, readerCount).Select(_ => Task.Run(Reader)).ToArray();

        var writer = Task.Run(() =>
        {
            begin.Wait();
            for (var i = 0; i < 60; i++)
            {
                repo.Insert(new ScheduleEvent { Title = $"written-{i}", StartsAt = DateTimeOffset.Now });
            }
            return repo.Count();
        });

        begin.Set();
        await Task.WhenAll(readers);
        var finalCount = await writer;

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));          // no reader saw "database is locked" or similar
        Assert.Equal(100, finalCount);   // 40 seed + 60 written
    }

    [Fact]
    public void NewDataFileHasNoAppSettingsTable()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);

        using var connection = db.OpenConnection();
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }
    }

    [Fact]
    public void LegacyAppSettingsMigrateIntoUserSettings()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);

        // Simulate a pre-migration data file that still has the app_settings table.
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                CREATE TABLE app_settings (key TEXT PRIMARY KEY, value TEXT NOT NULL);
                INSERT INTO app_settings (key, value) VALUES
                    ('appearance.background_color', '#FF8800'),
                    ('appearance.background_opacity', '0.55'),
                    ('window.x', '120'),
                    ('window.y', '80'),
                    ('window.width', '360'),
                    ('window.height', '540');
                """;
            command.ExecuteNonQuery();
        }

        var settingsPath = Path.Combine(_dir, "settings.json");
        var settings = new UserSettings();
        db.MigrateLegacyAppSettingsInto(settings, settingsPath);

        Assert.Equal("#FF8800", settings.BackgroundColor);
        Assert.Equal(0.55, settings.BackgroundOpacity);
        Assert.Equal(120, settings.WindowX);
        Assert.Equal(80, settings.WindowY);
        Assert.Equal(360, settings.WindowWidth);
        Assert.Equal(540, settings.WindowHeight);

        // Table is dropped, so the data file ends up with schedule items only.
        using (var connection = db.OpenConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText =
                "SELECT COUNT(*) FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';";
            Assert.Equal(0L, Convert.ToInt64(command.ExecuteScalar()));
        }

        // Idempotent: a second call no-ops and keeps values.
        settings.BackgroundColor = "#ABCDEF";
        db.MigrateLegacyAppSettingsInto(settings, settingsPath);
        Assert.Equal("#ABCDEF", settings.BackgroundColor);
    }

    [Fact]
    public void UserSettingsRoundTripAndPersist()
    {
        var settingsPath = Path.Combine(_dir, "settings.json");

        var saved = new UserSettings
        {
            LastOpenedFile = @"C:\data\schedule.db",
            BackgroundColor = "#112233",
            BackgroundOpacity = 0.45,
            WindowX = 200,
            WindowY = 150,
            WindowWidth = 340.0,
            WindowHeight = 500.0,
        };
        saved.Save(settingsPath);

        var loaded = UserSettings.Load(settingsPath);
        Assert.Equal(@"C:\data\schedule.db", loaded.LastOpenedFile);
        Assert.Equal("#112233", loaded.BackgroundColor);
        Assert.Equal(0.45, loaded.BackgroundOpacity);
        Assert.Equal(200, loaded.WindowX);
        Assert.Equal(150, loaded.WindowY);
        Assert.Equal(340.0, loaded.WindowWidth);
        Assert.Equal(500.0, loaded.WindowHeight);

        // Missing file yields defaults.
        var fresh = UserSettings.Load(Path.Combine(_dir, "missing.json"));
        Assert.Equal("#0D1117", fresh.BackgroundColor);
        Assert.Equal(0.6, fresh.BackgroundOpacity);
        Assert.Null(fresh.WindowX);
    }

    [Fact]
    public void GracefulCloseLeavesExactlyOneFile()
    {
        var db = new ScheduleDatabase();
        db.Open(_dbPath);
        var repo = new ScheduleRepository(db);
        repo.Insert(new ScheduleEvent { Title = "单文件检验", StartsAt = DateTimeOffset.Now });

        db.GracefulClose();

        var files = Directory.EnumerateFiles(_dir)
            .Select(f => Path.GetFileName(f)!)
            .Order()

            .ToArray();
        Assert.Equal(["schedule.db"], files);
    }
}
