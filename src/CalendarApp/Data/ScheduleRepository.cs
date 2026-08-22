using System.Globalization;
using Microsoft.Data.Sqlite;
using CalendarApp.Models;

namespace CalendarApp.Data;

/// <summary>CRUD access to schedule items stored in the single data file.</summary>
public sealed class ScheduleRepository(ScheduleDatabase database)
{
    private const string SelectColumns =
        "id, title, notes, starts_at, ends_at, all_day, location, created_at, updated_at";

    public ScheduleDatabase Database { get; } = database;

    /// <summary>
    /// Returns non-deleted events in <c>[from, to)</c> order, oldest first.
    /// </summary>
    public IReadOnlyList<ScheduleEvent> GetUpcoming(DateTimeOffset from, DateTimeOffset? to = null, int limit = 100)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             SELECT {SelectColumns}
             FROM schedule_items
             WHERE deleted_at IS NULL
               AND starts_at >= $from
               AND ($to IS NULL OR starts_at < $to)
             ORDER BY starts_at
             LIMIT $limit;
             """;
        command.Parameters.AddWithValue("$from", TimeDb.ToDb(from));
        command.Parameters.AddWithValue("$to", to is { } until ? TimeDb.ToDb(until) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit);

        return ReadAll(command);
    }

    /// <summary>Returns all non-deleted events on the given local day.</summary>
    public IReadOnlyList<ScheduleEvent> GetEventsOnDay(DateTimeOffset day)
    {
        var local = day.ToLocalTime();
        var start = new DateTimeOffset(local.Year, local.Month, local.Day, 0, 0, 0, local.Offset);
        return GetUpcoming(start, start.AddDays(1), limit: 500);
    }

    public long Insert(ScheduleEvent item)
    {
        var now = DateTimeOffset.UtcNow;
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            $"""
             INSERT INTO schedule_items
                 (title, notes, starts_at, ends_at, all_day, location, created_at, updated_at)
             VALUES
                 ($title, $notes, $startsAt, $endsAt, $allDay, $location, $createdAt, $updatedAt);
             """;
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", item.Notes);
        command.Parameters.AddWithValue("$startsAt", TimeDb.ToDb(item.StartsAt));
        command.Parameters.AddWithValue("$endsAt", ToDbParameter(item.EndsAt));
        command.Parameters.AddWithValue("$allDay", item.AllDay ? 1 : 0);
        command.Parameters.AddWithValue("$location", item.Location);
        command.Parameters.AddWithValue("$createdAt", TimeDb.ToDb(now));
        command.Parameters.AddWithValue("$updatedAt", TimeDb.ToDb(now));
        command.ExecuteNonQuery();

        using var idCommand = connection.CreateCommand();
        idCommand.CommandText = "SELECT last_insert_rowid();";
        return Convert.ToInt64(idCommand.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    public void Update(ScheduleEvent item)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE schedule_items
            SET title = $title, notes = $notes, starts_at = $startsAt, ends_at = $endsAt,
                all_day = $allDay, location = $location, updated_at = $updatedAt
            WHERE id = $id AND deleted_at IS NULL;
            """;
        command.Parameters.AddWithValue("$id", item.Id);
        command.Parameters.AddWithValue("$title", item.Title);
        command.Parameters.AddWithValue("$notes", item.Notes);
        command.Parameters.AddWithValue("$startsAt", TimeDb.ToDb(item.StartsAt));
        command.Parameters.AddWithValue("$endsAt", ToDbParameter(item.EndsAt));
        command.Parameters.AddWithValue("$allDay", item.AllDay ? 1 : 0);
        command.Parameters.AddWithValue("$location", item.Location);
        command.Parameters.AddWithValue("$updatedAt", TimeDb.ToDb(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    /// <summary>Soft-deletes an event; it disappears from all queries.</summary>
    public void Delete(long id)
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText =
            "UPDATE schedule_items SET deleted_at = $deletedAt WHERE id = $id AND deleted_at IS NULL;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$deletedAt", TimeDb.ToDb(DateTimeOffset.UtcNow));
        command.ExecuteNonQuery();
    }

    public long Count()
    {
        using var connection = Database.OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM schedule_items WHERE deleted_at IS NULL;";
        return Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
    }

    private static object ToDbParameter(DateTimeOffset? value) =>
        value is { } instant ? TimeDb.ToDb(instant) : DBNull.Value;

    private static List<ScheduleEvent> ReadAll(SqliteCommand command)
    {
        var items = new List<ScheduleEvent>();
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            items.Add(new ScheduleEvent
            {
                Id = reader.GetInt64(0),
                Title = reader.GetString(1),
                Notes = reader.GetString(2),
                StartsAt = TimeDb.FromDb(reader.GetString(3)),
                EndsAt = reader.IsDBNull(4) ? null : TimeDb.FromDb(reader.GetString(4)),
                AllDay = reader.GetInt64(5) != 0,
                Location = reader.GetString(6),
                CreatedAt = TimeDb.FromDb(reader.GetString(7)),
                UpdatedAt = TimeDb.FromDb(reader.GetString(8)),
            });
        }

        return items;
    }

    /// <summary>
    /// Serializes instants as UTC ISO-8601 in round-trip ("O") format so values
    /// sort lexically and can be range-queried directly in SQL.
    /// </summary>
    private static class TimeDb
    {
        public static string ToDb(DateTimeOffset value) =>
            value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);

        public static DateTimeOffset FromDb(string value) =>
            DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    }
}
