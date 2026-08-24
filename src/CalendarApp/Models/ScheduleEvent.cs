namespace CalendarApp.Models;

/// <summary>
/// A single schedule item persisted as a markdown file in the storage folder.
/// Times are stored as UTC instants and converted to local time for display.
/// </summary>
public sealed class ScheduleEvent
{
    public long Id { get; set; }

    /// <summary>Item file name in the folder store.</summary>
    public string? FileName { get; set; }

    public required string Title { get; set; }

    /// <summary>P1..P4: 1=紧急 2=优先 3=一般 4=长期 (file name field <c>P&lt;n&gt;</c>).</summary>
    public int Priority { get; set; } = 3;

    /// <summary>Tags joined by '-', e.g. "工作-生活" (file name field).</summary>
    public string Tags { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    public DateTimeOffset StartsAt { get; set; }

    public DateTimeOffset? EndsAt { get; set; }

    public bool AllDay { get; set; }

    public string Location { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
