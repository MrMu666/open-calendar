using System.Diagnostics;
using System.IO;
using Microsoft.Data.Sqlite;

namespace CalendarApp.Data;

/// <summary>
/// Owns the single sqlite data file that stores all of the app's data.
///
/// Concurrency design (optimized for concurrent reads):
///  - WAL journal mode: readers never block the single writer and the writer
///    never blocks readers, so any number of connections or processes can read
///    the same file simultaneously.
///  - Shared cache + connection pooling: connections opened by different calls
///    share the page cache and are reused instead of reopened.
///  - NORMAL synchronous mode (safe with WAL) so writes don't fsync per commit.
///  - Explicit busy_timeout so a competing writer waits instead of failing.
///
/// All data lives in exactly one .db file. WAL creates short-lived
/// "-wal"/"-shm" sidecar files during operation; they are checkpointed away by
/// <see cref="GracefulClose"/>, which should be called on clean app exit.
/// </summary>
public sealed class ScheduleDatabase
{
    /// <summary>Extension used when creating a new data file.</summary>
    public const string FileExtension = ".db";

    private const string SchemaScript = """
        CREATE TABLE IF NOT EXISTS schedule_items (
            id         INTEGER PRIMARY KEY AUTOINCREMENT,
            title      TEXT    NOT NULL,
            notes      TEXT    NOT NULL DEFAULT '',
            starts_at  TEXT    NOT NULL,               -- ISO-8601 UTC, lexically sortable
            ends_at    TEXT,                           -- ISO-8601 UTC, nullable
            all_day    INTEGER NOT NULL DEFAULT 0,
            location   TEXT    NOT NULL DEFAULT '',
            created_at TEXT    NOT NULL,
            updated_at TEXT    NOT NULL,
            deleted_at TEXT                            -- soft-delete marker
        );

        CREATE INDEX IF NOT EXISTS ix_schedule_items_starts_at ON schedule_items (starts_at);
        CREATE INDEX IF NOT EXISTS ix_schedule_items_deleted_at ON schedule_items (deleted_at);
        """;

    private string? _connectionString;

    /// <summary>
    /// Runs a scalar SQL statement on the given connection (Microsoft.Data.Sqlite
    /// no longer ships ExecuteNonQuery extension methods on SqliteConnection).
    /// </summary>
    internal static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public string? CurrentPath { get; private set; }

    /// <summary>Raised after a data file has been opened or created.</summary>
    public event EventHandler? Opened;

    /// <summary>Raised when the app is shutting down and the file is about to be checkpointed.</summary>
    public event EventHandler? Closing;

    public bool IsOpen => CurrentPath is not null;

    /// <summary>
    /// Builds a connection string tuned for the single-file, concurrent-read design.
    /// </summary>
    public static string BuildConnectionString(string filePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = filePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = true,
            ForeignKeys = true,
            DefaultTimeout = 30,
        }.ToString();

    /// <summary>
    /// Opens (or creates) the given sqlite file, applies WAL and the schema, then
    /// switches the app to it. Safe to call again to switch data files at runtime.
    /// </summary>
    public void Open(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using var connection = new SqliteConnection(BuildConnectionString(fullPath));
        connection.Open();

        // WAL is stored in the file header, so this only matters until the file's
        // first close; autocheckpoint keeps the -wal sidecar small during use.
        Execute(connection, "PRAGMA journal_mode = WAL;");
        Execute(connection, "PRAGMA synchronous = NORMAL;");
        Execute(connection, "PRAGMA wal_autocheckpoint = 1000;");
        Execute(connection, SchemaScript);

        CurrentPath = fullPath;
        _connectionString = BuildConnectionString(fullPath);
        Opened?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Opens a pooled, pragma-configured connection to the current data file.
    /// Callers dispose the connection; it then returns to the pool.
    /// </summary>
    public SqliteConnection OpenConnection()
    {
        if (CurrentPath is null || _connectionString is null)
        {
            throw new InvalidOperationException("尚未打开日程数据文件。");
        }

        var connection = new SqliteConnection(_connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA busy_timeout = 5000;");
            Execute(connection, "PRAGMA synchronous = NORMAL;");
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    /// <summary>
    /// One-time migration: legacy app preferences previously stored in the data
    /// file's <c>app_settings</c> table are folded into the user-profile
    /// settings.json and the table is dropped, so the data file ends up holding
    /// schedule items only. Safe to call on every open; it no-ops when the table
    /// is absent (new files and already-migrated files).
    /// </summary>
    public void MigrateLegacyAppSettingsInto(UserSettings settings, string? settingsPath = null)
    {
        if (CurrentPath is null)
        {
            return;
        }

        using var connection = OpenConnection();
        using (var probe = connection.CreateCommand())
        {
            probe.CommandText =
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name = 'app_settings';";
            if (probe.ExecuteScalar() is null)
            {
                return;
            }
        }

        var legacy = new Dictionary<string, string>();
        using (var read = connection.CreateCommand())
        {
            read.CommandText = "SELECT key, value FROM app_settings;";
            using var reader = read.ExecuteReader();
            while (reader.Read())
            {
                legacy[reader.GetString(0)] = reader.GetString(1);
            }
        }

        if (legacy.TryGetValue("appearance.background_color", out var color) &&
            !string.IsNullOrWhiteSpace(color))
        {
            settings.BackgroundColor = color;
        }

        if (legacy.TryGetValue("appearance.background_opacity", out var opacity) &&
            double.TryParse(opacity, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedOpacity))
        {
            settings.BackgroundOpacity = parsedOpacity;
        }

        if (legacy.TryGetValue("window.x", out var x) &&
            int.TryParse(x, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedX))
        {
            settings.WindowX = parsedX;
        }

        if (legacy.TryGetValue("window.y", out var y) &&
            int.TryParse(y, System.Globalization.NumberStyles.Integer,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedY))
        {
            settings.WindowY = parsedY;
        }

        if (legacy.TryGetValue("window.width", out var width) &&
            double.TryParse(width, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedWidth))
        {
            settings.WindowWidth = parsedWidth;
        }

        if (legacy.TryGetValue("window.height", out var height) &&
            double.TryParse(height, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var parsedHeight))
        {
            settings.WindowHeight = parsedHeight;
        }

        Execute(connection, "DROP TABLE IF EXISTS app_settings;");
        settings.Save(settingsPath);
    }

    /// <summary>
    /// Checkpoints the WAL away and closes the connection pool so the data
    /// directory ends with exactly one file again. Call on clean app exit.
    /// </summary>
    public void GracefulClose()
    {
        Closing?.Invoke(this, EventArgs.Empty);
        if (CurrentPath is not null)
        {
            try
            {
                using var connection = new SqliteConnection(BuildConnectionString(CurrentPath));
                connection.Open();
                Execute(connection, "PRAGMA wal_checkpoint(TRUNCATE);");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"WAL checkpoint failed: {ex.Message}");
            }
        }

        SqliteConnection.ClearAllPools();
        CurrentPath = null;
        _connectionString = null;
    }
}
